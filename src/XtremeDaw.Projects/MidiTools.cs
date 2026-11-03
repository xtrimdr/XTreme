using System.Text.RegularExpressions;
using XtremeDaw.Audio.Midi;

namespace XtremeDaw.Projects;

/// <summary>How a tool's setting is shown: a value with its unit, a list of choices, a switch, a key, a dice or free text.</summary>
public enum ToolParamKind { Percent, Signed, Ms, Count, Choice, Toggle, Key, Seed, Text, Color }

/// <summary>A setting of a tool, always a whole number (a choice is its index), so one panel and one command line serve them all.</summary>
public sealed record ToolParam(string Id, ToolParamKind Kind, int Min, int Max, int Default, string[]? Choices = null)
{
    public static ToolParam Pick(string id, int fallback, params string[] choices) => new(id, ToolParamKind.Choice, 0, choices.Length - 1, fallback, choices);
    public static ToolParam Switch(string id, bool on = false) => new(id, ToolParamKind.Toggle, 0, 1, on ? 1 : 0);
    public static ToolParam Dice => new("seed", ToolParamKind.Seed, 0, int.MaxValue, 1);
    public static ToolParam Words(string id) => new(id, ToolParamKind.Text, 0, 0, 0);
}

/// <summary>
/// One of the piano roll's tools: its id (texts <c>tool.&lt;id&gt;</c>), its group in the menu, its settings and what it does.
/// A tool with no settings runs at once; the others open the tools panel, where the result is heard and seen as it changes.
/// </summary>
public sealed record MidiTool(string Id, string Group, ToolParam[] Params, Action<ToolRun> Run)
{
    public bool Instant => Params.Length == 0;
}

/// <summary>
/// One run of a tool: the notes of the clip (a working copy, changed in place), the ones it works on (the selected ones or
/// all), its settings and what it may need to know about the song. <see cref="Result"/> is what stays selected.
/// </summary>
public sealed class ToolRun
{
    public required List<MidiNote> Notes { get; init; }
    public required HashSet<MidiNote> Targets { get; init; }
    public Dictionary<string, int> Values { get; init; } = [];
    public Dictionary<string, string> Words { get; init; } = [];
    public double ClipBeats { get; init; } = MidiClip.DefaultBeats;
    /// <summary>Where the clip starts on the song's grid, in beats.</summary>
    public double OriginBeats { get; init; }
    /// <summary>The magnet's step in beats (0 = free).</summary>
    public double SnapBeats { get; init; }
    public double Bpm { get; init; } = 120;
    /// <summary>The playhead inside the clip, in beats.</summary>
    public double PlayBeat { get; init; }
    public MusicScale? Scale { get; init; }
    /// <summary>The notes copied last: a groove to follow.</summary>
    public IReadOnlyList<MidiNote> Copied { get; init; } = [];
    public HashSet<MidiNote> Result { get; } = [];

    public int V(string id) => Values.GetValueOrDefault(id);
    public bool On(string id) => V(id) != 0;
    public double Fraction(string id) => V(id) / 100.0;
    /// <summary>Milliseconds at the song's tempo, in beats.</summary>
    public double Ms(string id) => V(id) / 1000.0 * Bpm / 60;
    /// <summary>A rate setting in beats; "snap" is the magnet, a 1/16 when it is free.</summary>
    public double Rate(string id, ToolParam p) => RateOf(p.Choices![Math.Clamp(V(id), 0, p.Choices.Length - 1)]);
    public double RateOf(string choice) => choice == "snap" ? Math.Max(MidiTools.MinStep, SnapBeats > 0 ? SnapBeats : 0.25) : MidiTools.Beats(choice);
    public MusicScale ScaleOrMajor => Scale ?? new MusicScale(0, MusicScale.Kinds[0]);
    /// <summary>What the generators fill: the span of the selected notes, or the whole clip.</summary>
    public (double From, double To) Span
    {
        get
        {
            if (Targets.Count == 0 || Targets.Count == Notes.Count) return (0, ClipBeats);
            return (Targets.Min(n => n.Beat), Math.Min(ClipBeats, Targets.Max(n => n.EndBeat)));
        }
    }

    public void Add(MidiNote n)
    {
        Notes.Add(n);
        Result.Add(n);
    }

    public void Remove(MidiNote n)
    {
        Notes.Remove(n);
        Result.Remove(n);
    }

    public void RemoveTargets()
    {
        Notes.RemoveAll(Targets.Contains);
        Result.ExceptWith(Targets);
    }
}

/// <summary>A note's editable level on the lane under the piano roll, as 1..127 whatever it is stored as.</summary>
public enum NoteLane { Velocity, Pan, Release, ModX, ModY }

/// <summary>Reads and writes a note's <see cref="NoteLane"/>. A level never set reads as the middle, or 100 on velocity.</summary>
public static class NoteLanes
{
    public const int Center = 64;

    public static int Get(MidiNote n, NoteLane lane) => lane switch
    {
        NoteLane.Velocity => n.Velocity,
        NoteLane.Pan => Center + (int)Math.Round(n.Pan * 0.63),
        NoteLane.Release => n.Release > 0 ? n.Release : Center,
        NoteLane.ModX => n.ModX > 0 ? n.ModX : Center,
        _ => n.ModY > 0 ? n.ModY : Center,
    };

    public static void Set(MidiNote n, NoteLane lane, int value)
    {
        value = Math.Clamp(value, 1, 127);
        switch (lane)
        {
            case NoteLane.Velocity: n.Velocity = value; break;
            case NoteLane.Pan: n.Pan = Math.Clamp((int)Math.Round((value - Center) / 0.63), -100, 100); break;
            case NoteLane.Release: n.Release = value; break;
            case NoteLane.ModX: n.ModX = value; break;
            default: n.ModY = value; break;
        }
    }

    /// <summary>The right button on the lane: back to no value of its own.</summary>
    public static void Reset(MidiNote n, NoteLane lane)
    {
        switch (lane)
        {
            case NoteLane.Velocity: n.Velocity = MidiNote.DefaultVelocity; break;
            case NoteLane.Pan: n.Pan = 0; break;
            case NoteLane.Release: n.Release = 0; break;
            case NoteLane.ModX: n.ModX = 0; break;
            default: n.ModY = 0; break;
        }
    }
}

/// <summary>
/// The piano roll's tools (asked by the user: everything FL Studio's Tools menu has, comfortable to use). One table
/// (<see cref="All"/>) gives the menu, the panel with its live result, the keys and xtlab, so a new tool is one row and one
/// function. A tool only changes the note list it gets, in beats; the window previews on a copy and commits the end result as one
/// undo step. Everything random comes from the run's seed: the same dice give the same notes.
/// </summary>
public static class MidiTools
{
    public const string Write = "write", Timing = "timing", Level = "level", Edit = "edit", NoteCard = "note";
    /// <summary>The shortest step a tool writes: a 1/64.</summary>
    public const double MinStep = MidiNote.MinBeats;
    /// <summary>Notes starting closer than this (a 1/64 triplet) are one chord.</summary>
    const double Together = 0.04;
    /// <summary>Notes this close count as touching (for gluing).</summary>
    const double Touching = 0.02;

