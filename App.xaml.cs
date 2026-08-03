using System.Windows;
using System.Windows.Threading;
using Picall.Services;

namespace Picall;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();
        _singleInstanceMutex = new Mutex(true, "Local\\Picall.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Picall уже открыт.", "Picall", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        WriteStartupLog("Application starting");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    internal static void WriteStartupLog(string message)
    {
        try { File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:O}  {message}\n"); } catch { }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:O}  {e.Exception}\n\n"); }
        catch { }

        MessageBox.Show(
            "Picall столкнулся с ошибкой. Подробности сохранены в журнале приложения.",
            "Picall", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
