using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shape = System.Windows.Shapes.Shape;
using BetterConsole.App.Controls;
using BetterConsole.App.Plugins;
using BetterConsole.App.Services;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;
using BetterConsole.App.Views;
using BetterConsole.Sdk;

namespace BetterConsole.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly PluginManager _plugins;
    private bool _closingConfirmed;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        RestorePlacement();

        vm.AddTab(new TabVm("console", "Console", "", 0, () => new ConsoleView(vm)));
        vm.AddTab(new TabVm("players", "Players", "", 10, () => new PlayersView(vm)));
        vm.AddTab(new TabVm("client-errors", "Client errors", "", 20, () => new ClientErrorsView(vm)));
        vm.AddTab(new TabVm("server-errors", "Server errors", "", 30, () => new ServerErrorsView(vm)));
        vm.AddTab(new TabVm("stats", "Statistics", "", 40, () => new StatsView(vm)));
        vm.SelectedTab = vm.FindTab("console");
        ShowSelected();

        vm.PropertyChanged += OnVmChanged;
        vm.LuaTabAdded += tab =>
        {
            var tv = new TabVm("lua:" + tab.Id, tab.Title, "", 100 + tab.Order, () => new LuaTabView(tab));
            vm.AddTab(tv);
        };
        vm.LuaTabRemoved += id => vm.RemoveTab("lua:" + id);
        vm.Tabs.CollectionChanged += (_, _) => ShowSelected();

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnKeys;

        _plugins = new PluginManager(vm, this);
        UpdateStateVisuals();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _plugins.LoadAll();
        if (string.IsNullOrWhiteSpace(_vm.Settings.Server.ServerDirectory))
        {
            _vm.WriteAppLine("Welcome! Choose your server folder in Settings to get started.", false);
            await Dispatcher.InvokeAsync(() => OpenSettings(firstRun: true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        else if (_vm.Settings.Server.StartWithApp)
        {
            await _vm.StartAsync();
        }
        else
        {
            _vm.WriteAppLine($"Server: {_vm.Settings.Server.ServerDirectory}. Press Start (F5) to launch it.", false);
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedTab):
                ShowSelected();
                break;
            case nameof(MainViewModel.State):
            case nameof(MainViewModel.BridgeConnected):
            case nameof(MainViewModel.ServerIdle):
                UpdateStateVisuals();
                break;
        }
    }

    private void UpdateStateVisuals()
    {
        string key = _vm.State switch
        {
            ServerState.Running => _vm.ServerIdle ? "Brush.Warning" : "Brush.Success",
            ServerState.Starting or ServerState.Stopping => "Brush.Warning",
            ServerState.Crashed => "Brush.Danger",
            _ => "Brush.TextMuted",
        };
        StateDot.SetResourceReference(Shape.FillProperty, key);
        BridgeDot.SetResourceReference(Shape.FillProperty, _vm.BridgeConnected ? "Brush.Success" : "Brush.TextMuted");
    }

    // ------------------------------------------------------------------------------ tabs

    private void ShowSelected()
    {
        var sel = _vm.SelectedTab;
        if (sel != null && !sel.IsCreated)
        {
            var content = sel.Content;
            content.Tag = sel.Id;
            Host.Children.Add(content);
        }
        foreach (FrameworkElement child in Host.Children)
            child.Visibility = (string?)child.Tag == sel?.Id ? Visibility.Visible : Visibility.Collapsed;
        // Views of removed tabs (an addon tab after a map change) are dropped.
        for (int i = Host.Children.Count - 1; i >= 0; i--)
        {
            var id = (string?)((FrameworkElement)Host.Children[i]).Tag;
            if (_vm.FindTab(id ?? "") == null) Host.Children.RemoveAt(i);
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabVm tab }) _vm.SelectedTab = tab;
    }

    private void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (mods == ModifierKeys.Shift) _ = _vm.StopAsync();
            else if (mods == ModifierKeys.Control) _ = _vm.RestartAsync();
            else if (_vm.CanStart) _ = _vm.StartAsync();
            return;
        }
        if (mods == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int i = e.Key - Key.D1;
            if (i < _vm.Tabs.Count) _vm.SelectedTab = _vm.Tabs[i];
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && e.Key == Key.Tab)
        {
            int i = _vm.SelectedTab == null ? 0 : _vm.Tabs.IndexOf(_vm.SelectedTab);
            _vm.SelectedTab = _vm.Tabs[(i + 1) % _vm.Tabs.Count];
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------------------ header buttons

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var themes = ThemeManager.Discover(Path.Combine(AppSettings.DataDirectory, "themes"));
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var t in themes)
        {
            var item = new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = t.Name == ThemeManager.Current.Name };
            item.Click += (_, _) =>
            {
                ThemeManager.Apply(t);
                _vm.Settings.Theme = t.Name;
                _vm.Settings.Save();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var folder = new MenuItem { Header = "Open themes folder…" };
        folder.Click += (_, _) => OpenFolder(EnsureThemesFolder());
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private static string EnsureThemesFolder()
    {
        var dir = Path.Combine(AppSettings.DataDirectory, "themes");
        Directory.CreateDirectory(dir);
        var sample = Path.Combine(dir, "example-ocean.json.sample");
        if (!File.Exists(sample))
        {
            File.WriteAllText(sample, """
                {
                  // Rename to example-ocean.json and pick it from the theme menu.
                  // Any colour you leave out comes from "base". All names: see docs/themes.md
                  "base": "Dark",
                  "Background": "#0F1B24",
                  "Panel": "#132331",
                  "Surface": "#18304A",
                  "Accent": "#3FC1C9",
                  "AccentSoft": "#1B4650",
                  "ConsoleBackground": "#0B151D"
                }
                """);
        }
        return dir;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings(firstRun: false);

    private void OpenSettings(bool firstRun)
    {
        var w = new SettingsWindow(_vm, _plugins, firstRun) { Owner = this };
        w.ShowDialog();
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        void Add(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("Kill the server process", () =>
        {
            if (PromptDialog.Confirm(this, "Kill the server", "Ends srcds at once, without saving anything. Use it only when the server hangs.", "Kill", danger: true))
                _vm.Kill();
        }, _vm.IsRunning);
        menu.Items.Add(new Separator());
        Add("Open the server folder", () => OpenFolder(_vm.Settings.Server.ServerDirectory), Directory.Exists(_vm.Settings.Server.ServerDirectory));
        Add("Open the garrysmod folder", () => OpenFolder(_vm.Settings.Server.GameDirectory), Directory.Exists(_vm.Settings.Server.GameDirectory));
        Add("Open BetterConsole's data folder", () => OpenFolder(AppSettings.DataDirectory));
        Add("Plugins folder", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "plugins");
            Directory.CreateDirectory(dir);
            OpenFolder(dir);
        });
        menu.Items.Add(new Separator());
        Add("Documentation", () => OpenUrl("https://github.com/RainBowSheepx/gm_betterconsole/tree/main/docs"));
        Add("Report a problem", () => OpenUrl("https://github.com/RainBowSheepx/gm_betterconsole/issues"));
        Add($"About BetterConsole {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", () => OpenUrl("https://github.com/RainBowSheepx/gm_betterconsole"));
        menu.IsOpen = true;
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------------------ window state

    private void RestorePlacement()
    {
        var s = _vm.Settings;
        if (s.WindowWidth >= MinWidth) Width = s.WindowWidth;
        if (s.WindowHeight >= MinHeight) Height = s.WindowHeight;
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            var r = new Rect(s.WindowLeft, s.WindowTop, Width, Height);
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(r))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.WindowLeft = b.Left;
        s.WindowTop = b.Top;
        s.WindowWidth = b.Width;
        s.WindowHeight = b.Height;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        e.Cancel = true;
        if (_vm.IsRunning && _vm.Settings.ConfirmExitWhileRunning)
        {
            var d = new PromptDialog(this, "The server is running", "BetterConsole will send \"quit\" to the server and wait for it to stop. Closing the console always stops the server.",
                "Stop server and exit", danger: true);
            if (d.ShowDialog() != true) return;
        }
        SavePlacement();
        IsEnabled = false;
        _vm.WriteAppLine("Stopping the server and closing…", false);
        try
        {
            _plugins.ShutdownAll();
            await _vm.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Write("shutdown: " + ex);
        }
        _closingConfirmed = true;
        Close();
    }
}
