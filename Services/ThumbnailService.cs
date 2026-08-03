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
    private static readonly HashSet<string> MagickPreferredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jxl", ".svg", ".exr", ".hdr", ".qoi", ".jp2", ".j2k", ".j2c", ".jpf", ".jpx",
        ".psd", ".psb", ".dds", ".tga", ".pcx", ".ppm", ".pgm", ".pbm", ".pnm", ".ras",
        ".xbm", ".xpm", ".jxr", ".wdp", ".hdp"
    };
    private readonly SemaphoreSlim _workers = new(4, 4);
    private readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> _memory = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public async Task<BitmapSource?> GetAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;
        var key = CreateKey(item);
        if (_memory.TryGetValue(key, out var weak) && weak.TryGetTarget(out var memoryImage)) return memoryImage;

        var cacheFile = GetCacheFile(key);
        if (File.Exists(cacheFile))
        {
            var cached = await Task.Run(() => LoadFrozen(cacheFile), cancellationToken);
            if (cached is not null) _memory[key] = new WeakReference<BitmapSource>(cached);
            return cached;
        }

        await _workers.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(cacheFile)) return await Task.Run(() => LoadFrozen(cacheFile), cancellationToken);
            var generated = await Task.Run(() => Generate(item, cacheFile), cancellationToken);
            if (generated is not null) _memory[key] = new WeakReference<BitmapSource>(generated);
            return generated;
        }
        finally { _workers.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _workers.Dispose();
        _memory.Clear();
    }

    public static void TrimDiskCache(long maximumBytes = 1_500_000_000)
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.Cache).EnumerateFiles("*.png", SearchOption.AllDirectories).ToList();
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

    private static BitmapSource? Generate(MediaItem item, string cacheFile)
    {
        if (!File.Exists(item.Path)) return null;
        BitmapSource? image = null;
        if (item.Kind == MediaKind.Photo && MagickPreferredExtensions.Contains(item.Extension))
        {
            try { image = GenerateWithMagick(item.Path); } catch { }
        }
        if (image is null)
        {
            try { image = ShellThumbnail.Get(item.Path, 480, 320); } catch { }
        }
        if (image is null && item.Kind == MediaKind.Photo)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 480;
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
                var encoder = new PngBitmapEncoder();
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
        magick.Thumbnail(480, 320);
        magick.Strip();
        using var encoded = new MemoryStream();
        magick.Write(encoded, MagickFormat.Png);
        encoded.Position = 0;
        var decoder = BitmapDecoder.Create(encoded, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource? LoadFrozen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    private static string CreateKey(MediaItem item)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"THUMB04|{item.Path.ToUpperInvariant()}|{item.ModifiedUtc.Ticks}|{item.Size}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetCacheFile(string key) =>
        System.IO.Path.Combine(AppPaths.Cache, key[..2], key + ".png");

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
