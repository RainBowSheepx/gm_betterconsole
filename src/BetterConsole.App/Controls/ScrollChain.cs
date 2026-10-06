using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterConsole.App.Controls;

/// <summary>
/// For a scrolling list inside a scrolling page: once the list is at its top or bottom, the mouse wheel
/// scrolls the page instead of doing nothing. (WPF gives the wheel to the innermost ScrollViewer only.)
/// </summary>
public static class ScrollChain
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollChain), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue) el.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject root) return;
        var inner = root as ScrollViewer ?? FindChild<ScrollViewer>(root);
        if (inner != null && inner.ScrollableHeight > 0)
        {
            bool atTop = inner.VerticalOffset <= 0.5;
            bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            if (!(e.Delta > 0 && atTop) && !(e.Delta < 0 && atBottom)) return; // the list scrolls itself
        }
        var outer = FindParent<ScrollViewer>(root as DependencyObject);
        if (outer == null) return;
        e.Handled = true;
        outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }

    private static T? FindChild<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var c = VisualTreeHelper.GetChild(node, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        node = node == null ? null : VisualTreeHelper.GetParent(node);
        while (node != null && node is not T) node = VisualTreeHelper.GetParent(node);
        return node as T;
    }
}
