namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// The settings of a modulation effect as the engine takes them, one shape for the four kinds (each reads what it uses).
/// <see cref="RateHz"/>: the LFO. <see cref="Depth"/>: how far it moves, 0 to 1. <see cref="Feedback"/>: −1 to 1, kept under
/// <see cref="ModMath.MaxFeedback"/>. <see cref="DelayMs"/>: the chorus's and flanger's base delay. <see cref="Spread"/>: how
/// far apart the two sides' LFOs run, 0 (together) to 1 (opposite). <see cref="Stages"/>: a phaser's all-pass stages.
/// <see cref="Shape"/>: a tremolo's wave, 0 = sine to 1 = square.
/// </summary>
public readonly record struct ModParams(double RateHz, double Depth, double Feedback, double DelayMs, double Spread, int Stages, double Shape)
{
    public const double RateMin = 0.05, RateMax = 12;
    public static readonly ModParams Defaults = new(0.8, 0.5, 0, 15, 0.5, 6, 0);
}

/// <summary>What the modulation effects share: the fractional delay read, the LFO and the limits that keep them stable.</summary>
static class ModMath
{
    public const double MaxFeedback = 0.95;
    /// <summary>A feedback path under this is written as silence: a decaying tail would otherwise end in denormals, which are slow.</summary>
    public const float Silence = 1e-15f;

    public static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    /// <summary>A delay line's size for <paramref name="maxMs"/>: a power of two, so the read position wraps with a mask.</summary>
    public static int Size(double maxMs, double fs)
    {
        int need = (int)Math.Ceiling(maxMs / 1000 * fs) + 8, n = 1;
        while (n < need) n <<= 1;
        return n;
    }

    /// <summary>Reads <paramref name="delay"/> samples back with a 4-point Hermite curve: a moving read stays bright, unlike a straight line between samples.</summary>
    public static float Read(float[] buf, int mask, int write, double delay)
    {
        double at = write - delay;
        int i = (int)Math.Floor(at);
        float f = (float)(at - i);
        float y0 = buf[(i - 1) & mask], y1 = buf[i & mask], y2 = buf[(i + 1) & mask], y3 = buf[(i + 2) & mask];
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - 2.5f * y1 + 2 * y2 - 0.5f * y3;
        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * f + c2) * f + c1) * f + y1;
    }

    public static double Wrap(double phase) => phase >= 1 ? phase - 1 : phase;
}

/// <summary>
/// Chorus: two delayed voices per side, each swept by its own LFO a quarter of a turn apart, so it thickens without the
/// single wobble of one voice; the sides' LFOs run <see cref="ModParams.Spread"/> apart, which opens it in stereo. It puts
/// out only the voices: the dry signal comes through the slot's Mix. Zero latency: Mix keeps the direct sound on time.
/// </summary>
public sealed class ChorusInsert : ITrackInsert
{
    const int Voices = 2;
    const double MaxDelayMs = 60, DepthMs = 10, VoiceSpacing = 1.33;
    /// <summary>The shortest read, in samples: what the Hermite curve needs behind the write.</summary>
    const double MinDelay = 2;
    /// <summary>Two voices that sum in phase would be 6 dB up: this keeps a steady tone near its own level.</summary>
    const float VoiceGain = 0.7071f;

    readonly double fs;
    readonly float[] bufL, bufR;
    readonly int mask;
    readonly double glide;
    volatile Config config;
    int write;
    double phase, delay;

    sealed record Config(double Step, double Delay, double Sweep, double Offset);

    public ChorusInsert(int sampleRate)
    {
        fs = sampleRate;
        int n = ModMath.Size(MaxDelayMs, fs);
        bufL = new float[n];
        bufR = new float[n];
        mask = n - 1;
        glide = 1 - Math.Exp(-1 / (0.05 * fs));
        config = Build(ModParams.Defaults);
        delay = config.Delay;
    }

    Config Build(ModParams p)
    {
        double rate = ModMath.Clamp(p.RateHz, ModParams.RateMin, ModParams.RateMax, ModParams.Defaults.RateHz);
        double depth = ModMath.Clamp(p.Depth, 0, 1, 0);
        double ms = ModMath.Clamp(p.DelayMs, 5, 30, ModParams.Defaults.DelayMs);
        return new Config(rate / fs, ms / 1000 * fs, depth * DepthMs / 1000 * fs, 0.5 * ModMath.Clamp(p.Spread, 0, 1, 0.5));
    }

    public string Kind => TrackInserts.Chorus;
    public int LatencySamples => 0;
    public void Set(ModParams p) => config = Build(p);

    public void Reset()
    {
        Array.Clear(bufL);
        Array.Clear(bufR);
        write = 0;
        phase = 0;
        delay = config.Delay;
    }

