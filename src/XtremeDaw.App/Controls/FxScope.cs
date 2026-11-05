using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// An effect's own picture (asked by the user, #187: each native effect with an identity, like the compressor's graph):
/// the reverb's tail, the delay's echoes, a modulation's wave (moving while the song plays), a saturation's curve with a
/// sine through it, the gate's and the limiter's transfer, the stereo field and the tone effects' response. It draws
/// what the knobs say, not the signal: the panel puts the effect's values in <see cref="Values"/> on every edit (in the
/// order of <see cref="Inputs"/>) and calls <see cref="InvalidateVisual"/>. Its color is the effect family's
/// (<see cref="Tint"/>). Nothing in <see cref="Render"/> allocates: short lines with cached pens, fills as 1 px columns.
/// </summary>
public sealed class FxScope : Control
{
    /// <summary>How often a modulation's wave moves while the song plays.</summary>
    const int TickMs = 33;
    /// <summary>Room around the picture inside its box.</summary>
    const double Pad = 6;
    /// <summary>A transfer curve's square, from the left, and the room between it and the sine beside it.</summary>
    const double Gap = 10;
    /// <summary>Points per drawn curve: smooth at any width, few enough lines for every redraw.</summary>
    const int Steps = 96;

    static readonly IPen GridPen = Palette.Pen(Palette.Alpha(Colors.White, 0.06));
    static readonly IPen AxisPen = Palette.Pen(Palette.Alpha(Colors.White, 0.14));
    static readonly IBrush Back = Palette.Alpha(Colors.Black, 0.25);

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    readonly DateTime born = DateTime.UtcNow;
    double phase;

    /// <summary>The effect kind it draws (<see cref="Has"/> says which have a picture).</summary>
    public string Kind { get; }
    /// <summary>The effect's values, in the order of <see cref="Inputs"/>; set by the panel on every edit.</summary>
    public double[] Values { get; } = new double[MaxInputs];
    /// <summary>A delay in ping-pong: its echoes go from side to side.</summary>
    public bool PingPong { get; set; }
    /// <summary>A saturation in its tape mode (softer and symmetric) instead of the tube's.</summary>
    public bool TapeMode { get; set; }
    public Color Tint { get; set; }
    /// <summary>Whether the song is playing: a modulation's wave moves only then.</summary>
    public Func<bool>? Running { get; init; }

    const int MaxInputs = 6;

    public FxScope(string kind, double height)
    {
        Kind = kind;
        Height = height;
        ClipToBounds = true;
        timer.Tick += (_, _) =>
        {
            if (Running?.Invoke() != true) return;
            phase = (DateTime.UtcNow - born).TotalSeconds;
            InvalidateVisual();
        };
    }

    /// <summary>Whether a kind has a picture (the compressors have their graph, the EQ its editor, the pump its panel).</summary>
    public static bool Has(string kind) => Inputs(kind).Length > 0;

    /// <summary>The parameter ids a kind's picture reads, in order (ids it doesn't have read as 0).</summary>
    public static string[] Inputs(string kind) => kind switch
    {
        InsertSlot.ReverbKind => ["decay", "pre", "tone", "width"],
        InsertSlot.DelayKind => ["time", "repeats", "width"],
        TrackInserts.Chorus or TrackInserts.Flanger or TrackInserts.Phaser or TrackInserts.Tremolo => ["rate", "depth", "shape", "spread", "feedback"],
        InsertSlot.SatKind or TrackInserts.Tape => ["amount"],
        TrackInserts.Clip => ["recorte", "ceiling"],
        TrackInserts.Limit => ["drive", "ceiling"],
        InsertSlot.GateKind => ["threshold"],
        TrackInserts.Width => ["width", "monoBass"],
        TrackInserts.Clean => ["lowCut", "mud"],
        TrackInserts.Color => ["lows", "presence", "air", "tilt"],
        TrackInserts.Bands => ["low", "mid", "high", "pick", "lowX", "highX"],
        _ => [],
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (FxDefs.Family(Kind) == FxFamily.Mod) timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        timer.Stop();
    }

    IBrush Ink(double a) => Palette.Alpha(Tint, a);
    IPen Stroke(double a = 1, double w = 1.5) => Palette.Pen(Ink(a), w, PenLineCap.Round);

