using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using BetterConsole.App.Services;
using BetterConsole.App.Views;
using BetterConsole.Core.Console;
using BetterConsole.Core.Errors;
using BetterConsole.Core.Server;
using BetterConsole.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterConsole.App.ViewModels;

/// <summary>
/// One server: its process, console, errors, players, statistics, tabs and plugins. Lives as long as
/// the server is in the list; one window at a time shows it (the main window or a window of its own).
/// </summary>
public sealed partial class ServerViewModel : ObservableObject
{
    private readonly ConcurrentQueue<(string Type, JsonElement Msg, DateTime At)> _bridgeQueue = new();
    private DateTime _bridgeAt;   // when the message being handled came off the pipe
    private readonly ConcurrentQueue<ConsoleEvent> _appLines = new();
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private readonly DispatcherTimer _pump;
    private readonly DispatcherTimer _clock;
    private readonly List<ConsoleEvent> _batch = new(4096);
    private long _appLineId = -1;
    private DateTime _lastLuaStats = DateTime.MinValue;
    private DateTime _lastFrames = DateTime.MinValue;
    private DateTime _lastPoll = DateTime.MinValue;
    private bool _helloReceived;
    // Errors read from the console while the addon was not connected, by EarlyKey. The addon replays
    // the same errors on connect; those are matched against this and not counted twice.
    private readonly Dictionary<string, int> _textErrorsOffline = new();
    private ProcessSnapshot? _lastProcess;
    // The next scheduled restart and the warnings already said for it (minutes before).
    private DateTime? _nextRestart;
    private readonly HashSet<int> _warned = new();

