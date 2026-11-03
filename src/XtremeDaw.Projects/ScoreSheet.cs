using System.Globalization;
using System.IO.Compression;
using System.Text;
using XtremeDaw.Audio.Midi;

namespace XtremeDaw.Projects;

/// <summary>
/// A notehead of the score: its pitch as spelled (<see cref="Step"/> = 7 × octave + letter, C = 0; <see cref="Alter"/> =
/// -1, 0 or 1), whether a tie comes in or goes out, and the sign written before it (null = none, 0 = a natural).
/// </summary>
public sealed record ScoreHead(int Pitch, int Step, int Alter, bool TieIn, bool TieOut)
{
    public int? Accidental { get; set; }
}

/// <summary>A note, chord or rest: where it starts in its bar and how long it is, both in sixteenths (a dot included).</summary>
public sealed record ScoreEvent(int Start, int Value, List<ScoreHead> Heads)
{
    public bool IsRest => Heads.Count == 0;
    public bool Dotted => Value is 12 or 6 or 3;
    /// <summary>The written value without its dot: 16, 8, 4, 2 or 1.</summary>
    public int Shape => Dotted ? Value * 2 / 3 : Value;
}

/// <summary>A 4/4 bar of the grand staff: one or two voices per staff, each one filling the bar.</summary>
public sealed record ScoreBar(List<List<ScoreEvent>> Treble, List<List<ScoreEvent>> Bass);

/// <summary>The music of a sheet before any geometry: the key signature (sharps above 0, flats below) and the bars.</summary>
public sealed record ScoreMusic(int Fifths, List<ScoreBar> Bars);

/// <summary>Where a bar's events go: the ticks that have one, their x from the bar's left and the width it asks for, in points.</summary>
public sealed record ScoreColumns(int[] Ticks, double[] X, double Width)
{
    public double At(int tick) => X[Math.Max(0, Array.IndexOf(Ticks, tick))];
}

/// <summary>
/// A system on the page: its bars, how much they are stretched to fill the line, where the first one starts and the
/// height of each staff's bottom line.
/// </summary>
public sealed record ScoreLine(int First, int Count, double Stretch, double X, double TrebleY, double BassY);

/// <summary>A page of systems.</summary>
public sealed record ScorePage(List<ScoreLine> Lines);

/// <summary>
/// A clip as a printable piano score: a PDF written by hand, vector only, with no library (no new dependency for one
/// page format) and no font except the built-in Helvetica. Two steps that don't know each other's business:
/// <see cref="Bars"/> turns notes into bars of voices (music only, easy to check) and <see cref="Pages"/> gives it its
/// geometry, which <see cref="ScorePdf"/> draws. Each staff has up to two voices because held notes under moving ones
/// are common in a piano part and a single voice would break them into chains of ties.
/// </summary>
public static class ScoreSheet
{
    /// <summary>Middle C and up go on the treble staff.</summary>
    public const int SplitPitch = 60;
    public const int StepsPerBar = 16, StepsPerBeat = 4;
    /// <summary>How many sixteenths a note can overlap the next one and still be read as legato, not as two voices.</summary>
    const int LegatoSteps = 1;
    const int MaxBarsPerLine = 6;
    /// <summary>A last line with few bars doesn't stretch to the margin.</summary>
    const double MaxLastStretch = 1.4;

    public const double PageWidth = 595.28, PageHeight = 841.89, MarginX = 40, MarginTop = 42, MarginBottom = 40;
    /// <summary>Distance between two staff lines, what everything on the staff is measured in.</summary>
    public const double Space = 6.4;
    /// <summary>What the title takes on the first page.</summary>
    const double TitleHeight = 44;
    /// <summary>Between the bass staff's top line and the treble staff's bottom one, and between two systems.</summary>
    const double StaffGap = 8 * Space, SystemGap = 5 * Space;
    /// <summary>Above the treble staff and below the bass one, for stems and bar numbers, before any ledger line.</summary>
    const double Headroom = 2 * Space;
    public const double StaffHeight = 4 * Space;

    /// <summary>The bars of <paramref name="notes"/>, quantized to sixteenths; what passes <paramref name="beats"/> is cut, muted notes are skipped.</summary>
    public static ScoreMusic Bars(IReadOnlyList<MidiNote> notes, double beats, MusicScale? scale)
    {
        int bars = Math.Max(1, (int)Math.Ceiling(beats / (StepsPerBar / StepsPerBeat) - 1e-9));
        int limit = Math.Min(bars * StepsPerBar, (int)Math.Round(beats * StepsPerBeat));
        var key = new ScoreKey(scale);
        var quantized = Quantized(notes, limit);
        var treble = Voices(quantized.Where(n => n.Pitch >= SplitPitch));
        var bass = Voices(quantized.Where(n => n.Pitch < SplitPitch));
        var list = new List<ScoreBar>(bars);
        for (int bar = 0; bar < bars; bar++) list.Add(new ScoreBar(Staff(treble, bar, key), Staff(bass, bar, key)));
        return new ScoreMusic(key.Fifths, list);
    }

    /// <summary>A note on the grid, in sixteenths from the clip's start.</summary>
    readonly record struct Step(int Pitch, int Start, int End);

    /// <summary>Notes held the same stretch in a voice, under one stem.</summary>
    sealed class Chord(int start, int end, int pitch)
    {
        public readonly int Start = start;
        public int End = end;
        public readonly List<int> Pitches = [pitch];
    }

