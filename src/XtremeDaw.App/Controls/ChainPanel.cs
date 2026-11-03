using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The Mastering view's chain: tiles in the order the sound goes, left to right, wrapping to the next row when the
/// width runs out (asked by the user: a narrow window shows the chain going on below instead of scrolling sideways).
/// It draws the signal's path itself: an arrow between neighbours in a row and, at a wrap, a line that leaves the row's
/// last tile, runs under the row back to the left and enters the next row's first one. The first <see cref="Movable"/>
/// tiles are dragged by their grip (<see cref="AddMovable"/>) to another place, in two dimensions: the others glide to
/// where they would be in the new order (<see cref="SlotDrag.SlideEase"/>, like every reorder), and once everything has
/// settled <see cref="Moved"/> gets the slot id, the one it lands next to and whether after it, as <see cref="SlotDrag"/>
/// does. The ones after them (Salida) never move. Everything is laid out from one packing (<see cref="Pack"/>), and the
/// path is drawn only on a layout pass, never per frame, by a visual of its own over the tiles (<see cref="Wires"/>: a
/// panel can't draw), which lets the pointer through.
/// </summary>
public sealed class ChainPanel : Panel
{
    /// <summary>The gap between tiles in a row (the arrow lives in it) and between rows (the wrap's line runs in it).</summary>
    const double Gap = 22, RowGap = 18;
    /// <summary>How far the pointer moves before a press on a grip becomes a drag.</summary>
    const double Threshold = 4;
    /// <summary>The arrowhead's half size.</summary>
    const double Head = 3.5;
    /// <summary>Room kept at both sides of every row, where a wrap's line turns (a negative margin of this much lines the tiles up with what is under them).</summary>
    public const double Side = 12;

    readonly List<string> ids = [];
    Rect[] rects = [];
    TranslateTransform[] shift = [];
    Vector[] target = [];
    DispatcherTimer? timer;
    int from = -1, landing = -1;
    Point start;
    Vector pointer;
    bool dragging, settling;
    readonly Wires wires;

    public ChainPanel()
    {
        wires = new Wires(this) { IsHitTestVisible = false };
        // a visual only, not one of the Children: the packing never sees it
        VisualChildren.Add(wires);
    }

    /// <summary>Draws the panel's path (<see cref="DrawWires"/>).</summary>
    sealed class Wires(ChainPanel owner) : Control
    {
        public override void Render(DrawingContext ctx) => owner.DrawWires(ctx);
    }

    /// <summary>How many of the first children can be dragged (the master's effects).</summary>
    public int Movable => ids.Count;

    /// <summary>Once a drop has settled: the slot id, the slot it lands next to, and whether it goes after that one.</summary>
    public event Action<string, string, bool>? Moved;
    /// <summary>A press and release on a grip that never became a drag: the slot id.</summary>
    public event Action<string>? Clicked;

