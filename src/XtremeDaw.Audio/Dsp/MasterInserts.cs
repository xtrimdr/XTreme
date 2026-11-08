namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// What every native effect shares, around the effect itself (<see cref="TrackInserts.Create"/>): its level
/// (<see cref="InsertSpec.Wet"/>) and the dry signal's (<see cref="InsertSpec.Dry"/>), each on its own knob (asked by the
/// user: all of one and all of the other is a valid setting), its output (<see cref="InsertSpec.OutputDb"/>) and turning it
/// on and off in a short crossfade (<see cref="FadeFrames"/>), so a power switch never clicks. Inside, the effect always runs on;
/// off and faded out it isn't run at all. At Wet 100 %, Dry 0 %, 0 dB and on, the effect's own output passes bit for bit. Runs on
/// the audio thread: the dry copy is preallocated (<see cref="TrackDspChain.MaxBlockSize"/>) and nothing allocates.
/// </summary>
public sealed class FxShell(ITrackInsert inner, int sampleRate) : ITrackInsert
{
    /// <summary>How long a change of the switch, Dry, Wet or the output takes, in frames at 48 kHz (10 ms).</summary>
    public const int FadeFrames = 480;
    const int Max = TrackDspChain.MaxBlockSize;

    readonly float[] dryL = new float[Max], dryR = new float[Max];
    readonly int fade = Math.Max(1, FadeFrames * sampleRate / 48000);
    volatile float wantOn = 1, wantWet = 1, wantDry, wantGain = 1;
    float on = 1, wet = 1, dry, gain = 1;
    bool set;

    volatile FxTap? tap;

    public ITrackInsert Inner => inner;
    public string Kind => inner.Kind;
    /// <summary>What goes in and out, for the effect's picture (#312); the window creates it, null otherwise.</summary>
    public FxTap? Tap { get => tap; set => tap = value; }
    public int LatencySamples => inner.LatencySamples;

    /// <summary>Takes the slot's settings. The first time (a new slot, the export) it starts there, with no fade.</summary>
    public void Set(InsertSpec spec)
    {
        TrackInserts.ApplyNative(inner, spec.Running);
        wantOn = spec.On ? 1 : 0;
        wantWet = (float)Math.Clamp(double.IsFinite(spec.Wet) ? spec.Wet : 1, 0, 1);
        wantDry = (float)Math.Clamp(double.IsFinite(spec.Dry) ? spec.Dry : 0, 0, 1);
        wantGain = (float)Math.Pow(10, (double.IsFinite(spec.OutputDb) ? spec.OutputDb : 0) / 20);
        if (!set) Snap();
        set = true;
    }

    /// <summary>Goes straight to its settings: for a chain the audio thread isn't running yet.</summary>
    public void Snap()
    {
        on = wantOn;
        wet = wantWet;
        dry = wantDry;
        gain = wantGain;
    }

    public void Reset()
    {
        inner.Reset();
        Snap();
    }

    public double TakeGrDb() => inner.TakeGrDb();

    static float Step(float from, float to, float step) => from < to ? Math.Min(to, from + step) : Math.Max(to, from - step);

    public void Process(Span<float> left, Span<float> right)
    {
        for (int at = 0; at < left.Length; at += Max)
        {
            int n = Math.Min(Max, left.Length - at);
            Block(left.Slice(at, n), right.Slice(at, n));
        }
    }

    void Block(Span<float> l, Span<float> r)
    {
        float toOn = wantOn, toWet = wantWet, toDry = wantDry, toGain = wantGain;
        float on0 = on, wet0 = wet, dry0 = dry, gain0 = gain;
        var feed = tap;
        bool watched = feed != null && feed.Watched;
        if (on0 == 0 && toOn == 0)
        {
            wet = toWet;
            dry = toDry;
            gain = toGain;
            if (watched) feed!.Add(l, r, l, r);
            return;
        }
        bool plain = on0 == 1 && toOn == 1 && wet0 == 1 && toWet == 1 && dry0 == 0 && toDry == 0 && gain0 == 1 && toGain == 1;
        if (plain && !watched)
        {
            inner.Process(l, r);
            return;
        }
        int n = l.Length;
        var dl = dryL.AsSpan(0, n);
        var dr = dryR.AsSpan(0, n);
        l.CopyTo(dl);
        r.CopyTo(dr);
        inner.Process(l, r);
        if (plain)
        {
            feed!.Add(dl, dr, l, r);
            return;
        }
        float step = n / (float)fade;
        float on1 = on = Step(on0, toOn, step);
        float wet1 = wet = Step(wet0, toWet, step);
        float dry1 = dry = Step(dry0, toDry, step);
        // the output moves as far as the knob asks in the same time: it only has to not jump
        float gain1 = gain = Step(gain0, toGain, step * Math.Max(1, Math.Abs(toGain - gain0)));
        for (int i = 0; i < n; i++)
        {
            float t = (i + 1) / (float)n;
            float p = on0 + (on1 - on0) * t, w = wet0 + (wet1 - wet0) * t, d = dry0 + (dry1 - dry0) * t, g = gain0 + (gain1 - gain0) * t;
            l[i] = dl[i] * (1 - p) + p * g * (dl[i] * d + l[i] * w);
            r[i] = dr[i] * (1 - p) + p * g * (dr[i] * d + r[i] * w);
        }
        if (watched) feed!.Add(dl, dr, l, r);
    }
}