    /// <summary>On the grid, in order and with no key struck over itself: a repeated note ends where the next one starts.</summary>
    static List<Step> Quantized(IReadOnlyList<MidiNote> notes, int limit)
    {
        var list = new List<Step>();
        foreach (var n in notes)
        {
            int start = (int)Math.Round(n.Beat * StepsPerBeat);
            if (n.Muted || start >= limit) continue;
            list.Add(new Step(n.Pitch, start, Math.Min(limit, Math.Max(start + 1, (int)Math.Round(n.EndBeat * StepsPerBeat)))));
        }
        list.Sort((a, b) => a.Pitch != b.Pitch ? a.Pitch.CompareTo(b.Pitch) : a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var clean = new List<Step>();
        foreach (var n in list)
        {
            if (clean.Count > 0 && clean[^1] is var last && last.Pitch == n.Pitch && last.End > n.Start)
            {
                if (last.Start == n.Start) continue;
                clean[^1] = last with { End = n.Start };
            }
            clean.Add(n);
        }
        clean.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Pitch.CompareTo(a.Pitch));
        return clean;
    }

    /// <summary>
    /// Splits a staff into two voices, higher notes first: what doesn't fit in the first goes to the second, and with
    /// both busy the note ends what started before it, so nothing is lost but a held tail.
    /// </summary>
    static List<Chord>[] Voices(IEnumerable<Step> notes)
    {
        List<Chord>[] voices = [[], []];
        foreach (var n in notes)
        {
            foreach (var v in voices)
                if (v.Count > 0 && v[^1] is var c && c.Start < n.Start && c.End > n.Start && c.End - n.Start <= LegatoSteps) c.End = n.Start;
            var free = Array.Find(voices, v => v.Count == 0 || v[^1].End <= n.Start);
            var same = Array.Find(voices, v => v.Count > 0 && v[^1].Start == n.Start && v[^1].End == n.End);
            if (same != null) same[^1].Pitches.Add(n.Pitch);
            else if (free != null) free.Add(new Chord(n.Start, n.End, n.Pitch));
            else if (Array.Find(voices, v => v[^1].Start == n.Start) is { } joined) joined[^1].Pitches.Add(n.Pitch);
            else
            {
                var soonest = voices[0][^1].End <= voices[1][^1].End ? voices[0] : voices[1];
                soonest[^1].End = n.Start;
                soonest.Add(new Chord(n.Start, n.End, n.Pitch));
            }
        }
        return voices;
    }

    /// <summary>A staff's bar: the first voice always, the second only where it has something; then its accidentals.</summary>
    static List<List<ScoreEvent>> Staff(List<Chord>[] voices, int bar, ScoreKey key)
    {
        var staff = new List<List<ScoreEvent>> { Events(voices[0], bar, key) };
        int from = bar * StepsPerBar, to = from + StepsPerBar;
        if (voices[1].Exists(c => c.Start < to && c.End > from)) staff.Add(Events(voices[1], bar, key));
        var state = new Dictionary<int, int>();
        foreach (var e in staff.SelectMany(v => v).Where(e => !e.IsRest).OrderBy(e => e.Start))
            foreach (var h in e.Heads)
            {
                int now = state.TryGetValue(h.Step, out int s) ? s : key.AlterOf(h.Step);
                if (!h.TieIn && h.Alter != now) h.Accidental = h.Alter;
                state[h.Step] = h.Alter;
            }
        return staff;
    }

    /// <summary>A voice's bar: what it plays, cut at the bar lines, and rests in the gaps.</summary>
    static List<ScoreEvent> Events(List<Chord> voice, int bar, ScoreKey key)
    {
        int from = bar * StepsPerBar, to = from + StepsPerBar, at = 0;
        var events = new List<ScoreEvent>();
        void Rest(int start, int end)
        {
            foreach (var (s, v) in Spell(start, end - start, true)) events.Add(new ScoreEvent(s, v, []));
        }
        foreach (var c in voice)
        {
            if (c.End <= from || c.Start >= to) continue;
            int start = Math.Max(c.Start, from) - from, end = Math.Min(c.End, to) - from;
            if (start > at) Rest(at, start);
            var pieces = Spell(start, end - start, false);
            for (int i = 0; i < pieces.Count; i++)
            {
                bool tieIn = i > 0 || c.Start < from, tieOut = i < pieces.Count - 1 || c.End > to;
                var heads = c.Pitches.Order().Select(p => key.Head(p, tieIn, tieOut)).ToList();
                events.Add(new ScoreEvent(pieces[i].Start, pieces[i].Value, heads));
            }
            at = end;
        }
        if (at < StepsPerBar) Rest(at, StepsPerBar);
        return events;
    }

    /// <summary>
    /// A stretch of a bar as written values, in sixteenths, longest first: a value never hides a beat it doesn't start
    /// on, and a half starts on beat 1 or 3. Rests have no dots.
    /// </summary>
    static readonly int[] RestValues = [16, 8, 4, 2, 1], NoteValues = [16, 12, 8, 6, 4, 3, 2, 1];

    public static List<(int Start, int Value)> Spell(int start, int length, bool rest)
    {
        var values = new List<(int, int)>();
        while (length > 0)
        {
            int value = Array.Find(rest ? RestValues : NoteValues, v => v <= length && Fits(start, v));
            values.Add((start, value));
            start += value;
            length -= value;
        }
        return values;
    }

