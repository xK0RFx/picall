using System.Text;
using Picall.Models;

namespace Picall.Services;

public static class IndexStore
{
    private const string Signature = "PICALL02";
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public static List<MediaItem> Load(HashSet<string> favorites)
    {
        var items = new List<MediaItem>();
        try
        {
            if (!File.Exists(AppPaths.IndexFile)) return items;
            using var stream = new FileStream(AppPaths.IndexFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);
            if (new string(reader.ReadChars(Signature.Length)) != Signature) return items;
            var count = reader.ReadInt32();
            if (count is < 0 or > 5_000_000) return items;
            items.Capacity = count;
            for (var i = 0; i < count; i++)
            {
                var path = reader.ReadString();
                var item = new MediaItem
                {
                    Path = path,
                    Name = reader.ReadString(),
                    Directory = reader.ReadString(),
                    Extension = reader.ReadString(),
                    ModifiedUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                    Size = reader.ReadInt64(),
                    Kind = (MediaKind)reader.ReadByte(),
                    IsFavorite = favorites.Contains(path)
                };
                if (!MediaScanner.ShouldIgnorePath(item.Path)) items.Add(item);
            }
        }
        catch
        {
            try { File.Move(AppPaths.IndexFile, AppPaths.IndexFile + ".broken", true); } catch { }
            items.Clear();
        }
        return items.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
    }

    public static async Task SaveAsync(IReadOnlyCollection<MediaItem> items, CancellationToken cancellationToken = default)
    {
        var persistedItems = items.Where(x => !MediaScanner.ShouldIgnorePath(x.Path))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToArray();
        await SaveGate.WaitAsync(cancellationToken);
        try
        {
            var temp = AppPaths.IndexFile + ".tmp";
            await Task.Run(() =>
            {
                using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
                writer.Write(Signature.ToCharArray());
                writer.Write(persistedItems.Length);
                foreach (var item in persistedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(item.Path);
                    writer.Write(item.Name);
                    writer.Write(item.Directory);
                    writer.Write(item.Extension);
                    writer.Write(item.ModifiedUtc.Ticks);
                    writer.Write(item.Size);
                    writer.Write((byte)item.Kind);
                }
            }, cancellationToken);
            File.Move(temp, AppPaths.IndexFile, true);
        }
        finally { SaveGate.Release(); }
    }
}