    static readonly string[] Rates = ["snap", "1/2", "1/4", "1/8", "1/8T", "1/16", "1/16T", "1/32"];
    static readonly string[] LongRates = ["1/4", "1/2", "1 bar", "2 bars", "4 bars"];
    static readonly string[] Lanes = ["velocity", "pan", "release", "modX", "modY"];

    static readonly ToolParam ArpRate = ToolParam.Pick("rate", 5, Rates), ChopRate = ToolParam.Pick("rate", 0, Rates),
        QuantRate = ToolParam.Pick("grid", 0, Rates), RandomRate = ToolParam.Pick("rate", 5, Rates), LfoRate = ToolParam.Pick("speed", 2, LongRates),
        ClawPeriod = ToolParam.Pick("period", 2, LongRates), ProgressionLength = ToolParam.Pick("length", 2, "1/2", "1 bar", "2 bars", "4 bars");

    static readonly double[][] ChopPatterns = [[1], [3, 1], [2, 1, 1], [1, 1, 2], [3, 3, 2], [1, -1], [-1, 1]];

    public static readonly MidiTool[] All =
    [
        new("arpeggiate", Write,
        [
            ToolParam.Pick("pattern", 0, "up", "down", "updown", "downup", "played", "random", "inward", "outward"), ArpRate,
            new("octaves", ToolParamKind.Count, 1, 4, 1), ToolParam.Pick("range", 0, "normal", "flip", "alternate"),
            new("gate", ToolParamKind.Percent, 10, 100, 90), new("swing", ToolParamKind.Percent, 0, 75, 0),
            ToolParam.Pick("sync", 0, "chord", "time"), ToolParam.Dice,
        ], Arpeggiate),
        new("strum", Write,
        [
            new("time", ToolParamKind.Ms, 0, 120, 25), new("tension", ToolParamKind.Signed, -100, 100, 0),
            new("velocity", ToolParamKind.Signed, -30, 30, 0), ToolParam.Pick("direction", 0, "low", "high", "alternate"),
            ToolParam.Switch("keepEnd", true), ToolParam.Switch("ahead"), ToolParam.Switch("chopChords"),
        ], Strum),
        new("legato", Write, [], r => Articulate(r, fill: true, 1)),
        new("articulate", Write, [ToolParam.Pick("mode", 0, "fill", "scale"), new("length", ToolParamKind.Percent, 5, 200, 100)],
            r => Articulate(r, r.V("mode") == 0, r.Fraction("length"))),
        new("chop-quick", Write, [], r => Chop(r, r.RateOf("snap"), [1], 1, false)),
        new("chop", Write,
        [
            ChopRate, ToolParam.Pick("pattern", 0, "even", "dotted", "gallop", "reverse", "tresillo", "skip", "offbeat"),
            new("gate", ToolParamKind.Percent, 10, 100, 100), ToolParam.Switch("absolute", true),
        ], r => Chop(r, r.Rate("rate", ChopRate), ChopPatterns[r.V("pattern")], r.Fraction("gate"), r.On("absolute"))),
        new("glue", Write, [], Glue),
        new("flam", Write,
        [
            new("time", ToolParamKind.Ms, 5, 100, 30), new("count", ToolParamKind.Count, 1, 4, 1), new("velocity", ToolParamKind.Percent, 10, 100, 60),
            ToolParam.Switch("before", true),
        ], Flam),
        new("claw", Write,
        [
            ClawPeriod, new("trash", ToolParamKind.Count, 0, 8, 0), new("distort", ToolParamKind.Signed, -100, 100, 0),
            ToolParam.Switch("removeShort", true), ToolParam.Switch("stretch"),
        ], Claw),
        new("limit", Write,
        [
            new("low", ToolParamKind.Key, 0, 127, 36), new("high", ToolParamKind.Key, 0, 127, 84), new("transpose", ToolParamKind.Signed, -24, 24, 0),
            ToolParam.Pick("wrap", 0, "octave", "bottom"), ToolParam.Switch("fit"),
        ], Limit),
        new("flip", Write, [ToolParam.Switch("across", true), ToolParam.Switch("keepStarts"), ToolParam.Switch("upDown")], Flip),
        new("random", Write,
        [
            new("count", ToolParamKind.Count, 1, 128, 16), RandomRate, new("variation", ToolParamKind.Signed, -100, 100, 0),
            new("octave", ToolParamKind.Count, 0, 8, 4), new("span", ToolParamKind.Count, 1, 4, 1), new("stack", ToolParamKind.Count, 1, 6, 1),
            ToolParam.Switch("merge", true), ToolParam.Switch("replace", true), ToolParam.Dice,
        ], RandomNotes),
        new("riff", Write,
        [
            ToolParam.Pick("style", 0, "bass", "arp", "lead", "chords"), new("density", ToolParamKind.Percent, 10, 100, 55), new("rests", ToolParamKind.Percent, 0, 80, 15),
            new("octave", ToolParamKind.Count, 1, 7, 3), new("span", ToolParamKind.Count, 1, 3, 1), ToolParam.Switch("replace", true), ToolParam.Dice,
        ], Riff),
        new("progression", Write,
        [
            ToolParam.Pick("preset", 0, ChordProgressions.PresetIds), ToolParam.Words("chords"), ProgressionLength,
            new("octave", ToolParamKind.Count, 2, 6, 4), ToolParam.Pick("voicing", 0, "block", "open", "octave", "stacked"),
            ToolParam.Pick("rhythm", 0, "hold", "stabs", "pulse", "arp", "pop"), new("adventure", ToolParamKind.Percent, 0, 100, 30),
            ToolParam.Switch("bass", true), ToolParam.Switch("replace", true), ToolParam.Dice,
        ], ChordProgressions.Run),

        new("quantize", Timing,
        [
            QuantRate, new("strength", ToolParamKind.Percent, 0, 100, 100), new("sensitivity", ToolParamKind.Percent, 1, 100, 100),
            new("swing", ToolParamKind.Percent, 0, 75, 0), ToolParam.Pick("ends", 0, "leaveLength", "leaveEnd", "gridLength", "gridEnd"),
        ], Quantize),
        new("quantize-starts", Timing, [], QuantizeStarts),
        new("groove", Timing,
        [
            ToolParam.Pick("source", 0, "copied", "swing8", "swing16", "shuffle", "laidBack", "pushed"), new("amount", ToolParamKind.Percent, 0, 100, 100),
            new("velocity", ToolParamKind.Percent, 0, 100, 0),
        ], Groove),
        new("humanize", Timing,
        [
            new("time", ToolParamKind.Ms, 0, 40, 8), new("velocity", ToolParamKind.Count, 0, 40, 8), new("length", ToolParamKind.Percent, 0, 40, 0),
            ToolParam.Dice,
        ], Humanize),

        new("levels", Level,
        [
            ToolParam.Pick("lane", 0, Lanes), new("multiply", ToolParamKind.Percent, 0, 200, 100), new("offset", ToolParamKind.Signed, -100, 100, 0),
            new("center", ToolParamKind.Signed, -100, 100, 0), new("tension", ToolParamKind.Signed, -100, 100, 0), ToolParam.Switch("invert"),
        ], Levels),
        new("lfo", Level,
        [
            ToolParam.Pick("lane", 0, Lanes), ToolParam.Pick("shape", 0, "sine", "triangle", "square", "saw"), LfoRate,
            new("depth", ToolParamKind.Percent, 0, 100, 40), new("middle", ToolParamKind.Percent, 0, 100, 60), new("phase", ToolParamKind.Count, 0, 359, 0),
        ], Lfo),

        new("rotate", Edit, [new("steps", ToolParamKind.Signed, -32, 32, 1)], Rotate),
        new("insert-space", Edit, [new("steps", ToolParamKind.Count, 1, 64, 16)], r => Space(r, insert: true)),
        new("delete-space", Edit, [new("steps", ToolParamKind.Count, 1, 64, 16)], r => Space(r, insert: false)),
        new("trim", Edit, [], Trim),
        new("discard-lengths", Edit, [], DiscardLengths),

        new("note", NoteCard,
        [
            new("velocity", ToolParamKind.Count, 1, 127, MidiNote.DefaultVelocity), new("pan", ToolParamKind.Signed, -100, 100, 0), new("release", ToolParamKind.Count, 0, 127, 0),
            new("modX", ToolParamKind.Count, 0, 127, 0), new("modY", ToolParamKind.Count, 0, 127, 0), new("color", ToolParamKind.Color, 0, MidiNote.Colors, 0),
            ToolParam.Switch("slide"), ToolParam.Switch("porta"), ToolParam.Switch("muted"),
        ], NoteProperties),
    ];

