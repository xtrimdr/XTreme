using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtremeDaw.Projects;

/// <summary>A tempo change of the <see cref="TempoMap"/>: from <see cref="Beat"/> (quarter notes from the song's start) on.</summary>
public sealed class TempoPoint
{
    public double Beat { get; set; }
    public double Bpm { get; set; } = TempoMap.DefaultBpm;
    /// <summary>The tempo goes in a straight line (in beats) to the next point's; false = it holds until the next point and jumps. On the last point it waits for one to come after it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Ramp { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public TempoPoint Copy() => new() { Beat = Beat, Bpm = Bpm, Ramp = Ramp, Extra = Extra };
}

/// <summary>A time signature of the <see cref="TempoMap"/>, from bar <see cref="Bar"/> (counted from 0) on: <see cref="Num"/>/<see cref="Den"/>.</summary>
public sealed class MeterChange
{
    public int Bar { get; set; }
    public int Num { get; set; } = 4;
    public int Den { get; set; } = 4;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public MeterChange Copy() => new() { Bar = Bar, Num = Num, Den = Den, Extra = Extra };
}

/// <summary>
/// The song's tempo and meter (#214, asked by the user): tempo points in beats (quarter notes), each one a step or a
/// linear ramp to the next, and meter changes that always land on a bar start (any n/d; a bar of n/d holds n·4/d
/// beats). The first point is at beat 0 and is <see cref="ProjectSettings.Bpm"/>, the initial tempo, which older
/// projects and the transport's tempo still speak in. Everything on the timeline stays in seconds: what is musical
/// (MIDI clips, follow-tempo tracks, markers) is moved through this map when it changes (<see cref="TempoFollow.SetMap"/>).
/// <para>
/// Once in a project a map is never changed in place: edit a <see cref="Copy"/> (or use the <c>With…</c> helpers, which
/// return new maps) and hand it to <see cref="TempoFollow.SetMap"/>. That is what lets the audio thread read the same
/// object without locks: its lookups use arrays built once (<see cref="Prepare"/>) and allocate nothing, with a binary
/// search, so the click and anything else on the audio thread can call them per sample. A ramp is integrated exactly:
/// over L beats from t0 to t1 BPM it lasts 60·L/(t1−t0)·ln(t1/t0) seconds, and the inverse goes through exp.
/// </para>
/// </summary>
public sealed class TempoMap
{
    /// <summary>What a new project starts at (110, like the web app's).</summary>
    public const double DefaultBpm = 110;
    public const double MinBpm = 20, MaxBpm = 400;
    public const int MaxNum = 32;
    /// <summary>The note values a meter can count in.</summary>
    public static readonly int[] Dens = [1, 2, 4, 8, 16, 32];

