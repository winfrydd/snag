using System.Windows;
using System.Windows.Threading;

namespace Snag;

public partial class App : Application
{
    bool _showingError;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log(e.Exception);
        if (_showingError) return; // never stack dialogs on top of each other
        _showingError = true;
        try { MessageBox.Show(e.Exception.Message, "Snag ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _showingError = false; }
    }

    /// <summary>Appends to data\error.log next to the exe (local only, never sent anywhere).</summary>
    static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
