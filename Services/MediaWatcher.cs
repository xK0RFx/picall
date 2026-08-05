using System.Collections.Concurrent;

namespace Picall.Services;

public enum MediaChangeKind { Upsert, Remove }
public sealed record MediaFileChange(string Path, MediaChangeKind Kind);

public sealed class MediaWatcher : IDisposable
{
    private sealed record PendingChange(MediaChangeKind Kind, long Timestamp);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly IReadOnlySet<string> _excludedPaths;
    private bool _disposed;

    public MediaWatcher(IEnumerable<string> roots, Action<IReadOnlyList<MediaFileChange>> callback, IEnumerable<string>? excludedPaths = null)
    {
        Callback = callback;
        _excludedPaths = (excludedPaths ?? []).Select(MediaScanner.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _timer = new Timer(Flush, null, 800, 800);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = false
                };
                watcher.Created += OnUpsert;
                watcher.Changed += OnUpsert;
                watcher.Deleted += OnRemove;
                watcher.Renamed += OnRenamed;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch { }
        }
    }

    private Action<IReadOnlyList<MediaFileChange>> Callback { get; }

    private void OnUpsert(object sender, FileSystemEventArgs e)
    {
        if (!MediaScanner.ShouldIgnorePath(e.FullPath, _excludedPaths) && MediaScanner.IsSupported(e.FullPath)) Queue(e.FullPath, MediaChangeKind.Upsert);
    }

    private void OnRemove(object sender, FileSystemEventArgs e)
    {
        if (!MediaScanner.ShouldIgnorePath(e.FullPath, _excludedPaths) && MediaScanner.IsSupported(e.FullPath)) Queue(e.FullPath, MediaChangeKind.Remove);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!MediaScanner.ShouldIgnorePath(e.OldFullPath, _excludedPaths) && MediaScanner.IsSupported(e.OldFullPath)) Queue(e.OldFullPath, MediaChangeKind.Remove);
        if (!MediaScanner.ShouldIgnorePath(e.FullPath, _excludedPaths) && MediaScanner.IsSupported(e.FullPath)) Queue(e.FullPath, MediaChangeKind.Upsert);
    }

    private void Queue(string path, MediaChangeKind kind) =>
        _pending[path] = new PendingChange(kind, Environment.TickCount64);

    private void Flush(object? state)
    {
        if (_disposed || _pending.IsEmpty) return;
        var now = Environment.TickCount64;
        var changes = new List<MediaFileChange>();
        foreach (var pair in _pending)
        {
            if (now - pair.Value.Timestamp < 500) continue;
            if (_pending.TryRemove(pair.Key, out var change))
                changes.Add(new MediaFileChange(pair.Key, change.Kind));
        }
        if (changes.Count > 0)
        {
            try { Callback(changes); } catch { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        foreach (var watcher in _watchers)
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
        }
        _watchers.Clear();
        _pending.Clear();
    }
}
