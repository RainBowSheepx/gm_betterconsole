using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using BetterConsole.App.Services;
using BetterConsole.Core.Console;
using BetterConsole.Core.Errors;
using BetterConsole.Core.Server;
using BetterConsole.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterConsole.App.ViewModels;

/// <summary>A tab of the main window. Content is created the first time the tab is shown.</summary>
public sealed partial class TabVm : ObservableObject
{
    private FrameworkElement? _content;

    public TabVm(string id, string header, string icon, int order, Func<FrameworkElement> factory)
    {
        Id = id;
        this.header = header;
        Icon = icon;
        Order = order;
        Factory = factory;
    }

    public string Id { get; }
    public string Icon { get; }
    public int Order { get; set; }
    public Func<FrameworkElement> Factory { get; }
    [ObservableProperty] private string header;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBadge))] private string? badge;
    /// <summary>"danger", "accent" or "muted".</summary>
    [ObservableProperty] private string badgeKind = "muted";
    [ObservableProperty] private bool isSelected;
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public bool IsCreated => _content != null;
    public FrameworkElement Content => _content ??= Factory();
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConcurrentQueue<(string Type, JsonElement Msg)> _bridgeQueue = new();
    private readonly ConcurrentQueue<ConsoleEvent> _appLines = new();
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private readonly DispatcherTimer _pump;
    private readonly DispatcherTimer _clock;
    private readonly List<ConsoleEvent> _batch = new(4096);
    private long _appLineId = -1;
    private DateTime _lastLuaStats = DateTime.MinValue;
    private bool _helloReceived;
    private ProcessSnapshot? _lastProcess;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        History = new CommandHistory(Path.Combine(AppSettings.DataDirectory, "history.txt"));
        Controller = new ServerController(settings.Server)
        {
            BeforeStart = PrepareServerAsync,
        };
        Controller.Pipeline.HideErrors = settings.HideErrorsInConsole;
        Controller.StateChanged += (o, n, code) => _uiActions.Enqueue(() => OnStateChanged(o, n, code));
        Controller.Notice += (text, isError) => WriteAppLine(text, isError);
        Controller.Sampled += s => _uiActions.Enqueue(() => OnProcessSample(s));
        Controller.Bridge.ConnectionChanged += c => _uiActions.Enqueue(() => OnBridgeConnection(c));
        Controller.Bridge.MessageReceived += (t, m) => _bridgeQueue.Enqueue((t, m));

        ServerErrors.MaxItems = settings.MaxErrorsPerList;
        ClientErrors.MaxItemsPerPlayer = Math.Max(50, settings.MaxErrorsPerList / 2);

        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _pump.Tick += (_, _) => Pump();
        _pump.Start();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockTick();
        _clock.Start();
    }

    public AppSettings Settings { get; }
    public ServerController Controller { get; }
    public CommandHistory History { get; }
    public CommandCatalog Catalog { get; } = new();
    public ServerErrorsVm ServerErrors { get; } = new();
    public ClientErrorsVm ClientErrors { get; } = new();
    public StatsVm Stats { get; } = new();
    public PlayersVm Players { get; } = new();
    public ObservableCollection<TabVm> Tabs { get; } = new();
    public ObservableCollection<StatusItemVm> ExtraStatus { get; } = new();
    public ObservableCollection<ToastVm> Toasts { get; } = new();
    public Dictionary<string, LuaTabVm> LuaTabs { get; } = new();

    /// <summary>Console events in order, delivered on the UI thread about 30 times a second.</summary>
    public event Action<IReadOnlyList<ConsoleEvent>>? ConsoleEvents;
    /// <summary>A bridge message after the built-in handling (for plugins), on the UI thread.</summary>
    public event Action<string, JsonElement>? BridgeMessage;
    public event Action<bool>? BridgeConnectionChanged;
    public event Action<ServerState, ServerState, int?>? ServerStateChanged;
    public event Action<ServerSnapshot>? SnapshotUpdated;
    /// <summary>A server addon defined a tab (the window builds a view for it).</summary>
    public event Action<LuaTabVm>? LuaTabAdded;
    public event Action<string>? LuaTabRemoved;
    /// <summary>The settings window saved new values.</summary>
    public event Action? SettingsChanged;

    public void RaiseSettingsChanged() => SettingsChanged?.Invoke();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRunning), nameof(CanStart), nameof(StateText))] private ServerState state;
    [ObservableProperty] private TabVm? selectedTab;
    [ObservableProperty] private string windowTitle = "BetterConsole";
    [ObservableProperty] private string? hostname;
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

    public bool IsRunning => State is ServerState.Running or ServerState.Starting or ServerState.Stopping;
    public bool CanStart => State is ServerState.Stopped or ServerState.Crashed;
    public string StateText => State switch
    {
        ServerState.Running => "Running",
        ServerState.Starting => "Starting",
        ServerState.Stopping => "Stopping",
        ServerState.Crashed => "Crashed",
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
        switch (newValue?.Id)
        {
            case "server-errors": ServerErrors.UnseenCount = 0; break;
            case "client-errors": ClientErrors.UnseenCount = 0; break;
        }
        if (oldValue?.Id == "players" || newValue?.Id == "players") SendPlayerSubscription();
        UpdateBadges();
    }

    private void SendPlayerSubscription() =>
        Controller.Bridge.Send("sub", new { players = SelectedTab?.Id == "players" });

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

    [RelayCommand]
    public async Task StartAsync()
    {
        if (!CanStart) return;
        Settings.Save();
        await Controller.StartAsync();
    }

    [RelayCommand]
    public async Task StopAsync() => await Controller.StopAsync();

    [RelayCommand]
    public async Task RestartAsync()
    {
        Settings.Save();
        await Controller.RestartAsync();
    }

    [RelayCommand]
    public void Kill() => Controller.Kill();

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

    /// <summary>Runs a console command typed by the user. Returns false when it could not be sent.</summary>
    public bool SendCommand(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return true;
        History.Add(text);
        if (!Controller.SendCommand(text, out var problem))
        {
            WriteAppLine(problem ?? "The command could not be sent.", true);
            return false;
        }
        bool ascii = text.All(c => c >= 0x20 && c <= 0x7E);
        if (!ascii || text.Length >= 250)
        {
            // Sent through the companion addon: srcds does not echo it, so we do.
            _appLines.Enqueue(new LineAdded(Interlocked.Decrement(ref _appLineId),
                new ConsoleLine { Text = text, Time = DateTime.Now, Kind = ConsoleLineKind.Command }));
        }
        return true;
    }

    public void WriteAppLine(string text, bool isError, uint argb = 0)
    {
        var line = new ConsoleLine
        {
            Text = text,
            Time = DateTime.Now,
            Kind = isError ? ConsoleLineKind.AppError : ConsoleLineKind.App,
            Spans = argb != 0 ? [new ColorSpan(0, text.Length, argb)] : Array.Empty<ColorSpan>(),
        };
        _appLines.Enqueue(new LineAdded(Interlocked.Decrement(ref _appLineId), line));
    }

    public void Notify(string text, NotifyKind kind = NotifyKind.Info)
    {
        void Show()
        {
            var t = new ToastVm(text, kind);
            Toasts.Add(t);
            while (Toasts.Count > 4) Toasts.RemoveAt(0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == NotifyKind.Error ? 8 : 4.5) };
            timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
            timer.Start();
        }
        if (Application.Current.Dispatcher.CheckAccess()) Show();
        else _uiActions.Enqueue(Show);
    }

    private void OnStateChanged(ServerState old, ServerState now, int? exitCode)
    {
        State = now;
        if (now is ServerState.Stopped or ServerState.Crashed)
        {
            BridgeConnected = false;
            ServerIdle = false;
            Players.Clear();
            UpdateBadges();
            CpuText = InText = OutText = SvText = TickText = LoadText = EntsText = LuaText = MemText = PlayersText = "—";
        }
        if (now == ServerState.Crashed) Notify($"The server crashed (exit code {ServerController.FormatExitCode(exitCode ?? 0)}).", NotifyKind.Error);
        if (now == ServerState.Starting) Stats.Clear();
        ServerStateChanged?.Invoke(old, now, exitCode);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = string.IsNullOrWhiteSpace(Hostname) ? null : Hostname;
        WindowTitle = name != null ? $"{name} — BetterConsole" : "BetterConsole";
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
            bool idle = BridgeConnected && _helloReceived && (DateTime.Now - _lastLuaStats).TotalSeconds > 3.5;
            if (idle != ServerIdle)
            {
                ServerIdle = idle;
                if (idle)
                {
                    SvText = "idle";
                    SvTooltip = "No server frames for a few seconds: the server is hibernating (no players) or frozen.";
                }
            }
        }
        else UptimeText = "";
    }

    // ------------------------------------------------------------------------------ bridge

    private void OnBridgeConnection(bool connected)
    {
        BridgeConnected = connected;
        _helloReceived = false;
        if (!connected)
        {
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
                // Addons are still loading right after the connection: ask for the command list a bit later.
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (_, _) => { t.Stop(); if (BridgeConnected) Controller.Bridge.Send("cmds"); };
                t.Start();
                // Lua tabs are sent again by the addon after a reconnect.
                foreach (var id in LuaTabs.Keys.ToList()) LuaTabRemoved?.Invoke(id);
                LuaTabs.Clear();
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
                if (LuaTabs.TryGetValue(StatsVm.Str(m, "tab") ?? "", out var tab) && StatsVm.Str(m, "id") is { } wid)
                    tab.Define(wid, StatsVm.Str(m, "kind") ?? "text", m.TryGetProperty("opts", out var o) ? o : default);
                break;
            case "wd":
                if (LuaTabs.TryGetValue(StatsVm.Str(m, "tab") ?? "", out var tab2) && StatsVm.Str(m, "id") is { } wid2)
                    tab2.Data(wid2, StatsVm.Str(m, "op") ?? "set", m.TryGetProperty("data", out var d) ? d : default);
                break;
            case "st":
                OnStatusItem(m);
                break;
            case "st_rm":
                if (ExtraStatus.FirstOrDefault(s => s.Id == "lua:" + StatsVm.Str(m, "id")) is { } sr) ExtraStatus.Remove(sr);
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

    private void OnLuaTab(JsonElement m)
    {
        var id = StatsVm.Str(m, "id");
        if (id == null) return;
        if (!LuaTabs.TryGetValue(id, out var tab))
        {
            tab = new LuaTabVm(id) { ActionSink = (t, w, a) => Controller.Bridge.Send("action", new { tab = t, widget = w, id = a }) };
            LuaTabs[id] = tab;
            tab.Title = StatsVm.Str(m, "title") ?? id;
            var order = StatsVm.Num(m, "order");
            tab.Order = double.IsNaN(order) ? 100 : (int)order;
            LuaTabAdded?.Invoke(tab);
        }
        else
        {
            tab.Title = StatsVm.Str(m, "title") ?? tab.Title;
            if (FindTab("lua:" + id) is { } tv) tv.Header = tab.Title;
        }
    }

    private void OnStatusItem(JsonElement m)
    {
        var id = "lua:" + StatsVm.Str(m, "id");
        var item = ExtraStatus.FirstOrDefault(s => s.Id == id);
        if (item == null)
        {
            item = new StatusItemVm(id, 500);
            ExtraStatus.Add(item);
        }
        item.Text = StatsVm.Str(m, "text") ?? "";
        item.Tooltip = StatsVm.Str(m, "tip");
        item.Argb = Themes.ThemeManager.TryParse(StatsVm.Str(m, "color"), out var c) ? 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B : 0;
    }

    private void OnBridgeError(JsonElement m)
    {
        var frames = new List<StackFrame>();
        if (m.TryGetProperty("stack", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var f in st.EnumerateArray())
                frames.Add(new StackFrame(StatsVm.Str(f, "fn") ?? "unknown", StatsVm.Str(f, "src") ?? "?", (int)StatsVm.Num0(f, "line")));
        var time = StatsVm.Num(m, "time") is var tt && !double.IsNaN(tt) ? DateTimeOffset.FromUnixTimeSeconds((long)tt).LocalDateTime : DateTime.Now;
        PlayerRef? ply = null;
        if (m.TryGetProperty("ply", out var p) && p.ValueKind == JsonValueKind.Object)
            ply = new PlayerRef(StatsVm.Str(p, "name") ?? "?", StatsVm.Str(p, "sid") ?? "", StatsVm.Str(p, "sid64") ?? "", (int)StatsVm.Num0(p, "uid"));
        var err = new LuaError
        {
            Realm = StatsVm.Str(m, "realm") == "client" ? LuaRealm.Client : LuaRealm.Server,
            Message = StatsVm.Str(m, "msg") ?? "",
            Stack = frames,
            Time = time,
            Count = (int)Math.Max(1, StatsVm.Num0(m, "n")),
            AddonTitle = StatsVm.Str(m, "addon"),
            WorkshopId = StatsVm.Str(m, "wsid"),
            Player = ply,
            Source = ErrorSource.Bridge,
        };
        AddError(err);
    }

    private void OnTextError(LuaError e)
    {
        // With the addon connected the same errors arrive through the bridge, with more detail.
        if (BridgeConnected && _helloReceived) return;
        AddError(e);
    }

    private void AddError(LuaError e)
    {
        if (e.Realm == LuaRealm.Client) ClientErrors.Add(e, Settings.MergeSimilarErrors);
        else ServerErrors.Add(e, Settings.MergeSimilarErrors);
        if (SelectedTab?.Id == "server-errors") ServerErrors.UnseenCount = 0;
        if (SelectedTab?.Id == "client-errors") ClientErrors.UnseenCount = 0;
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
        ServerIdle = false;
        Stats.HasLuaStats = true;
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

        if (m.TryGetProperty("spikes", out var spikes) && spikes.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in spikes.EnumerateArray())
            {
                var t = StatsVm.Num(s, "time");
                var b = StatsVm.Num(s, "busy");
                Stats.Spikes.Insert(0, new SpikeRow(double.IsNaN(t) ? DateTime.Now : DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime,
                    StatsVm.Num0(s, "ms"), double.IsNaN(b) ? null : b));
            }
            while (Stats.Spikes.Count > 100) Stats.Spikes.RemoveAt(Stats.Spikes.Count - 1);
        }

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

    public void SetProfiling(bool on)
    {
        if (!BridgeConnected)
        {
            Notify("The companion addon is not connected.", NotifyKind.Warning);
            return;
        }
        Controller.Bridge.Send("prof", new { on });
    }

    public async Task ShutdownAsync()
    {
        _pump.Stop();
        _clock.Stop();
        if (Controller.State is ServerState.Running or ServerState.Starting) await Controller.StopAsync();
        await Controller.DisposeAsync();
        Settings.Save();
    }
}