    public override void Render(DrawingContext ctx)
    {
        var box = new Rect(Bounds.Size);
        ctx.DrawRectangle(Back, Palette.Pen(Palette.Alpha(Tint, 0.22)), box.Deflate(0.5), 6, 6);
        var r = box.Deflate(Pad);
        if (r.Width < 20 || r.Height < 12) return;
        double[] v = Values;
        switch (Kind)
        {
            case InsertSlot.ReverbKind: Tail(ctx, r, v[0], v[1] / 1000, v[2], v[3]); break;
            case InsertSlot.DelayKind: Echoes(ctx, r, v[0] / 1000, v[1], v[2]); break;
            case TrackInserts.Chorus or TrackInserts.Flanger or TrackInserts.Phaser or TrackInserts.Tremolo: Wave(ctx, r, v[0], v[1], v[2], v[3]); break;
            case InsertSlot.SatKind or TrackInserts.Tape: Transfer(ctx, r, 0, v[0], 0); break;
            case TrackInserts.Clip: Transfer(ctx, r, 1, v[0], v[1]); break;
            case TrackInserts.Limit: Transfer(ctx, r, 2, v[0], v[1]); break;
            case InsertSlot.GateKind: Gate(ctx, r, v[0]); break;
            case TrackInserts.Width: Field(ctx, r, v[0], v[1]); break;
            case TrackInserts.Clean or TrackInserts.Color: Response(ctx, r, v); break;
            case TrackInserts.Bands: Bands(ctx, r, v); break;
        }
    }

    // --- the reverb's tail -----------------------------------------------------------------------

    /// <summary>
    /// The dry hit, the pre-delay gap, a few early reflections and the tail falling 60 dB over the decay, mirrored like a
    /// waveform: as wide as the stereo width, as grainy as the tone is bright.
    /// </summary>
    void Tail(DrawingContext ctx, Rect r, double decay, double pre, double tone, double width)
    {
        double span = Math.Max(2.5, pre + decay * 1.2);
        double mid = r.Center.Y, half = r.Height / 2;
        ctx.DrawLine(GridPen, new Point(r.Left, mid), new Point(r.Right, mid));
        double X(double t) => r.Left + t / span * r.Width;
        ctx.DrawLine(Stroke(0.5, 2), new Point(r.Left + 1, mid - half * 0.95), new Point(r.Left + 1, mid + half * 0.95));
        double grain = Math.Clamp((tone - 1000) / 17000, 0, 1);
        double spread = 0.55 + 0.45 * Math.Clamp(width, 0, 1);
        var fill = Ink(0.28);
        int cols = (int)r.Width;
        for (int i = 0; i < cols; i++)
        {
            double t = i / (double)cols * span - pre;
            if (t < 0) continue;
            double db = -60 * t / Math.Max(0.05, decay);
            if (db < -60) break;
            double a = 1 + db / 60;
            // a fixed hash, not random: the picture doesn't shimmer between redraws
            double n = Math.Sin(i * 12.9898) * 43758.5453;
            n -= Math.Floor(n);
            a *= 1 - grain * 0.45 * n;
            double h = half * 0.85 * a * spread;
            double x = r.Left + i + 0.5;
            ctx.DrawLine(Palette.Pen(fill), new Point(x, mid - h), new Point(x, mid + h));
        }
        // the envelope over it
        var pen = Stroke();
        Point prevTop = default, prevBot = default;
        for (int k = 0; k <= Steps; k++)
        {
            double t = k / (double)Steps * decay;
            double a = 1 - t / Math.Max(0.05, decay);
            var top = new Point(X(pre + t), mid - half * 0.85 * a * spread);
            var bot = new Point(top.X, mid + half * 0.85 * a * spread);
            if (k > 0) { ctx.DrawLine(pen, prevTop, top); ctx.DrawLine(pen, prevBot, bot); }
            prevTop = top; prevBot = bot;
        }
        // early reflections, right after the pre-delay
        for (int k = 0; k < 5; k++)
        {
            double t = pre + decay * (0.01 + k * 0.018);
            double h = half * (0.7 - k * 0.1) * spread;
            ctx.DrawLine(Stroke(0.85, 1.5), new Point(X(t), mid - h), new Point(X(t), mid + h));
        }
    }

