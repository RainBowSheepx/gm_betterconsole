using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using BetterConsole.App.Services;
using BetterConsole.App.ViewModels;
using BetterConsole.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.Plugins;

public sealed partial class PluginInfo : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Path { get; init; } = "";
    [ObservableProperty] private bool enabled = true;
    [ObservableProperty] private string status = "";
    internal Type? Type { get; set; }
}

/// <summary>A plugin running for one server.</summary>
public sealed record PluginInstance(PluginInfo Info, IConsolePlugin Plugin, IDisposable Context);

/// <summary>
/// Loads plugins from <c>plugins\*\*.dll</c> (one folder per plugin) and <c>plugins\*.dll</c>. Each
/// plugin gets its own load context, so its dependencies do not clash with the app's; the SDK
/// assembly is shared so the interfaces match. Every server gets its own instance of each plugin
/// (with several servers, a plugin's tab appears in each of them).
/// </summary>
public sealed class PluginManager
{
    public List<PluginInfo> Plugins { get; } = new();

    public void LoadAll(AppSettings settings)
    {
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(root)) return;
        var files = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(root).SelectMany(d => Directory.EnumerateFiles(d, "*.dll", SearchOption.TopDirectoryOnly)));
        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("BetterConsole.Sdk", StringComparison.OrdinalIgnoreCase)) continue;
            // In a plugin folder only the DLL named like the folder (or the only DLL) is the entry.
            var dir = System.IO.Path.GetDirectoryName(file)!;
            if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                var folder = System.IO.Path.GetFileName(dir);
                bool single = Directory.GetFiles(dir, "*.dll").Length == 1;
                if (!single && !string.Equals(name, folder, StringComparison.OrdinalIgnoreCase)) continue;
            }
            Load(file, settings);
        }
    }

    private void Load(string file, AppSettings settings)
    {
        try
        {
            var ctx = new PluginLoadContext(file);
            var asm = ctx.LoadFromAssemblyPath(file);
            var types = asm.GetTypes().Where(t => typeof(IConsolePlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IConsolePlugin probe) continue;
                var info = new PluginInfo { Id = probe.Id, Name = probe.Name, Description = probe.Description, Path = file, Type = type };
                Plugins.Add(info);
                if (settings.DisabledPlugins.Contains(probe.Id))
                {
                    info.Enabled = false;
                    info.Status = "Disabled";
                    info.Type = null;
                }
                else info.Status = $"Loaded from {System.IO.Path.GetFileName(file)}";
            }
        }
        catch (Exception ex)
        {
            Plugins.Add(new PluginInfo { Id = file, Name = System.IO.Path.GetFileName(file), Path = file, Status = "Could not load: " + ex.Message, Enabled = false });
            Log.Write($"plugin load {file}: {ex}");
        }
    }

    /// <summary>Starts every enabled plugin for a server.</summary>
    public void Attach(ServerViewModel vm)
    {
        foreach (var info in Plugins)
        {
            if (info.Type == null) continue;
            try
            {
                var plugin = (IConsolePlugin)Activator.CreateInstance(info.Type)!;
                var context = new PluginContext(plugin.Id, vm);
                plugin.Initialize(context);
                vm.PluginInstances.Add(new PluginInstance(info, plugin, context));
                Log.Write($"plugin {info.Id} started for server {vm.Profile.Id}");
            }
            catch (Exception ex)
            {
                info.Status = "Failed: " + ex.Message;
                vm.WriteAppLine($"Plugin {info.Name} failed to start: {ex.Message}", true);
                Log.Write($"plugin {info.Id} init failed: {ex}");
            }
        }
    }

    /// <summary>Stops the plugins of a server (it is closed or removed).</summary>
    public static void Detach(ServerViewModel vm)
    {
        foreach (var p in vm.PluginInstances)
        {
            try { p.Plugin.Shutdown(); }
            catch (Exception ex) { Log.Write($"plugin {p.Info.Id} shutdown: {ex}"); }
            p.Context.Dispose();
        }
        vm.PluginInstances.Clear();
    }

    private sealed class PluginLoadContext(string mainAssembly) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            // Shared with the app: the SDK and everything the app already has loaded.
            if (name.Name is "BetterConsole.Sdk" || Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}