    /// <summary>
    /// A note's card: only what was moved reaches the notes, so a chord with different velocities keeps them when you
    /// slide its pan. <c>was.x</c> is what setting x showed when the card opened.
    /// </summary>
    static void NoteProperties(ToolRun r)
    {
        bool Moved(string id) => r.Values.TryGetValue("was." + id, out int was) ? was != r.V(id) : r.V(id) != 0;
        foreach (var n in r.Targets)
        {
            if (Moved("velocity")) n.Velocity = r.V("velocity");
            if (Moved("pan")) n.Pan = r.V("pan");
            if (Moved("release")) n.Release = r.V("release");
            if (Moved("modX")) n.ModX = r.V("modX");
            if (Moved("modY")) n.ModY = r.V("modY");
            if (Moved("color")) n.Color = r.V("color");
            if (Moved("slide")) n.Slide = r.On("slide");
            if (Moved("porta")) n.Porta = r.On("porta");
            if (Moved("muted")) n.Muted = r.On("muted");
        }
    }

    public static MidiTool? Find(string id) => Array.Find(All, t => t.Id == id);

    /// <summary>A tool's settings as they start.</summary>
    public static Dictionary<string, int> Defaults(MidiTool tool) => tool.Params.ToDictionary(p => p.Id, p => p.Default);

    /// <summary>Beats of "1/8", "1/8T", "1 bar"…: the rates in the settings.</summary>
    public static double Beats(string rate)
    {
        if (rate.EndsWith("bar") || rate.EndsWith("bars")) return 4 * int.Parse(rate.Split(' ')[0]);
        bool triplet = rate.EndsWith('T');
        double beats = 4.0 / int.Parse(rate.TrimEnd('T').Split('/')[1]);
        return triplet ? beats * 2 / 3 : beats;
    }

    /// <summary>
    /// Runs a tool and leaves the list in order and in range: nothing starting past the clip's end, no note outside MIDI.
    /// What it returns is what should be selected.
    /// </summary>
    public static HashSet<MidiNote> Apply(MidiTool tool, ToolRun run)
    {
        foreach (var p in tool.Params) run.Values.TryAdd(p.Id, p.Default);
        run.Result.UnionWith(run.Targets);
        tool.Run(run);
        run.Notes.RemoveAll(n => n.Beat >= run.ClipBeats - 1e-9);
        foreach (var n in run.Notes)
        {
            n.Pitch = Math.Clamp(n.Pitch, MidiNote.MinPitch, MidiNote.MaxPitch);
            n.Beat = Math.Max(0, n.Beat);
            n.Beats = Math.Clamp(n.Beats, MinStep, MidiClip.MaxBeats);
            n.Velocity = Math.Clamp(n.Velocity, 1, 127);
        }
        run.Notes.Sort(MidiNote.Order);
        run.Result.IntersectWith(run.Notes);
        return run.Result;
    }

    /// <summary>The notes that start together, each stack from low to high, stacks in order.</summary>
    static List<List<MidiNote>> Stacks(IEnumerable<MidiNote> notes)
    {
        var sorted = notes.OrderBy(n => n.Beat).ThenBy(n => n.Pitch).ToList();
        var stacks = new List<List<MidiNote>>();
        foreach (var n in sorted)
            if (stacks.Count > 0 && n.Beat - stacks[^1][0].Beat <= Together) stacks[^1].Add(n);
            else stacks.Add([n]);
        foreach (var s in stacks) s.Sort((a, b) => a.Pitch.CompareTo(b.Pitch));
        return stacks;
    }

    /// <summary>0..1 bent toward an end, -100..100: 0 leaves it straight.</summary>
    static double Bend(double t, int tension) => Math.Pow(Math.Clamp(t, 0, 1), Math.Pow(2, tension / 50.0));

    /// <summary>One go through the pattern's order, as indexes into <paramref name="n"/> pitches.</summary>
    static int[] ArpOrder(string pattern, int n, Random random)
    {
        int[] up = [.. Enumerable.Range(0, n)];
        return pattern switch
        {
            "down" => [.. up.Reverse()],
            "updown" => n < 2 ? up : [.. up, .. up[1..^1].Reverse()],
            "downup" => n < 2 ? up : [.. up.Reverse(), .. up[1..^1]],
            "random" => [.. up.OrderBy(_ => random.Next())],
            "inward" => [.. up.Select(i => i % 2 == 0 ? i / 2 : n - 1 - i / 2)],
            "outward" => [.. up.Select(i => i % 2 == 0 ? i / 2 : n - 1 - i / 2).Reverse()],
            _ => up,
        };
    }

