using System.Text.Json;
using System.Text.Json.Serialization;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Audio.Vst;

namespace XtremeDaw.Projects;

/// <summary>A point of an <see cref="AutomationLane"/>: a timeline time and the control's position (0..1), as a knob or a fader has it.</summary>
public sealed class AutomationPoint
{
    public double Sec { get; set; }
    public double Value { get; set; }
    /// <summary>How the stretch from here to the next point goes (<see cref="AutoShapes"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), JsonConverter(typeof(AutoShapeJson))]
    public AutoShape Shape { get; set; }
    /// <summary>Its tension, -1..1: a curve's bend (0 = straight), or the steps and cycles of the others.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Tension { get; set; }
    /// <summary>The first bends were powers, t^(4^curve): read only (never written), they become the curve through the same middle.</summary>
    [JsonPropertyName("curve"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double OldCurve { get => 0; set => oldCurve = value; }
    double oldCurve;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The same point at another time (and value), with its stretch's shape.</summary>
    public AutomationPoint At(double sec, double? value = null) => new() { Sec = sec, Value = value ?? Value, Shape = Shape, Tension = Tension, Extra = Extra };

    internal void Normalize()
    {
        Sec = Math.Max(0, Sec);
        Value = Math.Clamp(Value, 0, 1);
        if (oldCurve != 0 && double.IsFinite(oldCurve) && Tension == 0)
            Tension = AutoShapes.TensionThrough(Math.Pow(0.5, Math.Pow(4, Math.Clamp(oldCurve, -1, 1))));
        oldCurve = 0;
        Tension = double.IsFinite(Tension) ? Math.Round(Math.Clamp(Tension, -1, 1), 4) : 0;
        if (!Enum.IsDefined(Shape)) Shape = AutoShape.Curve;
    }
}

/// <summary>A shape by its name in the project ("curve", "double", "hold", "stairs", "smooth-stairs", "pulse", "wave"); one it doesn't know reads as a curve.</summary>
sealed class AutoShapeJson : JsonConverter<AutoShape>
{
    static readonly string[] Names = ["curve", "double", "hold", "stairs", "smooth-stairs", "pulse", "wave"];

    public override AutoShape Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        int i = reader.TokenType == JsonTokenType.String ? Array.IndexOf(Names, reader.GetString()) : -1;
        return i < 0 ? AutoShape.Curve : (AutoShape)i;
    }

    public override void Write(Utf8JsonWriter writer, AutoShape value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Names[(int)value < Names.Length ? (int)value : 0]);
}

/// <summary>
/// A stretch of a lane, its own piece like a MIDI clip (asked by the user: to copy one N times): it moves, is trimmed,
/// copied and repeated whole. Its points' times are relative to <see cref="AtSec"/>; those past <see cref="LengthSec"/>
/// wait outside it, so trimming it back brings them back.
/// </summary>
public sealed class AutomationBlock
{
    public const double MinSec = 0.05;
    public string Id { get; set; } = Ids.New("atb");
    public double AtSec { get; set; }
    public double LengthSec { get; set; } = 8;
    public List<AutomationPoint> Points { get; set; } = [];
    /// <summary>
    /// A block that loops (asked by the user, like GarageBand): its points are a pass this long that repeats until the
    /// block ends. Null = it plays once.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? LoopSec { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public double EndSec => AtSec + LengthSec;
    /// <summary>One pass of its points: the loop's, or the whole block.</summary>
    [JsonIgnore] public double PassSec => LoopSec is > 0 ? LoopSec.Value : LengthSec;
    /// <summary>How many passes start inside it (the last one may be cut short).</summary>
    [JsonIgnore] public int Passes => Math.Max(1, (int)Math.Ceiling(LengthSec / PassSec - 1e-9));

    internal void Normalize()
    {
        if (string.IsNullOrEmpty(Id)) Id = Ids.New("atb");
        AtSec = double.IsFinite(AtSec) ? Math.Max(0, AtSec) : 0;
        LengthSec = double.IsFinite(LengthSec) ? Math.Max(MinSec, LengthSec) : MinSec;
        Points ??= [];
        Points.RemoveAll(p => p == null || !double.IsFinite(p.Sec) || !double.IsFinite(p.Value));
        foreach (var p in Points) p.Normalize();
        Points.Sort((a, b) => a.Sec.CompareTo(b.Sec));
        // a loop no longer than the block is no loop
        LoopSec = LoopSec is { } loop && double.IsFinite(loop) && loop >= MinSec && loop < LengthSec - 1e-9 ? loop : null;
    }

    /// <summary>A copy with a new id at another time (the same points and loop).</summary>
    public AutomationBlock Copy(double atSec) => new()
    {
        AtSec = Math.Max(0, atSec),
        LengthSec = LengthSec,
        LoopSec = LoopSec,
        Points = [.. Points.Select(p => p.At(p.Sec))],
    };