    static bool Fits(int at, int value) => value switch
    {
        16 => at == 0,
        12 => at is 0 or 4,
        8 or 6 => at % 8 == 0,
        4 => at % 4 == 0,
        _ => at % StepsPerBeat + value <= StepsPerBeat,
    };

    /// <summary>A bar's columns: the same on both staves, with more room for longer values and for accidentals.</summary>
    public static ScoreColumns Columns(ScoreBar bar)
    {
        var events = bar.Treble.Concat(bar.Bass).SelectMany(v => v).ToList();
        int[] ticks = [.. events.Select(e => e.Start).Append(0).Distinct().Order()];
        var xs = new double[ticks.Length];
        double x = 1.6 * Space;
        for (int i = 0; i < ticks.Length; i++)
        {
            int signs = events.Where(e => e.Start == ticks[i]).Select(e => e.Heads.Count(h => h.Accidental != null)).DefaultIfEmpty().Max();
            x += Math.Min(signs, 2) * 1.2 * Space;
            xs[i] = x;
            int next = i + 1 < ticks.Length ? ticks[i + 1] : StepsPerBar;
            x += (1.6 + 1.1 * Math.Sqrt(next - ticks[i])) * Space;
        }
        return new ScoreColumns(ticks, xs, Math.Max(12 * Space, x));
    }

    /// <summary>What comes before the first bar of a line: clef, key signature and, on the first one, the time signature.</summary>
    public static double Prefix(int fifths, bool first) => (4.4 + Math.Abs(fifths) * 0.95 + (first ? 2.4 : 0)) * Space;

    /// <summary>Half spaces above the staff's bottom line (E4 on the treble staff, G2 on the bass one).</summary>
    public static int Position(ScoreHead head, bool treble) => head.Step - (treble ? 30 : 18);

    /// <summary>The geometry: as many bars on a line as their notes let fit, and as many lines on a page as their ledger lines let fit.</summary>
    public static List<ScorePage> Pages(ScoreMusic music)
    {
        var columns = music.Bars.Select(Columns).ToList();
        double content = PageWidth - 2 * MarginX;
        var pages = new List<ScorePage> { new([]) };
        double y = PageHeight - MarginTop - TitleHeight;
        for (int first = 0; first < music.Bars.Count;)
        {
            double prefix = Prefix(music.Fifths, first == 0), room = content - prefix, sum = 0;
            int count = 0;
            while (first + count < music.Bars.Count && count < MaxBarsPerLine)
            {
                double w = columns[first + count].Width;
                if (count > 0 && sum + w > room) break;
                sum += w;
                count++;
            }
            double stretch = room / sum;
            if (first + count == music.Bars.Count) stretch = Math.Min(stretch, MaxLastStretch);

            var bars = music.Bars.GetRange(first, count);
            int top = bars.SelectMany(b => b.Treble).SelectMany(v => v).SelectMany(e => e.Heads).Select(h => Position(h, true)).Append(8).Max();
            int low = bars.SelectMany(b => b.Bass).SelectMany(v => v).SelectMany(e => e.Heads).Select(h => Position(h, false)).Append(0).Min();
            double above = Headroom + (top - 8) * Space / 2, below = Headroom - low * Space / 2;
            double height = above + 2 * StaffHeight + StaffGap + below;
            if (y - height < MarginBottom && pages[^1].Lines.Count > 0)
            {
                pages.Add(new([]));
                y = PageHeight - MarginTop;
            }
            double trebleY = y - above - StaffHeight;
            pages[^1].Lines.Add(new ScoreLine(first, count, stretch, MarginX + prefix, trebleY, trebleY - StaffGap - StaffHeight));
            y -= height + SystemGap;
            first += count;
        }
        return pages;
    }

    /// <summary>Writes the sheet (atomically: an old file stays whole if this fails) and returns how many pages it has.</summary>
    public static int Write(string path, string title, IReadOnlyList<MidiNote> notes, double beats, MusicScale? scale)
    {
        var music = Bars(notes, beats, scale);
        var pages = Pages(music);
        byte[] pdf = ScorePdf.Render(title, music, pages);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, pdf);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return pages.Count;
    }
}

/// <summary>
/// How a key spells pitches. A natural major or minor scale gives a key signature; any other scale gives none, but its
/// tonality still chooses whether black keys outside the key are written as sharps or as flats.
/// </summary>
sealed class ScoreKey
{
    static readonly int[] Naturals = [0, 2, 4, 5, 7, 9, 11];
    /// <summary>Letters in the order sharps and flats enter a key signature (C = 0).</summary>
    static readonly int[] SharpOrder = [3, 0, 4, 1, 5, 2, 6], FlatOrder = [6, 2, 5, 1, 4, 0, 3];
    /// <summary>The major key of each root: its sharps, or flats below 0.</summary>
    static readonly int[] MajorFifths = [0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5];

    readonly int[] alter = new int[7];
    readonly bool flats;
    public readonly int Fifths;

    public ScoreKey(MusicScale? scale)
    {
        if (scale == null) return;
        bool minor = scale.Kind.Group == MusicScale.Minor || scale.Kind.Group == MusicScale.Other && scale.Contains(scale.Root + 3) && !scale.Contains(scale.Root + 4);
        int fifths = MajorFifths[(scale.Root + (minor ? 3 : 0)) % 12];
        flats = fifths < 0;
        if (scale.Kind.Id is not (MusicScale.Major or MusicScale.Minor)) return;
        Fifths = fifths;
        for (int i = 0; i < Math.Abs(fifths); i++)
            if (fifths > 0) alter[SharpOrder[i]] = 1;
            else alter[FlatOrder[i]] = -1;
    }

