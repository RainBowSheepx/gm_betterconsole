using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BetterConsole.App.Controls;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

public partial class StatsView : UserControl
{
    private readonly ServerViewModel _vm;
    private readonly TimeSeriesChart[] _charts;

    public StatsView(ServerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        var s = vm.Stats;

        FrameChart.Add(s.FrameMs, "avg", "Chart1").Add(s.FrameMaxMs, "max", "Chart2");
        LoadChart.Add(s.Load, "game thread", "Chart1").Add(s.Cpu, "process CPU", "Chart4");
        FpsChart.Add(s.Fps, "fps", "Chart3").Add(s.Tps, "ticks", "Chart1");
        MemChart.Add(s.MemoryMB, "process", "Chart5").Add(s.LuaMB, "Lua", "Chart2");
        NetChart.Add(s.NetIn, "in", "Chart3").Add(s.NetOut, "out", "Chart1");
        PlayersChart.Add(s.Players, "players", "Chart1");
        EntsChart.Add(s.Entities, "entities", "Chart4");
        _charts = [FrameChart, LoadChart, FpsChart, MemChart, NetChart, PlayersChart, EntsChart];

        SetWindow(vm.Settings.StatsWindowMinutes);
        s.Updated += OnUpdated;
        SetupProfileGrid(HooksGrid, nameof(ProfileRow.MsPerSec));
        SetupProfileGrid(TimersGrid, nameof(ProfileRow.MsPerSec));
        SetupProfileGrid(NetInGrid, nameof(ProfileRow.MsPerSec));
        SetupProfileGrid(NetOutGrid, nameof(ProfileRow.BytesPerSec));
        SetupProfileGrid(EntsGrid, nameof(EntityClassRow.Count));
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatsVm.Profiling) or nameof(StatsVm.ProfilingInfo) or nameof(StatsVm.HasProfile)
                or nameof(StatsVm.ProviderName) or nameof(StatsVm.ProviderInfo)) UpdateProfiler();
            if (e.PropertyName is nameof(StatsVm.TickRate)) { UpdateBudget(); UpdateCapture(); }
            if (e.PropertyName is nameof(StatsVm.Capture) or nameof(StatsVm.ProviderName) or nameof(StatsVm.ProviderCapture) or nameof(StatsVm.ProviderVprof)) UpdateCapture();
        };
        s.ColumnsChanged += UpdateColumns;
        UpdateColumns();
        void UpdateNoSpikes() => NoSpikes.Visibility = s.Spikes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        s.Spikes.CollectionChanged += (_, _) => UpdateNoSpikes();
        UpdateNoSpikes();
        UpdateCapture();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ServerViewModel.BridgeConnected)) UpdateNote(); };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) OnUpdated(); };
        vm.StatsExtras.Widgets.CollectionChanged += (_, _) => SyncExtras();
        vm.StatsHiddenChanged += ApplyHidden;
        // What the user hides applies to every server; subscribed while on screen, so a removed server's view goes.
        Loaded += (_, _) => { UserHiddenChanged -= ApplyHidden; UserHiddenChanged += ApplyHidden; ApplyHidden(); };
        Unloaded += (_, _) => UserHiddenChanged -= ApplyHidden;
        UpdateBudget();
        UpdateProfiler();
        UpdateNote();
        SyncExtras();
        ApplyHidden();
    }

    private void OnUpdated()
    {
        if (!IsVisible) return;
        foreach (var c in _charts) c.Refresh();
        // The time axis of addon charts moves on too, also between their points.
        foreach (var c in _extraCharts.Values) c.Refresh();
    }

    // ------------------------------------------------------------------------------ addons and plugins

    // The built-in key numbers and charts by their id, in the order they have (10, 20, …): what addons place theirs by.
    private static readonly string[] KpiIds = ["fps", "frame", "load", "tick", "cpu", "memory", "lua", "players", "entities", "network", "uptime"];
    private static readonly string[] ChartIds = ["chart.frame", "chart.load", "chart.rate", "chart.memory", "chart.network", "chart.players", "chart.entities"];

    private readonly Dictionary<LuaWidgetVm, FrameworkElement> _extraViews = new();
    private readonly Dictionary<ChartWidgetVm, TimeSeriesChart> _extraCharts = new();

    /// <summary>
    /// The widgets of BetterConsole.Stats and of plugins: Stat cards with the key numbers, charts with the charts,
    /// the others below them; each by its order among the built-in ones.
    /// </summary>
    private void SyncExtras()
    {
        var widgets = _vm.StatsExtras.Widgets.ToList();
        foreach (var (w, view) in _extraViews.ToList())
        {
            if (widgets.Contains(w)) continue;
            if (view.Parent is Panel p) p.Children.Remove(view);
            _extraViews.Remove(w);
            if (w is ChartWidgetVm cw && _extraCharts.Remove(cw, out var chart)) cw.Updated -= chart.Refresh;
        }
        foreach (var w in widgets)
        {
            if (!_extraViews.ContainsKey(w))
            {
                _extraViews[w] = CreateView(w);
                // Defined again with another order or span: placed again.
                w.Reconfigured += () => { if (_extraViews.ContainsKey(w)) SyncExtras(); };
            }
            StatsLayout.SetSpan(_extraViews[w], SpanOf(w));
        }

        Place(Kpis, KpiIds, widgets.Where(w => w is StatWidgetVm));
        Place(Charts, ChartIds, widgets.Where(w => w is ChartWidgetVm));
        Place(Extras, [], widgets.Where(w => w is not (StatWidgetVm or ChartWidgetVm)));
        Extras.Visibility = Extras.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsLayout.Arrange(Charts);
        StatsLayout.Arrange(Extras);
        ApplyHidden();
    }

    /// <summary>The built-in children (by their Tag) and the widgets' views, sorted by order; built-in ones first on a tie.</summary>
    private void Place(Panel panel, string[] builtInIds, IEnumerable<LuaWidgetVm> widgets)
    {
        var builtIn = panel.Children.OfType<FrameworkElement>().Where(e => e.Tag is string id && builtInIds.Contains(id))
            .Select(e => (Order: (Array.IndexOf(builtInIds, (string)e.Tag) + 1) * 10, Rank: 0, View: e));
        var extra = widgets.Select(w => (Order: w.Order, Rank: 1, View: _extraViews[w]));
        var wanted = builtIn.Concat(extra).OrderBy(x => x.Order).ThenBy(x => x.Rank).Select(x => x.View).ToList();
        if (panel.Children.Cast<FrameworkElement>().SequenceEqual(wanted)) return;
        panel.Children.Clear();
        foreach (var v in wanted)
        {
            if (v.Parent is Panel old) old.Children.Remove(v);
            panel.Children.Add(v);
        }
    }

    /// <summary>Charts are half wide like the built-in ones unless the addon said otherwise.</summary>
    private static int SpanOf(LuaWidgetVm w) => w is ChartWidgetVm && !w.SpanGiven ? 6 : w.Span;

    private FrameworkElement CreateView(LuaWidgetVm w)
    {
        if (w is ChartWidgetVm cw)
        {
            var chart = new TimeSeriesChart { Decimals = 1, WindowSeconds = _vm.Settings.StatsWindowMinutes * 60 };
            chart.SetBinding(TimeSeriesChart.TitleProperty, new System.Windows.Data.Binding(nameof(ChartWidgetVm.Title)) { Source = cw });
            chart.SetBinding(TimeSeriesChart.UnitProperty, new System.Windows.Data.Binding(nameof(ChartWidgetVm.Unit)) { Source = cw });
            void Build()
            {
                chart.ClearSeries();
                foreach (var s in cw.Series) chart.Add(s.Data, s.Name, brush: s.Brush);
            }
            Build();
            cw.Series.CollectionChanged += (_, _) => Build();
            cw.Updated += chart.Refresh;
            _extraCharts[cw] = chart;
            var card = new Border { Child = chart };
            card.SetResourceReference(StyleProperty, "ChartCard");
            StatsLayout.SetSpan(card, SpanOf(cw));
            return card;
        }
        var view = new ContentPresenter { Content = w };
        StatsLayout.SetSpan(view, SpanOf(w));
        return view;
    }

    /// <summary>
    /// Built-in parts an addon or a plugin hid, and every part the user hid (right-click the tab). The
    /// user's choice is the same on every server and is saved when BetterConsole closes.
    /// </summary>
    private void ApplyHidden()
    {
        var user = _vm.Settings.StatsHidden;
        foreach (var e in BuiltInParts())
        {
            var id = (string)e.Tag;
            e.Visibility = _vm.IsStatHidden(id) || user.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var (w, view) in _extraViews)
            view.Visibility = user.Contains(KeyOf(w)) ? Visibility.Collapsed : Visibility.Visible;
    }

    private IEnumerable<FrameworkElement> BuiltInParts() =>
        Kpis.Children.OfType<FrameworkElement>().Concat(Charts.Children.OfType<FrameworkElement>()).Append(SpikesCard).Append(ProfilerCard)
            .Where(e => e.Tag is string id && (KpiIds.Contains(id) || ChartIds.Contains(id) || id is "spikes" or "profiler"));

    // ------------------------------------------------------------------------------ hiding parts (right-click)

    private static readonly string[] KpiNames =
        ["Server FPS", "Frame time", "Game thread load", "Ticks / second", "CPU (stats)", "Memory", "Lua memory", "Players", "Entities", "Network", "Uptime"];
    private static readonly string[] ChartNames = ["Frame time", "Load", "Rate", "Memory", "Network", "Players", "Entities"];
    private static readonly (string Id, string Name)[] Sections = [("spikes", "Lag spikes"), ("profiler", "Lua profiler")];

    /// <summary>The user hid or showed a part: the Statistics tabs of all servers follow.</summary>
    private static event Action? UserHiddenChanged;

    /// <summary>How the user's list names a widget of an addon ("lua:id") or a plugin ("plugin:…"), apart from the built-in ids.</summary>
    private static string KeyOf(LuaWidgetVm w) => w.Id.StartsWith("plugin:", StringComparison.Ordinal) ? w.Id : "lua:" + w.Id;

    private static string NameOf(LuaWidgetVm w) => (string.IsNullOrWhiteSpace(w.Title) ? w.Id[(w.Id.LastIndexOf(':') + 1)..] : w.Title!) +
        w switch { StatWidgetVm => " (number)", ChartWidgetVm => " (chart)", _ => "" };

    private void SetUserHidden(string key, bool hidden)
    {
        var list = _vm.Settings.StatsHidden;
        list.Remove(key);
        if (hidden) list.Add(key);
        UserHiddenChanged?.Invoke();
    }

    private void OnPartsMenu(object sender, MouseButtonEventArgs e)
    {
        if (HasOwnMenu(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
        var menu = _scriptMenu = PartsMenu(PartAt(e.OriginalSource as DependencyObject));
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// The pointer is on something with a menu of its own, which must keep it: a text box (Copy), a scroll
    /// bar (Scroll here, Top, Bottom) or anything with a ContextMenu (a plugin's section).
    /// </summary>
    private static bool HasOwnMenu(DependencyObject? from, DependencyObject? to)
    {
        for (var cur = from; cur != null && cur != to; cur = TreeWalk.Parent(cur))
            if (cur is TextBoxBase or ScrollBar || (cur is FrameworkElement fe && ContextMenuService.GetContextMenu(fe) != null)) return true;
        return false;
    }

    /// <summary>The card, chart or section the pointer is on: its key in the user's list and its name.</summary>
    private (string Key, string Name)? PartAt(DependencyObject? d)
    {
        for (var cur = d; cur != null; cur = TreeWalk.Parent(cur))
        {
            if (cur is not FrameworkElement fe) continue;
            if (fe.Parent != Kpis && fe.Parent != Charts && fe.Parent != Extras && fe != SpikesCard && fe != ProfilerCard) continue;
            if (_extraViews.FirstOrDefault(x => x.Value == fe) is { Key: { } w }) return (KeyOf(w), NameOf(w));
            if (fe.Tag is not string id) return null;
            int k = Array.IndexOf(KpiIds, id), c = Array.IndexOf(ChartIds, id);
            return (id, k >= 0 ? KpiNames[k] : c >= 0 ? ChartNames[c] + " (chart)" : Sections.FirstOrDefault(s => s.Id == id).Name ?? id);
        }
        return null;
    }

    /// <summary>"Hide …" for the part under the pointer, then every part to show or hide, and Show all.</summary>
    private ContextMenu PartsMenu((string Key, string Name)? at)
    {
        var user = _vm.Settings.StatsHidden;
        var menu = new ContextMenu();
        if (at is { } part)
        {
            var hide = new MenuItem { Header = $"Hide \"{part.Name}\"" };
            hide.Click += (_, _) => SetUserHidden(part.Key, true);
            menu.Items.Add(hide);
            menu.Items.Add(new Separator());
        }
        var all = new MenuItem { Header = "Show all you hid" };
        var groups = new List<(MenuItem Group, string Title)>();
        // The items stay open on click: the counts and Show all follow at once.
        void Refresh()
        {
            foreach (var (group, title) in groups)
            {
                int hidden = group.Items.OfType<MenuItem>().Count(i => !i.IsChecked);
                group.Header = hidden > 0 ? $"{title}  ·  {hidden} hidden" : title;
            }
            all.IsEnabled = user.Count > 0;
        }
        // A submenu per group: the whole list would be taller than many screens.
        void Group(string title, IEnumerable<(string Key, string Name, bool ByAddon)> parts)
        {
            var group = new MenuItem { Header = title };
            foreach (var (key, name, byAddon) in parts)
            {
                var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = !byAddon && !user.Contains(key), StaysOpenOnClick = true, IsEnabled = !byAddon };
                if (byAddon)
                {
                    item.ToolTip = "Hidden by an addon or a plugin";
                    ToolTipService.SetShowOnDisabled(item, true);
                }
                item.Click += (_, _) =>
                {
                    SetUserHidden(key, !item.IsChecked);
                    Refresh();
                };
                group.Items.Add(item);
            }
            if (group.Items.Count == 0) return;
            groups.Add((group, title));
            menu.Items.Add(group);
        }
        Group("Numbers", KpiIds.Select((id, i) => (id, KpiNames[i], _vm.IsStatHidden(id))));
        Group("Charts", ChartIds.Select((id, i) => (id, ChartNames[i], _vm.IsStatHidden(id))));
        Group("Sections", Sections.Select(s => (s.Id, s.Name, _vm.IsStatHidden(s.Id))));
        // Addon parts of this server, and what the user hid of addons that are not here now (another
        // server's, a removed addon's): so each can be shown again on its own.
        var extras = _vm.StatsExtras.Widgets.Select(w => (KeyOf(w), NameOf(w), false)).ToList();
        var builtIn = KpiIds.Concat(ChartIds).Concat(Sections.Select(s => s.Id)).ToHashSet();
        foreach (var key in user.Where(k => !builtIn.Contains(k) && extras.All(x => x.Item1 != k)).ToList())
            extras.Add((key, key[(key.LastIndexOf(':') + 1)..] + " (not here now)", false));
        Group("From addons and plugins", extras);
        menu.Items.Add(new Separator());
        all.Click += (_, _) =>
        {
            user.Clear();
            UserHiddenChanged?.Invoke();
        };
        menu.Items.Add(all);
        Refresh();
        return menu;
    }

    /// <summary>
    /// For the UI script runner: a right-click (the real handler) on the innermost element of the part with
    /// that key; returns the menu that opened.
    /// </summary>
    public ContextMenu? ScriptRightClick(string key)
    {
        var part = BuiltInParts().Concat(_extraViews.Values).FirstOrDefault(e => e.Tag as string == key || _extraViews.Any(x => x.Value == e && KeyOf(x.Key) == key));
        if (part == null) return null;
        DependencyObject target = part;
        while (System.Windows.Media.VisualTreeHelper.GetChildrenCount(target) > 0) target = System.Windows.Media.VisualTreeHelper.GetChild(target, 0);
        // MouseUp bubbles; on its way every element raises its own MouseRightButtonUp, as for a real click.
        _scriptMenu = null;
        ((UIElement)target).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.MouseUpEvent });
        return _scriptMenu;
    }

    private ContextMenu? _scriptMenu;

    /// <summary>For the UI script runner: the menu as a right-click on the part with that key (or on nothing) opens it.</summary>
    public ContextMenu ScriptPartsMenu(string? key)
    {
        var at = key == null ? null : BuiltInParts().Concat(_extraViews.Values).FirstOrDefault(e => e.Tag as string == key || _extraViews.Any(x => x.Value == e && KeyOf(x.Key) == key));
        _scriptMenu = PartsMenu(at == null ? null : PartAt(at));
        _scriptMenu.PlacementTarget = this;
        _scriptMenu.Placement = PlacementMode.Relative;
        _scriptMenu.HorizontalOffset = 300;
        _scriptMenu.VerticalOffset = 120;
        _scriptMenu.IsOpen = true;
        return _scriptMenu;
    }

    private void OnChartsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is Panel p) StatsLayout.Arrange(p);
    }

    /// <summary>For the UI script runner: the ids of the built-in parts that are shown.</summary>
    public IEnumerable<string> ScriptVisibleParts() =>
        Kpis.Children.OfType<FrameworkElement>().Concat(Charts.Children.OfType<FrameworkElement>()).Append(SpikesCard).Append(ProfilerCard)
            .Where(e => e.Tag is string && e.Visibility == Visibility.Visible).Select(e => (string)e.Tag)
            .Concat(_extraViews.Select(x => $"extra {x.Key.Id} ({x.Key.Kind}) in {(x.Value.Parent as FrameworkElement)?.Name} {x.Value.ActualWidth:F0}x{x.Value.ActualHeight:F0}"));

    private void UpdateBudget()
    {
        var tr = _vm.Stats.TickRate;
        if (double.IsNaN(tr) || tr <= 0) return;
        FrameChart.Reference = 1000.0 / tr;
        FrameChart.ReferenceLabel = $"tick {1000.0 / tr:F1} ms";
        FpsChart.Reference = tr;
        FpsChart.ReferenceLabel = $"tick rate {tr:F0}";
    }

    private void UpdateNote()
    {
        Note.Text = _vm.BridgeConnected
            ? "Numbers from the companion addon inside the server, one sample per second."
            : "Only process numbers (CPU, memory) until the companion addon connects.";
    }

    /// <summary>
    /// Biggest first. The order follows the numbers as they change (live sorting) but holds still while
    /// the pointer is over the table, so the row being read does not move away.
    /// </summary>
    private static void SetupProfileGrid(DataGrid grid, string sortBy)
    {
        grid.Items.SortDescriptions.Add(new SortDescription(sortBy, ListSortDirection.Descending));
        foreach (var col in grid.Columns)
            if (col.SortMemberPath == sortBy) col.SortDirection = ListSortDirection.Descending;
        if (grid.Items is ICollectionViewLiveShaping { CanChangeLiveSorting: true } live)
        {
            live.IsLiveSorting = true;
            grid.MouseEnter += (_, _) => live.IsLiveSorting = false;
            grid.MouseLeave += (_, _) => live.IsLiveSorting = true;
        }
    }

    private void UpdateProfiler()
    {
        var s = _vm.Stats;
        bool on = s.Profiling;
        // The last results stay after stopping, until the next start.
        bool show = on || s.HasProfile;
        ProfTables.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProfExtras.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProfLabel.Text = on ? "Stop profiling" : "Start profiling";
        ProfIcon.Text = on ? "\uE71A" : "\uE768";
        ProfButton.SetResourceReference(StyleProperty, on ? "Btn.Danger" : "Btn.Primary");
        // Whose profiler it is: an addon's, with its own description.
        ProfTitle.Text = s.ProviderName ?? "Lua profiler";
        ProfAddon.Visibility = s.ProviderName != null ? Visibility.Visible : Visibility.Collapsed;
        var about = s.ProviderName != null
            ? s.ProviderInfo ?? $"{s.ProviderName} comes from a server addon. The built-in profiler is off while it is set."
            : "Times every hook, timer and net message handler and counts outgoing net messages and entities. Costs 1-3 µs per call while it runs, so switch it off when done.";
        ProfInfo.Text = show && !string.IsNullOrEmpty(s.ProfilingInfo) ? s.ProfilingInfo : about;
    }

    /// <summary>Columns no row has a number for are left out (the max of a profiler that does not measure it, KB/s of the built-in one).</summary>
    private void UpdateColumns()
    {
        var s = _vm.Stats;
        foreach (var (grid, table) in new[] { (HooksGrid, s.Hooks), (TimersGrid, s.Timers), (NetInGrid, s.NetReceive), (NetOutGrid, s.NetSend) })
        {
            foreach (var col in grid.Columns)
            {
                var field = col.SortMemberPath switch
                {
                    nameof(ProfileRow.MsPerSec) => "ms",
                    nameof(ProfileRow.CallsPerSec) => "n",
                    nameof(ProfileRow.MaxMs) => "max",
                    nameof(ProfileRow.BytesPerSec) => "b",
                    nameof(ProfileRow.KbPerSec) => "kb",
                    _ => null,
                };
                if (field == null) continue;
                // Before the first numbers: the columns of the built-in profiler.
                bool has = s.HasField(table, field) || field != "kb" && !table.Any();
                col.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>For the UI script runner: the profiler card's title and the columns of its tables that are shown.</summary>
    public IEnumerable<string> ScriptProfiler()
    {
        yield return $"title '{ProfTitle.Text}' addon pill {ProfAddon.Visibility} info '{ProfInfo.Text}'";
        foreach (var g in new[] { HooksGrid, TimersGrid, NetInGrid, NetOutGrid })
            yield return g.Name + ": " + string.Join(", ", g.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header));
        yield return "spikes: " + SpikesInfo.Text;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGrid, object> _extraBound = new();

    private void OnExtraTableLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid && grid.DataContext is TableWidgetVm vm && _extraBound.TryAdd(grid, vm)) LiveTable.Attach(grid, vm);
    }

    private void OnProfile(object sender, RoutedEventArgs e) => _vm.SetProfiling(!_vm.Stats.Profiling);

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        _vm.SetCapture(CaptureButton.IsChecked == true);
        // Refused (no addon): the button goes back to what the capture is.
        UpdateCapture();
    }

    private void OnClearSpikes(object sender, RoutedEventArgs e) => _vm.Stats.Spikes.Clear();

    private void UpdateCapture()
    {
        var s = _vm.Stats;
        CaptureButton.IsChecked = s.Capture;
        CaptureLabel.Text = s.Capture ? "Detailed capture: on" : "Detailed capture";
        double tick = double.IsNaN(s.TickRate) || s.TickRate <= 0 ? 33 : s.TickRate;
        double limit = Math.Max(3000 / tick, 50);
        var longer = 3000 / tick >= 50 ? $"Frames longer than {limit:F0} ms (3 ticks)" : $"Frames longer than {limit:F0} ms";
        if (s.ProviderName != null)
        {
            // An addon's profiler has the capture.
            var engine = s.ProviderVprof ? " and the engine's profile (vprof)" : "";
            SpikesInfo.Text = s.Capture
                ? $"{longer}. Detailed capture by {s.ProviderName} is on" + (s.ProviderCapture ? $": each long frame gets what it measured{engine}." : $"{(engine.Length > 0 ? ": the engine's profile (vprof)" : "")}.")
                : $"{longer}, newest first, with what they were made of. Detailed capture is done by {s.ProviderName} (a server addon). Double-click a Lua name to open its file.";
            return;
        }
        SpikesInfo.Text = s.Capture
            ? longer + ". Detailed capture is on: each long frame names its slowest hooks, timers and net messages (Lua) and what the engine did in it (vprof; the engine reports one long frame a second at most). It costs 1-3 µs per Lua call: switch it off when done."
            : longer + ", newest first, with what they were made of: CPU time, Lua collector, physics, entities, players joining, the slowest timer. Detailed capture names the hooks, timers and net messages of each long frame and adds the engine's profile (vprof). Double-click a Lua name to open its file.";
    }

    private void OnWindow(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && int.TryParse(tag, out var minutes)) SetWindow(minutes);
    }

    private void SetWindow(int minutes)
    {
        minutes = minutes is 1 or 5 or 15 or 60 ? minutes : 5;
        _vm.Settings.StatsWindowMinutes = minutes;
        W1.IsChecked = minutes == 1;
        W5.IsChecked = minutes == 5;
        W15.IsChecked = minutes == 15;
        W60.IsChecked = minutes == 60;
        foreach (var c in _charts) c.WindowSeconds = minutes * 60;
        foreach (var c in _extraCharts.Values) c.WindowSeconds = minutes * 60;
    }
}
