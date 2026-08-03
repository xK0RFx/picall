namespace Picall.Services;

public static class AppPaths
{
    public static readonly string Root = GetRoot();
    public static readonly string Cache = System.IO.Path.Combine(Root, "thumbs");
    public static readonly string IndexFile = System.IO.Path.Combine(Root, "media.index");
    public static readonly string SettingsFile = System.IO.Path.Combine(Root, "settings.json");
    public static readonly string LogFile = System.IO.Path.Combine(Root, "picall.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
    }

    private static string GetRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("PICALL_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridden)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picall")
            : System.IO.Path.GetFullPath(overridden);
    }
}
