using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Picall.Models;

public sealed class SourceOption : INotifyPropertyChanged
{
    private bool _isSelected;
    public required string Root { get; init; }
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    public required bool IsDrive { get; init; }
    public required bool CanRemove { get; init; }
    public required int Count { get; init; }
    public string IconGlyph => IsDrive ? "\uEDA2" : "\uE8B7";
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    }
    public string CountLabel => Count.ToString("N0").Replace(',', ' ');
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ExcludedPathOption
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public required bool IsSource { get; init; }
}

public sealed class FormatFilterOption : INotifyPropertyChanged
{
    private bool _isSelected = true;
    public required string Extension { get; init; }
    public required int Count { get; init; }
    public string Label => Extension.TrimStart('.').ToUpperInvariant();
    public string CountLabel => Count.ToString("N0").Replace(',', ' ');
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
