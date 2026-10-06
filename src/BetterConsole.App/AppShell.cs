using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using BetterConsole.App.Controls;
using BetterConsole.App.Plugins;
using BetterConsole.App.Services;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;
using BetterConsole.Core.Server;
using BetterConsole.Sdk;

namespace BetterConsole.App;

/// <summary>
/// The application: the servers, the windows that show them and what they all share (settings, command
/// history, journal, plugins). Without multi-console there is one server and one window. With it, the
/// main window shows the servers that are not in a window of their own, one at a time.
/// </summary>
public sealed class AppShell
{
    private readonly Dictionary<ServerViewModel, MainWindow> _detached = new();
    private bool _exiting;

    public AppShell(AppSettings settings)
    {
        Settings = settings;
        History = new CommandHistory(Path.Combine(AppSettings.DataDirectory, "history.txt"));
        Journal = new Journal(Path.Combine(AppSettings.DataDirectory, "journal.jsonl"));
    }

    public static AppShell Current { get; private set; } = null!;

    public AppSettings Settings { get; }
    public CommandHistory History { get; }
    public Journal Journal { get; }
    public PluginManager Plugins { get; } = new();
    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    public MainWindow Main { get; private set; } = null!;
    public bool MultiServer => Settings.MultiServer;
    /// <summary>The servers are being stopped to quit.</summary>
    public bool IsExiting => _exiting;
    public IEnumerable<MainWindow> Windows => _detached.Values.Prepend(Main);

    /// <summary>A server was added, removed, moved to another window, or multi-console was switched.</summary>
    public event Action? ServersChanged;

    public void Start(string? uiScript)
    {
        Current = this;
        LuaLinks.Open = (element, file, line) =>
        {
            var problem = EditorLauncher.Open(Settings, file, line);
            if (problem != null)
                (Window.GetWindow(element) as MainWindow ?? Main)?.ShowToast(null, $"Could not open {Path.GetFileName(file)}: {problem}", NotifyKind.Error);
        };
        Plugins.LoadAll(Settings);
        foreach (var p in Settings.ActiveServers) CreateServer(p, plugins: false);
        Main = new MainWindow(this, isMain: true);
        Application.Current.MainWindow = Main;
        Main.ShowServer(Servers[0]);
        // Plugins after the window: what they say in Initialize must have a window to appear in.
        foreach (var vm in Servers) Plugins.Attach(vm);
        Main.Loaded += async (_, _) =>
        {
            // Servers that had a window of their own get it back (one stays in the main window).
            if (MultiServer)
                foreach (var vm in Servers.Where(s => s.Profile.Window != null).ToList())
                    if (Servers.Count(s => !s.IsDetached) > 1) Detach(vm, activate: false);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await StartupAsync();
            if (uiScript != null) _ = new UiScriptRunner(Main, this, uiScript).RunAsync();
            // Not while a script drives the window (screenshots, tests).
            else if (Settings.CheckForUpdates) _ = CheckUpdatesAtStartAsync();
        };
        Main.Show();
    }

    private static Dispatcher Dispatcher => Application.Current.Dispatcher;

    // ------------------------------------------------------------------------------ updates

    /// <summary>A newer release of BetterConsole on GitHub (found at start or by … → Check for updates), or null.</summary>
    public UpdateChecker.Release? AvailableUpdate { get; private set; }

    /// <summary>The windows show it on their … button.</summary>
    public event Action? UpdateChanged;

    public void SetUpdate(UpdateChecker.Release? release)
    {
        AvailableUpdate = release != null && release.Version > UpdateChecker.Current ? release : null;
        UpdateChanged?.Invoke();
    }

