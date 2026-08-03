using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Picall.Models;
using ImageMagick;

namespace Picall.Services;

public sealed class ThumbnailService : IDisposable
{
    private const int ThumbnailWidth = 360;
    private const int ThumbnailHeight = 240;
    private const int JpegCacheQuality = 82;
    private const int HotCacheLimit = 64;
    private static readonly HashSet<string> MagickPreferredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jxl", ".svg", ".exr", ".hdr", ".qoi", ".jp2", ".j2k", ".j2c", ".jpf", ".jpx",
        ".psd", ".psb", ".dds", ".tga", ".pcx", ".ppm", ".pgm", ".pbm", ".pnm", ".ras",
        ".xbm", ".xpm", ".jxr", ".wdp", ".hdp"
    };
    private static readonly HashSet<string> TransparencyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".apng", ".gif", ".webp", ".avif", ".avifs", ".heic", ".heif",
        ".ico", ".svg", ".bmp", ".tif", ".tiff", ".jxl", ".jxr", ".wdp", ".hdp", ".qoi", ".exr", ".psd", ".psb",
        ".dds", ".tga", ".jp2", ".j2k", ".j2c", ".jpf", ".jpx", ".xbm", ".xpm"
    };
    private readonly SemaphoreSlim _cacheIo = new(4, 4);
    private readonly SemaphoreSlim _cacheDecoders = new(2, 2);
    private readonly SemaphoreSlim _generators = new(2, 2);
    private readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BitmapSource> _hotCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _hotCacheOrder = new();
    private int _requestsSinceSweep;
    private bool _disposed;

    public async Task<BitmapSource?> GetAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;
        if (Interlocked.Increment(ref _requestsSinceSweep) >= 256)
        {
            Interlocked.Exchange(ref _requestsSinceSweep, 0);
            foreach (var entry in _memory)
                if (!entry.Value.TryGetTarget(out _)) _memory.TryRemove(entry.Key, out _);
        }
        var preserveTransparency = item.Kind == MediaKind.Photo && TransparencyExtensions.Contains(item.Extension);
        var key = CreateKey(item, preserveTransparency);
        if (_hotCache.TryGetValue(key, out var hotImage)) return hotImage;
        if (_memory.TryGetValue(key, out var weak) && weak.TryGetTarget(out var memoryImage))
        {
            Remember(key, memoryImage);
            return memoryImage;
        }

        var cacheFile = GetCacheFile(key, preserveTransparency);
        if (File.Exists(cacheFile))
        {
            var cached = await LoadFrozenAsync(cacheFile, cancellationToken);
            if (cached is not null)
            {
                Remember(key, cached);
                return cached;
            }
        }

        await _generators.WaitAsync(cancellationToken);
        try
        {
            var generated = File.Exists(cacheFile)
                ? await LoadFrozenAsync(cacheFile, cancellationToken)
                : await Task.Run(() => Generate(item, cacheFile, preserveTransparency), cancellationToken);
            if (generated is not null) Remember(key, generated);
            return generated;
        }
        finally { _generators.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _memory.Clear();
        TrimMemoryCache();
    }

    public void TrimMemoryCache()
    {
        _hotCache.Clear();
        _hotCacheOrder.Clear();
    }

    public static void TrimDiskCache(long maximumBytes = 1_500_000_000)
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.Cache).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                               file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var total = files.Sum(f => f.Length);
            if (total <= maximumBytes) return;
            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                try { total -= file.Length; file.Delete(); } catch { }
                if (total <= maximumBytes * 0.8) break;
            }
        }
        catch { }
    }

    private static BitmapSource? Generate(MediaItem item, string cacheFile, bool preserveTransparency)
    {
        if (!File.Exists(item.Path)) return null;
        BitmapSource? image = null;
        if (item.Kind == MediaKind.Photo && MagickPreferredExtensions.Contains(item.Extension))
        {
            try { image = GenerateWithMagick(item.Path); } catch { }
        }
        if (image is null)
        {
            try { image = ShellThumbnail.Get(item.Path, ThumbnailWidth, ThumbnailHeight); } catch { }
        }
        if (image is null && item.Kind == MediaKind.Photo)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = ThumbnailWidth;
                bitmap.UriSource = new Uri(item.Path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch { }
        }

        if (image is null && item.Kind == MediaKind.Photo)
        {
            try { image = GenerateWithMagick(item.Path); } catch { }
        }

        if (image is null) return null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cacheFile)!);
            var temp = cacheFile + $".{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                BitmapEncoder encoder = preserveTransparency
                    ? new PngBitmapEncoder()
                    : new JpegBitmapEncoder { QualityLevel = JpegCacheQuality };
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            File.Move(temp, cacheFile, true);
        }
        catch { }
        return image;
    }

    private static BitmapSource? GenerateWithMagick(string path)
    {
        var settings = new MagickReadSettings { BackgroundColor = MagickColors.Transparent };
        using var magick = new MagickImage(path, settings);
        magick.AutoOrient();
        magick.Thumbnail(ThumbnailWidth, ThumbnailHeight);
        magick.Strip();
        using var encoded = new MemoryStream();
        magick.Write(encoded, MagickFormat.Png);
        encoded.Position = 0;
        var decoder = BitmapDecoder.Create(encoded, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private async Task<BitmapSource?> LoadFrozenAsync(string path, CancellationToken cancellationToken)
    {
        byte[] encoded;
        await _cacheIo.WaitAsync(cancellationToken);
        try
        {
            encoded = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            TryDeleteCacheFile(path);
            return null;
        }
        finally { _cacheIo.Release(); }

        await _cacheDecoders.WaitAsync(cancellationToken);
        try
        {
            var image = await Task.Run(() => DecodeFrozen(encoded), cancellationToken);
            if (image is null) TryDeleteCacheFile(path);
            return image;
        }
        finally { _cacheDecoders.Release(); }
    }

    private static BitmapSource? DecodeFrozen(byte[] encoded)
    {
        try
        {
            using var stream = new MemoryStream(encoded, false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch { return null; }
    }

    private static void TryDeleteCacheFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void Remember(string key, BitmapSource image)
    {
        _memory[key] = new WeakReference<BitmapSource>(image);
        if (!_hotCache.TryAdd(key, image)) return;
        _hotCacheOrder.Enqueue(key);
        while (_hotCache.Count > HotCacheLimit && _hotCacheOrder.TryDequeue(out var expiredKey))
            _hotCache.TryRemove(expiredKey, out _);
    }

    private static string CreateKey(MediaItem item, bool preserveTransparency)
    {
        var cacheKind = preserveTransparency ? "PNG" : "JPG";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"THUMB06|{cacheKind}|{item.Path.ToUpperInvariant()}|{item.ModifiedUtc.Ticks}|{item.Size}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetCacheFile(string key, bool preserveTransparency) =>
        System.IO.Path.Combine(AppPaths.Cache, key[..2], key + (preserveTransparency ? ".png" : ".jpg"));

    private static class ShellThumbnail
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width; public int Height; }

        [Flags]
        private enum ImageFlags : uint
        {
            ResizeToFit = 0x00,
            BiggerSizeOk = 0x01,
            MemoryOnly = 0x02,
            IconOnly = 0x04,
            ThumbnailOnly = 0x08,
            InCacheOnly = 0x10
        }

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, ImageFlags flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr objectHandle);

        public static BitmapSource? Get(string path, int width, int height)
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            IntPtr handle = IntPtr.Zero;
            try
            {
                var hr = factory.GetImage(new NativeSize { Width = width, Height = height },
                    ImageFlags.ThumbnailOnly | ImageFlags.BiggerSizeOk, out handle);
                if (hr != 0 || handle == IntPtr.Zero) return null;
                var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                if (handle != IntPtr.Zero) DeleteObject(handle);
                if (Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
            }
        }
    }
}
