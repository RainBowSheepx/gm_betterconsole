using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BetterConsole.App.Controls;

/// <summary>
/// Walking up from the element a mouse event came from. That can be a content element (a Run inside
/// a TextBlock), which is not in the visual tree: VisualTreeHelper.GetParent throws for it.
/// </summary>
public static class TreeWalk
{
    public static DependencyObject? Parent(DependencyObject d) => d switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent ?? LogicalTreeHelper.GetParent(d),
        _ => LogicalTreeHelper.GetParent(d),
    };

    /// <summary>The element itself or the nearest one above it of type T.</summary>
    public static T? Up<T>(DependencyObject? d) where T : class
    {
        for (var cur = d; cur != null; cur = Parent(cur))
            if (cur is T t) return t;
        return null;
    }
}
