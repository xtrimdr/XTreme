namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// The pump's settings as the engine takes them. <see cref="Division"/>: an index into <see cref="Beats"/>, how often it
/// ducks. <see cref="Depth"/>: how far down, 0 to 1 (1 = silence). <see cref="Length"/>: how much of each period it takes
/// to come back up, 0.05 to 1. <see cref="Curve"/>: −1 (it comes back fast) to 1 (it stays down and comes back late).
/// <see cref="AttackMs"/>: the fall into the dip, so the duck never clicks. <see cref="Offset"/>: where in the period it
/// ducks, −0.5 to 0.5 of it (a groove ahead of or behind the beat). With a sidechain the hits set when it ducks, so
/// <see cref="Division"/>, <see cref="Length"/> and <see cref="Offset"/> don't count and it reads like a compressor:
/// <see cref="ThresholdDb"/> is the key's level that fires a duck (at or under <see cref="ThresholdAutoDb"/>: half its
/// recent peak), <see cref="HoldMs"/> how long it stays down and <see cref="KeyMs"/> how long it takes to come back.
/// </summary>
public readonly record struct PumpParams(int Division, double Depth, double Length, double Curve, double AttackMs, double Offset)
{
    public double KeyMs { get; init; } = 250;
    public const double KeyMinMs = 30, KeyMaxMs = 2000;
    public double ThresholdDb { get; init; } = ThresholdAutoDb;
    public double HoldMs { get; init; }
    public const double ThresholdAutoDb = -60, HoldMaxMs = 500;
    /// <summary>The periods <see cref="Division"/> picks, in beats: a bar, a half, a quarter, an eighth and a sixteenth.</summary>
    public static readonly double[] Beats = [4, 2, 1, 0.5, 0.25];
    public static readonly string[] Names = ["1/1", "1/2", "1/4", "1/8", "1/16"];
    public const double AttackMaxMs = 30, LengthMin = 0.05;
    public static readonly PumpParams Defaults = new(2, 0.8, 0.6, 0.4, 2, 0);
}

/// <summary>
/// The pump: a volume curve locked to the song's beat, the sidechain sound without a sidechain (like KickStart). Each
/// period (<see cref="PumpParams.Beats"/>) the level falls in <see cref="PumpParams.AttackMs"/> to the bottom and comes back
/// over <see cref="PumpParams.Length"/> of it, bent by <see cref="PumpParams.Curve"/>. Its phase comes from the song frame
/// the chain hands it (<see cref="Frame"/>, beat 0 = the timeline's 0, like the click), so it lands on the beat wherever
/// playback starts or jumps; a frame that doesn't move (stopped, an instrument played by hand) lets it run on by itself.
/// Each sample's phase comes from its own frame, so any block size gives the same floats.
/// With a key (<see cref="Key"/>, a sidechain: the kick's track) it ducks on each of the key's hits instead of on the
/// beat, with its curve over <see cref="PumpParams.HoldMs"/> and <see cref="PumpParams.KeyMs"/>: a hit is the key rising
/// to half its recent peak (or to <see cref="PumpParams.ThresholdDb"/>), and it fires again only after falling under a
/// quarter of it (6 dB under the threshold), so one kick is one duck.
/// Each sample's level before and after goes into <see cref="WaveIn"/> and <see cref="WaveOut"/> by where in the curve it
/// fell, so the window draws the sound under the curve (like KickStart) without a lock or a copy.
/// </summary>
public sealed class PumpInsert(int sampleRate) : ITrackInsert, IKeyed
{
    /// <summary>How fast the key's follower falls (s) and how long its recent peak lasts (s); under <see cref="Floor"/> is silence.</summary>
    const double FollowSec = 0.02, PeakSec = 1.0;
    const float Floor = 0.003f;

    readonly double fs = sampleRate;
    readonly float follow = (float)Math.Exp(-1 / (FollowSec * sampleRate)), peakFall = (float)Math.Exp(-1 / (PeakSec * sampleRate));
    volatile Config config = Build(PumpParams.Defaults, 120, sampleRate, false), keyConfig = Build(PumpParams.Defaults, 120, sampleRate, true);
    long lastFrame = long.MinValue, at;
    float grMin = 1;
    // the key's follower, its recent peak, whether it can fire again and the frames since its last hit
    float env, peak;
    bool armed = true;
    long since = long.MaxValue / 2;

    public KeyTap Key { get; } = new();

