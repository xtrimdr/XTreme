using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The mixer's two sides (<see cref="MixerSides"/>, #275; #277's left dock went in #303: the user wants left and right
/// only): the left side (channels by default) and the right dock (buses by default), each in a scroll of its own. The
/// line between them is a divider (asked by the user, #275): dragged, it gives one more room than the other; double click
/// goes back to the default, where the right dock takes what it needs up to half. The share is kept in config.json
/// ("mixerSplit"); a strip that comes to the right dock opens it until all its strips show (#285). Every scroll sits in
/// the layout's own grid (<see cref="MixerLayout.Strips"/>), so dragging a strip measures everything in one coordinate
/// space; a side that scrolls while dragging moves its slots. A strip changes side from its menu ("Fijar a la izquierda /
/// a la derecha") or dragged.
/// </summary>
public partial class MainWindow
{
    /// <summary>The least a side keeps when the divider is dragged (px).</summary>
    const double MixerSideMinW = 80;
    /// <summary>The divider's grab room on each side of its line (px).</summary>
    const double MixerSplitGrab = 4;
    /// <summary>The default's cap on the right side: this share of the mixer's width.</summary>
    const double MixerRightMaxShare = 0.5;

    /// <summary>The left side and the right dock.</summary>
    readonly ScrollViewer mixerLeftScroll = SideScroll(), mixerRightScroll = SideScroll();
    /// <summary>What each side scrolls: kept across rebuilds, so a side keeps its scroll; transparent, so the empty space takes clicks.</summary>
    readonly Panel mixerLeftHost = new() { Background = Brushes.Transparent }, mixerRightHost = new() { Background = Brushes.Transparent };
    readonly Groove mixerSplitLine = new() { Grip = true };
    readonly GridSplitter mixerSplitter = new()
    {
        Width = Groove.W + 2 * MixerSplitGrab, Margin = new Thickness(-MixerSplitGrab, 0), Background = Brushes.Transparent,
        ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ZIndex = 2,
    };
    readonly TextBlock mixerEmpty = new()
    {
        FontSize = 12, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false, IsVisible = false,
    };
    /// <summary>The left side's share of the two (0..1), or NaN for the default.</summary>
    double mixerSplit = double.NaN;
    /// <summary>The taller of the two sides' scroll bars (px, 0 when none shows): every column's foot keeps level with it.</summary>
    double mixerBarH;