    public double TakeGrDb() => 0;

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double ph = phase, d = delay;
        int w = write;
        for (int i = 0; i < left.Length; i++)
        {
            bufL[w & mask] = left[i];
            bufR[w & mask] = right[i];
            d += (c.Delay - d) * glide;
            ph = ModMath.Wrap(ph + c.Step);
            float l = 0, r = 0;
            for (int v = 0; v < Voices; v++)
            {
                double p = ph + v * 0.25;
                double base0 = d * (v == 0 ? 1 : VoiceSpacing);
                // never ahead of the write: a deep sweep on a short delay would read the ring's oldest audio and click
                l += ModMath.Read(bufL, mask, w, Math.Max(MinDelay, base0 + c.Sweep * Math.Sin(2 * Math.PI * p)));
                r += ModMath.Read(bufR, mask, w, Math.Max(MinDelay, base0 + c.Sweep * Math.Sin(2 * Math.PI * (p + c.Offset))));
            }
            left[i] = l * VoiceGain;
            right[i] = r * VoiceGain;
            w++;
        }
        write = w & mask;
        phase = ph;
        delay = d;
    }
}

/// <summary>
/// Flanger: one short delay swept over a few milliseconds with feedback: a negative feedback hollows it and a positive one
/// rings. Like the chorus it puts out only the delayed side, and the slot's Mix makes the comb against the dry signal.
/// </summary>
public sealed class FlangerInsert : ITrackInsert
{
    const double MaxDelayMs = 30, SweepMs = 8;

    readonly double fs;
    readonly float[] bufL, bufR;
    readonly int mask;
    volatile Config config;
    int write;
    double phase;

    sealed record Config(double Step, double Delay, double Sweep, float Feedback, double Offset);

    public FlangerInsert(int sampleRate)
    {
        fs = sampleRate;
        int n = ModMath.Size(MaxDelayMs, fs);
        bufL = new float[n];
        bufR = new float[n];
        mask = n - 1;
        config = Build(ModParams.Defaults);
    }

    Config Build(ModParams p)
    {
        double rate = ModMath.Clamp(p.RateHz, ModParams.RateMin, ModParams.RateMax, ModParams.Defaults.RateHz);
        double ms = ModMath.Clamp(p.DelayMs, 0.2, 10, 1);
        return new Config(rate / fs, ms / 1000 * fs, ModMath.Clamp(p.Depth, 0, 1, 0) * SweepMs / 1000 * fs,
            (float)ModMath.Clamp(p.Feedback, -ModMath.MaxFeedback, ModMath.MaxFeedback, 0), 0.5 * ModMath.Clamp(p.Spread, 0, 1, 0.5));
    }

    public string Kind => TrackInserts.Flanger;
    public int LatencySamples => 0;
    public void Set(ModParams p) => config = Build(p);

    public void Reset()
    {
        Array.Clear(bufL);
        Array.Clear(bufR);
        write = 0;
        phase = 0;
    }

    public double TakeGrDb() => 0;

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double ph = phase;
        int w = write;
        float fb = c.Feedback;
        for (int i = 0; i < left.Length; i++)
        {
            ph = ModMath.Wrap(ph + c.Step);
            // from the base delay up, so the top of the sweep never reads the sample being written
            double dl = 2 + c.Delay + c.Sweep * (0.5 + 0.5 * Math.Sin(2 * Math.PI * ph));
            double dr = 2 + c.Delay + c.Sweep * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (ph + c.Offset)));
            float yl = ModMath.Read(bufL, mask, w, dl), yr = ModMath.Read(bufR, mask, w, dr);
            float xl = left[i] + yl * fb, xr = right[i] + yr * fb;
            bufL[w & mask] = Math.Abs(xl) < ModMath.Silence ? 0 : xl;
            bufR[w & mask] = Math.Abs(xr) < ModMath.Silence ? 0 : xr;
            left[i] = yl;
            right[i] = yr;
            w++;
        }
        write = w & mask;
        phase = ph;
    }
}

/// <summary>
/// Phaser: a chain of first-order all-pass filters swept from <see cref="MinHz"/> to <see cref="MaxHz"/> on a log curve, with
/// feedback around it. An all-pass only turns the phase, so the notches come from the slot's Mix against the dry signal
/// (50 % is the deepest). The coefficient costs a tangent, so it is worked out every <see cref="CoefEvery"/> samples and held.
/// </summary>
public sealed class PhaserInsert : ITrackInsert
{
    public const int MaxStages = 12;
    const double MinHz = 150, MaxHz = 5000;
    const int CoefEvery = 8;

    readonly double fs;
    readonly float[] stateL = new float[MaxStages], stateR = new float[MaxStages];
    volatile Config config;
    double phase;
    float lastL, lastR, coefL, coefR;
    int countdown;

    sealed record Config(double Step, double Depth, float Feedback, int Stages, double Offset);

    public PhaserInsert(int sampleRate)
    {
        fs = sampleRate;
        config = Build(ModParams.Defaults);
    }

