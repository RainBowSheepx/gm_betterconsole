using System.Text;
using System.Windows;
using System.Windows.Threading;
using BetterConsole.App.Services;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    /// <summary>
    /// Command line: <c>BetterConsole.exe [--profile name]</c>. Each profile has its own settings
    /// (settings.&lt;name&gt;.json), so one copy of the app can manage several servers.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Crash(ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Write("unobserved task exception: " + ex.Exception);
            ex.SetObserved();
        };

        string? profile = null, uiScript = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] is "--profile" or "-p" && i + 1 < e.Args.Length) profile = e.Args[++i];
            else if (e.Args[i] == "--ui-script" && i + 1 < e.Args.Length) uiScript = Path.GetFullPath(e.Args[++i]);
        }

        var settings = AppSettings.Load(profile);
        var mutexName = "BetterConsole-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(settings.FilePath!.ToLowerInvariant())))[..16];
        _instanceMutex = new Mutex(true, mutexName, out bool created);
        if (!created)
        {
            MessageBox.Show(profile == null
                    ? "BetterConsole is already running for this folder.\n\nTo manage another server start it with --profile <name>."
                    : $"BetterConsole is already running with the profile \"{profile}\".",
                "BetterConsole", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var themes = ThemeManager.Discover(Path.Combine(AppSettings.DataDirectory, "themes"));
        ThemeManager.Apply(themes.FirstOrDefault(t => t.Name.Equals(settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? ThemePalette.Dark);

        Log.Write($"start {typeof(App).Assembly.GetName().Version}, data {AppSettings.DataDirectory}, profile {profile ?? "(default)"}");
        new AppShell(settings).Start(uiScript);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("ui exception: " + e.Exception);
        // Keep the console alive: the server must not die because a view threw.
        e.Handled = true;
        try
        {
            var window = Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive) ?? MainWindow as MainWindow;
            window?.ShowToast(null, "Internal error: " + e.Exception.Message + " (details in logs\\betterconsole.log)", BetterConsole.Sdk.NotifyKind.Error);
        }
        catch { }
    }

    private static void Crash(Exception? ex)
    {
        Log.Write("fatal: " + ex);
        try
        {
            File.WriteAllText(Path.Combine(AppSettings.DataDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), ex?.ToString() ?? "unknown");
        }
        catch { }
    }
}