    public ServerViewModel(AppShell shell, ServerProfile profile)
    {
        Shell = shell;
        Settings = shell.Settings;
        History = shell.History;
        Files = new LuaFileResolver(() => Profile.GameDirectory);
        Controller = new ServerController(profile)
        {
            BeforeStart = PrepareServerAsync,
        };
        Controller.Pipeline.HideErrors = Settings.HideErrorsInConsole;
        Controller.StateChanged += (o, n, code) => _uiActions.Enqueue(() => OnStateChanged(o, n, code));
        Controller.Lifecycle += e => shell.Journal.Add(Profile.Id, DisplayName, e);
        Controller.Notice += (text, isError) => WriteAppLine(text, isError);
        Controller.CommandEcho += text => _appLines.Enqueue(new LineAdded(Interlocked.Decrement(ref _appLineId),
            new ConsoleLine { Text = text, Time = DateTime.Now, Kind = ConsoleLineKind.Command }));
        Controller.Sampled += s => _uiActions.Enqueue(() => OnProcessSample(s));
        Controller.Bridge.ConnectionChanged += c => _uiActions.Enqueue(() => OnBridgeConnection(c));
        Controller.Bridge.MessageReceived += (t, m) => _bridgeQueue.Enqueue((t, m, DateTime.Now));
        StatsExtras = new LuaTabVm("@stats") { ActionSink = (t, w, a) => Request("action", new { tab = t, widget = w, id = a }) };

        ServerErrors.MaxItems = Settings.MaxErrorsPerList;
        ClientErrors.MaxItemsPerPlayer = Math.Max(50, Settings.MaxErrorsPerList / 2);
        ServerErrors.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ServerErrorsVm.UnseenCount)) OnPropertyChanged(nameof(UnseenErrors)); };
        ClientErrors.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ClientErrorsVm.UnseenCount)) OnPropertyChanged(nameof(UnseenErrors)); };

        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _pump.Tick += (_, _) => Pump();
        _pump.Start();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockTick();
        _clock.Start();
        UpdateTitle();
        UpdateSchedule();
    }

    public AppShell Shell { get; }
    public AppSettings Settings { get; }
    public ServerController Controller { get; }
    /// <summary>This server's settings (the same object as in <see cref="AppSettings.Servers"/>).</summary>
    public ServerProfile Profile => Controller.Profile;
    /// <summary>Finds the Lua files of this server for links in errors and the profiler.</summary>
    public LuaFileResolver Files { get; }
    public CommandHistory History { get; }
    public CommandCatalog Catalog { get; } = new();
    public ServerErrorsVm ServerErrors { get; } = new();
    public ClientErrorsVm ClientErrors { get; } = new();
    public StatsVm Stats { get; } = new();
    public PlayersVm Players { get; } = new();
    public ObservableCollection<TabVm> Tabs { get; } = new();
    public ObservableCollection<StatusItemVm> ExtraStatus { get; } = new();
    public Dictionary<string, LuaTabVm> LuaTabs { get; } = new();
    /// <summary>What addons (BetterConsole.Stats) and plugins (IUiHost.Stats) put on the Statistics tab.</summary>
    public LuaTabVm StatsExtras { get; }
    /// <summary>Items of addons and plugins in the player menu.</summary>
    public List<PlayerActionDef> PlayerActions { get; } = new();
    /// <summary>Built-in items of the player menu an addon hid (BetterConsole.HidePlayerAction).</summary>
    public HashSet<string> PlayerActionsHidden { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The player menu is about to open (plugins change it): the selected players and the menu.</summary>
    public event Action<IReadOnlyList<PlayerRowVm>, System.Windows.Controls.ContextMenu>? PlayerMenuOpening;

    public void RaisePlayerMenuOpening(IReadOnlyList<PlayerRowVm> players, System.Windows.Controls.ContextMenu menu) => PlayerMenuOpening?.Invoke(players, menu);

    // Built-in parts of the Statistics tab that are hidden: by the addon (its whole list, sent again on
    // every change) and by plugins.
    private readonly HashSet<string> _statsHiddenLua = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _statsHiddenPlugins = new();

    /// <summary>The built-in parts of the Statistics tab that changed between hidden and shown.</summary>
    public event Action? StatsHiddenChanged;

    public bool IsStatHidden(string id) => _statsHiddenLua.Contains(id) || _statsHiddenPlugins.Values.Any(s => s.Contains(id));

    public void SetStatHidden(string plugin, string id, bool hidden)
    {
        if (!_statsHiddenPlugins.TryGetValue(plugin, out var set)) _statsHiddenPlugins[plugin] = set = new(StringComparer.OrdinalIgnoreCase);
        if (hidden ? set.Add(id) : set.Remove(id)) StatsHiddenChanged?.Invoke();
    }
    /// <summary>The plugin instances of this server (each server has its own).</summary>
    public List<Plugins.PluginInstance> PluginInstances { get; } = new();

    /// <summary>Console events in order, delivered on the UI thread about 30 times a second.</summary>
    public event Action<IReadOnlyList<ConsoleEvent>>? ConsoleEvents;
    /// <summary>A bridge message after the built-in handling (for plugins), on the UI thread.</summary>
    public event Action<string, JsonElement>? BridgeMessage;
    public event Action<bool>? BridgeConnectionChanged;
    public event Action<ServerState, ServerState, int?>? ServerStateChanged;
    public event Action<ServerSnapshot>? SnapshotUpdated;
    /// <summary>A server addon defined a tab.</summary>
    public event Action<LuaTabVm>? LuaTabAdded;
    public event Action<string>? LuaTabRemoved;
    /// <summary>The settings window saved new values.</summary>
    public event Action? SettingsChanged;
    /// <summary>Right before the settings are written on exit: views put what they remember into them.</summary>
    public event Action? SavingSettings;

    public void RaiseSavingSettings() => SavingSettings?.Invoke();

    /// <summary>A notification for the window that shows this server (on the UI thread).</summary>
    public event Action<string, NotifyKind>? Notified;

    public void RaiseSettingsChanged()
    {
        Controller.Pipeline.HideErrors = Settings.HideErrorsInConsole;
        ServerErrors.MaxItems = Settings.MaxErrorsPerList;
        ClientErrors.MaxItemsPerPlayer = Math.Max(50, Settings.MaxErrorsPerList / 2);
        ApplyAvatars();
        UpdateTitle();
        UpdateSchedule();
        SettingsChanged?.Invoke();
    }

    /// <summary>The built-in tabs. Addon tabs come and go with the addon, plugin tabs with the plugins.</summary>
    public void CreateTabs()
    {
        AddTab(new TabVm("console", "Console", "", 0, () => new ConsoleView(this)));
        AddTab(new TabVm("players", "Players", "", 10, () => new PlayersView(this)));
        AddTab(new TabVm("client-errors", "Client errors", "", 20, () => new ClientErrorsView(this)));
        AddTab(new TabVm("server-errors", "Server errors", "", 30, () => new ServerErrorsView(this)));
        AddTab(new TabVm("stats", "Statistics", "", 40, () => new StatsView(this)));
        SelectedTab = FindTab("console");
        // The console keeps every line from the start, also of a server no window shows yet.
        _ = FindTab("console")!.Content;
        LuaTabAdded += tab => AddTab(new TabVm("lua:" + tab.Id, tab.Title, tab.Icon, 100 + tab.Order, () => new LuaTabView(tab)));
        LuaTabRemoved += id => RemoveTab("lua:" + id);
        ApplyAvatars();
    }

    /// <summary>Steam avatars as the settings say; none in compact mode (nothing downloaded or decoded).</summary>
    public void ApplyAvatars()
    {
        bool on = Settings.ShowAvatars && !Settings.CompactMode;
        Players.ShowAvatars = on;
        ClientErrors.ShowAvatars = on;
    }


    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRunning), nameof(CanStart), nameof(StateText), nameof(StateColorKey))] private ServerState state;
    [ObservableProperty] private TabVm? selectedTab;
    [ObservableProperty] private string windowTitle = "BetterConsole";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HostnameIfDifferent))] private string? hostname;
    /// <summary>The name in the server list: the name given in the settings, else the hostname, else the folder.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HostnameIfDifferent))] private string displayName = "Server";
    /// <summary>Shown in a window of its own instead of the main window.</summary>
    [ObservableProperty] private bool isDetached;
    /// <summary>"Restart at 05:00" (scheduled), or empty.</summary>
    [ObservableProperty] private string nextRestartText = "";
    /// <summary>One line for the server list: map, players and server fps, or the state.</summary>
    [ObservableProperty] private string summaryText = "Stopped";

    /// <summary>The hostname under the name in the server list, when the name is not the hostname already.</summary>
    public string? HostnameIfDifferent => !string.IsNullOrWhiteSpace(Hostname) && Hostname != DisplayName ? Hostname : null;
    /// <summary>Errors of this server nobody has looked at yet (a badge in the server list).</summary>
    public int UnseenErrors => ServerErrors.UnseenCount > 0 ? ServerErrors.UniqueCount : 0;
    /// <summary>Theme brush of the state dot.</summary>
    public string StateColorKey => State switch
    {
        ServerState.Running => ServerIdle ? "Brush.Warning" : "Brush.Success",
        ServerState.Starting or ServerState.Stopping => "Brush.Warning",
        ServerState.Crashed => "Brush.Danger",
        _ => "Brush.TextMuted",
    };

    partial void OnServerIdleChanged(bool value) => OnPropertyChanged(nameof(StateColorKey));
    [ObservableProperty] private string uptimeText = "";
    [ObservableProperty] private bool bridgeConnected;
    [ObservableProperty] private string bridgeText = "Addon: off";
    [ObservableProperty] private string bridgeTooltip = "The companion addon is not connected.";

    [ObservableProperty] private string mapText = "—";
    [ObservableProperty] private string playersText = "—";
    [ObservableProperty] private string cpuText = "—";
    [ObservableProperty] private string cpuTooltip = "";
    [ObservableProperty] private string inText = "—";
    [ObservableProperty] private string outText = "—";
    [ObservableProperty] private string svText = "—";
    [ObservableProperty] private string svTooltip = "";
    [ObservableProperty] private string tickText = "—";
    [ObservableProperty] private string loadText = "—";
    [ObservableProperty] private string entsText = "—";
    [ObservableProperty] private string luaText = "—";
    [ObservableProperty] private string memText = "—";
    [ObservableProperty] private bool serverIdle;
    /// <summary>Tab headers show only their icons (the title is in the tooltip).</summary>
    [ObservableProperty] private bool compactTabs;

    private int? _lastExitCode;

    public bool IsRunning => State is ServerState.Running or ServerState.Starting or ServerState.Stopping;
    public bool CanStart => State is ServerState.Stopped or ServerState.Crashed;
    public string StateText => State switch
    {
        ServerState.Running => "Running",
        ServerState.Starting => "Starting",
        ServerState.Stopping => "Stopping",
        ServerState.Crashed => _lastExitCode == 0 ? "Exited" : "Crashed",
        _ => "Stopped",
    };

    public ServerSnapshot? Latest { get; private set; }

    // ------------------------------------------------------------------------------ tabs

    public TabVm AddTab(TabVm tab)
    {
        int i = 0;
        while (i < Tabs.Count && Tabs[i].Order <= tab.Order) i++;
        Tabs.Insert(i, tab);
        SelectedTab ??= tab;
        return tab;
    }

    public void RemoveTab(string id)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == id);
        if (tab == null) return;
        if (SelectedTab == tab) SelectedTab = Tabs.FirstOrDefault();
        Tabs.Remove(tab);
    }

    public TabVm? FindTab(string id) => Tabs.FirstOrDefault(t => t.Id == id);

    partial void OnSelectedTabChanged(TabVm? oldValue, TabVm? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        MarkSeen();
        if (oldValue?.Id == "players" || newValue?.Id == "players") SendPlayerSubscription();
        UpdateBadges();
    }

    /// <summary>
    /// A window shows this server now (or stopped showing it). Errors count as seen and the per-player
    /// load is measured only while somebody can see them, not for a server in the background.
    /// </summary>
    [ObservableProperty] private bool isShown;

    partial void OnIsShownChanged(bool value)
    {
        MarkSeen();
        SendPlayerSubscription();
        UpdateBadges();
    }

    /// <summary>The errors of the tab that is in view are not new any more.</summary>
    private void MarkSeen()
    {
        if (!IsShown) return;
        switch (SelectedTab?.Id)
        {
            case "server-errors": ServerErrors.UnseenCount = 0; break;
            case "client-errors": ClientErrors.UnseenCount = 0; break;
        }
    }

    private void SendPlayerSubscription() => Request("sub", new { players = IsShown && SelectedTab?.Id == "players" });

    /// <summary>
    /// Sends a request to the addon. A hibernating server runs no frames, so nothing would read it:
    /// then the hidden console command betterconsole_poll makes Lua answer at once.
    /// </summary>
    public void Request(string type, object? data = null)
    {
        Controller.Bridge.Send(type, data);
        if (BridgeConnected && (DateTime.Now - _lastFrames).TotalSeconds > 1.5) Poll();
    }

    private void Poll()
    {
        _lastPoll = DateTime.Now;
        Controller.TypeInternal("betterconsole_poll");
    }

    private void UpdateBadges()
    {
        if (FindTab("server-errors") is { } se)
        {
            se.Badge = ServerErrors.UniqueCount > 0 ? ServerErrors.UniqueCount.ToString() : null;
            se.BadgeKind = ServerErrors.UnseenCount > 0 ? "danger" : "muted";
        }
        if (FindTab("client-errors") is { } ce)
        {
            ce.Badge = ClientErrors.Players.Count > 0 ? ClientErrors.Players.Count.ToString() : null;
            ce.BadgeKind = ClientErrors.UnseenCount > 0 ? "danger" : "muted";
        }
        if (FindTab("players") is { } pt)
        {
            pt.Badge = Players.HasData ? Players.Count.ToString() : null;
            pt.BadgeKind = "muted";
        }
    }

    // ------------------------------------------------------------------------------ server control

    // The buttons of the header: their reasons go to the journal.
    [RelayCommand]
    private Task Start() => StartAsync("Start button");

    [RelayCommand]
    private Task Stop() => StopAsync("Stop button");

    [RelayCommand]
    private Task Restart() => RestartAsync("Restart button");

    public async Task StartAsync(string reason)
    {
        if (!CanStart) return;
        Settings.Save();
        await Controller.StartAsync(reason);
    }

    public Task StopAsync(string reason) => Controller.StopAsync(reason);

    public async Task RestartAsync(string reason)
    {
        Settings.Save();
        await Controller.RestartAsync(reason);
    }

    public void Kill() => Controller.Kill("Killed from BetterConsole (… → Kill the server process)");

    private Task PrepareServerAsync(ServerProfile profile, string exe)
    {
        if (!profile.InstallCompanion) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var r = CompanionInstaller.Install(profile, exe);
            if (r.Written > 0) WriteAppLine($"Companion addon installed/updated ({r.Written} file(s)).", false);
            foreach (var w in r.Warnings) WriteAppLine(w, true);
        });
    }

    /// <summary>Runs a console command (typed by the user: it goes into the history). Returns false when it could not be sent.</summary>
    public bool SendCommand(string text, bool remember = true)
    {
        text = text.Trim();
        if (text.Length == 0) return true;
        if (remember) History.Add(text);
        if ((Stats.Capture || Controller.Pipeline.CaptureVprof) && text.Split(';').Any(c => c.TrimStart().StartsWith("vprof", StringComparison.OrdinalIgnoreCase))) PauseVprofCapture();
        if (!Controller.SendCommand(text, out var problem))
        {
            WriteAppLine(problem ?? "The command could not be sent.", true);
            return false;
        }
        return true;
    }

    /// <summary>
    /// A line of BetterConsole's own (a notice). Long ones are broken at spaces: a line wider than the
    /// console would bring up its horizontal scroll bar (start options, long exit reasons).
    /// </summary>
    public void WriteAppLine(string text, bool isError, uint argb = 0)
    {
        const int max = 100;
        var now = DateTime.Now;
        bool first = true;
        foreach (var part in Wrap(text, max))
        {
            // The console shows a continuation without its marker (see ConsoleView.AppendPending).
            var t = first ? part : "  " + part;
            first = false;
            var line = new ConsoleLine
            {
                Text = t,
                Time = now,
                Kind = isError ? ConsoleLineKind.AppError : ConsoleLineKind.App,
                Spans = argb != 0 ? [new ColorSpan(0, t.Length, argb)] : Array.Empty<ColorSpan>(),
            };
            _appLines.Enqueue(new LineAdded(Interlocked.Decrement(ref _appLineId), line));
        }
    }

    private static IEnumerable<string> Wrap(string text, int max)
    {
        foreach (var raw in text.Split('\n'))
        {
            var rest = raw.TrimEnd('\r');
            while (rest.Length > max)
            {
                int cut = rest.LastIndexOf(' ', max);
                if (cut < max / 2) cut = max; // one long word (a path): cut it
                yield return rest[..cut].TrimEnd();
                rest = rest[cut..].TrimStart();
            }
            yield return rest;
        }
    }

    /// <summary>A toast in the window that shows this server.</summary>
    public void Notify(string text, NotifyKind kind = NotifyKind.Info)
    {
        if (Application.Current.Dispatcher.CheckAccess()) Notified?.Invoke(text, kind);
        else _uiActions.Enqueue(() => Notified?.Invoke(text, kind));
    }

    private void OnStateChanged(ServerState old, ServerState now, int? exitCode)
    {
        _lastExitCode = exitCode;
        State = now;
        if (now is ServerState.Stopped or ServerState.Crashed)
        {
            BridgeConnected = false;
            ServerIdle = false;
            Players.Clear();
            UpdateBadges();
            CpuText = InText = OutText = SvText = TickText = LoadText = EntsText = LuaText = MemText = PlayersText = "—";
            // What the addon put into the app stops with the server (the next start may run without it).
            ForgetLuaExtras();
        }
        if (now == ServerState.Crashed)
        {
            if (exitCode == 0) Notify("The server quit by itself (exit code 0).", NotifyKind.Warning);
            else Notify($"The server crashed: exit code {ServerController.FormatExitCode(exitCode ?? 0)}.", NotifyKind.Error);
        }
        if (now == ServerState.Starting) Stats.Clear();
        if (now == ServerState.Running) UpdateSchedule();
        ServerStateChanged?.Invoke(old, now, exitCode);
        UpdateTitle();
        UpdateSummary();
    }

    partial void OnHostnameChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Profile.LastHostname) return;
        Profile.LastHostname = value;
    }

    private void UpdateTitle()
    {
        var host = !string.IsNullOrWhiteSpace(Hostname) ? Hostname
            : !string.IsNullOrWhiteSpace(Profile.LastHostname) ? Profile.LastHostname : null;
        DisplayName = !string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Name.Trim()
            : host ?? (string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? "New server" : Path.GetFileName(Profile.ServerDirectory.TrimEnd('\\', '/')));
        var name = Settings.MultiServer ? DisplayName : host;
        WindowTitle = name != null ? $"{name} — BetterConsole" : "BetterConsole";
    }

    // ------------------------------------------------------------------------------ scheduled restarts

    /// <summary>The next restart of the schedule (after the settings changed or a restart happened).</summary>
    private void UpdateSchedule()
    {
        var next = Profile.NextRestart(DateTime.Now);
        // The same restart (settings saved, the server came back): the warnings said for it stay said.
        if (next != _nextRestart) _warned.Clear();
        _nextRestart = next;
        NextRestartText = _nextRestart is { } at
            ? $"Restart {(at.Date == DateTime.Today ? "at" : "tomorrow at")} {at:HH:mm}"
            : "";
    }

    /// <summary>Once a second: warnings in the chat before a scheduled restart, then the restart.</summary>
    private void CheckSchedule()
    {
        if (_nextRestart is not { } at) return;
        var now = DateTime.Now;
        if (now >= at)
        {
            UpdateSchedule();
            // A restart that is minutes overdue (the PC was asleep) is skipped.
            if ((now - at).TotalMinutes > 2) return;
            if (State != ServerState.Running) return;
            WriteAppLine($"Scheduled restart ({at:HH:mm}).", false);
            _ = RestartAsync($"Scheduled restart ({at:HH:mm})");
            return;
        }
        if (State != ServerState.Running) return;
        double left = (at - now).TotalSeconds;
        foreach (var minutes in Profile.RestartWarningMinutes)
        {
            if (minutes <= 0 || _warned.Contains(minutes) || left > minutes * 60) continue;
            _warned.Add(minutes);
            // Only the latest of the warnings that are due (BetterConsole was started a minute before).
            if (Profile.RestartWarningMinutes.Any(m => m > 0 && m < minutes && left <= m * 60)) continue;
            if (string.IsNullOrWhiteSpace(Profile.RestartWarningText)) continue;
            // What is really left (BetterConsole may have started after the warning was due).
            int mins = (int)Math.Round(left / 60);
            var time = mins >= 2 ? $"{mins} minutes" : left >= 50 ? "1 minute" : $"{(int)Math.Ceiling(left)} seconds";
            SendCommand("say " + Profile.RestartWarningText.Replace("{time}", time), remember: false);
        }
    }

    // ------------------------------------------------------------------------------ the pump

    private void Pump()
    {
        while (_uiActions.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { Log.Write("ui action: " + ex); }
        }

        _batch.Clear();
        while (_appLines.TryDequeue(out var line)) _batch.Add(line);
        int budget = 20000;
        while (budget-- > 0 && Controller.Pipeline.Events.TryDequeue(out var e))
        {
            switch (e)
            {
                case VprofCaptured vc:
                    OnVprof(vc);
                    break;
                case ErrorRecognized er:
                    OnTextError(er.Error);
                    break;
                case TitleChanged tc:
                    if (tc.Title != "SOURCE DEDICATED SERVER" && !tc.Title.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Hostname = tc.Title;
                        UpdateTitle();
                    }
                    break;
                case LineAdded la:
                    // rcon from "1.2.3.4:27005": command "quit"  (with the log prefix when logging is on)
                    if (la.Line.Text.Contains("rcon from \"", StringComparison.Ordinal) && RconQuit().Match(la.Line.Text) is { Success: true } rm)
                        Controller.NoteShutdownCause($"rcon from {rm.Groups["ip"].Value}: \"{rm.Groups["cmd"].Value}\"");
                    // GMod refuses "quit" from Lua: "game.ConsoleCommand blocked! (quit)". It is not why the server stops later.
                    else if (la.Line.Text.Contains(" blocked! (", StringComparison.Ordinal) && BlockedQuit().IsMatch(la.Line.Text))
                    {
                        _quitBlockedAt = DateTime.Now;
                        Controller.ClearShutdownCause();
                    }
                    _batch.Add(e);
                    break;
                default:
                    _batch.Add(e);
                    break;
            }
        }
        if (_batch.Count > 0) ConsoleEvents?.Invoke(_batch);

        int msgBudget = 2000;
        while (msgBudget-- > 0 && _bridgeQueue.TryDequeue(out var m))
        {
            try
            {
                _bridgeAt = m.At;
                HandleBridge(m.Type, m.Msg);
                BridgeMessage?.Invoke(m.Type, m.Msg);
            }
            catch (Exception ex)
            {
                Log.Write($"bridge message {m.Type}: {ex}");
            }
        }
    }

    private void ClockTick()
    {
        if (IsRunning && State == ServerState.Running)
        {
            UptimeText = ServerController.FormatSpan(DateTime.Now - Controller.StartedAt);
            bool idle = BridgeConnected && _helloReceived && (DateTime.Now - _lastFrames).TotalSeconds > 3.5;
            if (idle != ServerIdle)
            {
                ServerIdle = idle;
                if (idle)
                {
                    SvText = "idle";
                    SvTooltip = "No server frames for a few seconds: the server is hibernating (no players) or frozen.";
                }
            }
            // A sleeping server still reads its console: ask it for a summary every few seconds.
            if (idle && (DateTime.Now - _lastPoll).TotalSeconds > 5) Poll();
        }
        else UptimeText = "";
        CheckSchedule();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (State != ServerState.Running)
        {
            SummaryText = State == ServerState.Crashed && _lastExitCode is { } code and not 0
                ? $"Crashed ({ServerController.FormatExitCode(code)})"
                : StateText;
            return;
        }
        var parts = new List<string>();
        if (MapText is { Length: > 0 } map && map != "—") parts.Add(map);
        if (PlayersText is { Length: > 0 } pl && pl != "—") parts.Add(pl);
        // "66.0 fps ±0.4 ms" → "66.0 fps"; "hibernating", "idle" as they are.
        if (SvText is { Length: > 0 } sv && sv != "—") parts.Add(sv.Contains(" fps") ? sv[..(sv.IndexOf(" fps") + 4)] : sv);
        SummaryText = parts.Count > 0 ? string.Join(" · ", parts) : "Running · " + UptimeText;
    }

    [GeneratedRegex(@"rcon from ""(?<ip>[^""]+)"": command ""(?<cmd>(?:quit|exit|_restart)\b[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex RconQuit();

    [GeneratedRegex(@"blocked! \((?:quit|exit|_restart)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BlockedQuit();

    private DateTime _quitBlockedAt = DateTime.MinValue;

    // ------------------------------------------------------------------------------ bridge

    private void OnBridgeConnection(bool connected)
    {
        BridgeConnected = connected;
        _helloReceived = false;
        if (!connected)
        {
            // The replay of the previous offline window has arrived by now.
            _textErrorsOffline.Clear();
            BridgeText = "Addon: off";
            BridgeTooltip = IsRunning
                ? "The companion addon is not connected (server still loading, map change, or the addon/module is missing)."
                : "The server is not running.";
            Stats.Profiling = false;
        }
        BridgeConnectionChanged?.Invoke(connected);
    }

    private void HandleBridge(string type, JsonElement m)
    {
        switch (type)
        {
            case "hello":
                _helloReceived = true;
                _clockSamples.Clear();   // a new server process: a new SysTime
                _clockOffset = double.NaN;
                BridgeText = "Addon: on";
                BridgeTooltip = $"Companion addon {StatsVm.Str(m, "addon")}, module {StatsVm.Str(m, "module")}\n" +
                                $"Garry's Mod {StatsVm.Str(m, "gmod")} ({StatsVm.Str(m, "branch")}), {StatsVm.Str(m, "gamemode")} on {StatsVm.Str(m, "map")}";
                if (StatsVm.Str(m, "hostname") is { Length: > 0 } hn)
                {
                    Hostname = hn;
                    UpdateTitle();
                }
                MapText = StatsVm.Str(m, "map") ?? MapText;
                var tr = StatsVm.Num(m, "tickrate");
                if (!double.IsNaN(tr)) Stats.TickRate = tr;
                SendPlayerSubscription();
                // A new Lua state (map change, restart) knows nothing of the capture.
                if (Stats.Capture) ApplyCapture();
                // Addons are still loading right after the connection: ask for the command list a bit later.
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (_, _) => { t.Stop(); if (BridgeConnected) Request("cmds"); };
                t.Start();
                // Lua tabs are sent again by the addon after a reconnect, and so is the rest it defined.
                foreach (var id in LuaTabs.Keys.ToList()) LuaTabRemoved?.Invoke(id);
                LuaTabs.Clear();
                ForgetLuaExtras();
                break;
            case "stats":
                OnLuaStats(m);
                break;
            case "players":
                Players.TickBudgetMs = double.IsNaN(Stats.TickRate) ? 15 : 1000 / Stats.TickRate;
                Players.Apply(m);
                PlayersText = Players.MaxPlayers > 0 ? $"{Players.Count - Players.Bots}/{Players.MaxPlayers}" + (Players.Bots > 0 ? $" +{Players.Bots}" : "") : $"{Players.Count}";
                UpdateBadges();
                break;
            case "err":
                OnBridgeError(m);
                break;
            case "prof":
                Stats.ApplyProfile(m);
                break;
            case "prof_state":
                Stats.Profiling = m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                break;
            case "cmds":
                Catalog.Load(m, Settings.CompleteServerCommandsOnly);
                if (Catalog.LoadError != null) WriteAppLine("Command list: only Lua commands are known (" + Catalog.LoadError + ").", true);
                break;
            case "cvals":
                if (m.TryGetProperty("vals", out var vals)) Catalog.UpdateValues(vals);
                break;
            case "tab":
                OnLuaTab(m);
                break;
            case "tab_rm":
                if (StatsVm.Str(m, "id") is { } rid && LuaTabs.Remove(rid)) LuaTabRemoved?.Invoke(rid);
                break;
            case "w":
                if (WidgetTab(m) is { } tab && StatsVm.Str(m, "id") is { } wid)
                    tab.Define(wid, StatsVm.Str(m, "kind") ?? "text", m.TryGetProperty("opts", out var o) ? o : default);
                break;
            case "wd":
                if (WidgetTab(m) is { } tab2 && StatsVm.Str(m, "id") is { } wid2)
                    tab2.Data(wid2, StatsVm.Str(m, "op") ?? "set", m.TryGetProperty("data", out var d) ? d : default);
                break;
            case "w_rm":
                if (WidgetTab(m) is { } tab3 && StatsVm.Str(m, "id") is { } wid3) tab3.Remove(wid3);
                break;
            case "stats_hide":
                _statsHiddenLua.Clear();
                if (m.TryGetProperty("ids", out var hids) && hids.ValueKind == JsonValueKind.Array)
                    foreach (var h in hids.EnumerateArray())
                        if (h.ValueKind == JsonValueKind.String) _statsHiddenLua.Add(h.GetString()!);
                StatsHiddenChanged?.Invoke();
                break;
            case "pa":
                var action = PlayerActionDef.FromLua(m);
                PlayerActions.RemoveAll(a => a.Id == action.Id);
                PlayerActions.Add(action);
                break;
            case "pa_rm":
                PlayerActions.RemoveAll(a => a.Id == "lua:" + StatsVm.Str(m, "id"));
                break;
            case "pa_hide":
                PlayerActionsHidden.Clear();
                if (m.TryGetProperty("ids", out var pids) && pids.ValueKind == JsonValueKind.Array)
                    foreach (var h in pids.EnumerateArray())
                        if (h.ValueKind == JsonValueKind.String) PlayerActionsHidden.Add(h.GetString()!);
                break;
            case "st":
                OnStatusItem(m);
                break;
            case "st_rm":
                if (ExtraStatus.FirstOrDefault(s => s.Id == "lua:" + StatsVm.Str(m, "id")) is { } sr) ExtraStatus.Remove(sr);
                break;
            case "quitcmd":
                OnQuitCommand(m);
                break;
            case "notify":
                Notify(StatsVm.Str(m, "text") ?? "", (StatsVm.Str(m, "kind") ?? "info") switch
                {
                    "success" => NotifyKind.Success,
                    "warning" => NotifyKind.Warning,
                    "error" => NotifyKind.Error,
                    _ => NotifyKind.Info,
                });
                break;
        }
    }

    /// <summary>For the UI script runner: a message as if the addon had sent it.</summary>
    public void InjectBridgeMessage(string type, string json) =>
        _bridgeQueue.Enqueue((type, JsonDocument.Parse(json).RootElement.Clone(), DateTime.Now));

    /// <summary>
    /// Lua ran "quit" / "_restart" (ulx rcon, a restart addon, a chat command): who and from where, for
    /// the journal when the server exits right after.
    /// </summary>
    private void OnQuitCommand(JsonElement m)
    {
        // GMod said it refused this one (the console line can come before the message).
        if ((DateTime.Now - _quitBlockedAt).TotalSeconds < 3) return;
        var cmd = StatsVm.Str(m, "cmd") ?? "quit";
        var where = StatsVm.Str(m, "src") is { Length: > 0 } src ? $" ({src})" : "";
        if (m.TryGetProperty("ply", out var p) && p.ValueKind == JsonValueKind.Object)
            Controller.NoteShutdownCause($"Player {StatsVm.Str(p, "name") ?? "?"} ({StatsVm.Str(p, "sid") ?? "?"}) ran \"{cmd}\"{where}");
        else
            Controller.NoteShutdownCause($"\"{cmd}\" from Lua{where}");
    }

    private void OnLuaTab(JsonElement m)
    {
        var id = StatsVm.Str(m, "id");
        if (id == null) return;
        if (!LuaTabs.TryGetValue(id, out var tab))
        {
            tab = new LuaTabVm(id) { ActionSink = (t, w, a) => Request("action", new { tab = t, widget = w, id = a }) };
            LuaTabs[id] = tab;
            tab.Title = StatsVm.Str(m, "title") ?? id;
            tab.Icon = LuaTabVm.ParseIcon(StatsVm.Str(m, "icon"));
            var order = StatsVm.Num(m, "order");
            tab.Order = double.IsNaN(order) ? 100 : (int)order;
            LuaTabAdded?.Invoke(tab);
        }
        else
        {
            tab.Title = StatsVm.Str(m, "title") ?? tab.Title;
            tab.Icon = LuaTabVm.ParseIcon(StatsVm.Str(m, "icon"));
            if (FindTab("lua:" + id) is { } tv)
            {
                tv.Header = tab.Title;
                tv.Icon = tab.Icon;
            }
        }
    }

    /// <summary>The tab a widget message is for: an addon tab, or the Statistics tab ("@stats").</summary>
    private LuaTabVm? WidgetTab(JsonElement m)
    {
        var id = StatsVm.Str(m, "tab") ?? "";
        if (id == StatsExtras.Id) return StatsExtras;
        return LuaTabs.TryGetValue(id, out var tab) ? tab : null;
    }

    /// <summary>A new Lua state (or the server stopped): what the addon put into the app goes; it sends it again on connect.</summary>
    private void ForgetLuaExtras()
    {
        StatsExtras.RemoveLuaWidgets();
        if (_statsHiddenLua.Count > 0)
        {
            _statsHiddenLua.Clear();
            StatsHiddenChanged?.Invoke();
        }
        PlayerActions.RemoveAll(a => a.Id.StartsWith("lua:", StringComparison.Ordinal));
        PlayerActionsHidden.Clear();
        for (int i = ExtraStatus.Count - 1; i >= 0; i--)
            if (ExtraStatus[i].Id.StartsWith("lua:", StringComparison.Ordinal)) ExtraStatus.RemoveAt(i);
    }

    private void OnStatusItem(JsonElement m)
    {
        var id = "lua:" + StatsVm.Str(m, "id");
        var o = StatsVm.Num(m, "order");
        int order = double.IsNaN(o) ? 500 : (int)Math.Clamp(o, -100000, 100000);
        var item = ExtraStatus.FirstOrDefault(s => s.Id == id);
        if (item != null && item.Order != order)
        {
            ExtraStatus.Remove(item);
            item = null;
        }
        if (item == null)
        {
            item = new StatusItemVm(id, order) { UserHidden = Settings.StatusHidden.Contains(id) };
            InsertStatusItem(item);
        }
        item.Text = StatsVm.Str(m, "text") ?? "";
        item.Label = StatsVm.Str(m, "label");
        item.Name = StatsVm.Str(m, "name");
        item.Tooltip = StatsVm.Str(m, "tip");
        item.Argb = Themes.ThemeManager.TryParse(StatsVm.Str(m, "color"), out var c) ? 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B : 0;
    }

    /// <summary>Into the status bar by its order (addons and plugins alike).</summary>
    public void InsertStatusItem(StatusItemVm item)
    {
        int i = 0;
        while (i < ExtraStatus.Count && ExtraStatus[i].Order <= item.Order) i++;
        ExtraStatus.Insert(i, item);
    }

    private void OnBridgeError(JsonElement m)
    {
        var frames = new List<StackFrame>();
        if (m.TryGetProperty("stack", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var f in st.EnumerateArray())
                frames.Add(new StackFrame(StatsVm.Str(f, "fn") ?? "unknown", StatsVm.Str(f, "src") ?? "?", (int)StatsVm.Num0(f, "line")));
        // A Lua traceback in the message (ErrorNoHalt(debug.traceback())) says more than GMod's stack of that call.
        var (message, traceback) = LuaTraceback.Split(StatsVm.Str(m, "msg") ?? "");
        if (traceback != null) frames = new List<StackFrame>(traceback);
        var time = StatsVm.Num(m, "time") is var tt && !double.IsNaN(tt) ? DateTimeOffset.FromUnixTimeSeconds((long)tt).LocalDateTime : DateTime.Now;
        PlayerRef? ply = null;
        if (m.TryGetProperty("ply", out var p) && p.ValueKind == JsonValueKind.Object)
            ply = new PlayerRef(StatsVm.Str(p, "name") ?? "?", StatsVm.Str(p, "sid") ?? "", StatsVm.Str(p, "sid64") ?? "", (int)StatsVm.Num0(p, "uid"));
        var err = new LuaError
        {
            Realm = StatsVm.Str(m, "realm") == "client" ? LuaRealm.Client : LuaRealm.Server,
            Message = message,
            Stack = frames,
            Time = time,
            Count = (int)Math.Max(1, StatsVm.Num0(m, "n")),
            AddonTitle = StatsVm.Str(m, "addon"),
            WorkshopId = StatsVm.Str(m, "wsid"),
            Player = ply,
            Source = ErrorSource.Bridge,
        };
        if (m.TryGetProperty("early", out var early) && early.ValueKind == JsonValueKind.True
            && _textErrorsOffline.TryGetValue(EarlyKey(err), out var seen))
        {
            int left = seen - err.Count;
            if (left > 0) _textErrorsOffline[EarlyKey(err)] = left;
            else _textErrorsOffline.Remove(EarlyKey(err));
            if (left >= 0) return;
            err = err with { Count = -left };
        }
        AddError(err);
    }

    private static string EarlyKey(LuaError e) => $"{e.Realm}\0{e.Player?.SteamId}\0{e.Message.Trim()}";

    private void OnTextError(LuaError e)
    {
        // With the addon connected the same errors arrive through the bridge, with more detail.
        if (BridgeConnected && _helloReceived) return;
        var key = EarlyKey(e);
        _textErrorsOffline[key] = _textErrorsOffline.GetValueOrDefault(key) + e.Count;
        AddError(e);
    }

    private void AddError(LuaError e)
    {
        e = ErrorAttribution.Fix(e);
        if (e.Realm == LuaRealm.Client) ClientErrors.Add(e, Settings.MergeSimilarErrors);
        else ServerErrors.Add(e, Settings.MergeSimilarErrors);
        MarkSeen();
        UpdateBadges();
    }

    // ------------------------------------------------------------------------------ statistics

    private void OnProcessSample(ProcessSnapshot s)
    {
        _lastProcess = s;
        double now = StatsVm.Now();
        Stats.Cpu.Add(now, s.CpuPercent);
        Stats.MemoryMB.Add(now, s.PrivateBytes / (1024.0 * 1024.0));
        CpuText = $"{s.CpuPercent:F1}%";
        CpuTooltip = $"CPU time of srcds over the last 5 s, like the \"stats\" command (100% = one core).\nWhole machine: {s.CpuPercent / Environment.ProcessorCount:F1}% of {Environment.ProcessorCount} logical processors.";
        MemText = $"{s.PrivateBytes / (1024 * 1024)} MB";
        Stats.CpuText = $"{s.CpuPercent:F1}%";
        Stats.CpuSub = $"{s.Threads} threads · {s.Handles} handles";
        Stats.MemoryText = $"{s.PrivateBytes / (1024 * 1024)} MB";
        Stats.MemorySub = $"working set {s.WorkingSet / (1024 * 1024)} MB";
        Stats.UptimeText = ServerController.FormatSpan(s.Uptime);
        Stats.UptimeSub = $"since {Controller.StartedAt:HH:mm:ss}";
        if (!BridgeConnected) Stats.RaiseUpdated();
        PublishSnapshot(null);
    }

    private void OnLuaStats(JsonElement m)
    {
        _lastLuaStats = DateTime.Now;
        Stats.HasLuaStats = true;
        if (m.TryGetProperty("idle", out var idleFlag) && idleFlag.ValueKind == JsonValueKind.True)
        {
            OnIdleStats(m);
            return;
        }
        _lastFrames = DateTime.Now;
        ServerIdle = false;
        double now = StatsVm.Now();
        double fps = StatsVm.Num(m, "fps"), ft = StatsVm.Num(m, "ft"), ftmax = StatsVm.Num(m, "ftmax"), ftsd = StatsVm.Num(m, "ftsd");
        double busy = StatsVm.Num(m, "busy"), busymax = StatsVm.Num(m, "busymax"), load = StatsVm.Num(m, "load");
        double tps = StatsVm.Num(m, "tps"), tickrate = StatsVm.Num(m, "tickrate");
        double players = StatsVm.Num0(m, "players"), bots = StatsVm.Num0(m, "bots"), maxpl = StatsVm.Num0(m, "maxplayers");
        double ents = StatsVm.Num0(m, "ents"), edicts = StatsVm.Num(m, "edicts"), lua = StatsVm.Num0(m, "lua") / 1024.0;
        double netin = StatsVm.Num(m, "netin"), netout = StatsVm.Num(m, "netout"), uptime = StatsVm.Num0(m, "uptime");
        if (!double.IsNaN(tickrate)) Stats.TickRate = tickrate;

        Stats.Fps.Add(now, fps);
        Stats.FrameMs.Add(now, ft);
        Stats.FrameMaxMs.Add(now, ftmax);
        if (!double.IsNaN(busy)) Stats.Busy.Add(now, busy);
        if (!double.IsNaN(load)) Stats.Load.Add(now, load);
        Stats.Tps.Add(now, tps);
        Stats.LuaMB.Add(now, lua);
        Stats.Players.Add(now, players);
        Stats.Entities.Add(now, ents);
        if (!double.IsNaN(netin)) Stats.NetIn.Add(now, netin / 1024.0);
        if (!double.IsNaN(netout)) Stats.NetOut.Add(now, netout / 1024.0);

        Stats.FpsText = $"{fps:F1}";
        Stats.FpsSub = $"±{ftsd:F2} ms variance";
        Stats.FrameText = $"{ft:F2} ms";
        Stats.FrameSub = $"max {ftmax:F1} ms · budget {(double.IsNaN(tickrate) ? 0 : 1000 / tickrate):F1} ms";
        Stats.LoadText = double.IsNaN(load) ? "—" : $"{load:F1}%";
        Stats.LoadSub = double.IsNaN(busy) ? "needs the native module" : $"{busy:F2} ms CPU per frame · max {busymax:F1}";
        Stats.TickText = $"{tps:F1}";
        Stats.TickSub = $"of {tickrate:F0} ticks per second";
        Stats.LuaText = $"{lua:F1} MB";
        Stats.LuaSub = "collectgarbage(\"count\")";
        Stats.PlayersText = $"{players:F0} / {maxpl:F0}";
        Stats.PlayersSub = bots > 0 ? $"+ {bots:F0} bots" : "humans";
        Stats.EntitiesText = $"{ents:F0}";
        Stats.EntitiesSub = double.IsNaN(edicts) ? "" : $"{edicts:F0} / 8192 edicts";
        Stats.NetText = double.IsNaN(netin) ? "—" : $"{netin / 1024:F1} / {netout / 1024:F1}";
        Stats.NetSub = "KB/s in / out";

        SampleClock(m);
        if (m.TryGetProperty("spikes", out var spikes) && spikes.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in spikes.EnumerateArray())
            {
                SpikeRow row;
                try { row = SpikeRow.From(s, _clockOffset); }
                catch (Exception ex)
                {
                    Log.Write("spike: " + ex.Message);
                    continue;
                }
                Stats.Spikes.Insert(0, row);
            }
            while (Stats.Spikes.Count > 100) Stats.Spikes.RemoveAt(Stats.Spikes.Count - 1);
        }
        if (_vprofPending.Count > 0) MatchPending(_bridgeAt);

        MapText = StatsVm.Str(m, "map") ?? MapText;
        SvText = $"{fps:F1} fps ±{ftsd:F1} ms";
        SvTooltip = $"Server frame rate and frame time deviation (like net_graph's \"sv\").\nAverage frame {ft:F2} ms, longest {ftmax:F1} ms.";
        TickText = $"{tps:F0}/{tickrate:F0}";
        LoadText = double.IsNaN(load) ? "—" : $"{load:F0}%";
        EntsText = double.IsNaN(edicts) ? $"{ents:F0}" : $"{ents:F0} ({edicts:F0} ed.)";
        LuaText = $"{lua:F0} MB";
        InText = double.IsNaN(netin) ? "—" : FormatRate(netin);
        OutText = double.IsNaN(netout) ? "—" : FormatRate(netout);
        if (string.IsNullOrEmpty(PlayersText) || PlayersText == "—")
            PlayersText = $"{players:F0}/{maxpl:F0}";
        Stats.RaiseUpdated();
        PublishSnapshot(m);
    }

    /// <summary>The summary of a hibernating server (no frames): only the numbers that still apply.</summary>
    private void OnIdleStats(JsonElement m)
    {
        double players = StatsVm.Num0(m, "players"), bots = StatsVm.Num0(m, "bots"), maxpl = StatsVm.Num0(m, "maxplayers");
        double ents = StatsVm.Num0(m, "ents"), edicts = StatsVm.Num(m, "edicts"), lua = StatsVm.Num0(m, "lua") / 1024.0;
        var tr = StatsVm.Num(m, "tickrate");
        if (!double.IsNaN(tr)) Stats.TickRate = tr;
        MapText = StatsVm.Str(m, "map") ?? MapText;
        PlayersText = $"{players:F0}/{maxpl:F0}" + (bots > 0 ? $" +{bots:F0}" : "");
        EntsText = double.IsNaN(edicts) ? $"{ents:F0}" : $"{ents:F0} ({edicts:F0} ed.)";
        LuaText = $"{lua:F0} MB";
        TickText = "0/" + (double.IsNaN(tr) ? "?" : tr.ToString("F0"));
        LoadText = "0%";
        InText = OutText = FormatRate(0);
        SvText = "hibernating";
        SvTooltip = "The server is empty and hibernating (sv_hibernate_think 0): it runs no frames until a player joins.";
        Stats.PlayersText = $"{players:F0} / {maxpl:F0}";
        Stats.EntitiesText = $"{ents:F0}";
        Stats.EntitiesSub = double.IsNaN(edicts) ? "" : $"{edicts:F0} / 8192 edicts";
        Stats.LuaText = $"{lua:F1} MB";
        Stats.FpsText = "0";
        Stats.FpsSub = "hibernating: no frames";
        double now = StatsVm.Now();
        Stats.Players.Add(now, players);
        Stats.Entities.Add(now, ents);
        Stats.LuaMB.Add(now, lua);
        Stats.RaiseUpdated();
        PublishSnapshot(m);
    }

    private static string FormatRate(double bytesPerSec) =>
        bytesPerSec >= 1024 * 1024 ? $"{bytesPerSec / (1024 * 1024):F1} MB/s" : $"{bytesPerSec / 1024:F1} KB/s";

    private void PublishSnapshot(JsonElement? lua)
    {
        var s = _lastProcess;
        var snap = new ServerSnapshot
        {
            Time = DateTime.Now,
            CpuPercent = s?.CpuPercent ?? 0,
            PrivateBytes = s?.PrivateBytes ?? 0,
            WorkingSet = s?.WorkingSet ?? 0,
            Threads = s?.Threads ?? 0,
            Uptime = s?.Uptime ?? TimeSpan.Zero,
            ServerFps = Stats.Fps.LastValue,
            FrameMs = Stats.FrameMs.LastValue,
            FrameMaxMs = Stats.FrameMaxMs.LastValue,
            FrameVarMs = lua is { } l1 ? StatsVm.Num(l1, "ftsd") : Latest?.FrameVarMs ?? double.NaN,
            TickRate = Stats.TickRate,
            TicksPerSecond = Stats.Tps.LastValue,
            GameThreadLoad = Stats.Load.LastValue,
            NetInKBps = Stats.NetIn.LastValue,
            NetOutKBps = Stats.NetOut.LastValue,
            Players = (int)(double.IsNaN(Stats.Players.LastValue) ? -1 : Stats.Players.LastValue),
            Bots = Players.Bots,
            MaxPlayers = Players.MaxPlayers,
            Entities = (int)(double.IsNaN(Stats.Entities.LastValue) ? -1 : Stats.Entities.LastValue),
            LuaMemoryMB = Stats.LuaMB.LastValue,
            Map = MapText,
            Hostname = Hostname,
        };
        Latest = snap;
        SnapshotUpdated?.Invoke(snap);
    }

    // ------------------------------------------------------------------------------ detailed capture of lag spikes

    // Reports of the engine profiler not matched to a spike yet (they come through the console, the spikes
    // with the next summary of the addon).
    private readonly List<(DateTime At, VprofReport Report)> _vprofPending = new();

    /// <summary>
    /// Detailed capture: the addon times every hook, timer and net message per frame and names the slowest
    /// of each long frame; the engine profiler (vprof) reports what the engine did in it.
    /// </summary>
    public void SetCapture(bool on)
    {
        if (on && !BridgeConnected)
        {
            Notify("The companion addon is not connected.", NotifyKind.Warning);
            Stats.Capture = false;
            return;
        }
        Stats.Capture = on;
        ApplyCapture();
        if (on) Controller.Pipeline.CaptureVprof = _vprofPause == null;
        else
        {
            _vprofPending.Clear();
            // The engine stops a moment later, when it reads "vprof_dump_spikes 0": its reports until then
            // still stay out of the console (and are dropped).
            _vprofOff?.Stop();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_vprofOff != t) return;
                _vprofOff = null;
                if (!Stats.Capture) Controller.Pipeline.CaptureVprof = false;
            };
            _vprofOff = t;
            t.Start();
        }
    }

    private DispatcherTimer? _vprofOff, _vprofPause;

    /// <summary>
    /// A vprof command of the user's own while the capture is on (vprof_generate_report …): its report covers one
    /// frame (the engine starts its profile anew every frame for the spikes) and would be taken for a spike's, its
    /// file deleted. The console gets vprof's reports for a few seconds instead.
    /// </summary>
    private void PauseVprofCapture()
    {
        Controller.Pipeline.CaptureVprof = false;
        _vprofPause?.Stop();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_vprofPause != t) return;
            _vprofPause = null;
            Controller.Pipeline.CaptureVprof = Stats.Capture;
        };
        _vprofPause = t;
        t.Start();
    }

    /// <summary>Tells the addon and the engine (again after a map change or a restart).</summary>
    private void ApplyCapture()
    {
        if (BridgeConnected) Request("capture", new { on = Stats.Capture });
        // The engine's part goes through the console: switched off also while the addon is away.
        if (!IsRunning || Stats.Capture && !BridgeConnected) return;
        if (Stats.Capture)
        {
            // vprof_dump_spikes takes a frame rate: the frames slower than it are reported, the same ones as the addon's spikes.
            double tick = double.IsNaN(Stats.TickRate) || Stats.TickRate <= 0 ? 33 : Stats.TickRate;
            double fps = 1000 / Math.Max(3000 / tick, 50);
            Controller.Pipeline.VprofMinFrameMs = 1000 / fps;
            Controller.TypeInternal("vprof_on");
            Controller.TypeInternal("vprof_dump_spikes " + fps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            Controller.TypeInternal("vprof_dump_spikes 0");
            Controller.TypeInternal("vprof_off");
        }
    }

    private void OnVprof(VprofCaptured v)
    {
        // The engine writes every such report to garrysmod/vprof/vprofN.txt as well: those are removed again.
        if (v.SavedTo is { } rel) DeleteVprofFile(rel);
        if (v.Report == null || !Stats.Capture) return;
        // The frame's spike comes with the addon's next summary: matched then.
        _vprofPending.Add((v.Time, v.Report));
        _vprofPending.RemoveAll(p => (DateTime.Now - p.At).TotalSeconds > 10);
    }

    /// <summary>
    /// After a summary's spikes: the waiting reports from before it go to their rows (VprofMatch). Their own
    /// spike is in by then (the addon noticed it before the report came), so a report is never handed to an
    /// earlier spike only because its own had not arrived yet.
    /// </summary>
    private void MatchPending(DateTime summaryAt)
    {
        double tickMs = double.IsNaN(Stats.TickRate) || Stats.TickRate <= 0 ? 30 : 1000 / Stats.TickRate;
        for (int i = 0; i < _vprofPending.Count; i++)
        {
            var (at, report) = _vprofPending[i];
            if (at > summaryAt) continue;
            var rows = Stats.Spikes.Where(r => !r.HasEngine).ToList();
            int k = VprofMatch.Pick(rows.Select(r => (r.Time, r.Ms, r.IsPrecise)).ToList(), at, report.FrameMs, tickMs, _vprofLatency);
            if (k < 0) continue;
            var row = rows[k];
            row.AttachEngine(report);
            // How late the console usually is, from the reports that found their frame.
            if (row.IsPrecise) _vprofLatency = Math.Clamp(0.8 * _vprofLatency + 0.2 * (at - row.Time).TotalSeconds, 0, 0.15);
            _vprofPending.RemoveAt(i--);
        }
    }

    private double _vprofLatency = 0.02;

    // The addon's SysTime on this machine's clock: received minus sent of each summary is the offset plus the
    // delivery; the least of the last half minute has the shortest delivery in it.
    private readonly Queue<double> _clockSamples = new();
    private double _clockOffset = double.NaN;

    private void SampleClock(JsonElement m)
    {
        var st = StatsVm.Num(m, "st");
        if (double.IsNaN(st)) return;
        _clockSamples.Enqueue(new DateTimeOffset(_bridgeAt).ToUnixTimeMilliseconds() / 1000.0 - st);
        while (_clockSamples.Count > 30) _clockSamples.Dequeue();
        _clockOffset = _clockSamples.Min();
    }

    private void DeleteVprofFile(string relative)
    {
        var game = Profile.GameDirectory;
        _ = Task.Run(async () =>
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(game, relative));
                var dir = Path.GetFullPath(Path.Combine(game, "vprof")) + Path.DirectorySeparatorChar;
                // Only a report the engine just wrote into garrysmod/vprof.
                if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("vprof", StringComparison.OrdinalIgnoreCase)) return;
                for (int i = 0; i < 10 && !File.Exists(full); i++) await Task.Delay(200);
                if (File.Exists(full) && (DateTime.Now - File.GetLastWriteTime(full)).TotalSeconds < 60) File.Delete(full);
            }
            catch (Exception ex)
            {
                Log.Write("vprof file: " + ex.Message);
            }
        });
    }

    public void SetProfiling(bool on)
    {
        if (!BridgeConnected)
        {
            Notify("The companion addon is not connected.", NotifyKind.Warning);
            return;
        }
        Request("prof", new { on });
    }

    /// <summary>Stops the server (if it runs) and frees everything. The view model is not used afterwards.</summary>
    public async Task ShutdownAsync(string reason = "BetterConsole was closed")
    {
        if (Controller.State is ServerState.Running or ServerState.Starting) await Controller.StopAsync(reason);
        _pump.Stop();
        _clock.Stop();
        Pump();
        await Controller.DisposeAsync();
    }
}
