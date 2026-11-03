using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The sampler's wave: the sample as bars, what plays in the accent and what is cut off faint, with the start, the end
/// and the loop as flags that are dragged. With no sample it is the place to drop one (a click picks a file). The voices'
/// heads run over it in a layer of their own, redrawn alone while notes sound. It only raises events: the window edits.
/// <para>
/// It zooms down to the single samples (asked by the user: to put a loop point right on the zero crossing, so the loop
/// doesn't click). The wheel zooms at the pointer, dragging the empty wave moves it, a double click shows it all. Close up
/// the peaks run out, so it draws the sound itself: scaled to what is in view, because a loop often sits in a quiet tail.
/// </para>
/// </summary>
public sealed class SamplerWaveView : Control
{
    public enum Marker { None, Start, End, LoopStart, LoopEnd }

    const double BarW = 2, Gap = 1;
    /// <summary>How close to a flag a press takes it, in px.</summary>
    const double Grab = 8;
    const double FlagW = 12, FlagH = 11;
    /// <summary>The least the sound between two flags can be, in frames, or as a fraction before there is a sound: they never cross.</summary>
    const double MinGapFrames = 16, MinGapFraction = 0.001;
    const double Radius = 6;
    /// <summary>The closest view, in frames: each sample is several px apart, more would show nothing new.</summary>
    const double MinViewFrames = 64;
    /// <summary>How much a notch of the wheel zooms.</summary>
    const double WheelZoom = 1.35;
    /// <summary>What a notch of the wheel moves the view with a modifier held, as a part of it.</summary>
    const double WheelPan = 0.15;
    /// <summary>The height of the strip that says which part of the sample is in view.</summary>
    const double StripH = 3;
    /// <summary>From this many px per frame on, each sample shows as a dot on its line.</summary>
    const double DotsFrom = 5;

