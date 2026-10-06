using System.Windows;
using System.Windows.Controls;

namespace BetterConsole.App.Controls;

/// <summary>
/// Cards in a wrap panel that take a share of its width: <see cref="SpanProperty"/> twelfths (6 = two to a
/// row). Narrow panels show every card full width.
/// </summary>
public static class StatsLayout
{
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
        "Span", typeof(int), typeof(StatsLayout), new PropertyMetadata(6));

    public static int GetSpan(DependencyObject d) => (int)d.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject d, int value) => d.SetValue(SpanProperty, value);

    /// <summary>Gives every child of <paramref name="panel"/> its width; the card's own margin is the gutter.</summary>
    public static void Arrange(Panel panel)
    {
        double w = panel.ActualWidth;
        if (w <= 0) return;
        foreach (FrameworkElement child in panel.Children)
        {
            int span = Math.Clamp(GetSpan(child), 1, 12);
            double share = w < 700 ? w - 1 : Math.Floor(w * span / 12.0 - 0.5);
            child.Width = Math.Max(100, share - child.Margin.Left - child.Margin.Right);
        }
    }
}
