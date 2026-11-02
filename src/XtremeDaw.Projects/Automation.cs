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
    /// <summary>The curve of the stretch from here to the next point, -1..1 (<see cref="AutoCurve.Shape"/>); 0 = straight.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Curve { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
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
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public double EndSec => AtSec + LengthSec;

    internal void Normalize()
    {
        if (string.IsNullOrEmpty(Id)) Id = Ids.New("atb");
        AtSec = double.IsFinite(AtSec) ? Math.Max(0, AtSec) : 0;
        LengthSec = double.IsFinite(LengthSec) ? Math.Max(MinSec, LengthSec) : MinSec;
        Points ??= [];
        Points.RemoveAll(p => p == null || !double.IsFinite(p.Sec) || !double.IsFinite(p.Value));
        foreach (var p in Points)
        {
            p.Sec = Math.Max(0, p.Sec);
            p.Value = Math.Clamp(p.Value, 0, 1);
            p.Curve = double.IsFinite(p.Curve) ? Math.Clamp(p.Curve, -1, 1) : 0;
        }
        Points.Sort((a, b) => a.Sec.CompareTo(b.Sec));
    }

    /// <summary>A copy with a new id at another time (the same points).</summary>
    public AutomationBlock Copy(double atSec) => new()
    {
        AtSec = Math.Max(0, atSec),
        LengthSec = LengthSec,
        Points = [.. Points.Select(p => new AutomationPoint { Sec = p.Sec, Value = p.Value, Curve = p.Curve, Extra = p.Extra })],
    };

    /// <summary>The control's position at a time inside the block (relative): it holds its first and last point.</summary>
    public double PositionAt(double sec, bool stepped) => Automations.PositionAt(Points, sec, stepped);

    /// <summary>
    /// The piece from <paramref name="from"/> to <paramref name="to"/> (relative) as a block of its own, sounding the same:
    /// a point on each edge, and a bent stretch the edge cuts goes on as short straight lines.
    /// </summary>
    public AutomationBlock Cut(double from, double to, bool stepped)
    {
        var piece = new AutomationBlock { AtSec = AtSec + from, LengthSec = Math.Max(MinSec, to - from) };
        void Add(double sec, double curve = 0) => piece.Points.Add(new() { Sec = sec - from, Value = PositionAt(sec, stepped), Curve = curve });
        void Between(double a, double b)
        {
            for (int k = 1; k < CutSteps; k++) Add(a + (b - a) * k / CutSteps);
        }
        var inside = Points.Where(p => p.Sec > from && p.Sec < to).ToList();
        double first = inside.Count > 0 ? inside[0].Sec : to;
        Add(from);
        if (!stepped && Points.FindLast(p => p.Sec <= from) is { Curve: not 0 }) Between(from, first);
        for (int i = 0; i < inside.Count; i++)
        {
            var p = inside[i];
            bool last = i == inside.Count - 1;
            // the stretch the end cuts goes as lines; the others keep their curve
            bool runsOver = last && !stepped && p.Curve != 0 && Points.Exists(x => x.Sec >= to);
            piece.Points.Add(new() { Sec = p.Sec - from, Value = p.Value, Curve = runsOver ? 0 : p.Curve, Extra = p.Extra });
            if (runsOver) Between(p.Sec, to);
        }
        Add(to);
        return piece;
    }

    const int CutSteps = 16;
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
    /// <summary>Its own height in the timeline, as a multiple of <see cref="DefaultHeight"/>; null = 1.</summary>
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
                flat.Add(new() { Sec = b.AtSec + p.Sec, Value = p.Value, Curve = p.Curve });
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
/// The names of what a lane can drive, as saved in <see cref="AutomationLane.Target"/>: <c>volume</c>, <c>pan</c>,
/// <c>mute</c>, <c>send:&lt;bus&gt;</c>, <c>fx:&lt;slot&gt;:&lt;param&gt;</c> (a native effect's <see cref="FxParam"/>, its
/// ON or an EQ band's knob), <c>vst:&lt;slot&gt;:&lt;id&gt;</c> (a VST3 effect's parameter) and <c>inst:&lt;id&gt;</c> (the
/// MIDI track's instrument). Ids in the project, so a slot moved or renamed keeps its lanes.
/// </summary>
public static class AutoTarget
{
    public const string Volume = "volume", Pan = "pan", Mute = "mute";
    const string SendPrefix = "send:", FxPrefix = "fx:", VstPrefix = "vst:", InstPrefix = "inst:";