    static void Arpeggiate(ToolRun r)
    {
        double step = r.Rate("rate", ArpRate), swing = r.Fraction("swing") * step / 2, gate = r.Fraction("gate");
        string pattern = All[0].Params[0].Choices![r.V("pattern")];
        var random = new Random(r.V("seed"));
        int octaves = r.V("octaves"), range = r.V("range"), lap = 0;
        var stacks = Stacks(r.Targets);
        r.RemoveTargets();
        foreach (var stack in stacks)
        {
            double start = stack[0].Beat, end = stack.Max(n => n.EndBeat);
            var pitches = stack.Select(n => n.Pitch).ToList();
            if (pattern == "played") pitches = [.. stack.OrderBy(n => n.Beat).Select(n => n.Pitch)];
            bool free = r.V("sync") == 1;
            int done = free ? (int)Math.Round((start - stacks[0][0].Beat) / step) : 0;
            for (int i = 0; start + i * step < end - 1e-9; i++, done++)
            {
                int[] order = ArpOrder(pattern, pitches.Count, random);
                int span = order.Length * octaves, at = done % span;
                int octave = at / order.Length;
                int rangeLap = done / span;
                bool flipped = range == 1 || range == 2 && (rangeLap + lap) % 2 == 1;
                if (flipped) octave = octaves - 1 - octave;
                double beat = start + i * step + (i % 2 == 1 ? swing : 0);
                double length = Math.Max(MinStep, Math.Min(step * gate, end - beat));
                var model = stack[order[at % order.Length] % stack.Count].Copy();
                model.Pitch = pitches[order[at % order.Length]] + 12 * octave;
                model.Beat = beat;
                model.Beats = length;
                r.Add(model);
            }
            lap++;
        }
    }

    static void Strum(ToolRun r)
    {
        int mode = r.V("direction"), tension = r.V("tension"), velocity = r.V("velocity");
        double each = r.Ms("time");
        var stacks = Stacks(r.Targets);
        for (int s = 0; s < stacks.Count; s++)
        {
            var stack = stacks[s];
            bool highFirst = mode == 1 || mode == 2 && s % 2 == 1;
            var order = highFirst ? Enumerable.Reverse(stack).ToList() : stack;
            int n = order.Count;
            double total = each * (n - 1), start = order[0].Beat, ahead = r.On("ahead") ? total / 2 : 0;
            for (int i = 0; i < n; i++)
            {
                var note = order[i];
                double end = note.EndBeat;
                double at = Math.Max(0, start + (n > 1 ? total * Bend(i / (double)(n - 1), tension) : 0) - ahead);
                note.Beat = at;
                if (r.On("keepEnd")) note.Beats = Math.Max(MinStep, end - at);
                note.Velocity = Math.Clamp(note.Velocity + velocity * i, 1, 127);
            }
            if (!r.On("chopChords") || s + 1 >= stacks.Count) continue;
            double next = stacks[s + 1].Min(n => n.Beat);
            foreach (var note in stack) note.Beats = Math.Max(MinStep, Math.Min(note.Beats, next - note.Beat));
        }
    }

    static void Articulate(ToolRun r, bool fill, double amount)
    {
        var starts = r.Targets.Select(n => n.Beat).Distinct().Order().ToArray();
        foreach (var n in r.Targets)
        {
            if (!fill)
            {
                n.Beats = Math.Max(MinStep, n.Beats * amount);
                continue;
            }
            int next = Array.FindIndex(starts, b => b > n.Beat + Together);
            double room = next >= 0 ? starts[next] - n.Beat : Math.Max(n.Beats, r.Span.To - n.Beat);
            n.Beats = Math.Max(MinStep, room * amount);
        }
    }

    /// <summary>The chopper's rhythms, in steps; a negative one is a rest.</summary>
    static void Chop(ToolRun r, double step, double[] pattern, double gate, bool absolute)
    {
        double cycle = pattern.Sum(Math.Abs) * step;
        foreach (var n in r.Targets.ToList())
        {
            double beat = absolute ? Math.Floor((n.Beat + r.OriginBeats) / cycle + 1e-9) * cycle - r.OriginBeats : n.Beat;
            bool any = false;
            var pieces = new List<MidiNote>();
            while (beat < n.EndBeat - 1e-9)
                foreach (double part in pattern)
                {
                    double length = Math.Abs(part) * step, from = Math.Max(beat, n.Beat), to = Math.Min(beat + length, n.EndBeat);
                    if (part > 0 && to - from > 1e-9)
                    {
                        var piece = n.Copy();
                        piece.Beat = from;
                        piece.Beats = Math.Max(MinStep, (to - from) * gate);
                        piece.Slide = n.Slide && !any;
                        pieces.Add(piece);
                        any = true;
                    }
                    beat += length;
                }
            r.Remove(n);
            foreach (var piece in pieces) r.Add(piece);
        }
    }

    static void Glue(ToolRun r)
    {
        foreach (var row in r.Targets.GroupBy(n => n.Pitch))
        {
            MidiNote? held = null;
            foreach (var n in row.OrderBy(n => n.Beat))
            {
                if (held != null && n.Beat - held.EndBeat <= Touching)
                {
                    held.Beats = Math.Max(held.EndBeat, n.EndBeat) - held.Beat;
                    r.Remove(n);
                }
                else held = n;
            }
        }
    }

    static void Flam(ToolRun r)
    {
        double gap = r.Ms("time");
        foreach (var n in r.Targets.ToList())
        {
            int count = r.V("count");
            for (int i = 1; i <= count; i++)
            {
                var hit = n.Copy();
                hit.Velocity = (int)Math.Max(1, n.Velocity * Math.Pow(r.Fraction("velocity"), count + 1 - i));
                hit.Beat = r.On("before") ? n.Beat - gap * (count + 1 - i) : n.Beat + gap * (i - 1);
                hit.Beats = Math.Max(MinStep, gap * 0.9);
                if (hit.Beat >= 0) r.Add(hit);
            }
            if (!r.On("before")) n.Beat += gap * count;
        }
    }