/// <summary>The master's Limpieza as a slot: low cut and mud (<see cref="MasterEq.ProcessPre"/>).</summary>
public sealed class CleanInsert(int sampleRate) : ITrackInsert
{
    readonly MasterEq eq = new(sampleRate);
    public string Kind => TrackInserts.Clean;
    public int LatencySamples => 0;
    public void Set(MasterParams p) => eq.Set(p);
    public void Process(Span<float> left, Span<float> right) => eq.ProcessPre(left, right);
    public void Reset() => eq.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>The master's Color as a slot: lows, presence, air and tilt (<see cref="MasterEq.ProcessPost"/>).</summary>
public sealed class ColorInsert(int sampleRate) : ITrackInsert
{
    readonly MasterEq eq = new(sampleRate);
    public string Kind => TrackInserts.Color;
    public int LatencySamples => 0;
    public void Set(MasterParams p) => eq.Set(p);
    public void Process(Span<float> left, Span<float> right) => eq.ProcessPost(left, right);
    public void Reset() => eq.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>The master's Multibanda as a slot: the three bands and the Púa, each following the level of what goes through it.</summary>
public sealed class BandsInsert(int sampleRate) : ITrackInsert
{
    readonly MasterMultiband bands = new(sampleRate);
    readonly MasterPickTamer pick = new(sampleRate);
    public string Kind => TrackInserts.Bands;
    public int LatencySamples => 0;

    public void Set(MasterParams p)
    {
        bands.Set(p with { BandsOn = true });
        pick.Set(p.PickAmount);
    }

    public void Process(Span<float> left, Span<float> right)
    {
        bands.Process(left, right);
        pick.Process(left, right);
    }

    public void Reset()
    {
        bands.Reset();
        pick.Reset();
    }

    public double TakeGrDb()
    {
        double low = pick.TakeReductionDb();
        for (int i = 0; i < MasterBands.Count; i++) low = Math.Min(low, bands.TakeReductionDb(i));
        return low;
    }

    /// <summary>One band's deepest reduction since the last call (<see cref="MasterBands.Count"/> = the Púa), for its meter.</summary>
    public double TakeBandDb(int band) => band < MasterBands.Count ? bands.TakeReductionDb(band) : pick.TakeReductionDb();
}

/// <summary>The master's Cinta as a slot: the tracks' tape, gentler (<see cref="MasterChain.TapeParams"/>).</summary>
public sealed class TapeInsert(int sampleRate) : ITrackInsert
{
    readonly Saturator tape = new(sampleRate);
    public string Kind => TrackInserts.Tape;
    public int LatencySamples => 0;
    public void Set(MasterParams p) => tape.Set(MasterChain.TapeParams(p.TapeAmount));
    public void Process(Span<float> left, Span<float> right) => tape.Process(left, right);
    public void Reset() => tape.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>The master's Ancho as a slot (<see cref="StereoWidth"/>).</summary>
public sealed class WidthInsert(int sampleRate) : ITrackInsert
{
    readonly StereoWidth width = new(sampleRate);
    public string Kind => TrackInserts.Width;
    public int LatencySamples => 0;
    public void Set(MasterParams p) => width.Set(p.Width, p.MonoBassHz);
    public void Process(Span<float> left, Span<float> right) => width.Process(left, right);
    public void Reset() => width.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>The master's Soft clip as a slot: Recorte against its own Techo, with Auto on (<see cref="SoftClipper"/>).</summary>
public sealed class ClipInsert(int sampleRate) : ITrackInsert
{
    readonly SoftClipper clip = new(sampleRate);
    public string Kind => TrackInserts.Clip;
    public int LatencySamples => 0;
    public void Set(MasterParams p) => clip.Set(p.ClipDb, p.LimCeilingDb, true);
    public void Process(Span<float> left, Span<float> right) => clip.Process(left, right);
    public void Reset() => clip.Reset();
    public double TakeGrDb() => clip.TakeReductionDb();
}

/// <summary>
/// The master's Limitador as a slot: Empuje, then <see cref="MasterLimiter"/>. Its look-ahead is a real latency, which
/// the rest of the mix waits for (<see cref="LatencySamples"/>). Off the delay stays, like the master's, so turning it
/// off moves nothing in time, and the Empuje glides back to 0 dB. It has no <see cref="FxShell"/>, whose dry signal would
/// come out ahead of it: it keeps its own Dry, in a delay as long as the look-ahead, Wet and output.
/// </summary>
public sealed class LimitInsert(int sampleRate) : ITrackInsert
{
    const double DriveGlideSec = 0.05;
    readonly MasterLimiter limiter = new(sampleRate, MasterLimiter.LookaheadSamples(sampleRate));
    readonly float glide = (float)(1 - Math.Exp(-1 / (DriveGlideSec * sampleRate)));
    readonly float[] lineL = new float[MasterLimiter.LookaheadSamples(sampleRate)], lineR = new float[MasterLimiter.LookaheadSamples(sampleRate)];
    readonly float[] dryL = new float[TrackDspChain.MaxBlockSize], dryR = new float[TrackDspChain.MaxBlockSize];
    readonly float fadeStep = 1f / Math.Max(1, FxShell.FadeFrames * sampleRate / 48000);
    volatile float target = 1, wantWet = 1, wantDry, wantGain = 1;
    float drive = 1, wet = 1, dry, gain = 1;
    int at;
    bool set;
    volatile FxTap? tap;

