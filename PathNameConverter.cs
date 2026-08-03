using System.Globalization;
using System.Windows.Data;

namespace Picall;

public sealed class PathNameConverter : IValueConverter
{
    public static PathNameConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return new DirectoryInfo(path).Name is { Length: > 0 } name ? name : path; }
        catch { return path; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