    /// <summary>
    /// A looped block's pass set to <paramref name="passSec"/> (the original's edge, asked by the user, #171): every repeat
    /// follows and the block keeps how many passes it had. Points past the new pass wait outside it.
    /// </summary>
    public void SetPass(double passSec)
    {
        if (LoopSec is not { } pass) return;
        double cell = Math.Max(MinSec, passSec);
        LengthSec = LengthSec * cell / pass;
        LoopSec = cell;
    }

    /// <summary>A time inside the block (relative) as a time inside its pass.</summary>
    public double InPass(double sec) => LoopSec is > 0 && sec >= 0 ? sec % PassSec : sec;

    /// <summary>The control's position at a time inside the block (relative): it holds its first and last point.</summary>
    public double PositionAt(double sec, bool stepped) => Automations.PositionAt(Points, InPass(sec), stepped);

    /// <summary>
    /// The block with its repeats written out as points, sounding the same (a loop's joins go in
    /// <see cref="Automations.MinGapSec"/>); a block that doesn't loop is itself.
    /// </summary>
    public AutomationBlock WrittenOut(bool stepped)
    {
        if (LoopSec == null) return this;
        var once = new AutomationBlock { Id = Id, AtSec = AtSec, LengthSec = PassSec, Points = Points, Extra = Extra };
        var all = new AutomationBlock { Id = Id, AtSec = AtSec, LengthSec = LengthSec, Extra = Extra };
        double pass = PassSec;
        for (int k = 0; k < Passes; k++)
        {
            double at = k * pass;
            bool last = at + pass >= LengthSec - 1e-9;
            double to = last ? LengthSec - at : pass - Automations.MinGapSec;
            foreach (var p in once.Cut(0, to, stepped).Points)
                all.Points.Add(p.At(at + p.Sec));
        }
        return all;
    }

    /// <summary>
    /// The piece from <paramref name="from"/> to <paramref name="to"/> (relative) as a block of its own, sounding the same:
    /// a point on each edge, and a stretch an edge cuts goes on as the piece of it that is left (<see cref="Write"/>).
    /// </summary>
    public AutomationBlock Cut(double from, double to, bool stepped)
    {
        if (LoopSec != null) return WrittenOut(stepped).Cut(from, to, stepped);
        var piece = new AutomationBlock { AtSec = AtSec + from, LengthSec = Math.Max(MinSec, to - from) };
        var into = piece.Points;
        int first = Points.FindIndex(p => p.Sec > from), end = Points.FindIndex(p => p.Sec >= to);
        if (first < 0) first = Points.Count;
        if (end < 0) end = Points.Count;
        // the stretch running at the start, up to the first point inside or the end
        if (!stepped && first > 0 && first < Points.Count) Write(into, Points[first - 1], Points[first], from, Math.Min(Points[first].Sec, to), from);
        else into.Add(new() { Sec = 0, Value = PositionAt(from, stepped) });
        for (int i = first; i < end; i++)
        {
            var p = Points[i];
            // the stretch the end cuts goes on as its piece; the others keep their shape
            if (!stepped && i == end - 1 && end < Points.Count) Write(into, p, Points[end], p.Sec, to, from);
            else into.Add(p.At(p.Sec - from));
        }
        if (into[^1].Sec < to - from - 1e-9) into.Add(new() { Sec = to - from, Value = PositionAt(to, stepped) });
        return piece;
    }

    /// <summary>Lines per half cycle of a smooth shape that is cut (it has no shape of its own for a piece).</summary>
    const int CutSteps = 16;

