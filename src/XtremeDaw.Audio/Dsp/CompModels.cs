namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// The settings of a compressor model as the engine takes them, one shape for the five kinds (each reads what it uses).
/// <see cref="InputDb"/>: how hard it is driven (the FET and the Vari-Mu; the level is given back after, so it only adds
/// compression and color). <see cref="ThresholdDb"/>: where it starts (the Óptico's Reducción becomes one,
/// <see cref="OptoCompInsert.ThresholdFor"/>).
/// <see cref="Ratio"/>: <see cref="AllButtons"/> on the FET is its four buttons in at once, <see cref="Infinite"/> a limit.
/// <see cref="ReleaseMs"/> under 0 is the program-dependent release (<see cref="AutoRelease"/>). <see cref="SideHpfHz"/>:
/// the detector's high-pass, 20 or under off. <see cref="Mode"/>: the Óptico's Limitar, the VCA's soft knee and the Vari-Mu's
/// time constant (1 to 6).
/// </summary>
public readonly record struct CompModelParams(double InputDb, double ThresholdDb, double Ratio, double AttackMs, double ReleaseMs, double MakeupDb, double SideHpfHz, int Mode)
{
    public const double AllButtons = 50, Infinite = 1000, AutoRelease = -1;
    public static readonly CompModelParams Defaults = new(6, -18, 4, 10, 200, 0, 0, 0);
}

/// <summary>What the models share: the static curves, the ballistics' coefficients and the meter.</summary>
static class CompModel
{
    public static double Db(double x) => x > 1e-6 ? 20 * Math.Log10(x) : -120;
    public static double Lin(double db) => Math.Pow(10, db / 20);
    public static double ToDb(double lin) => 20 * Math.Log10(lin);
    public static double Coef(double sec, double fs) => Math.Exp(-1 / (Math.Max(1e-6, sec) * fs));
    public static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    /// <summary>A fixed ratio with a soft knee <paramref name="knee"/> dB wide: the reduction (dB, &lt;= 0) for a level <paramref name="over"/> the threshold.</summary>
    public static double Knee(double over, double slope, double knee)
    {
        if (2 * over <= -knee) return 0;
        if (knee > 0 && 2 * Math.Abs(over) < knee) return slope * (over + knee / 2) * (over + knee / 2) / (2 * knee);
        return slope * over;
    }

    /// <summary>
    /// A ratio that grows with the level, as a variable-mu tube's does: 1:1 at the threshold, half way to
    /// <paramref name="maxRatio"/> <paramref name="width"/> dB over it. There is no corner to soften: it starts as a curve.
    /// </summary>
    public static double Variable(double over, double maxRatio, double width)
    {
        if (over <= 0) return 0;
        double r = 1 + (maxRatio - 1) * over / (over + width);
        return over * (1 / r - 1);
    }

    /// <summary>One step of a smoothed reduction: down at <paramref name="attack"/>, back up at <paramref name="release"/>.</summary>
    public static double Follow(double y, double target, double attack, double release) =>
        target < y ? attack * y + (1 - attack) * target : release * y + (1 - release) * target;
}

/// <summary>
/// The tube and transformer color of the models: a tanh curve with a bias, scaled so a quiet signal passes at its own
/// level and only what gets loud bends (the bias tilts it, which brings the even harmonics), and a 10 Hz high-pass that
/// takes out the offset the bias leaves. Its amount comes per sample, so it can follow the reduction.
/// </summary>
sealed class ModelColor(double fs)
{
    const double DcHz = 10;
    readonly double a = Math.Exp(-2 * Math.PI * DcHz / fs);
    double xl, yl, xr, yr;

    public void Reset() => xl = yl = xr = yr = 0;

    static double Shape(double x, double k, double bias)
    {
        double t0 = Math.Tanh(k * bias);
        return (Math.Tanh(k * (x + bias)) - t0) / (k * (1 - t0 * t0));
    }

    public void Process(ref double l, ref double r, double k, double bias)
    {
        if (k <= 0) return;
        double sl = Shape(l, k, bias), sr = Shape(r, k, bias);
        yl = sl - xl + a * yl;
        xl = sl;
        yr = sr - xr + a * yr;
        xr = sr;
        if (Math.Abs(yl) < 1e-20) yl = 0;
        if (Math.Abs(yr) < 1e-20) yr = 0;
        l = yl;
        r = yr;
    }
}

