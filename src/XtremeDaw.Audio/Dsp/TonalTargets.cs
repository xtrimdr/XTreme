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
/// The dubstep family (#265, asked to be exact) has three targets, because its styles differ where it matters most:
/// modern dubstep (brostep) is a loud, steady wall with a sub peak at 45–60 Hz, a dense growl region up to 300 Hz, a
/// raised 1–5 kHz (screeches, snare crack) and a gentle top; riddim is even more limited, with the mids filled by its
/// repeated mid-bass; deep (UK, 140) dubstep is mostly sub, with sparse dark drums and more room for transients. All three
/// cut under 30 Hz, where a dubstep master puts its high-pass.
/// </summary>
public static class TonalTargets
{
    /// <summary>The target ids; their names are the window's. A project keeps the target by id's place in the effect's
    /// stored order (<c>FxDefs.TonalStored</c>), not here: this list only says the menu's order.</summary>
    public const string Pop = "pop", Rock = "rock", Edm = "edm", HipHop = "hiphop", Acoustic = "acoustic", Orchestral = "orchestral",
        Dubstep = "dubstep", Riddim = "riddim", DeepDubstep = "deepdubstep", DrumBass = "dnb", House = "house", Trap = "trap",
        Reggaeton = "reggaeton", Reggae = "reggae", Metal = "metal", Rnb = "rnb", LoFi = "lofi", Country = "country";

    /// <summary>The half-width of a target's range at each frequency, before the genre's spread (dB).</summary>
    static readonly (double Hz, double Db)[] Width =
        [(20, 6), (40, 4.5), (80, 3.5), (150, 3), (300, 2.5), (3000, 2.5), (6000, 3), (10000, 3.5), (16000, 4.5), (20480, 6)];

