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
/// what the knobs say: the panel puts the effect's values in <see cref="Values"/> on every edit (in the order of
/// <see cref="Inputs"/>) and calls <see cref="InvalidateVisual"/>. Over it, while the track sounds, what the effect is doing
/// to the signal (#312, from its <see cref="FxTap"/>): the real wave in and out and a cloud on the transfer curve, the gate's
/// level history, a goniometer in the stereo field, the output's spectrum under a tone response and the real level behind
/// a reverb's or delay's picture, with how much it lifts or cuts in dB. Its color is the effect family's
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

    /// <summary>Ticks without a new point after which the live layer goes (the track stopped sounding): about 0.3 s.</summary>
    const int QuietTicks = 10;

    /// <summary>The running effect's tap (#312: what it is doing to the wave, drawn over what its knobs say); null while the
    /// slot isn't in the engine.</summary>
    public Func<FxTap?>? Live { get; init; }

    FxTap? tap;
    int lastWritten = -1, quiet = QuietTicks;
    /// <summary>The output's spectrum, for the tone effects only (null for the rest).</summary>
    readonly float[]? bins;
    int readTenths = int.MinValue;
    string readText = "";

    public FxScope(string kind, double height)
    {
        Kind = kind;
        Height = height;
        ClipToBounds = true;
        if (kind is TrackInserts.Clean or TrackInserts.Color or TrackInserts.Bands) bins = new float[SpectrumTap.Bins];
        timer.Tick += (_, _) => Tick();
    }

    /// <summary>Keeps the tap fed while it shows, redraws when a new point came (or the signal went), and moves a modulation's
    /// wave while the song plays.</summary>
    void Tick()
    {
        bool redraw = false;
        tap = Live?.Invoke();
        if (tap != null)
        {
            tap.Watch();
            if (bins != null && tap.Spectrum == null) tap.Spectrum = new SpectrumTap();
            int w = tap.Written;
            if (w != lastWritten)
            {
                lastWritten = w;
                quiet = 0;
                redraw = true;
                tap.Spectrum?.Read(bins ?? []);
            }
            else if (quiet < QuietTicks && ++quiet == QuietTicks) redraw = true;
        }
        else if (quiet < QuietTicks)
        {
            quiet = QuietTicks;
            redraw = true;
        }
        if (FxDefs.Family(Kind) == FxFamily.Mod && Running?.Invoke() == true)
        {
            phase = (DateTime.UtcNow - born).TotalSeconds;
            redraw = true;
        }
        if (redraw) InvalidateVisual();
    }

    /// <summary>Whether there is signal to draw: the tap got a point in the last few ticks.</summary>
    bool Fresh => tap != null && quiet < QuietTicks && tap.Written > 0;

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
        timer.Start();
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

    /// <summary>How long the reverb's picture shows at least, and how far past the decay it goes.</summary>
    const double TailSpanMin = 2.5, TailSpanOver = 1.2;

    /// <summary>
    /// The reverb as a room seen in time (asked by the user, #312: it should catch the eye): the dry hit, the pre-delay
    /// gap with its bracket, the early reflections bouncing between the sides and the tail falling 60 dB over the decay,
    /// glowing where it starts and fading out, with sparks of diffusion in it. The left channel above the line and the
    /// right under it: alike when narrow, each its own when wide. A bright tone grains it and lights it towards white.
    /// </summary>
    void Tail(DrawingContext ctx, Rect r, double decay, double pre, double tone, double width)
    {
        decay = Math.Max(0.05, decay);
        double span = Math.Max(TailSpanMin, pre + decay * TailSpanOver);
        double mid = r.Center.Y, half = r.Height / 2;
        double X(double t) => r.Left + t / span * r.Width;
        double bright = Math.Clamp((tone - 1000) / 17000, 0, 1);
        double stereo = Math.Clamp(width, 0, 1);
        double spread = 0.6 + 0.4 * stereo;
        // the tint lit towards white as the tone opens, in eight steps (a cached brush per step, not per value)
        double lift = Math.Round(bright * 8) / 8 * 0.45;
        var hot = Color.FromRgb((byte)(Tint.R + (255 - Tint.R) * lift), (byte)(Tint.G + (255 - Tint.G) * lift), (byte)(Tint.B + (255 - Tint.B) * lift));

        // a grid of seconds, the stronger line every second
        for (double s = 0.5; s < span; s += 0.5)
        {
            double gx = Math.Round(X(s)) + 0.5;
            ctx.DrawLine(Math.Abs(s - Math.Round(s)) < 0.01 ? AxisPen : GridPen, new Point(gx, r.Top), new Point(gx, r.Bottom));
        }
        ctx.DrawLine(GridPen, new Point(r.Left, mid), new Point(r.Right, mid));

        // the tail's body: a column per pixel, each side with its own grain, brightest where it starts
        static double Hash(double i)
        {
            double n = Math.Sin(i * 12.9898) * 43758.5453;
            return n - Math.Floor(n);
        }
        int cols = (int)r.Width;
        double peak = half * 0.88 * spread;
        for (int i = 0; i < cols; i++)
        {
            double t = i / (double)cols * span - pre;
            if (t < 0) continue;
            double fall = 1 - t / decay;
            if (fall <= 0) break;
            double env = fall * fall * (3 - 2 * fall) * 0.35 + fall * 0.65;
            double nl = Hash(i), nr = Hash(i + 0.37);
            // narrow: both sides the same; wide: each its own
            nr = nl + (nr - nl) * stereo;
            double hl = peak * env * (1 - bright * 0.5 * nl);
            double hr = peak * env * (1 - bright * 0.5 * nr);
            double glow = 0.1 + 0.5 * fall * fall;
            var pen = Palette.Pen(Palette.Alpha(Tint, Math.Round(glow * 32) / 32));
            double x = r.Left + i + 0.5;
            ctx.DrawLine(pen, new Point(x, mid - hl), new Point(x, mid + hr));
            // sparks of diffusion: denser early, lit with the tone
            if (Hash(i * 1.618) < 0.18 + 0.3 * fall)
            {
                double up = Hash(i + 0.71), down = Hash(i + 0.53);
                var spark = Palette.Pen(Palette.Alpha(hot, Math.Round((0.35 + 0.6 * fall) * 16) / 16), 1.5, PenLineCap.Round);
                double y1 = mid - hl * up, y2 = mid + hr * down;
                ctx.DrawLine(spark, new Point(x, y1), new Point(x, y1 - 1));
                ctx.DrawLine(spark, new Point(x, y2), new Point(x, y2 + 1));
            }
        }

        // the envelope, glowing: a wide faint stroke under the line
        IPen glowWide = Stroke(0.12, 7), glowNear = Stroke(0.28, 3.5), core = Palette.Pen(Palette.Alpha(hot, 1), 1.75, PenLineCap.Round);
        Point prevTop = default, prevBot = default;
        for (int k = 0; k <= Steps; k++)
        {
            double t = k / (double)Steps * decay;
            double fall = 1 - t / decay;
            double env = fall * fall * (3 - 2 * fall) * 0.35 + fall * 0.65;
            var top = new Point(X(pre + t), mid - peak * env);
            var bot = new Point(top.X, mid + peak * env);
            if (k > 0)
            {
                ctx.DrawLine(glowWide, prevTop, top); ctx.DrawLine(glowWide, prevBot, bot);
                ctx.DrawLine(glowNear, prevTop, top); ctx.DrawLine(glowNear, prevBot, bot);
                ctx.DrawLine(core, prevTop, top); ctx.DrawLine(core, prevBot, bot);
            }
            prevTop = top; prevBot = bot;
        }

        // the dry hit: white, with its glow
        double hx = r.Left + 2;
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Colors.White, 0.12), 7, PenLineCap.Round), new Point(hx, mid - half * 0.92), new Point(hx, mid + half * 0.92));
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Colors.White, 0.85), 2, PenLineCap.Round), new Point(hx, mid - half * 0.92), new Point(hx, mid + half * 0.92));

        // the pre-delay: a bracket from the hit to where the room answers
        double px = X(pre);
        if (px - hx > 6)
        {
            double by = r.Top + 3;
            var dash = Palette.Dashed(Ink(0.7), 1);
            ctx.DrawLine(dash, new Point(hx + 3, by), new Point(px, by));
            ctx.DrawLine(Stroke(0.7, 1), new Point(px, by - 2), new Point(px, by + 4));
        }

        // early reflections right after the pre-delay, bouncing from one side to the other as wide as the width
        var glowRef = Stroke(0.2, 5);
        for (int k = 0; k < 7; k++)
        {
            double t = pre + decay * (0.008 + k * 0.016 * (1 + 0.35 * Hash(k + 3)));
            double h = half * (0.82 - k * 0.09) * spread;
            double skew = (k % 2 == 0 ? 1 : -1) * stereo * 0.35;
            var a = new Point(X(t), mid - Math.Min(half * 0.95, h * (1 + skew)));
            var b = new Point(a.X, mid + h * (1 - skew));
            ctx.DrawLine(glowRef, a, b);
            ctx.DrawLine(Palette.Pen(Palette.Alpha(hot, 0.95 - k * 0.08), 1.5, PenLineCap.Round), a, b);
        }

        // where it is 60 dB down: the decay's end
        double ex = X(pre + decay);
        if (ex < r.Right - 2) ctx.DrawLine(Palette.Dashed(Ink(0.45), 1), new Point(ex, r.Top), new Point(ex, r.Bottom));
        if (Fresh) LiveLevel(ctx, r, span);
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
        if (Fresh) LiveLevel(ctx, r, span);
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
        bool live = Fresh;
        if (live) Cloud(ctx, plot, tap!);
        if (wave.Width < 24) return;
        double mid = wave.Center.Y, half = sq / 2;
        if (live)
        {
            Scope(ctx, wave, mid, half, pen);
            Readout(ctx, wave, tap!);
            return;
        }
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

    // --- live layers (#312) ---------------------------------------------------------------------

    /// <summary>Sample pairs the transfer's cloud shows: the last 20 ms.</summary>
    const int CloudPairs = 256;
    /// <summary>Sample pairs the scope spans across its width: 85 ms at 48 kHz, a few turns of a low note.</summary>
    const int ScopePairs = 1024;
    /// <summary>Level points read for the lift or cut written in a corner: the last 85 ms.</summary>
    const int ReadPoints = 16;
    /// <summary>The level range the live histories draw, dBFS.</summary>
    const double LiveLo = -60;

    static double LevelUnit(double db) => Math.Clamp((db - LiveLo) / -LiveLo, 0, 1);

    /// <summary>
    /// Each recent sample on the transfer square, what came in against what went out, the newest brightest: where the
    /// signal lands on the curve now (a sound that only grazes the bend shows it).
    /// </summary>
    void Cloud(DrawingContext ctx, Rect plot, FxTap t)
    {
        int end = t.PairsWritten, from = Math.Max(0, end - CloudPairs);
        double sq = plot.Width;
        for (int i = from; i < end; i++)
        {
            t.ReadPair(i, out float x, out float y, out _, out _);
            double age = (end - i) / (double)CloudPairs;
            var pen = Palette.Pen(Palette.Alpha(Colors.White, age < 0.25 ? 0.9 : age < 0.6 ? 0.5 : 0.25), 2.5, PenLineCap.Round);
            var p = new Point(plot.Left + (Math.Clamp(x, -1, 1) + 1) / 2 * sq, plot.Center.Y - Math.Clamp(y, -1, 1) * sq / 2);
            ctx.DrawLine(pen, p, new Point(p.X + 0.25, p.Y));
        }
    }

    /// <summary>
    /// An oscilloscope of the last <see cref="ScopePairs"/> samples at full scale: what came in, faint, and what goes out
    /// in the family's color, so a clipped or bent top can be seen against the ceiling's lines.
    /// </summary>
    void Scope(DrawingContext ctx, Rect wave, double mid, double half, IPen outPen)
    {
        var t = tap!;
        int end = t.PairsWritten, cols = Math.Max(1, (int)wave.Width);
        int from = Math.Max(0, end - ScopePairs);
        double per = (end - from) / (double)cols;
        if (per <= 0) return;
        var inPen = Palette.Pen(Palette.Alpha(Colors.White, 0.28), 1.25, PenLineCap.Round);
        ctx.DrawLine(GridPen, new Point(wave.Left, mid), new Point(wave.Right, mid));
        for (int c = 0; c < cols; c++)
        {
            int a = from + (int)(c * per), b = Math.Max(a + 1, from + (int)((c + 1) * per));
            float inLo = 1, inHi = -1, outLo = 1, outHi = -1;
            for (int i = a; i < b && i < end; i++)
            {
                t.ReadPair(i, out float x, out float y, out _, out _);
                if (x < inLo) inLo = x;
                if (x > inHi) inHi = x;
                if (y < outLo) outLo = y;
                if (y > outHi) outHi = y;
            }
            if (inHi < inLo) continue;
            double px = wave.Left + c + 0.5;
            ctx.DrawLine(inPen, new Point(px, mid - Math.Clamp(inHi, -1.2, 1.2) * half), new Point(px, mid - Math.Clamp(inLo, -1.2, 1.2) * half + 0.5));
            ctx.DrawLine(outPen, new Point(px, mid - Math.Clamp(outHi, -1.2, 1.2) * half), new Point(px, mid - Math.Clamp(outLo, -1.2, 1.2) * half + 0.5));
        }
    }

    /// <summary>
    /// How much the effect lifts (+) or cuts (−) the loudest of the last few points, in the corner of <paramref name="r"/>;
    /// nothing while what comes in is under the floor. Its text is rebuilt only when its tenth changes.
    /// </summary>
    void Readout(DrawingContext ctx, Rect r, FxTap t)
    {
        int end = t.Written;
        float inMax = FxTap.Floor, outMax = FxTap.Floor;
        for (int i = Math.Max(0, end - ReadPoints); i < end; i++)
        {
            t.ReadPoint(i, out float a, out float b);
            if (a > inMax) inMax = a;
            if (b > outMax) outMax = b;
        }
        if (inMax <= LiveLo) return;
        int tenths = (int)Math.Round(Math.Clamp(outMax - inMax, -99, 99) * 10);
        if (tenths != readTenths)
        {
            readTenths = tenths;
            readText = tenths == 0 ? "0 dB" : (tenths / 10.0).ToString("+0.0;-0.0") + " dB";
        }
        var text = Palette.Text(readText, 11, Palette.Dim, Palette.Mono);
        ctx.DrawText(text, new Point(r.Right - text.Width - 2, r.Bottom - text.Height));
    }

    /// <summary>
    /// The real level of the last seconds behind a reverb's or a delay's picture, the newest at the right: what goes out as
    /// a faint band around the middle (its tail as it really falls) and what came in as a line on top of it.
    /// </summary>
    void LiveLevel(DrawingContext ctx, Rect r, double span)
    {
        var t = tap!;
        double sec = Math.Min(span, FxTap.Points * t.PointSec * 0.95);
        double x0 = r.Right - sec / span * r.Width;
        int cols = Math.Max(1, (int)((r.Right - x0) / 2));
        int end = t.Written;
        double per = sec / t.PointSec / cols;
        double mid = r.Center.Y, half = r.Height / 2 * 0.95;
        var outPen = Palette.Pen(Palette.Alpha(Colors.White, 0.16), 2);
        var inPen = Palette.Pen(Palette.Alpha(Colors.White, 0.6), 1.25, PenLineCap.Round);
        Point prev = default;
        bool started = false;
        for (int c = 0; c < cols; c++)
        {
            int to = end - (int)Math.Round((cols - 1 - c) * per);
            int from = Math.Max(0, to - Math.Max(1, (int)Math.Round(per)));
            if (to <= from) { started = false; continue; }
            float inMax = FxTap.Floor, outMax = FxTap.Floor;
            for (int i = from; i < to; i++)
            {
                t.ReadPoint(i, out float a, out float b);
                if (a > inMax) inMax = a;
                if (b > outMax) outMax = b;
            }
            double x = x0 + c * 2 + 1;
            double hOut = LevelUnit(outMax) * half;
            if (hOut > 0.5) ctx.DrawLine(outPen, new Point(x, mid - hOut), new Point(x, mid + hOut));
            var p = new Point(x, mid - LevelUnit(inMax) * half);
            if (started) ctx.DrawLine(inPen, prev, p);
            prev = p;
            started = true;
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
        bool live = Fresh;
        if (live)
        {
            // where the signal is now on the curve: the loudest of the last few points
            var t = tap!;
            int end = t.Written;
            float inNow = FxTap.Floor, outNow = FxTap.Floor;
            for (int i = Math.Max(0, end - ReadPoints); i < end; i++)
            {
                t.ReadPoint(i, out float a, out float b);
                if (a > inNow) inNow = a;
                if (b > outNow) outNow = b;
            }
            if (inNow > lo) ctx.DrawEllipse(Palette.Fg, null, new Point(Px(inNow), Py(outNow)), 3.5, 3.5);
        }
        if (wave.Width < 24) return;
        double ty = wave.Bottom - (threshold - lo) / -lo * wave.Height;
        ctx.DrawLine(dash, new Point(wave.Left, ty), new Point(wave.Right, ty));
        if (live)
        {
            GateHistory(ctx, wave, lo);
            Readout(ctx, wave, tap!);
            return;
        }
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

    /// <summary>How much the gate's history shows, in level points (about 4 s).</summary>
    const int GateSpan = 768;

    /// <summary>
    /// The gate's last seconds, the newest at the right: what came in as a faint column and what went out over it in the
    /// family's color, so where it shut (only the faint column left) and how it opens can be seen against the threshold.
    /// </summary>
    void GateHistory(DrawingContext ctx, Rect wave, double lo)
    {
        var t = tap!;
        int end = t.Written, first = Math.Max(0, end - GateSpan);
        int cols = Math.Max(1, (int)(wave.Width / 2));
        double per = GateSpan / (double)cols;
        double Y(double db) => wave.Bottom - (Math.Clamp(db, lo, 0) - lo) / -lo * wave.Height;
        var inPen = Palette.Pen(Palette.Alpha(Colors.White, 0.14), 2);
        var outPen = Palette.Pen(Ink(0.6), 2);
        for (int c = 0; c < cols; c++)
        {
            int to = end - (int)Math.Round((cols - 1 - c) * per);
            int from = Math.Max(first, to - Math.Max(1, (int)Math.Round(per)));
            if (to <= from) continue;
            float inMax = FxTap.Floor, outMax = FxTap.Floor;
            for (int i = from; i < to; i++)
            {
                t.ReadPoint(i, out float a, out float b);
                if (a > inMax) inMax = a;
                if (b > outMax) outMax = b;
            }
            double x = wave.Left + c * 2 + 1;
            if (inMax > lo) ctx.DrawLine(inPen, new Point(x, wave.Bottom), new Point(x, Y(inMax)));
            if (outMax > lo) ctx.DrawLine(outPen, new Point(x, wave.Bottom), new Point(x, Y(outMax)));
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
        if (Fresh) Gonio(ctx, r, c, rad);
    }

    /// <summary>Sample pairs the goniometer shows (every second one drawn): 85 ms at 48 kHz.</summary>
    const int GonioPairs = 1024;

    /// <summary>
    /// What goes out as a half goniometer over the fan: the middle up, the sides across, scaled to its loudest point, so
    /// a mono sound is a line up the middle and a wide one spreads like the fan. On top, the correlation from −1 to +1,
    /// in the mute color when the sides cancel each other.
    /// </summary>
    void Gonio(DrawingContext ctx, Rect r, Point c, double rad)
    {
        var t = tap!;
        int end = t.PairsWritten, from = Math.Max(0, end - GonioPairs);
        double peak = 0, lr = 0, ll = 0, rr = 0;
        for (int i = from; i < end; i++)
        {
            t.ReadPair(i, out _, out _, out float l, out float rt);
            peak = Math.Max(peak, Math.Max(Math.Abs(l + rt), Math.Abs(rt - l)) / 2);
            lr += l * rt;
            ll += l * l;
            rr += rt * rt;
        }
        if (peak < 1e-4) return;
        double scale = rad * 0.9 / peak;
        for (int i = from; i < end; i += 2)
        {
            t.ReadPair(i, out _, out _, out float l, out float rt);
            double m = (l + rt) / 2, s = (rt - l) / 2;
            if (m < 0) { m = -m; s = -s; }
            double age = (end - i) / (double)GonioPairs;
            var pen = Palette.Pen(Palette.Alpha(Colors.White, age < 0.3 ? 0.75 : 0.35), 2, PenLineCap.Round);
            var p = new Point(c.X + s * scale, c.Y - m * scale);
            ctx.DrawLine(pen, p, new Point(p.X + 0.25, p.Y));
        }
        double corr = ll > 1e-9 && rr > 1e-9 ? Math.Clamp(lr / Math.Sqrt(ll * rr), -1, 1) : 1;
        double w = Math.Min(r.Width * 0.4, 120), y = r.Top + 2.5;
        ctx.DrawLine(AxisPen, new Point(r.Center.X - w / 2, y), new Point(r.Center.X + w / 2, y));
        ctx.DrawLine(GridPen, new Point(r.Center.X, y - 2), new Point(r.Center.X, y + 2));
        double cx = r.Center.X + corr * w / 2;
        var mark = corr < 0 ? Palette.Pen(Palette.Mute, 3, PenLineCap.Round) : Stroke(1, 3);
        ctx.DrawLine(mark, new Point(cx, y - 2), new Point(cx, y + 2));
    }

    // --- tone responses --------------------------------------------------------------------------

    const double FLo = 20, FHi = 20000;

    static double FreqAt(double t) => FLo * Math.Pow(FHi / FLo, t);

    /// <summary>The tone effect's response, 20 Hz to 20 kHz, ±12 dB: Limpieza's low cut and mud dip, Color's shelves and tilt.</summary>
    void Response(DrawingContext ctx, Rect r, double[] v)
    {
        const double range = 12;
        FreqGrid(ctx, r);
        if (Fresh) Spectrum(ctx, r);
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

    /// <summary>The level range of the output's spectrum, dB as <see cref="SpectrumTap.Read"/> gives it.</summary>
    const double SpecLo = -100, SpecHi = -15;

    /// <summary>
    /// The output's spectrum behind a tone effect's response, on the same axis (20 Hz to 20 kHz): a faint fill and its
    /// edge, each column the loudest bin under it, so what the effect leaves of the low end or the presence shows as it plays.
    /// </summary>
    void Spectrum(DrawingContext ctx, Rect r)
    {
        if (bins == null) return;
        double binHz = PlaybackSampleRate / (double)SpectrumTap.FftSize;
        int cols = (int)r.Width;
        var fill = Palette.Pen(Palette.Alpha(Colors.White, 0.07));
        var edge = Palette.Pen(Palette.Alpha(Colors.White, 0.32), 1.25, PenLineCap.Round);
        Point prev = default;
        for (int i = 0; i < cols; i++)
        {
            int k0 = (int)(FreqAt(i / (double)cols) / binHz), k1 = (int)(FreqAt((i + 1) / (double)cols) / binHz);
            k0 = Math.Clamp(k0, 1, SpectrumTap.Bins - 1);
            k1 = Math.Clamp(k1, k0, SpectrumTap.Bins - 1);
            float db = float.MinValue;
            for (int k = k0; k <= k1; k++) if (bins[k] > db) db = bins[k];
            double u = Math.Clamp((db - SpecLo) / (SpecHi - SpecLo), 0, 1);
            var p = new Point(r.Left + i + 0.5, r.Bottom - u * r.Height);
            if (u > 0) ctx.DrawLine(fill, new Point(p.X, r.Bottom), p);
            if (i > 0) ctx.DrawLine(edge, prev, p);
            prev = p;
        }
    }

    /// <summary>The engine's rate, which the spectrum's bins are spaced by.</summary>
    const double PlaybackSampleRate = 48000;

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
        if (Fresh) Spectrum(ctx, r);
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