    public static string Send(string busId) => SendPrefix + busId;
    public static string Fx(string slotId, string paramId) => $"{FxPrefix}{slotId}:{paramId}";
    public static string Vst(string slotId, uint id) => $"{VstPrefix}{slotId}:{id}";
    public static string Instrument(uint id) => InstPrefix + id;

    public static string? BusOf(string target) => target.StartsWith(SendPrefix, StringComparison.Ordinal) ? target[SendPrefix.Length..] : null;

    /// <summary>A native effect's target as its slot and parameter.</summary>
    public static (string Slot, string Param)? FxOf(string target) => Split(target, FxPrefix);

    /// <summary>A VST3 effect's target as its slot and parameter id.</summary>
    public static (string Slot, uint Id)? VstOf(string target) =>
        Split(target, VstPrefix) is { } s && uint.TryParse(s.Tail, out uint id) ? (s.First, id) : null;

    public static uint? InstrumentOf(string target) =>
        target.StartsWith(InstPrefix, StringComparison.Ordinal) && uint.TryParse(target.AsSpan(InstPrefix.Length), out uint id) ? id : null;

    static (string First, string Tail)? Split(string target, string prefix)
    {
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int at = target.IndexOf(':', prefix.Length);
        return at < 0 ? null : (target[prefix.Length..at], target[(at + 1)..]);
    }
}

