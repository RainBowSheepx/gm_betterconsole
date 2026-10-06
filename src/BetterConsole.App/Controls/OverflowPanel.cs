using System.Windows;
using System.Windows.Controls;

namespace BetterConsole.App.Controls;

/// <summary>
/// A horizontal row that shows only the children that fit completely, in order: the ones from the
/// first that does not fit on are hidden instead of being cut through the middle (the status bar in a
/// narrow window). A child marked <see cref="CanShrinkProperty"/> gets the width that is left and
/// handles it itself (an items control whose panel is another OverflowPanel).
/// </summary>
public sealed class OverflowPanel : Panel
{
    public static readonly DependencyProperty CanShrinkProperty = DependencyProperty.RegisterAttached(
        "CanShrink", typeof(bool), typeof(OverflowPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetCanShrink(DependencyObject d) => (bool)d.GetValue(CanShrinkProperty);
    public static void SetCanShrink(DependencyObject d, bool value) => d.SetValue(CanShrinkProperty, value);

    private int _hiddenFrom = int.MaxValue;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, height = 0;
        _hiddenFrom = int.MaxValue;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            bool shrink = GetCanShrink(c);
            c.Measure(new Size(shrink ? Math.Max(0, available.Width - x) : double.PositiveInfinity, available.Height));
            if (_hiddenFrom != int.MaxValue) continue;
            var w = c.DesiredSize.Width;
            if (x + w > available.Width + 0.5)
            {
                _hiddenFrom = i;
                continue;
            }
            x += w;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        return new Size(x, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            if (i >= _hiddenFrom)
            {
                c.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            c.Arrange(new Rect(x, 0, c.DesiredSize.Width, finalSize.Height));
            x += c.DesiredSize.Width;
        }
        return finalSize;
    }
}