    /// <summary>The overlay: only the heads, so a voice moving doesn't redraw the wave.</summary>
    sealed class HeadLayer(SamplerWaveView wave) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            double h = Bounds.Height;
            for (int i = 0; i < wave.headCount; i++)
            {
                double x = Math.Round(wave.ToX(wave.heads[i])) + 0.5;
                if (x >= 0 && x <= Bounds.Width) ctx.DrawLine(Palette.Pen(Brushes.White), new Point(x, 0), new Point(x, h));
            }
        }
    }

    readonly HeadLayer layer;
    readonly float[] heads = new float[Sampler.MaxVoices];
    int headCount;
    byte[]? peaks;
    float[]? left, right;
    bool reversed;
    /// <summary>One px of the wave up close: its lowest and highest value. Grows with the width and is kept, nothing is made per frame.</summary>
    float[] columnLow = [], columnHigh = [];
    Marker dragging, hot;
    string emptyTitle = "", emptyHint = "";
    bool dropHot, looped, crossings;
    /// <summary>What is in view, as fractions of the sample.</summary>
    double viewFrom, viewTo = 1;
    bool panning;
    double panFromX, panFromView;

    public SamplerWaveView()
    {
        ClipToBounds = true;
        layer = new HeadLayer(this) { IsHitTestVisible = false };
        VisualChildren.Add(layer);
        Height = 148;
    }

    protected override Size ArrangeOverride(Size size)
    {
        layer.Arrange(new Rect(size));
        return size;
    }

    /// <summary>A flag was dragged to this fraction of the sample; true = Alt is held, free of any magnet.</summary>
    public event Action<Marker, double, bool>? MarkerMoved;
    /// <summary>A press on the empty wave.</summary>
    public event Action? PickRequested;
    /// <summary>The part in view changed.</summary>
    public event Action? ViewChanged;

    public double Start { get; private set; }
    public double End { get; private set; } = 1;
    public double LoopStart { get; private set; }
    public double LoopEnd { get; private set; } = 1;

    int Frames => left?.Length ?? 0;
    double Span => viewTo - viewFrom;
    double MinGap => Frames > 0 ? MinGapFrames / Frames : MinGapFraction;
    /// <summary>The whole sample is in view.</summary>
    public bool ShowsAll => viewFrom <= 0 && viewTo >= 1;

    /// <summary>Marks the crossings going up, the places a loop point can land on.</summary>
    public bool ShowCrossings
    {
        get => crossings;
        set
        {
            if (crossings == value) return;
            crossings = value;
            InvalidateVisual();
        }
    }

    double ToX(double fraction) => (fraction - viewFrom) / Span * (Bounds.Width - 1);
    double ToFraction(double x) => viewFrom + x / Math.Max(1, Bounds.Width - 1) * Span;

    public void SetEmpty(string title, string hint)
    {
        emptyTitle = title;
        emptyHint = hint;
        InvalidateVisual();
    }

    /// <summary>The sample's peaks (0..255), in the order they play, and its sound; null = no sample. A new sample shows whole.</summary>
    public void SetSample(byte[]? p, float[]? l, float[]? r, bool reverse)
    {
        if ((peaks == null) != (p == null)) Cursor = new Cursor(p == null ? StandardCursorType.Hand : StandardCursorType.Arrow);
        if (!ReferenceEquals(l, left)) SetView(0, 1);
        peaks = p;
        (left, right, reversed) = (l, r != null && l != null && r.Length == l.Length ? r : l, reverse);
        InvalidateVisual();
    }

    public void SetMarkers(double start, double end, bool loop, double loopStart, double loopEnd)
    {
        if (start == Start && end == End && loop == looped && loopStart == LoopStart && loopEnd == LoopEnd) return;
        (Start, End, looped, LoopStart, LoopEnd) = (start, end, loop, loopStart, loopEnd);
        InvalidateVisual();
    }

    /// <summary>Something is being dragged over it.</summary>
    public bool DropHot
    {
        get => dropHot;
        set
        {
            if (dropHot == value) return;
            dropHot = value;
            InvalidateVisual();
        }
    }

    /// <summary>Where the voices are; redraws only the layer, and only with something to show or to clear.</summary>
    public void SetHeads(ReadOnlySpan<float> now)
    {
        if (now.Length == 0 && headCount == 0) return;
        now.CopyTo(heads);
        headCount = now.Length;
        layer.InvalidateVisual();
    }

    /// <summary>Shows the whole sample.</summary>
    public void ShowAll() => SetView(0, 1);

    /// <summary>Goes close to a point of the sample, <paramref name="frames"/> wide, with the point in the middle.</summary>
    public void ShowAround(double fraction, double frames)
    {
        double span = Frames > 0 ? frames / Frames : 1;
        SetView(fraction - span / 2, fraction + span / 2);
    }

    /// <summary>Moves the view, kept inside the sample and never closer than <see cref="MinViewFrames"/>.</summary>
    void SetView(double from, double to)
    {
        double span = Math.Clamp(to - from, Frames > 0 ? Math.Min(1, MinViewFrames / Frames) : 1, 1);
        from = Math.Clamp(from, 0, 1 - span);
        if (from == viewFrom && from + span == viewTo) return;
        (viewFrom, viewTo) = (from, from + span);
        InvalidateVisual();
        layer.InvalidateVisual();
        ViewChanged?.Invoke();
    }

    float Mono(int frame)
    {
        int k = reversed ? left!.Length - 1 - frame : frame;
        return (left![k] + right![k]) * 0.5f;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, mid = h / 2;
        var box = new Rect(0, 0, w, h);
        ctx.DrawRectangle(Palette.App, dropHot ? Palette.Pen(Palette.Accent, 2) : Palette.Pen(Palette.Line), box.Deflate(0.5), Radius, Radius);
        if (peaks is not { Length: > 0 } p)
        {
            var title = Palette.Text(emptyTitle, 12.5, dropHot ? Palette.Accent : Palette.Fg, Palette.UiBold);
            var hint = Palette.Text(emptyHint, 11, Palette.Faint);
            ctx.DrawText(title, new Point((w - title.Width) / 2, mid - title.Height - 1));
            ctx.DrawText(hint, new Point((w - hint.Width) / 2, mid + 3));
            return;
        }
        if (looped)
        {
            double a = Math.Max(0, ToX(LoopStart)), b = Math.Min(w, ToX(LoopEnd));
            if (b > a) ctx.FillRectangle(Palette.AccentSoft, new Rect(a, 0, b - a, h));
        }
        double height = (h - 2 * FlagH - 4) / 2;
        // the peaks are only good while each bar has some of its own; past that, the sound itself
        if (Frames > 0 && Span * p.Length * (BarW + Gap) < w) DrawSound(ctx, w, mid, height);
        else DrawBars(ctx, p, w, mid, height);
        DrawFlag(ctx, Start, Marker.Start, "S", true, Palette.Fg);
        DrawFlag(ctx, End, Marker.End, "E", false, Palette.Fg);
        if (looped)
        {
            DrawFlag(ctx, LoopStart, Marker.LoopStart, "L", true, Palette.Accent);
            DrawFlag(ctx, LoopEnd, Marker.LoopEnd, "L", false, Palette.Accent);
        }
        if (!ShowsAll)
        {
            ctx.FillRectangle(Palette.Hover, new Rect(0, h - StripH, w, StripH));
            ctx.FillRectangle(Palette.Dim, new Rect(viewFrom * w, h - StripH, Math.Max(4, Span * w), StripH));
        }
    }

    void DrawBars(DrawingContext ctx, byte[] p, double w, double mid, double height)
    {
        var on = Palette.Pen(Palette.Accent, BarW);
        var off = Palette.Pen(Palette.Faint, BarW);
        for (double x = BarW / 2; x < w; x += BarW + Gap)
        {
            double f = viewFrom + x / w * Span, next = viewFrom + (x + BarW + Gap) / w * Span;
            int from = (int)(f * p.Length), to = Math.Min(p.Length, Math.Max(from + 1, (int)(next * p.Length)));
            int top = 0;
            for (int i = from; i < to; i++) top = Math.Max(top, p[i]);
            double bar = Math.Max(0.5, top / 255.0 * height);
            ctx.DrawLine(f >= Start && f <= End ? on : off, new Point(x, mid - bar), new Point(x, mid + bar));
        }
    }

    /// <summary>The sound in view, on its zero line: a band per px while several frames fall on it, then the line from sample to sample.</summary>
    void DrawSound(DrawingContext ctx, double w, double mid, double height)
    {
        int n = Frames, first = Math.Clamp((int)Math.Floor(viewFrom * n), 0, n - 1), last = Math.Clamp((int)Math.Ceiling(viewTo * n), first + 1, n);
        double framesPerPx = Span * n / w;
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, Math.Round(mid) + 0.5), new Point(w, Math.Round(mid) + 0.5));
        var on = Palette.Pen(Palette.Accent, 1.25);
        var off = Palette.Pen(Palette.Faint, 1.25);
        if (framesPerPx <= 1) DrawLine(ctx, first, last, mid, height, on, off);
        else DrawBands(ctx, first, last, w, mid, height, on, off);
    }

    /// <summary>The loudest of a stretch: what the drawing scales to.</summary>
    float Loudest(int first, int last)
    {
        float top = 1e-4f;
        for (int i = first; i < last; i++) top = Math.Max(top, Math.Abs(Mono(i)));
        return top;
    }

    void DrawLine(DrawingContext ctx, int first, int last, double mid, double height, IPen on, IPen off)
    {
        double scale = height / Loudest(first, last), pxPerFrame = (Bounds.Width - 1) / (Span * Frames);
        double before = 0, beforeX = 0;
        for (int i = first; i < last; i++)
        {
            double f = i / (double)Frames, x = ToX(f), y = mid - Mono(i) * scale;
            bool plays = f >= Start && f <= End;
            if (i > first) ctx.DrawLine(plays ? on : off, new Point(beforeX, before), new Point(x, y));
            if (pxPerFrame >= DotsFrom) ctx.DrawEllipse(plays ? Palette.Accent : Palette.Faint, null, new Point(x, y), 1.75, 1.75);
            // a place a loop point can land on, marked on the zero line
            if (crossings && i > 0 && Mono(i - 1) < 0 && Mono(i) >= 0)
                ctx.DrawEllipse(null, Palette.Pen(Palette.Fg), new Point(x, mid), 3, 3);
            (before, beforeX) = (y, x);
        }
    }

    void DrawBands(DrawingContext ctx, int first, int last, double w, double mid, double height, IPen on, IPen off)
    {
        int columns = (int)Math.Ceiling(w);
        if (columnLow.Length < columns) (columnLow, columnHigh) = (new float[columns], new float[columns]);
        Array.Fill(columnLow, float.MaxValue, 0, columns);
        Array.Fill(columnHigh, float.MinValue, 0, columns);
        float top = 1e-4f;
        for (int i = first; i < last; i++)
        {
            int c = (int)ToX(i / (double)Frames);
            if (c < 0 || c >= columns) continue;
            float v = Mono(i);
            columnLow[c] = Math.Min(columnLow[c], v);
            columnHigh[c] = Math.Max(columnHigh[c], v);
            top = Math.Max(top, Math.Abs(v));
        }
        double scale = height / top;
        for (int c = 0; c < columns; c++)
        {
            if (columnLow[c] > columnHigh[c]) continue;
            double f = ToFraction(c), x = c + 0.5;
            ctx.DrawLine(f >= Start && f <= End ? on : off, new Point(x, mid - columnHigh[c] * scale), new Point(x, mid - columnLow[c] * scale + 0.5));
        }
    }

    /// <summary>A line with its tab: the start's looks right, into the sound; the end's left. Loop flags hang from the bottom. One out of view isn't drawn.</summary>
    void DrawFlag(DrawingContext ctx, double at, Marker kind, string letter, bool opens, IBrush color)
    {
        double w = Bounds.Width, h = Bounds.Height, x = Math.Round(ToX(at)) + 0.5;
        if (x < -FlagW || x > w + FlagW) return;
        bool lit = kind == hot || kind == dragging;
        ctx.DrawLine(Palette.Pen(color, lit ? 2 : 1), new Point(x, 0), new Point(x, h));
        bool low = kind is Marker.LoopStart or Marker.LoopEnd;
        var tab = new Rect(opens ? x : x - FlagW, low ? h - FlagH - (ShowsAll ? 0 : StripH) : 0, FlagW, FlagH);
        ctx.FillRectangle(color, tab, 2);
        var text = Palette.Text(letter, 9, Palette.App, Palette.UiBold);
        ctx.DrawText(text, new Point(tab.X + (FlagW - text.Width) / 2, tab.Y + (FlagH - text.Height) / 2));
    }

    Marker MarkerAt(Point p)
    {
        double h = Bounds.Height, best = Grab;
        var found = Marker.None;
        void Try(Marker m, double at, bool low)
        {
            // the tabs are the sure place to grab: with flags together, the one whose tab is at this height wins
            bool right = low ? p.Y > h / 2 : p.Y <= h / 2;
            double d = Math.Abs(p.X - ToX(at)) + (right ? 0 : Grab / 2);
            if (d < best) (best, found) = (d, m);
        }
        Try(Marker.Start, Start, false);
        Try(Marker.End, End, false);
        if (looped)
        {
            Try(Marker.LoopStart, LoopStart, true);
            Try(Marker.LoopEnd, LoopEnd, true);
        }
        return found;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (peaks == null)
        {
            PickRequested?.Invoke();
            return;
        }
        var at = e.GetPosition(this);
        dragging = MarkerAt(at);
        if (dragging == Marker.None)
        {
            if (e.ClickCount == 2) ShowAll();
            else if (!ShowsAll)
            {
                panning = true;
                (panFromX, panFromView) = (at.X, viewFrom);
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (panning)
        {
            double from = panFromView - (at.X - panFromX) / Math.Max(1, Bounds.Width - 1) * Span;
            SetView(from, from + Span);
            return;
        }
        if (dragging == Marker.None)
        {
            var near = peaks == null ? Marker.None : MarkerAt(at);
            if (near == hot) return;
            hot = near;
            if (peaks != null) Cursor = new Cursor(near == Marker.None ? StandardCursorType.Arrow : StandardCursorType.SizeWestEast);
            InvalidateVisual();
            return;
        }
        double f = Math.Clamp(ToFraction(Math.Clamp(at.X, 0, Bounds.Width)), 0, 1);
        MarkerMoved?.Invoke(dragging, Limit(dragging, f), (e.KeyModifiers & KeyModifiers.Alt) != 0);
    }

    /// <summary>Where a flag can go: never past its partner.</summary>
    public double Limit(Marker m, double f) => m switch
    {
        Marker.Start => Math.Min(f, End - MinGap),
        Marker.End => Math.Max(f, Start + MinGap),
        Marker.LoopStart => Math.Min(f, LoopEnd - MinGap),
        _ => Math.Max(f, LoopStart + MinGap),
    };

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (peaks == null) return;
        e.Handled = true;
        double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (e.KeyModifiers != KeyModifiers.None || e.Delta.X != 0)
        {
            SetView(viewFrom - d * WheelPan * Span, viewTo - d * WheelPan * Span);
            return;
        }
        // what is under the pointer stays under it
        double at = ToFraction(e.GetPosition(this).X), k = Math.Pow(WheelZoom, -d);
        SetView(at - (at - viewFrom) * k, at + (viewTo - at) * k);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (panning)
        {
            panning = false;
            e.Pointer.Capture(null);
            return;
        }
        if (dragging == Marker.None) return;
        dragging = Marker.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = Marker.None;
        panning = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hot == Marker.None) return;
        hot = Marker.None;
        InvalidateVisual();
    }
}
