using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BetterConsole.App.Controls;
using BetterConsole.App.ViewModels;
using BetterConsole.Sdk;

namespace BetterConsole.App.Views;

/// <summary>
/// Players on the server: a sortable table (default: time on the server, newest at the bottom),
/// columns can be hidden, right-click for admin actions on the selected players (ULX when installed, engine commands otherwise).
/// </summary>
public partial class PlayersView : UserControl
{
    private readonly ServerViewModel _vm;
    private readonly ListCollectionView _view;

    public PlayersView(ServerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(vm.Players.Rows);
        _view.IsLiveSorting = true;
        Grid.ItemsSource = _view;

        foreach (var col in Grid.Columns) _defaultWidths[col] = col.Width;
        RestoreColumns();
        ApplySort(vm.Settings.PlayerSortColumn, vm.Settings.PlayerSortDescending);

        // Width and order go into the settings when BetterConsole saves them (on exit), not on every drag.
        vm.SavingSettings += StoreColumns;
        Grid.ColumnReordered += (_, _) => StoreColumns();

        Grid.Loaded += (_, _) => HookHeaderMenu();
        vm.Players.PropertyChanged += (_, _) => Refresh();
        vm.Players.Rows.CollectionChanged += (_, _) => Refresh();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ServerViewModel.BridgeConnected)) Refresh(); };
        Refresh();
    }

    private void Refresh()
    {
        var p = _vm.Players;
        CountText.Text = p.HeaderText;
        Empty.Visibility = p.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_vm.BridgeConnected)
        {
            EmptyTitle.Text = _vm.IsRunning ? "Waiting for the companion addon" : "The server is not running";
            EmptyText.Text = "The player list comes from the companion addon inside the server.";
        }
        else
        {
            EmptyTitle.Text = "Nobody is playing";
            EmptyText.Text = "Players appear here as they join. Right-click a player for admin actions.";
        }
        Hint.Text = p.LoadMeasured ? "Load is measured while this tab is open" : "";
        UlxText.Text = p.HasUlx ? "ULX" : "No ULX: engine commands";
        UlxText.SetResourceReference(TextBlock.ForegroundProperty, p.HasUlx ? "Brush.Success" : "Brush.TextMuted");
        UlxPill.Visibility = p.HasData ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
        UlxPill.ToolTip = p.HasUlx
            ? "Kick, ban, groups, gag, mute and jail use ULX commands."
            : "ULX is not installed: kick and ban use kickid / banid, groups are set until the player leaves, gag / mute / jail are not available.";
    }

    // ------------------------------------------------------------------------------ sorting

    private void ApplySort(string columnHeader, bool descending)
    {
        var col = Grid.Columns.FirstOrDefault(c => (c.Header as string) == columnHeader) ?? Grid.Columns.First(c => (c.Header as string) == "Time");
        foreach (var c in Grid.Columns) c.SortDirection = null;
        var dir = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        col.SortDirection = dir;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(col.SortMemberPath, dir));
            // The Group column ranks by hierarchy; groups of the same rank by name.
            if (col.SortMemberPath == nameof(PlayerRowVm.GroupRank)) _view.SortDescriptions.Add(new SortDescription(nameof(PlayerRowVm.Group), ListSortDirection.Ascending));
            // Ties (same time, same ping...) always in the same order.
            _view.SortDescriptions.Add(new SortDescription(nameof(PlayerRowVm.UserId), ListSortDirection.Ascending));
            _view.LiveSortingProperties.Clear();
            _view.LiveSortingProperties.Add(col.SortMemberPath);
        }
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var header = e.Column.Header as string ?? "Time";
        // First click on a numeric column (and Group: superadmin first) sorts the biggest first; "Nick", "SteamID" A-Z.
        bool textual = header is "Nick" or "SteamID" or "IP" or "Team";
        bool desc = e.Column.SortDirection switch
        {
            ListSortDirection.Descending => false,
            ListSortDirection.Ascending => true,
            _ => !textual,
        };
        ApplySort(header, desc);
        _vm.Settings.PlayerSortColumn = header;
        _vm.Settings.PlayerSortDescending = desc;
    }

    // ------------------------------------------------------------------------------ columns

    private readonly Dictionary<DataGridColumn, DataGridLength> _defaultWidths = new();
    private static readonly DataGridLengthConverter Sizes = new();

    /// <summary>"150", "2.4*", "Auto" back into a width; null when it is not one (a hand-edited file).</summary>
    private static DataGridLength? ParseWidth(string text)
    {
        try
        {
            return Sizes.ConvertFromInvariantString(text) is DataGridLength l && (l.IsAuto || l.IsStar || l.IsAbsolute && l.Value is >= 20 and < 3000) ? l : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NameOf(DataGridColumn col) => col.Header as string ?? "";

    /// <summary>Visibility, width and order of the columns as they were left.</summary>
    private void RestoreColumns()
    {
        var s = _vm.Settings;
        foreach (var col in Grid.Columns)
        {
            var name = NameOf(col);
            // A column this settings file has not seen yet (a newer version): hidden if it is by default.
            if (!s.PlayerColumnsKnown.Contains(name))
            {
                if (Services.AppSettings.DefaultHiddenColumns.Contains(name) && !s.PlayerColumnsHidden.Contains(name)) s.PlayerColumnsHidden.Add(name);
                s.PlayerColumnsKnown.Add(name);
            }
            col.Visibility = s.PlayerColumnsHidden.Contains(name) ? Visibility.Collapsed : Visibility.Visible;
            col.Width = s.PlayerColumnSizes.TryGetValue(name, out var w) && ParseWidth(w) is { } width ? width : _defaultWidths[col];
        }
        var order = s.PlayerColumnOrder.Distinct().Select(n => Grid.Columns.FirstOrDefault(c => NameOf(c) == n)).OfType<DataGridColumn>().ToList();
        // Columns the saved order does not know (a new version added them) go after the others; none saved: the default order.
        order.AddRange(Grid.Columns.Where(c => !order.Contains(c)));
        for (int i = 0; i < order.Count; i++) order[i].DisplayIndex = i;
    }

    /// <summary>Into the settings (written when BetterConsole closes, like the other view settings).</summary>
    private void StoreColumns()
    {
        var s = _vm.Settings;
        // Only the widths the user changed; the others keep sizing themselves.
        s.PlayerColumnSizes = Grid.Columns.Where(c => !c.Width.Equals(_defaultWidths[c])).ToDictionary(NameOf, c => Sizes.ConvertToInvariantString(c.Width) ?? "Auto");
        s.PlayerColumnOrder = Grid.Columns.OrderBy(c => c.DisplayIndex).Select(NameOf).ToList();
    }

    private void ResetColumns()
    {
        var s = _vm.Settings;
        s.PlayerColumnsHidden = new Services.AppSettings().PlayerColumnsHidden;
        s.PlayerColumnSizes = new();
        s.PlayerColumnOrder = new();
        RestoreColumns();
    }

    private void HookHeaderMenu()
    {
        var presenter = FindChild<DataGridColumnHeadersPresenter>(Grid);
        if (presenter != null) presenter.ContextMenu = BuildColumnsMenu();
    }

    private ContextMenu BuildColumnsMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            var header = new MenuItem { Header = "Columns", IsEnabled = false };
            menu.Items.Add(header);
            foreach (var col in Grid.Columns)
            {
                var name = col.Header as string ?? "?";
                var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = col.Visibility == Visibility.Visible, StaysOpenOnClick = true };
                item.Click += (_, _) =>
                {
                    if (!item.IsChecked && Grid.Columns.Count(c => c.Visibility == Visibility.Visible) <= 1)
                    {
                        item.IsChecked = true;
                        return;
                    }
                    col.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                    var hidden = _vm.Settings.PlayerColumnsHidden;
                    hidden.Remove(name);
                    if (!item.IsChecked) hidden.Add(name);
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var reset = new MenuItem { Header = "Reset columns (visibility, width, order)" };
            reset.Click += (_, _) => ResetColumns();
            menu.Items.Add(reset);
        };
        return menu;
    }

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        var menu = BuildColumnsMenu();
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    // ------------------------------------------------------------------------------ selection

    /// <summary>The selected players in the table's order.</summary>
    private List<PlayerRowVm> Selected()
    {
        var set = Grid.SelectedItems.OfType<PlayerRowVm>().ToHashSet();
        return Grid.Items.OfType<PlayerRowVm>().Where(set.Contains).ToList();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        int n = Grid.SelectedItems.Count, all = Grid.Items.Count;
        SelectionText.Text = n > 1 ? $"{n} selected" : "";
        SelectionText.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllLabel.Text = all > 0 && n == all ? "Clear selection" : "Select all";
        SelectAllButton.IsEnabled = all > 0;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (Grid.Items.Count > 0 && Grid.SelectedItems.Count == Grid.Items.Count) Grid.UnselectAll();
        else Grid.SelectAll();
        Grid.Focus();
    }

    // ------------------------------------------------------------------------------ admin actions

    // Every action works on the selection: a right-click on a selected row keeps it, anywhere else selects that row alone.
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(Grid, (DependencyObject)e.OriginalSource) as DataGridRow;
        if (row?.Item is not PlayerRowVm p) return;
        if (!row.IsSelected) Grid.SelectedItem = p;
        var menu = BuildPlayerMenu(Selected());
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu? _scriptMenu;
    public ContextMenu? ScriptMenu => _scriptMenu;

    /// <summary>For the UI script runner: open the menu of the n-th row (of the selection, when that row is in it).</summary>
    public void ScriptOpenMenu(int index)
    {
        _scriptMenu = null;
        if (index < 0 || index >= Grid.Items.Count || Grid.Items[index] is not PlayerRowVm p) return;
        if (!Grid.SelectedItems.Contains(p)) Grid.SelectedItem = p;
        var row = (DataGridRow?)Grid.ItemContainerGenerator.ContainerFromIndex(index);
        var menu = BuildPlayerMenu(Selected());
        menu.PlacementTarget = row ?? (UIElement)Grid;
        menu.Placement = PlacementMode.Relative;
        menu.HorizontalOffset = 180;
        menu.VerticalOffset = 18;
        menu.IsOpen = true;
        _scriptMenu = menu;
    }

    /// <summary>For the UI script runner: "all", "none" or row numbers ("0,2,3").</summary>
    public void ScriptSelect(string spec)
    {
        if (spec == "all") Grid.SelectAll();
        else
        {
            Grid.UnselectAll();
            if (spec != "none")
                foreach (var s in spec.Split(','))
                    if (int.TryParse(s, out var i) && i >= 0 && i < Grid.Items.Count) Grid.SelectedItems.Add(Grid.Items[i]);
        }
        Grid.Focus();
    }

    /// <summary>For the UI script runner: click the item of the open menu whose header starts with the text.</summary>
    public void ScriptMenuClick(string text)
    {
        if (_scriptMenu == null) return;
        var item = _scriptMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header is string h && h.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        _scriptMenu.IsOpen = false;
        _scriptMenu = null;
        item?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    /// <summary>For the UI script runner: the dialog an action would show, for the selection (not modal; nothing is run).</summary>
    public Window? ScriptDialog(string which)
    {
        var ps = Selected();
        if (ps.Count == 0) return null;
        if (_vm.PlayerActions.FirstOrDefault(a => a.Id == which) is { } action) return ActionDialog(action, ps.Where(action.AppliesTo).ToList());
        return which switch
        {
            "kick" => KickDialog(ps),
            "ban" => BanDialog(ps),
            "jail" => JailDialog(ps),
            "group" => GroupDialog(ps, _vm.Players.Groups.FirstOrDefault() ?? "user"),
            _ => null,
        };
    }

    /// <summary>For the UI script runner: an item of an addon or a plugin for the selection, as if its dialog said OK with these values.</summary>
    public void ScriptRunAction(string id, Dictionary<string, string> values)
    {
        if (_vm.PlayerActions.FirstOrDefault(a => a.Id == id) is not { } action) return;
        var targets = Selected().Where(action.AppliesTo).ToList();
        if (targets.Count > 0) Execute(action, targets, values);
    }

    /// <summary>For the UI script runner: the items of the open menu ("Tag | header", separators as "---").</summary>
    public IEnumerable<string> ScriptMenuItems() =>
        _scriptMenu?.Items.Cast<object>().Select(i => i is MenuItem m ? $"{m.Tag} | {m.Header}{(m.IsEnabled ? "" : " (disabled)")}" : "---") ?? [];

    /// <summary>
    /// The menu for the selected players. Every item has an order: the built-in ones 100-120 (kick, ban, group),
    /// 200-221 (gag, mute, jail) and 400-430 (copy, profile); items of addons and plugins go in by theirs, and a
    /// separator goes between hundreds. Addons can hide built-in items; plugins change the finished menu.
    /// </summary>
    private ContextMenu BuildPlayerMenu(IReadOnlyList<PlayerRowVm> ps)
    {
        bool ulx = _vm.Players.HasUlx;
        var humans = ps.Where(x => !x.IsBot).ToList();
        var entries = new List<(int Order, MenuItem Item)>();
        void Add(int order, string id, MenuItem item)
        {
            if (_vm.PlayerActionsHidden.Contains(id)) return;
            item.Tag = id;
            entries.Add((order, item));
        }

        Add(100, "kick", Item("Kick…", "", () => Kick(ps)));
        // Without ULX a ban is by SteamID: bots have none.
        Add(110, "ban", Item("Ban…", "", () => Ban(ps), enabled: humans.Count > 0 || ulx));
        var groups = Item("Set group", "", null);
        foreach (var g in _vm.Players.Groups)
        {
            var gi = new MenuItem { Header = g, IsCheckable = true, IsChecked = humans.Count > 0 && humans.All(x => SameGroup(x, g)) };
            gi.Click += (_, _) => SetGroup(humans, g);
            groups.Items.Add(gi);
        }
        groups.IsEnabled = humans.Count > 0;
        Add(120, "group", groups);
        foreach (var (order, item) in Toggle(ps, x => x.Gagged, "Gag (voice)", "Ungag (voice)", "", x => $"ulx gag {x.UlxTarget}", x => $"ulx ungag {x.UlxTarget}", ulx))
            Add(200 + order, "gag", item);
        foreach (var (order, item) in Toggle(ps, x => x.Muted, "Mute (chat)", "Unmute (chat)", "", x => $"ulx mute {x.UlxTarget}", x => $"ulx unmute {x.UlxTarget}", ulx))
            Add(210 + order, "mute", item);
        var free = ps.Where(x => !x.Jailed).ToList();
        var jailed = ps.Where(x => x.Jailed).ToList();
        if (free.Count > 0) Add(220, "jail", Item(Part("Jail…", free, ps), "", () => Jail(free), enabled: ulx));
        if (jailed.Count > 0) Add(221, "jail", Item(Part("Unjail", jailed, ps), "", () => RunAll(jailed.Select(x => $"ulx unjail {x.UlxTarget}")), enabled: ulx));
        // Several at once: separated by spaces.
        Add(400, "copy", Item(ps.Count == 1 ? "Copy name" : "Copy names", "", () => Copy(string.Join(" ", ps.Select(x => x.Name)))));
        Add(410, "copy", Item(humans.Count > 1 ? "Copy SteamIDs" : "Copy SteamID", "", () => Copy(string.Join(" ", humans.Select(x => x.SteamId))), enabled: humans.Count > 0));
        var sid64 = humans.Where(x => x.SteamId64.Length > 0).ToList();
        Add(420, "copy", Item(sid64.Count > 1 ? "Copy SteamID64s" : "Copy SteamID64", "", () => Copy(string.Join(" ", sid64.Select(x => x.SteamId64))), enabled: sid64.Count > 0));
        var profiles = ps.Where(x => x.ProfileUrl.Length > 0).ToList();
        Add(430, "profile", Item(profiles.Count > 1 ? "Open Steam profiles" : "Open Steam profile", "", () => OpenProfiles(profiles), enabled: profiles.Count > 0));

        // Items of addons and plugins: for the selected players they apply to (none: not shown).
        foreach (var a in _vm.PlayerActions.ToList())
        {
            var targets = ps.Where(a.AppliesTo).ToList();
            if (targets.Count == 0) continue;
            // multi = false: only while one player is selected.
            bool one = a.Multi || ps.Count == 1;
            var item = Item(Part(a.Text + (a.Fields.Count > 0 ? "…" : ""), targets, ps), a.Icon, () => RunAction(a, targets), enabled: one);
            if (!one)
            {
                item.ToolTip = "For one player at a time";
                ToolTipService.SetShowOnDisabled(item, true);
            }
            item.Tag = a.Id;
            entries.Add((a.Order, item));
        }

        var menu = new ContextMenu();
        var title = new MenuItem { Header = ps.Count == 1 ? ps[0].Name : $"{ps.Count} players", IsEnabled = false, FontWeight = FontWeights.SemiBold };
        if (ps.Count == 1 && ps[0].Avatar is { } av)
            title.Icon = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = new ImageBrush(av) { Stretch = Stretch.UniformToFill } };
        else if (ps.Count > 1)
            title.Icon = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["Font.Icons"], FontSize = 14 };
        menu.Items.Add(title);
        int? section = null;
        // OrderBy keeps the built-in order among equal numbers.
        foreach (var (order, item) in entries.OrderBy(e => e.Order))
        {
            int s = (int)Math.Floor(order / 100.0);
            if (section != s) menu.Items.Add(new Separator());
            section = s;
            menu.Items.Add(item);
        }
        _vm.RaisePlayerMenuOpening(ps, menu);
        TidySeparators(menu);
        return menu;
    }

    /// <summary>No separator at an end or next to another (after items were hidden or a plugin removed some).</summary>
    private static void TidySeparators(ContextMenu menu)
    {
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not Separator) continue;
            bool last = i == menu.Items.Count - 1;
            bool doubled = i + 1 < menu.Items.Count && menu.Items[i + 1] is Separator;
            if (last || doubled || i == 0) menu.Items.RemoveAt(i);
        }
    }

    /// <summary>"Gag (voice)" for all of them, or, when some are gagged and some not, both items for their part (order 0 and 1).</summary>
    private IEnumerable<(int, MenuItem)> Toggle(IReadOnlyList<PlayerRowVm> ps, Func<PlayerRowVm, bool> isOn, string onText, string offText, string icon,
        Func<PlayerRowVm, string> onCmd, Func<PlayerRowVm, string> offCmd, bool enabled)
    {
        var off = ps.Where(x => !isOn(x)).ToList();
        var on = ps.Where(isOn).ToList();
        if (off.Count > 0) yield return (0, Item(Part(onText, off, ps), icon, () => RunAll(off.Select(onCmd)), enabled));
        if (on.Count > 0) yield return (1, Item(Part(offText, on, ps), icon, () => RunAll(on.Select(offCmd)), enabled));
    }

    /// <summary>An item of an addon or a plugin: its dialog (fields, a question), then its command, its Lua or the plugin.</summary>
    private void RunAction(PlayerActionDef a, IReadOnlyList<PlayerRowVm> targets)
    {
        if (ActionDialog(a, targets) is { } d)
        {
            if (d.ShowDialog() != true) return;
            Execute(a, targets, a.Fields.ToDictionary(f => f.Id, f => d[f.Id]));
        }
        else Execute(a, targets, new Dictionary<string, string>());
    }

    private void Execute(PlayerActionDef a, IReadOnlyList<PlayerRowVm> targets, Dictionary<string, string> values)
    {
        var typed = new Dictionary<string, object>();
        foreach (var f in a.Fields)
        {
            var v = values.GetValueOrDefault(f.Id, f.Default);
            if (f.Number)
            {
                // "NaN", "Infinity", "1e400" parse as numbers, but are none (and JSON cannot carry them).
                if (!double.TryParse(v.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n))
                {
                    _vm.Notify($"{f.Label}: a number is needed.", NotifyKind.Warning);
                    return;
                }
                v = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                typed[f.Id] = n;
            }
            else typed[f.Id] = v;
            values[f.Id] = v;
        }
        // A plugin's code, else the addon's onRun (it wins over a command), else the command.
        if (a.PluginRun != null) a.PluginRun(targets.Select(t => t.ToInfo()).ToList());
        else if (a.LuaRun)
        {
            _vm.Request("paction", new { id = a.LuaId, uids = targets.Select(t => t.UserId).ToArray(), values = typed });
            _vm.Notify($"› {a.Text}: {Who(targets)}", NotifyKind.Info);
        }
        else if (a.Command != null)
            RunAll(a.PerPlayer ? targets.Select(t => PlayerActionDef.Expand(a.Command, t, values)) : [PlayerActionDef.Expand(a.Command, null, values)]);
    }

    /// <summary>The dialog of an action with fields or a question, else null.</summary>
    private PromptDialog? ActionDialog(PlayerActionDef a, IReadOnlyList<PlayerRowVm> targets)
    {
        if ((a.Fields.Count == 0 && a.Confirm == null) || targets.Count == 0) return null;
        var question = a.Confirm?.Replace("{players}", Who(targets)).Replace("{name}", targets[0].Name).Replace("{count}", targets.Count.ToString());
        var d = new PromptDialog(Owner, targets.Count == 1 ? a.Text : $"{a.Text} · {targets.Count} players", question, a.Text, a.Danger).Players(Faces(targets));
        foreach (var f in a.Fields)
        {
            if (f.Choices is { Count: > 0 } choices) d.Choice(f.Id, f.Label, choices, f.Default, f.Editable);
            else d.Text(f.Id, f.Label, f.Default);
        }
        return d;
    }

    private static string Part(string text, IReadOnlyCollection<PlayerRowVm> part, IReadOnlyCollection<PlayerRowVm> all) =>
        part.Count == all.Count ? text : $"{text} · {part.Count} of {all.Count}";

    private static bool SameGroup(PlayerRowVm p, string group) => string.Equals(group, p.Group, StringComparison.OrdinalIgnoreCase);

    private static MenuItem Item(string header, string icon, Action? onClick, bool enabled = true)
    {
        var mi = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
            Icon = new TextBlock { Text = icon, FontFamily = (FontFamily)Application.Current.Resources["Font.Icons"], FontSize = 13 },
        };
        if (onClick != null) mi.Click += (_, _) => onClick();
        return mi;
    }

    private Window? Owner => Window.GetWindow(this);

    /// <summary>The commands one after another; one notice for all of them.</summary>
    private void RunAll(IEnumerable<string> commands)
    {
        var list = commands.ToList();
        int sent = 0;
        foreach (var c in list)
        {
            if (!_vm.SendCommand(c)) break;
            sent++;
        }
        if (sent > 0) _vm.Notify("› " + list[0] + (sent > 1 ? $"  (+{sent - 1} more)" : ""), NotifyKind.Info);
    }

    private static string Clean(string s) => s.Replace("\"", "'").Replace(";", ",").Replace("\n", " ").Trim();

    private void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    private void OpenProfiles(IReadOnlyList<PlayerRowVm> ps)
    {
        if (ps.Count > 5 && !PromptDialog.Confirm(Owner, "Open Steam profiles", $"Open {ps.Count} Steam profiles in the browser?", "Open")) return;
        foreach (var p in ps)
        {
            try { Process.Start(new ProcessStartInfo(p.ProfileUrl) { UseShellExecute = true }); } catch { }
        }
    }

    private static List<(string, string, ImageSource?, bool)> Faces(IEnumerable<PlayerRowVm> ps) => ps.Select(p => (p.Name, p.SteamId, (ImageSource?)p.Avatar, p.IsBot)).ToList();

    private static string Who(IReadOnlyList<PlayerRowVm> ps) => ps.Count == 1 ? ps[0].Name : $"these {ps.Count} players";

    private PromptDialog KickDialog(IReadOnlyList<PlayerRowVm> ps) =>
        new PromptDialog(Owner, ps.Count == 1 ? "Kick player" : $"Kick {ps.Count} players", $"Disconnects {Who(ps)} from the server.", "Kick", danger: true)
            .Players(Faces(ps))
            .Text("reason", "Reason", "Kicked by the server console");

    private void Kick(IReadOnlyList<PlayerRowVm> ps)
    {
        var d = KickDialog(ps);
        if (d.ShowDialog() != true) return;
        var reason = Clean(d["reason"]);
        RunAll(ps.Select(p => _vm.Players.HasUlx ? $"ulx kick {p.UlxTarget} {reason}" : $"kickid {p.UserId} {reason}"));
    }

    private static readonly (string, string)[] BanDurations =
    [
        ("15 minutes", "15"), ("1 hour", "60"), ("6 hours", "360"), ("1 day", "1440"), ("3 days", "4320"),
        ("1 week", "10080"), ("30 days", "43200"), ("Permanent", "0"),
    ];

    // Without ULX only players with a SteamID can be banned.
    private List<PlayerRowVm> Bannable(IReadOnlyList<PlayerRowVm> ps) => _vm.Players.HasUlx ? ps.ToList() : ps.Where(p => !p.IsBot).ToList();

    private PromptDialog BanDialog(IReadOnlyList<PlayerRowVm> ps)
    {
        var targets = Bannable(ps);
        var d = new PromptDialog(Owner, targets.Count == 1 ? "Ban player" : $"Ban {targets.Count} players",
                targets.Count == 1 ? $"Bans {targets[0].Name} ({targets[0].SteamId}) and disconnects them." : $"Bans {Who(targets)} and disconnects them.", "Ban", danger: true)
            .Players(Faces(targets))
            .Choice("minutes", "Duration (minutes; you can type your own, 0 = permanent)", BanDurations, "60", editable: true)
            .Text("reason", "Reason", "Banned by the server console");
        if (!_vm.Players.HasUlx) d.Note("Without ULX the ban goes to the engine's ban list (banid + writeid)" + (targets.Count < ps.Count ? "; bots are left out." : "."));
        return d;
    }

    private void Ban(IReadOnlyList<PlayerRowVm> ps)
    {
        var targets = Bannable(ps);
        if (targets.Count == 0) return;
        var d = BanDialog(ps);
        if (d.ShowDialog() != true) return;
        if (!int.TryParse(d["minutes"].Trim(), out var minutes) || minutes < 0)
        {
            _vm.Notify("The duration must be a number of minutes.", NotifyKind.Warning);
            return;
        }
        var reason = Clean(d["reason"]);
        if (_vm.Players.HasUlx)
            RunAll(targets.Select(p => p.IsBot ? $"ulx ban {p.UlxTarget} {minutes} {reason}" : $"ulx banid \"{p.SteamId}\" {minutes} {reason}"));
        else
            RunAll(targets.Select(p => $"banid {minutes} {p.SteamId} kick").Append("writeid"));
    }

    private PromptDialog GroupDialog(IReadOnlyList<PlayerRowVm> ps, string group) =>
        new PromptDialog(Owner, "Set group", $"Put {Who(ps)} into the group \"{group}\"?", "Set group").Players(Faces(ps));

    private void SetGroup(IReadOnlyList<PlayerRowVm> humans, string group)
    {
        var ps = humans.Where(p => !SameGroup(p, group)).ToList();
        if (ps.Count == 0 || GroupDialog(ps, group).ShowDialog() != true) return;
        if (_vm.Players.HasUlx)
        {
            RunAll(ps.Select(p => group.Equals("user", StringComparison.OrdinalIgnoreCase) ? $"ulx removeuserid \"{p.SteamId}\"" : $"ulx adduserid \"{p.SteamId}\" {group}"));
        }
        else
        {
            foreach (var p in ps) _vm.Request("setgroup", new { sid = p.SteamId, uid = p.UserId, group });
            _vm.Notify($"{(ps.Count == 1 ? ps[0].Name + " is" : $"{ps.Count} players are")} now in \"{group}\" until they leave (no ULX to save it).", NotifyKind.Info);
        }
    }

    private static readonly (string, string)[] JailDurations =
    [
        ("30 seconds", "30"), ("1 minute", "60"), ("5 minutes", "300"), ("15 minutes", "900"), ("1 hour", "3600"), ("Until unjailed", "0"),
    ];

    private PromptDialog JailDialog(IReadOnlyList<PlayerRowVm> ps) =>
        new PromptDialog(Owner, ps.Count == 1 ? "Jail player" : $"Jail {ps.Count} players", $"Puts {Who(ps)} into a ULX jail where they stand.", "Jail")
            .Players(Faces(ps))
            .Choice("seconds", "Duration (seconds; 0 = until unjailed)", JailDurations, "300", editable: true);

    private void Jail(IReadOnlyList<PlayerRowVm> ps)
    {
        var d = JailDialog(ps);
        if (d.ShowDialog() != true) return;
        if (!int.TryParse(d["seconds"].Trim(), out var seconds) || seconds < 0)
        {
            _vm.Notify("The duration must be a number of seconds.", NotifyKind.Warning);
            return;
        }
        RunAll(ps.Select(p => $"ulx jail {p.UlxTarget} {seconds}"));
    }
}
