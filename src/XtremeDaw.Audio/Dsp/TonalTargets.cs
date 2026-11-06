namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// What a mix is compared with in the tonal balance (#210): per point of <see cref="TonalScale"/>, the expected level
/// (<see cref="Mean"/>, levelled like a reading) and the range a good mix stays in (<see cref="Min"/>..<see cref="Max"/>),
/// and the range of its low-end punch (crest factor under <see cref="TonalAnalyzer.LowXHz"/>, dB).
/// </summary>
public sealed record TonalTarget(string Id, double[] Mean, double[] Min, double[] Max, double CrestMin, double CrestMax)
{
    /// <summary>The broad view of the target: each band's mean, low and high edge.</summary>
    public void Bands(Span<double> mean, Span<double> min, Span<double> max)
    {
        TonalScale.BandValues(Mean, mean);
        TonalScale.BandValues(Min, min);
        TonalScale.BandValues(Max, max);
    }
}

/// <summary>
/// The factory targets, one per genre (#210). Our own curves, not measured from any product: each is a few anchor points
/// on <see cref="TonalScale"/> (a constant-Q scale where pink noise is flat), joined on a log-frequency axis and levelled.
/// They follow what is generally known of long-term spectra of commercial music: a low-end hump around 50–100 Hz (bigger
/// and lower in EDM and hip-hop, smaller and higher in acoustic and orchestral music), a near-pink midrange, and a fall
/// that gets steeper above 4–8 kHz (gentler in electronic genres, steeper in acoustic recordings). The range around it
/// is a shared width profile (wider at the extremes, where mixes differ most) times the genre's own spread. The punch
/// ranges go from limited, steady lows (EDM, hip-hop) to the untouched transients of acoustic and orchestral music.
/// A user who wants another reference builds one from files (<see cref="TonalReference"/>).
/// </summary>
public static class TonalTargets
{
    /// <summary>The target ids, in the order the menu lists them; their names are the window's.</summary>
    public const string Pop = "pop", Rock = "rock", Edm = "edm", HipHop = "hiphop", Acoustic = "acoustic", Orchestral = "orchestral";

    /// <summary>The half-width of a target's range at each frequency, before the genre's spread (dB).</summary>
    static readonly (double Hz, double Db)[] Width =
        [(20, 6), (40, 4.5), (80, 3.5), (150, 3), (300, 2.5), (3000, 2.5), (6000, 3), (10000, 3.5), (16000, 4.5), (20480, 6)];

    public static readonly TonalTarget[] Factory =
    [
        Make(Pop, 1.0, 7, 10.5,
            [(20, -8), (40, 4), (60, 6), (100, 6), (200, 3), (500, 0.5), (1000, -1), (2000, -2), (4000, -4), (8000, -7), (12000, -10), (16000, -14), (20480, -22)]),
        Make(Rock, 1.0, 8, 11.5,
            [(20, -12), (40, 1), (60, 4), (100, 5), (200, 3), (500, 1), (1000, -0.5), (2000, -1.5), (4000, -3), (8000, -6.5), (12000, -10), (16000, -14), (20480, -22)]),
        Make(Edm, 0.85, 6, 9,
            [(20, -4), (40, 8), (60, 10), (100, 9), (200, 4), (500, 0), (1000, -1.5), (2000, -2.5), (4000, -3.5), (8000, -5.5), (12000, -8), (16000, -11), (20480, -18)]),
        Make(HipHop, 0.9, 6.5, 9.5,
            [(20, -2), (40, 9), (60, 11), (100, 9), (200, 4), (500, 0), (1000, -1.5), (2000, -2.5), (4000, -4.5), (8000, -7.5), (12000, -10.5), (16000, -14), (20480, -22)]),
        Make(Acoustic, 1.2, 9.5, 14,
            [(20, -16), (40, -4), (60, 1), (100, 3), (200, 3), (500, 1), (1000, -1), (2000, -2.5), (4000, -5), (8000, -9), (12000, -12.5), (16000, -16), (20480, -24)]),
        Make(Orchestral, 1.3, 10, 15,
            [(20, -16), (40, -5), (60, -1), (100, 2), (200, 3), (500, 2), (1000, 0), (2000, -2.5), (4000, -6), (8000, -11), (12000, -15), (16000, -19), (20480, -27)]),
    ];

    public static TonalTarget? Find(string id) => Array.Find(Factory, t => t.Id == id);

    /// <summary>A value on a log-frequency axis between anchor points (the ends hold).</summary>
    public static double Interp((double Hz, double Db)[] anchors, double f)
    {
        if (f <= anchors[0].Hz) return anchors[0].Db;
        for (int i = 1; i < anchors.Length; i++)
            if (f <= anchors[i].Hz)
            {
                double t = Math.Log(f / anchors[i - 1].Hz) / Math.Log(anchors[i].Hz / anchors[i - 1].Hz);
                return anchors[i - 1].Db + t * (anchors[i].Db - anchors[i - 1].Db);
            }
        return anchors[^1].Db;
    }

