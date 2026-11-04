using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The pump's display, laid out like KickStart's (asked by the user): one period of the curve, from the duck to the next,
/// drawn big over the sound that goes through it (what comes in, dim, and what goes out, in the accent: the effect's
/// <see cref="PumpInsert.WaveIn"/> / <see cref="PumpInsert.WaveOut"/>), and a line with a dot that rides it with what is
/// sounding now. On the beat the line follows the song; with a sidechain it sits where the key's level puts the gain, and
/// a bar on the right shows the key's level with where it starts to duck (accent) and where it ducks all of its depth
/// (faint). The curve is built
/// only when its settings or its size change; a frame only reads the effect's numbers and redraws if they moved, so it
/// allocates nothing. Without a running effect (stopped, off) the line goes away and the sound fades.
/// </summary>
public sealed class PumpView : Control
{
    const int Points = 128;
    /// <summary>How often it reads the effect, and how long without a new block before the line goes away.</summary>
    const int TickMs = 33, IdleTicks = 8;
    /// <summary>Room around the curve (px) and the key's bar width on the right.</summary>
    const double Pad = 8, KeyBarW = 5, KeyGap = 8;
    /// <summary>How much of the half height the loudest slice of sound takes.</summary>
    const double WaveFill = 0.85;

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    PumpParams settings = PumpParams.Defaults;
    double bpm = 120;
    bool active = true, keyed;
    StreamGeometry? line, fill;
    Size built;
    float phase, gain = 1, key = -1;
    int lastBlocks, still = IdleTicks;

    /// <summary>The running effect, for the line and the sound; null while it isn't in the engine.</summary>
    public Func<PumpInsert?>? Live { get; init; }

    public PumpView()
    {
        Height = 120;
        ClipToBounds = true;
        timer.Tick += (_, _) => Tick();
    }