/// <summary>
/// The detector's high-pass (12 dB/oct, two one-pole stages per side): the lows still pass to the output, they just
/// stop pushing the compression, which keeps a bass or a kick from pumping the whole mix.
/// </summary>
sealed class SideHpf
{
    readonly double[] x1 = new double[4], y1 = new double[4];

    public void Reset()
    {
        Array.Clear(x1);
        Array.Clear(y1);
    }

    public double Process(double x, int ch, double a)
    {
        for (int s = 0; s < 2; s++)
        {
            int k = ch * 2 + s;
            double y = a * (y1[k] + x - x1[k]);
            x1[k] = x;
            y1[k] = Math.Abs(y) < 1e-20 ? 0 : y;
            x = y;
        }
        return x;
    }

    public static double Coef(double hz, double fs) => hz <= 20 ? 0 : 1 / (1 + 2 * Math.PI * hz / fs);
}

/// <summary>
/// FET: the fast, forward compressor of the classic studio limiting amplifier. Attack from 20 µs, ratios 4, 8, 12 and 20
/// (each harder in the knee) and the four buttons in at once, where the curve bends past flat (the loudest comes out a
/// little quieter) and the color jumps. Input drives it into a fixed threshold and the level is given back after, so
/// Entrada only adds compression; its transformer color grows with the reduction.
/// </summary>
public sealed class FetCompInsert : ITrackInsert, IKeyed, ICompGraph
{
    /// <summary>The track it listens to instead of its input (a sidechain).</summary>
    public KeyTap Key { get; } = new();
    public CompScope Scope { get; } = new();
    /// <summary>Where it starts, in dBFS before its Entrada.</summary>
    const double Threshold = -10;
    /// <summary>The four buttons in: the slope a little past 20:1's, and the faster release they bring.</summary>
    const double AllSlope = 1.05 * (1.0 / 20 - 1), AllRelease = 0.7;

    readonly double fs;
    readonly ModelColor color;
    volatile Config config;
    double y, minGr;

    sealed record Config(double In, double Out, double Slope, double Knee, double Attack, double Release, double Color, double ColorPerDb);

    public FetCompInsert(int sampleRate)
    {
        fs = sampleRate;
        color = new(fs);
        config = Build(CompModelParams.Defaults);
    }

    Config Build(CompModelParams p)
    {
        double input = CompModel.Clamp(p.InputDb, 0, 48, 6);
        bool all = p.Ratio >= CompModelParams.AllButtons;
        double ratio = CompModel.Clamp(p.Ratio, 1, 20, 4);
        double knee = all ? 0 : ratio <= 4 ? 6 : ratio <= 8 ? 4 : ratio <= 12 ? 3 : 2;
        double attack = CompModel.Clamp(p.AttackMs, 0.02, 0.8, 0.2) / 1000;
        double release = CompModel.Clamp(p.ReleaseMs, 50, 1100, 400) / 1000 * (all ? AllRelease : 1);
        return new(CompModel.Lin(input), CompModel.Lin(-input + CompModel.Clamp(p.MakeupDb, 0, 24, 0)), all ? AllSlope : 1 / ratio - 1, knee,
            CompModel.Coef(attack, fs), CompModel.Coef(release, fs), all ? 0.6 : 0.3, all ? 0.05 : 0.025);
    }

    public double CurveOutDb(double inDb)
    {
        var c = config;
        double x = inDb + CompModel.ToDb(c.In);
        return x + CompModel.Knee(x - Threshold, c.Slope, c.Knee) + CompModel.ToDb(c.Out);
    }

    public string Kind => TrackInserts.Fet;
    public int LatencySamples => 0;
    public void Set(CompModelParams p) => config = Build(p);

    public void Reset()
    {
        y = 0;
        minGr = 0;
        color.Reset();
    }

