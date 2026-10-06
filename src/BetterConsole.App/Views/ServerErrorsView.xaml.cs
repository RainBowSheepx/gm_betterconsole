using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

public partial class ServerErrorsView : UserControl
{
    private readonly ServerViewModel _vm;
    private readonly ICollectionView _view;

    public ServerErrorsView(ServerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(vm.ServerErrors.Items);
        List.ItemsSource = _view;
        vm.ServerErrors.Items.CollectionChanged += (_, _) => Refresh();
        vm.ServerErrors.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ServerErrorsVm.TotalCount)) Refresh(); };
        Refresh();
    }

    private void Refresh()
    {
        var s = _vm.ServerErrors;
        Empty.Visibility = s.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = s.Items.Count == 0 ? "" : $"{s.Items.Count} distinct · {s.TotalCount:N0} total";
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var f = Filter.Text.Trim();
        _view.Filter = f.Length == 0 ? null : o => o is ErrorEntryVm x && x.Matches(f);
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        foreach (var x in _view.OfType<ErrorEntryVm>()) x.IsExpanded = true;
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        foreach (var x in _vm.ServerErrors.Items) x.IsExpanded = false;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var x in _view.OfType<ErrorEntryVm>()) sb.AppendLine(x.ToClipboardText());
        try { Clipboard.SetText(sb.ToString()); } catch { }
        _vm.Notify("Errors copied to the clipboard.", BetterConsole.Sdk.NotifyKind.Success);
    }

    private void OnClear(object sender, RoutedEventArgs e) => _vm.ServerErrors.Clear();
}