    /// <summary>The curve's settings, the tempo it follows, whether it listens to a track and whether it is on.</summary>
    public void Set(PumpParams p, double tempo, bool sidechain, bool on)
    {
        if (p == settings && tempo == bpm && sidechain == keyed && on == active && line != null) return;
        settings = p;
        bpm = tempo;
        keyed = sidechain;
        active = on;
        built = default;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Live != null) timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        timer.Stop();
    }

    void Tick()
    {
        var p = Live?.Invoke();
        int blocks = p?.Blocks ?? lastBlocks;
        bool was = still < IdleTicks;
        if (p != null && blocks != lastBlocks)
        {
            lastBlocks = blocks;
            still = 0;
            phase = p.ShownPhase;
            gain = p.ShownGain;
            key = p.ShownKey;
            // the sound under the curve changes with every block, so a running pump redraws on every tick
            InvalidateVisual();
            return;
        }
        if (still < IdleTicks && ++still == IdleTicks && was) InvalidateVisual();
    }

    double CurveW => Math.Max(1, Bounds.Width - 2 * Pad - (keyed ? KeyBarW + KeyGap : 0));
    double X(double ph) => Pad + ph * CurveW;
    double Y(double g) => Pad + (1 - g) * (Bounds.Height - 2 * Pad);

    void Build()
    {
        built = Bounds.Size;
        line = new StreamGeometry();
        fill = new StreamGeometry();
        double bottom = Bounds.Height;
        using var lc = line.Open();
        using var fc = fill.Open();
        fc.BeginFigure(new Point(X(0), bottom), true);
        for (int i = 0; i <= Points; i++)
        {
            double ph = i / (double)Points;
            var pt = new Point(X(ph), Y(PumpInsert.GainAt(settings, bpm, ph, 48000, keyed)));
            if (i == 0) lc.BeginFigure(pt, false);
            else lc.LineTo(pt);
            fc.LineTo(pt);
        }
        fc.LineTo(new Point(X(1), bottom));
        fc.EndFigure(true);
        lc.EndFigure(false);
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (size.Width < 20 || size.Height < 20) return;
        if (built != size || line == null) Build();
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), new Rect(size), 6, 6);

        bool running = active && still < IdleTicks;
        using (ctx.PushClip(new RoundedRect(new Rect(size).Deflate(1), 6)))
        {
            if (Live?.Invoke() is { } p) Wave(ctx, p, running ? 1 : 0.35);
            double alpha = active ? 1 : 0.4;
            ctx.DrawGeometry(Palette.Alpha(Palette.AccentColor, 0.08 * alpha), null, fill!);
            ctx.DrawGeometry(null, Palette.Pen(active ? Palette.Accent : Palette.Dim, 2.5), line!);
        }

        if (running)
        {
            double x = Math.Round(X(phase)) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.FgColor, 0.7)), new Point(x, 1), new Point(x, size.Height - 1));
            ctx.DrawEllipse(Palette.Fg, Palette.Pen(Palette.Accent, 2), new Point(X(phase), Y(gain)), 4, 4);
        }

        if (!keyed) return;
        // the key: its level, the line where a hit fires and the one it has to fall under again
        double bx = size.Width - Pad - KeyBarW, h = size.Height - 2 * Pad;
        ctx.DrawRectangle(Palette.Hover, null, new Rect(bx, Pad, KeyBarW, h), 2, 2);
        if (running && key > 0)
            ctx.DrawRectangle(Palette.Dim, null, new Rect(bx, Pad + (1 - key) * h, KeyBarW, key * h), 2, 2);
        // automatic: the bar is against the key's recent peak, and the threshold sits a span under it
        double thr = settings.ThresholdDb <= PumpParams.ThresholdAutoDb ? -PumpInsert.SpanDb : settings.ThresholdDb;
        double from = Math.Round(Pad + (1 - PumpInsert.KeyMeter(thr)) * h) + 0.5, full = Math.Round(Pad + (1 - PumpInsert.KeyMeter(thr + PumpInsert.SpanDb)) * h) + 0.5;
        ctx.DrawLine(Palette.Pen(Palette.Accent), new Point(bx - 2, from), new Point(bx + KeyBarW + 2, from));
        ctx.DrawLine(Palette.Pen(Palette.Faint), new Point(bx - 2, full), new Point(bx + KeyBarW + 2, full));
    }

    /// <summary>The sound that went through each slice of the curve: what came in, dim, and what went out over it.</summary>
    void Wave(DrawingContext ctx, PumpInsert p, double alpha)
    {
        float max = 0;
        for (int i = 0; i < PumpInsert.WaveBins; i++) max = Math.Max(max, p.WaveIn[i]);
        if (max < 1e-4f) return;
        double mid = Bounds.Height / 2, scale = (mid - Pad) * WaveFill / max, w = CurveW / PumpInsert.WaveBins;
        var dim = Palette.Alpha(Palette.FaintColor, 0.55 * alpha);
        var lit = Palette.Alpha(Palette.AccentColor, 0.45 * alpha);
        double bw = Math.Max(1, w - 1);
        for (int i = 0; i < PumpInsert.WaveBins; i++)
        {
            double x = X(i / (double)PumpInsert.WaveBins);
            double a = p.WaveIn[i] * scale, b = Math.Min(a, p.WaveOut[i] * scale);
            if (a >= 0.5) ctx.DrawRectangle(dim, null, new Rect(x, mid - a, bw, 2 * a));
            if (b >= 0.5) ctx.DrawRectangle(lit, null, new Rect(x, mid - b, bw, 2 * b));
        }
    }
}

/// <summary>
/// One of the pump's shapes as a small drawing (the buttons under its display, like KickStart's): the curve a length and
/// a curve give, in the accent when it is the one in use. Built once per size and state.
/// </summary>
public sealed class PumpShapeIcon : Control
{
    const double Pad = 5;
    readonly double length, curve;
    StreamGeometry? line;
    Size built;
    bool lit;

    public PumpShapeIcon(double length, double curve)
    {
        this.length = length;
        this.curve = curve;
        Height = 26;
    }

    /// <summary>Whether it is the shape in use.</summary>
    public bool Lit
    {
        get => lit;
        set { if (lit == value) return; lit = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (size.Width < 8 || size.Height < 8) return;
        if (line == null || built != size)
        {
            built = size;
            line = new StreamGeometry();
            using var g = line.Open();
            var p = PumpParams.Defaults with { Depth = 1, Length = length, Curve = curve, AttackMs = PumpParams.AttackMaxMs };
            const int n = 40;
            for (int i = 0; i <= n; i++)
            {
                double ph = i / (double)n;
                var pt = new Point(Pad + ph * (size.Width - 2 * Pad), Pad + (1 - PumpInsert.GainAt(p, 120, ph, 48000)) * (size.Height - 2 * Pad));
                if (i == 0) g.BeginFigure(pt, false);
                else g.LineTo(pt);
            }
            g.EndFigure(false);
        }
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        ctx.DrawGeometry(null, Palette.Pen(lit ? Palette.Accent : Palette.Dim, lit ? 2 : 1.5), line);
    }
}