    public List<TempoPoint> Points { get; set; } = [];
    public List<MeterChange> Meters { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>In beats: two positions closer than this are the same one (a click on a bar line that floating point left just short).</summary>
    const double Eps = 1e-9;
    /// <summary>A ramp whose ends differ by less than this (relative) is a step: its log would only add rounding.</summary>
    const double FlatRamp = 1e-12;

    /// <summary>The lookup tables, built once from the lists (<see cref="Prepare"/>); never changed after, so the audio thread can read them.</summary>
    sealed class Table
    {
        public required double[] Beat, Bpm, Sec;
        public required bool[] Ramp;
        public required int[] MBar, MNum, MDen;
        public required double[] MBeat, MLen;
    }
    Table? table;

    /// <summary>The tempo at beat 0: <see cref="ProjectSettings.Bpm"/>.</summary>
    [JsonIgnore] public double Bpm => Points.Count > 0 ? Points[0].Bpm : DefaultBpm;
    /// <summary>One tempo for the whole song (one point, no ramp).</summary>
    [JsonIgnore] public bool SingleTempo => T.Beat.Length == 1;

    public static TempoMap Constant(double bpm, int num = 4, int den = 4)
    {
        var m = new TempoMap { Points = [new() { Beat = 0, Bpm = bpm }], Meters = [new() { Bar = 0, Num = num, Den = den }] };
        m.Normalize();
        return m;
    }

    public TempoMap Copy() => new() { Points = [.. Points.Select(p => p.Copy())], Meters = [.. Meters.Select(x => x.Copy())], Extra = Extra };

    /// <summary>Every tempo multiplied by <paramref name="k"/> (a change of the whole song's speed, <see cref="TempoFollow.SetBpm"/>): every time in it divides by <paramref name="k"/>.</summary>
    public TempoMap Scaled(double k)
    {
        var m = Copy();
        foreach (var p in m.Points) p.Bpm *= k;
        m.Normalize();
        return m;
    }

    /// <summary>The same map from another initial tempo: the first point changes, the rest stay.</summary>
    public TempoMap WithInitial(double bpm)
    {
        var m = Copy();
        if (m.Points.Count == 0 || m.Points[0].Beat > 0) m.Points.Insert(0, new() { Beat = 0, Bpm = bpm });
        else m.Points[0].Bpm = bpm;
        m.Normalize();
        return m;
    }

    /// <summary>A map with a point at <paramref name="beat"/> (replacing one already there).</summary>
    public TempoMap WithPoint(double beat, double bpm, bool ramp = false)
    {
        var m = Copy();
        m.Points.RemoveAll(p => Math.Abs(p.Beat - beat) < Eps);
        m.Points.Add(new() { Beat = beat, Bpm = bpm, Ramp = ramp });
        m.Normalize();
        return m;
    }

    /// <summary>A map without the point at <paramref name="beat"/> (the first one, at 0, always stays).</summary>
    public TempoMap WithoutPoint(double beat)
    {
        var m = Copy();
        m.Points.RemoveAll(p => p.Beat > Eps && Math.Abs(p.Beat - beat) < Eps);
        m.Normalize();
        return m;
    }

    /// <summary>A map with <paramref name="num"/>/<paramref name="den"/> from bar <paramref name="bar"/> on.</summary>
    public TempoMap WithMeter(int bar, int num, int den)
    {
        var m = Copy();
        m.Meters.RemoveAll(x => x.Bar == bar);
        m.Meters.Add(new() { Bar = bar, Num = num, Den = den });
        m.Normalize();
        return m;
    }

    /// <summary>A map without the meter change at <paramref name="bar"/> (bar 0 always has one).</summary>
    public TempoMap WithoutMeter(int bar)
    {
        var m = Copy();
        if (bar > 0) m.Meters.RemoveAll(x => x.Bar == bar);
        m.Normalize();
        return m;
    }

    /// <summary>
    /// What the map says from <paramref name="beat"/> on, as a map that starts there (a clip written to a .mid file).
    /// The meters come along only from a bar start; from inside a bar, the meter there stays for the rest.
    /// </summary>
    public TempoMap From(double beat)
    {
        var t = T;
        var m = new TempoMap { Points = [new() { Beat = 0, Bpm = BpmAt(beat), Ramp = Ramp(beat) }] };
        for (int i = 0; i < t.Beat.Length; i++)
            if (t.Beat[i] > beat + Eps) m.Points.Add(new() { Beat = t.Beat[i] - beat, Bpm = t.Bpm[i], Ramp = t.Ramp[i] });
        int bar = BarAt(beat);
        var (num, den) = MeterAt(bar);
        m.Meters.Add(new() { Bar = 0, Num = num, Den = den });
        if (Math.Abs(BarStartBeat(bar) - beat) < Eps)
            for (int i = 0; i < t.MBar.Length; i++)
                if (t.MBar[i] > bar) m.Meters.Add(new() { Bar = t.MBar[i] - bar, Num = t.MNum[i], Den = t.MDen[i] });
        m.Normalize();
        return m;

        bool Ramp(double b)
        {
            int i = Segment(t, b);
            return t.Ramp[i];
        }
    }

    /// <summary>
    /// Puts the lists in order and in range and builds the lookup tables: points sorted by beat, one per beat (the
    /// last one wins), the first at 0 (an initial tempo out of range is <see cref="DefaultBpm"/>, the others are
    /// clamped to <see cref="MinBpm"/>..<see cref="MaxBpm"/>); meters sorted by bar, one per bar, bar 0 always (4/4
    /// when missing), numerators 1..<see cref="MaxNum"/> and denominators in <see cref="Dens"/>.
    /// </summary>
    public void Normalize()
    {
        Points ??= [];
        Meters ??= [];
        Points.RemoveAll(p => p == null || !double.IsFinite(p.Beat) || p.Beat < -Eps || !double.IsFinite(p.Bpm));
        foreach (var p in Points) if (p.Beat < Eps) p.Beat = 0;
        // stable, so of two points on the same beat the one added last stays
        var sorted = Points.Select((p, i) => (p, i)).OrderBy(x => x.p.Beat).ThenBy(x => x.i).Select(x => x.p).ToList();
        Points = [];
        foreach (var p in sorted)
        {
            if (Points.Count > 0 && Math.Abs(Points[^1].Beat - p.Beat) < Eps) Points[^1] = p;
            else Points.Add(p);
        }
        if (Points.Count == 0 || Points[0].Beat > 0) Points.Insert(0, new() { Beat = 0, Bpm = Points.Count > 0 ? Points[0].Bpm : DefaultBpm });
        if (!(Points[0].Bpm is >= MinBpm and <= MaxBpm)) Points[0].Bpm = DefaultBpm;
        for (int i = 1; i < Points.Count; i++) Points[i].Bpm = Math.Clamp(Points[i].Bpm, MinBpm, MaxBpm);

        Meters.RemoveAll(x => x == null || x.Bar < 0);
        var meters = Meters.Select((x, i) => (x, i)).OrderBy(x => x.x.Bar).ThenBy(x => x.i).Select(x => x.x).ToList();
        Meters = [];
        foreach (var x in meters)
        {
            x.Num = Math.Clamp(x.Num, 1, MaxNum);
            if (Array.IndexOf(Dens, x.Den) < 0) x.Den = 4;
            if (Meters.Count > 0 && Meters[^1].Bar == x.Bar) Meters[^1] = x;
            else Meters.Add(x);
        }
        if (Meters.Count == 0 || Meters[0].Bar > 0) Meters.Insert(0, new() { Bar = 0, Num = 4, Den = 4 });
        table = Build();
    }

    /// <summary>Builds the tables if they aren't yet: call it on the UI thread before the audio thread gets the map.</summary>
    public TempoMap Prepare()
    {
        _ = T;
        return this;
    }

    Table T => table ??= Build();

    Table Build()
    {
        // a map read but never normalized (a fresh deserialization) still answers; Normalize fixes what it holds
        var pts = Points is { Count: > 0 } ? Points : [new() { Beat = 0, Bpm = DefaultBpm }];
        var mts = Meters is { Count: > 0 } ? Meters : [new() { Bar = 0, Num = 4, Den = 4 }];
        int n = pts.Count;
        var t = new Table
        {
            Beat = new double[n], Bpm = new double[n], Sec = new double[n], Ramp = new bool[n],
            MBar = new int[mts.Count], MNum = new int[mts.Count], MDen = new int[mts.Count], MBeat = new double[mts.Count], MLen = new double[mts.Count],
        };
        for (int i = 0; i < n; i++)
        {
            t.Beat[i] = i == 0 ? 0 : pts[i].Beat;
            t.Bpm[i] = Math.Clamp(double.IsFinite(pts[i].Bpm) ? pts[i].Bpm : DefaultBpm, MinBpm, MaxBpm);
            t.Ramp[i] = pts[i].Ramp && i < n - 1;
        }
        for (int i = 1; i < n; i++) t.Sec[i] = t.Sec[i - 1] + SegmentSec(t, i - 1, t.Beat[i] - t.Beat[i - 1]);
        for (int i = 0; i < mts.Count; i++)
        {
            t.MBar[i] = i == 0 ? 0 : mts[i].Bar;
            t.MNum[i] = Math.Clamp(mts[i].Num, 1, MaxNum);
            t.MDen[i] = Array.IndexOf(Dens, mts[i].Den) >= 0 ? mts[i].Den : 4;
            t.MLen[i] = t.MNum[i] * 4.0 / t.MDen[i];
            t.MBeat[i] = i == 0 ? 0 : t.MBeat[i - 1] + (t.MBar[i] - t.MBar[i - 1]) * t.MLen[i - 1];
        }
        return t;
    }

    /// <summary>The point whose segment holds <paramref name="beat"/> (0 before the song's start).</summary>
    static int Segment(Table t, double beat)
    {
        int lo = 0, hi = t.Beat.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (t.Beat[mid] <= beat) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The point whose segment holds second <paramref name="sec"/>.</summary>
    static int SegmentOfSec(Table t, double sec)
    {
        int lo = 0, hi = t.Sec.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (t.Sec[mid] <= sec) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The meter change whose bars hold <paramref name="beat"/> (0 before the song's start).</summary>
    static int MeterOfBeat(Table t, double beat)
    {
        int lo = 0, hi = t.MBeat.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (t.MBeat[mid] <= beat + Eps) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    static int MeterOfBar(Table t, int bar)
    {
        int lo = 0, hi = t.MBar.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (t.MBar[mid] <= bar) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The tempo of segment <paramref name="i"/> at its end (its next point's for a ramp, its own for a step).</summary>
    static double EndBpm(Table t, int i) => t.Ramp[i] ? t.Bpm[i + 1] : t.Bpm[i];

    /// <summary>Seconds from point <paramref name="i"/> to <paramref name="x"/> beats past it.</summary>
    static double SegmentSec(Table t, int i, double x)
    {
        double t0 = t.Bpm[i];
        if (!t.Ramp[i] || x <= 0) return 60 * x / t0;
        double len = t.Beat[i + 1] - t.Beat[i], d = t.Bpm[i + 1] - t0;
        if (len <= 0 || Math.Abs(d) <= FlatRamp * t0) return 60 * x / t0;
        // the tempo grows linearly in beats: dt/dx = 60/T(x), T(x) = t0 + d·x/len
        return 60 * len / d * LogP1(d * x / (len * t0));
    }

    /// <summary>Beats from point <paramref name="i"/> to <paramref name="s"/> seconds past it.</summary>
    static double SegmentBeats(Table t, int i, double s)
    {
        double t0 = t.Bpm[i];
        if (!t.Ramp[i] || s <= 0) return s * t0 / 60;
        double len = t.Beat[i + 1] - t.Beat[i], d = t.Bpm[i + 1] - t0;
        if (len <= 0 || Math.Abs(d) <= FlatRamp * t0) return s * t0 / 60;
        return len * t0 / d * ExpM1(s * d / (60 * len));
    }

    /// <summary>ln(1 + x), exact for small x (Kahan): a gentle ramp doesn't lose its digits.</summary>
    static double LogP1(double x)
    {
        double u = 1 + x;
        return u == 1 ? x : Math.Log(u) * x / (u - 1);
    }

    /// <summary>e^x − 1, exact for small x (Kahan).</summary>
    static double ExpM1(double x)
    {
        double u = Math.Exp(x);
        if (u == 1) return x;
        double um = u - 1;
        return um == -1 ? -1 : um * x / Math.Log(u);
    }

    /// <summary>Seconds from the song's start to <paramref name="beat"/> (before 0, at the first tempo).</summary>
    public double SecAt(double beat)
    {
        var t = T;
        if (beat <= 0) return 60 * beat / t.Bpm[0];
        int i = Segment(t, beat);
        return t.Sec[i] + SegmentSec(t, i, beat - t.Beat[i]);
    }

    /// <summary>The beat at <paramref name="sec"/> seconds (before 0, at the first tempo).</summary>
    public double BeatAt(double sec)
    {
        var t = T;
        if (sec <= 0) return sec * t.Bpm[0] / 60;
        int i = SegmentOfSec(t, sec);
        double x = SegmentBeats(t, i, sec - t.Sec[i]);
        // the exp of a ramp can land a hair past its end
        if (i + 1 < t.Beat.Length) x = Math.Min(x, t.Beat[i + 1] - t.Beat[i]);
        return t.Beat[i] + x;
    }

    /// <summary>The tempo at <paramref name="beat"/>.</summary>
    public double BpmAt(double beat)
    {
        var t = T;
        if (beat <= 0) return t.Bpm[0];
        int i = Segment(t, beat);
        if (!t.Ramp[i]) return t.Bpm[i];
        double len = t.Beat[i + 1] - t.Beat[i];
        return len <= 0 ? t.Bpm[i] : t.Bpm[i] + (EndBpm(t, i) - t.Bpm[i]) * (beat - t.Beat[i]) / len;
    }

    public double BpmAtSec(double sec) => BpmAt(BeatAt(sec));

    /// <summary>Seconds per beat at <paramref name="beat"/>.</summary>
    public double BeatSecAt(double beat) => 60 / BpmAt(beat);

    /// <summary>The bar (from 0) that holds <paramref name="beat"/>; bars before the song's start count back in its first meter.</summary>
    public int BarAt(double beat)
    {
        var t = T;
        int i = MeterOfBeat(t, beat);
        return t.MBar[i] + (int)Math.Floor((beat - t.MBeat[i]) / t.MLen[i] + Eps);
    }

    /// <summary>The beat where bar <paramref name="bar"/> starts.</summary>
    public double BarStartBeat(int bar)
    {
        var t = T;
        int i = bar < 0 ? 0 : MeterOfBar(t, bar);
        return t.MBeat[i] + (bar - t.MBar[i]) * t.MLen[i];
    }

    public double BarStartSec(int bar) => SecAt(BarStartBeat(bar));

    /// <summary>The meter of bar <paramref name="bar"/>.</summary>
    public (int Num, int Den) MeterAt(int bar)
    {
        var t = T;
        int i = bar < 0 ? 0 : MeterOfBar(t, bar);
        return (t.MNum[i], t.MDen[i]);
    }

    /// <summary>How many beats (quarter notes) bar <paramref name="bar"/> holds: n·4/d.</summary>
    public double BarBeats(int bar)
    {
        var t = T;
        return t.MLen[bar < 0 ? 0 : MeterOfBar(t, bar)];
    }

    /// <summary>The start of the bar that holds <paramref name="beat"/> (a beat right on a bar line is that bar's start).</summary>
    public double BarFloorBeat(double beat) => BarStartBeat(BarAt(beat));

    /// <summary>
    /// Where <paramref name="sec"/> falls for the clock: its bar (from 0) and, inside it, how many of the meter's
    /// units (the denominator's note: eighths in 6/8) have gone by (from 0, with the fraction).
    /// </summary>
    public void BarBeatAt(double sec, out int bar, out double unit)
    {
        var t = T;
        double beat = BeatAt(sec);
        int i = MeterOfBeat(t, beat);
        bar = t.MBar[i] + (int)Math.Floor((beat - t.MBeat[i]) / t.MLen[i] + Eps);
        double start = t.MBeat[i] + (bar - t.MBar[i]) * t.MLen[i];
        unit = Math.Max(0, (beat - start) * t.MDen[i] / 4);
    }

    /// <summary>
    /// The click at or after <paramref name="beat"/> (after it, with <paramref name="strict"/>): one on every unit of
    /// the meter (the denominator's note: quarters in 4/4, eighths in 6/8 and 7/8), counted from each meter change, and
    /// <paramref name="accent"/> on the first of each bar. Allocates nothing: the audio thread calls it.
    /// </summary>
    public double ClickAt(double beat, bool strict, out bool accent)
    {
        var t = T;
        int i = MeterOfBeat(t, beat);
        double u = 4.0 / t.MDen[i], rel = (beat - t.MBeat[i]) / u;
        double k = strict ? Math.Floor(rel + Eps) + 1 : Math.Ceiling(rel - Eps);
        double at = t.MBeat[i] + k * u;
        if (i + 1 < t.MBeat.Length && at >= t.MBeat[i + 1] - Eps * u)
        {
            accent = true;
            return t.MBeat[i + 1];
        }
        double m = k % t.MNum[i];
        accent = m == 0;
        return at;
    }

    /// <summary>The length of a click (the meter's unit) at <paramref name="beat"/>, in beats.</summary>
    public double ClickBeats(double beat)
    {
        var t = T;
        return 4.0 / t.MDen[MeterOfBeat(t, beat)];
    }

    /// <summary>
    /// <paramref name="beat"/> on a grid of <paramref name="step"/> beats that starts over at each bar (so a dotted
    /// value, or a step that doesn't fit the bar's meter, lines up with every bar): the nearest line, or with
    /// <paramref name="cell"/> the start of the cell it falls in. A step of 0 or less is the bar.
    /// </summary>
    public double SnapBeat(double beat, double step, bool cell = false)
    {
        int bar = BarAt(beat);
        double start = BarStartBeat(bar), len = BarBeats(bar), r = beat - start;
        if (step <= 0 || step >= len - Eps) return start + (cell || r < len / 2 ? 0 : len);
        double lo = Math.Floor(r / step + Eps) * step, hi = Math.Min(lo + step, len);
        return start + (cell || r - lo < hi - r ? lo : hi);
    }

    /// <summary>A timeline time on the grid of a value of <see cref="Subdivisions"/> ("bar", "1/8", "1/4T"…), meter by meter.</summary>
    public double SnapSec(double sec, string mode, bool cell = false)
    {
        double step = mode == "bar" ? 0 : Subdivisions.Beats(mode);
        if (mode != "bar" && step <= 0) return sec;
        return SecAt(SnapBeat(BeatAt(sec), step, cell));
    }

    /// <summary>Same points and meters (the lookup tables don't count).</summary>
    public bool SameAs(TempoMap other)
    {
        var a = T;
        var b = other.T;
        if (a.Beat.Length != b.Beat.Length || a.MBar.Length != b.MBar.Length) return false;
        for (int i = 0; i < a.Beat.Length; i++)
            if (a.Beat[i] != b.Beat[i] || a.Bpm[i] != b.Bpm[i] || a.Ramp[i] != b.Ramp[i]) return false;
        for (int i = 0; i < a.MBar.Length; i++)
            if (a.MBar[i] != b.MBar[i] || a.MNum[i] != b.MNum[i] || a.MDen[i] != b.MDen[i]) return false;
        return true;
    }
}