    public string Kind => TrackInserts.Limit;
    /// <summary>What goes in (as late as the look-ahead, before the Empuje) and out, for its picture (#312); the window creates it.</summary>
    public FxTap? Tap { get => tap; set => tap = value; }
    public int LatencySamples => limiter.Latency;

    /// <summary>Its settings; off, what comes out is the input, late by its look-ahead.</summary>
    public void Set(MasterParams p, bool on, double wetLevel = 1, double dryLevel = 0, double outputDb = 0)
    {
        wantWet = on ? (float)Math.Clamp(double.IsFinite(wetLevel) ? wetLevel : 1, 0, 1) : 1;
        wantDry = on ? (float)Math.Clamp(double.IsFinite(dryLevel) ? dryLevel : 0, 0, 1) : 0;
        wantGain = on ? (float)Math.Pow(10, (double.IsFinite(outputDb) ? outputDb : 0) / 20) : 1;
        if (!set)
        {
            wet = wantWet;
            dry = wantDry;
            gain = wantGain;
        }
        limiter.Set(p.LimCeilingDb, p.LimReleaseMs);
        limiter.Bypass = !on;
        target = on ? (float)Math.Pow(10, Math.Clamp(double.IsFinite(p.LimDriveDb) ? p.LimDriveDb : 0, 0, MasterParams.DriveMaxDb) / 20) : 1;
        if (!set) drive = target;
        set = true;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        // the dry signal, as late as the limiter's output; the line runs always, so Dry can come in at any moment
        int n = Math.Min(left.Length, dryL.Length), pos = at;
        for (int i = 0; i < n; i++)
        {
            dryL[i] = lineL[pos];
            dryR[i] = lineR[pos];
            lineL[pos] = left[i];
            lineR[pos] = right[i];
            if (++pos == lineL.Length) pos = 0;
        }
        at = pos;

        float d = drive, t = target;
        if (d != 1 || t != 1)
        {
            for (int i = 0; i < left.Length; i++)
            {
                d += (t - d) * glide;
                left[i] *= d;
                right[i] *= d;
            }
            if (Math.Abs(t - d) < 1e-5f) d = t;
            drive = d;
        }
        limiter.Process(left, right);
        Mix(left, right, n);
        if (tap is { } feed && feed.Watched) feed.Add(dryL.AsSpan(0, n), dryR.AsSpan(0, n), left[..n], right[..n]);
    }

    /// <summary>Wet, Dry and the output over the limited block, gliding to where their knobs are.</summary>
    void Mix(Span<float> left, Span<float> right, int n)
    {
        float w0 = wet, d0 = dry, g0 = gain, w1 = wantWet, d1 = wantDry, g1 = wantGain;
        if (w0 == 1 && w1 == 1 && d0 == 0 && d1 == 0 && g0 == 1 && g1 == 1) return;
        float step = n * fadeStep;
        wet = w1 = w0 < w1 ? Math.Min(w1, w0 + step) : Math.Max(w1, w0 - step);
        dry = d1 = d0 < d1 ? Math.Min(d1, d0 + step) : Math.Max(d1, d0 - step);
        float gStep = step * Math.Max(1, Math.Abs(g1 - g0));
        gain = g1 = g0 < g1 ? Math.Min(g1, g0 + gStep) : Math.Max(g1, g0 - gStep);
        for (int i = 0; i < n; i++)
        {
            float k = (i + 1) / (float)n;
            float w = w0 + (w1 - w0) * k, a = d0 + (d1 - d0) * k, g = g0 + (g1 - g0) * k;
            left[i] = g * (left[i] * w + dryL[i] * a);
            right[i] = g * (right[i] * w + dryR[i] * a);
        }
    }

    public void Reset()
    {
        limiter.Reset();
        Array.Clear(lineL);
        Array.Clear(lineR);
        at = 0;
        drive = target;
        wet = wantWet;
        dry = wantDry;
        gain = wantGain;
    }

    public double TakeGrDb() => limiter.TakeReductionDb();
}