    /// <summary>What the key signature does to a step.</summary>
    public int AlterOf(int step) => alter[(step % 7 + 7) % 7];

    public ScoreHead Head(int pitch, bool tieIn, bool tieOut)
    {
        var (letter, a) = Spell(pitch);
        int octave = (int)Math.Floor((pitch - a) / 12.0) - 1;
        return new ScoreHead(pitch, octave * 7 + letter, a, tieIn, tieOut);
    }

    /// <summary>The key's own note if it has one; if not, a natural; if not, a sharp or a flat by the key's side.</summary>
    (int Letter, int Alter) Spell(int pitch)
    {
        int pc = (pitch % 12 + 12) % 12;
        for (int l = 0; l < 7; l++)
            if ((Naturals[l] + alter[l] + 12) % 12 == pc) return (l, alter[l]);
        int i = Array.IndexOf(Naturals, pc);
        if (i >= 0) return (i, 0);
        return flats ? (Array.IndexOf(Naturals, (pc + 1) % 12), -1) : (Array.IndexOf(Naturals, pc - 1), 1);
    }
}

/// <summary>
/// The drawing operators of a PDF page, in points with y up. Numbers always go out with a point: the app runs in a
/// culture with a decimal comma.
/// </summary>
sealed class PdfCanvas
{
    readonly StringBuilder o = new();

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public override string ToString() => o.ToString();

    public PdfCanvas Op(string s) { o.Append(s).Append('\n'); return this; }
    public void Width(double w) => Op($"{F(w)} w");
    public PdfCanvas M(double x, double y) => Op($"{F(x)} {F(y)} m");
    public PdfCanvas L(double x, double y) => Op($"{F(x)} {F(y)} l");
    public PdfCanvas C(double a, double b, double c, double d, double x, double y) => Op($"{F(a)} {F(b)} {F(c)} {F(d)} {F(x)} {F(y)} c");
    public void Fill() => Op("h f");
    public void FillHoles() => Op("h f*");
    public void Stroke() => Op("S");

    /// <summary>Drawing in a unit of its own around (x, y); negative scales mirror.</summary>
    public void Push(double x, double y, double sx, double sy) => Op($"q {F(sx)} 0 0 {F(sy)} {F(x)} {F(y)} cm");
    public void Pop() => Op("Q");

    public void Line(double x1, double y1, double x2, double y2, double width)
    {
        Width(width);
        M(x1, y1).L(x2, y2).Stroke();
    }

    public void Rect(double x, double y, double w, double h) => Op($"{F(x)} {F(y)} {F(w)} {F(h)} re f");

    /// <summary>An ellipse turned <paramref name="degrees"/>, as four Bézier curves, added to the current path.</summary>
    public void Ellipse(double cx, double cy, double rx, double ry, double degrees = 0)
    {
        const double K = 0.5522847498; // the handle that makes a Bézier quarter of a circle
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        (double X, double Y) P(double x, double y) => (cx + x * rx * cos - y * ry * sin, cy + x * rx * sin + y * ry * cos);
        void Q(double x1, double y1, double x2, double y2, double x, double y)
        {
            var (a1, b1) = P(x1, y1);
            var (a2, b2) = P(x2, y2);
            var (a3, b3) = P(x, y);
            C(a1, b1, a2, b2, a3, b3);
        }
        var (x0, y0) = P(1, 0);
        M(x0, y0);
        Q(1, K, K, 1, 0, 1);
        Q(-K, 1, -1, K, -1, 0);
        Q(-1, -K, -K, -1, 0, -1);
        Q(K, -1, 1, -K, 1, 0);
    }

    public void Text(double x, double y, double size, string escaped) => Op($"BT /F1 {F(size)} Tf {F(x)} {F(y)} Td ({escaped}) Tj ET");
}

/// <summary>The shapes of the notation as paths, each in staff spaces around its own origin: the file needs no music font.</summary>
static class ScoreGlyphs
{
    const double Sp = ScoreSheet.Space;

    /// <summary>The treble clef, around the G line.</summary>
    public static void Treble(PdfCanvas c, double x, double y)
    {
        c.Push(x, y, Sp, Sp);
        c.Width(0.17);
        c.Op("1 J 1 j");
        c.M(0.12, -0.62).C(-0.72, -0.62, -0.72, 0.8, 0.18, 0.8).C(1.2, 0.8, 1.3, -1.02, 0.02, -1.02)
            .C(-1.32, -1.02, -1.5, 0.72, -0.5, 1.72).C(0.08, 2.32, 0.78, 2.92, 0.72, 3.72)
            .C(0.68, 4.42, 0.36, 4.72, 0.2, 4.36).C(-0.1, 3.7, -0.08, 2.9, 0.04, 2.2).L(0.56, -1.86)
            .C(0.68, -2.72, -0.34, -2.92, -0.46, -2.22).Stroke();
        c.Ellipse(-0.22, -2.14, 0.3, 0.3);
        c.Fill();
        c.Op("0 J 0 j");
        c.Pop();
    }