/// <summary>
/// The edits of a track's lanes and their reading: one place for the window, the export and wtlab. A control's position
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

    public static AutomationLane? Find(Track t, string target)
    {
        if (t.Automation is not { } list) return null;
        for (int i = 0; i < list.Count; i++) if (list[i].Target == target) return list[i]; // read every tick: no closure
        return null;
    }

    public static AutomationLane? ById(Track t, string id) => t.Automation?.Find(l => l.Id == id);

    public static AutomationBlock? BlockById(AutomationLane lane, string id) => lane.Blocks.Find(b => b.Id == id);

    /// <summary>
    /// A lane for <paramref name="target"/>: the one it has, shown, or a new one with a block over <paramref name="lengthSec"/>
    /// holding <paramref name="position"/>, so nothing changes until a point moves.
    /// </summary>
    public static AutomationLane Show(Track t, string target, double position, string? name = null, double lengthSec = 0)
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

    public static void Remove(Track t, string id)
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
        return span <= 0 ? p[hi].Value : p[lo].Value + (p[hi].Value - p[lo].Value) * AutoCurve.Shape((sec - p[lo].Sec) / span, p[lo].Curve);
    }

    /// <summary>A switch: it holds each point until the next one, and only 0 and 1 mean anything.</summary>
    public static bool Stepped(Track t, string target) =>
        target == AutoTarget.Mute || FxParamOf(t.Channel.Slots, target) is { Param.Toggle: true };

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
    static Send? SendOf(Channel c, string bus) => c.Sends.Find(s => s.BusId == bus);

    /// <summary>Whether the lane still has something to drive.</summary>
    public static bool Exists(Track t, string target)
    {
        if (target is AutoTarget.Volume or AutoTarget.Pan or AutoTarget.Mute) return true;
        if (AutoTarget.BusOf(target) is { } bus) return SendOf(t.Channel, bus) != null;
        if (AutoTarget.VstOf(target) is { } v) return t.Channel.Slots.Exists(s => s.Id == v.Slot && s.Type == InsertSlot.VstKind);
        if (AutoTarget.InstrumentOf(target) != null) return t.Instrument != null;
        return FxParamOf(t.Channel.Slots, target) != null;
    }

    /// <summary>Where the control is now, for a new lane's first point. A plugin's parameter starts from what the caller knows.</summary>
    public static double Position(Track t, string target)
    {
        var c = t.Channel;
        if (target == AutoTarget.Volume) return Math.Sqrt(Math.Max(0, c.Volume) / 1.5);
        if (target == AutoTarget.Pan) return (c.Pan + 1) / 2;
        if (target == AutoTarget.Mute) return c.Muted ? 1 : 0;
        if (AutoTarget.BusOf(target) is { } bus) return SendOf(c, bus)?.Level ?? 0;
        return FxParamOf(c.Slots, target) is { } f ? f.Param.ToKnob(f.Param.Get(f.Slot)) : 0.5;
    }

    /// <summary>The lane as the engine reads it; null when it does nothing (off, or without points).</summary>
    public static AutoCurve? Curve(AutomationLane? lane, bool stepped = false, AutoLaw law = AutoLaw.Linear)
    {
        if (lane is not { On: true, Blocks.Count: > 0 }) return null;
        var p = lane.Flat(stepped);
        if (p.Count == 0) return null;
        var sec = new double[p.Count];
        var pos = new float[p.Count];
        float[]? bend = null;
        for (int i = 0; i < p.Count; i++)
        {
            sec[i] = p[i].Sec;
            pos[i] = (float)p[i].Value;
            if (p[i].Curve != 0) (bend ??= new float[p.Count])[i] = (float)p[i].Curve;
        }
        return new AutoCurve(sec, pos, stepped, law, bend);
    }

    /// <summary>What the track's fader follows; null without a lane for it.</summary>
    public static TrackAutomation? Compile(Track t)
    {
        var volume = Curve(Find(t, AutoTarget.Volume), law: AutoLaw.Fader);
        var pan = Curve(Find(t, AutoTarget.Pan), law: AutoLaw.Pan);
        var mute = Curve(Find(t, AutoTarget.Mute), stepped: true);
        return volume == null && pan == null && mute == null ? null : new TrackAutomation(volume, pan, mute);
    }

    /// <summary>A send's lane by its bus; null without one.</summary>
    public static AutoCurve? SendCurve(Track t, string busId) => Curve(Find(t, AutoTarget.Send(busId)));

    /// <summary>A VST3 effect slot's lanes, or null.</summary>
    public static VstParamCurves? VstCurves(Track t, string slotId) => Plugin(t, l => AutoTarget.VstOf(l) is { } v && v.Slot == slotId ? v.Id : null);

    /// <summary>The instrument's lanes, or null.</summary>
    public static VstParamCurves? InstrumentCurves(Track t) => Plugin(t, AutoTarget.InstrumentOf);

    static VstParamCurves? Plugin(Track t, Func<string, uint?> idOf)
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
    public static void CloseGap(Track t, double startSec, double endSec)
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

    readonly Channel channel;
    readonly Lane[] lanes;
    readonly double[] last;
    readonly double rmsDb, bpm;
    readonly Func<InsertSlot, VstBridge?>? live;
    readonly bool bypass, wait;
    readonly Dictionary<string, VstParamCurves> plugins = [];

    InsertAutomator(Channel channel, Lane[] lanes, Track track, double rmsDb, double bpm, Func<InsertSlot, VstBridge?>? live, bool bypass)
    {
        this.channel = channel;
        this.lanes = lanes;
        this.rmsDb = rmsDb;
        this.bpm = bpm;
        this.live = live;
        this.bypass = bypass;
        wait = live == null;
        last = new double[lanes.Length];
        Array.Fill(last, double.NaN);
        foreach (var s in channel.Slots)
            if (s.Type == InsertSlot.VstKind && Automations.VstCurves(track, s.Id) is { } c) plugins[s.Id] = c;
    }

    /// <summary>
    /// For a track with lanes on its native effects; null without any. <paramref name="live"/> as in
    /// <see cref="ChannelDsp.Specs(Channel, double, double, Func{InsertSlot, VstBridge?}?, bool)"/>: without it, the export.
    /// </summary>
    public static InsertAutomator? Create(Track t, double rmsDb, double bpm, Func<InsertSlot, VstBridge?>? live = null, bool bypass = false)
    {
        if (t.Automation == null) return null;
        var channel = ProjectLibrary.Clone(t.Channel);
        channel.Normalize();
        var slots = channel.Slots;
        var list = new List<Lane>();
        foreach (var l in t.Automation)
            if (Automations.FxParamOf(slots, l.Target) is { } f && Automations.Curve(l, f.Param.Toggle) is { } c)
                list.Add(new(f.Slot, f.Param, c, f.Slot.Type == InsertSlot.ReverbKind && f.Param.Id is "decay" or "tone" or "width"));
        return list.Count == 0 ? null : new InsertAutomator(channel, [.. list], t, rmsDb, bpm, live, bypass);
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
        var specs = ChannelDsp.Specs(channel, rmsDb, bpm, live, bypass);
        for (int i = 0; i < specs.Length; i++)
        {
            if (plugins.TryGetValue(specs[i].Id, out var c) && specs[i].Vst is { } v) specs[i] = specs[i] with { Vst = v with { Curves = c } };
            if (wait) specs[i] = specs[i] with { Wait = true };
        }
        return specs;
    }
}