    /// <summary>How many slices of the curve the sound is kept in for the window.</summary>
    public const int WaveBins = 128;
    /// <summary>The loudest sample in and out of each slice of the curve, from the last time it went by (audio thread writes, UI reads).</summary>
    public readonly float[] WaveIn = new float[WaveBins], WaveOut = new float[WaveBins];
    int lastBin = -1;

    /// <summary>
    /// What the window draws, as the last block left it (audio thread writes, UI reads): where in the curve it is (0 to 1),
    /// its gain, the key's level against its recent peak (−1 = no key: on the beat) and a count that moves with every
    /// block, so the window knows it is running.
    /// </summary>
    public volatile float ShownPhase, ShownGain = 1, ShownKey = -1;
    public volatile int Blocks;

    /// <summary>The levels of the key, against its recent peak, where a hit fires and where it can fire again.</summary>
    public const float HitAt = 0.5f, RearmAt = 0.25f;
    /// <summary>With a threshold, how far under it (as a gain) the key has to fall to fire again: 6 dB.</summary>
    const float RearmUnder = 0.5f;
    /// <summary>The range the key's meter shows with a threshold, in dB under full scale.</summary>
    public const double KeyRangeDb = 60;

    /// <summary>Where a level (dB) sits on the key's meter with a threshold, 0 to 1.</summary>
    public static double KeyMeter(double db) => Math.Clamp(1 + db / KeyRangeDb, 0, 1);

    /// <summary>The gain at <paramref name="phase"/> of the curve (0 to 1) for these settings, for the window's drawing.</summary>
    public static double GainAt(PumpParams p, double bpm, double phase, int sampleRate, bool keyed = false) => Gain(Build(p, bpm, sampleRate, keyed), Math.Clamp(phase, 0, 1));

    sealed record Config(double Period, double Floor, double Attack, double Hold, double Rise, double Exp, double Offset, float Threshold);

    /// <summary>The song frame of the block's first sample, set by the chain before each <see cref="Process"/>.</summary>
    public long Frame { get; set; }

    public string Kind => TrackInserts.Pump;
    public int LatencySamples => 0;

    public void Set(PumpParams p, double bpm)
    {
        config = Build(p, bpm, fs, false);
        keyConfig = Build(p, bpm, fs, true);
    }

    /// <summary>
    /// The curve for the beat, or with <paramref name="keyed"/> for a sidechain: one duck that holds <see cref="PumpParams.HoldMs"/>
    /// and takes <see cref="PumpParams.KeyMs"/> to come back, all of it.
    /// </summary>
    static Config Build(PumpParams p, double bpm, double fs, bool keyed)
    {
        int div = Math.Clamp(p.Division, 0, PumpParams.Beats.Length - 1);
        double tempo = double.IsFinite(bpm) && bpm > 0 ? bpm : 120;
        double holdMs = keyed ? ModMath.Clamp(p.HoldMs, 0, PumpParams.HoldMaxMs, 0) : 0;
        double period = keyed ? (ModMath.Clamp(p.KeyMs, PumpParams.KeyMinMs, PumpParams.KeyMaxMs, 250) + holdMs) / 1000 * fs : PumpParams.Beats[div] * 60 / tempo * fs;
        if (keyed) p = p with { Length = 1, Offset = 0 };
        double attack = ModMath.Clamp(p.AttackMs, 0, PumpParams.AttackMaxMs, PumpParams.Defaults.AttackMs) / 1000 * fs / period;
        // the fall can't take more than a quarter of the period, so a fast division still ducks
        attack = Math.Min(attack, 0.25);
        double hold = Math.Min(holdMs / 1000 * fs / period, 1 - attack - 1e-3);
        double length = ModMath.Clamp(p.Length, PumpParams.LengthMin, 1, PumpParams.Defaults.Length);
        double rise = Math.Max(1e-6, Math.Min(length, 1 - attack - hold));
        double curve = ModMath.Clamp(p.Curve, -1, 1, PumpParams.Defaults.Curve);
        double thr = ModMath.Clamp(p.ThresholdDb, PumpParams.ThresholdAutoDb, 0, PumpParams.ThresholdAutoDb);
        float threshold = keyed && thr > PumpParams.ThresholdAutoDb ? (float)Math.Pow(10, thr / 20) : 0;
        return new Config(period, 1 - ModMath.Clamp(p.Depth, 0, 1, 0), attack, hold, rise, Math.Pow(4, curve), ModMath.Clamp(p.Offset, -0.5, 0.5, 0), threshold);
    }