    /// <summary>Adds a tile that <paramref name="grip"/> drags. Movable tiles go before any added with <see cref="Panel.Children"/>.</summary>
    public void AddMovable(Control tile, string id, Control grip)
    {
        int index = ids.Count;
        ids.Add(id);
        Children.Insert(index, tile);
        grip.Cursor = new Cursor(StandardCursorType.SizeAll);
        grip.PointerPressed += (_, e) =>
        {
            if (e.Handled || settling || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            from = landing = index;
            dragging = false;
            start = e.GetPosition(this);
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (from != index || settling) return;
            var delta = e.GetPosition(this) - start;
            if (!dragging && Math.Abs(delta.X) < Threshold && Math.Abs(delta.Y) < Threshold) return;
            if (!dragging) Lift();
            pointer = delta;
            shift[from].X = delta.X;
            shift[from].Y = delta.Y;
            int k = LandingFor(rects[from].Center + delta);
            if (k != landing)
            {
                landing = k;
                MakeRoom();
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            if (from != index || settling) return;
            if (dragging)
            {
                settling = true;
                target[from] = Layout(Order())[from].TopLeft - rects[from].TopLeft;
                timer!.Start();
            }
            else
            {
                from = -1;
                Clicked?.Invoke(id);
            }
            e.Pointer.Capture(null);
        };
        grip.PointerCaptureLost += (_, _) =>
        {
            if (from != index || settling) return;
            if (!dragging) { from = -1; return; }
            // cancelled: everything glides back
            settling = true;
            landing = from;
            Array.Clear(target);
            timer!.Start();
        };
    }

    void Lift()
    {
        dragging = true;
        int n = ids.Count;
        shift = new TranslateTransform[n];
        target = new Vector[n];
        for (int i = 0; i < n; i++)
        {
            shift[i] = new TranslateTransform();
            Children[i].RenderTransform = shift[i];
        }
        Children[from].ZIndex = 1;
        Children[from].Opacity = 0.85;
        timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
        wires.InvalidateVisual(); // the path hides while tiles move
    }

    /// <summary>The movable tiles' order with the dragged one at <see cref="landing"/>, then the fixed ones.</summary>
    int[] Order()
    {
        var order = new List<int>(Children.Count);
        for (int i = 0; i < ids.Count; i++) if (i != from) order.Add(i);
        order.Insert(Math.Clamp(landing, 0, order.Count), from);
        for (int i = ids.Count; i < Children.Count; i++) order.Add(i);
        return [.. order];
    }

    /// <summary>Where each child (by its index) would be with the children in <paramref name="order"/>.</summary>
    Rect[] Layout(int[] order)
    {
        var placed = Pack(order, Bounds.Width);
        var byChild = new Rect[Children.Count];
        for (int k = 0; k < order.Length; k++) byChild[order[k]] = placed[k];
        return byChild;
    }

    /// <summary>Where the dragged tile lands with its center at <paramref name="at"/>: before the first tile, in reading order, it is ahead of.</summary>
    int LandingFor(Point at)
    {
        int k = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            if (i == from) continue;
            var r = rects[i];
            bool above = at.Y < r.Top - RowGap / 2, below = at.Y > r.Bottom + RowGap / 2;
            if (below || (!above && at.X > r.Center.X)) k++;
        }
        return k;
    }

    /// <summary>The others glide to their places in the order the drop would give.</summary>
    void MakeRoom()
    {
        var next = Layout(Order());
        for (int i = 0; i < ids.Count; i++)
            if (i != from) target[i] = next[i].TopLeft - rects[i].TopLeft;
        timer!.Start();
    }

    void Step()
    {
        bool still = true;
        for (int i = 0; i < ids.Count; i++)
        {
            if (i == from && !settling) continue;
            var now = new Vector(shift[i].X, shift[i].Y);
            var t = target[i];
            now += (t - now) * SlotDrag.SlideEase;
            if (Math.Abs(t.X - now.X) < 0.5 && Math.Abs(t.Y - now.Y) < 0.5) now = t;
            else still = false;
            shift[i].X = now.X;
            shift[i].Y = now.Y;
        }
        if (!still || !settling) { if (still) timer!.Stop(); return; }
        timer!.Stop();
        int f = from, k = landing;
        from = landing = -1;
        dragging = settling = false;
        // the chain is rebuilt in the new order right here, so the tiles that glided never show their old places again
        if (k >= 0 && k != f)
        {
            var order = Order(f, k);
            int at = Array.IndexOf(order, f);
            bool after = at > 0;
            Moved?.Invoke(ids[f], ids[after ? order[at - 1] : order[at + 1]], after);
        }
        for (int i = 0; i < ids.Count; i++)
        {
            Children[i].RenderTransform = null;
            Children[i].ZIndex = 0;
            Children[i].Opacity = 1;
        }
        wires.InvalidateVisual();
    }

    int[] Order(int f, int k)
    {
        (from, landing) = (f, k);
        var order = Order();
        from = landing = -1;
        return order;
    }

    /// <summary>
    /// Greedy rows over the children in <paramref name="order"/>: a tile that would not fit after the ones before starts
    /// a new row. Each row is as tall as its tallest tile, and the tiles are centered in it.
    /// </summary>
    Rect[] Pack(int[] order, double width)
    {
        var placed = new Rect[order.Length];
        double x = Side, y = 0, rowH = 0;
        int rowStart = 0;
        for (int k = 0; k < order.Length; k++)
        {
            var d = Children[order[k]].DesiredSize;
            if (k > rowStart && x + d.Width > width - Side)
            {
                Center(rowStart, k);
                y += rowH + RowGap;
                x = Side;
                rowH = 0;
                rowStart = k;
            }
            placed[k] = new Rect(x, y, d.Width, d.Height);
            x += d.Width + Gap;
            rowH = Math.Max(rowH, d.Height);
        }
        Center(rowStart, order.Length);
        return placed;

        void Center(int a, int b)
        {
            for (int k = a; k < b; k++) placed[k] = placed[k].WithY(y + (rowH - placed[k].Height) / 2);
        }
    }

    int[] Natural()
    {
        var order = new int[Children.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        return order;
    }

    protected override Size MeasureOverride(Size available)
    {
        foreach (var c in Children) c.Measure(Size.Infinity);
        wires.Measure(Size.Infinity);
        var placed = Pack(Natural(), available.Width);
        double w = 0, h = 0;
        foreach (var r in placed)
        {
            w = Math.Max(w, r.Right + Side);
            h = Math.Max(h, r.Bottom);
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (dragging || settling) return final; // a drag moves them by their transforms
        rects = Pack(Natural(), final.Width);
        for (int i = 0; i < Children.Count; i++) Children[i].Arrange(rects[i]);
        wires.Arrange(new Rect(final));
        wires.InvalidateVisual();
        return final;
    }

    void DrawWires(DrawingContext ctx)
    {
        if (dragging || settling || rects.Length < 2) return;
        var pen = Palette.Pen(Palette.Faint, 1, PenLineCap.Round, PenLineJoin.Round);
        for (int i = 1; i < rects.Length; i++)
        {
            Rect a = rects[i - 1], b = rects[i];
            double y = Math.Round(b.Center.Y) + 0.5;
            if (Math.Abs(a.Center.Y - b.Center.Y) < 1)
            {
                // same row: an arrow across the gap
                Arrow(ctx, pen, new Point(a.Right + 5, y), new Point(b.Left - 5, y));
                continue;
            }
            // a wrap: out of the row's last tile, under the row, back to the left and into the next row's first
            double ay = Math.Round(a.Center.Y) + 0.5, mid = Math.Round(a.Bottom + RowGap / 2) + 0.5;
            double right = Math.Round(a.Right + Side / 2) + 0.5, left = Math.Round(b.Left - Side / 2) + 0.5;
            ctx.DrawLine(pen, new Point(a.Right + 5, ay), new Point(right, ay));
            ctx.DrawLine(pen, new Point(right, ay), new Point(right, mid));
            ctx.DrawLine(pen, new Point(right, mid), new Point(left, mid));
            ctx.DrawLine(pen, new Point(left, mid), new Point(left, y));
            Arrow(ctx, pen, new Point(left, y), new Point(b.Left - 3, y));
        }
    }

    static void Arrow(DrawingContext ctx, IPen pen, Point a, Point b)
    {
        ctx.DrawLine(pen, a, b);
        ctx.DrawLine(pen, new Point(b.X - Head, b.Y - Head), b);
        ctx.DrawLine(pen, new Point(b.X - Head, b.Y + Head), b);
    }
}