    // --- the delay's echoes ----------------------------------------------------------------------

    /// <summary>One bar per echo, each the repeats' share of the one before; in ping-pong they go from one side to the other.</summary>
    void Echoes(DrawingContext ctx, Rect r, double time, double feedback, double width)
    {
        double span = Math.Max(2.5, time * 7);
        double mid = r.Center.Y, half = r.Height / 2 * 0.9;
        ctx.DrawLine(GridPen, new Point(r.Left, mid), new Point(r.Right, mid));
        for (double s = 0.5; s < span; s += 0.5)
        {
            double gx = r.Left + s / span * r.Width;
            ctx.DrawLine(GridPen, new Point(gx, r.Top), new Point(gx, r.Bottom));
        }
        double w = Math.Clamp(r.Width / 60, 2, 5);
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Colors.White, 0.35), w, PenLineCap.Round), new Point(r.Left + w, mid - half), new Point(r.Left + w, mid + half));
        double a = 1;
        bool side = true;
        double stereo = 0.4 + 0.6 * Math.Clamp(width, 0, 1);
        for (int n = 1; n < 64; n++)
        {
            a = n == 1 ? 0.85 : a * feedback;
            double t = n * time;
            if (a < 0.03 || t > span) break;
            double x = r.Left + t / span * r.Width;
            var pen = Palette.Pen(Ink(0.35 + 0.65 * a), w, PenLineCap.Round);
            if (PingPong)
            {
                double h = half * a;
                ctx.DrawLine(pen, new Point(x, mid), new Point(x, side ? mid - h : mid + h * stereo));
                side = !side;
            }
            else ctx.DrawLine(pen, new Point(x, mid - half * a), new Point(x, mid + half * a));
        }
    }

    // --- a modulation's wave ---------------------------------------------------------------------

    /// <summary>
    /// Two seconds of the LFO: its depth is the height and the rate how many turns; the tremolo's shape squares it. The
    /// right side (spread) is a fainter wave shifted against it. It runs while the song plays.
    /// </summary>
    void Wave(DrawingContext ctx, Rect r, double rate, double depth, double shape, double spread)
    {
        const double span = 2;
        double mid = r.Center.Y, half = r.Height / 2 * 0.9;
        ctx.DrawLine(GridPen, new Point(r.Left, mid), new Point(r.Right, mid));
        ctx.DrawLine(GridPen, new Point(r.Left, mid - half), new Point(r.Right, mid - half));
        ctx.DrawLine(GridPen, new Point(r.Left, mid + half), new Point(r.Right, mid + half));
        bool square = Kind == TrackInserts.Tremolo;
        double k = 1 + shape * 12;
        double Lfo(double t, double off)
        {
            double s = Math.Sin(2 * Math.PI * (rate * (t + phase)) + off);
            return square ? Math.Tanh(k * s) / Math.Tanh(k) : s;
        }
        int n = Math.Max(Steps, (int)r.Width);
        double amp = half * Math.Clamp(depth, 0, 1);
        void Curve(double off, IPen pen)
        {
            var prev = default(Point);
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n * span;
                var p = new Point(r.Left + i / (double)n * r.Width, mid - amp * Lfo(t, off));
                if (i > 0) ctx.DrawLine(pen, prev, p);
                prev = p;
            }
        }
        if (spread > 0.01) Curve(Math.PI * Math.Clamp(spread, 0, 1), Stroke(0.35));
        Curve(0, Stroke(1, 1.75));
    }

    // --- transfer curves -------------------------------------------------------------------------

    /// <summary>
    /// What comes out against what goes in, -1 to 1 on both axes, and a sine through it beside: a saturation (0) bends
    /// it by its amount, a clipper (1) rounds it into its ceiling, a limiter (2) flattens it there after its drive.
    /// </summary>
    void Transfer(DrawingContext ctx, Rect r, int type, double a, double b)
    {
        double sq = Math.Min(r.Height, r.Width * 0.4);
        var plot = new Rect(r.Left, r.Top + (r.Height - sq) / 2, sq, sq);
        var wave = new Rect(plot.Right + Gap, r.Top, Math.Max(0, r.Right - plot.Right - Gap), r.Height);
        ctx.DrawLine(GridPen, new Point(plot.Center.X, plot.Top), new Point(plot.Center.X, plot.Bottom));
        ctx.DrawLine(GridPen, new Point(plot.Left, plot.Center.Y), new Point(plot.Right, plot.Center.Y));
        ctx.DrawLine(AxisPen, plot.BottomLeft, plot.TopRight);
        double ceiling = type == 0 ? 1 : Math.Pow(10, b / 20);
        if (type != 0)
        {
            var dash = Palette.Dashed(Ink(0.6), 1);
            double cy = plot.Center.Y - ceiling * sq / 2;
            ctx.DrawLine(dash, new Point(plot.Left, cy), new Point(wave.Right, cy));
            double cy2 = plot.Center.Y + ceiling * sq / 2;
            ctx.DrawLine(dash, new Point(plot.Left, cy2), new Point(wave.Right, cy2));
        }
        double F(double x) => Shape(type, a, ceiling, x);
        var pen = Stroke(1, 1.75);
        var prev = default(Point);
        for (int i = 0; i <= Steps; i++)
        {
            double x = -1 + 2.0 * i / Steps;
            var p = new Point(plot.Left + (x + 1) / 2 * sq, plot.Center.Y - F(x) * sq / 2);
            if (i > 0) ctx.DrawLine(pen, prev, p);
            prev = p;
        }
        if (wave.Width < 24) return;
        double mid = wave.Center.Y, half = sq / 2;
        int n = Math.Max(Steps, (int)wave.Width);
        var inPen = Palette.Pen(Palette.Alpha(Colors.White, 0.22), 1.25, PenLineCap.Round);
        Point pi = default, po = default;
        for (int i = 0; i <= n; i++)
        {
            double t = i / (double)n;
            double s = Math.Sin(2 * Math.PI * 2 * t);
            var a1 = new Point(wave.Left + t * wave.Width, mid - s * half);
            var a2 = new Point(a1.X, mid - F(s) * half);
            if (i > 0) { ctx.DrawLine(inPen, pi, a1); ctx.DrawLine(pen, po, a2); }
            pi = a1; po = a2;
        }
    }

    double Shape(int type, double a, double ceiling, double x)
    {
        switch (type)
        {
            case 0:
                {
                    // tube: driven harder and a little lopsided; tape: softer and even
                    double g = 1 + a * (TapeMode || Kind == TrackInserts.Tape ? 5 : 9);
                    double bias = TapeMode || Kind == TrackInserts.Tape ? 0 : 0.18 * a;
                    double y = Math.Tanh(g * (x + bias)) - Math.Tanh(g * bias);
                    double norm = Math.Max(Math.Tanh(g * (1 + bias)) - Math.Tanh(g * bias), Math.Tanh(g * bias) - Math.Tanh(g * (-1 + bias)));
                    return y / norm;
                }
            case 1:
                {
                    double g = Math.Pow(10, (a <= 0 ? SoftClipper.MaxDb / 2 : a) / 20);
                    return ceiling * Math.Tanh(g * x / ceiling);
                }
            default:
                {
                    double y = Math.Pow(10, a / 20) * x;
                    double knee = ceiling * 0.9;
                    double m = Math.Abs(y);
                    if (m > knee) m = knee + (ceiling - knee) * Math.Tanh((m - knee) / (ceiling - knee));
                    return Math.Sign(y) * Math.Min(m, ceiling);
                }
        }
    }

    // --- the gate --------------------------------------------------------------------------------

    /// <summary>Its transfer in dB (closed under the threshold) and, beside it, three hits: what passes and what it shuts.</summary>
    void Gate(DrawingContext ctx, Rect r, double threshold)
    {
        const double lo = -80;
        double sq = Math.Min(r.Height, r.Width * 0.4);
        var plot = new Rect(r.Left, r.Top + (r.Height - sq) / 2, sq, sq);
        double Px(double db) => plot.Left + (db - lo) / -lo * sq;
        double Py(double db) => plot.Bottom - (Math.Max(lo, db) - lo) / -lo * sq;
        ctx.DrawLine(AxisPen, plot.BottomLeft, plot.TopRight);
        var dash = Palette.Dashed(Ink(0.6), 1);
        ctx.DrawLine(dash, new Point(Px(threshold), plot.Top), new Point(Px(threshold), plot.Bottom));
        var pen = Stroke(1, 1.75);
        var prev = default(Point);
        for (int i = 0; i <= Steps; i++)
        {
            double x = lo + -lo * i / Steps;
            double y = x >= threshold ? x : threshold + (x - threshold) * 5;
            var p = new Point(Px(x), Py(y));
            if (i > 0) ctx.DrawLine(pen, prev, p);
            prev = p;
        }
        var wave = new Rect(plot.Right + Gap, r.Top, Math.Max(0, r.Right - plot.Right - Gap), r.Height);
        if (wave.Width < 24) return;
        double ty = wave.Bottom - (threshold - lo) / -lo * wave.Height;
        ctx.DrawLine(dash, new Point(wave.Left, ty), new Point(wave.Right, ty));
        int cols = (int)wave.Width;
        var under = Palette.Pen(Palette.Alpha(Colors.White, 0.1));
        var over = Palette.Pen(Ink(0.55));
        for (int i = 0; i < cols; i++)
        {
            double t = i / (double)cols * 3;
            double local = t - Math.Floor(t);
            double db = -6 - local * 70;
            double y = wave.Bottom - (db - lo) / -lo * wave.Height;
            double x = wave.Left + i + 0.5;
            ctx.DrawLine(db >= threshold ? over : under, new Point(x, wave.Bottom), new Point(x, y));
        }
    }

    // --- the stereo field ------------------------------------------------------------------------

    /// <summary>
    /// The field seen from the listener: a fan as open as the width (100 % = the speakers, 200 % = past them), and the bass
    /// under the mono frequency drawn as a narrow beam in the middle.
    /// </summary>
    void Field(DrawingContext ctx, Rect r, double width, double monoBass)
    {
        var c = new Point(r.Center.X, r.Bottom);
        double rad = Math.Min(r.Height, r.Width / 2) * 0.98;
        for (int k = -2; k <= 2; k++)
        {
            double a = k * 45 * Math.PI / 180;
            ctx.DrawLine(GridPen, c, new Point(c.X + Math.Sin(a) * rad, c.Y - Math.Cos(a) * rad));
        }
        // the speakers' ±30°
        foreach (int s in (ReadOnlySpan<int>)[-1, 1])
        {
            double a = s * 30 * Math.PI / 180;
            ctx.DrawLine(AxisPen, c, new Point(c.X + Math.Sin(a) * rad, c.Y - Math.Cos(a) * rad));
        }
        double open = Math.Clamp(width, 0, StereoWidth.MaxWidth) * 30;
        var fill = Palette.Pen(Ink(0.22), 2);
        int n = Math.Max(2, (int)(open * 2));
        for (int i = 0; i <= n; i++)
        {
            double a = (-open + 2 * open * i / n) * Math.PI / 180;
            ctx.DrawLine(fill, c, new Point(c.X + Math.Sin(a) * rad * 0.94, c.Y - Math.Cos(a) * rad * 0.94));
        }
        var pen = Stroke(1, 1.75);
        double ar = open * Math.PI / 180;
        ctx.DrawLine(pen, c, new Point(c.X + Math.Sin(ar) * rad * 0.94, c.Y - Math.Cos(ar) * rad * 0.94));
        ctx.DrawLine(pen, c, new Point(c.X - Math.Sin(ar) * rad * 0.94, c.Y - Math.Cos(ar) * rad * 0.94));
        if (monoBass > 0)
        {
            double share = Math.Log(monoBass / 20) / Math.Log(StereoWidth.MonoBassMaxHz / 20);
            ctx.DrawLine(Palette.Pen(Palette.Alpha(Colors.White, 0.55), 3, PenLineCap.Round), c, new Point(c.X, c.Y - rad * (0.2 + 0.5 * share)));
        }
    }

    // --- tone responses --------------------------------------------------------------------------

    const double FLo = 20, FHi = 20000;

    static double FreqAt(double t) => FLo * Math.Pow(FHi / FLo, t);

    /// <summary>The tone effect's response, 20 Hz to 20 kHz, ±12 dB: Limpieza's low cut and mud dip, Color's shelves and tilt.</summary>
    void Response(DrawingContext ctx, Rect r, double[] v)
    {
        const double range = 12;
        FreqGrid(ctx, r);
        double mid = r.Center.Y;
        ctx.DrawLine(AxisPen, new Point(r.Left, mid), new Point(r.Right, mid));
        bool clean = Kind == TrackInserts.Clean;
        double Db(double f)
        {
            if (clean)
            {
                double cut = v[0] > 0 ? -10 * Math.Log10(1 + Math.Pow(v[0] / f, 4)) : 0;
                return cut + Bell(f, MasterEq.MudHz, v[1], 1.2);
            }
            double tilt = v[3] * Math.Clamp(Math.Log10(f / MasterEq.TiltHz) / Math.Log10(FHi / MasterEq.TiltHz), -1, 1);
            return v[0] / (1 + (f / MasterEq.LowsHz) * (f / MasterEq.LowsHz)) + Bell(f, MasterEq.PresenceHz, v[1], 1.5)
                + v[2] / (1 + (MasterEq.AirHz / f) * (MasterEq.AirHz / f)) + tilt;
        }
        var fill = Palette.Pen(Ink(0.2));
        var pen = Stroke(1, 1.75);
        int cols = (int)r.Width;
        var prev = default(Point);
        for (int i = 0; i <= cols; i++)
        {
            double db = Math.Clamp(Db(FreqAt(i / (double)cols)), -range * 1.5, range);
            var p = new Point(r.Left + i, mid - db / range * (r.Height / 2));
            ctx.DrawLine(fill, new Point(p.X, mid), new Point(p.X, Math.Min(r.Bottom, p.Y)));
            if (i > 0) ctx.DrawLine(pen, prev, p);
            prev = p;
        }
    }

    static double Bell(double f, double f0, double gain, double octaves)
    {
        double o = Math.Log2(f / f0) / (octaves / 2);
        return gain * Math.Exp(-o * o);
    }

    void FreqGrid(DrawingContext ctx, Rect r)
    {
        foreach (double f in (ReadOnlySpan<double>)[100, 1000, 10000])
        {
            double x = r.Left + Math.Log(f / FLo) / Math.Log(FHi / FLo) * r.Width;
            ctx.DrawLine(GridPen, new Point(x, r.Top), new Point(x, r.Bottom));
        }
    }

    /// <summary>The three bands between their crossovers, each filled as much as it works, and the pick (transients) as a line on top.</summary>
    void Bands(DrawingContext ctx, Rect r, double[] v)
    {
        FreqGrid(ctx, r);
        double X(double f) => r.Left + Math.Log(Math.Clamp(f, FLo, FHi) / FLo) / Math.Log(FHi / FLo) * r.Width;
        double x1 = X(v[4]), x2 = X(v[5]);
        var dash = Palette.Dashed(Ink(0.6), 1);
        ctx.DrawLine(dash, new Point(x1, r.Top), new Point(x1, r.Bottom));
        ctx.DrawLine(dash, new Point(x2, r.Top), new Point(x2, r.Bottom));
        void Band(double from, double to, double amount)
        {
            double h = r.Height * 0.9 * Math.Clamp(amount, 0, 1);
            if (h < 1) return;
            ctx.DrawRectangle(Ink(0.25), Stroke(0.9, 1), new Rect(from + 3, r.Bottom - h, Math.Max(0, to - from - 6), h), 3, 3);
        }
        Band(r.Left, x1, v[0]);
        Band(x1, x2, v[1]);
        Band(x2, r.Right, v[2]);
        if (v[3] > 0.01)
        {
            double y = r.Top + r.Height * (1 - 0.95 * Math.Clamp(v[3], 0, 1));
            ctx.DrawLine(Palette.Pen(Palette.Alpha(Colors.White, 0.5), 1.25, PenLineCap.Round), new Point(r.Left, y), new Point(r.Right, y));
        }
    }
}