    public double TakeGrDb()
    {
        double m = minGr;
        minGr = 0;
        return m;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        bool keyed = Key.Take(left.Length, out var kl, out var kr);
        double gr = y, low = minGr;
        for (int i = 0; i < left.Length; i++)
        {
            double inAbs = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            double l = left[i] * c.In, r = right[i] * c.In;
            double over = CompModel.Db(keyed ? Math.Max(Math.Abs(kl[i]), Math.Abs(kr[i])) * c.In : Math.Max(Math.Abs(l), Math.Abs(r))) - Threshold;
            gr = CompModel.Follow(gr, CompModel.Knee(over, c.Slope, c.Knee), c.Attack, c.Release);
            double g = CompModel.Lin(gr);
            l *= g;
            r *= g;
            color.Process(ref l, ref r, c.Color - c.ColorPerDb * gr, 0.08);
            left[i] = (float)(l * c.Out);
            right[i] = (float)(r * c.Out);
            Scope.Add(inAbs, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])), gr);
            if (gr < low) low = gr;
        }
        y = gr;
        minGr = low;
    }
}

/// <summary>
/// Óptico: the electro-optical leveler. A light and a photocell set the reduction, so it reacts in about 10 ms and lets go
/// in two stages: half of it in some 60 ms and the rest over one to five seconds, longer the longer and deeper it has been
/// working (the cell remembers). The knee is wide and the ratio soft (Comprimir, about 3:1) or firm (Limitar). Reducción
/// is the only control that matters; Ganancia brings the level back. A tube output stage adds its even harmonics.
/// </summary>
public sealed class OptoCompInsert : ITrackInsert, IKeyed, ICompGraph
{
    /// <summary>The track it listens to instead of its input (a sidechain).</summary>
    public KeyTap Key { get; } = new();
    public CompScope Scope { get; } = new();
    /// <summary>The threshold at Reducción 0 and how far down the knob takes it, dB.</summary>
    const double Top = -2, Range = 40;

    /// <summary>The threshold for a Reducción (0 to 1): the knob turns the detector's gain up, which is the same as lowering it.</summary>
    public static double ThresholdFor(double reduction) => Top - Range * CompModel.Clamp(reduction, 0, 1, 0.4);
    const double AttackSec = 0.01, FastSec = 0.06, SlowMinSec = 0.5, SlowMaxSec = 5;
    /// <summary>How the cell's memory builds up and fades (s) and how many seconds of slow release each dB of it adds.</summary>
    const double MemorySec = 2, SecPerDb = 0.3;

    readonly double fs;
    readonly ModelColor color;
    readonly double attack, fast, memory;
    volatile Config config;
    double yF, yS, mem, minGr;

    sealed record Config(double Threshold, double Slope, double Knee, double Out);

    public OptoCompInsert(int sampleRate)
    {
        fs = sampleRate;
        color = new(fs);
        attack = CompModel.Coef(AttackSec, fs);
        fast = CompModel.Coef(FastSec, fs);
        memory = CompModel.Coef(MemorySec, fs);
        config = Build(CompModelParams.Defaults);
    }

    Config Build(CompModelParams p)
    {
        bool limit = p.Mode == 1;
        return new(CompModel.Clamp(p.ThresholdDb, Top - Range, Top, Top), limit ? 1.0 / 10 - 1 : 1.0 / 3 - 1, limit ? 6 : 12, CompModel.Lin(CompModel.Clamp(p.MakeupDb, 0, 40, 0)));
    }

    public double CurveOutDb(double inDb)
    {
        var c = config;
        return inDb + CompModel.Knee(inDb - c.Threshold, c.Slope, c.Knee) + CompModel.ToDb(c.Out);
    }

    public string Kind => TrackInserts.Opto;
    public int LatencySamples => 0;
    public void Set(CompModelParams p) => config = Build(p);

    public void Reset()
    {
        yF = yS = mem = minGr = 0;
        color.Reset();
    }