    /// <summary>
    /// The stretch from <paramref name="a"/> to <paramref name="b"/> between <paramref name="s0"/> and <paramref name="s1"/>
    /// (inside it) as points from <paramref name="s0"/> on, sounding the same with a point at <paramref name="s1"/> after
    /// them: a curve with the tension its piece has, a double curve as its two halves, a stepped shape as holds, and the
    /// smooth stairs and the wave as short lines. Times go relative to <paramref name="origin"/>.
    /// </summary>
    static void Write(List<AutomationPoint> into, AutomationPoint a, AutomationPoint b, double s0, double s1, double origin)
    {
        double span = b.Sec - a.Sec;
        double u0 = span > 0 ? (s0 - a.Sec) / span : 0, u1 = span > 0 ? (s1 - a.Sec) / span : 0, rise = b.Value - a.Value, t = a.Tension;
        void Put(double u, AutoShape shape, double tension, double along) =>
            into.Add(new() { Sec = a.Sec + u * span - origin, Value = a.Value + rise * along, Shape = shape, Tension = Math.Clamp(tension, -1, 1) });
        if (span <= 0 || u1 <= u0)
        {
            Put(Math.Max(0, u0), a.Shape, t, AutoShapes.Along(a.Shape, Math.Max(0, u0), t));
            return;
        }
        switch (a.Shape)
        {
            case AutoShape.Curve:
                Put(u0, AutoShape.Curve, t * (u1 - u0), AutoShapes.Exp(u0, t));
                break;
            case AutoShape.Double:
                if (u0 < 0.5) Put(u0, AutoShape.Curve, t * 2 * (Math.Min(u1, 0.5) - u0), AutoShapes.Along(AutoShape.Double, u0, t));
                if (u1 > 0.5) Put(Math.Max(u0, 0.5), AutoShape.Curve, -t * 2 * (u1 - Math.Max(u0, 0.5)), AutoShapes.Along(AutoShape.Double, Math.Max(u0, 0.5), t));
                break;
            case AutoShape.Hold or AutoShape.Stairs or AutoShape.Pulse:
            {
                int m = AutoShapes.Pieces(a.Shape, t);
                Put(u0, AutoShape.Hold, 0, AutoShapes.Along(a.Shape, u0, t));
                for (int j = (int)Math.Floor(u0 * m) + 1; j < m && j / (double)m < u1; j++)
                    Put(j / (double)m, AutoShape.Hold, 0, AutoShapes.Along(a.Shape, (j + 0.5) / m, t));
                break;
            }
            default:
            {
                int halves = a.Shape == AutoShape.Wave ? 2 * AutoShapes.Count(t) - 1 : AutoShapes.Count(t);
                int n = Math.Max(1, (int)Math.Ceiling(halves * CutSteps * (u1 - u0)));
                for (int k = 0; k < n; k++)
                {
                    double u = u0 + (u1 - u0) * k / n;
                    Put(u, AutoShape.Curve, 0, AutoShapes.Along(a.Shape, u, t));
                }
                break;
            }
        }
    }
}

/// <summary>
/// One automated parameter of a track (asked by the user: anything a channel has, audio or MIDI), drawn in its own lane
/// under the track, its child in the timeline. <see cref="Target"/> says which parameter (<see cref="AutoTarget"/>). Its
/// curve lives in <see cref="Blocks"/>, which read as one curve (<see cref="Flat"/>): between blocks it holds the value the
/// last one left, and where two overlap the later one takes over at its start. The points hold the control's position,
/// not its value, so a lane reads like the knob it moves. Without blocks or <see cref="On"/>, the parameter stays where
/// the channel has it. A target that no longer exists (an effect taken out) keeps its lane, silent, so undoing that
/// brings it all back.
/// </summary>
public sealed class AutomationLane
{
    public string Id { get; set; } = Ids.New("aut");
    public string Target { get; set; } = AutoTarget.Volume;
    /// <summary>A plugin parameter's name as the plugin gave it (only the running plugin can say it again).</summary>
    public string? Name { get; set; }
    public bool On { get; set; } = true;
    /// <summary>Out of the timeline: it keeps playing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }
    /// <summary>Its own height in the timeline, as a multiple of <see cref="DefaultHeight"/> at the tracks' default
    /// height (it follows the vertical zoom like a track); null = 1.</summary>
    public double? HeightScale { get; set; }
    public const double DefaultHeight = 52, MinHeight = 32, MaxHeight = 200;
    public List<AutomationBlock> Blocks { get; set; } = [];
    /// <summary>The first lanes had their points right here: read only (never written), they become one block.</summary>
    [JsonPropertyName("points")]
    public List<AutomationPoint>? OldPoints { get => null; set => oldPoints = value; }
    List<AutomationPoint>? oldPoints;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    List<AutomationPoint>? flat;
    bool flatStepped;

    /// <summary>The blocks as one curve, in timeline seconds. Kept until <see cref="Touch"/>: it is read every tick.</summary>
    public List<AutomationPoint> Flat(bool stepped)
    {
        if (flat == null || flatStepped != stepped)
        {
            flat = Flatten(Blocks, stepped);
            flatStepped = stepped;
        }
        return flat;
    }

    /// <summary>After any edit of the blocks.</summary>
    public void Touch() => flat = null;

