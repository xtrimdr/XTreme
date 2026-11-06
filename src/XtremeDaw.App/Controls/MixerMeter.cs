using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// A mixer strip's meter (from the web's Meter in Mixer.tsx, completed in the native app): two bars, left and right,
/// from -48 to +3 dBFS over the green → yellow → red gradient, each solid up to its RMS and dimmed from there up to its
/// peak (with a thin line on the peak), and a dB scale beside them that only shows the marks that fit. A
/// <see cref="Mono"/> source (a track whose takes are mono) shows one bar as wide as the pair, with the louder side,
/// so every strip keeps its width and the faders stay level. Over the bars: the peak-hold readout (the highest peak
/// of either side since the last reset, in dBFS) and a clip light per bar, which latches red once its peak passes
/// <see cref="ClipDb"/> (the same threshold as <see cref="StereoMeter"/>'s light). A click anywhere on the meter
/// resets them. The window feeds <see cref="Set"/> (peaks as amplitude with their fall-off, RMS as amplitude already
/// integrated) from its tick, so any strip (track, folder, return, master) can drive one; it redraws only when what
/// it shows changes, and the readout's text is rebuilt only when its tenth of a dB changes. Without its
/// <see cref="Scale"/> (a very narrow strip) it is only as wide as its bars and the readout drops the tenths. How the
/// bars are painted follows <see cref="MeterLooks"/> (or its own <see cref="Look"/>, the settings' samples).
/// </summary>
public sealed class MixerMeter : Control
{
    /// <summary>In priority order: a mark is shown only if it is at least 9 px from the ones already shown.</summary>
    static readonly int[] ScaleMarks = [0, -12, -24, -6, -36, -18, -3];
    /// <summary>The marks' labels, built once (a render allocates no text).</summary>
    static readonly string[] MarkTexts = ScaleMarks.Select(db => db == 0 ? "0" : (-db).ToString()).ToArray();
    const double ScaleW = 14;
    /// <summary>The readout's row and the clip light over the bar (px); the bar starts under them.</summary>
    const double ReadoutH = 11, LightH = 4, BarTop = ReadoutH + 1 + LightH + 3;
    /// <summary>Peaks above this light the clip light (dBFS), as in <see cref="StereoMeter"/>.</summary>
    public const double ClipDb = -0.3;
    static readonly double ClipAmp = Math.Pow(10, ClipDb / 20);
    /// <summary>Under this (−100 dBFS) the readout says −∞.</summary>
    const double SilentAmp = 1e-5;
    /// <summary>How much of the gradient the stretch between RMS and peak shows.</summary>
    const double PeakOpacity = 0.4;
    static readonly Cursor Hand = new(StandardCursorType.Hand);

    /// <summary>Each bar's width and the gap between the two (px); a mono bar takes the pair's width.</summary>
    double barW, pairW;
    const double Gap = 1;
    /// <summary>Only over the readout: over the bars, beside the fader, a tooltip gets in the way.</summary>
    readonly string holdTip;
    double levelL, levelR, rmsL, rmsR, hold;
    bool mono, clipL, clipR, overHead;
    /// <summary>The readout as shown, in tenths of a dB (int.MinValue = −∞), and its text.</summary>
    int holdTenths = int.MinValue;
    string holdText = "-∞";
    bool scale = true;

    public MixerMeter(bool wide = false)
    {
        barW = wide ? 7 : 5;
        pairW = 2 * barW + Gap;
        Width = pairW + 2 + ScaleW;
        Cursor = Hand;
        holdTip = Loc.T("meter.mixer.hold.tip");
        levels = new Layer(this, false) { IsHitTestVisible = false };
        marksLayer = new Layer(this, true) { IsHitTestVisible = false };
        VisualChildren.Add(levels);
        VisualChildren.Add(marksLayer);
    }

    /// <summary>Each bar's width (px): a wider mixer strip widens its meter (#199).</summary>
    public double BarWidth
    {
        set
        {
            if (value == barW) return;
            barW = value;
            pairW = 2 * barW + Gap;
            Width = pairW + (scale ? 2 + ScaleW : 0);
            InvalidateVisual();
        }
    }

    /// <summary>Widens the bars with <paramref name="room"/> px (scale included, if shown): each takes
    /// <paramref name="share"/> of its half, between <paramref name="min"/> and <paramref name="max"/> px (#199: they
    /// grow with the strip at every width; #205: filling all of it made them too wide).</summary>
    public void Fit(double room, double min, double max, double share = 1) =>
        BarWidth = Math.Clamp(Math.Floor((room - (scale ? 2 + ScaleW : 0) - Gap) / 2 * share), min, max);

