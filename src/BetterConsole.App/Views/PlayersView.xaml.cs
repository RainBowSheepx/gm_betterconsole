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
/// columns can be hidden, right-click for admin actions (ULX when installed, engine commands otherwise).
/// </summary>
public partial class PlayersView : UserControl
{
    private readonly MainViewModel _vm;
    private readonly ListCollectionView _view;

    public PlayersView(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(vm.Players.Rows);
        _view.IsLiveSorting = true;
        Grid.ItemsSource = _view;

        foreach (var col in Grid.Columns)
        {
            var name = col.Header as string ?? "";
            col.Visibility = vm.Settings.PlayerColumnsHidden.Contains(name) ? Visibility.Collapsed : Visibility.Visible;
        }
        ApplySort(vm.Settings.PlayerSortColumn, vm.Settings.PlayerSortDescending);

        Grid.Loaded += (_, _) => HookHeaderMenu();
        vm.Players.PropertyChanged += (_, _) => Refresh();
        vm.Players.Rows.CollectionChanged += (_, _) => Refresh();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.BridgeConnected)) Refresh(); };
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
        // First click on a numeric column sorts the biggest first; "Nick", "SteamID", "Group" A-Z.
        bool textual = header is "Nick" or "SteamID" or "Group" or "IP" or "Team";
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

    // ------------------------------------------------------------------------------ admin actions

    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(Grid, (DependencyObject)e.OriginalSource) as DataGridRow;
        if (row?.Item is not PlayerRowVm p) return;
        Grid.SelectedItem = p;
        var menu = BuildPlayerMenu(p);
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>For the UI script runner: open the menu of the n-th row.</summary>
    public void ScriptOpenMenu(int index)
    {
        if (index < 0 || index >= Grid.Items.Count || Grid.Items[index] is not PlayerRowVm p) return;
        Grid.SelectedItem = p;
        var row = (DataGridRow?)Grid.ItemContainerGenerator.ContainerFromIndex(index);
        var menu = BuildPlayerMenu(p);
        menu.PlacementTarget = row ?? (UIElement)Grid;
        menu.Placement = PlacementMode.Relative;
        menu.HorizontalOffset = 180;
        menu.VerticalOffset = 18;
        menu.IsOpen = true;
    }

    private ContextMenu BuildPlayerMenu(PlayerRowVm p)
    {
        bool ulx = _vm.Players.HasUlx;
        var menu = new ContextMenu();
        var title = new MenuItem { Header = p.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Kick…", "", () => Kick(p)));
        menu.Items.Add(Item("Ban…", "", () => Ban(p), enabled: !p.IsBot || ulx));
        var groups = Item("Set group", "", null);
        foreach (var g in _vm.Players.Groups)
        {
            var gi = new MenuItem { Header = g, IsCheckable = true, IsChecked = string.Equals(g, p.Group, StringComparison.OrdinalIgnoreCase) };
            gi.Click += (_, _) => SetGroup(p, g);
            groups.Items.Add(gi);
        }
        groups.IsEnabled = !p.IsBot;
        menu.Items.Add(groups);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(p.Gagged ? "Ungag (voice)" : "Gag (voice)", "", () => Run(p.Gagged ? $"ulx ungag {p.UlxTarget}" : $"ulx gag {p.UlxTarget}"), enabled: ulx));
        menu.Items.Add(Item(p.Muted ? "Unmute (chat)" : "Mute (chat)", "", () => Run(p.Muted ? $"ulx unmute {p.UlxTarget}" : $"ulx mute {p.UlxTarget}"), enabled: ulx));
        menu.Items.Add(p.Jailed
            ? Item("Unjail", "", () => Run($"ulx unjail {p.UlxTarget}"), enabled: ulx)
            : Item("Jail…", "", () => Jail(p), enabled: ulx));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy name", "", () => Copy(p.Name)));
        menu.Items.Add(Item("Copy SteamID", "", () => Copy(p.SteamId), enabled: !p.IsBot));
        menu.Items.Add(Item("Copy SteamID64", "", () => Copy(p.SteamId64), enabled: !p.IsBot && p.SteamId64.Length > 0));
        menu.Items.Add(Item("Open Steam profile", "", () => Process.Start(new ProcessStartInfo(p.ProfileUrl) { UseShellExecute = true }), enabled: p.ProfileUrl.Length > 0));
        return menu;
    }

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

    private void Run(string command)
    {
        if (_vm.SendCommand(command)) _vm.Notify("› " + command, NotifyKind.Info);
    }

    private static string Clean(string s) => s.Replace("\"", "'").Replace(";", ",").Replace("\n", " ").Trim();

    private void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    private void Kick(PlayerRowVm p)
    {
        var d = new PromptDialog(Owner, "Kick player", $"Disconnects {p.Name} from the server.", "Kick", danger: true)
            .Text("reason", "Reason", "Kicked by the server console");
        if (d.ShowDialog() != true) return;
        var reason = Clean(d["reason"]);
        Run(_vm.Players.HasUlx ? $"ulx kick {p.UlxTarget} {reason}" : $"kickid {p.UserId} {reason}");
    }

    private static readonly (string, string)[] BanDurations =
    [
        ("15 minutes", "15"), ("1 hour", "60"), ("6 hours", "360"), ("1 day", "1440"), ("3 days", "4320"),
        ("1 week", "10080"), ("30 days", "43200"), ("Permanent", "0"),
    ];

    private void Ban(PlayerRowVm p)
    {
        var d = new PromptDialog(Owner, "Ban player", $"Bans {p.Name} ({p.SteamId}) and disconnects him.", "Ban", danger: true)
            .Choice("minutes", "Duration (minutes; you can type your own, 0 = permanent)", BanDurations, "60", editable: true)
            .Text("reason", "Reason", "Banned by the server console");
        if (!_vm.Players.HasUlx) d.Note("Without ULX the ban goes to the engine's ban list (banid + writeid).");
        if (d.ShowDialog() != true) return;
        if (!int.TryParse(d["minutes"].Trim(), out var minutes) || minutes < 0)
        {
            _vm.Notify("The duration must be a number of minutes.", NotifyKind.Warning);
            return;
        }
        var reason = Clean(d["reason"]);
        if (_vm.Players.HasUlx)
        {
            Run(p.IsBot ? $"ulx ban {p.UlxTarget} {minutes} {reason}" : $"ulx banid \"{p.SteamId}\" {minutes} {reason}");
        }
        else
        {
            Run($"banid {minutes} {p.SteamId} kick");
            Run("writeid");
        }
    }

    private void SetGroup(PlayerRowVm p, string group)
    {
        if (string.Equals(group, p.Group, StringComparison.OrdinalIgnoreCase)) return;
        if (!PromptDialog.Confirm(Owner, "Set group", $"Put {p.Name} into the group \"{group}\"?", "Set group")) return;
        if (_vm.Players.HasUlx)
        {
            Run(group.Equals("user", StringComparison.OrdinalIgnoreCase) ? $"ulx removeuserid \"{p.SteamId}\"" : $"ulx adduserid \"{p.SteamId}\" {group}");
        }
        else
        {
            _vm.Request("setgroup", new { sid = p.SteamId, uid = p.UserId, group });
            _vm.Notify($"{p.Name} is now in \"{group}\" until he leaves (no ULX to save it).", NotifyKind.Info);
        }
    }

    private static readonly (string, string)[] JailDurations =
    [
        ("30 seconds", "30"), ("1 minute", "60"), ("5 minutes", "300"), ("15 minutes", "900"), ("1 hour", "3600"), ("Until unjailed", "0"),
    ];

    private void Jail(PlayerRowVm p)
    {
        var d = new PromptDialog(Owner, "Jail player", $"Puts {p.Name} into a ULX jail where he stands.", "Jail")
            .Choice("seconds", "Duration (seconds; 0 = until unjailed)", JailDurations, "300", editable: true);
        if (d.ShowDialog() != true) return;
        if (!int.TryParse(d["seconds"].Trim(), out var seconds) || seconds < 0)
        {
            _vm.Notify("The duration must be a number of seconds.", NotifyKind.Warning);
            return;
        }
        Run($"ulx jail {p.UlxTarget} {seconds}");
    }
}