    /// <summary>Blocks as one curve (<see cref="Flat"/>).</summary>
    public static List<AutomationPoint> Flatten(IEnumerable<AutomationBlock> blocks, bool stepped)
    {
        var list = blocks.OrderBy(b => b.AtSec).ToList();
        var flat = new List<AutomationPoint>();
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            if (b.Points.Count == 0) continue;
            double end = i + 1 < list.Count ? Math.Min(b.EndSec, list[i + 1].AtSec) : b.EndSec;
            if (end <= b.AtSec) continue;
            // it hands over at once, not in a line from where the last block left it
            if (flat.Count > 0 && b.AtSec > flat[^1].Sec + Automations.MinGapSec)
                flat.Add(new() { Sec = b.AtSec - Automations.MinGapSec, Value = flat[^1].Value });
            foreach (var p in b.Cut(0, end - b.AtSec, stepped).Points)
                flat.Add(p.At(b.AtSec + p.Sec));
        }
        return flat;
    }

    internal void Normalize()
    {
        if (string.IsNullOrEmpty(Id)) Id = Ids.New("aut");
        Target ??= AutoTarget.Volume;
        HeightScale = Track.NormalizeHeight(HeightScale, 0.3, 5);
        Blocks ??= [];
        if (oldPoints is { Count: > 0 } old && Blocks.Count == 0)
            Blocks.Add(new() { LengthSec = old.Max(p => p?.Sec ?? 0) + AutomationBlock.MinSec, Points = old });
        oldPoints = null;
        Blocks.RemoveAll(b => b == null);
        foreach (var b in Blocks) b.Normalize();
        Touch();
    }
}

/// <summary>
/// What carries lanes (<see cref="Automations"/>): a track, a folder or a bus, all channels (asked by the user, #136: a folder's
/// lanes are like a track's; #267: a bus's too). Its slots, routing, fader, pan and mute are what a lane drives and starts from; the
/// instrument targets are a track's alone.
/// </summary>
public interface IAutomated
{
    string Id { get; }
    List<AutomationLane>? Automation { get; set; }
    bool AutomationFolded { get; set; }
    IReadOnlyList<InsertSlot> AutoSlots { get; }
    IRouted AutoRouted { get; }
    double AutoVolume { get; }
    double AutoPan { get; }
    bool AutoMuted { get; }
}

/// <summary>
/// The names of what a lane can drive, as saved in <see cref="AutomationLane.Target"/>: <c>volume</c>, <c>pan</c>,
/// <c>mute</c>, <c>send:&lt;bus&gt;</c>, <c>fx:&lt;slot&gt;:&lt;param&gt;</c> (a native effect's <see cref="FxParam"/>, its
/// ON or an EQ band's knob), <c>vst:&lt;slot&gt;:&lt;id&gt;</c> (a VST3 effect's parameter), <c>inst:&lt;id&gt;</c> (the
/// MIDI track's instrument), <c>sampler:&lt;knob&gt;</c> (<see cref="SamplerKnobs.Ids"/>) and <c>granular:&lt;knob&gt;</c> (<see cref="GranularKnobs.Ids"/>). Ids in the project, so a slot moved or renamed keeps its lanes.
/// </summary>
public static class AutoTarget
{
    public const string Volume = "volume", Pan = "pan", Mute = "mute";
    const string SendPrefix = "send:", FxPrefix = "fx:", VstPrefix = "vst:", InstPrefix = "inst:", SamplerPrefix = "sampler:", GranularPrefix = "granular:";

    public static string Send(string busId) => SendPrefix + busId;
    public static string Fx(string slotId, string paramId) => $"{FxPrefix}{slotId}:{paramId}";
    public static string Vst(string slotId, uint id) => $"{VstPrefix}{slotId}:{id}";
    public static string Instrument(uint id) => InstPrefix + id;
    public static string Sampler(SamplerKnob knob) => SamplerPrefix + SamplerKnobs.Ids[(int)knob];
    public static string Granular(GranularKnob knob) => GranularPrefix + GranularKnobs.Ids[(int)knob];

    public static string? BusOf(string target) => target.StartsWith(SendPrefix, StringComparison.Ordinal) ? target[SendPrefix.Length..] : null;

    /// <summary>A native effect's target as its slot and parameter.</summary>
    public static (string Slot, string Param)? FxOf(string target) => Split(target, FxPrefix);

    /// <summary>A VST3 effect's target as its slot and parameter id.</summary>
    public static (string Slot, uint Id)? VstOf(string target) =>
        Split(target, VstPrefix) is { } s && uint.TryParse(s.Tail, out uint id) ? (s.First, id) : null;

    public static uint? InstrumentOf(string target) =>
        target.StartsWith(InstPrefix, StringComparison.Ordinal) && uint.TryParse(target.AsSpan(InstPrefix.Length), out uint id) ? id : null;

    public static SamplerKnob? SamplerOf(string target)
    {
        if (!target.StartsWith(SamplerPrefix, StringComparison.Ordinal)) return null;
        int at = Array.IndexOf(SamplerKnobs.Ids, target[SamplerPrefix.Length..]);
        return at < 0 ? null : (SamplerKnob)at;
    }

