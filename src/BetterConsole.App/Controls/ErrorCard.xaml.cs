using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Controls;

/// <summary>
/// A Lua error: count, time, message; the arrow shows the stack trace. The text can be selected and
/// copied freely (a click into it does not fold the card); Lua paths open in the editor with a double
/// click (see <see cref="LuaLinks"/>).
/// </summary>
public partial class ErrorCard : UserControl
{
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
        Toggle.ToolTip = open ? "Hide the stack trace" : "Stack trace and details";
        double angle = open ? 90 : 0;
        if (animate) ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(120)));
        else
        {
            ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            ChevronRotate.Angle = angle;
        }
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.IsExpanded = !_vm.IsExpanded;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        try { Clipboard.SetText(_vm.ToClipboardText()); } catch { }
    }
}
