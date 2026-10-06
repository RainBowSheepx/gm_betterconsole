using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shape = System.Windows.Shapes.Shape;
using BetterConsole.App.Controls;
using BetterConsole.App.Services;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;
using BetterConsole.App.Views;
using BetterConsole.Core.Server;
using BetterConsole.Sdk;

namespace BetterConsole.App;

/// <summary>
/// A window that shows one server at a time: its tabs, its controls and its status bar. The main window
/// shows every server that has no window of its own (multi-console: switched in the side bar); closing
/// it quits. A server's own window shows only that server; closing it puts the server back.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly DependencyProperty CurrentServerProperty =
        DependencyProperty.Register(nameof(CurrentServer), typeof(ServerViewModel), typeof(MainWindow));

    private readonly AppShell _shell;
    private readonly ObservableCollection<ToastVm> _toasts = new();
    private readonly DispatcherTimer _attention;
    private ServerViewModel? _vm;
    private bool _closingConfirmed;
    private bool _sideOpen;

    public MainWindow(AppShell shell, bool isMain)
    {
        _shell = shell;
        IsMain = isMain;
        InitializeComponent();
        if (isMain) RestorePlacement();
        else WindowStartupLocation = WindowStartupLocation.Manual;
        ToastList.ItemsSource = _toasts;
        ServerList.ItemsSource = shell.Servers;

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        TabScroller.ScrollChanged += (_, _) => UpdateTabOverflow();
        SizeChanged += (_, _) => UpdateCompactHeader();
        Closing += OnClosing;
        PreviewKeyDown += OnKeys;
        shell.ServersChanged += UpdateMode;
        shell.UpdateChanged += ShowUpdateBadge;
        Closed += (_, _) =>
        {
            shell.ServersChanged -= UpdateMode;
            shell.UpdateChanged -= ShowUpdateBadge;
        };
        ShowUpdateBadge();

        _attention = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _attention.Tick += (_, _) => UpdateAttention();
        _attention.Start();
        UpdateMode();
    }

    /// <summary>The main window (closing it quits) or a server's own window.</summary>
    public bool IsMain { get; }

    /// <summary>The server this window shows.</summary>
    public ServerViewModel? Current => _vm;

    /// <summary>Same as <see cref="Current"/>, for bindings (the selected item of the server list).</summary>
    public ServerViewModel? CurrentServer
    {
        get => (ServerViewModel?)GetValue(CurrentServerProperty);
        private set => SetValue(CurrentServerProperty, value);
    }

    // ------------------------------------------------------------------------------ the server shown

    /// <summary>Shows another server: its views move into this window, the previous server's views leave it.</summary>
    public void ShowServer(ServerViewModel vm)
    {
        if (_vm == vm) return;
        Release();
        _vm = vm;
        CurrentServer = vm;
        DataContext = vm;
        // Paths in the server's errors and profiler open its files.
        LuaLinks.SetResolver(Host, vm.Files);
        vm.IsShown = true;
        vm.PropertyChanged += OnVmChanged;
        vm.Tabs.CollectionChanged += OnTabsChanged;
        vm.SettingsChanged += UpdateTabOverflow;
        vm.ExtraStatus.CollectionChanged += OnExtraStatusChanged;
        foreach (var tab in vm.Tabs)
        {
            if (tab.CreatedContent is not { } view) continue;
            if (view.Parent is Panel old) old.Children.Remove(view);
            Host.Children.Add(view);
        }
        ShowSelected();
        UpdateStateVisuals();
        UpdateAttention();
        ApplyStatusHidden();
        Dispatcher.BeginInvoke(UpdateTabOverflow, DispatcherPriority.Loaded);
    }

    /// <summary>Lets go of the server's views, so another window can show it.</summary>
    public void Release()
    {
        if (_vm == null) return;
        _vm.PropertyChanged -= OnVmChanged;
        _vm.Tabs.CollectionChanged -= OnTabsChanged;
        _vm.SettingsChanged -= UpdateTabOverflow;
        _vm.ExtraStatus.CollectionChanged -= OnExtraStatusChanged;
        UnwatchStatus();
        _vm.IsShown = false;
        Host.Children.Clear();
        _vm = null;
        CurrentServer = null;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ShowSelected();

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ServerViewModel.SelectedTab):
                ShowSelected();
                break;
            case nameof(ServerViewModel.State):
            case nameof(ServerViewModel.BridgeConnected):
            case nameof(ServerViewModel.ServerIdle):
                UpdateStateVisuals();
                break;
            case nameof(ServerViewModel.NextRestartText):
            case nameof(ServerViewModel.DisplayName):
                UpdateStateVisuals();
                break;
        }
    }

    private void UpdateStateVisuals()
    {
        if (_vm == null) return;
        StateDot.SetResourceReference(Shape.FillProperty, _vm.StateColorKey);
        BridgeDot.SetResourceReference(Shape.FillProperty, _vm.BridgeConnected ? "Brush.Success" : "Brush.TextMuted");
        var tips = new List<string>();
        if (_shell.MultiServer) tips.Add(_vm.DisplayName);
        if (!ProcessTuning.IsAll(_vm.Profile.AffinityMask) || _vm.Profile.Priority != "Normal")
            tips.Add($"CPU {ProcessTuning.Describe(_vm.Profile.AffinityMask)} · priority {ProcessTuning.PriorityText(_vm.Profile.Priority)}");
        if (_vm.Profile.AlwaysRun) tips.Add("Always run: started again whatever stops it (except Stop)");
        if (_vm.NextRestartText.Length > 0) tips.Add(_vm.NextRestartText);
        StatePill.ToolTip = tips.Count > 0 ? string.Join("\n", tips) : null;
        ServerButton.ToolTip = $"Servers · now: {_vm.DisplayName}\nCtrl+Alt+1…9 switch servers";
    }

    /// <summary>Multi-console on or off, servers added or moved: the logo button and the side bar.</summary>
    private void UpdateMode()
    {
        bool multi = _shell.MultiServer;
        ServerButton.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        LogoImage.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        SideMergeButton.Visibility = _shell.Servers.Any(s => s.IsDetached) ? Visibility.Visible : Visibility.Collapsed;
        if (!multi && _sideOpen) CloseSide();
        UpdateStateVisuals();
        UpdateAttention();
    }

    /// <summary>A red dot on the logo while another server crashed or has errors nobody looked at.</summary>
    private void UpdateAttention()
    {
        bool attention = _shell.MultiServer && _shell.Servers.Any(s => s != _vm && (s.State == ServerState.Crashed || s.UnseenErrors > 0));
        AttentionDot.Visibility = attention ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------------------ status bar

    /// <summary>The built-in items of the status bar by their id (the Tag in the XAML), with their names in its menu.</summary>
    private static readonly (string Id, string Name)[] StatusNames =
    [
        ("map", "Map"), ("players", "Players"), ("cpu", "CPU (like \"stats\")"), ("in", "Network in"), ("out", "Network out"),
        ("sv", "Server frame rate (sv)"), ("tick", "Ticks per second"), ("load", "Game thread load"), ("ents", "Entities"),
        ("lua", "Lua memory"), ("ram", "Memory of srcds"), ("addon", "Companion addon"),
    ];

    // The extra items whose showing and hiding this window follows (their separators).
    private readonly HashSet<StatusItemVm> _watchedStatus = new();

    private void OnExtraStatusChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyStatusHidden();

    private void OnExtraStatusItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusItemVm.IsShown)) UpdateExtraSeparators();
    }

    private void UnwatchStatus()
    {
        foreach (var x in _watchedStatus) x.PropertyChanged -= OnExtraStatusItemChanged;
        _watchedStatus.Clear();
    }

    /// <summary>
    /// What the user hid (the menu of the status bar). The separator in front of an item is left out when
    /// nothing is shown before it.
    /// </summary>
    public void ApplyStatusHidden()
    {
        var hidden = _shell.Settings.StatusHidden;
        bool before = false;
        foreach (var item in StatusItems.Children.OfType<StackPanel>())
        {
            bool shown = !hidden.Contains((string)item.Tag);
            item.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (item.Children.Count > 0 && item.Children[0] is Border sep) sep.Visibility = before ? Visibility.Visible : Visibility.Collapsed;
            before |= shown;
        }
        BridgeItem.Visibility = hidden.Contains("addon") ? Visibility.Collapsed : Visibility.Visible;
        if (_vm == null) return;
        foreach (var x in _vm.ExtraStatus)
        {
            x.UserHidden = hidden.Contains(x.Id);
            // A plugin shows or hides its item by itself, too.
            if (_watchedStatus.Add(x)) x.PropertyChanged += OnExtraStatusItemChanged;
        }
        UpdateExtraSeparators();
    }

    /// <summary>An extra item has no separator when nothing is shown before it (the user hid the built-in items).</summary>
    private void UpdateExtraSeparators()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm == null) return;
            bool any = StatusItems.Children.OfType<StackPanel>().Any(i => i.Visibility == Visibility.Visible);
            foreach (var x in _vm.ExtraStatus)
            {
                if (ExtraStatusList.ItemContainerGenerator.ContainerFromItem(x) is not ContentPresenter cp) continue;
                cp.ApplyTemplate();
                if (cp.ContentTemplate?.FindName("Sep", cp) is Border sep) sep.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                any |= x.IsShown;
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnStatusBarRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = StatusMenu();
        menu.PlacementTarget = StatusBar;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Show or hide each item; the choice is the same in every window and is saved when BetterConsole closes.</summary>
    private ContextMenu StatusMenu()
    {
        var menu = new ContextMenu();
        var hidden = _shell.Settings.StatusHidden;
        void Toggle(string id, string name)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = !hidden.Contains(id), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                hidden.Remove(id);
                if (!item.IsChecked) hidden.Add(id);
                foreach (var w in _shell.Windows) w.ApplyStatusHidden();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuItem { Header = "Status bar", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var (id, name) in StatusNames) Toggle(id, name);
        if (_vm is { ExtraStatus.Count: > 0 })
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "From addons and plugins", IsEnabled = false });
            foreach (var x in _vm.ExtraStatus) Toggle(x.Id, x.MenuName);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Show all", IsEnabled = hidden.Count > 0 };
        all.Click += (_, _) =>
        {
            hidden.Clear();
            foreach (var w in _shell.Windows) w.ApplyStatusHidden();
        };
        menu.Items.Add(all);
        return menu;
    }

    private ContextMenu? _scriptStatusMenu;

    /// <summary>For the UI script runner: the menu of the status bar, open; "click" toggles an item by its name.</summary>
    public ContextMenu ScriptStatusMenu()
    {
        _scriptStatusMenu = StatusMenu();
        _scriptStatusMenu.PlacementTarget = StatusBar;
        _scriptStatusMenu.Placement = PlacementMode.Relative;
        _scriptStatusMenu.HorizontalOffset = 260;
        _scriptStatusMenu.VerticalOffset = -380;
        _scriptStatusMenu.IsOpen = true;
        return _scriptStatusMenu;
    }

    // ------------------------------------------------------------------------------ toasts

    /// <summary>A notification in the corner. One of another server than the shown one names it.</summary>
    public void ShowToast(ServerViewModel? from, string text, NotifyKind kind, TimeSpan? duration = null)
    {
        if (from != null && _shell.MultiServer && from != _vm) text = $"{from.DisplayName}: {text}";
        var t = new ToastVm(text, kind);
        _toasts.Add(t);
        while (_toasts.Count > 4) _toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(kind == NotifyKind.Error ? 8 : 4.5) };
        timer.Tick += (_, _) => { timer.Stop(); _toasts.Remove(t); };
        timer.Start();
    }

    // ------------------------------------------------------------------------------ side bar

    private void OnServerButton(object sender, RoutedEventArgs e)
    {
        if (_sideOpen) CloseSide();
        else OpenSide();
    }

    public void OpenSide()
    {
        if (!_shell.MultiServer) return;
        _sideOpen = true;
        UpdateMode();
        SideOverlay.Visibility = Visibility.Visible;
        if (Look.Compact)
        {
            // No animation in compact mode: there at once.
            SideShift.BeginAnimation(TranslateTransform.XProperty, null);
            SideDim.BeginAnimation(OpacityProperty, null);
            SideShift.X = 0;
            SideDim.Opacity = 0.55;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SideShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
        SideDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, TimeSpan.FromMilliseconds(190)));
    }

    public void CloseSide()
    {
        if (!_sideOpen) return;
        _sideOpen = false;
        if (Look.Compact)
        {
            SideShift.BeginAnimation(TranslateTransform.XProperty, null);
            SideDim.BeginAnimation(OpacityProperty, null);
            SideShift.X = -SidePanel.ActualWidth - 12;
            SideDim.Opacity = 0;
            SideOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(-SidePanel.ActualWidth - 12, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        slide.Completed += (_, _) => { if (!_sideOpen) SideOverlay.Visibility = Visibility.Collapsed; };
        SideShift.BeginAnimation(TranslateTransform.XProperty, slide);
        SideDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    private void OnSideDimClick(object sender, MouseButtonEventArgs e) => CloseSide();

    private void OnSideClose(object sender, RoutedEventArgs e) => CloseSide();

    /// <summary>A server of this window is shown here; one in another window brings that window up.</summary>
    private void OnSideServerClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerViewModel vm) return;
        SwitchTo(vm);
    }

    private void SwitchTo(ServerViewModel vm)
    {
        CloseSide();
        if (_shell.WindowOf(vm) == this) ShowServer(vm);
        else _shell.Focus(vm);
    }

    private void OnSideServerMenu(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerViewModel vm) return;
        var menu = ServerMenu(vm);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnSideServerMore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerViewModel vm) return;
        var menu = ServerMenu(vm);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu ServerMenu(ServerViewModel vm)
    {
        var menu = new ContextMenu();
        void Add(string header, string icon, Action a, bool enabled = true)
        {
            var mi = new MenuItem
            {
                Header = header,
                IsEnabled = enabled,
                Icon = new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 13 },
            };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        menu.Items.Add(new MenuItem { Header = vm.DisplayName, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        if (vm.CanStart) Add("Start", "", () => _ = vm.StartAsync("Start (server list)"));
        else
        {
            Add("Stop", "", () => _ = vm.StopAsync("Stop (server list)"));
            Add("Restart", "", () => _ = vm.RestartAsync("Restart (server list)"));
        }
        menu.Items.Add(new Separator());
        if (vm.IsDetached) Add("Move back to the main window", "", () => _shell.Attach(vm));
        else Add("Open in a window of its own", "", () => { CloseSide(); _shell.Detach(vm); }, _shell.CanDetach(vm));
        Add("CPU affinity and priority…", "", () => OpenAffinity(vm));
        Add("Server settings…", "", () => { CloseSide(); OpenSettings(firstRun: false, vm); });
        return menu;
    }

    private void OnSideAddServer(object sender, RoutedEventArgs e)
    {
        CloseSide();
        OpenSettings(firstRun: false, null, addServer: true);
    }

    private void OnSideMerge(object sender, RoutedEventArgs e)
    {
        CloseSide();
        _shell.MergeAll();
    }

    private void OnSideJournal(object sender, RoutedEventArgs e)
    {
        CloseSide();
        OpenJournal();
    }

    // ------------------------------------------------------------------------------ tabs

    private void ShowSelected()
    {
        if (_vm == null) return;
        var sel = _vm.SelectedTab;
        if (sel != null && sel.CreatedContent?.Parent != Host)
        {
            var content = sel.Content;
            if (content.Parent is Panel old) old.Children.Remove(content);
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
        // A tab chosen from the list or with Ctrl+number may be scrolled out of the strip.
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm?.SelectedTab is { } t && TabStrip.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe) fe.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is FrameworkElement { DataContext: TabVm tab }) _vm.SelectedTab = tab;
    }

    private void UpdateTabOverflow()
    {
        UpdateCompactTabs();
        TabsMenuButton.Visibility = TabScroller.ExtentWidth > TabScroller.ViewportWidth + 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Icons only, by the setting; "auto" switches to icons when the tabs with their titles do not fit
    /// (and back once they do again, with a little margin so it does not flip at the edge).
    /// </summary>
    private void UpdateCompactTabs()
    {
        if (_vm == null) return;
        var mode = _shell.Settings.TabTitles;
        if (mode == "icons") { _vm.CompactTabs = true; return; }
        if (mode != "auto") { _vm.CompactTabs = false; return; }
        if (!IsLoaded) return;
        double full = 0;
        var font = new Typeface((FontFamily)FindResource("Font.Ui"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var tab in _vm.Tabs)
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement fe) return;
            full += fe.ActualWidth;
            if (_vm.CompactTabs)
            {
                // What the title adds: its width, the gap after the icon, the wider padding.
                var text = new FormattedText(tab.Header, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font, FontSize, Brushes.White, dip);
                full += text.WidthIncludingTrailingWhitespace + 7 + 2;
            }
        }
        double room = TabScroller.ActualWidth + (TabsMenuButton.Visibility == Visibility.Visible ? TabsMenuButton.ActualWidth + 10 : 0);
        if (room <= 0) return;
        if (!_vm.CompactTabs && full > room + 0.5) _vm.CompactTabs = true;
        else if (_vm.CompactTabs && full < room - 12) _vm.CompactTabs = false;
    }

    private void SetTabTitles(string mode)
    {
        _shell.Settings.TabTitles = mode;   // saved on exit
        foreach (var w in _shell.Windows) w.UpdateCompactTabs();
    }

    private void OnTabsRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TabScroller, Placement = PlacementMode.MousePoint };
        foreach (var (mode, text) in new[] { ("show", "Show tab titles"), ("icons", "Icons only"), ("auto", "Icons only when the titles do not fit") })
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = _shell.Settings.TabTitles == mode };
            item.Click += (_, _) => SetTabTitles(mode);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>All tabs in a list, for when they do not fit in the strip.</summary>
    private void OnTabsMenu(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var menu = new ContextMenu { PlacementTarget = TabsMenuButton, Placement = PlacementMode.Bottom, MaxHeight = Math.Max(200, ActualHeight - 80) };
        var icons = (FontFamily)FindResource("Font.Icons");
        foreach (var tab in _vm.Tabs)
        {
            var item = new MenuItem
            {
                Header = tab.HasBadge ? $"{tab.Header}  ({tab.Badge})" : tab.Header,
                Icon = new TextBlock { Text = tab.Icon, FontFamily = icons, FontSize = 13 },
                FontWeight = tab.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            item.Click += (_, _) => _vm.SelectedTab = tab;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>For the UI script runner.</summary>
    public void ScriptTabsMenu() => OnTabsMenu(this, new RoutedEventArgs());

    /// <summary>In a narrow window the server buttons show only their icons, so more tabs fit.</summary>
    private void UpdateCompactHeader()
    {
        var labels = ActualWidth < 1150 ? Visibility.Collapsed : Visibility.Visible;
        StartLabel.Visibility = StopLabel.Visibility = RestartLabel.Visibility = labels;
        UptimeHost.Visibility = ActualWidth < 900 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_sideOpen && key == Key.Escape)
        {
            CloseSide();
            e.Handled = true;
            return;
        }
        // AltGr arrives as Ctrl+Alt: AltGr+7/8/9 type { [ ] on many layouts, so it is not a shortcut.
        if (_shell.MultiServer && mods == (ModifierKeys.Control | ModifierKeys.Alt) && !Keyboard.IsKeyDown(Key.RightAlt) && key >= Key.D1 && key <= Key.D9)
        {
            int n = key - Key.D1;
            if (n < _shell.Servers.Count)
            {
                SwitchTo(_shell.Servers[n]);
                e.Handled = true;
            }
            return;
        }
        if (_vm == null) return;
        if (key == Key.F5)
        {
            e.Handled = true;
            if (mods == ModifierKeys.Shift) _ = _vm.StopAsync("Stop (Shift+F5)");
            else if (mods == ModifierKeys.Control) _ = _vm.RestartAsync("Restart (Ctrl+F5)");
            else if (_vm.CanStart) _ = _vm.StartAsync("Start (F5)");
            return;
        }
        if (mods == ModifierKeys.Control && key >= Key.D1 && key <= Key.D9)
        {
            int i = key - Key.D1;
            if (i < _vm.Tabs.Count) _vm.SelectedTab = _vm.Tabs[i];
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && key == Key.Tab)
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
        bool compact = _shell.Settings.CompactMode;
        var mode = new MenuItem
        {
            Header = "Compact mode",
            IsCheckable = true,
            IsChecked = compact,
            ToolTip = "Smaller and plain: no themes, shadows, animations or avatars, for the least CPU and graphics work (a VPS without a graphics card draws everything in software). Everything works as before.",
        };
        mode.Click += (_, _) => _shell.SetCompact(!compact);
        menu.Items.Add(mode);
        menu.Items.Add(new Separator());
        if (compact) menu.Items.Add(new MenuItem { Header = "Themes are off in compact mode", IsEnabled = false });
        foreach (var t in themes)
        {
            var item = new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = t.Name == _shell.Settings.Theme, IsEnabled = !compact };
            item.Click += (_, _) =>
            {
                ThemeManager.Apply(t);
                // Saved when BetterConsole closes, like the other view settings.
                _shell.Settings.Theme = t.Name;
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
        return dir;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings(firstRun: false);

    /// <summary>The settings, on the page of <paramref name="server"/> (default: the one shown here).</summary>
    public void OpenSettings(bool firstRun, ServerViewModel? server = null, bool addServer = false)
    {
        var w = new SettingsWindow(_shell, server ?? _vm, firstRun, addServer) { Owner = this };
        w.ShowDialog();
    }

    public void OpenJournal(ServerViewModel? server = null)
    {
        var w = new JournalWindow(_shell, server) { Owner = this };
        w.Show();
    }

    public void OpenAffinity(ServerViewModel vm)
    {
        var d = new AffinityDialog(this, vm.Profile.AffinityMask, vm.Profile.Priority, _shell.MultiServer ? vm.DisplayName : null,
            _shell.Servers.Where(s => s != vm).Select(s => (s.DisplayName, s.Profile.AffinityMask)).ToList());
        if (d.ShowDialog() != true) return;
        vm.Profile.AffinityMask = d.Mask;
        vm.Profile.Priority = d.Priority;
        _shell.Settings.Save();
        UpdateStateVisuals();
        if (vm.Controller.ProcessId == null) return;
        var problem = vm.Controller.ApplyProcessSettings();
        if (problem == null)
        {
            vm.WriteAppLine($"CPU: {ProcessTuning.Describe(d.Mask)} · priority {ProcessTuning.PriorityText(d.Priority)}.", false);
            vm.Notify("CPU affinity and priority applied.", NotifyKind.Success);
        }
        else vm.Notify("Could not set CPU affinity / priority: " + problem, NotifyKind.Error);
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var vm = _vm;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        LastMoreMenu = menu;
        MenuItem Add(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
            return mi;
        }
        if (_shell.AvailableUpdate is { } update)
        {
            var mi = Add($"Download BetterConsole {update.Version.ToString(3)}…", () => OpenUrl(update.Url));
            mi.FontWeight = FontWeights.SemiBold;
            mi.SetResourceReference(ForegroundProperty, "Brush.Accent");
            mi.ToolTip = $"A newer version is on GitHub (this is {UpdateChecker.Current.ToString(3)}): its release page has the downloads and what is new.";
            menu.Items.Add(new Separator());
        }
        Add("Kill the server process", () =>
        {
            if (PromptDialog.Confirm(this, "Kill the server", "Ends srcds at once, without saving anything. Use it only when the server hangs.", "Kill", danger: true))
                vm.Kill();
        }, vm.IsRunning);
        Add($"CPU affinity and priority…  ({ProcessTuning.Describe(vm.Profile.AffinityMask)})", () => OpenAffinity(vm));
        Add("Start / stop journal…", () => OpenJournal(_shell.MultiServer ? vm : null));
        menu.Items.Add(new Separator());
        // Here too: compact mode hides the theme button.
        var compact = new MenuItem { Header = "Compact mode", IsCheckable = true, IsChecked = _shell.Settings.CompactMode };
        compact.Click += (_, _) => _shell.SetCompact(compact.IsChecked);
        menu.Items.Add(compact);
        if (_shell.MultiServer)
        {
            menu.Items.Add(new Separator());
            if (vm.IsDetached) Add("Move back to the main window", () => _shell.Attach(vm));
            else Add("Open this server in a window of its own", () => _shell.Detach(vm), _shell.CanDetach(vm));
        }
        menu.Items.Add(new Separator());
        Add("Open the server folder", () => OpenFolder(vm.Profile.ServerDirectory), Directory.Exists(vm.Profile.ServerDirectory));
        Add("Open the garrysmod folder", () => OpenFolder(vm.Profile.GameDirectory), Directory.Exists(vm.Profile.GameDirectory));
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
        Add("Check for updates", () => _ = CheckForUpdatesAsync());
        Add($"About BetterConsole {UpdateChecker.Current.ToString(3)}", () => OpenUrl("https://github.com/RainBowSheepx/gm_betterconsole"));
        menu.IsOpen = true;
    }

    /// <summary>For the UI script runner: the … menu opened last.</summary>
    public ContextMenu? LastMoreMenu { get; private set; }

    /// <summary>… → Check for updates: asks GitHub now and says what it found.</summary>
    public async Task CheckForUpdatesAsync()
    {
        ShowToast(null, "Checking for updates…", NotifyKind.Info);
        var r = await UpdateChecker.CheckAsync();
        var current = UpdateChecker.Current.ToString(3);
        if (r.Error != null)
        {
            ShowToast(null, "Could not check for updates: " + r.Error, NotifyKind.Warning);
            return;
        }
        _shell.SetUpdate(r.Latest);
        if (r.Latest is not { } latest)
        {
            ShowToast(null, "No release of BetterConsole is on GitHub yet.", NotifyKind.Info);
            return;
        }
        if (!r.IsNewer)
        {
            ShowToast(null, latest.Version == UpdateChecker.Current ? $"BetterConsole {current} is the latest version." : $"This is {current}; the latest release is {latest.Version.ToString(3)}.", NotifyKind.Success);
            return;
        }
        var when = latest.Published is { } p ? $" (released {p.LocalDateTime:yyyy-MM-dd})" : "";
        if (PromptDialog.Confirm(this, "A new version", $"BetterConsole {latest.Version.ToString(3)}{when} is available; this is {current}. Its release page has the downloads and what is new.", "Open the release page"))
            OpenUrl(latest.Url);
    }

    /// <summary>A dot on the … button while a newer version is available.</summary>
    private void ShowUpdateBadge()
    {
        if (_shell.AvailableUpdate is not { } update)
        {
            MoreButton.Content = "\uE712";
            MoreButton.ToolTip = "More";
            return;
        }
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0) };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Accent");
        MoreButton.Content = new Grid { Children = { new TextBlock { Text = "\uE712" }, dot } };
        MoreButton.ToolTip = $"More · BetterConsole {update.Version.ToString(3)} is available";
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
        var s = _shell.Settings;
        Place(new WindowPlacement { Left = s.WindowLeft, Top = s.WindowTop, Width = s.WindowWidth, Height = s.WindowHeight, Maximized = s.WindowMaximized });
    }

    /// <summary>Size and position, if that is still on a screen.</summary>
    public void Place(WindowPlacement p)
    {
        if (p.Width >= MinWidth) Width = p.Width;
        if (p.Height >= MinHeight) Height = p.Height;
        if (!double.IsNaN(p.Left) && !double.IsNaN(p.Top))
        {
            var r = new Rect(p.Left, p.Top, Width, Height);
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(r))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = p.Left;
                Top = p.Top;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    public void SavePlacement()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        bool max = WindowState == WindowState.Maximized;
        if (IsMain)
        {
            var s = _shell.Settings;
            s.WindowMaximized = max;
            s.WindowLeft = b.Left;
            s.WindowTop = b.Top;
            s.WindowWidth = b.Width;
            s.WindowHeight = b.Height;
        }
        else if (_vm != null)
        {
            _vm.Profile.Window = new WindowPlacement { Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height, Maximized = max };
        }
    }

    /// <summary>Closes without asking (the server moved back, or the app quits).</summary>
    public void CloseQuietly()
    {
        _closingConfirmed = true;
        _attention.Stop();
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        if (!IsMain && _shell.IsExiting)
        {
            // BetterConsole closes all windows itself once the servers have stopped.
            e.Cancel = true;
            return;
        }
        if (!IsMain)
        {
            // A server's own window: it closes, the server goes back into the main window and keeps running.
            // (Close() must not be called again while the window is closing.)
            _closingConfirmed = true;
            _attention.Stop();
            if (_vm != null) _shell.Attach(_vm, windowClosing: true);
            return;
        }
        e.Cancel = true;
        // Already quitting (another click on X while the servers stop): wait for that.
        if (_shell.IsExiting) return;
        if (!await _shell.ExitAsync()) return;
        // Not from inside Closing (the await may have finished synchronously).
        _ = Dispatcher.BeginInvoke(CloseQuietly);
    }
}
