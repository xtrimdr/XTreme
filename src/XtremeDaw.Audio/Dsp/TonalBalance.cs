namespace XtremeDaw.Audio.Dsp;

/// <summary>What the tonal balance listens to: both sides (the mean of their spectra), their sum (Mid) or their difference (Side).</summary>
public enum TonalMode { Stereo, Mid, Side }

/// <summary>
/// The frequency scale the tonal balance reads and its targets are written in (asked by the user, #210): <see cref="Points"/>
/// points a sixth of an octave apart from 20 Hz, each the energy of a third of an octave around it, so pink noise reads
/// flat (a constant-Q scale: what a mix "should" look like is a gentle tilt on it, not the steep fall of a per-Hz
/// spectrum). A curve is level-independent: it is shifted so its mean over <see cref="NormLoHz"/>–<see cref="NormHiHz"/>
/// is 0 dB, so only its shape is compared. The broad view's four bands split it at <see cref="BandEdges"/>.
/// </summary>
public static class TonalScale
{
    public const int Points = 61;
    public const double LowHz = 20;
    /// <summary>Points per octave, and how wide each point's band is (a third of an octave).</summary>
    public const double PerOctave = 6, BandOctaves = 1.0 / 3;
    /// <summary>The range a curve is levelled on: where every kind of music has energy, away from the extremes a genre moves most.</summary>
    public const double NormLoHz = 200, NormHiHz = 5000;
    /// <summary>
    /// The broad view's bands: Graves (kick and bass fundamentals), Medios graves (body, warmth, mud), Medios agudos
    /// (presence, attack, voice intelligibility) and Agudos (brilliance and air).
    /// </summary>
    public static readonly double[] BandEdges = [20, 120, 1000, 5000, 20500];
    public const int Bands = 4;

    public static double Freq(int i) => LowHz * Math.Pow(2, i / PerOctave);

    /// <summary>The point's place in the broad view (0..3).</summary>
    public static int BandOf(int i)
    {
        double f = Freq(i);
        for (int b = Bands - 1; b > 0; b--)
            if (f >= BandEdges[b]) return b;
        return 0;
    }

    static bool InNorm(int i) => Freq(i) >= NormLoHz && Freq(i) <= NormHiHz;

    /// <summary>Shifts a curve (dB) so its mean over the levelling range is 0.</summary>
    public static void Level(Span<double> curve)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < curve.Length; i++)
            if (InNorm(i)) { sum += curve[i]; n++; }
        double shift = n > 0 ? sum / n : 0;
        for (int i = 0; i < curve.Length; i++) curve[i] -= shift;
    }

    /// <summary>The broad view of a curve: the mean (dB) of its points in each band.</summary>
    public static void BandValues(ReadOnlySpan<double> curve, Span<double> bands)
    {
        bands.Clear();
        Span<int> count = stackalloc int[Bands];
        for (int i = 0; i < curve.Length && i < Points; i++)
        {
            int b = BandOf(i);
            bands[b] += curve[i];
            count[b]++;
        }
        for (int b = 0; b < Bands; b++) bands[b] = count[b] > 0 ? bands[b] / count[b] : 0;
    }
}

/// <summary>
/// An in-place radix-2 FFT with its tables made once (the analyzer runs one per frame on the window's thread, and must
/// not allocate there). <see cref="Transform"/> with <c>inverse</c> divides by the size.
/// </summary>
public sealed class TonalFft
{
    readonly int n;
    readonly double[] cos, sin;
    readonly int[] rev;

    public TonalFft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("the size must be a power of two", nameof(size));
        n = size;
        cos = new double[n / 2];
        sin = new double[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            cos[k] = Math.Cos(-2 * Math.PI * k / n);
            sin[k] = Math.Sin(-2 * Math.PI * k / n);
        }
        rev = new int[n];
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            rev[i] = r;
        }
    }

    public int Size => n;

    public void Transform(double[] re, double[] im, bool inverse = false)
    {
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            if (j <= i) continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }
        double sign = inverse ? -1 : 1;
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1, step = n / size;
            for (int start = 0; start < n; start += size)
                for (int k = 0; k < half; k++)
                {
                    int a = start + k, b = a + half;
                    double c = cos[k * step], s = sign * sin[k * step];
                    double tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
        if (!inverse) return;
        for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}