    public static GranularKnob? GranularOf(string target)
    {
        if (!target.StartsWith(GranularPrefix, StringComparison.Ordinal)) return null;
        int at = Array.IndexOf(GranularKnobs.Ids, target[GranularPrefix.Length..]);
        return at < 0 ? null : (GranularKnob)at;
    }

    static (string First, string Tail)? Split(string target, string prefix)
    {
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int at = target.IndexOf(':', prefix.Length);
        return at < 0 ? null : (target[prefix.Length..at], target[(at + 1)..]);
    }
}

/// <summary>
/// The edits of a track's lanes and their reading: one place for the window, the export and xtlab. A control's position
/// goes through <see cref="Position"/> and <see cref="Stepped"/>, and <see cref="Compile"/> hands the engine what it plays.
/// </summary>
public static class Automations
{
    /// <summary>How far apart two points have to be (s).</summary>
    public const double MinGapSec = 0.001;
    /// <summary>A new lane's block is at least this long.</summary>
    public const double DefaultBlockSec = 8;
    /// <summary>The most copies one "Repetir" makes.</summary>
    public const int MaxRepeat = 512;

    public static AutomationLane? Find(IAutomated t, string target)
    {
        if (t.Automation is not { } list) return null;
        for (int i = 0; i < list.Count; i++) if (list[i].Target == target) return list[i]; // read every tick: no closure
        return null;
    }

    public static AutomationLane? ById(IAutomated t, string id) => t.Automation?.Find(l => l.Id == id);

    public static AutomationBlock? BlockById(AutomationLane lane, string id) => lane.Blocks.Find(b => b.Id == id);

    /// <summary>
    /// A lane for <paramref name="target"/>: the one it has, shown, or a new one with a block over <paramref name="lengthSec"/>
    /// holding <paramref name="position"/>, so nothing changes until a point moves.
    /// </summary>
    public static AutomationLane Show(IAutomated t, string target, double position, string? name = null, double lengthSec = 0)
    {
        t.AutomationFolded = false;
        if (Find(t, target) is { } had)
        {
            had.Hidden = false;
            return had;
        }
        var lane = new AutomationLane { Target = target, Name = name };
        AddBlock(lane, 0, Math.Max(lengthSec, DefaultBlockSec), position);
        (t.Automation ??= []).Add(lane);
        return lane;
    }

    public static void Remove(IAutomated t, string id)
    {
        t.Automation?.RemoveAll(l => l.Id == id);
        if (t.Automation is { Count: 0 }) t.Automation = null;
    }

    /// <summary>A new block holding one value.</summary>
    public static AutomationBlock AddBlock(AutomationLane lane, double atSec, double lengthSec, double position)
    {
        var b = new AutomationBlock { AtSec = Math.Max(0, atSec), LengthSec = Math.Max(AutomationBlock.MinSec, lengthSec), Points = [new() { Sec = 0, Value = Math.Clamp(position, 0, 1) }] };
        lane.Blocks.Add(b);
        lane.Touch();
        return b;
    }

    /// <summary>
    /// <paramref name="times"/> copies of a block, one right after the other from its end. Returns the last one.
    /// They are independent: editing one leaves the others alone.
    /// </summary>
    public static AutomationBlock? Repeat(AutomationLane lane, string id, int times)
    {
        if (BlockById(lane, id) is not { } b) return null;
        AutomationBlock? last = null;
        for (int k = 1; k <= Math.Clamp(times, 1, MaxRepeat); k++)
        {
            last = b.Copy(b.AtSec + k * b.LengthSec);
            lane.Blocks.Add(last);
        }
        lane.Touch();
        return last;
    }

    /// <summary>
    /// Loops a block up to <paramref name="lengthSec"/> (the loop handle, like GarageBand): what it held is one pass,
    /// repeated until its new end. One that already loops keeps its pass; a length inside the first pass takes the loop away.
    /// </summary>
    public static void Loop(AutomationLane lane, string id, double lengthSec)
    {
        if (BlockById(lane, id) is not { } b) return;
        b.LoopSec ??= b.LengthSec;
        b.LengthSec = Math.Max(AutomationBlock.MinSec, lengthSec);
        b.Normalize();
        lane.Touch();
    }

    /// <summary>A looped block's pass changed, its repeats with it (<see cref="AutomationBlock.SetPass"/>).</summary>
    public static void Pass(AutomationLane lane, string id, double passSec)
    {
        if (BlockById(lane, id) is not { } b) return;
        b.SetPass(passSec);
        b.Normalize();
        lane.Touch();
    }