    static void Claw(ToolRun r)
    {
        double period = r.Rate("period", ClawPeriod), slice = period / 8;
        int trash = r.V("trash"), distort = r.V("distort");
        var (from, to) = r.Span;
        double Warp(double beat)
        {
            double at = beat - from, lap = Math.Floor(at / period);
            return from + (lap + Bend((at - lap * period) / period, distort)) * period;
        }
        var kept = new List<MidiNote>();
        foreach (var n in r.Targets.ToList())
        {
            r.Remove(n);
            double b = n.Beat;
            while (b < n.EndBeat - 1e-9)
            {
                double cell = from + (Math.Floor((b - from) / slice + 1e-9) + 1) * slice;
                double end = Math.Min(cell, n.EndBeat);
                int index = (int)Math.Floor((b - from) / slice + 1e-9);
                if (trash == 0 || index % trash != trash - 1)
                {
                    var piece = n.Copy();
                    piece.Beat = Warp(b);
                    piece.Beats = Math.Max(0, Warp(end) - piece.Beat);
                    if (!r.On("removeShort") || piece.Beats >= MinStep) kept.Add(piece);
                }
                b = end;
            }
        }
        double reach = kept.Count == 0 ? to : kept.Max(n => n.EndBeat);
        double scale = r.On("stretch") && reach > from ? (to - from) / (reach - from) : 1;
        foreach (var n in kept)
        {
            n.Beat = from + (n.Beat - from) * scale;
            n.Beats *= scale;
            r.Add(n);
        }
    }

    static void Limit(ToolRun r)
    {
        int low = Math.Min(r.V("low"), r.V("high")), high = Math.Max(r.V("low"), r.V("high"));
        int octaves = Math.Max(1, (high - low + 1) / 12);
        foreach (var n in r.Targets)
        {
            int p = n.Pitch + r.V("transpose");
            if (r.V("wrap") == 1)
            {
                // everything out of the window folds to its lowest octave
                if (p < low || p > high) p = low + ((p - low) % 12 + 12) % 12;
            }
            while (p > high && p - 12 * octaves >= low) p -= 12;
            while (p > high) p -= 12;
            while (p < low) p += 12;
            if (r.On("fit") && r.Scale != null) p = r.Scale.Nearest(p);
            n.Pitch = p;
        }
    }

    static void Flip(ToolRun r)
    {
        if (r.Targets.Count == 0) return;
        double start = r.Targets.Min(n => n.Beat), last = r.Targets.Max(n => n.Beat), end = r.Targets.Max(n => n.EndBeat);
        int low = r.Targets.Min(n => n.Pitch), high = r.Targets.Max(n => n.Pitch);
        foreach (var n in r.Targets)
        {
            if (r.On("upDown")) n.Pitch = low + high - n.Pitch;
            // a perfect mirror turns ends into starts; keeping the starts mirrors where each one attacks
            if (r.On("across")) n.Beat = r.On("keepStarts") ? start + last - n.Beat : start + end - n.EndBeat;
        }
    }

    static void RandomNotes(ToolRun r)
    {
        var random = new Random(r.V("seed"));
        var (from, to) = r.Span;
        if (r.On("replace")) r.RemoveTargets();
        var scale = r.ScaleOrMajor;
        double step = r.Rate("rate", RandomRate);
        int bottom = (r.V("octave") + 1) * 12 + scale.Root, top = bottom + 12 * r.V("span");
        int low = scale.Degree(bottom), high = Math.Max(low + 1, scale.Degree(top));
        int cells = Math.Max(1, (int)Math.Floor((to - from) / step));
        for (int i = 0; i < r.V("count"); i++)
        {
            double beat = from + random.Next(cells) * step;
            double variation = r.Fraction("variation") * random.NextDouble();
            double length = Math.Max(MinStep, step * (1 + (variation >= 0 ? variation * 3 : variation)));
            for (int k = 0; k < r.V("stack"); k++)
                r.Add(new MidiNote { Pitch = scale.PitchOf(random.Next(low, high)), Beat = beat, Beats = length, Velocity = random.Next(70, 120) });
        }
        if (r.On("merge")) MergeSame(r);
    }

    /// <summary>A note on a row where the last one just ended, or still sounds, joins it.</summary>
    static void MergeSame(ToolRun r)
    {
        foreach (var row in r.Result.ToList().GroupBy(n => n.Pitch))
        {
            MidiNote? held = null;
            foreach (var n in row.OrderBy(n => n.Beat))
                if (held != null && n.Beat <= held.EndBeat + Touching)
                {
                    held.Beats = Math.Max(held.EndBeat, n.EndBeat) - held.Beat;
                    r.Remove(n);
                }
                else held = n;
        }
    }

    /// <summary>Hits spread evenly over <paramref name="steps"/>, the rhythm under most popular music.</summary>
    static bool[] Euclid(int hits, int steps)
    {
        var line = new bool[steps];
        for (int i = 0; i < steps; i++) line[i] = i * hits % steps < hits;
        return line;
    }

    static void Riff(ToolRun r)
    {
        var random = new Random(r.V("seed"));
        var (from, to) = r.Span;
        if (r.On("replace")) r.RemoveTargets();
        var scale = r.ScaleOrMajor;
        int style = r.V("style"), root = scale.Degree((r.V("octave") + 1) * 12 + scale.Root), reach = scale.Count * r.V("span");
        double step = style == 3 ? 0.5 : 0.25;
        int perBar = (int)Math.Round(4 / step), hits = Math.Max(1, (int)Math.Round(perBar * r.Fraction("density")));
        bool[] groove = Euclid(hits, perBar);
        // the fourth bar answers the other three with a turn of its own
        bool[] answer = Euclid(Math.Min(perBar, hits + 2), perBar);
        int degree = root;
        for (int cell = 0; from + cell * step < to - 1e-9; cell++)
        {
            int bar = cell / perBar, at = cell % perBar;
            bool hit = (bar % 4 == 3 ? answer : groove)[at];
            if (!hit || at != 0 && random.NextDouble() < r.Fraction("rests")) continue;
            // what comes next stays near: small steps, pulled home at the top of each bar
            if (at == 0 && style != 3) degree = root + (bar % 2 == 1 && style != 0 ? 4 : 0);
            else degree += random.Next(0, 5) switch { 0 => 2, 1 => 1, 2 => -1, 3 => -2, _ => 0 };
            degree = Math.Clamp(degree, root, root + reach);
            double beat = from + cell * step;
            int velocity = at % (perBar / 4) == 0 ? 112 : 86;
            int steps = 1;
            while (at + steps < perBar && !groove[at + steps] && steps < 3) steps++;
            double length = step * (style == 0 ? steps * 0.9 : style == 2 ? steps : 0.8);
            if (style == 3)
                foreach (int third in (int[])[0, 2, 4]) r.Add(new MidiNote { Pitch = scale.PitchOf(degree + third), Beat = beat, Beats = length, Velocity = velocity });
            else r.Add(new MidiNote { Pitch = scale.PitchOf(degree), Beat = beat, Beats = length, Velocity = velocity });
        }
    }

