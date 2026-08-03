using System.Collections.Concurrent;
using Picall.Models;

namespace Picall.Services;

public sealed record ScanProgress(long FilesVisited, int MediaFound, int AccessErrors, string CurrentRoot);

public sealed class MediaScanner
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff",
        ".heic", ".heif", ".avif", ".dng", ".raw", ".cr2", ".cr3", ".nef", ".arw",
        ".orf", ".rw2", ".pef", ".srw", ".ico", ".jxl", ".jxr", ".wdp", ".hdp",
        ".jp2", ".j2k", ".j2c", ".jpf", ".jpx", ".avifs", ".apng", ".qoi", ".exr",
        ".hdr", ".psd", ".psb", ".dds", ".tga", ".pcx", ".svg", ".ppm", ".pgm",
        ".pbm", ".pnm", ".ras", ".xbm", ".xpm"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".mpeg", ".mpg",
        ".mts", ".m2ts", ".3gp", ".3g2", ".ts", ".vob", ".flv", ".f4v", ".ogv",
        ".asf", ".divx", ".mxf", ".dv", ".rm", ".rmvb"
    };

    private static readonly HashSet<string> DriveRootExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "Recovery", "$WinREAgent", "PerfLogs"
    };

    public static IReadOnlyList<string> GetScanRoots(IEnumerable<string> extraFolders)
    {
        var overridden = Environment.GetEnvironmentVariable("PICALL_SCAN_ROOTS");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Directory.Exists).Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var roots = new List<string>();
        try
        {
            roots.AddRange(DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => NormalizeRoot(d.RootDirectory.FullName)));
        }
        catch { }

        foreach (var folder in extraFolders)
        {
            try
            {
                var normalized = NormalizeRoot(folder);
                if (Directory.Exists(normalized) && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase)) roots.Add(normalized);
            }
            catch { }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<List<MediaItem>> ScanAsync(
        IEnumerable<MediaItem> existing,
        IReadOnlyList<string> roots,
        Action<IReadOnlyList<MediaItem>>? newItems,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var old = existing.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentDictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
        long visited = 0;
        var errors = 0;
        var lastReport = 0L;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(roots.Count, 1, 2)
        };

        await Parallel.ForEachAsync(roots, parallelOptions, async (root, token) =>
        {
            await Task.Yield();
            var batch = new List<MediaItem>(96);
            ScanRoot(root, old, found, batch, ref visited, ref errors, ref lastReport, newItems, progress, token);
            FlushBatch(batch, newItems);
        });

        progress?.Report(new ScanProgress(visited, found.Count, errors, string.Empty));
        return found.Values.ToList();
    }

    private static void ScanRoot(
        string root,
        IReadOnlyDictionary<string, MediaItem> old,
        ConcurrentDictionary<string, MediaItem> found,
        List<MediaItem> batch,
        ref long visited,
        ref int errors,
        ref long lastReport,
        Action<IReadOnlyList<MediaItem>>? newItems,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        var directories = new Stack<(string Path, bool IsDriveRoot)>();
        directories.Push((root, IsDriveRoot(root)));
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        while (directories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, isDriveRoot) = directories.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    var current = Interlocked.Increment(ref visited);
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsFileSystemLink(entry)) continue;
                        if (ShouldIgnorePath(entry.FullName)) continue;
                        if (isDriveRoot && DriveRootExclusions.Contains(entry.Name)) continue;
                        directories.Push((entry.FullName, false));
                        continue;
                    }

                    var extension = entry.Extension;
                    if (ShouldIgnorePath(entry.FullName)) continue;
                    var kind = PhotoExtensions.Contains(extension) ? MediaKind.Photo :
                        VideoExtensions.Contains(extension) ? MediaKind.Video : (MediaKind?)null;
                    if (kind is null) continue;

                    try
                    {
                        var info = (FileInfo)entry;
                        var modified = info.LastWriteTimeUtc;
                        var size = info.Length;
                        if (old.TryGetValue(info.FullName, out var cached) &&
                            cached.ModifiedUtc.Ticks == modified.Ticks && cached.Size == size)
                        {
                            found.TryAdd(cached.Path, cached);
                        }
                        else
                        {
                            var item = new MediaItem
                            {
                                Path = info.FullName,
                                Name = System.IO.Path.GetFileNameWithoutExtension(info.Name),
                                Directory = info.DirectoryName ?? root,
                                Extension = extension,
                                ModifiedUtc = modified,
                                Size = size,
                                Kind = kind.Value,
                                IsFavorite = cached?.IsFavorite ?? false
                            };
                            if (found.TryAdd(item.Path, item))
                            {
                                batch.Add(item);
                                if (batch.Count >= 96) FlushBatch(batch, newItems);
                            }
                        }
                    }
                    catch (IOException) { Interlocked.Increment(ref errors); }
                    catch (UnauthorizedAccessException) { Interlocked.Increment(ref errors); }

                    var reported = Volatile.Read(ref lastReport);
                    if (current - reported >= 1500 && Interlocked.CompareExchange(ref lastReport, current, reported) == reported)
                        progress?.Report(new ScanProgress(current, found.Count, errors, root));
                }
            }
            catch (IOException) { Interlocked.Increment(ref errors); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref errors); }
            catch (System.Security.SecurityException) { Interlocked.Increment(ref errors); }
        }
    }

    private static void FlushBatch(List<MediaItem> batch, Action<IReadOnlyList<MediaItem>>? callback)
    {
        if (batch.Count == 0) return;
        callback?.Invoke(batch.ToArray());
        batch.Clear();
    }

    public static bool IsSupported(string path) =>
        PhotoExtensions.Contains(System.IO.Path.GetExtension(path)) || VideoExtensions.Contains(System.IO.Path.GetExtension(path));

    public static IReadOnlyList<string> GetSupportedExtensions() =>
        PhotoExtensions.Concat(VideoExtensions).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool ShouldIgnorePath(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
            var appData = System.IO.Path.GetFullPath(AppPaths.Root).TrimEnd('\\', '/');
            return string.Equals(full, appData, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(appData + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static MediaItem? ReadMediaFile(string path, bool isFavorite = false)
    {
        try
        {
            if (ShouldIgnorePath(path)) return null;
            var extension = System.IO.Path.GetExtension(path);
            var kind = PhotoExtensions.Contains(extension) ? MediaKind.Photo :
                VideoExtensions.Contains(extension) ? MediaKind.Video : (MediaKind?)null;
            if (kind is null || !File.Exists(path)) return null;
            var info = new FileInfo(path);
            return new MediaItem
            {
                Path = info.FullName,
                Name = System.IO.Path.GetFileNameWithoutExtension(info.Name),
                Directory = info.DirectoryName ?? string.Empty,
                Extension = extension,
                ModifiedUtc = info.LastWriteTimeUtc,
                Size = info.Length,
                Kind = kind.Value,
                IsFavorite = isFavorite
            };
        }
        catch { return null; }
    }

    private static bool IsDriveRoot(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var root = System.IO.Path.GetPathRoot(full)?.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileSystemLink(FileSystemInfo entry)
    {
        try { return entry.LinkTarget is not null; }
        catch { return true; }
    }

    private static string NormalizeRoot(string path)
    {
        var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var driveRoot = System.IO.Path.GetPathRoot(full);
        return string.Equals(full.TrimEnd('\\', '/'), driveRoot?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? driveRoot! : full.TrimEnd('\\', '/');
    }

}
