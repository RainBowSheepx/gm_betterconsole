using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterConsole.Core.Errors;

namespace BetterConsole.App.Controls;

/// <summary>
/// Paths of Lua files in error texts and profiler tables open in the editor (Settings → Lua files) at
/// their line, with a double click. They look like the rest of the text; only the pointer turns into a
/// hand over a path whose file is on the server. Text stays selectable as before.
///
/// <c>LuaLinks.Resolver</c> (inherited) is set on a server's views; <c>LuaLinks.IsEnabled</c> on a
/// TextBox (links inside its text) or on any other element (TextBlocks below it, like data grid cells).
/// </summary>
public static class LuaLinks
{
    public static readonly DependencyProperty ResolverProperty = DependencyProperty.RegisterAttached(
        "Resolver", typeof(LuaFileResolver), typeof(LuaLinks), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static LuaFileResolver? GetResolver(DependencyObject d) => (LuaFileResolver?)d.GetValue(ResolverProperty);
    public static void SetResolver(DependencyObject d, LuaFileResolver? value) => d.SetValue(ResolverProperty, value);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(LuaLinks), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    /// <summary>Opens a file at a line (set by the app: the editor of the settings, a toast on failure).</summary>
    public static Action<FrameworkElement, string, int>? Open { get; set; }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if ((bool)e.NewValue)
        {
            // After the text box's own handlers: it sets the I-beam first, the hand wins over a link.
            el.AddHandler(UIElement.QueryCursorEvent, new QueryCursorEventHandler(OnQueryCursor), handledEventsToo: true);
            el.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewDown), handledEventsToo: true);
        }
        else
        {
            el.RemoveHandler(UIElement.QueryCursorEvent, new QueryCursorEventHandler(OnQueryCursor));
            el.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewDown));
        }
    }

    private static void OnQueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (Hit(sender, e) is not null)
        {
            e.Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    private static void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || Hit(sender, e) is not { } hit) return;
        // Instead of selecting the word under the pointer.
        e.Handled = true;
        Open?.Invoke((FrameworkElement)sender, hit.Path, hit.Line);
    }

    /// <summary>The file under the pointer, if it is a link to a file that exists.</summary>
    private static (string Path, int Line)? Hit(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement host || GetResolver(host) is not { } resolver) return null;
        if (sender is TextBox tb)
        {
            var at = tb.GetCharacterIndexFromPoint(e.GetPosition(tb), snapToText: false);
            if (at < 0) return null;
            foreach (var link in Links(tb.Text))
                if (at >= link.Start && at < link.Start + link.Length && resolver.Resolve(link.Source) is { } file) return (file, link.Line);
            return null;
        }
        // A text block below the element (a data grid cell): its text is a path or contains one, or its
        // Tag names where the thing in it is defined (a hook's name opens the hook function).
        for (var cur = e.OriginalSource as DependencyObject; cur != null && cur != host; cur = Parent(cur))
        {
            if (cur is not TextBlock block) continue;
            foreach (var text in new[] { block.Text, block.Tag as string })
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var link in Links(text))
                    if (resolver.Resolve(link.Source) is { } file) return (file, link.Line);
            }
            return null;
        }
        return null;
    }

    // QueryCursor comes with every mouse move: the links of a text are found once.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, List<LuaLink>> Scanned = new();

    private static List<LuaLink> Links(string text) => Scanned.GetValue(text, LuaFileResolver.Scan);

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
