using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterConsole.App.Controls;

/// <summary>
/// Keeps what the user is looking at in place when content is inserted or grows above it, the way
/// browsers do "scroll anchoring". Attach to a ScrollViewer; mark the elements that can serve as an
/// anchor (cards, headers) with <c>ScrollAnchor.IsCandidate="True"</c>.
/// With <c>StickToBottom</c> the view follows new content while it is scrolled to the end.
/// </summary>
public static class ScrollAnchor
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollAnchor), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty StickToBottomProperty = DependencyProperty.RegisterAttached(
        "StickToBottom", typeof(bool), typeof(ScrollAnchor), new PropertyMetadata(false));

    public static readonly DependencyProperty IsCandidateProperty = DependencyProperty.RegisterAttached(
        "IsCandidate", typeof(bool), typeof(ScrollAnchor), new PropertyMetadata(false));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(AnchorState), typeof(ScrollAnchor));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);
    public static bool GetStickToBottom(DependencyObject d) => (bool)d.GetValue(StickToBottomProperty);
    public static void SetStickToBottom(DependencyObject d, bool v) => d.SetValue(StickToBottomProperty, v);
    public static bool GetIsCandidate(DependencyObject d) => (bool)d.GetValue(IsCandidateProperty);
    public static void SetIsCandidate(DependencyObject d, bool v) => d.SetValue(IsCandidateProperty, v);

    private sealed class AnchorState
    {
        public FrameworkElement? Anchor;
        public double AnchorTop;
        public bool AtBottom = true;
        public bool Adjusting;
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.SetValue(StateProperty, new AnchorState());
            sv.ScrollChanged += OnScrollChanged;
        }
        else
        {
            sv.ScrollChanged -= OnScrollChanged;
        }
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.GetValue(StateProperty) is not AnchorState st) return;
        if (st.Adjusting)
        {
            st.Adjusting = false;
            Record(sv, st);
            return;
        }
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            // Content changed size: restore the anchor (or stay at the bottom).
            Correct(sv);
            return;
        }
        // The user scrolled: remember what is at the top now.
        st.AtBottom = sv.VerticalOffset >= sv.ScrollableHeight - 2;
        Record(sv, st);
    }

    /// <summary>
    /// Call after changes that move items without changing the total height (reordering): restores
    /// the anchor once the layout has run.
    /// </summary>
    public static void RequestCorrection(ScrollViewer sv) =>
        sv.Dispatcher.BeginInvoke(() => Correct(sv), System.Windows.Threading.DispatcherPriority.Loaded);

    private static void Record(ScrollViewer sv, AnchorState st)
    {
        st.Anchor = FindAnchor(sv);
        st.AnchorTop = st.Anchor != null ? TopOf(st.Anchor, sv) : 0;
        st.AtBottom = sv.VerticalOffset >= sv.ScrollableHeight - 2;
    }

    private static void Correct(ScrollViewer sv)
    {
        if (sv.GetValue(StateProperty) is not AnchorState st) return;
        if (GetStickToBottom(sv) && st.AtBottom)
        {
            if (sv.VerticalOffset < sv.ScrollableHeight - 1)
            {
                st.Adjusting = true;
                sv.ScrollToEnd();
            }
            return;
        }
        if (st.Anchor == null || !st.Anchor.IsVisible || !IsDescendant(sv, st.Anchor))
        {
            Record(sv, st);
            return;
        }
        double now = TopOf(st.Anchor, sv);
        double delta = now - st.AnchorTop;
        if (Math.Abs(delta) > 0.5 && sv.VerticalOffset + delta >= 0)
        {
            st.Adjusting = true;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
        }
    }

    private static double TopOf(FrameworkElement el, ScrollViewer sv)
    {
        try { return el.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
        catch (InvalidOperationException) { return 0; }
    }

    private static bool IsDescendant(DependencyObject root, DependencyObject node)
    {
        for (var cur = node; cur != null; cur = TreeWalk.Parent(cur))
            if (cur == root) return true;
        return false;
    }

    /// <summary>The deepest candidate whose top edge is the first at or below the viewport top.</summary>
    private static FrameworkElement? FindAnchor(ScrollViewer sv)
    {
        FrameworkElement? best = null;
        double bestTop = double.MaxValue;
        void Walk(DependencyObject node)
        {
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe && fe.IsVisible)
                {
                    if (GetIsCandidate(fe))
                    {
                        double top = TopOf(fe, sv);
                        double bottom = top + fe.ActualHeight;
                        if (bottom > 0 && top < sv.ViewportHeight && top >= -fe.ActualHeight && Math.Abs(top) < Math.Abs(bestTop))
                        {
                            best = fe;
                            bestTop = top;
                        }
                    }
                    Walk(fe);
                }
                else if (child is not FrameworkElement)
                {
                    Walk(child);
                }
            }
        }
        if (sv.Content is DependencyObject content) Walk(content);
        return best;
    }
}
