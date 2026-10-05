using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Controls;

/// <summary>
/// A Lua error: count, time, message; click to show the stack trace. Text can be selected with the
/// mouse: a click that selected something (or dragged) does not toggle the card.
/// </summary>
public partial class ErrorCard : UserControl
{
    private Point _downAt;
    private ErrorEntryVm? _vm;

    public ErrorCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ErrorEntryVm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmChanged;
            ApplyExpanded(animate: false);
            if (_vm.IsFresh) _vm.IsFresh = false;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ErrorEntryVm.IsExpanded)) ApplyExpanded(animate: true);
        else if (e.PropertyName == nameof(ErrorEntryVm.IsFresh) && _vm is { IsFresh: true })
        {
            _vm.IsFresh = false;
            var anim = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new QuadraticEase() };
            Flash.BeginAnimation(OpacityProperty, anim);
        }
    }

    private void ApplyExpanded(bool animate)
    {
        bool open = _vm?.IsExpanded == true;
        Details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        double angle = open ? 90 : 0;
        if (animate) ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(120)));
        else
        {
            ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            ChevronRotate.Angle = angle;
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e) => _downAt = e.GetPosition(this);

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (IsInside(e.OriginalSource as DependencyObject, CopyButton)) return;
        var p = e.GetPosition(this);
        if ((p - _downAt).Length > 4) return;
        if (FindTextBox(e.OriginalSource as DependencyObject) is { SelectionLength: > 0 }) return;
        _vm.IsExpanded = !_vm.IsExpanded;
    }

    private static bool IsInside(DependencyObject? node, DependencyObject target)
    {
        for (var cur = node; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (cur == target) return true;
        return false;
    }

    private static TextBox? FindTextBox(DependencyObject? node)
    {
        for (var cur = node; cur != null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is TextBox tb) return tb;
        return null;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        try { Clipboard.SetText(_vm.ToClipboardText()); } catch { }
    }
}