    /// <summary>The bass clef, around the F line.</summary>
    public static void Bass(PdfCanvas c, double x, double y)
    {
        c.Push(x, y, Sp, Sp);
        c.M(-0.75, 0.05).C(-0.72, 0.8, 0, 1.05, 0.42, 1.05).C(1.15, 1.05, 1.5, 0.55, 1.5, -0.1)
            .C(1.5, -1.15, 0.4, -2.0, -0.75, -2.4).C(0.1, -1.9, 0.82, -1.05, 0.82, -0.1)
            .C(0.82, 0.5, 0.62, 0.85, 0.22, 0.85).C(-0.15, 0.85, -0.4, 0.6, -0.48, 0.25);
        c.Fill();
        c.Ellipse(-0.38, 0.04, 0.38, 0.38);
        c.Fill();
        c.Ellipse(1.88, 0.5, 0.17, 0.17);
        c.Fill();
        c.Ellipse(1.88, -0.5, 0.17, 0.17);
        c.Fill();
        c.Pop();
    }

    /// <summary>A "4" two spaces tall, its foot at y: the time signature.</summary>
    public static void Four(PdfCanvas c, double x, double y)
    {
        c.Push(x, y, Sp, Sp);
        c.M(0.38, 2).L(0.82, 2).L(0.82, 0.72).L(1.12, 0.72).L(1.12, 0.46).L(0.82, 0.46).L(0.82, 0.14).L(1.02, 0.08).L(1.02, 0)
            .L(0.08, 0).L(0.08, 0.08).L(0.3, 0.14).L(0.3, 0.46).L(-0.62, 0.46).L(-0.62, 0.68);
        c.Op("h");
        c.M(0.3, 1.5).L(-0.3, 0.72).L(0.3, 0.72);
        c.FillHoles();
        c.Pop();
    }

    public static void Accidental(PdfCanvas c, int alter, double x, double y)
    {
        c.Push(x, y, Sp, Sp);
        if (alter > 0) Sharp(c);
        else if (alter < 0) Flat(c);
        else Natural(c);
        c.Pop();
    }

    static void Bar(PdfCanvas c, double left, double right, double y, double rise, double thick)
    {
        c.M(left, y - rise).L(right, y + rise).L(right, y + rise + thick).L(left, y - rise + thick);
        c.Fill();
    }

    static void Sharp(PdfCanvas c)
    {
        c.Line(-0.22, -1.32, -0.22, 1.2, 0.09);
        c.Line(0.22, -1.2, 0.22, 1.32, 0.09);
        Bar(c, -0.5, 0.5, -0.58, 0.14, 0.26);
        Bar(c, -0.5, 0.5, 0.34, 0.14, 0.26);
    }

    static void Flat(PdfCanvas c)
    {
        c.Line(-0.34, -0.58, -0.34, 1.75, 0.09);
        c.M(-0.34, -0.62).C(0.3, -0.16, 0.62, 0.2, 0.5, 0.5).C(0.36, 0.86, -0.08, 0.72, -0.34, 0.36).L(-0.34, 0.16)
            .C(-0.08, 0.5, 0.24, 0.52, 0.24, 0.28).C(0.24, 0.02, -0.08, -0.26, -0.34, -0.42);
        c.Fill();
    }

    static void Natural(PdfCanvas c)
    {
        c.Line(-0.3, -0.76, -0.3, 1.42, 0.09);
        c.Line(0.3, -1.42, 0.3, 0.76, 0.09);
        Bar(c, -0.3, 0.3, -0.56, 0.08, 0.24);
        Bar(c, -0.3, 0.3, 0.34, 0.08, 0.24);
    }

    /// <summary>A head centered on (x, y): 16 whole, 8 half, anything else filled.</summary>
    public static void Head(PdfCanvas c, int shape, double x, double y)
    {
        c.Push(x, y, Sp, Sp);
        if (shape >= 16)
        {
            c.Ellipse(0, 0, 0.8, 0.5);
            c.Ellipse(0, 0, 0.44, 0.27, 62);
            c.FillHoles();
        }
        else if (shape == 8)
        {
            c.Ellipse(0, 0, 0.64, 0.42, 22);
            c.Ellipse(0, 0, 0.56, 0.2, 28);
            c.FillHoles();
        }
        else
        {
            c.Ellipse(0, 0, 0.64, 0.43, 22);
            c.Fill();
        }
        c.Pop();
    }

    /// <summary>The flags of an eighth (one) or a sixteenth (two) from the tip of a stem; <paramref name="dir"/> 1 = stem up.</summary>
    public static void Flags(PdfCanvas c, int count, double x, double y, int dir)
    {
        c.Push(x, y, Sp, dir * Sp);
        for (int i = 0; i < count; i++)
        {
            double top = -i * 0.9;
            c.M(0, top).C(0.12, top - 0.8, 1.45, top - 1.1, 1.0, top - 2.4).C(1.1, top - 1.7, 0.5, top - 1.3, 0, top - 0.95);
            c.Fill();
        }
        c.Pop();
    }

    /// <summary>A quarter rest around the middle line.</summary>
    public static void QuarterRest(PdfCanvas c, double x, double y)
    {
        c.Push(x, y, Sp, Sp);
        c.M(-0.34, 1.56).L(0.44, 0.62).C(0.14, 0.26, 0.1, -0.08, 0.46, -0.5).L(0.5, -0.44)
            .C(0.0, -0.72, -0.34, -0.6, -0.12, -1.42).L(-0.2, -1.46).C(-0.86, -0.86, -0.62, -0.1, -0.02, -0.26)
            .L(-0.44, 0.22).C(-0.12, 0.62, -0.08, 0.98, -0.42, 1.48);
        c.Fill();
        c.Pop();
    }