    public double TakeGrDb()
    {
        double m = minGr;
        minGr = 0;
        return m;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double f = yF, s = yS, m = mem, low = minGr;
        bool keyed = Key.Take(left.Length, out var kl, out var kr);
        for (int i = 0; i < left.Length; i++)
        {
            double inAbs = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            double l = left[i], r = right[i];
            double target = CompModel.Knee(CompModel.Db(keyed ? Math.Max(Math.Abs(kl[i]), Math.Abs(kr[i])) : Math.Max(Math.Abs(l), Math.Abs(r))) - c.Threshold, c.Slope, c.Knee);
            double grNow = 0.5 * (f + s);
            m = memory * m + (1 - memory) * -grNow;
            double slow = CompModel.Coef(Math.Clamp(SlowMinSec + SecPerDb * m, SlowMinSec, SlowMaxSec), fs);
            f = CompModel.Follow(f, target, attack, fast);
            s = CompModel.Follow(s, target, attack, slow);
            double gr = 0.5 * (f + s);
            double g = CompModel.Lin(gr);
            l *= g;
            r *= g;
            color.Process(ref l, ref r, 0.35, 0.25);
            left[i] = (float)(l * c.Out);
            right[i] = (float)(r * c.Out);
            Scope.Add(inAbs, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])), gr);
            if (gr < low) low = gr;
        }
        yF = f;
        yS = s;
        mem = m;
        minGr = low;
    }
}

/// <summary>
/// VCA de bus: the console's master-bus compressor that glues a mix. Clean (no color), stepped like the original: ratio
/// 2, 4 or 10, attack 0.1 to 30 ms, release 0.1 to 1.2 s or Auto, where a fast release lets go of the transients and a
/// slow one, that only a sustained level builds, holds the body. The detector's high-pass keeps the low end from pumping it.
/// </summary>
public sealed class BusCompInsert : ITrackInsert, IKeyed, ICompGraph
{
    /// <summary>The track it listens to instead of its input (a sidechain).</summary>
    public KeyTap Key { get; } = new();
    public CompScope Scope { get; } = new();
    const double Knee = 3, AutoFastSec = 0.1, AutoSlowAttackSec = 0.15, AutoSlowSec = 1.2;

    readonly double fs;
    readonly SideHpf hpf = new();
    readonly double slowAttack, slowRelease;
    volatile Config config;
    double yF, yS, minGr;

    sealed record Config(double Threshold, double Slope, double Attack, double Release, bool Auto, double Hpf, double Out);

    public BusCompInsert(int sampleRate)
    {
        fs = sampleRate;
        slowAttack = CompModel.Coef(AutoSlowAttackSec, fs);
        slowRelease = CompModel.Coef(AutoSlowSec, fs);
        config = Build(CompModelParams.Defaults);
    }

    Config Build(CompModelParams p)
    {
        bool auto = p.ReleaseMs < 0;
        double release = auto ? AutoFastSec : CompModel.Clamp(p.ReleaseMs, 100, 1200, 300) / 1000;
        return new(CompModel.Clamp(p.ThresholdDb, -40, 0, -14), 1 / CompModel.Clamp(p.Ratio, 1, 10, 4) - 1,
            CompModel.Coef(CompModel.Clamp(p.AttackMs, 0.1, 30, 30) / 1000, fs), CompModel.Coef(release, fs), auto,
            SideHpf.Coef(p.SideHpfHz, fs), CompModel.Lin(CompModel.Clamp(p.MakeupDb, 0, 20, 0)));
    }

    public double CurveOutDb(double inDb)
    {
        var c = config;
        return inDb + CompModel.Knee(inDb - c.Threshold, c.Slope, Knee) + CompModel.ToDb(c.Out);
    }

    public string Kind => TrackInserts.BusComp;
    public int LatencySamples => 0;
    public void Set(CompModelParams p) => config = Build(p);

    public void Reset()
    {
        yF = yS = minGr = 0;
        hpf.Reset();
    }

    public double TakeGrDb()
    {
        double m = minGr;
        minGr = 0;
        return m;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double f = yF, s = yS, low = minGr;
        bool keyed = Key.Take(left.Length, out var kl, out var kr);
        for (int i = 0; i < left.Length; i++)
        {
            double inAbs = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            double l = left[i], r = right[i];
            double sl = keyed ? kl[i] : l, sr = keyed ? kr[i] : r;
            double dl = c.Hpf > 0 ? hpf.Process(sl, 0, c.Hpf) : sl, dr = c.Hpf > 0 ? hpf.Process(sr, 1, c.Hpf) : sr;
            double target = CompModel.Knee(CompModel.Db(Math.Max(Math.Abs(dl), Math.Abs(dr))) - c.Threshold, c.Slope, Knee);
            f = CompModel.Follow(f, target, c.Attack, c.Release);
            double gr = f;
            if (c.Auto)
            {
                s = CompModel.Follow(s, target, slowAttack, slowRelease);
                gr = Math.Min(f, s);
            }
            double g = CompModel.Lin(gr) * c.Out;
            left[i] = (float)(l * g);
            right[i] = (float)(r * g);
            Scope.Add(inAbs, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])), gr);
            if (gr < low) low = gr;
        }
        yF = f;
        yS = c.Auto ? s : f;
        minGr = low;
    }
}