    static ScrollViewer SideScroll() => new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };

    void InitMixerSplit()
    {
        if (double.TryParse(AppConfig.Get("mixerSplit"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
            mixerSplit = Math.Clamp(s, 0.05, 0.95);
        mixerLeftScroll.Content = mixerLeftHost;
        mixerRightScroll.Content = mixerRightHost;
        var g = mixerLayout.Strips;
        // the left side, the divider and the right dock
        g.ColumnDefinitions = new ColumnDefinitions("*,Auto,*");
        // the room at the ends goes inside each side, so it scrolls with its strips
        g.Margin = default;
        Grid.SetColumn(mixerLeftScroll, 0);
        Grid.SetColumn(mixerSplitLine, 1);
        Grid.SetColumn(mixerSplitter, 1);
        Grid.SetColumn(mixerRightScroll, 2);
        Grid.SetColumnSpan(mixerEmpty, 3);
        g.Children.Add(mixerLeftScroll);
        g.Children.Add(mixerSplitLine);
        g.Children.Add(mixerRightScroll);
        g.Children.Add(mixerSplitter);
        g.Children.Add(mixerEmpty);
        // dragged, both sides take a share of the width (the default's right side is sized by its content)
        // the grip lights while the pointer is on the divider or drags it
        bool over = false, held = false;
        void Light() => mixerSplitLine.Hot = over || held;
        mixerSplitter.PointerEntered += (_, _) => { over = true; Light(); };
        mixerSplitter.PointerExited += (_, _) => { over = false; Light(); };
        mixerSplitter.DragStarted += (_, _) => { held = true; Light(); };
        mixerSplitter.DragCompleted += (_, _) => { held = false; Light(); };
        mixerSplitter.DragStarted += (_, _) =>
        {
            var c = g.ColumnDefinitions;
            double a = c[0].ActualWidth, b = c[2].ActualWidth;
            c[2].MaxWidth = MixerRightFullW();
            c[0].Width = new GridLength(Math.Max(1, a), GridUnitType.Star);
            c[2].Width = new GridLength(Math.Max(1, b), GridUnitType.Star);
        };
        mixerSplitter.DragCompleted += (_, _) =>
        {
            var c = g.ColumnDefinitions;
            double a = c[0].ActualWidth, b = c[2].ActualWidth;
            if (a + b > 0)
            {
                mixerSplit = Math.Clamp(Math.Round(a / (a + b), 4), 0.05, 0.95);
                SaveViewPref("mixerSplit", mixerSplit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            ApplyMixerSplit();
        };
        mixerSplitter.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            mixerSplit = double.NaN;
            SaveViewPref("mixerSplit", "");
            ApplyMixerSplit();
        };
        g.SizeChanged += (_, e) => { if (e.WidthChanged) QueueMixerSplit(); };
        foreach (var side in new[] { mixerLeftScroll, mixerRightScroll })
        {
            side.ScrollChanged += (_, _) => AlignMixerFeet();
            side.SizeChanged += (_, _) => AlignMixerFeet();
        }
    }

    /// <summary>
    /// The columns as the sides are now: one alone takes the whole width, both share it at the divider.
    /// </summary>
    void ApplyMixerSplit()
    {
        var o = mixerLayout.Order;
        bool l = o.Sides[MixerSides.LeftSide] is { Children.Count: > 0 }, r = o.Sides[MixerSides.RightSide] is { Children.Count: > 0 }, both = l && r;
        mixerSplitLine.IsVisible = mixerSplitter.IsVisible = both;
        mixerRightScroll.IsVisible = r;
        mixerLeftScroll.IsVisible = l || !r;
        var c = mixerLayout.Strips.ColumnDefinitions;
        double w = mixerLayout.Strips.Bounds.Width;
        double cap = w > 0 ? Math.Max(MixerSideMinW, w * MixerRightMaxShare) : double.PositiveInfinity;
        (GridLength left, GridLength right, double max) = !r ? (new GridLength(1, GridUnitType.Star), new GridLength(0), double.PositiveInfinity)
            : !l ? (new GridLength(0), new GridLength(1, GridUnitType.Star), double.PositiveInfinity)
            : double.IsNaN(mixerSplit) ? (new GridLength(1, GridUnitType.Star), GridLength.Auto, cap)
            : (new GridLength(mixerSplit, GridUnitType.Star), new GridLength(1 - mixerSplit, GridUnitType.Star), MixerRightFullW());
        double min = both ? MixerSideMinW : 0;
        bool changed = c[0].Width != left || c[2].Width != right || c[2].MaxWidth != max || c[0].MinWidth != min || c[2].MinWidth != min;
        c[0].Width = left;
        c[2].Width = right;
        c[2].MaxWidth = max;
        c[0].MinWidth = c[2].MinWidth = min;
        // a new cap alone doesn't lay the grid out again (#285: a new bus stayed hidden until the divider moved), and
        // a measure alone keeps the columns where they were when the size it needs doesn't change (#303)
        if (changed)
        {
            mixerLayout.Strips.InvalidateMeasure();
            mixerLayout.Strips.InvalidateArrange();
        }
    }

    /// <summary>An <see cref="ApplyMixerSplit"/> is waiting for the layout pass to end.</summary>
    bool mixerSplitQueued;
    Action? applyQueuedSplit;

    /// <summary>
    /// <see cref="ApplyMixerSplit"/> once the layout pass in course ends (#303): called from a size change, the grid is
    /// still arranging, so its new columns would be measured but never arranged until the divider moved.
    /// </summary>
    void QueueMixerSplit()
    {
        if (mixerSplitQueued) return;
        mixerSplitQueued = true;
        applyQueuedSplit ??= () =>
        {
            mixerSplitQueued = false;
            ApplyMixerSplit();
        };
        Avalonia.Threading.Dispatcher.UIThread.Post(applyQueuedSplit, Avalonia.Threading.DispatcherPriority.Normal);
    }

    /// <summary>What the right dock held at the last build (ids of top-level tracks, folders and buses), and its project.</summary>
    HashSet<string>? mixerRightIds;
    string? mixerRightProject;
    /// <summary>A strip came to the right dock in the last build: once it is laid out, the dock opens to show them all.</summary>
    bool mixerRightGrow;
    /// <summary>The ids that came to the right dock in the last build: once it opened, they are scrolled into sight.</summary>
    HashSet<string>? mixerRightNew;

    /// <summary>
    /// Notes whether a strip came to the right dock since the last build (#285: a new bus, or one moved there), by the
    /// model and not by what the filter shows, so showing a hidden kind again doesn't count. A new project only sets the base.
    /// </summary>
    void NoteMixerRightAdds()
    {
        var ids = new HashSet<string>();
        foreach (var b in Buses) if (MixerSides.Of(b) == MixerSides.RightSide) ids.Add(b.Id);
        foreach (var f in Folders) if (MixerSides.Of(f) == MixerSides.RightSide) ids.Add(f.Id);
        foreach (var t in tracks) if (t.Track.FolderId == null && MixerSides.Of(t.Track) == MixerSides.RightSide) ids.Add(t.Track.Id);
        string? project = store?.Project.Id;
        if (mixerRightIds != null && project == mixerRightProject && !ids.IsSubsetOf(mixerRightIds))
        {
            mixerRightGrow = true;
            mixerRightNew = ids.Except(mixerRightIds).ToHashSet();
        }
        mixerRightIds = ids;
        mixerRightProject = project;
    }

    /// <summary>
    /// After a strip came to the right dock (#285): if its strips don't all fit, the divider moves so they do, leaving the
    /// middle at least <see cref="MixerSideMinW"/>. The new share is kept like a dragged one.
    /// </summary>
    void GrowMixerRight()
    {
        // not laid out yet (#303: a rebuild came before this ran, and its dock isn't measured): the flag waits for that
        // dock's own size change, or the new strips would stay hidden until the divider moved
        if (!mixerRightGrow || mixerLayout.Order.Sides[MixerSides.RightSide] is not { Bounds.Width: > 0 } rightStrips
            || rightStrips.GetVisualRoot() == null) return;
        mixerRightGrow = false;
        // whatever the room, the new strips end in sight (a dock that can't open all the way scrolls to them)
        Avalonia.Threading.Dispatcher.UIThread.Post(ShowMixerRightNew, Avalonia.Threading.DispatcherPriority.Background);
        if (!mixerRightScroll.IsVisible || !mixerLeftScroll.IsVisible) return;
        var c = mixerLayout.Strips.ColumnDefinitions;
        double full = MixerRightFullW(), now = c[2].ActualWidth, total = c[0].ActualWidth + now;
        if (double.IsInfinity(full) || total <= 0 || now >= full - 1) return;
        double right = Math.Min(full, total - MixerSideMinW);
        if (right <= now) return;
        // rounded down, so the right side never ends a fraction short of its last strip (its max holds it at full)
        mixerSplit = Math.Clamp(Math.Floor((1 - right / total) * 1e4) / 1e4, 0.05, 0.95);
        SaveViewPref("mixerSplit", mixerSplit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ApplyMixerSplit();
    }

    /// <summary>Scrolls the right dock so the strips that just came to it show (#285), once it is laid out.</summary>
    void ShowMixerRightNew()
    {
        if (mixerRightNew is not { Count: > 0 } ids) return;
        mixerRightNew = null;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var (column, item, side) in mixerLayout.Order.Top)
        {
            string? id = item switch { Bus b => b.Id, Folder f => f.Id, XtremeDaw.Projects.Track t => t.Id, _ => null };
            if (side != MixerSides.RightSide || id == null || !ids.Contains(id)) continue;
            if (column.TranslatePoint(default, mixerRightHost) is not { } p) continue;
            lo = Math.Min(lo, p.X);
            hi = Math.Max(hi, p.X + column.Bounds.Width);
        }
        if (double.IsInfinity(lo)) return;
        double view = mixerRightScroll.Viewport.Width, x = mixerRightScroll.Offset.X;
        if (view <= 0 || lo >= x && hi <= x + view) return;
        // the end of the new strips in sight, and their start if they all fit
        double to = hi - lo <= view ? (hi > x + view ? hi - view : lo) : lo;
        ScrollSideBy(mixerRightScroll, to - x);
    }

    /// <summary>
    /// The right dock's widest (#285): all its strips in sight. Dragged past that it would only add empty room, so the
    /// divider stops there and the middle keeps the rest.
    /// </summary>
    double MixerRightFullW()
    {
        // the whole side: its strips, the room before them (the line's padding, #270) and after them
        var strips = mixerLayout.Order.Sides[MixerSides.RightSide];
        double w = strips?.Bounds.Width ?? 0;
        double pad = strips?.Parent is Border side ? side.Padding.Left + side.Padding.Right : 0;
        return w > 0 ? Math.Max(MixerSideMinW, w + pad + mixerRightHost.Margin.Left + mixerRightHost.Margin.Right) : double.PositiveInfinity;
    }

    /// <summary>
    /// Keeps every column's foot level: when one side shows its scroll bar, the other side and the pinned master give up
    /// the same height, or their meters would end lower than that side's.
    /// </summary>
    void AlignMixerFeet()
    {
        static double Bar(ScrollViewer s) => s.IsVisible ? Math.Max(0, s.Bounds.Height - s.Viewport.Height) : 0;
        double l = Bar(mixerLeftScroll), r = Bar(mixerRightScroll), bar = Math.Max(l, r);
        SetFoot(mixerLeftScroll, bar - l);
        SetFoot(mixerRightScroll, bar - r);
        SetFoot(masterPin, bar);
        mixerBarH = bar;

        static void SetFoot(Control c, double h)
        {
            if (Math.Abs(c.Margin.Bottom - h) > 0.5) c.Margin = new Thickness(c.Margin.Left, c.Margin.Top, c.Margin.Right, h);
        }
    }

    /// <summary>The side under <paramref name="x"/> (px from the mixer's left), or the only one shown.</summary>
    ScrollViewer MixerSideAt(double x)
    {
        if (!mixerRightScroll.IsVisible) return mixerLeftScroll;
        if (!mixerLeftScroll.IsVisible) return mixerRightScroll;
        return mixerRightScroll.TranslatePoint(default, mixerScroll) is { } p && x >= p.X ? mixerRightScroll : mixerLeftScroll;
    }

    /// <summary>The <see cref="MixerSides"/> index of a side's scroll.</summary>
    int MixerSideIndex(ScrollViewer s) => s == mixerRightScroll ? MixerSides.RightSide : MixerSides.LeftSide;

    /// <summary>Scrolls one side by <paramref name="dx"/> px, within its content; returns how far it moved.</summary>
    static double ScrollSideBy(ScrollViewer s, double dx)
    {
        double before = s.Offset.X, to = Math.Clamp(before + dx, 0, Math.Max(0, s.Extent.Width - s.Viewport.Width));
        if (to != before) s.Offset = s.Offset.WithX(to);
        return to - before;
    }

    /// <summary>
    /// Scrolls the side under the pointer while a dragged strip is near its edges. What was measured on that side when
    /// the drag started (slots, the others' bounds, the dragged strip's own place) moves with its content.
    /// </summary>
    void AutoScrollMixer(PointerEventArgs e)
    {
        var side = MixerSideAt(e.GetPosition(mixerScroll).X);
        double w = side.Viewport.Width, x = e.GetPosition(side).X;
        if (w <= 0) return;
        double dx = x < MixerDragEdge ? -MixerDragScroll : x > w - MixerDragEdge ? MixerDragScroll : 0;
        if (dx == 0) return;
        double moved = ScrollSideBy(side, dx);
        if (moved == 0) return;
        int right = MixerSideIndex(side);
        for (int i = 0; i < mixerSlots.Count; i++)
            if (mixerSlots[i].Side == right) mixerSlots[i] = mixerSlots[i] with { X = mixerSlots[i].X - moved };
        for (int i = 0; i < mixerLeaves.Count; i++)
            if (mixerLeaves[i].Side == right) mixerLeaves[i] = mixerLeaves[i] with { L = mixerLeaves[i].L - moved, R = mixerLeaves[i].R - moved };
        if (mixerDrop is { } d && d.Side == right) mixerDrop = d with { X = d.X - moved };
        if (mixerDragSide == right)
        {
            mixerDragL -= moved;
            mixerDragFrom = mixerDragFrom.WithX(mixerDragFrom.X - moved);
        }
    }

    /// <summary>A rope's end held inside the visible part of the card's side: a card scrolled away points at its side's edge.</summary>
    double ClampToSide(Control card, double x) => SideSpan(card, out double lo, out double hi) ? Math.Clamp(x, lo, hi) : x;

    /// <summary>The visible part of the side that holds <paramref name="card"/>, in the connections band's coordinates.</summary>
    bool SideSpan(Control card, out double lo, out double hi)
    {
        var side = mixerLeftScroll.IsVisualAncestorOf(card) ? mixerLeftScroll : mixerRightScroll.IsVisualAncestorOf(card) ? mixerRightScroll : null;
        if (side != null && side.TranslatePoint(default, ropes) is { } p)
        {
            lo = p.X;
            hi = p.X + side.Viewport.Width;
            return true;
        }
        lo = hi = 0;
        return false;
    }
}