    /// <summary>The dB marks beside the bars; off, the meter is as wide as its bars and its readout is in whole dB.</summary>
    public bool Scale
    {
        set
        {
            if (value == scale) return;
            scale = value;
            Width = pairW + (scale ? 2 + ScaleW : 0);
            holdText = HoldText(holdTenths);
            InvalidateVisual();
        }
    }

    string? look;
    /// <summary>This meter's look, whatever the window's is (a sample in the settings); null follows <see cref="MeterLooks.Current"/>.</summary>
    public string? Look
    {
        set { look = value; InvalidateVisual(); }
    }

    /// <summary>The LEDs' clip over the bars' area, kept until the size changes.</summary>
    StreamGeometry? leds;
    Rect ledsFor;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MeterLooks.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MeterLooks.Changed -= InvalidateVisual;
    }

    string HoldText(int tenths) => tenths == int.MinValue ? "-∞" : scale ? $"{tenths / 10.0:+0.0;-0.0;0.0}" : $"{Math.Round(tenths / 10.0):+0;-0;0}";

    /// <summary>One bar with the louder side instead of two (a mono source); the light is lit if either side clipped.</summary>
    public bool Mono
    {
        set
        {
            if (value == mono) return;
            mono = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// What to show: each side's peak (amplitude, with its fall-off; it also feeds the peak hold and the clip lights) and
    /// RMS (amplitude, already integrated).
    /// </summary>
    public void Set(double peakL, double peakR, double rmsL, double rmsR)
    {
        bool moved = Moved(peakL, levelL) || Moved(peakR, levelR) || Moved(rmsL, this.rmsL) || Moved(rmsR, this.rmsR), dirty = false;
        levelL = peakL; levelR = peakR;
        this.rmsL = rmsL; this.rmsR = rmsR;
        double peak = Math.Max(peakL, peakR);
        if (peak > hold)
        {
            hold = peak;
            int tenths = Tenths(peak);
            if (tenths != holdTenths)
            {
                holdTenths = tenths;
                holdText = HoldText(tenths);
                dirty = true;
            }
        }
        if (peakL >= ClipAmp && !clipL) { clipL = true; dirty = true; }
        if (peakR >= ClipAmp && !clipR) { clipR = true; dirty = true; }
        // the levels on their own layer; the rest (readout, lights) only when they changed
        if (moved) levels.InvalidateVisual();
        if (dirty) base.InvalidateVisual();
    }

    static bool Moved(double now, double was) => Math.Abs(Pct(now) - Pct(was)) >= 0.002;

    /// <summary>Clears the peak hold and the clip light (a click on the meter).</summary>
    public void ResetHold()
    {
        hold = 0;
        holdTenths = int.MinValue;
        holdText = "-∞";
        clipL = clipR = false;
        InvalidateVisual();
    }

    static int Tenths(double amp) => amp <= SilentAmp ? int.MinValue : (int)Math.Round(200 * Math.Log10(amp));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        bool on = e.GetPosition(this).Y < BarTop;
        if (on == overHead) return;
        overHead = on;
        ToolTip.SetTip(this, on ? holdTip : null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        ResetHold();
        e.Handled = true;
    }

    static double DbToPct(double db) => (db + 48) / 51;
    static double Pct(double amp) => amp <= 0.0001 ? 0 : Math.Clamp(DbToPct(20 * Math.Log10(amp)), 0, 1);

    // Three layers, so a moving level redraws only what moves (asked for by #138: a full meter per strip and per frame
    // was most of the garbage whose collections stalled the audio): this control draws what changes only with the
    // readout, the clip lights, the look or the size (readout, lights, bar backgrounds, scale); `levels` draws the bars'
    // levels, every frame they move; `marks` draws the dB lines over them and never redraws on its own.
    sealed class Layer(MixerMeter owner, bool front) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            if (front) owner.RenderMarks(ctx);
            else owner.RenderLevels(ctx);
        }
    }

    readonly Layer levels, marksLayer;

    /// <summary>Redraws every layer: the look, the scale or the layout changed.</summary>
    new void InvalidateVisual()
    {
        base.InvalidateVisual();
        levels.InvalidateVisual();
        marksLayer.InvalidateVisual();
    }

    /// <summary>The bars' height and the marks that fit at it (indices into <see cref="ScaleMarks"/>).</summary>
    int Marks(double h, Span<int> marks)
    {
        int n = 0;
        for (int i = 0; i < ScaleMarks.Length; i++)
        {
            bool fits = true;
            for (int k = 0; k < n; k++) if (Math.Abs(DbToPct(ScaleMarks[marks[k]]) - DbToPct(ScaleMarks[i])) * h < 9) { fits = false; break; }
            if (fits) marks[n++] = i;
        }
        return n;
    }

    /// <summary>Where each bar goes: one as wide as the pair (mono) or two.</summary>
    int Bars(Span<double> x, Span<double> w)
    {
        if (mono) { x[0] = 0; w[0] = pairW; return 1; }
        x[0] = 0; w[0] = barW;
        x[1] = barW + Gap; w[1] = barW;
        return 2;
    }

    public override void Render(DrawingContext ctx)
    {
        // a transparent backdrop so the whole meter (readout included) takes the click
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var t = Palette.Text(holdText, 8, clipL || clipR ? Palette.Rec : Palette.Dim, Palette.Mono);
        ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, 0));

        double h = Bounds.Height - BarTop;
        if (h <= 0) return;
        string lk = look ?? MeterLooks.Current;
        if (MeterLooks.Segmented(lk)) EnsureLeds(h);
        Span<double> xs = stackalloc double[2], ws = stackalloc double[2];
        int n = Bars(xs, ws);
        for (int b = 0; b < n; b++)
        {
            bool clip = mono ? clipL || clipR : b == 0 ? clipL : clipR;
            ctx.DrawRectangle(clip ? Palette.Rec : Palette.Hover, null, new Rect(xs[b], ReadoutH + 1, ws[b], LightH), 2, 2);
            var bar = new Rect(xs[b], BarTop, ws[b], h);
            using (ctx.PushClip(new RoundedRect(bar, 2)))
                Background(ctx, bar, lk);
            if (lk == MeterLooks.Tron)
                ctx.DrawRectangle(null, Palette.Pen(Palette.Alpha(Palette.AccentColor, MeterLooks.TronEdge)), new RoundedRect(bar.Deflate(0.5), 2));
        }
        if (!scale) return;
        Span<int> marks = stackalloc int[ScaleMarks.Length];
        int nMarks = Marks(h, marks);
        for (int k = 0; k < nMarks; k++)
        {
            int db = ScaleMarks[marks[k]];
            var m = Palette.Text(MarkTexts[marks[k]], 8, db == 0 ? Palette.Fg : Palette.Dim, Palette.Mono);
            ctx.DrawText(m, new Point(pairW + 2, BarTop + h - DbToPct(db) * h - m.Height / 2));
        }
    }

    /// <summary>The bars' levels: the RMS solid, the stretch up to the peak dimmed.</summary>
    void RenderLevels(DrawingContext ctx)
    {
        double h = Bounds.Height - BarTop;
        if (h <= 0) return;
        string lk = look ?? MeterLooks.Current;
        if (MeterLooks.Segmented(lk)) EnsureLeds(h);
        Span<double> xs = stackalloc double[2], ws = stackalloc double[2];
        int n = Bars(xs, ws);
        for (int b = 0; b < n; b++)
        {
            double peak = mono ? Math.Max(levelL, levelR) : b == 0 ? levelL : levelR;
            double rms = mono ? Math.Max(rmsL, rmsR) : b == 0 ? rmsL : rmsR;
            double peakPct = Pct(peak), rmsPct = Math.Min(Pct(rms), peakPct);
            if (peakPct <= 0) continue; // silence: nothing over the background
            var bar = new Rect(xs[b], BarTop, ws[b], h);
            using (ctx.PushClip(new RoundedRect(bar, 2)))
            {
                if (MeterLooks.Segmented(lk) && leds != null)
                    using (ctx.PushGeometryClip(leds))
                        Fill(ctx, bar, peakPct, rmsPct, lk);
                else Fill(ctx, bar, peakPct, rmsPct, lk);
            }
        }
    }

    /// <summary>Builds the LEDs' clip for bars <paramref name="h"/> px tall, once per size.</summary>
    void EnsureLeds(double h)
    {
        var area = new Rect(0, BarTop, pairW, h);
        if (leds == null || area != ledsFor) { leds = MeterLooks.Segments(area, true); ledsFor = area; }
    }

    /// <summary>The dB lines over the bars.</summary>
    void RenderMarks(DrawingContext ctx)
    {
        double h = Bounds.Height - BarTop;
        if (h <= 0) return;
        Span<int> marks = stackalloc int[ScaleMarks.Length];
        int nMarks = Marks(h, marks);
        Span<double> xs = stackalloc double[2], ws = stackalloc double[2];
        int n = Bars(xs, ws);
        for (int b = 0; b < n; b++)
            using (ctx.PushClip(new RoundedRect(new Rect(xs[b], BarTop, ws[b], h), 2)))
                for (int k = 0; k < nMarks; k++)
                {
                    int db = ScaleMarks[marks[k]];
                    double y = Math.Round(BarTop + h - DbToPct(db) * h) + 0.5;
                    ctx.DrawLine(Palette.Pen(Palette.Alpha(db == 0 ? Palette.FgColor : Palette.AppColor, db == 0 ? 0.4 : 0.6)), new Point(xs[b], y), new Point(xs[b] + ws[b], y));
                }
    }

    /// <summary>A bar's empty background in <paramref name="lk"/>: <see cref="MeterLooks.Thin"/> a narrow strip in the middle,
    /// <see cref="MeterLooks.Gradient"/> the whole scale faintly, <see cref="MeterLooks.Tron"/> black with its unlit LEDs
    /// faintly in their colors.</summary>
    void Background(DrawingContext ctx, Rect bar, string lk)
    {
        if (lk == MeterLooks.Tron)
        {
            ctx.FillRectangle(Palette.App, bar);
            if (leds != null)
                using (ctx.PushGeometryClip(leds))
                using (ctx.PushOpacity(MeterLooks.TronGhost))
                    ctx.FillRectangle(MeterLooks.TronLevels(), bar);
            return;
        }
        if (lk == MeterLooks.Thin)
        {
            double lw = Math.Max(2, Math.Round(bar.Width * MeterLooks.ThinShare));
            ctx.FillRectangle(Palette.Hover, new Rect(bar.X + Math.Floor((bar.Width - lw) / 2), bar.Y, lw, bar.Height));
        }
        // Hover, a step above both a strip's card and a selected one (Raised), in every surface palette
        else ctx.FillRectangle(Palette.Hover, bar);
        if (lk == MeterLooks.Gradient)
            using (ctx.PushOpacity(MeterLooks.GhostOpacity))
                ctx.FillRectangle(MeterColors.MixerLevels(), bar);
    }

    /// <summary>
    /// A bar's levels in <paramref name="lk"/>, over its background: the RMS solid, the stretch up to the peak dimmed with
    /// a line on the peak. <see cref="MeterLooks.Thin"/> lights a narrow strip in the middle; <see cref="MeterLooks.Led"/>
    /// and <see cref="MeterLooks.Tron"/> (clipped to its LEDs by the caller) light whole LEDs, Tron in its own colors.
    /// </summary>
    static void Fill(DrawingContext ctx, Rect bar, double peakPct, double rmsPct, string lk)
    {
        double lx = bar.X, lw = bar.Width, h = bar.Height, bottom = bar.Bottom;
        if (lk == MeterLooks.Thin)
        {
            lw = Math.Max(2, Math.Round(bar.Width * MeterLooks.ThinShare));
            lx = bar.X + Math.Floor((bar.Width - lw) / 2);
        }
        // the gradient is laid over the whole bar and clipped to each stretch, so its colors stay where the dB are
        double yPeak = bar.Y + h * (1 - peakPct), yRms = bar.Y + h * (1 - rmsPct);
        bool led = MeterLooks.Segmented(lk);
        var grad = lk == MeterLooks.Tron ? MeterLooks.TronLevels() : MeterColors.MixerLevels();
        if (led)
        {
            yPeak = bottom - MeterLooks.SnapLed(bottom - yPeak);
            yRms = bottom - MeterLooks.SnapLed(bottom - yRms);
        }
        if (led ? yRms - yPeak > 0.01 : peakPct > rmsPct)
        {
            using (ctx.PushClip(new Rect(lx, yPeak, lw, yRms - yPeak)))
            using (ctx.PushOpacity(PeakOpacity))
                ctx.FillRectangle(grad, bar);
            // the peak's line; a LED meter lights the peak's whole LED
            using (ctx.PushClip(new Rect(bar.X, yPeak, bar.Width, led ? MeterLooks.SegStep : 1.5)))
                ctx.FillRectangle(grad, bar);
        }
        if (led ? bottom - yRms > 0.01 : rmsPct > 0)
            using (ctx.PushClip(new Rect(lx, yRms, lw, bottom - yRms)))
                ctx.FillRectangle(grad, bar);
    }
}