    /// <summary>The gain at <paramref name="phase"/> (0 to 1, the period's start = the beat).</summary>
    static double Gain(Config c, double phase)
    {
        if (phase < c.Attack)
        {
            // a half cosine down: no corner at the top, none at the bottom
            double t = phase / c.Attack;
            return c.Floor + (1 - c.Floor) * (0.5 + 0.5 * Math.Cos(Math.PI * t));
        }
        if (phase < c.Attack + c.Hold) return c.Floor;
        double u = (phase - c.Attack - c.Hold) / c.Rise;
        if (u >= 1) return 1;
        return c.Floor + (1 - c.Floor) * Math.Pow(u, c.Exp);
    }

    public void Reset()
    {
        lastFrame = long.MinValue;
        grMin = 1;
        env = peak = 0;
        armed = true;
        since = long.MaxValue / 2;
        lastBin = -1;
    }

    public double TakeGrDb()
    {
        float g = grMin;
        grMin = 1;
        return g >= 1 ? 0 : 20 * Math.Log10(Math.Max(g, 1e-5f));
    }

    public void Process(Span<float> left, Span<float> right)
    {
        if (Key.Take(left.Length, out var kl, out var kr)) Keyed(left, right, kl, kr);
        else Beat(left, right);
    }

    /// <summary>On the key's hits: each one starts the curve over.</summary>
    void Keyed(Span<float> left, Span<float> right, ReadOnlySpan<float> kl, ReadOnlySpan<float> kr)
    {
        var c = keyConfig;
        double inv = 1 / c.Period;
        float e = env, pk = peak, low = grMin, thr = c.Threshold;
        bool arm = armed;
        long s = since;
        for (int i = 0; i < left.Length; i++)
        {
            float x = Math.Max(Math.Abs(kl[i]), Math.Abs(kr[i]));
            e = x > e ? x : e * follow;
            pk = x > pk ? x : pk * peakFall;
            bool fire = thr > 0 ? x >= thr : x > Floor && x >= HitAt * pk;
            if (arm && fire)
            {
                arm = false;
                s = 0;
            }
            else if (!arm && (thr > 0 ? e < RearmUnder * thr : e < RearmAt * pk)) arm = true;
            double phase = s * inv;
            float g = phase >= 1 ? 1 : (float)Gain(c, phase);
            if (phase < 1) Wave(phase, left[i], right[i], g);
            left[i] *= g;
            right[i] *= g;
            if (g < low) low = g;
            s++;
        }
        env = e;
        peak = pk;
        armed = arm;
        since = s;
        grMin = low;
        lastFrame = long.MinValue;
        double shown = Math.Min(1, (s - 1) * inv);
        ShownPhase = (float)shown;
        ShownGain = shown >= 1 ? 1 : (float)Gain(c, shown);
        ShownKey = thr > 0 ? (e > 0 ? (float)KeyMeter(20 * Math.Log10(e)) : 0) : pk > Floor ? Math.Min(1, e / pk) : 0;
        Blocks++;
    }

    /// <summary>A sample into its slice of the curve: a slice it just entered starts over, so it shows the last time by.</summary>
    void Wave(double phase, float l, float r, float g)
    {
        int bin = Math.Min(WaveBins - 1, (int)(phase * WaveBins));
        float x = Math.Max(Math.Abs(l), Math.Abs(r));
        if (bin != lastBin)
        {
            lastBin = bin;
            WaveIn[bin] = x;
            WaveOut[bin] = x * g;
            return;
        }
        if (x > WaveIn[bin]) WaveIn[bin] = x;
        if (x * g > WaveOut[bin]) WaveOut[bin] = x * g;
    }

    void Beat(Span<float> left, Span<float> right)
    {
        var c = config;
        long frame = Frame;
        // the song moved: follow it; it stands still: run on from where the last block ended
        if (frame != lastFrame) at = frame;
        lastFrame = frame;
        double inv = 1 / c.Period;
        float low = grMin;
        for (int i = 0; i < left.Length; i++)
        {
            // from the frame itself, not added up sample by sample: no drift, and no block size rounds differently
            double phase = (at + i) * inv - c.Offset;
            phase -= Math.Floor(phase);
            float g = (float)Gain(c, phase);
            Wave(phase, left[i], right[i], g);
            left[i] *= g;
            right[i] *= g;
            if (g < low) low = g;
        }
        grMin = low;
        double end = (at + left.Length - 1) * inv - c.Offset;
        end -= Math.Floor(end);
        ShownPhase = (float)end;
        ShownGain = (float)Gain(c, end);
        ShownKey = -1;
        Blocks++;
        at += left.Length;
    }
}