/// <summary>
/// Where the tonal balance slot leaves what passes through it: the audio thread copies both sides into a preallocated ring
/// (no allocation, no locks) and the reader (<see cref="TonalAnalyzer.Pull"/>) takes what came since its last read. A
/// reader that fell more than the ring behind skips ahead; a read that races the writer can mix two blocks of the oldest
/// part, which it never reads (<see cref="Margin"/>).
/// </summary>
public sealed class TonalTap
{
    /// <summary>About 1.4 s at 48 kHz: the window reads every 50 ms.</summary>
    public const int Capacity = 1 << 16;
    /// <summary>What the reader leaves unread at the old end, where the writer may be writing.</summary>
    public const int Margin = 8192;
    const int Mask = Capacity - 1;

    readonly float[] left = new float[Capacity], right = new float[Capacity];
    long written;

    public long Written => Interlocked.Read(ref written);

    /// <summary>Audio thread: appends a block.</summary>
    public void Write(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        long w = Interlocked.Read(ref written);
        int n = Math.Min(l.Length, r.Length);
        for (int i = 0; i < n; i++)
        {
            int at = (int)((w + i) & Mask);
            left[at] = l[i];
            right[at] = r[i];
        }
        Interlocked.Add(ref written, n);
    }

    internal float L(long at) => left[(int)(at & Mask)];
    internal float R(long at) => right[(int)(at & Mask)];
}

/// <summary>
/// The tonal balance's analysis (#210), the same for the live slot, a reference file and xtlab: frames of
/// <see cref="FftSize"/> samples every <see cref="Hop"/> (Hann window), their power averaged over time
/// (<see cref="TimeSec"/>, exponential, counting from the first frame so a fresh reading is a plain mean) for the three
/// modes at once (Stereo, Mid, Side: switching shows the other at once), read on <see cref="TonalScale"/>. Frames under
/// <see cref="SilenceDb"/> don't count, so a stop holds the reading. The low-end punch is the crest factor (peak over RMS)
/// of what is under <see cref="LowXHz"/> (a 4th-order Linkwitz-Riley low-pass), per <see cref="CrestWindowSec"/> window,
/// averaged the same way. Runs on the reader's thread; nothing allocates after the constructor.
/// </summary>
public sealed class TonalAnalyzer
{
    public const int FftSize = 16384, Hop = FftSize / 2;
    const int Mask = FftSize - 1, Half = FftSize / 2;
    public const double DefaultTimeSec = 3, MinTimeSec = 1, MaxTimeSec = 10;
    /// <summary>A frame (or a whole reading) quieter than this is silence: it doesn't count.</summary>
    public const double SilenceDb = -100;
    /// <summary>The low-end band the punch meter reads, and its window.</summary>
    public const double LowXHz = 120, CrestWindowSec = 0.5;
    /// <summary>A crest window whose low end is quieter than this has no punch to speak of: it doesn't count.</summary>
    public const double CrestGateDb = -70;
    const int Modes = 3;

    readonly int sr;
    readonly TonalFft fft = new(FftSize);
    readonly float[] bufL = new float[FftSize], bufR = new float[FftSize];
    readonly double[] window = new double[FftSize], re = new double[FftSize], im = new double[FftSize];
    readonly double[][] avg = [new double[Half + 1], new double[Half + 1], new double[Half + 1]];
    readonly double scale;
    // each point's band as bins and their share of it (a bin overlaps a band partly at its edges)
    readonly int[] binFrom = new int[TonalScale.Points], binTo = new int[TonalScale.Points];
    readonly double[][] binShare = new double[TonalScale.Points][];
    int pos, filled, sinceHop;
    long frames;

    // the low-end punch: two Butterworth low-passes in series per side (Linkwitz-Riley), as doubles
    readonly double b0, b1, b2, a1, a2;
    // each stage's state per side, at side * 4: x1, x2, y1, y2
    readonly double[] lp = new double[8], lp2 = new double[8];
    readonly int crestWindow;
    int crestAt;
    readonly double[] peak = new double[Modes], sumSq = new double[Modes], crest = new double[Modes];
    readonly long[] crestCount = new long[Modes];

    long read = -1;