    static void Quantize(ToolRun r)
    {
        double grid = r.Rate("grid", QuantRate), swing = r.Fraction("swing") * grid, strength = r.Fraction("strength"), reach = grid / 2 * r.Fraction("sensitivity");
        int ends = r.V("ends");
        foreach (var n in r.Targets)
        {
            double song = n.Beat + r.OriginBeats;
            double index = Math.Round(song / grid), target = index * grid + (index % 2 != 0 ? swing : 0);
            if (Math.Abs(target - song) > reach + 1e-9) continue;
            double end = n.EndBeat;
            n.Beat = Math.Max(0, n.Beat + (target - song) * strength);
            switch (ends)
            {
                case 1: n.Beats = Math.Max(MinStep, end - n.Beat); break;
                case 2: n.Beats = Math.Max(grid, n.Beats + (Math.Round(n.Beats / grid) * grid - n.Beats) * strength); break;
                case 3:
                    double songEnd = end + r.OriginBeats, snapped = Math.Round(songEnd / grid) * grid;
                    n.Beats = Math.Max(MinStep, end + (snapped - songEnd) * strength - n.Beat);
                    break;
            }
        }
    }

    static void QuantizeStarts(ToolRun r)
    {
        double grid = r.RateOf("snap");
        foreach (var n in r.Targets)
        {
            double end = n.EndBeat, song = n.Beat + r.OriginBeats;
            n.Beat = Math.Max(0, Math.Round(song / grid) * grid - r.OriginBeats);
            n.Beats = Math.Max(MinStep, end - n.Beat);
        }
    }

    static void Groove(ToolRun r)
    {
        // the groove as (place in its cycle, velocity): the notes copied, or a feel on the magnet's steps
        double grid = r.RateOf("snap"), cycle = 2 * grid;
        List<(double At, int Velocity)> marks;
        string source = Find("groove")!.Params[0].Choices![r.V("source")];
        if (source == "copied")
        {
            if (r.Copied.Count == 0) return;
            double first = Math.Floor(r.Copied.Min(n => n.Beat) / 4) * 4;
            cycle = Math.Max(1, Math.Ceiling((r.Copied.Max(n => n.Beat) - first + 1e-9) / 4) * 4);
            marks = [.. r.Copied.Select(n => ((n.Beat - first) % cycle, n.Velocity))];
        }
        else
        {
            double late = source switch { "swing8" or "swing16" => 0.16, "shuffle" => 1 / 3.0 * 0.5, _ => 0 };
            if (source == "swing8") { grid = 0.5; cycle = 1; }
            if (source == "swing16") { grid = 0.25; cycle = 0.5; }
            double all = source switch { "laidBack" => 0.03, "pushed" => -0.03, _ => 0 };
            marks = [(all, 112), (grid + late * grid * 2 + all, 88)];
        }
        double amount = r.Fraction("amount"), velocity = r.Fraction("velocity");
        foreach (var n in r.Targets)
        {
            double song = n.Beat + r.OriginBeats, inCycle = (song % cycle + cycle) % cycle, best = double.MaxValue, near = inCycle;
            int level = n.Velocity;
            foreach (var (at, v) in marks)
                foreach (double place in (double[])[at - cycle, at, at + cycle])
                    if (Math.Abs(place - inCycle) < best)
                    {
                        best = Math.Abs(place - inCycle);
                        near = place;
                        level = v;
                    }
            n.Beat = Math.Max(0, n.Beat + (near - inCycle) * amount);
            n.Velocity = (int)Math.Round(n.Velocity + (level - n.Velocity) * velocity);
        }
    }

    static void Humanize(ToolRun r)
    {
        var random = new Random(r.V("seed"));
        double time = r.Ms("time"), length = r.Fraction("length");
        int velocity = r.V("velocity");
        // the order is the notes', not the set's: the same dice always give the same take
        foreach (var n in r.Notes.Where(r.Targets.Contains))
        {
            double a = random.NextDouble(), b = random.NextDouble();
            n.Beat = Math.Max(0, n.Beat + (a - b) * time);
            n.Velocity = Math.Clamp(n.Velocity + random.Next(-velocity, velocity + 1), 1, 127);
            n.Beats = Math.Max(MinStep, n.Beats * (1 + (random.NextDouble() - 0.5) * 2 * length));
        }
    }

    static void Levels(ToolRun r)
    {
        var lane = (NoteLane)r.V("lane");
        double multiply = r.Fraction("multiply"), offset = r.Fraction("offset"), pivot = 0.5 + r.Fraction("center") / 2;
        foreach (var n in r.Targets)
        {
            double x = NoteLanes.Get(n, lane) / 127.0;
            if (r.On("invert")) x = 1 - x;
            x = pivot + (x - pivot) * multiply + offset;
            NoteLanes.Set(n, lane, (int)Math.Round(Bend(x, r.V("tension")) * 127));
        }
    }

    static void Lfo(ToolRun r)
    {
        var lane = (NoteLane)r.V("lane");
        double period = r.Rate("speed", LfoRate), phase = r.V("phase") / 360.0, depth = r.Fraction("depth"), middle = r.Fraction("middle");
        int shape = r.V("shape");
        foreach (var n in r.Targets)
        {
            double t = (n.Beat / period + phase) % 1;
            double wave = shape switch
            {
                0 => Math.Sin(2 * Math.PI * t),
                1 => t < 0.25 ? 4 * t : t < 0.75 ? 2 - 4 * t : 4 * t - 4,
                2 => t < 0.5 ? 1 : -1,
                _ => 2 * t - 1,
            };
            NoteLanes.Set(n, lane, (int)Math.Round((middle + wave * depth / 2) * 127));
        }
    }

    static void Rotate(ToolRun r)
    {
        double step = r.RateOf("snap"), cycle = Math.Max(step, Math.Ceiling(r.ClipBeats / 4 - 1e-9) * 4);
        cycle = Math.Min(cycle, r.ClipBeats);
        double shift = r.V("steps") * step;
        foreach (var n in r.Targets) n.Beat = ((n.Beat + shift) % cycle + cycle) % cycle;
    }

    static void Space(ToolRun r, bool insert)
    {
        double at = Math.Max(0, r.PlayBeat), size = r.V("steps") * r.RateOf("snap");
        foreach (var n in r.Notes.ToList())
            if (insert)
            {
                if (n.Beat >= at - 1e-9) n.Beat += size;
                else if (n.EndBeat > at) n.Beats = at - n.Beat;
            }
            else if (n.Beat >= at + size - 1e-9) n.Beat -= size;
            else if (n.Beat >= at - 1e-9) r.Remove(n);
            else if (n.EndBeat > at) n.Beats = at - n.Beat;
    }

    static void Trim(ToolRun r)
    {
        if (r.Targets.Count == 0) return;
        double start = r.Targets.Min(n => n.Beat);
        r.Notes.RemoveAll(n => !r.Targets.Contains(n));
        foreach (var n in r.Notes) n.Beat -= start;
    }