/// <summary>
/// VCA clásico: the RMS compressor of the late seventies. It hears the average level, not the peaks, so it rides a bass
/// or a kick evenly. Its attack follows the program (about 15 ms for a 10 dB jump, 5 ms for 30 dB) and it lets go at a
/// fixed 120 dB per second, which gives its punch. Knee hard or soft; the ratio goes up to ∞ (a limit).
/// </summary>
public sealed class VcaCompInsert : ITrackInsert, IKeyed, ICompGraph
{
    /// <summary>The track it listens to instead of its input (a sidechain).</summary>
    public KeyTap Key { get; } = new();
    public CompScope Scope { get; } = new();
    const double RmsSec = 0.005, ReleaseDbPerSec = 120;
    /// <summary>The attack's time for each dB the reduction has to go down (s·dB), and its limits (s).</summary>
    const double AttackSecDb = 0.15, AttackMin = 0.002, AttackMax = 0.03;
    const double HardKnee = 0.5, SoftKnee = 10;

    readonly double fs;
    readonly double rms, relStep;
    volatile Config config;
    double ms, y, minGr;

    sealed record Config(double Threshold, double Slope, double Knee, double Out);

    public VcaCompInsert(int sampleRate)
    {
        fs = sampleRate;
        rms = CompModel.Coef(RmsSec, fs);
        relStep = ReleaseDbPerSec / fs;
        config = Build(CompModelParams.Defaults);
    }

    Config Build(CompModelParams p)
    {
        double ratio = p.Ratio >= CompModelParams.Infinite ? double.PositiveInfinity : CompModel.Clamp(p.Ratio, 1, 100, 4);
        return new(CompModel.Clamp(p.ThresholdDb, -50, 0, -20), 1 / ratio - 1, p.Mode == 1 ? SoftKnee : HardKnee,
            CompModel.Lin(CompModel.Clamp(p.MakeupDb, 0, 24, 0)));
    }

    public double CurveOutDb(double inDb)
    {
        var c = config;
        return inDb + CompModel.Knee(inDb - c.Threshold, c.Slope, c.Knee) + CompModel.ToDb(c.Out);
    }

    public string Kind => TrackInserts.Vca;
    public int LatencySamples => 0;
    public void Set(CompModelParams p) => config = Build(p);

    public void Reset() => ms = y = minGr = 0;

    public double TakeGrDb()
    {
        double m = minGr;
        minGr = 0;
        return m;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double sq = ms, gr = y, low = minGr;
        bool keyed = Key.Take(left.Length, out var kl, out var kr);
        for (int i = 0; i < left.Length; i++)
        {
            double inAbs = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            double l = left[i], r = right[i];
            double kx = keyed ? Math.Max(kl[i] * kl[i], kr[i] * kr[i]) : Math.Max(l * l, r * r);
            sq = rms * sq + (1 - rms) * kx;
            if (sq < 1e-30) sq = 0;
            double level = sq > 1e-12 ? 10 * Math.Log10(sq) + 3 : -120; // + 3: a sine's RMS reads its peak, as the meter on the original did
            double target = CompModel.Knee(level - c.Threshold, c.Slope, c.Knee);
            if (target < gr)
            {
                double tau = Math.Clamp(AttackSecDb / Math.Max(1, gr - target), AttackMin, AttackMax);
                double a = Math.Exp(-1 / (tau * fs));
                gr = a * gr + (1 - a) * target;
            }
            else gr = Math.Min(target, gr + relStep);
            double g = CompModel.Lin(gr) * c.Out;
            left[i] = (float)(l * g);
            right[i] = (float)(r * g);
            Scope.Add(inAbs, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])), gr);
            if (gr < low) low = gr;
        }
        ms = sq;
        y = gr;
        minGr = low;
    }
}