    public TonalAnalyzer(int sampleRate)
    {
        sr = sampleRate;
        double wsum = 0;
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize);
            wsum += window[i] * window[i];
        }
        // one-sided power that adds up to the mean square of the frame
        scale = 2 / (FftSize * wsum);
        double binHz = sr / (double)FftSize;
        double halfBand = Math.Pow(2, TonalScale.BandOctaves / 2);
        for (int p = 0; p < TonalScale.Points; p++)
        {
            double f = TonalScale.Freq(p), lo = f / halfBand, hi = f * halfBand;
            int k0 = Math.Max(1, (int)Math.Floor(lo / binHz + 0.5)), k1 = Math.Min(Half, (int)Math.Ceiling(hi / binHz - 0.5));
            k1 = Math.Max(k0, k1);
            binFrom[p] = k0;
            binTo[p] = k1;
            var share = binShare[p] = new double[k1 - k0 + 1];
            for (int k = k0; k <= k1; k++)
            {
                double a = Math.Max(lo, (k - 0.5) * binHz), b = Math.Min(hi, (k + 0.5) * binHz);
                share[k - k0] = Math.Max(0, b - a) / binHz;
            }
        }
        double w0 = 2 * Math.PI * LowXHz / sr, alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5)), cw = Math.Cos(w0), a0 = 1 + alpha;
        b0 = (1 - cw) / 2 / a0; b1 = (1 - cw) / a0; b2 = b0; a1 = -2 * cw / a0; a2 = (1 - alpha) / a0;
        crestWindow = Math.Max(1, (int)(CrestWindowSec * sr));
    }

    /// <summary>How long the average looks back, in seconds (the slot's Respuesta).</summary>
    public double TimeSec { get; set; } = DefaultTimeSec;

    /// <summary>Grows with every frame and crest window taken, and with <see cref="Reset"/>: a view redraws when it moved.</summary>
    public long Version { get; private set; }

    public long Frames => frames;

    /// <summary>Starts the reading over (the panel's reset).</summary>
    public void Reset()
    {
        foreach (var a in avg) Array.Clear(a);
        frames = 0;
        Array.Clear(crest);
        Array.Clear(crestCount);
        Array.Clear(peak);
        Array.Clear(sumSq);
        crestAt = 0;
        Version++;
    }

    /// <summary>Takes what the tap got since the last pull (or the newest it still holds). True when something came.</summary>
    public bool Pull(TonalTap tap)
    {
        long w = tap.Written;
        if (w == read) return false;
        long from = Math.Max(read < 0 ? 0 : read, w - (TonalTap.Capacity - TonalTap.Margin));
        for (long at = from; at < w; at++) Sample(tap.L(at), tap.R(at));
        read = w;
        return true;
    }

    /// <summary>Feeds a block straight in (a file, xtlab).</summary>
    public void Feed(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        int n = Math.Min(l.Length, r.Length);
        for (int i = 0; i < n; i++) Sample(l[i], r[i]);
    }

    double Weight(long count, double stepSec) => Math.Max(1 - Math.Exp(-stepSec / Math.Max(0.05, TimeSec)), 1.0 / (count + 1));

    void Sample(float l, float r)
    {
        bufL[pos] = l;
        bufR[pos] = r;
        pos = (pos + 1) & Mask;
        if (filled < FftSize) filled++;
        if (++sinceHop >= Hop && filled == FftSize)
        {
            sinceHop = 0;
            Frame();
        }
        double yl = Low(l, 0), yr = Low(r, 1);
        Crest(0, Math.Max(Math.Abs(yl), Math.Abs(yr)), 0.5 * (yl * yl + yr * yr));
        double m = 0.5 * (yl + yr), s = 0.5 * (yl - yr);
        Crest(1, Math.Abs(m), m * m);
        Crest(2, Math.Abs(s), s * s);
        if (++crestAt < crestWindow) return;
        crestAt = 0;
        double gate = Math.Pow(10, CrestGateDb / 10);
        for (int k = 0; k < Modes; k++)
        {
            double ms = sumSq[k] / crestWindow;
            if (ms > gate && peak[k] > 0)
            {
                double db = 10 * Math.Log10(peak[k] * peak[k] / ms);
                crest[k] += Weight(crestCount[k], CrestWindowSec) * (db - crest[k]);
                crestCount[k]++;
            }
            peak[k] = 0;
            sumSq[k] = 0;
        }
        Version++;
    }

    void Crest(int k, double p, double sq)
    {
        if (p > peak[k]) peak[k] = p;
        sumSq[k] += sq;
    }

    double Low(double x, int side)
    {
        int o = side * 4;
        double y = b0 * x + b1 * lp[o] + b2 * lp[o + 1] - a1 * lp[o + 2] - a2 * lp[o + 3];
        lp[o + 1] = lp[o]; lp[o] = x; lp[o + 3] = lp[o + 2]; lp[o + 2] = y;
        double z = b0 * y + b1 * lp2[o] + b2 * lp2[o + 1] - a1 * lp2[o + 2] - a2 * lp2[o + 3];
        lp2[o + 1] = lp2[o]; lp2[o] = y; lp2[o + 3] = lp2[o + 2]; lp2[o + 2] = z;
        return z;
    }

    void Frame()
    {
        // one complex FFT carries both sides: z = l + i·r
        for (int i = 0; i < FftSize; i++)
        {
            int at = (pos + i) & Mask;
            re[i] = bufL[at] * window[i];
            im[i] = bufR[at] * window[i];
        }
        fft.Transform(re, im);
        double total = 0;
        for (int k = 1; k < Half; k++)
        {
            int nk = FftSize - k;
            double lr = 0.5 * (re[k] + re[nk]), li = 0.5 * (im[k] - im[nk]);
            double rr = 0.5 * (im[k] + im[nk]), ri = -0.5 * (re[k] - re[nk]);
            total += (lr * lr + li * li + rr * rr + ri * ri) * 0.5;
        }
        if (10 * Math.Log10(total * scale + 1e-300) < SilenceDb) return;
        double a = Weight(frames, Hop / (double)sr);
        for (int k = 1; k <= Half; k++)
        {
            int nk = (FftSize - k) & Mask;
            double lr = 0.5 * (re[k] + re[nk]), li = 0.5 * (im[k] - im[nk]);
            double rr = 0.5 * (im[k] + im[nk]), ri = -0.5 * (re[k] - re[nk]);
            double pl = lr * lr + li * li, pr = rr * rr + ri * ri;
            double mr = lr + rr, mi = li + ri, sr2 = lr - rr, si = li - ri;
            double st = 0.5 * (pl + pr) * scale, mid = 0.25 * (mr * mr + mi * mi) * scale, side = 0.25 * (sr2 * sr2 + si * si) * scale;
            avg[0][k] += a * (st - avg[0][k]);
            avg[1][k] += a * (mid - avg[1][k]);
            avg[2][k] += a * (side - avg[2][k]);
        }
        frames++;
        Version++;
    }

    /// <summary>The mode's level over the whole range, dBFS (mean square of its long-term average); very low when silent.</summary>
    public double LevelDb(TonalMode mode)
    {
        var a = avg[(int)mode];
        double sum = 0;
        for (int k = 1; k <= Half; k++) sum += a[k];
        return 10 * Math.Log10(sum + 1e-300);
    }

    /// <summary>
    /// The mode's long-term spectrum on <see cref="TonalScale"/> (<see cref="TonalScale.Points"/> values, dB), levelled
    /// (<see cref="TonalScale.Level"/>). False while there is nothing to read (no frame yet, or silence in that mode).
    /// </summary>
    public bool Read(TonalMode mode, Span<double> curve)
    {
        if (frames == 0 || LevelDb(mode) < SilenceDb) return false;
        var a = avg[(int)mode];
        for (int p = 0; p < TonalScale.Points && p < curve.Length; p++)
        {
            double e = 0;
            var share = binShare[p];
            for (int k = binFrom[p]; k <= binTo[p]; k++) e += a[k] * share[k - binFrom[p]];
            curve[p] = 10 * Math.Log10(e + 1e-30);
        }
        TonalScale.Level(curve[..Math.Min(curve.Length, TonalScale.Points)]);
        return true;
    }

    /// <summary>The low end's crest factor in the mode, dB, or NaN while it has none.</summary>
    public double CrestDb(TonalMode mode) => crestCount[(int)mode] > 0 ? crest[(int)mode] : double.NaN;
}

/// <summary>
/// The tonal balance as a slot (#210): an analyzer, not an effect. Audio passes untouched, bit for bit (it never writes
/// to the buffers, and it goes without a <see cref="FxShell"/>, so not even its power switch fades anything); on, it
/// leaves a copy in <see cref="Tap"/>. The analysis runs on the window's thread (<see cref="Meter"/>), only while a
/// panel shows it: the slot costs the audio thread a copy.
/// </summary>
public sealed class TonalInsert(int sampleRate) : ITrackInsert
{
    volatile bool on = true;
    TonalAnalyzer? meter;

    public TonalTap Tap { get; } = new();
    /// <summary>The analysis of what passed, shared by every view of the slot. Reader's thread only.</summary>
    public TonalAnalyzer Meter => meter ??= new(sampleRate);
    public string Kind => TrackInserts.Tonal;
    public int LatencySamples => 0;

    public void Set(bool isOn) => on = isOn;

    public void Process(Span<float> left, Span<float> right)
    {
        if (on) Tap.Write(left, right);
    }

    public void Reset() { }
    public double TakeGrDb() => 0;
}