    static void DiscardLengths(ToolRun r)
    {
        double step = r.RateOf("snap");
        foreach (var n in r.Targets) n.Beats = step;
    }
}

/// <summary>What a select command leaves picked: the piano roll's Select menu, one table for the menu and xtlab.</summary>
public static class MidiSelect
{
    public static readonly string[] Commands = ["invert", "random", "more", "offbeat", "overlapping", "stacked", "muted", "color"];

    /// <summary>The new selection. "color" takes the color of the selected notes, or the track's with none.</summary>
    public static HashSet<MidiNote> Run(string command, IReadOnlyList<MidiNote> notes, IReadOnlyCollection<MidiNote> selected, double originBeats, Random random, double stepBeats)
    {
        var picked = new HashSet<MidiNote>(selected);
        switch (command)
        {
            case "invert":
                return [.. notes.Where(n => !selected.Contains(n))];
            case "random":
                return notes.Count == 0 ? [] : [notes[random.Next(notes.Count)]];
            case "more":
                var rest = notes.Where(n => !selected.Contains(n)).ToList();
                if (rest.Count > 0) picked.Add(rest[random.Next(rest.Count)]);
                return picked;
            case "offbeat":
                double step = stepBeats > 0 ? stepBeats : 0.5;
                return [.. notes.Where(n => (int)Math.Round((n.Beat + originBeats) / step) % 2 != 0)];
            case "overlapping":
                return [.. notes.Where(n => notes.Any(o => o != n && o.Pitch < n.Pitch && o.Beat < n.EndBeat - 1e-9 && n.Beat < o.EndBeat - 1e-9))];
            case "stacked":
                return [.. notes.Where(n => notes.Any(o => o != n && o.Pitch < n.Pitch && Math.Abs(o.Beat - n.Beat) < 1e-6 && Math.Abs(o.Beats - n.Beats) < 1e-6))];
            case "muted":
                return [.. notes.Where(n => n.Muted)];
            case "color":
                var colors = selected.Select(n => n.Color).ToHashSet();
                if (colors.Count == 0) colors.Add(0);
                return [.. notes.Where(n => colors.Contains(n.Color))];
        }
        return picked;
    }
}

/// <summary>
/// What a click writes besides a chord (FL Studio's stamps): runs along the scale and drum figures, on the row clicked.
/// Each one is its notes as (degrees or semitones up, beat, length).
/// </summary>
public static class MidiStamps
{
    public sealed record Stamp(string Id, string Group, bool OnScale, (int Up, double Beat, double Beats)[] Notes);
    public const string Scales = "scales", Drums = "drums";

    static (int, double, double)[] Run(int count, int direction, double step) =>
        [.. Enumerable.Range(0, count).Select(i => (i * direction, i * step, step))];
    static (int, double, double)[] Hits(double step, int count) =>
        [.. Enumerable.Range(0, count).Select(i => (0, i * step, step * 0.9))];

    public static readonly Stamp[] All =
    [
        new("run-up", Scales, true, Run(8, 1, 0.25)),
        new("run-down", Scales, true, Run(8, -1, 0.25)),
        new("run-octave", Scales, true, Run(15, 1, 0.125)),
        new("penta", Scales, true, [(0, 0, 0.25), (2, 0.25, 0.25), (4, 0.5, 0.25), (7, 0.75, 0.25), (9, 1, 0.5)]),
        new("eighths", Drums, false, Hits(0.5, 8)),
        new("sixteenths", Drums, false, Hits(0.25, 16)),
        new("four-floor", Drums, false, Hits(1, 4)),
        new("offbeats", Drums, false, [(0, 0.5, 0.25), (0, 1.5, 0.25), (0, 2.5, 0.25), (0, 3.5, 0.25)]),
        new("roll", Drums, false, Hits(0.125, 8)),
        new("tresillo", Drums, false, [(0, 0, 0.5), (0, 0.75, 0.5), (0, 1.5, 0.5)]),
    ];

    public static Stamp? Find(string? id) => Array.Find(All, s => s.Id == id);

    /// <summary>The notes of a stamp on a row, at a beat. On a scale it goes along it; a drum figure stays on its row.</summary>
    public static List<MidiNote> Notes(Stamp stamp, int pitch, double beat, MusicScale? scale, int velocity)
    {
        var key = scale ?? new MusicScale(pitch % 12, MusicScale.Kinds[0]);
        return
        [
            .. stamp.Notes.Select(n => new MidiNote
            {
                Pitch = stamp.OnScale ? key.Move(pitch, n.Up) : pitch + n.Up,
                Beat = beat + n.Beat, Beats = n.Beats, Velocity = velocity,
            }).Where(n => n.Pitch is >= MidiNote.MinPitch and <= MidiNote.MaxPitch),
        ];
    }
}

/// <summary>
/// The chord progression tool (FL Studio's, without its AI: presets, chords typed by name or by degree, or a generator
/// that walks the common moves of harmony, bolder with Atrevimiento). The chords become notes with a voicing, a rhythm and a bass.
/// </summary>
public static class ChordProgressions
{
    /// <summary>Degrees of each preset, as they are written; "auto" generates them.</summary>
    static readonly (string Id, string Chords)[] Presets =
    [
        ("auto", ""), ("pop", "I V vi IV"), ("axis", "vi IV I V"), ("ballad", "I vi IV V"), ("blues", "I I IV I V IV I V"),
        ("andalusian", "i bVII bVI V"), ("jazz", "ii7 V7 Imaj7 vi7"), ("rock", "I bVII IV I"), ("dream", "I iii IV iv"),
    ];
    public static readonly string[] PresetIds = [.. Presets.Select(p => p.Id)];

    /// <summary>Where each degree usually goes next, the first ones the most common.</summary>
    static readonly Dictionary<int, int[]> Moves = new()
    {
        [0] = [3, 4, 5, 1, 2], [1] = [4, 3, 6], [2] = [5, 3, 1], [3] = [4, 0, 1, 5], [4] = [0, 5, 3], [5] = [3, 1, 4, 2], [6] = [0, 2, 5],
    };

    static readonly Regex Roman = new(@"^(b|#)?(VII|VI|IV|V|III|II|I|vii|vi|iv|v|iii|ii|i)(°|dim|maj7|m7|7|sus2|sus4)?$", RegexOptions.Compiled);
    static readonly Regex Named = new(@"^([A-Ga-g])(#|b)?(maj7|m7|dim|aug|sus2|sus4|m|5|7)?$", RegexOptions.Compiled);
    static readonly string[] RomanNames = ["I", "II", "III", "IV", "V", "VI", "VII"];
    static readonly int[] Letters = [9, 11, 0, 2, 4, 5, 7];