    /// <summary>An eighth (one ball) or sixteenth rest (two) around the middle line.</summary>
    public static void FlagRest(PdfCanvas c, int balls, double x, double y)
    {
        c.Push(x, y, Sp, Sp);
        double top = balls == 1 ? 0.92 : 1.42;
        double foot = balls == 1 ? -1 : -2;
        c.Line(0.52, top, 0.02, foot, 0.12);
        for (int i = 0; i < balls; i++)
        {
            double t = top - i * 0.96, sx = 0.52 - i * 0.25;
            c.Ellipse(sx - 0.72, t - 0.1, 0.24, 0.24);
            c.Fill();
            c.M(sx - 0.88, t - 0.26).C(sx - 0.5, t - 0.46, sx - 0.2, t - 0.3, sx + 0.04, t + 0.04).L(sx - 0.02, t - 0.1)
                .C(sx - 0.28, t - 0.44, sx - 0.6, t - 0.56, sx - 0.88, t - 0.3);
            c.Fill();
        }
        c.Pop();
    }

    /// <summary>The brace joining the two staves.</summary>
    public static void Brace(PdfCanvas c, double x, double bottom, double top)
    {
        double h = top - bottom, m = (top + bottom) / 2, w = 0.9 * Sp;
        void Half(double end, double s)
        {
            c.M(x, end).C(x - 1.4 * w, end - s * 0.14 * h, x + 0.35 * w, m + s * 0.1 * h, x - w, m)
                .C(x + 0.75 * w, m + s * 0.12 * h, x - 0.9 * w, end - s * 0.16 * h, x, end);
            c.Fill();
        }
        Half(top, 1);
        Half(bottom, -1);
    }
}

/// <summary>
/// Draws the laid out music and assembles the file by hand. Each page is a compressed content stream; the only other
/// objects are the catalog, the page tree, the Helvetica font and the document's title.
/// </summary>
static class ScorePdf
{
    const double Sp = ScoreSheet.Space;
    /// <summary>Width of a head, and its edge from the column's x, where a stem goes.</summary>
    const double HeadWidth = 1.2 * Sp, Edge = 0.6 * Sp;
    const double StemLength = 3.4 * Sp, StaffLine = 0.45, BarLine = 0.6, Stem = 0.7, Ledger = 0.6;
    const double FontTitle = 17, FontBar = 8;
    /// <summary>Lines of the staff run through even positions from the bottom one to this.</summary>
    const int TopLine = 8, MiddleLine = 4;
    /// <summary>Where each sign of a key signature goes on the treble staff; the bass staff is two positions lower.</summary>
    static readonly int[] SharpSpots = [8, 5, 9, 6, 3, 7, 4], FlatSpots = [4, 7, 3, 6, 2, 5, 1];

    /// <summary>Helvetica's widths from space to tilde, in thousandths of the size.</summary>
    static readonly short[] Widths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    static double TextWidth(string text, double size) =>
        text.Sum(ch => ch is >= ' ' and <= '~' ? Widths[ch - ' '] : 556) * size / 1000;

    /// <summary>A PDF literal string in WinAnsi: what is outside it becomes "?".</summary>
    static string Escape(string text)
    {
        var s = new StringBuilder();
        foreach (char ch in text)
        {
            if (ch is '(' or ')' or '\\') s.Append('\\');
            s.Append(ch is >= ' ' and <= '~' or >= ' ' and <= 'ÿ' ? ch : '?');
        }
        return s.ToString();
    }

    public static byte[] Render(string title, ScoreMusic music, List<ScorePage> pages)
    {
        var columns = music.Bars.Select(ScoreSheet.Columns).ToList();
        var streams = new List<byte[]>();
        for (int i = 0; i < pages.Count; i++)
        {
            var c = new PdfCanvas();
            if (i == 0) DrawTitle(c, title);
            foreach (var line in pages[i].Lines) DrawLine(c, music, columns, line);
            streams.Add(Compress(Encoding.Latin1.GetBytes(c.ToString())));
        }
        return Assemble(title, streams);
    }

