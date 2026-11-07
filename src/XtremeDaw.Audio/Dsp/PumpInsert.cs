namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// The pump's settings as the engine takes them. <see cref="Division"/>: an index into <see cref="Beats"/>, how often it
/// ducks. <see cref="Depth"/>: how far down, 0 to 1 (1 = silence). <see cref="Length"/>: how much of each period it takes
/// to come back up, 0.05 to 1. <see cref="Curve"/>: −1 (it comes back fast) to 1 (it stays down and comes back late).
/// <see cref="AttackMs"/>: the fall into the dip, so the duck never clicks. <see cref="Offset"/>: where in the period it
/// ducks, −0.5 to 0.5 of it (a groove ahead of or behind the beat). With a sidechain the hits set when it ducks, so
/// <see cref="Division"/>, <see cref="Length"/> and <see cref="Offset"/> don't count and it reads like a compressor:
/// <see cref="ThresholdDb"/> is the key's level where it starts to duck (at or under <see cref="ThresholdAutoDb"/>: 24 dB
/// under its recent peak), <see cref="HoldMs"/> how long it stays down after the key's last peak and <see cref="KeyMs"/>
/// how long it takes to come back.
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
/// With a key (<see cref="Key"/>, a sidechain: the kick's track) the key's own level shapes it instead of the beat (asked
/// by the user: it has to follow what comes in): its envelope (instant up, held <see cref="PumpParams.HoldMs"/>, then
/// down <see cref="SpanDb"/> in <see cref="PumpParams.KeyMs"/>, straight in dB) over the threshold gives how much it ducks,
/// 0 at the threshold and all of its depth <see cref="SpanDb"/> over it, reached in <see cref="PumpParams.AttackMs"/> and
/// bent by the same curve as on the beat. So a full hit draws the beat's shape, a soft one ducks less, and a long note
/// holds it down for as long as it lasts.
/// Each sample's level before and after goes into <see cref="WaveIn"/> and <see cref="WaveOut"/> by where in the curve it
/// fell, so the window draws the sound under the curve (like KickStart) without a lock or a copy.
/// </summary>
public sealed class PumpInsert(int sampleRate) : ITrackInsert, IKeyed
{
    /// <summary>How long the key's recent peak lasts (s), for the automatic threshold; a peak under <see cref="Floor"/> is silence.</summary>
    const double PeakSec = 2.0;
    const float Floor = 0.003f;
    /// <summary>From the threshold to all of the depth, in dB of the key; also how far the automatic threshold sits under its peak.</summary>
    public const double SpanDb = 24;

    readonly double fs = sampleRate;
    readonly float peakFall = (float)Math.Exp(-1 / (PeakSec * sampleRate));
    volatile Config config = Build(PumpParams.Defaults, 120, sampleRate, false), keyConfig = Build(PumpParams.Defaults, 120, sampleRate, true);
    long lastFrame = long.MinValue, at;
    float grMin = 1;
    // the key's envelope, its recent peak, the frames it still holds and how much it ducks now (0 to 1)
    float env, peak, amount;
    int holding;

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

    /// <summary>The range the key's meter shows, in dB under full scale (or under its recent peak, with the automatic threshold).</summary>
    public const double KeyRangeDb = 60;

    /// <summary>Where a level (dB) sits on the key's meter, 0 to 1.</summary>
    public static double KeyMeter(double db) => Math.Clamp(1 + db / KeyRangeDb, 0, 1);

    /// <summary>The gain at <paramref name="phase"/> of the curve (0 to 1) for these settings, for the window's drawing.</summary>
    public static double GainAt(PumpParams p, double bpm, double phase, int sampleRate, bool keyed = false) => Gain(Build(p, bpm, sampleRate, keyed), Math.Clamp(phase, 0, 1));

    /// <summary>
    /// A curve, as phases of its period (what draws it and the beat's), and for a key what the envelope reads per sample:
    /// <see cref="Threshold"/> (0 = automatic), how much the amount climbs (<see cref="AttackStep"/>), the frames it holds
    /// and how much the envelope falls (<see cref="Fall"/>).
    /// </summary>
    sealed record Config(double Period, double Floor, double Attack, double Hold, double Rise, double Exp, double Offset, float Threshold)
    {
        public float AttackStep { get; init; } = 1;
        public int HoldFrames { get; init; }
        public float Fall { get; init; } = 1;
        /// <summary>A key's curve: its attack is the amount climbing straight, bent like the way back.</summary>
        public bool Keyed { get; init; }
    }

    /// <summary>
    /// The song frame of the sound the block's first sample carries, set by the chain before each <see cref="Process"/>:
    /// the block's frame less what the slots before it delay (<see cref="InsertRack.Process"/>), so it ducks on the beat
    /// it hears.
    /// </summary>
    public long Frame { get; set; }