    /// <summary>A chord's root (semitones from C) and its shape.</summary>
    public readonly record struct Chord(int Root, int[] Steps);

    static int[] Shape(string kind) => kind switch
    {
        "m" => [0, 3, 7], "7" => [0, 4, 7, 10], "maj7" => [0, 4, 7, 11], "m7" => [0, 3, 7, 10], "dim" or "°" => [0, 3, 6],
        "aug" => [0, 4, 8], "sus2" => [0, 2, 7], "sus4" => [0, 5, 7], "5" => [0, 7], _ => [0, 4, 7],
    };

    /// <summary>
    /// Chords typed with spaces, commas or dashes: names (Am, F#m7) or degrees of the scale (I V vi IV, bVII). A dot
    /// repeats the last one; anything else is skipped.
    /// </summary>
    public static List<Chord> Parse(string text, MusicScale scale)
    {
        var list = new List<Chord>();
        int[] steps = scale.Count == 7 ? scale.Kind.Steps : MusicScale.Kinds[0].Steps;
        foreach (string word in text.Split([' ', ',', '-', '\t', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == "." && list.Count > 0) { list.Add(list[^1]); continue; }
            var roman = Roman.Match(word);
            if (roman.Success)
            {
                string numeral = roman.Groups[2].Value, suffix = roman.Groups[3].Value;
                int degree = Array.IndexOf(RomanNames, numeral.ToUpperInvariant());
                int shift = roman.Groups[1].Value switch { "b" => -1, "#" => 1, _ => 0 };
                string kind = suffix.Length > 0 ? suffix : char.IsLower(numeral[0]) ? "m" : "";
                if (suffix == "7" && char.IsLower(numeral[0])) kind = "m7";
                list.Add(new Chord((scale.Root + steps[degree] + shift + 12) % 12, Shape(kind)));
                continue;
            }
            var name = Named.Match(word);
            if (!name.Success) continue;
            int root = Letters[char.ToUpperInvariant(name.Groups[1].Value[0]) - 'A'] + name.Groups[2].Value switch { "#" => 1, "b" => -1, _ => 0 };
            list.Add(new Chord((root + 12) % 12, Shape(name.Groups[3].Value)));
        }
        return list;
    }

    /// <summary>Four chords in the scale, walking <see cref="Moves"/>: a bold one takes the rarer turns and adds sevenths.</summary>
    static List<Chord> Generate(MusicScale scale, int count, double adventure, Random random)
    {
        int[] steps = scale.Count == 7 ? scale.Kind.Steps : MusicScale.Kinds[0].Steps;
        var list = new List<Chord>();
        int degree = random.NextDouble() < adventure ? random.Next(7) : 0;
        for (int i = 0; i < count; i++)
        {
            bool seventh = random.NextDouble() < adventure * 0.7;
            int[] stack = [0, 2, 4, .. seventh ? (int[])[6] : []];
            list.Add(new Chord((scale.Root + steps[degree]) % 12, [.. stack.Select(s => Up(steps, degree, s))]));
            var next = Moves[degree];
            degree = next[Math.Min(next.Length - 1, (int)(random.NextDouble() * (1 + adventure * (next.Length - 1)) * (random.NextDouble() < 0.6 ? 0.5 : 1)))];
        }
        return list;
    }

    /// <summary>The semitones above <paramref name="degree"/> of the note <paramref name="up"/> degrees over it.</summary>
    static int Up(int[] steps, int degree, int up)
    {
        int to = degree + up;
        return steps[to % 7] + (to / 7) * 12 - steps[degree];
    }

    static int[] Voice(int[] notes, int voicing) => voicing switch
    {
        // the middle of the chord an octave up: nothing crowded
        1 when notes.Length > 2 => [notes[0], .. notes[2..], notes[1] + 12],
        2 => [.. notes, notes[0] + 12],
        3 => [.. notes, .. notes.Select(n => n + 12)],
        _ => notes,
    };

    /// <summary>The rhythms as (beat of a bar, length), per four beats; "hold" takes the whole chord and "arp" is built apart.</summary>
    static readonly (double Beat, double Beats)[][] Rhythms =
    [
        [], [(0, 0.5), (1.5, 0.5), (2.5, 0.5), (3.5, 0.5)], [(0, 0.4), (0.5, 0.4), (1, 0.4), (1.5, 0.4), (2, 0.4), (2.5, 0.4), (3, 0.4), (3.5, 0.4)], [], [(0, 1.5), (1.5, 1), (2.5, 1.5)],
    ];

    public static void Run(ToolRun r)
    {
        var scale = r.ScaleOrMajor;
        var tool = MidiTools.Find("progression")!;
        var random = new Random(r.V("seed"));
        double length = r.Rate("length", tool.Params.First(p => p.Id == "length"));
        string typed = r.Words.GetValueOrDefault("chords", "").Trim();
        var (from, to) = r.Span;
        int room = Math.Max(1, (int)Math.Ceiling((to - from) / length - 1e-9));
        var chords = typed.Length > 0 ? Parse(typed, scale)
            : Presets[r.V("preset")].Id == "auto" ? Generate(scale, Math.Min(room, 8), r.Fraction("adventure"), random)
            : Parse(Presets[r.V("preset")].Chords, scale);
        if (chords.Count == 0) return;
        if (r.On("replace")) r.RemoveTargets();
        int bottom = (r.V("octave") + 1) * 12, rhythm = r.V("rhythm");
        for (int i = 0; i < room; i++)
        {
            var chord = chords[i % chords.Count];
            double start = from + i * length, end = Math.Min(to, start + length);
            int root = bottom + chord.Root;
            int[] notes = Voice([.. chord.Steps.Select(s => root + s)], r.V("voicing"));
            if (rhythm == 3)
            {
                for (int k = 0; start + k * 0.5 < end - 1e-9; k++)
                    r.Add(new MidiNote { Pitch = notes[k % notes.Length], Beat = start + k * 0.5, Beats = 0.45, Velocity = k % 2 == 0 ? 105 : 88 });
            }
            else if (rhythm == 0)
                foreach (int p in notes) r.Add(new MidiNote { Pitch = p, Beat = start, Beats = end - start, Velocity = 96 });
            else
                for (double bar = start; bar < end - 1e-9; bar += 4)
                    foreach (var (beat, beats) in Rhythms[rhythm])
                        if (bar + beat < end - 1e-9)
                            foreach (int p in notes) r.Add(new MidiNote { Pitch = p, Beat = bar + beat, Beats = Math.Min(beats, end - bar - beat), Velocity = beat == 0 ? 108 : 92 });
            if (r.On("bass")) r.Add(new MidiNote { Pitch = root - 12, Beat = start, Beats = end - start, Velocity = 104 });
        }
    }
}