/// <summary>
/// Vari-Mu: the variable-mu tube limiter of the fifties. Its ratio grows with the level (gentle at the threshold, near
/// 6:1 well over it), so it has no knee to hear, and its six time constants are the original's: 1 to 4 fixed (300 ms to
/// 5 s), 5 and 6 program dependent, a fast release for single peaks and a slow one that a sustained level builds. Entrada
/// drives the tubes (more color and compression, the level given back after); the color grows with the reduction too.
/// </summary>
public sealed class VariMuInsert : ITrackInsert, IKeyed, ICompGraph
{
    /// <summary>The track it listens to instead of its input (a sidechain).</summary>
    public KeyTap Key { get; } = new();
    public CompScope Scope { get; } = new();
    const double MaxRatio = 6, Width = 10;
    const double AutoSlowAttackSec = 1;

    /// <summary>Each time constant: attack and release (s), and the slow release of the program-dependent ones (0 = none).</summary>
    static readonly (double Attack, double Release, double Slow)[] Times =
    [
        (0.0002, 0.3, 0), (0.0002, 0.8, 0), (0.0004, 2, 0), (0.0008, 5, 0), (0.0002, 2, 10), (0.0004, 0.3, 10),
    ];

    readonly double fs;
    readonly ModelColor color;
    readonly double slowAttack;
    volatile Config config;
    double yF, yS, minGr;

    sealed record Config(double In, double Out, double Threshold, double Attack, double Release, double Slow, double Color);

    public VariMuInsert(int sampleRate)
    {
        fs = sampleRate;
        color = new(fs);
        slowAttack = CompModel.Coef(AutoSlowAttackSec, fs);
        config = Build(CompModelParams.Defaults with { Mode = 2 });
    }

    Config Build(CompModelParams p)
    {
        double input = CompModel.Clamp(p.InputDb, 0, 24, 6);
        var t = Times[Math.Clamp(p.Mode, 1, Times.Length) - 1];
        return new(CompModel.Lin(input), CompModel.Lin(-input + CompModel.Clamp(p.MakeupDb, 0, 24, 0)), CompModel.Clamp(p.ThresholdDb, -40, 0, -18),
            CompModel.Coef(t.Attack, fs), CompModel.Coef(t.Release, fs), t.Slow > 0 ? CompModel.Coef(t.Slow, fs) : 0, 0.15 + 0.035 * input);
    }

    public double CurveOutDb(double inDb)
    {
        var c = config;
        double x = inDb + CompModel.ToDb(c.In);
        return x + CompModel.Variable(x - c.Threshold, MaxRatio, Width) + CompModel.ToDb(c.Out);
    }

    public string Kind => TrackInserts.VariMu;
    public int LatencySamples => 0;
    public void Set(CompModelParams p) => config = Build(p);

    public void Reset()
    {
        yF = yS = minGr = 0;
        color.Reset();
    }

    public double TakeGrDb()
    {
        double m = minGr;
        minGr = 0;
        return m;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double f = yF, s = yS, low = minGr;
        bool keyed = Key.Take(left.Length, out var kl, out var kr);
        for (int i = 0; i < left.Length; i++)
        {
            double inAbs = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            double l = left[i] * c.In, r = right[i] * c.In;
            double target = CompModel.Variable(CompModel.Db(keyed ? Math.Max(Math.Abs(kl[i]), Math.Abs(kr[i])) * c.In : Math.Max(Math.Abs(l), Math.Abs(r))) - c.Threshold, MaxRatio, Width);
            f = CompModel.Follow(f, target, c.Attack, c.Release);
            double gr = f;
            if (c.Slow > 0)
            {
                s = CompModel.Follow(s, target, slowAttack, c.Slow);
                gr = Math.Min(f, s);
            }
            double g = CompModel.Lin(gr);
            l *= g;
            r *= g;
            color.Process(ref l, ref r, c.Color - 0.01 * gr, 0.3);
            left[i] = (float)(l * c.Out);
            right[i] = (float)(r * c.Out);
            Scope.Add(inAbs, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])), gr);
            if (gr < low) low = gr;
        }
        yF = f;
        yS = c.Slow > 0 ? s : f;
        minGr = low;
    }
}