    /// <summary>The factory targets, in the menu's order: from band music to electronic, then acoustic.</summary>
    public static readonly TonalTarget[] Factory =
    [
        Make(Pop, 1.0, 7, 10.5,
            [(20, -8), (40, 4), (60, 6), (100, 6), (200, 3), (500, 0.5), (1000, -1), (2000, -2), (4000, -4), (8000, -7), (12000, -10), (16000, -14), (20480, -22)]),
        Make(Rock, 1.0, 8, 11.5,
            [(20, -12), (40, 1), (60, 4), (100, 5), (200, 3), (500, 1), (1000, -0.5), (2000, -1.5), (4000, -3), (8000, -6.5), (12000, -10), (16000, -14), (20480, -22)]),
        // tight lows (kick and palm mutes, under a high-pass), a dense wall of guitars through the mids, kick click at 3–5 kHz
        Make(Metal, 0.9, 7, 10,
            [(20, -14), (40, -1), (60, 3), (100, 4.5), (200, 3), (500, 1.5), (1000, 0), (2000, -0.5), (4000, -2), (8000, -5.5), (12000, -9.5), (16000, -14), (20480, -22)]),
        Make(Country, 1.0, 8, 12,
            [(20, -14), (40, -1), (60, 3), (100, 4.5), (200, 3.5), (500, 1), (1000, -0.5), (2000, -1.5), (4000, -3.5), (8000, -7), (12000, -10.5), (16000, -14.5), (20480, -22)]),
        // warm lows, a forward voice, a smooth top
        Make(Rnb, 1.0, 7, 10,
            [(20, -6), (40, 6), (60, 8), (100, 7), (200, 4), (500, 1), (1000, -1), (2000, -2.5), (4000, -4.5), (8000, -7.5), (12000, -11), (16000, -15), (20480, -23)]),
        Make(HipHop, 0.9, 6.5, 9.5,
            [(20, -2), (40, 9), (60, 11), (100, 9), (200, 4), (500, 0), (1000, -1.5), (2000, -2.5), (4000, -4.5), (8000, -7.5), (12000, -10.5), (16000, -14), (20480, -22)]),
        // the 808 reaches lower than hip-hop's kick; bright hats over a darker body
        Make(Trap, 0.9, 5.5, 8.5,
            [(20, 0), (30, 8), (40, 11), (60, 11.5), (100, 8), (200, 3), (500, -0.5), (1000, -2), (2000, -3), (4000, -4.5), (8000, -6.5), (12000, -9), (16000, -12.5), (20480, -20)]),
        // dembow kick and bass at 50–80 Hz, the voice up front
        Make(Reggaeton, 0.9, 6, 9,
            [(20, -6), (40, 7), (60, 9.5), (100, 8), (200, 3.5), (500, 0.5), (1000, -1), (2000, -2), (4000, -3.5), (8000, -6), (12000, -9), (16000, -12.5), (20480, -20)]),
        // round bass at 60–120 Hz, relaxed mids and top, live drums
        Make(Reggae, 1.1, 7.5, 11,
            [(20, -8), (40, 5), (60, 9), (100, 9), (200, 4), (500, 0.5), (1000, -1.5), (2000, -3), (4000, -5), (8000, -8), (12000, -11), (16000, -15), (20480, -23)]),
        Make(Edm, 0.85, 6, 9,
            [(20, -4), (40, 8), (60, 10), (100, 9), (200, 4), (500, 0), (1000, -1.5), (2000, -2.5), (4000, -3.5), (8000, -5.5), (12000, -8), (16000, -11), (20480, -18)]),
        // a four-on-the-floor kick focused at 50–60 Hz, scooped low mids, open hats
        Make(House, 0.85, 6.5, 9.5,
            [(20, -6), (40, 7), (60, 10), (100, 8.5), (200, 3.5), (500, -0.5), (1000, -2), (2000, -3), (4000, -3.5), (8000, -5), (12000, -7.5), (16000, -10.5), (20480, -18)]),
        Make(Dubstep, 0.8, 5, 8,
            [(20, -6), (30, 4), (40, 9), (50, 10.5), (63, 10.5), (80, 9.5), (100, 8.5), (150, 7), (200, 5.5), (300, 3.5), (500, 1), (1000, -1),
             (2000, -1.5), (3000, -2), (4000, -2.5), (6000, -4), (8000, -5), (10000, -6.5), (12000, -8), (16000, -11), (20480, -18)]),
        Make(Riddim, 0.8, 4.5, 7,
            [(20, -6), (30, 5), (40, 9.5), (50, 10.5), (63, 10), (80, 9), (100, 8.5), (150, 7.5), (200, 6), (300, 4), (500, 1.5), (1000, -0.5),
             (2000, -1.5), (3000, -2), (4000, -2.5), (6000, -4), (8000, -5.5), (10000, -7), (12000, -8.5), (16000, -12), (20480, -19)]),
        Make(DeepDubstep, 0.95, 6, 9.5,
            [(20, -4), (30, 6), (40, 11), (50, 12), (63, 11.5), (80, 9), (100, 7), (150, 4), (200, 2.5), (300, 1), (500, -0.5), (1000, -2),
             (2000, -3.5), (4000, -5.5), (6000, -7.5), (8000, -9.5), (12000, -13), (16000, -17), (20480, -25)]),
        // sub under fast breaks: a scooped middle and the brightest top of the electronic targets
        Make(DrumBass, 0.9, 6, 9,
            [(20, -6), (40, 7), (60, 8), (100, 6), (200, 2.5), (500, -0.5), (1000, -1.5), (2000, -2), (4000, -3), (8000, -4.5), (12000, -7), (16000, -10), (20480, -17)]),
        // warm and rolled off on top (tape, vinyl, sampled loops), with soft, uncompressed drums
        Make(LoFi, 1.2, 8, 12,
            [(20, -12), (40, 2), (60, 5), (100, 6), (200, 4.5), (500, 1.5), (1000, -0.5), (2000, -2.5), (4000, -5.5), (8000, -10), (12000, -15), (16000, -21), (20480, -30)]),
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
