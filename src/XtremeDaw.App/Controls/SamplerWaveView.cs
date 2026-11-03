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
/// </summary>
public sealed class SamplerWaveView : Control
{
    public enum Marker { None, Start, End, LoopStart, LoopEnd }

    const double BarW = 2, Gap = 1;
    /// <summary>How close to a flag a press takes it, in px.</summary>
    const double Grab = 8;
    const double FlagW = 12, FlagH = 11;
    /// <summary>The least the sound between two flags can be, as a fraction: they never cross.</summary>
    const double MinGap = 0.001;
    const double Radius = 6;

    /// <summary>The overlay: only the heads, so a voice moving doesn't redraw the wave.</summary>
    sealed class HeadLayer(SamplerWaveView wave) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            for (int i = 0; i < wave.headCount; i++)
            {
                double x = Math.Round(wave.heads[i] * w) + 0.5;
                if (x >= 0 && x <= w) ctx.DrawLine(Palette.Pen(Brushes.White), new Point(x, 0), new Point(x, h));
            }
        }
    }

    readonly HeadLayer layer;
    readonly float[] heads = new float[Sampler.MaxVoices];
    int headCount;
    byte[]? peaks;
    Marker dragging, hot;
    string emptyTitle = "", emptyHint = "";
    bool dropHot, looped;

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

    /// <summary>A flag was dragged to this fraction of the sample.</summary>
    public event Action<Marker, double>? MarkerMoved;
    /// <summary>A press on the empty wave.</summary>
    public event Action? PickRequested;

    public double Start { get; private set; }
    public double End { get; private set; } = 1;
    public double LoopStart { get; private set; }
    public double LoopEnd { get; private set; } = 1;

    public void SetEmpty(string title, string hint)
    {
        emptyTitle = title;
        emptyHint = hint;
        InvalidateVisual();
    }

    /// <summary>The sample's peaks (0..255), in the order they play; null = no sample.</summary>
    public void SetPeaks(byte[]? p)
    {
        if ((peaks == null) != (p == null)) Cursor = new Cursor(p == null ? StandardCursorType.Hand : StandardCursorType.Arrow);
        peaks = p;
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
        if (looped) ctx.FillRectangle(Palette.AccentSoft, new Rect(LoopStart * w, 0, (LoopEnd - LoopStart) * w, h));
        double height = (h - 2 * FlagH - 4) / 2;
        var on = Palette.Pen(Palette.Accent, BarW);
        var off = Palette.Pen(Palette.Faint, BarW);
        for (double x = BarW / 2; x < w; x += BarW + Gap)
        {
            double f = x / w;
            int from = (int)(f * p.Length), to = Math.Min(p.Length, Math.Max(from + 1, (int)((x + BarW + Gap) / w * p.Length)));
            int top = 0;
            for (int i = from; i < to; i++) top = Math.Max(top, p[i]);
            double bar = Math.Max(0.5, top / 255.0 * height);
            ctx.DrawLine(f >= Start && f <= End ? on : off, new Point(x, mid - bar), new Point(x, mid + bar));
        }
        DrawFlag(ctx, Start, Marker.Start, "S", true, Palette.Fg);
        DrawFlag(ctx, End, Marker.End, "E", false, Palette.Fg);
        if (!looped) return;
        DrawFlag(ctx, LoopStart, Marker.LoopStart, "L", true, Palette.Accent);
        DrawFlag(ctx, LoopEnd, Marker.LoopEnd, "L", false, Palette.Accent);
    }

    /// <summary>A line with its tab: the start's looks right, into the sound; the end's left. Loop flags hang from the bottom.</summary>
    void DrawFlag(DrawingContext ctx, double at, Marker kind, string letter, bool opens, IBrush color)
    {
        double w = Bounds.Width, h = Bounds.Height, x = Math.Round(at * (w - 1)) + 0.5;
        bool lit = kind == hot || kind == dragging;
        ctx.DrawLine(Palette.Pen(color, lit ? 2 : 1), new Point(x, 0), new Point(x, h));
        bool low = kind is Marker.LoopStart or Marker.LoopEnd;
        var tab = new Rect(opens ? x : x - FlagW, low ? h - FlagH : 0, FlagW, FlagH);
        ctx.FillRectangle(color, tab, 2);
        var text = Palette.Text(letter, 9, Palette.App, Palette.UiBold);
        ctx.DrawText(text, new Point(tab.X + (FlagW - text.Width) / 2, tab.Y + (FlagH - text.Height) / 2));
    }

    Marker MarkerAt(Point p)
    {
        double w = Bounds.Width, h = Bounds.Height, best = Grab;
        var found = Marker.None;
        void Try(Marker m, double at, bool low)
        {
            // the tabs are the sure place to grab: with flags together, the one whose tab is at this height wins
            bool right = low ? p.Y > h / 2 : p.Y <= h / 2;
            double d = Math.Abs(p.X - at * (w - 1)) + (right ? 0 : Grab / 2);
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
        dragging = MarkerAt(e.GetPosition(this));
        if (dragging == Marker.None) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (dragging == Marker.None)
        {
            var near = peaks == null ? Marker.None : MarkerAt(at);
            if (near == hot) return;
            hot = near;
            if (peaks != null) Cursor = new Cursor(near == Marker.None ? StandardCursorType.Arrow : StandardCursorType.SizeWestEast);
            InvalidateVisual();
            return;
        }
        double f = Math.Clamp(at.X / Math.Max(1, Bounds.Width - 1), 0, 1);
        f = dragging switch
        {
            Marker.Start => Math.Min(f, End - MinGap),
            Marker.End => Math.Max(f, Start + MinGap),
            Marker.LoopStart => Math.Min(f, LoopEnd - MinGap),
            _ => Math.Max(f, LoopStart + MinGap),
        };
        MarkerMoved?.Invoke(dragging, f);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging == Marker.None) return;
        dragging = Marker.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = Marker.None;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hot == Marker.None) return;
        hot = Marker.None;
        InvalidateVisual();
    }
}