    static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal)) z.Write(data);
        return output.ToArray();
    }

    /// <summary>Objects 1 to 4 are fixed; each page adds its dictionary and its stream.</summary>
    static byte[] Assemble(string title, List<byte[]> streams)
    {
        string W(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        int count = streams.Count, objects = 4 + 2 * count;
        var offsets = new long[objects + 1];
        using var o = new MemoryStream();
        void Put(string s) => o.Write(Encoding.Latin1.GetBytes(s));
        void Start(int n)
        {
            offsets[n] = o.Position;
            Put($"{n} 0 obj\n");
        }
        Put("%PDF-1.4\n%âãÏÓ\n");
        Start(1);
        Put("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Start(2);
        string kids = string.Join(" ", Enumerable.Range(0, count).Select(i => $"{5 + 2 * i} 0 R"));
        Put($"<< /Type /Pages /Kids [{kids}] /Count {count} >>\nendobj\n");
        Start(3);
        Put("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Start(4);
        Put($"<< /Title ({Escape(title)}) /Producer (XTreme) >>\nendobj\n");
        for (int i = 0; i < count; i++)
        {
            int page = 5 + 2 * i;
            Start(page);
            Put($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W(ScoreSheet.PageWidth)} {W(ScoreSheet.PageHeight)}] /Resources << /Font << /F1 3 0 R >> >> /Contents {page + 1} 0 R >>\nendobj\n");
            Start(page + 1);
            Put($"<< /Length {streams[i].Length} /Filter /FlateDecode >>\nstream\n");
            o.Write(streams[i]);
            Put("\nendstream\nendobj\n");
        }
        long xref = o.Position;
        Put($"xref\n0 {objects + 1}\n0000000000 65535 f \n");
        for (int n = 1; n <= objects; n++) Put($"{offsets[n]:D10} 00000 n \n");
        Put($"trailer\n<< /Size {objects + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF");
        return o.ToArray();
    }

    static void DrawTitle(PdfCanvas c, string title)
    {
        double room = ScoreSheet.PageWidth - 2 * ScoreSheet.MarginX, size = FontTitle;
        double width = TextWidth(title, size);
        if (width > room)
        {
            size *= room / width;
            width = room;
        }
        c.Text((ScoreSheet.PageWidth - width) / 2, ScoreSheet.PageHeight - ScoreSheet.MarginTop - size, size, Escape(title));
    }

    static void DrawLine(PdfCanvas c, ScoreMusic music, List<ScoreColumns> columns, ScoreLine line)
    {
        double left = ScoreSheet.MarginX, height = ScoreSheet.StaffHeight;
        var xs = new double[line.Count + 1];
        xs[0] = line.X;
        for (int i = 0; i < line.Count; i++) xs[i + 1] = xs[i] + columns[line.First + i].Width * line.Stretch;
        double right = xs[line.Count];

        foreach (double y in (double[])[line.TrebleY, line.BassY])
            for (int i = 0; i < 5; i++) c.Line(left, y + i * Sp, right, y + i * Sp, StaffLine);
        c.Line(left, line.BassY, left, line.TrebleY + height, BarLine);
        ScoreGlyphs.Brace(c, left - 2, line.BassY, line.TrebleY + height);

        ScoreGlyphs.Treble(c, left + 2 * Sp, line.TrebleY + Sp);
        ScoreGlyphs.Bass(c, left + 1.6 * Sp, line.BassY + 3 * Sp);
        double x = left + 4 * Sp;
        for (int i = 0; i < Math.Abs(music.Fifths); i++, x += 0.95 * Sp)
        {
            int spot = music.Fifths > 0 ? SharpSpots[i] : FlatSpots[i];
            ScoreGlyphs.Accidental(c, Math.Sign(music.Fifths), x, line.TrebleY + spot * Sp / 2);
            ScoreGlyphs.Accidental(c, Math.Sign(music.Fifths), x, line.BassY + (spot - 2) * Sp / 2);
        }
        if (line.First == 0)
            foreach (double y in (double[])[line.TrebleY, line.BassY])
            {
                ScoreGlyphs.Four(c, x + 0.7 * Sp, y + 2 * Sp);
                ScoreGlyphs.Four(c, x + 0.7 * Sp, y);
            }
        c.Text(left, line.TrebleY + height + 0.9 * Sp, FontBar, (line.First + 1).ToString(CultureInfo.InvariantCulture));

        for (int i = 0; i < line.Count; i++)
        {
            int index = line.First + i;
            var bar = music.Bars[index];
            bool last = index == music.Bars.Count - 1;
            double end = xs[i + 1];
            if (last)
            {
                c.Line(end - 3.2, line.BassY, end - 3.2, line.TrebleY + height, BarLine);
                c.Line(end - 1, line.BassY, end - 1, line.TrebleY + height, 2);
            }
            else c.Line(end, line.BassY, end, line.TrebleY + height, BarLine);

            // where a tie out of this bar lands: the next bar's first column, or the end of the line
            double? next = i + 1 < line.Count ? xs[i + 1] + columns[index + 1].At(0) * line.Stretch : null;
            var place = new BarPlace(xs[i], end, columns[index], line.Stretch, next, line.X, index == line.First);
            DrawStaff(c, bar.Treble, place, line.TrebleY, true);
            DrawStaff(c, bar.Bass, place, line.BassY, false);
        }
    }

    /// <summary>Where a bar went on its line, and what a tie needs to reach past it.</summary>
    readonly record struct BarPlace(double Left, double Right, ScoreColumns Columns, double Stretch, double? NextHead, double LineStart, bool OpensLine)
    {
        public double X(int tick) => Left + Columns.At(tick) * Stretch;
    }

    static void DrawStaff(PdfCanvas c, List<List<ScoreEvent>> voices, BarPlace place, double y, bool treble)
    {
        for (int v = 0; v < voices.Count; v++)
            foreach (var e in voices[v])
            {
                if (e.IsRest) DrawRest(c, e, place, y, voices.Count > 1 ? (v == 0 ? 2 : -2) : 0);
                else DrawChord(c, e, place, y, treble, voices.Count > 1 ? (v == 0 ? 1 : -1) : 0);
            }
    }

    static void DrawRest(PdfCanvas c, ScoreEvent e, BarPlace place, double y, int shift)
    {
        double x = place.X(e.Start), mid = y + (MiddleLine + shift) * Sp / 2;
        switch (e.Value)
        {
            case 16:
                // a bar's rest sits in its middle, whatever the columns say
                c.Rect((place.Left + place.Right) / 2 - 0.65 * Sp, mid + 0.5 * Sp, 1.3 * Sp, 0.5 * Sp);
                break;
            case 8:
                c.Rect(x - 0.65 * Sp, mid, 1.3 * Sp, 0.5 * Sp);
                break;
            case 4:
                ScoreGlyphs.QuarterRest(c, x, mid);
                break;
            default:
                ScoreGlyphs.FlagRest(c, e.Value == 2 ? 1 : 2, x, mid);
                break;
        }
    }

    /// <summary><paramref name="voice"/>: 1 = upper, stems up; -1 = lower, stems down; 0 = alone, by where its notes sit.</summary>
    static void DrawChord(PdfCanvas c, ScoreEvent e, BarPlace place, double y, bool treble, int voice)
    {
        double x = place.X(e.Start);
        double Y(int position) => y + position * Sp / 2;
        var heads = e.Heads.Select(h => (Head: h, Pos: ScoreSheet.Position(h, treble))).OrderBy(h => h.Pos).ToList();
        int low = heads[0].Pos, high = heads[^1].Pos;
        bool up = voice != 0 ? voice > 0 : high - MiddleLine < MiddleLine - low;
        int dir = up ? 1 : -1;
        if (!up) heads.Reverse();

        // a head a second away from the one before goes to the other side of the stem
        var side = new bool[heads.Count];
        for (int i = 1; i < heads.Count; i++) side[i] = Math.Abs(heads[i].Pos - heads[i - 1].Pos) == 1 && !side[i - 1];
        bool anySide = side.Contains(true);

        var ledgers = new HashSet<(int, bool)>();
        for (int i = 0; i < heads.Count; i++)
        {
            int pos = heads[i].Pos;
            for (int l = -2; l >= pos; l -= 2) ledgers.Add((l, side[i]));
            for (int l = TopLine + 2; l <= pos; l += 2) ledgers.Add((l, side[i]));
        }
        foreach (var (l, off) in ledgers)
        {
            double at = x + (off ? dir * HeadWidth : 0);
            c.Line(at - Edge - 0.35 * Sp, Y(l), at + Edge + 0.35 * Sp, Y(l), Ledger);
        }
        for (int i = 0; i < heads.Count; i++)
            ScoreGlyphs.Head(c, e.Shape, x + (side[i] ? dir * HeadWidth : 0), Y(heads[i].Pos));

        if (e.Shape < 16)
        {
            double stemX = x + dir * (Edge - Stem / 2);
            int flags = e.Shape == 2 ? 1 : e.Shape == 1 ? 2 : 0;
            double length = StemLength + flags * 0.45 * Sp;
            double tip = up ? Math.Max(Y(high) + length, Y(MiddleLine)) : Math.Min(Y(low) - length, Y(MiddleLine));
            c.Line(stemX, up ? Y(low) : Y(high), stemX, tip, Stem);
            if (flags > 0) ScoreGlyphs.Flags(c, flags, stemX, tip, dir);
        }

        double right = x + Edge + (up && anySide ? HeadWidth : 0);
        if (e.Dotted)
            foreach (int spot in heads.Select(h => h.Pos % 2 == 0 ? h.Pos + 1 : h.Pos).Distinct())
            {
                c.Ellipse(right + 0.5 * Sp, Y(spot), 0.19 * Sp, 0.19 * Sp);
                c.Fill();
            }
        DrawAccidentals(c, heads.Select(h => h.Head).Zip(heads.Select(h => h.Pos)), x - Edge - (!up && anySide ? HeadWidth : 0), Y);
        DrawTies(c, e, place, x, right + (e.Dotted ? 0.8 * Sp : 0), heads.Select(h => (h.Head, h.Pos)), Y, voice != 0 ? voice : dir);
    }

    /// <summary>Signs close in height step left, so none sits on another.</summary>
    static void DrawAccidentals(PdfCanvas c, IEnumerable<(ScoreHead Head, int Pos)> heads, double left, Func<int, double> y)
    {
        const int Clear = 6; // positions two signs need between them to share a column
        var placed = new List<(int Pos, int Column)>();
        foreach (var (head, pos) in heads.Where(h => h.Head.Accidental != null).OrderByDescending(h => h.Pos))
        {
            int column = 0;
            while (placed.Exists(p => p.Column == column && Math.Abs(p.Pos - pos) < Clear)) column++;
            placed.Add((pos, column));
            ScoreGlyphs.Accidental(c, head.Accidental!.Value, left - (0.75 + column * 1.1) * Sp, y(pos));
        }
    }

    /// <summary>The curves leaving each head, and the one arriving at the start of a line from the line before.</summary>
    static void DrawTies(PdfCanvas c, ScoreEvent e, BarPlace place, double x, double from, IEnumerable<(ScoreHead Head, int Pos)> heads, Func<int, double> y, int dir)
    {
        double? arrive = e.Start + e.Value < ScoreSheet.StepsPerBar ? place.X(e.Start + e.Value) : place.NextHead;
        foreach (var (head, pos) in heads)
        {
            double at = y(pos) - dir * 0.75 * Sp;
            if (head.TieOut) Tie(c, from, (arrive ?? place.Right + Edge + 0.2 * Sp) - Edge - 0.2 * Sp, at, -dir);
            if (head.TieIn && e.Start == 0 && place.OpensLine) Tie(c, place.LineStart - 0.8 * Sp, x - Edge - 0.2 * Sp, at, -dir);
        }
    }

    static void Tie(PdfCanvas c, double x1, double x2, double y, int dir)
    {
        double d = x2 - x1, rise = Math.Min(1.1 * Sp, 0.6 * Sp + d / 40), thick = 0.28 * Sp;
        c.M(x1, y).C(x1 + d / 4, y + dir * rise, x2 - d / 4, y + dir * rise, x2, y)
            .C(x2 - d / 4, y + dir * (rise + thick), x1 + d / 4, y + dir * (rise + thick), x1, y);
        c.Fill();
    }
}