    /// <summary>A block in two at a timeline time, sounding the same. False when the time isn't inside it.</summary>
    public static bool Split(AutomationLane lane, string id, double sec, bool stepped)
    {
        if (BlockById(lane, id) is not { } b || sec <= b.AtSec + AutomationBlock.MinSec || sec >= b.EndSec - AutomationBlock.MinSec) return false;
        double at = sec - b.AtSec;
        var left = b.Cut(0, at, stepped);
        var right = b.Cut(at, b.LengthSec, stepped);
        lane.Blocks[lane.Blocks.IndexOf(b)] = left;
        lane.Blocks.Add(right);
        lane.Touch();
        return true;
    }

    /// <summary>The control's position at a time on the lane's curve (it has blocks).</summary>
    public static double PositionAt(AutomationLane lane, bool stepped, double sec) => PositionAt(lane.Flat(stepped), sec, stepped);

    /// <summary>The position at a time on a list of points, in order: it holds the first and the last.</summary>
    public static double PositionAt(List<AutomationPoint> p, double sec, bool stepped)
    {
        if (p.Count == 0) return 0;
        if (sec <= p[0].Sec) return p[0].Value;
        if (sec >= p[^1].Sec) return p[^1].Value;
        int lo = 0, hi = p.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (p[mid].Sec <= sec) lo = mid;
            else hi = mid;
        }
        if (stepped) return p[lo].Value;
        double span = p[hi].Sec - p[lo].Sec;
        return span <= 0 ? p[hi].Value : p[lo].Value + (p[hi].Value - p[lo].Value) * AutoShapes.Along(p[lo].Shape, (sec - p[lo].Sec) / span, p[lo].Tension);
    }

    /// <summary>A switch: it holds each point until the next one, and only 0 and 1 mean anything.</summary>
    public static bool Stepped(IAutomated t, string target) =>
        target == AutoTarget.Mute || FxParamOf(t.AutoSlots, target) is { Param.Toggle: true };

    /// <summary>The native parameter of a lane's target, with its slot; null for anything else or a slot that is gone.</summary>
    public static (InsertSlot Slot, FxParam Param)? FxParamOf(IReadOnlyList<InsertSlot> slots, string target)
    {
        if (AutoTarget.FxOf(target) is not { } f) return null;
        foreach (var s in slots)
        {
            if (s.Id != f.Slot) continue;
            foreach (var p in FxDefs.Automatable(s.Type))
                if (p.Id == f.Param) return (s, p);
        }
        return null;
    }

    /// <summary>The bus of a send lane the channel still has a place for (a send there, or a bus to add one to).</summary>
    static Send? SendOf(IRouted c, string bus) => c.Sends.Find(s => s.BusId == bus);

    /// <summary>Whether the lane still has something to drive.</summary>
    public static bool Exists(IAutomated t, string target)
    {
        if (target is AutoTarget.Volume or AutoTarget.Pan or AutoTarget.Mute) return true;
        if (AutoTarget.BusOf(target) is { } bus) return SendOf(t.AutoRouted, bus) != null;
        if (AutoTarget.VstOf(target) is { } v) return t.AutoSlots.Any(s => s.Id == v.Slot && s.Type == InsertSlot.VstKind);
        if (AutoTarget.InstrumentOf(target) != null) return t is Track { Instrument: not null };
        if (AutoTarget.SamplerOf(target) != null) return t is Track { Sampler: not null };
        if (AutoTarget.GranularOf(target) != null) return t is Track { Granular: not null };
        return FxParamOf(t.AutoSlots, target) != null;
    }

    /// <summary>Where the control is now, for a new lane's first point. A plugin's parameter starts from what the caller knows.</summary>
    public static double Position(IAutomated t, string target)
    {
        if (target == AutoTarget.Volume) return Math.Sqrt(Math.Max(0, t.AutoVolume) / 1.5);
        if (target == AutoTarget.Pan) return (t.AutoPan + 1) / 2;
        if (target == AutoTarget.Mute) return t.AutoMuted ? 1 : 0;
        if (AutoTarget.BusOf(target) is { } bus) return SendOf(t.AutoRouted, bus)?.Level ?? 0;
        if (AutoTarget.SamplerOf(target) is { } knob) return t is Track { Sampler: { } s } ? SamplerKnobs.Position(knob, s.Knob(knob)) : 0.5;
        if (AutoTarget.GranularOf(target) is { } grain) return t is Track { Granular: { } g } ? GranularKnobs.Position(grain, g.Knob(grain)) : 0.5;
        return FxParamOf(t.AutoSlots, target) is { } f ? f.Param.ToKnob(f.Param.Get(f.Slot)) : 0.5;
    }

    /// <summary>The lane as the engine reads it; null when it does nothing (off, or without points).</summary>
    public static AutoCurve? Curve(AutomationLane? lane, bool stepped = false, AutoLaw law = AutoLaw.Linear)
    {
        if (lane is not { On: true, Blocks.Count: > 0 }) return null;
        var p = lane.Flat(stepped);
        if (p.Count == 0) return null;
        var sec = new double[p.Count];
        var pos = new float[p.Count];
        AutoShape[]? shapes = null;
        float[]? tension = null;
        for (int i = 0; i < p.Count; i++)
        {
            sec[i] = p[i].Sec;
            pos[i] = (float)p[i].Value;
            if (p[i].Shape == AutoShape.Curve && p[i].Tension == 0) continue;
            (shapes ??= new AutoShape[p.Count])[i] = p[i].Shape;
            (tension ??= new float[p.Count])[i] = (float)p[i].Tension;
        }
        return new AutoCurve(sec, pos, stepped, law, shapes, tension);
    }

    /// <summary>What the track's (or folder's) fader follows; null without a lane for it.</summary>
    public static TrackAutomation? Compile(IAutomated t)
    {
        var volume = Curve(Find(t, AutoTarget.Volume), law: AutoLaw.Fader);
        var pan = Curve(Find(t, AutoTarget.Pan), law: AutoLaw.Pan);
        var mute = Curve(Find(t, AutoTarget.Mute), stepped: true);
        return volume == null && pan == null && mute == null ? null : new TrackAutomation(volume, pan, mute);
    }

    /// <summary>A send's lane by its bus; null without one.</summary>
    public static AutoCurve? SendCurve(IAutomated t, string busId) => Curve(Find(t, AutoTarget.Send(busId)));

    /// <summary>A VST3 effect slot's lanes, or null.</summary>
    public static VstParamCurves? VstCurves(IAutomated t, string slotId) => Plugin(t, l => AutoTarget.VstOf(l) is { } v && v.Slot == slotId ? v.Id : null);

    /// <summary>The instrument's lanes, or null.</summary>
    public static VstParamCurves? InstrumentCurves(Track t) => Plugin(t, AutoTarget.InstrumentOf);

    /// <summary>The native sampler's lanes, or null. It reads them itself, at its voices' control steps.</summary>
    public static SamplerCurves? SamplerCurves(Track t)
    {
        if (t.Sampler == null || t.Automation == null) return null;
        var byKnob = new AutoCurve?[SamplerKnobs.Ids.Length];
        bool any = false;
        foreach (var l in t.Automation)
            if (AutoTarget.SamplerOf(l.Target) is { } knob && Curve(l) is { } c)
            {
                byKnob[(int)knob] = c;
                any = true;
            }
        return any ? new SamplerCurves(byKnob) : null;
    }

    /// <summary>The native granular's lanes, or null. It reads them itself, at its grains and its voices' control steps.</summary>
    public static GranularCurves? GranularCurves(Track t)
    {
        if (t.Granular == null || t.Automation == null) return null;
        var byKnob = new AutoCurve?[GranularKnobs.Ids.Length];
        bool any = false;
        foreach (var l in t.Automation)
            if (AutoTarget.GranularOf(l.Target) is { } knob && Curve(l) is { } c)
            {
                byKnob[(int)knob] = c;
                any = true;
            }
        return any ? new GranularCurves(byKnob) : null;
    }

    static VstParamCurves? Plugin(IAutomated t, Func<string, uint?> idOf)
    {
        if (t.Automation == null) return null;
        var ids = new List<uint>();
        var curves = new List<AutoCurve>();
        foreach (var l in t.Automation)
            if (idOf(l.Target) is { } id && Curve(l) is { } c)
            {
                ids.Add(id);
                curves.Add(c);
            }
        return ids.Count == 0 ? null : new VstParamCurves([.. ids], [.. curves]);
    }

    /// <summary>
    /// A gap closed on the track (<see cref="ClipEditing.DeleteRangeClosing"/>): the points inside go, what follows slides
    /// left with the audio, and each side keeps the value it had at the cut.
    /// </summary>
    public static void CloseGap(IAutomated t, double startSec, double endSec)
    {
        if (t.Automation == null) return;
        double len = endSec - startSec;
        foreach (var l in t.Automation)
        {
            bool stepped = Stepped(t, l.Target);
            var kept = new List<AutomationBlock>();
            foreach (var b in l.Blocks)
            {
                if (b.AtSec < startSec) kept.Add(b.EndSec <= startSec ? b : b.Cut(0, startSec - b.AtSec, stepped));
                if (b.EndSec <= endSec) continue;
                var after = b.AtSec >= endSec ? b : b.Cut(endSec - b.AtSec, b.LengthSec, stepped);
                after.AtSec = Math.Max(0, after.AtSec - len);
                kept.Add(after);
            }
            l.Blocks = kept;
            l.Touch();
        }
    }
}

