using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BetterConsole.App.Controls;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

public partial class ClientErrorsView : UserControl
{
    private readonly ServerViewModel _vm;
    private readonly ICollectionView _view;

    public ClientErrorsView(ServerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(vm.ClientErrors.Players);
        List.ItemsSource = _view;
        vm.ClientErrors.Players.CollectionChanged += OnPlayersChanged;
        vm.ClientErrors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClientErrorsVm.TotalCount)) Refresh();
        };
        Refresh();
    }

    private void OnPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (PlayerErrorsVm p in e.NewItems)
            {
                p.Entries.CollectionChanged += (_, ce) =>
                {
                    // Moving a card to the top keeps the height: the anchor has to be restored explicitly.
                    if (ce.Action == NotifyCollectionChangedAction.Move) ScrollAnchor.RequestCorrection(Scroller);
                };
                p.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(PlayerErrorsVm.PendingMoves)) UpdateFrozenBar(); };
            }
        Refresh();
    }

    private void Refresh()
    {
        var s = _vm.ClientErrors;
        Empty.Visibility = s.Players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = s.Players.Count == 0 ? "" : $"{s.Players.Count} player{(s.Players.Count == 1 ? "" : "s")} · {s.TotalCount:N0} errors";
    }

    private void UpdateFrozenBar()
    {
        bool show = _vm.ClientErrors.IsFrozen && _vm.ClientErrors.Players.Any(p => p.PendingMoves > 0);
        FrozenBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPointerEnter(object sender, MouseEventArgs e) => _vm.ClientErrors.IsFrozen = true;

    private void OnPointerLeave(object sender, MouseEventArgs e)
    {
        _vm.ClientErrors.IsFrozen = false;
        UpdateFrozenBar();
    }

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlayerErrorsVm p) return;
        // A click that selected the player's name or SteamID does not toggle.
        if (e.OriginalSource is DependencyObject d && FindTextBox(d) is { SelectionLength: > 0 }) return;
        p.IsExpanded = !p.IsExpanded;
        if (p.IsExpanded) p.HasUnseen = false;
    }

    private static TextBox? FindTextBox(DependencyObject node)
    {
        for (DependencyObject? cur = node; cur != null; cur = TreeWalk.Parent(cur))
            if (cur is TextBox tb) return tb;
        return null;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var f = Filter.Text.Trim();
        _view.Filter = f.Length == 0 ? null : o => o is PlayerErrorsVm p &&
            (p.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || p.SteamId.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Entries.Any(x => x.Matches(f)));
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        foreach (var p in _vm.ClientErrors.Players) p.IsExpanded = false;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var p in _view.OfType<PlayerErrorsVm>())
        {
            sb.AppendLine($"== {p.Name} ({p.SteamId}) — {p.TotalCount} errors");
            foreach (var x in p.Entries) sb.AppendLine(x.ToClipboardText());
        }
        try { Clipboard.SetText(sb.ToString()); } catch { }
        _vm.Notify("Client errors copied to the clipboard.", BetterConsole.Sdk.NotifyKind.Success);
    }

    private void OnClear(object sender, RoutedEventArgs e) => _vm.ClientErrors.Clear();
}
