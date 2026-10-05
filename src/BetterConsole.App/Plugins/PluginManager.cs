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
    internal IConsolePlugin? Instance { get; set; }
}

/// <summary>
/// Loads plugins from <c>plugins\*\*.dll</c> (one folder per plugin) and <c>plugins\*.dll</c>. Each
/// plugin gets its own load context, so its dependencies do not clash with the app's; the SDK
/// assembly is shared so the interfaces match.
/// </summary>
public sealed class PluginManager
{
    private readonly MainViewModel _vm;
    private readonly Window _window;

    public PluginManager(MainViewModel vm, Window window)
    {
        _vm = vm;
        _window = window;
    }

    public List<PluginInfo> Plugins { get; } = new();

    public void LoadAll()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(root)) return;
        var files = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(root).SelectMany(d => Directory.EnumerateFiles(d, "*.dll", SearchOption.TopDirectoryOnly)));
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("BetterConsole.Sdk", StringComparison.OrdinalIgnoreCase)) continue;
            // In a plugin folder only the DLL named like the folder (or the only DLL) is the entry.
            var dir = Path.GetDirectoryName(file)!;
            if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(dir);
                bool single = Directory.GetFiles(dir, "*.dll").Length == 1;
                if (!single && !string.Equals(name, folder, StringComparison.OrdinalIgnoreCase)) continue;
            }
            Load(file);
        }
    }

    private void Load(string file)
    {
        try
        {
            var ctx = new PluginLoadContext(file);
            var asm = ctx.LoadFromAssemblyPath(file);
            var types = asm.GetTypes().Where(t => typeof(IConsolePlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IConsolePlugin plugin) continue;
                var info = new PluginInfo { Id = plugin.Id, Name = plugin.Name, Description = plugin.Description, Path = file };
                Plugins.Add(info);
                if (_vm.Settings.DisabledPlugins.Contains(plugin.Id))
                {
                    info.Enabled = false;
                    info.Status = "Disabled";
                    continue;
                }
                try
                {
                    plugin.Initialize(new PluginContext(plugin.Id, _vm, _window));
                    info.Instance = plugin;
                    info.Status = $"Loaded from {Path.GetFileName(file)}";
                    Log.Write($"plugin {plugin.Id} loaded from {file}");
                }
                catch (Exception ex)
                {
                    info.Status = "Failed: " + ex.Message;
                    _vm.WriteAppLine($"Plugin {plugin.Name} failed to start: {ex.Message}", true);
                    Log.Write($"plugin {plugin.Id} init failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            _vm.WriteAppLine($"Could not load plugin {Path.GetFileName(file)}: {ex.Message}", true);
            Log.Write($"plugin load {file}: {ex}");
        }
    }

    public void ShutdownAll()
    {
        foreach (var p in Plugins)
        {
            try { p.Instance?.Shutdown(); }
            catch (Exception ex) { Log.Write($"plugin {p.Id} shutdown: {ex}"); }
        }
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

/// <summary>What a plugin sees of the app (all calls on the UI thread).</summary>
internal sealed class PluginContext : IPluginContext, IServer, IConsoleOutput, ILuaBridge, IUiHost
{
    private readonly string _id;
    private readonly MainViewModel _vm;
    private readonly Window _window;

    public PluginContext(string id, MainViewModel vm, Window window)
    {
        _id = id;
        _vm = vm;
        _window = window;
        vm.ServerStateChanged += (o, n, c) => StateChanged?.Invoke(this, new ServerStateChangedEventArgs(o, n, c));
        vm.SnapshotUpdated += s => SnapshotUpdated?.Invoke(this, s);
        vm.ConsoleEvents += events =>
        {
            if (LineReceived == null) return;
            foreach (var e in events)
                if (e is BetterConsole.Core.Console.LineAdded la) LineReceived?.Invoke(this, new ConsoleLineEventArgs(la.Line));
        };
        vm.BridgeConnectionChanged += c => ConnectionChanged?.Invoke(this, c);
        vm.BridgeMessage += (type, msg) =>
        {
            if (MessageReceived == null) return;
            if (type == "custom")
            {
                var t = msg.TryGetProperty("type", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString()! : "custom";
                var data = msg.TryGetProperty("data", out var d) ? d : default;
                MessageReceived?.Invoke(this, new BridgeMessage(t, data));
            }
            else MessageReceived?.Invoke(this, new BridgeMessage(type, msg));
        };
        Themes.ThemeManager.Changed += t => ThemeChanged?.Invoke(this, t.Name);
    }

    public IServer Server => this;
    public IConsoleOutput Console => this;
    public ILuaBridge Bridge => this;
    public IUiHost Ui => this;

    public string DataDirectory
    {
        get
        {
            var dir = Path.Combine(AppSettings.DataDirectory, "plugins-data", string.Join("_", _id.Split(Path.GetInvalidFileNameChars())));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Log(string message) => Services.Log.Write($"[{_id}] {message}");

    // IServer
    public ServerState State => _vm.State;
    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;
    public int? ProcessId => _vm.Controller.ProcessId;
    public string GameDirectory => _vm.Settings.Server.GameDirectory;
    public ServerSnapshot? Latest => _vm.Latest;
    public event EventHandler<ServerSnapshot>? SnapshotUpdated;
    public void SendCommand(string command) => _vm.SendCommand(command);
    public Task StartAsync() => _vm.StartAsync();
    public Task StopAsync() => _vm.StopAsync();
    public Task RestartAsync() => _vm.RestartAsync();

    // IConsoleOutput
    public event EventHandler<ConsoleLineEventArgs>? LineReceived;
    public void WriteLine(string text, uint argb = 0) => _vm.WriteAppLine(text, false, argb);

    // ILuaBridge
    public bool IsConnected => _vm.BridgeConnected;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<BridgeMessage>? MessageReceived;
    public void Send(string type, object? data = null) => _vm.Controller.Bridge.Send("custom", new { type, data });

    // IUiHost
    public void AddTab(string id, string header, Func<FrameworkElement> content, int order = 1000) =>
        _vm.AddTab(new TabVm("plugin:" + id, header, "", order, content));

    public void RemoveTab(string id) => _vm.RemoveTab("plugin:" + id);

    public IStatusItem AddStatusItem(string id, string text, string? tooltip = null, int order = 1000)
    {
        var item = new StatusItemVm("plugin:" + id, order) { Text = text, Tooltip = tooltip };
        int i = 0;
        while (i < _vm.ExtraStatus.Count && _vm.ExtraStatus[i].Order <= order) i++;
        _vm.ExtraStatus.Insert(i, item);
        return item;
    }

    public void Notify(string message, NotifyKind kind = NotifyKind.Info) => _vm.Notify(message, kind);

    public string ThemeName => Themes.ThemeManager.Current.Name;
    public event EventHandler<string>? ThemeChanged;
}