/// <summary>
/// What a track's native effects are at a time, for the lanes on their knobs. Works on its own copy of the channel, so
/// the project never holds an automated value, and the export can run it on its own thread; each call hands back the
/// slots only if a lane moved, as the engine takes them from the UI (<see cref="TrackDspChain.SetInserts"/>). The same in
/// the window (read every tick) and in the export (<see cref="MixTrackSpec.InsertsAt"/>).
/// </summary>
public sealed class InsertAutomator
{
    /// <summary>A lane's move under this (of the knob's travel) isn't worth new settings.</summary>
    const double Least = 1.0 / 512;
    /// <summary>A reverb's decay, tone and width make a new impulse: these read the lane at this pace, on a grid of the timeline.</summary>
    const double SlowSec = 0.25;

    readonly record struct Lane(InsertSlot Slot, FxParam Param, AutoCurve Curve, bool Slow);

    readonly IReadOnlyList<InsertSlot> slots;
    readonly Lane[] lanes;
    readonly double[] last;
    readonly double rmsDb, bpm;
    readonly Func<InsertSlot, VstBridge?>? live;
    readonly bool bypass, wait;
    readonly Dictionary<string, VstParamCurves> plugins = [];

    InsertAutomator(IReadOnlyList<InsertSlot> slots, Lane[] lanes, IAutomated track, double rmsDb, double bpm, Func<InsertSlot, VstBridge?>? live, bool bypass)
    {
        this.slots = slots;
        this.lanes = lanes;
        this.rmsDb = rmsDb;
        this.bpm = bpm;
        this.live = live;
        this.bypass = bypass;
        wait = live == null;
        last = new double[lanes.Length];
        Array.Fill(last, double.NaN);
        foreach (var s in slots)
            if (s.Type == InsertSlot.VstKind && Automations.VstCurves(track, s.Id) is { } c) plugins[s.Id] = c;
    }