    /// <summary>A few seconds after the start (the servers come first): quietly, a notification only when there is a newer one.</summary>
    private async Task CheckUpdatesAtStartAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        var r = await UpdateChecker.CheckAsync();
        if (r.Error != null)
        {
            Log.Write("update check: " + r.Error);
            return;
        }
        if (!r.IsNewer) return;
        SetUpdate(r.Latest);
        Log.Write($"update check: {r.Latest!.Tag} is out");
        Main.ShowToast(null, $"BetterConsole {r.Latest.Version.ToString(3)} is available: … → Download.", BetterConsole.Sdk.NotifyKind.Info, TimeSpan.FromSeconds(12));
    }

    private async Task StartupAsync()
    {
        if (Servers.Count == 1 && string.IsNullOrWhiteSpace(Servers[0].Profile.ServerDirectory))
        {
            Servers[0].WriteAppLine("Welcome! Choose your server folder in Settings to get started.", false);
            Main.OpenSettings(firstRun: true);
            return;
        }
        foreach (var vm in Servers.ToList())
        {
            if (string.IsNullOrWhiteSpace(vm.Profile.ServerDirectory)) vm.WriteAppLine("Choose the folder of this server in Settings.", false);
            else if (vm.Profile.StartWithApp || vm.Profile.AlwaysRun) _ = vm.StartAsync("Started with BetterConsole");
            else vm.WriteAppLine($"Server: {vm.Profile.ServerDirectory}. Press Start (F5) to launch it.", false);
        }
        await Task.CompletedTask;
    }

    private ServerViewModel CreateServer(ServerProfile profile, bool plugins = true)
    {
        var vm = new ServerViewModel(this, profile);
        vm.CreateTabs();
        vm.Notified += (text, kind) => WindowOf(vm)?.ShowToast(vm, text, kind);
        Servers.Add(vm);
        if (plugins) Plugins.Attach(vm);
        return vm;
    }

    /// <summary>The window that shows the server.</summary>
    public MainWindow? WindowOf(ServerViewModel vm) => _detached.TryGetValue(vm, out var w) ? w : Main;

    /// <summary>Brings the window of the server to the front and shows the server there.</summary>
    public void Focus(ServerViewModel vm)
    {
        var w = WindowOf(vm);
        if (w == null) return;
        if (w == Main && Main.Current != vm) Main.ShowServer(vm);
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    // ------------------------------------------------------------------------------ windows

    /// <summary>Can the server get a window of its own? One server always stays in the main window.</summary>
    public bool CanDetach(ServerViewModel vm) => MultiServer && !vm.IsDetached && Servers.Count(s => !s.IsDetached) > 1;

    /// <summary>Moves the server out of the main window into a window of its own.</summary>
    public void Detach(ServerViewModel vm, bool activate = true)
    {
        if (!CanDetach(vm)) return;
        if (Main.Current == vm) Main.ShowServer(Servers.First(s => s != vm && !s.IsDetached));
        vm.IsDetached = true;
        var w = new MainWindow(this, isMain: false);
        _detached[vm] = w;
        w.Place(vm.Profile.Window ??= new WindowPlacement { Left = Main.Left + 40, Top = Main.Top + 40, Width = Main.ActualWidth, Height = Main.ActualHeight });
        w.ShowServer(vm);
        w.ShowActivated = activate;
        w.Show();
        Settings.Save();
        ServersChanged?.Invoke();
    }

    /// <summary>Puts the server back into the main window (its own window closes).</summary>
    /// <param name="windowClosing">The user closes the server's window: it is closing already.</param>
    public void Attach(ServerViewModel vm, bool select = true, bool windowClosing = false)
    {
        if (!_detached.Remove(vm, out var w)) return;
        w.Release();
        vm.IsDetached = false;
        if (!_exiting) vm.Profile.Window = null;
        if (!windowClosing) w.CloseQuietly();
        if (select) Focus(vm);
        Settings.Save();
        ServersChanged?.Invoke();
    }

    /// <summary>Every server back into the main window.</summary>
    public void MergeAll()
    {
        foreach (var vm in _detached.Keys.ToList()) Attach(vm, select: false);
        Main.Activate();
    }

    /// <summary>
    /// Compact mode or the theme of the settings, and the avatars that go with it (compact mode has none).
    /// Written to the settings file when BetterConsole closes, like the other view settings.
    /// </summary>
    public void SetCompact(bool on)
    {
        Settings.CompactMode = on;
        var theme = ThemeManager.Discover(Path.Combine(AppSettings.DataDirectory, "themes"))
            .FirstOrDefault(t => t.Name.Equals(Settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? ThemePalette.Dark;
        Look.Apply(on, theme);
        foreach (var vm in Servers) vm.ApplyAvatars();
    }

    // ------------------------------------------------------------------------------ servers

    /// <summary>
    /// After the settings were saved: servers that were added get started up, removed ones are closed,
    /// and multi-console switched on or off. Servers that still run are never removed.
    /// </summary>
    public void ApplySettings()
    {
        // A server that still runs is neither removed nor left out by switching multi-console off.
        foreach (var vm in Servers.Where(s => s.IsRunning && !Settings.ActiveServers.Contains(s.Profile)).ToList())
        {
            if (!Settings.Servers.Contains(vm.Profile)) Settings.Servers.Add(vm.Profile);
            if (!Settings.MultiServer) Settings.MultiServer = true;
            vm.Notify("This server still runs, so it stays. Stop it first, then remove it or switch multi-console off.", NotifyKind.Warning);
        }
        var wanted = Settings.ActiveServers.ToList();
        // Without multi-console everything is in the main window again.
        if (!MultiServer) MergeAll();
        foreach (var vm in Servers.Where(s => !wanted.Contains(s.Profile)).ToList()) RemoveServer(vm);
        foreach (var p in wanted)
            if (Servers.All(s => s.Profile != p)) CreateServer(p).WriteAppLine("Server added. Press Start (F5) to launch it.", false);
        // Same order as in the settings.
        var sorted = Servers.OrderBy(s => Settings.Servers.IndexOf(s.Profile)).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Servers.IndexOf(sorted[i]);
            if (cur != i) Servers.Move(cur, i);
        }
        if (Main.Current == null || !Servers.Contains(Main.Current)) Main.ShowServer(Servers.FirstOrDefault(s => !s.IsDetached) ?? Servers[0]);
        foreach (var vm in Servers) vm.RaiseSettingsChanged();
        Settings.Save();
        ServersChanged?.Invoke();
    }

    /// <summary>Takes a server out of the app (it is stopped already; a pending restart is cancelled).</summary>
    private void RemoveServer(ServerViewModel vm)
    {
        try
        {
            if (vm.IsDetached) Attach(vm, select: false);
            if (Main.Current == vm)
            {
                // Another server takes its place in the main window, from its own window if need be.
                var next = Servers.FirstOrDefault(s => s != vm && !s.IsDetached) ?? Servers.FirstOrDefault(s => s != vm);
                if (next != null)
                {
                    if (next.IsDetached) Attach(next, select: false);
                    Main.ShowServer(next);
                }
            }
            Servers.Remove(vm);
            PluginManager.Detach(vm);
            ServersChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("remove server: " + ex);
        }
        _ = ShutdownRemovedAsync(vm);
    }

    private static async Task ShutdownRemovedAsync(ServerViewModel vm)
    {
        try { await vm.ShutdownAsync("The server was removed from BetterConsole"); }
        catch (Exception ex) { Log.Write("remove server: " + ex); }
    }

    // ------------------------------------------------------------------------------ exit

    /// <summary>Closing the main window: stop every server (after asking), then quit.</summary>
    public async Task<bool> ExitAsync()
    {
        if (_exiting) return false;
        var running = Servers.Where(s => s.IsRunning).ToList();
        if (running.Count > 0 && Settings.ConfirmExitWhileRunning)
        {
            var text = running.Count == 1
                ? $"BetterConsole will send \"quit\" to {(MultiServer ? running[0].DisplayName : "the server")} and wait for it to stop. Closing the console always stops the server."
                : $"{running.Count} servers are running ({string.Join(", ", running.Select(s => s.DisplayName))}). BetterConsole will send \"quit\" to each and wait for them to stop.";
            var d = new PromptDialog(Main, running.Count == 1 ? "The server is running" : "Servers are running", text,
                running.Count == 1 ? "Stop server and exit" : "Stop servers and exit", danger: true);
            if (d.ShowDialog() != true) return false;
        }
        _exiting = true;
        foreach (var w in Windows) w.SavePlacement();
        foreach (var vm in Servers)
        {
            if (running.Contains(vm)) vm.WriteAppLine("Stopping the server and closing…", false);
            PluginManager.Detach(vm);
        }
        foreach (var w in _detached.Values.ToList()) w.IsEnabled = false;
        Main.IsEnabled = false;
        try
        {
            await Task.WhenAll(Servers.Select(s => s.ShutdownAsync()));
        }
        catch (Exception ex)
        {
            Log.Write("shutdown: " + ex);
        }
        foreach (var vm in Servers) vm.RaiseSavingSettings();
        Settings.Save();
        foreach (var w in _detached.Values.ToList()) w.CloseQuietly();
        return true;
    }
}