    static TonalTarget Make(string id, double spread, double crestMin, double crestMax, (double Hz, double Db)[] anchors)
    {
        int n = TonalScale.Points;
        var mean = new double[n];
        for (int i = 0; i < n; i++) mean[i] = Interp(anchors, TonalScale.Freq(i));
        TonalScale.Level(mean);
        var min = new double[n];
        var max = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = Interp(Width, TonalScale.Freq(i)) * spread;
            min[i] = mean[i] - w;
            max[i] = mean[i] + w;
        }
        return new(id, mean, min, max, crestMin, crestMax);
    }
}

/// <summary>
/// A target made from the user's own references (#210): each file's sound in <see cref="SegmentSec"/> pieces, each piece
/// analysed in Stereo and levelled like a live reading; quiet pieces (fades, breaks: <see cref="QuietDb"/> under the file's
/// loudest) are left out. The target's level is the mean of the pieces and its range their spread (one deviation over a
/// minimum width, clamped), so a steady reference gives a narrow target and a varied set a wide one; the punch range the
/// same way. Pure: decoding is the caller's (<see cref="AnalyzeFiles"/> does it with <see cref="MediaDecoder"/>).
/// </summary>
public static class TonalReference
{
    public const double SegmentSec = 4;
    /// <summary>A piece this far under the file's loudest one doesn't count.</summary>
    public const double QuietDb = 30;
    /// <summary>The narrowest and widest half-range a reference target gets, around one deviation of its pieces (dB).</summary>
    public const double MinHalfWidth = 1.5, MaxHalfWidth = 8, CrestMinHalfWidth = 1, CrestMaxHalfWidth = 4;
    /// <summary>The most of each file read (a long mix or a live set says nothing more after this).</summary>
    public const double MaxSec = 20 * 60;

    /// <summary>The pieces of one file: each piece's levelled curve, level and punch.</summary>
    public static List<(double[] Curve, double Level, double Crest)> Pieces(float[] left, float[] right, int sampleRate, Action<double>? progress = null)
    {
        var list = new List<(double[], double, double)>();
        var meter = new TonalAnalyzer(sampleRate) { TimeSec = 1e9 };
        int seg = (int)(SegmentSec * sampleRate), n = Math.Min(left.Length, right.Length);
        for (int at = 0; at + seg / 2 <= n; at += seg)
        {
            int len = Math.Min(seg, n - at);
            meter.Reset();
            meter.Feed(left.AsSpan(at, len), right.AsSpan(at, len));
            var curve = new double[TonalScale.Points];
            if (meter.Read(TonalMode.Stereo, curve)) list.Add((curve, meter.LevelDb(TonalMode.Stereo), meter.CrestDb(TonalMode.Stereo)));
            progress?.Invoke(Math.Min(1, (at + len) / (double)n));
        }
        if (list.Count == 0) return list;
        double loudest = list.Max(p => p.Item2);
        list.RemoveAll(p => p.Item2 < loudest - QuietDb);
        return list;
    }

    /// <summary>The target from every piece of every file, or null when none had sound.</summary>
    public static TonalTarget? From(IReadOnlyList<(double[] Curve, double Level, double Crest)> pieces)
    {
        if (pieces.Count == 0) return null;
        int n = TonalScale.Points;
        var mean = new double[n];
        var min = new double[n];
        var max = new double[n];
        for (int i = 0; i < n; i++)
        {
            double m = 0;
            foreach (var p in pieces) m += p.Curve[i];
            m /= pieces.Count;
            double v = 0;
            foreach (var p in pieces) v += (p.Curve[i] - m) * (p.Curve[i] - m);
            double hw = Math.Clamp(MinHalfWidth + Math.Sqrt(v / pieces.Count), MinHalfWidth, MaxHalfWidth);
            mean[i] = m;
            min[i] = m - hw;
            max[i] = m + hw;
        }
        // the mean of levelled curves is levelled already
        var crests = pieces.Select(p => p.Crest).Where(double.IsFinite).ToArray();
        double cm = crests.Length > 0 ? crests.Average() : 10;
        double cs = crests.Length > 0 ? Math.Sqrt(crests.Sum(c => (c - cm) * (c - cm)) / crests.Length) : 0;
        double chw = Math.Clamp(0.5 + cs, CrestMinHalfWidth, CrestMaxHalfWidth);
        return new("custom", mean, min, max, cm - chw, cm + chw);
    }

    /// <summary>
    /// Decodes and analyses the files one after another (off the window's thread: it takes a while), at most
    /// <see cref="MaxSec"/> of each. Throws what <see cref="MediaDecoder.Load"/> throws for a file it can't read.
    /// </summary>
    public static TonalTarget? AnalyzeFiles(IReadOnlyList<string> paths, int sampleRate, Action<double>? progress = null, CancellationToken ct = default)
    {
        var all = new List<(double[], double, double)>();
        for (int f = 0; f < paths.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            int at = f;
            var audio = MediaDecoder.Load(paths[f], sampleRate, x => progress?.Invoke((at + x * 0.5) / paths.Count), MaxSec, ct);
            all.AddRange(Pieces(audio.Left, audio.Right, sampleRate, x => progress?.Invoke((at + 0.5 + x * 0.5) / paths.Count)));
        }
        return From(all);
    }
}