    /// <summary>
    /// For a track with lanes on its native effects; null without any. <paramref name="live"/> as in
    /// <see cref="ChannelDsp.Specs(Channel, double, double, Func{InsertSlot, VstBridge?}?, bool)"/>: without it, the export.
    /// </summary>
    public static InsertAutomator? Create(IAutomated t, double rmsDb, double bpm, Func<InsertSlot, VstBridge?>? live = null, bool bypass = false)
    {
        if (t.Automation == null) return null;
        List<InsertSlot> slots;
        if (t is Track track)
        {
            var channel = ProjectLibrary.Clone(track.Channel);
            channel.Normalize();
            slots = channel.Slots;
        }
        else
        {
            slots = ProjectLibrary.Clone(new List<InsertSlot>(t.AutoSlots));
            InsertSlot.NormalizeList(slots);
        }
        var list = new List<Lane>();
        foreach (var l in t.Automation)
            if (Automations.FxParamOf(slots, l.Target) is { } f && Automations.Curve(l, f.Param.Toggle) is { } c)
                list.Add(new(f.Slot, f.Param, c, f.Slot.Type == InsertSlot.ReverbKind && f.Param.Id is "decay" or "tone" or "width"));
        return list.Count == 0 ? null : new InsertAutomator(slots, [.. list], t, rmsDb, bpm, live, bypass);
    }

    /// <summary>The slots at <paramref name="sec"/> if a lane moved since the last call; with <paramref name="force"/>, always.</summary>
    public InsertSpec[]? At(double sec, bool force = false)
    {
        bool moved = force;
        bool recalc = false;
        for (int i = 0; i < lanes.Length; i++)
        {
            ref readonly var l = ref lanes[i];
            double k = l.Curve.PositionAt(l.Slow ? Math.Floor(sec / SlowSec) * SlowSec : sec);
            if (!(Math.Abs(k - last[i]) >= Least) && !double.IsNaN(last[i])) continue;
            last[i] = k;
            moved = true;
            l.Param.Set(l.Slot, l.Param.Toggle ? (k >= 0.5 ? 1 : 0) : l.Param.FromKnob(k));
            recalc |= l.Slot.Comp is { Preset: not "custom" } && l.Param.Id == "amount";
        }
        if (!moved) return null;
        if (recalc)
            foreach (var l in lanes)
                if (l.Slot.Comp is { Preset: not "custom" } c && l.Param.Id == "amount") ChannelDsp.RecalculateComp(c);
        var specs = ChannelDsp.Specs(slots, rmsDb, bpm, live, bypass);
        for (int i = 0; i < specs.Length; i++)
        {
            if (plugins.TryGetValue(specs[i].Id, out var c) && specs[i].Vst is { } v) specs[i] = specs[i] with { Vst = v with { Curves = c } };
            if (wait) specs[i] = specs[i] with { Wait = true };
        }
        return specs;
    }
}
