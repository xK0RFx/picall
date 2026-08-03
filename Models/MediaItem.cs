using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Picall.Models;

public enum MediaKind : byte
{
    Photo = 0,
    Video = 1
}

public sealed class MediaItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _isFavorite;

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required string Extension { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required long Size { get; init; }
    public required MediaKind Kind { get; init; }

    public DateTime ModifiedLocal => ModifiedUtc.ToLocalTime();
    public string DateLabel => ModifiedLocal.ToString("d MMM yyyy");
    public string SizeLabel => FormatSize(Size);
    public string KindLabel => Kind == MediaKind.Video ? "Видео" : "Фото";
    public string ExtensionLabel => Extension.TrimStart('.').ToUpperInvariant();

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { if (!ReferenceEquals(_thumbnail, value)) { _thumbnail = value; OnPropertyChanged(); } }
    }

    public bool ThumbnailRequested
    {
        get => _thumbnailRequested;
        set => _thumbnailRequested = value;
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