    /// <summary>
    /// A loop that turns inside the block: from sample <see cref="TurnAt"/> on (−1 = it doesn't) the sound is
    /// <see cref="TurnBack"/> frames earlier in the song, the loop's start. Set by the chain with <see cref="Frame"/>.
    /// </summary>
    public int TurnAt { get; set; } = -1;
    public long TurnBack { get; set; }

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
        double attackFrames = attack * period, riseFrames = rise * period;
        return new Config(period, 1 - ModMath.Clamp(p.Depth, 0, 1, 0), attack, hold, rise, Math.Pow(4, curve), ModMath.Clamp(p.Offset, -0.5, 0.5, 0), threshold)
        {
            AttackStep = attackFrames < 1 ? 1 : (float)(1 / attackFrames),
            HoldFrames = (int)Math.Round(hold * period),
            Fall = (float)Math.Pow(10, -SpanDb / 20 / Math.Max(1, riseFrames)),
            Keyed = keyed,
        };
    }

    /// <summary>The gain for how much it ducks (0 to 1), bent so a fall of the amount in a straight line draws the curve's rise.</summary>
    static float Duck(Config c, float amount) => amount <= 0 ? 1 : (float)(c.Floor + (1 - c.Floor) * Math.Pow(1 - Math.Min(1, amount), c.Exp));

    /// <summary>The gain at <paramref name="phase"/> (0 to 1, the period's start = the beat).</summary>
    static double Gain(Config c, double phase)
    {
        if (c.Keyed && phase < c.Attack) return Duck(c, (float)(phase / c.Attack));
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
        env = peak = amount = 0;
        holding = 0;
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

    /// <summary>
    /// Shaped by the key: its envelope over the threshold says how much it ducks. The window's line goes where that gain
    /// sits on the curve: down in the attack, in the hold while it holds, on the way back as the amount falls.
    /// </summary>
    void Keyed(Span<float> left, Span<float> right, ReadOnlySpan<float> kl, ReadOnlySpan<float> kr)
    {
        var c = keyConfig;
        float e = env, pk = peak, a = amount, low = grMin, fall = c.Fall, step = c.AttackStep;
        // the amount's scale: dB of the key over the threshold, over the span (as natural logs, no Log10 per sample)
        const float LnToDb = 8.685889f;
        float perDb = (float)(1 / SpanDb), autoUnder = (float)Math.Pow(10, -SpanDb / 20);
        int hold = holding, holdFrames = c.HoldFrames;
        for (int i = 0; i < left.Length; i++)
        {
            float x = Math.Max(Math.Abs(kl[i]), Math.Abs(kr[i]));
            pk = x > pk ? x : pk * peakFall;
            if (x >= e)
            {
                e = x;
                hold = holdFrames;
            }
            else if (hold > 0) hold--;
            else e *= fall;
            float thr = c.Threshold > 0 ? c.Threshold : pk * autoUnder;
            float target = pk < Floor || e <= thr ? 0 : Math.Min(1, MathF.Log(e / thr) * LnToDb * perDb);
            a = target > a ? Math.Min(target, a + step) : target;
            float g = Duck(c, a);
            Wave(Where(c, a, hold, holdFrames), left[i], right[i], g);
            left[i] *= g;
            right[i] *= g;
            if (g < low) low = g;
        }
        env = e;
        peak = pk;
        amount = a;
        holding = hold;
        grMin = low;
        lastFrame = long.MinValue;
        ShownPhase = (float)Where(c, a, hold, holdFrames);
        ShownGain = Duck(c, a);
        double ref0 = c.Threshold > 0 ? 1 : pk;
        ShownKey = e > 0 && ref0 > Floor ? (float)KeyMeter(20 * Math.Log10(e / ref0)) : 0;
        Blocks++;
    }

    /// <summary>Where on the drawn curve a key's amount sits: at the bottom while it holds, else on the way back.</summary>
    static double Where(Config c, float a, int hold, int holdFrames)
    {
        if (a <= 0) return 1;
        if (a >= 1 && hold > 0 && holdFrames > 0) return c.Attack + c.Hold * (1 - hold / (double)holdFrames);
        return Math.Min(1, c.Attack + c.Hold + (1 - a) * c.Rise);
    }

    /// <summary>A sample into its slice of the curve: a slice it just entered starts over, so it shows the last time by.</summary>
    void Wave(double phase, float l, float r, float g)
    {
        if (phase >= 1) return;
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
        int turn = TurnAt < 0 ? left.Length : TurnAt;
        long back = TurnBack;
        for (int i = 0; i < left.Length; i++)
        {
            // from the frame itself, not added up sample by sample: no drift, and no block size rounds differently
            double phase = (at + i - (i >= turn ? back : 0)) * inv - c.Offset;
            phase -= Math.Floor(phase);
            float g = (float)Gain(c, phase);
            Wave(phase, left[i], right[i], g);
            left[i] *= g;
            right[i] *= g;
            if (g < low) low = g;
        }
        grMin = low;
        double end = (at + left.Length - 1 - (left.Length - 1 >= turn ? back : 0)) * inv - c.Offset;
        end -= Math.Floor(end);
        ShownPhase = (float)end;
        ShownGain = (float)Gain(c, end);
        ShownKey = -1;
        Blocks++;
        at += left.Length;
    }
}