/// <summary>What a plugin sees of the app (all calls on the UI thread): one server and its window.</summary>
internal sealed class PluginContext : IPluginContext, IServer, IConsoleOutput, ILuaBridge, IUiHost, IStatsHost, IDisposable
{
    private readonly string _id;
    private readonly ServerViewModel _vm;
    private readonly Action<Themes.ThemePalette> _themeChanged;
    private bool _reportedFailure;

    // After Shutdown the plugin gets no more events (the server is removed or BetterConsole closes).
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        Themes.ThemeManager.Changed -= _themeChanged;
    }

    public PluginContext(string id, ServerViewModel vm)
    {
        _id = id;
        _vm = vm;
        vm.ServerStateChanged += (o, n, c) => Raise(StateChanged, new ServerStateChangedEventArgs(o, n, c));
        vm.SnapshotUpdated += s => Raise(SnapshotUpdated, s);
        vm.ConsoleEvents += events =>
        {
            if (LineReceived == null) return;
            foreach (var e in events)
                if (e is BetterConsole.Core.Console.LineAdded la) Raise(LineReceived, new ConsoleLineEventArgs(la.Line));
        };
        vm.BridgeConnectionChanged += c => Raise(ConnectionChanged, c);
        vm.BridgeMessage += (type, msg) =>
        {
            if (MessageReceived == null) return;
            if (type == "custom")
            {
                var t = msg.TryGetProperty("type", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString()! : "custom";
                var data = msg.TryGetProperty("data", out var d) ? d : default;
                Raise(MessageReceived, new BridgeMessage(t, data));
            }
            else Raise(MessageReceived, new BridgeMessage(type, msg));
        };
        vm.PlayerMenuOpening += (players, menu) =>
        {
            if (PlayerMenuOpening != null) Raise(PlayerMenuOpening, new PlayerMenuEventArgs(players.Select(p => p.ToInfo()).ToList(), menu));
        };
        _themeChanged = t => Raise(ThemeChanged, t.Name);
        Themes.ThemeManager.Changed += _themeChanged;
    }

    /// <summary>
    /// Calls every handler separately: a plugin that throws neither breaks the app nor the other
    /// handlers; the first failure of a plugin is shown as a notification.
    /// </summary>
    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler == null || _disposed) return;
        foreach (EventHandler<T> h in handler.GetInvocationList())
        {
            try
            {
                h(this, args);
            }
            catch (Exception ex)
            {
                Services.Log.Write($"[{_id}] event handler failed: {ex}");
                if (!_reportedFailure)
                {
                    _reportedFailure = true;
                    _vm.Notify($"Plugin {_id} failed in an event handler: {ex.Message}", NotifyKind.Error);
                }
            }
        }
    }

    public IServer Server => this;
    public IConsoleOutput Console => this;
    public ILuaBridge Bridge => this;
    public IUiHost Ui => this;

    public string DataDirectory
    {
        get
        {
            var dir = System.IO.Path.Combine(AppSettings.DataDirectory, "plugins-data", string.Join("_", _id.Split(System.IO.Path.GetInvalidFileNameChars())));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Log(string message) => Services.Log.Write($"[{_id}] {message}");

    // IServer
    public ServerState State => _vm.State;
    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;
    public int? ProcessId => _vm.Controller.ProcessId;
    public string GameDirectory => _vm.Profile.GameDirectory;
    public ServerSnapshot? Latest => _vm.Latest;
    public event EventHandler<ServerSnapshot>? SnapshotUpdated;
    public void SendCommand(string command) => _vm.SendCommand(command, remember: false);
    public Task StartAsync() => _vm.StartAsync($"Plugin {_id}");
    public Task StopAsync() => _vm.StopAsync($"Plugin {_id}");
    public Task RestartAsync() => _vm.RestartAsync($"Plugin {_id}");

    // IConsoleOutput
    public event EventHandler<ConsoleLineEventArgs>? LineReceived;
    public void WriteLine(string text, uint argb = 0) => _vm.WriteAppLine(text, false, argb);

    // ILuaBridge
    public bool IsConnected => _vm.BridgeConnected;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<BridgeMessage>? MessageReceived;
    public void Send(string type, object? data = null) => _vm.Request("custom", new { type, data });

    // IUiHost
    /// <summary>Segoe Fluent Icons "Puzzle".</summary>
    private const string PluginTabIcon = "";

    public void AddTab(string id, string header, Func<FrameworkElement> content, int order = 1000) =>
        AddTab(id, header, PluginTabIcon, content, order);

    public void AddTab(string id, string header, string icon, Func<FrameworkElement> content, int order = 1000) =>
        _vm.AddTab(new TabVm("plugin:" + id, header, string.IsNullOrWhiteSpace(icon) ? PluginTabIcon : icon, order, content));

    public void RemoveTab(string id) => _vm.RemoveTab("plugin:" + id);

    /// <summary>Each plugin has its own ids: two plugins that both call something "map" do not replace each other's.</summary>
    private string Key(string id) => $"plugin:{_id}:{id}";

    public IStatusItem AddStatusItem(string id, string text, string? tooltip = null, int order = 1000)
    {
        RemoveStatusItem(id);
        var item = new StatusItemVm(Key(id), order) { ShortId = id, Text = text, Tooltip = tooltip, UserHidden = _vm.Settings.StatusHidden.Contains(Key(id)) };
        _vm.InsertStatusItem(item);
        return item;
    }

    public void RemoveStatusItem(string id)
    {
        if (_vm.ExtraStatus.FirstOrDefault(s => s.Id == Key(id)) is { } item) _vm.ExtraStatus.Remove(item);
    }

    // The Statistics tab
    public IStatsHost Stats => this;

    public IStatCard AddCard(string id, string title, int order = 1000)
    {
        var card = new StatWidgetVm { Id = Key(id), Kind = "stat", Title = title, Order = order };
        _vm.StatsExtras.Add(card);
        return card;
    }

    public IStatChart AddChart(string id, string title, IReadOnlyList<string> series, string? unit = null, bool wide = false, int order = 1000)
    {
        var chart = new ChartWidgetVm { Id = Key(id), Kind = "chart", Title = title, Unit = unit, Order = order, Span = wide ? 12 : 6, SpanGiven = true };
        chart.SetSeries(series);
        _vm.StatsExtras.Add(chart);
        return chart;
    }

    public void AddSection(string id, string? title, Func<FrameworkElement> content, int order = 1000) =>
        _vm.StatsExtras.Add(new SectionWidgetVm(content) { Id = Key(id), Kind = "section", Title = title, Order = order });

    void IStatsHost.Remove(string id) => _vm.StatsExtras.Remove(Key(id));

    public void SetBuiltInVisible(string id, bool visible) => _vm.SetStatHidden(_id, id, !visible);

    // The player menu
    public void AddPlayerAction(string id, string text, Action<IReadOnlyList<PlayerInfo>> run, string? icon = null, int order = 300, Func<PlayerInfo, bool>? appliesTo = null)
    {
        RemovePlayerAction(id);
        _vm.PlayerActions.Add(new PlayerActionDef
        {
            Id = Key(id),
            Text = text,
            Icon = string.IsNullOrWhiteSpace(icon) ? "" : LuaTabVm.ParseIcon(icon),
            Order = order,
            PluginRun = players =>
            {
                try { run(players); }
                catch (Exception ex)
                {
                    Services.Log.Write($"[{_id}] player action {id}: {ex}");
                    _vm.Notify($"Plugin {_id}: \"{text}\" failed: {ex.Message}", NotifyKind.Error);
                }
            },
            PluginFilter = appliesTo,
        });
    }

    public void RemovePlayerAction(string id) => _vm.PlayerActions.RemoveAll(a => a.Id == Key(id));

    public event EventHandler<PlayerMenuEventArgs>? PlayerMenuOpening;

    public void Notify(string message, NotifyKind kind = NotifyKind.Info) => _vm.Notify(message, kind);

    public string ThemeName => Themes.ThemeManager.Current.Name;
    public event EventHandler<string>? ThemeChanged;
}