    Config Build(ModParams p)
    {
        double rate = ModMath.Clamp(p.RateHz, ModParams.RateMin, ModParams.RateMax, ModParams.Defaults.RateHz);
        int stages = Math.Clamp(p.Stages, 2, MaxStages) & ~1;
        return new Config(rate / fs, ModMath.Clamp(p.Depth, 0, 1, 0), (float)ModMath.Clamp(p.Feedback, -ModMath.MaxFeedback, ModMath.MaxFeedback, 0),
            stages, 0.5 * ModMath.Clamp(p.Spread, 0, 1, 0.5));
    }

    public string Kind => TrackInserts.Phaser;
    public int LatencySamples => 0;
    public void Set(ModParams p) => config = Build(p);

    public void Reset()
    {
        Array.Clear(stateL);
        Array.Clear(stateR);
        phase = 0;
        lastL = lastR = 0;
        countdown = 0;
    }

    public double TakeGrDb() => 0;

    float Coef(double lfo, double depth)
    {
        // the sweep opens around the middle of its range as the depth grows
        double t = 0.5 + 0.5 * depth * lfo;
        double hz = MinHz * Math.Pow(MaxHz / MinHz, t);
        double k = Math.Tan(Math.PI * Math.Min(hz, fs * 0.45) / fs);
        return (float)((k - 1) / (k + 1));
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double ph = phase;
        float fb = c.Feedback, l1 = lastL, r1 = lastR, al = coefL, ar = coefR;
        int stages = c.Stages, wait = countdown;
        for (int i = 0; i < left.Length; i++)
        {
            ph = ModMath.Wrap(ph + c.Step);
            if (--wait < 0)
            {
                wait = CoefEvery - 1;
                al = Coef(Math.Sin(2 * Math.PI * ph), c.Depth);
                ar = Coef(Math.Sin(2 * Math.PI * (ph + c.Offset)), c.Depth);
            }
            float xl = left[i] + l1 * fb, xr = right[i] + r1 * fb;
            for (int s = 0; s < stages; s++)
            {
                float yl = al * xl + stateL[s], yr = ar * xr + stateR[s];
                stateL[s] = xl - al * yl;
                stateR[s] = xr - ar * yr;
                if (Math.Abs(stateL[s]) < ModMath.Silence) stateL[s] = 0;
                if (Math.Abs(stateR[s]) < ModMath.Silence) stateR[s] = 0;
                xl = yl;
                xr = yr;
            }
            l1 = xl;
            r1 = xr;
            left[i] = xl;
            right[i] = xr;
        }
        phase = ph;
        lastL = l1;
        lastR = r1;
        coefL = al;
        coefR = ar;
        countdown = wait;
    }
}

/// <summary>
/// Tremolo and auto-pan: an LFO on the level of each side. <see cref="ModParams.Spread"/> at 0 is a tremolo, at 1 the sides
/// run opposite and the sound moves across the stereo image. <see cref="ModParams.Shape"/> squares the wave off, from a
/// sine to a chop that still has soft edges, so it never clicks.
/// </summary>
public sealed class TremoloInsert : ITrackInsert
{
    /// <summary>How hard a full <see cref="ModParams.Shape"/> drives the wave into its soft square.</summary>
    const double SquareDrive = 8;

    readonly double fs;
    volatile Config config;
    double phase;

    sealed record Config(double Step, double Depth, double Offset, double Drive);

    public TremoloInsert(int sampleRate)
    {
        fs = sampleRate;
        config = Build(ModParams.Defaults);
    }

    Config Build(ModParams p)
    {
        double rate = ModMath.Clamp(p.RateHz, ModParams.RateMin, ModParams.RateMax, ModParams.Defaults.RateHz);
        double shape = ModMath.Clamp(p.Shape, 0, 1, 0);
        return new Config(rate / fs, ModMath.Clamp(p.Depth, 0, 1, 0), 0.5 * ModMath.Clamp(p.Spread, 0, 1, 0), 1 + shape * SquareDrive);
    }

    public string Kind => TrackInserts.Tremolo;
    public int LatencySamples => 0;
    public void Set(ModParams p) => config = Build(p);
    public void Reset() => phase = 0;
    public double TakeGrDb() => 0;

    static double Wave(double phase, double drive)
    {
        double s = Math.Sin(2 * Math.PI * phase);
        return drive <= 1 ? s : Math.Tanh(s * drive) / Math.Tanh(drive);
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = config;
        double ph = phase;
        for (int i = 0; i < left.Length; i++)
        {
            ph = ModMath.Wrap(ph + c.Step);
            left[i] = (float)(left[i] * (1 - c.Depth * (0.5 + 0.5 * Wave(ph, c.Drive))));
            right[i] = (float)(right[i] * (1 - c.Depth * (0.5 + 0.5 * Wave(ph + c.Offset, c.Drive))));
        }
        phase = ph;
    }
}
