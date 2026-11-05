using XtremeDaw.Audio;
using XtremeDaw.Audio.Midi;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtremeDaw.Projects;

/// <summary>
/// What a track holds (native only): takes (<see cref="Audio"/>, the clips of a media) or notes for an instrument
/// (<see cref="Midi"/>, <see cref="Track.MidiClips"/>). A MIDI track never gets a video panel and never records audio:
/// it is for building the backing track the guitar is played over, by hand or from a MIDI keyboard.
/// </summary>
public static class TrackKinds
{
    public const string Audio = "audio", Midi = "midi";

    public static string Normalize(string? kind) => kind == Midi ? Midi : Audio;
}

/// <summary>
/// A VST3 plugin a track uses (native only): the file and the class inside it, its name and maker to show, and its
/// state as the plugin saved it (base64; what its window was left at), so the project sounds the same when it opens.
/// The file stays where it was installed: a project opened on a computer without it says so and plays the test tone.
/// </summary>
public sealed class PluginRef
{
    public string Path { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string? State { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>False when it doesn't say which plugin it is.</summary>
    internal bool Normalize()
    {
        Path = Path?.Trim() ?? "";
        ClassId = ClassId?.Trim() ?? "";
        Name = string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Name.Trim();
        Vendor = Vendor?.Trim() ?? "";
        if (string.IsNullOrEmpty(State)) State = null;
        return Path.Length > 0 && ClassId.Length > 0;
    }

    /// <summary>The same plugin (not the same state).</summary>
    public bool SamePlugin(PluginRef? o) => o != null && string.Equals(o.Path, Path, StringComparison.OrdinalIgnoreCase) && o.ClassId == ClassId;
}

/// <summary>
/// A track's arpeggiator (FL Studio's channel arpeggiator): the keys held become a line in time with the song, heard and
/// recorded as played, so a take keeps exactly what sounded. The notes already written don't go through it.
/// </summary>
public sealed class ArpSettings
{
    public bool On { get; set; } = true;
    /// <summary>"up", "down", "updown", "random" (<see cref="LiveArp.Patterns"/>).</summary>
    public string Pattern { get; set; } = "up";
    /// <summary>Beats per step.</summary>
    public double Step { get; set; } = 0.25;
    public int Octaves { get; set; } = 1;
    /// <summary>How much of a step each note lasts, 0.1..1.</summary>
    public double Gate { get; set; } = 0.8;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        if (!LiveArp.Patterns.Contains(Pattern)) Pattern = "up";
        Step = double.IsFinite(Step) ? Math.Clamp(Step, MidiNote.MinBeats, 2) : 0.25;
        Octaves = Math.Clamp(Octaves, 1, 4);
        Gate = double.IsFinite(Gate) ? Math.Clamp(Gate, 0.1, 1) : 0.8;
    }

    /// <summary>What the engine plays; null when it is off.</summary>
    public LiveArp? Live() => On ? new LiveArp(Array.IndexOf(LiveArp.Patterns, Pattern), Step, Octaves, Gate) : null;
}

/// <summary>
/// A stretch of notes on a MIDI track (native only, asked by the user: clips per track, like Logic or Studio One).
/// It starts at a timeline time in seconds like every clip, but its length and its notes are in beats, so they follow
/// the tempo: at 90 bpm a four-bar clip still holds four bars. Notes past its end are kept but not heard.
/// <para>
/// A clip with a <see cref="PatternId"/> is one use of a pattern (asked by the user, see <see cref="MidiPatterns"/>): its
/// notes are a pass of <see cref="PatternBeats"/> that repeats until the clip ends, and every clip of the same
/// <see cref="PatternPart"/> holds the same notes.
/// </para>
/// </summary>
public sealed class MidiClip
{
    public string Id { get; set; } = Ids.New("mid");
    /// <summary>Timeline time of its first beat.</summary>
    public double AtSec { get; set; }
    /// <summary>How long it is, in beats (four per bar: the project's grid is always 4/4).</summary>
    public double LengthBeats { get; set; } = DefaultBeats;
    public List<MidiNote> Notes { get; set; } = [];
    /// <summary>The pattern this clip belongs to, its name and how long one pass is; null on a clip of its own.</summary>
    public string? PatternId { get; set; }
    public string? PatternName { get; set; }
    public double? PatternBeats { get; set; }
    /// <summary>Which of its pattern's instruments it plays; null = the first one, the only one until another is added.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PatternPart { get; set; }
    /// <summary>
    /// A clip of its own that loops (asked by the user, like GarageBand): its notes are a pass of this many beats that
    /// repeats until the clip ends, as a pattern's do. Null = it plays once; always null on a pattern's clip.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? LoopBeats { get; set; }
    /// <summary>
    /// A name of its own (asked by the user), drawn on the clip; null = none. A pattern's clip goes by its pattern's
    /// name instead (<see cref="MidiEditing.Rename"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Four bars, what a new clip gets.</summary>
    public const double DefaultBeats = 16;
    /// <summary>The shortest clip (a sixteenth) and the longest one (1024 bars).</summary>
    public const double MinBeats = 0.25, MaxBeats = 4096;

    public double LengthSec(double bpm) => LengthBeats * 60 / Math.Max(1, bpm);

    /// <summary>One pass of its notes: the pattern's, its loop's, or the whole clip.</summary>
    public double CellBeats => PatternId != null && PatternBeats is > 0 ? PatternBeats.Value : LoopBeats is > 0 ? LoopBeats.Value : LengthBeats;
    /// <summary>Its notes repeat: a pattern's clip or a looped one.</summary>
    public bool Repeats => PatternId != null || LoopBeats != null;
    /// <summary>How many passes start inside the clip (the last one may be cut short).</summary>
    public int Passes => Math.Max(1, (int)Math.Ceiling(LengthBeats / CellBeats - 1e-9));

    /// <summary>
    /// Where a note sounds in pass <paramref name="pass"/>, in beats from the clip's start, for a pass of
    /// <paramref name="cell"/> beats in a clip of <paramref name="length"/>: cut at the end of the pass and of the clip.
    /// False when it isn't heard. Allocates nothing: the piano roll and the timeline call it while they draw.
    /// </summary>
    public static bool Slice(MidiNote n, int pass, double cell, double length, out double from, out double to)
    {
        double at = pass * cell;
        from = at + n.Beat;
        to = Math.Min(at + Math.Min(n.EndBeat, cell), length);
        return n.Beat < cell && from < length;
    }

    public bool Heard(MidiNote n, int pass, out double from, out double to) => Slice(n, pass, CellBeats, LengthBeats, out from, out to);

    /// <summary>
    /// A copy with a new id and its own notes (duplicate, copy and paste); a pattern's copy stays in the pattern. A copy
    /// doesn't go on from anything: its <see cref="MidiNote.Tie"/>s go, unless <paramref name="ties"/> (a copy left in
    /// the clip's own place) or it is a pattern's, whose notes every clip shares.
    /// </summary>
    public MidiClip Copy(bool ties = false)
    {
        var copy = new MidiClip
        {
            AtSec = AtSec, LengthBeats = LengthBeats, Notes = [.. Notes.Select(n => n.Copy())],
            PatternId = PatternId, PatternName = PatternName, PatternBeats = PatternBeats, PatternPart = PatternPart, LoopBeats = LoopBeats, Name = Name, Extra = Extra,
        };
        if (!ties && PatternId == null)
            foreach (var n in copy.Notes) n.Tie = false;
        return copy;
    }
    public double EndSec(double bpm) => AtSec + LengthSec(bpm);

    internal void Normalize()
    {
        AtSec = double.IsFinite(AtSec) ? Math.Max(0, AtSec) : 0;
        LengthBeats = double.IsFinite(LengthBeats) ? Math.Clamp(LengthBeats, MinBeats, MaxBeats) : DefaultBeats;
        Notes ??= [];
        Notes.RemoveAll(n => n == null);
        foreach (var n in Notes) n.Normalize();
        Notes.Sort(MidiNote.Order);
        Name = ClipEditing.CleanName(Name);
        if (string.IsNullOrWhiteSpace(PatternId))
        {
            PatternId = PatternName = PatternPart = null;
            PatternBeats = null;
            // a loop no longer than the clip is no loop
            LoopBeats = LoopBeats is { } loop && double.IsFinite(loop) && loop >= MinBeats && loop < LengthBeats - 1e-9 ? loop : null;
            return;
        }
        LoopBeats = null;
        if (string.IsNullOrWhiteSpace(PatternPart)) PatternPart = null;
        PatternName = string.IsNullOrWhiteSpace(PatternName) ? "Patrón" : PatternName.Trim();
        PatternBeats = PatternBeats is { } beats && double.IsFinite(beats) ? Math.Clamp(beats, MinBeats, MaxBeats) : LengthBeats;
    }
}

/// <summary>
/// A note of a <see cref="MidiClip"/>: its pitch (MIDI number, 60 = C4, the middle C), where it starts and how long it
/// lasts in beats from the clip's start, and how hard it is played (1..127).
/// </summary>
public sealed class MidiNote
{
    public int Pitch { get; set; } = 60;
    public double Beat { get; set; }
    public double Beats { get; set; } = 1;
    public int Velocity { get; set; } = DefaultVelocity;
    /// <summary>Stays on the roll, not heard.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Muted { get; set; }
    /// <summary>Notes with the same number (not 0) are picked and moved together.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Group { get; set; }
    /// <summary>1..<see cref="Colors"/>, a color of its own on the roll; 0 = the track's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Color { get; set; }
    /// <summary>No attack of its own: what is sounding glides to this pitch during this note (FL's slide).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Slide { get; set; }
    /// <summary>It attacks starting from the note before and glides to its own pitch (<see cref="PortaBeats"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Porta { get; set; }
    /// <summary>-100 (left) to 100 (right), set where the note starts; 0 = as it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Pan { get; set; }
    /// <summary>
    /// It goes on from the note of the same pitch that ends right where it starts, with no attack of its own: the second
    /// half of a note a division cut (<see cref="MidiEditing.Split"/>). With nothing ending there it is a plain note, so
    /// moving a piece away, or removing the other one, gives it its attack back. It can be as short as the cut left it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Tie { get; set; }
    /// <summary>The note-off's velocity, 1..127; 0 = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Release { get; set; }
    /// <summary>CC 1 and CC 74 where the note starts, 1..127; 0 = not sent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ModX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ModY { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public const int DefaultVelocity = 100;
    /// <summary>The shortest note: a 1/64.</summary>
    public const double MinBeats = 1.0 / 16;
    /// <summary>The shortest a <see cref="Tie"/> can be: what a cut left has to last what it lasted.</summary>
    const double MinTieBeats = 1e-6;
    public const int MinPitch = 0, MaxPitch = 127;
    /// <summary>The color groups a note can be in.</summary>
    public const int Colors = 15;
    /// <summary>How long a <see cref="Porta"/> note glides: a 1/16, or its length if it is shorter.</summary>
    public const double PortaBeats = 0.25;

    public double EndBeat => Beat + Beats;

    /// <summary>The order notes are kept in: by start, then from low to high.</summary>
    public static int Order(MidiNote a, MidiNote b) => a.Beat != b.Beat ? a.Beat.CompareTo(b.Beat) : a.Pitch.CompareTo(b.Pitch);

    public MidiNote Copy() => (MidiNote)MemberwiseClone();

    internal void Normalize()
    {
        Pitch = Math.Clamp(Pitch, MinPitch, MaxPitch);
        Beat = double.IsFinite(Beat) ? Math.Max(0, Beat) : 0;
        Beats = double.IsFinite(Beats) ? Math.Clamp(Beats, Tie ? MinTieBeats : MinBeats, MidiClip.MaxBeats) : 1;
        Velocity = Math.Clamp(Velocity, 1, 127);
        Group = Math.Max(0, Group);
        Color = Math.Clamp(Color, 0, Colors);
        Pan = Math.Clamp(Pan, -100, 100);
        Release = Math.Clamp(Release, 0, 127);
        ModX = Math.Clamp(ModX, 0, 127);
        ModY = Math.Clamp(ModY, 0, 127);
    }

    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>"C4", "F#2"… (60 = C4).</summary>
    public static string Name(int pitch) => $"{Names[((pitch % 12) + 12) % 12]}{pitch / 12 - 1}";

    public static bool IsBlack(int pitch) => Names[((pitch % 12) + 12) % 12].Length > 1;

    /// <summary>A pitch from "60", "C4", "c#3" or "Eb2"; null when it isn't one.</summary>
    public static int? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (int.TryParse(s, out int n)) return n is >= MinPitch and <= MaxPitch ? n : null;
        int i = Array.FindIndex(Names, x => x.Length == 1 && char.ToUpperInvariant(s[0]) == x[0]);
        if (i < 0) return null;
        int k = 1;
        if (k < s.Length && s[k] == '#') { i++; k++; }
        else if (k < s.Length && s[k] == 'b') { i--; k++; }
        if (!int.TryParse(s[k..], out int octave)) return null;
        int pitch = (octave + 1) * 12 + i;
        return pitch is >= MinPitch and <= MaxPitch ? pitch : null;
    }
}

/// <summary>
/// The edits of MIDI tracks and clips, for the window and xtlab alike (like <see cref="ClipEditing"/> for takes).
/// Every edit leaves the clip normalized (notes in order, inside their limits).
/// </summary>
public static class MidiEditing
{
    /// <summary>A new MIDI track at the end with the piano icon: "MIDI 1", "MIDI 2"…, or <paramref name="name"/> (its
    /// instrument's), numbered if taken; returns its id.</summary>
    public static string AddTrack(Project p, string? name = null)
    {
        var used = p.Tracks.Select(t => t.Name).ToHashSet();
        string label;
        if (!string.IsNullOrWhiteSpace(name))
        {
            label = name.Trim();
            for (int k = 2; used.Contains(label); k++) label = $"{name.Trim()} {k}";
        }
        else
        {
            int n = 1;
            while (used.Contains($"MIDI {n}")) n++;
            label = $"MIDI {n}";
        }
        var track = new Track { Name = label, Kind = TrackKinds.Midi, Video = false, Icon = "piano" };
        p.Tracks.Add(track);
        return track.Id;
    }

    /// <summary>A new MIDI track right under <paramref name="below"/>, in its folder.</summary>
    public static Track AddTrackBelow(Project p, Track below)
    {
        string made = AddTrack(p);
        var t = p.Tracks.Find(x => x.Id == made)!;
        var seq = TrackFolders.Sequence(p);
        seq.Remove(t);
        TrackFolders.Move(p, t.Id, seq.IndexOf(below) + 1, below.FolderId);
        return t;
    }

    public static (Track Track, MidiClip Clip) Find(Project p, string clipId)
    {
        foreach (var t in p.Tracks)
            if (t.MidiClips?.Find(c => c.Id == clipId) is { } c) return (t, c);
        throw new ArgumentException(Texts.T("err.midi.clipNotFound", "Clip MIDI no encontrado."));
    }

    public static MidiClip? TryFind(Project p, string? clipId) => clipId == null ? null : p.Tracks.SelectMany(t => t.MidiClips ?? []).FirstOrDefault(c => c.Id == clipId);

    /// <summary>
    /// Names a clip (blank takes its name away). A pattern's clip renames its pattern, in every use (decision of the
    /// user), and keeps its old name when the new one is blank.
    /// </summary>
    public static void Rename(Project p, string clipId, string? name)
    {
        var c = Find(p, clipId).Clip;
        if (c.PatternId != null) MidiPatterns.Rename(p, clipId, name ?? "");
        else c.Name = ClipEditing.CleanName(name);
    }

    /// <summary>The name drawn on a clip: its pattern's or its own; null = none.</summary>
    public static string? Label(MidiClip c) => c.PatternId != null ? c.PatternName : c.Name;

    public static MidiClip AddClip(Project p, string trackId, double atSec, double lengthBeats = MidiClip.DefaultBeats)
    {
        var t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "Pista no encontrada."));
        if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Los clips MIDI van en pistas MIDI."));
        var c = new MidiClip { AtSec = ClipEditing.ToSample(Math.Max(0, atSec)), LengthBeats = lengthBeats };
        c.Normalize();
        (t.MidiClips ??= []).Add(c);
        return c;
    }

    /// <summary>Moves a clip to <paramref name="atSec"/> and gives it <paramref name="lengthBeats"/> (its notes go with it).</summary>
    public static void Place(Project p, string clipId, double atSec, double lengthBeats)
    {
        var (_, c) = Find(p, clipId);
        c.AtSec = ClipEditing.ToSample(Math.Max(0, atSec));
        c.LengthBeats = lengthBeats;
        c.Normalize();
    }

    /// <summary>Moves a clip to another MIDI track; false when that track isn't MIDI.</summary>
    public static bool MoveToTrack(Project p, string clipId, string trackId)
    {
        var (from, c) = Find(p, clipId);
        if (p.Tracks.Find(t => t.Id == trackId) is not { IsMidi: true } to) return false;
        if (to == from) return true;
        from.MidiClips?.Remove(c);
        (to.MidiClips ??= []).Add(c);
        return true;
    }

    public static void RemoveClip(Project p, string clipId)
    {
        foreach (var t in p.Tracks) t.MidiClips?.RemoveAll(c => c.Id == clipId);
    }

    /// <summary>
    /// Trims a clip's start to <paramref name="atSec"/> and keeps its end where it is. The notes stay where they sound:
    /// they move by the cut, the ones that end before the new start go, and one that crosses it is cut there. A pattern's
    /// clip can't be trimmed this way: its notes are one pass counted from the clip's start.
    /// </summary>
    public static void TrimStart(Project p, string clipId, double atSec)
    {
        var (track, c) = Find(p, clipId);
        if (track.Locked) throw new ArgumentException(Texts.T("err.clip.trackLocked", "La pista «{0}» está bloqueada.", track.Name));
        if (c.PatternId != null) throw new ArgumentException(Texts.T("err.midi.trimPattern", "Un clip de patrón no se recorta por el inicio: sus notas cuentan desde su principio."));
        double beatSec = 60 / Math.Max(1, p.Settings.Bpm), end = c.AtSec + c.LengthBeats * beatSec;
        double at = ClipEditing.ToSample(Math.Clamp(atSec, 0, end - MidiClip.MinBeats * beatSec));
        double cut = (at - c.AtSec) / beatSec;
        WriteOut(c);
        c.Notes.RemoveAll(n => n.Beat - cut + n.Beats < MidiNote.MinBeats);
        foreach (var n in c.Notes)
        {
            double beat = n.Beat - cut;
            if (beat < 0) { n.Beats += beat; beat = 0; }
            n.Beat = beat;
        }
        c.AtSec = at;
        c.LengthBeats -= cut;
        c.Normalize();
    }

    /// <summary>
    /// Loops a clip of its own up to <paramref name="lengthBeats"/> (the loop handle, like GarageBand): what it held is
    /// one pass, repeated until its new end. A clip that already loops or a pattern's keeps its pass; a length inside
    /// the first pass takes the loop away.
    /// </summary>
    public static void Loop(Project p, string clipId, double lengthBeats)
    {
        var (track, c) = Find(p, clipId);
        if (track.Locked) throw new ArgumentException(Texts.T("err.clip.trackLocked", "La pista «{0}» está bloqueada.", track.Name));
        if (c.PatternId == null) c.LoopBeats ??= c.LengthBeats;
        c.LengthBeats = Math.Clamp(lengthBeats, MidiClip.MinBeats, MidiClip.MaxBeats);
        c.Normalize();
    }

    /// <summary>
    /// A looped clip's pass set to <paramref name="passBeats"/> (the original's edge, asked by the user, #171): every
    /// repeat follows and the clip keeps how many passes it had. Notes past the new pass are kept, not heard.
    /// </summary>
    public static void Pass(Project p, string clipId, double passBeats)
    {
        var (track, c) = Find(p, clipId);
        if (track.Locked) throw new ArgumentException(Texts.T("err.clip.trackLocked", "La pista «{0}» está bloqueada.", track.Name));
        if (c.PatternId != null || c.LoopBeats is not { } pass) return;
        double cell = Math.Clamp(passBeats, MidiClip.MinBeats, MidiClip.MaxBeats);
        c.LengthBeats = Math.Min(MidiClip.MaxBeats, c.LengthBeats * cell / pass);
        c.LoopBeats = cell;
        c.Normalize();
    }

    /// <summary>A looped clip's repeats written out as notes of its own, so it sounds the same without the loop.</summary>
    static void WriteOut(MidiClip c)
    {
        if (c.PatternId != null || c.LoopBeats == null) return;
        List<MidiNote> notes = [];
        for (int pass = 0; pass < c.Passes; pass++)
            foreach (var n in c.Notes)
                if (c.Heard(n, pass, out double from, out double to) && to > from)
                {
                    var copy = n.Copy();
                    copy.Beat = from;
                    copy.Beats = to - from;
                    if (pass > 0) copy.Tie = false;
                    notes.Add(copy);
                }
        c.Notes = notes;
        c.LoopBeats = null;
    }

    /// <summary>Moves a clip to <paramref name="atSec"/>, its length as it is.</summary>
    public static void Move(Project p, string clipId, double atSec)
    {
        var (_, c) = Find(p, clipId);
        c.AtSec = ClipEditing.ToSample(Math.Max(0, atSec));
    }

    /// <summary>Removes a take's clip or a MIDI clip, whichever the id is.</summary>
    public static void RemoveAny(Project p, string clipId)
    {
        if (TryFind(p, clipId) != null) RemoveClip(p, clipId);
        else ClipEditing.RemoveClip(p, clipId);
    }

    /// <summary>
    /// Duplicates takes' clips and MIDI clips together: every copy goes the same time later, right after the block they
    /// all make, each on its own track. A pattern's clip brings the other instruments that start with it. Returns the
    /// copies' ids in the order of <paramref name="ids"/>, those instruments' after them. With the magnet on
    /// (<paramref name="step"/>), the copies go a whole number of its steps later (<see cref="ClipEditing.DuplicateShift"/>).
    /// </summary>
    public static List<string> DuplicateMany(Project p, IReadOnlyList<string> ids, Func<Clip, double?>? lengthOf = null, double step = 0)
    {
        var unique = ids.Distinct().ToList();
        foreach (string id in ids)
            if (TryFind(p, id) is { } picked)
                foreach (var c in MidiPatterns.Together(p.Tracks, picked))
                    if (!unique.Contains(c.Id)) unique.Add(c.Id);
        var midi = unique.Where(id => TryFind(p, id) != null).ToList();
        var takes = unique.Where(id => !midi.Contains(id)).ToList();
        double start = double.MaxValue, end = double.MinValue;
        if (takes.Count > 0) (start, end) = ClipEditing.Span(p, takes.Select(id => ClipEditing.Find(p, id).Clip), lengthOf);
        foreach (var id in midi)
        {
            var c = TryFind(p, id)!;
            start = Math.Min(start, c.AtSec);
            end = Math.Max(end, c.EndSec(p.Settings.Bpm));
        }
        if (start > end) return [];
        double shift = ClipEditing.DuplicateShift(end - start, step);
        var copies = new Dictionary<string, string>();
        var takeCopies = takes.Count > 0 ? ClipEditing.DuplicateMany(p, takes, lengthOf, shift) : [];
        for (int i = 0; i < takes.Count; i++) copies[takes[i]] = takeCopies[i];
        foreach (var id in midi)
        {
            var (t, c) = Find(p, id);
            var copy = c.Copy();
            copy.AtSec = ClipEditing.ToSample(c.AtSec + shift);
            t.MidiClips!.Insert(t.MidiClips.IndexOf(c) + 1, copy);
            copies[id] = copy.Id;
        }
        return [.. unique.Where(copies.ContainsKey).Select(id => copies[id])];
    }

    /// <summary>
    /// Shift+drag on the timeline: leaves a copy of each clip, take or MIDI, exactly where it is, under it, so the clip
    /// that the gesture then moves is the one that keeps its selection.
    /// </summary>
    public static void LeaveCopies(Project p, IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (TryFind(p, id) != null)
            {
                var (t, c) = Find(p, id);
                t.MidiClips!.Insert(t.MidiClips.IndexOf(c), c.Copy(ties: true));
            }
            else
            {
                var (t, c) = ClipEditing.Find(p, id);
                var copy = ClipEditing.Copy(c);
                copy.Id = Ids.New("clp");
                t.Clips.Insert(t.Clips.IndexOf(c), copy);
            }
    }

    /// <summary>Two times this close (seconds, a sample) are the same place: where a cut lands has been rounded to a sample.</summary>
    const double CutSlackSec = 1.0 / 48000;
    /// <summary>In beats: closer than this to a cut is on it.</summary>
    const double NoteSlack = 1e-9;

    /// <summary>Whether a cut at <paramref name="atSec"/> leaves a clip's shortest on each side.</summary>
    public static bool CanSplit(MidiClip c, double atSec, double bpm)
    {
        double beatsPerSec = Math.Max(1, bpm) / 60, least = MidiClip.MinBeats - CutSlackSec * beatsPerSec;
        double beats = (atSec - c.AtSec) * beatsPerSec;
        return beats >= least && c.LengthBeats - beats >= least;
    }

    /// <summary>
    /// Splits a clip at a timeline time into two that sound exactly as the one did: through <see cref="ChannelDsp.Notes"/>
    /// they give the same notes in the same frames, to a plugin, the sampler and the test tone alike. A note the cut goes
    /// through stays in both pieces, the left one up to the cut and the right one from it as a
    /// <see cref="MidiNote.Tie"/>, which lengthens the left one instead of attacking; move the pieces apart and it is a
    /// note again. On a pattern's clip a cut on a seam between passes leaves two clips of the pattern. Anywhere else the
    /// left piece stays in the pattern and the right one, which would have to start in the middle of a pass, becomes a
    /// clip of its own with its repeats written out (as <see cref="MidiPatterns.MakeUnique"/> does): the sound holds and
    /// the pattern's notes aren't touched. The other instruments that start with it (<see cref="MidiPatterns.Together"/>)
    /// are cut too, unless they are locked or the cut doesn't fit them. Returns the right piece's id.
    /// </summary>
    public static string Split(Project p, string clipId, double atSec)
    {
        var (track, c) = Find(p, clipId);
        if (track.Locked) throw new ArgumentException(Texts.T("err.clip.trackLocked", "La pista «{0}» está bloqueada.", track.Name));
        double bpm = p.Settings.Bpm;
        if (!CanSplit(c, atSec, bpm)) throw new ArgumentException(Texts.T("err.midi.splitEdge", "El corte tiene que caer dentro del clip, a más de una semicorchea de los bordes."));
        foreach (var other in MidiPatterns.Together(p.Tracks.Where(t => !t.Locked), c))
            if (CanSplit(other, atSec, bpm)) Cut(p, Find(p, other.Id).Track, other, atSec, tie: true);
        return Cut(p, track, c, atSec, tie: true).Id;
    }

    /// <summary>
    /// Cuts <paramref name="c"/> and hands back the right piece, right after it on the track. Without
    /// <paramref name="tie"/> (what is before the cut is going away) a note the cut goes through attacks again there.
    /// </summary>
    static MidiClip Cut(Project p, Track track, MidiClip c, double atSec, bool tie)
    {
        double beatSec = 60 / Math.Max(1, p.Settings.Bpm);
        double at = ClipEditing.ToSample(atSec), beats = (at - c.AtSec) / beatSec, cell = c.CellBeats, seam = Math.Round(beats / cell) * cell;
        MidiClip right;
        if (c.PatternId != null && Math.Abs(beats - seam) * beatSec <= CutSlackSec)
        {
            beats = seam;
            right = c.Copy();
            right.AtSec = ClipEditing.ToSample(c.AtSec + seam * beatSec);
        }
        else
        {
            right = new MidiClip { AtSec = at, Extra = c.Extra };
            if (c.Repeats)
            {
                for (int pass = 0; pass < c.Passes; pass++)
                    foreach (var n in c.Notes)
                        if (c.Heard(n, pass, out double from, out double to) && to > beats + NoteSlack) right.Notes.Add(Piece(n, from, to, beats, tie));
            }
            else
            {
                // what runs past the clip's end isn't heard, and stays with the piece that has that end
                foreach (var n in c.Notes)
                    if (n.EndBeat > beats + NoteSlack) right.Notes.Add(Piece(n, n.Beat, n.EndBeat, beats, tie));
                c.Notes.RemoveAll(n => n.Beat >= beats - NoteSlack);
                foreach (var n in c.Notes)
                    if (n.EndBeat > beats) n.Beats = beats - n.Beat;
            }
        }
        right.LengthBeats = c.LengthBeats - beats;
        c.LengthBeats = beats;
        c.Normalize();
        right.Normalize();
        track.MidiClips!.Insert(track.MidiClips.IndexOf(c) + 1, right);
        MidiPatterns.Sync(p, c);
        return right;
    }

    /// <summary>What the right piece of a cut at <paramref name="cut"/> keeps of a note that sounds from <paramref name="from"/> to <paramref name="to"/>.</summary>
    static MidiNote Piece(MidiNote n, double from, double to, double cut, bool tie)
    {
        var piece = n.Copy();
        piece.Beat = Math.Max(0, from - cut);
        piece.Beats = to - Math.Max(from, cut);
        if (from < cut - NoteSlack)
        {
            piece.Tie = tie;
            piece.Slide = piece.Porta = false; // those are about how it started
        }
        // a stretch shorter than a note can be still has to last what it lasted (a pass cuts notes short too)
        piece.Tie |= piece.Beats < MidiNote.MinBeats;
        return piece;
    }

    /// <summary>
    /// Takes what is between two timeline times out of a track's MIDI clips, like <see cref="ClipEditing.DeleteRange"/>
    /// does with takes (and calls this): the rest stays where it was, leaving a gap. A clip inside goes whole, and so does
    /// a piece left shorter than <see cref="MidiClip.MinBeats"/>. A note that sticks out of the range keeps what is left
    /// of it: on the right it attacks where the range ends (its start is gone), even after the gap closes. A pattern's
    /// clip is cut as in <see cref="Split"/>, on this track only: the range says which tracks. Returns how many clips changed.
    /// </summary>
    public static int DeleteRange(Project p, Track track, double startSec, double endSec)
    {
        if (track.Locked || track.MidiClips is not { } clips) return 0;
        double bpm = Math.Max(1, p.Settings.Bpm), least = MidiClip.MinBeats - CutSlackSec * bpm / 60;
        int changed = 0;
        foreach (var c in clips.ToList())
        {
            double start = c.AtSec, end = c.EndSec(bpm);
            if (endSec <= start || startSec >= end) continue;
            changed++;
            if ((end - endSec) * bpm / 60 >= least) Cut(p, track, c, endSec, tie: false);
            if ((startSec - start) * bpm / 60 >= least) clips.Remove(Cut(p, track, c, startSec, tie: true));
            else clips.Remove(c);
        }
        return changed;
    }

    /// <summary>A copy right after the clip (or a whole number of the magnet's steps later, <see cref="ClipEditing.DuplicateShift"/>),
    /// on the same track; returns the copy's id.</summary>
    public static string Duplicate(Project p, string clipId, double step = 0)
    {
        var (t, c) = Find(p, clipId);
        var copy = c.Copy();
        copy.AtSec = ClipEditing.ToSample(c.AtSec + ClipEditing.DuplicateShift(c.EndSec(p.Settings.Bpm) - c.AtSec, step));
        (t.MidiClips ??= []).Add(copy);
        return copy.Id;
    }

    /// <summary>Puts <paramref name="notes"/> in the clip in place of the ones it had (the piano roll's edits).</summary>
    public static void SetNotes(Project p, string clipId, IEnumerable<MidiNote> notes)
    {
        var (_, c) = Find(p, clipId);
        c.Notes = [.. notes.Select(n => n.Copy())];
        c.Normalize();
        MidiPatterns.Sync(p, c);
    }

    public static MidiNote AddNote(Project p, string clipId, int pitch, double beat, double beats = 1, int velocity = MidiNote.DefaultVelocity)
    {
        var (_, c) = Find(p, clipId);
        var n = new MidiNote { Pitch = pitch, Beat = beat, Beats = beats, Velocity = velocity };
        c.Notes.Add(n);
        c.Normalize();
        MidiPatterns.Sync(p, c);
        return n;
    }

    /// <summary>
    /// Moves where each note starts to the nearest step of <paramref name="gridBeats"/> (the piano roll's magnet) on
    /// the song's grid, not the clip's (a dotted step starts over at each bar): a clip that doesn't start on a beat still lands its notes on the beats. The
    /// length stays. <paramref name="which"/> = the notes to move (null = all). Returns how many moved; a grid of 0
    /// (the magnet off) moves none.
    /// </summary>
    public static int Quantize(IList<MidiNote> notes, double clipAtSec, double bpm, double gridBeats, IReadOnlyCollection<MidiNote>? which = null)
    {
        if (gridBeats <= 0) return 0;
        double clipBeat = clipAtSec * Math.Max(1, bpm) / 60;
        int moved = 0;
        foreach (var n in notes)
        {
            if (which != null && !which.Contains(n)) continue;
            double song = clipBeat + n.Beat;
            double snapped = ClipEditing.Snap(song, new SnapGrid(gridBeats, Bar: Subdivisions.BarBeats), false);
            // a note pulled before the clip's start stays at its start
            double beat = Math.Max(0, snapped - clipBeat);
            if (Math.Abs(beat - n.Beat) < 1e-9) continue;
            n.Beat = beat;
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Takes the notes out of the scale to its nearest note. Only <paramref name="which"/> (null = all). Returns how many moved.
    /// </summary>
    public static int FitScale(IList<MidiNote> notes, MusicScale scale, IReadOnlyCollection<MidiNote>? which = null)
    {
        int moved = 0;
        foreach (var n in notes)
        {
            if (which != null && !which.Contains(n) || scale.Contains(n.Pitch)) continue;
            int pitch = scale.Nearest(n.Pitch);
            if (pitch is < MidiNote.MinPitch or > MidiNote.MaxPitch) continue;
            n.Pitch = pitch;
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Scales the velocities around their average (asked by the user): 100% leaves them, under it they get closer
    /// (0% = all on the average, an even take), over it further apart (more dynamics). Only <paramref name="which"/>
    /// (null = all), kept in 1..127. Returns how many changed.
    /// </summary>
    public static int ScaleVelocity(IList<MidiNote> notes, double percent, IReadOnlyCollection<MidiNote>? which = null)
    {
        var set = notes.Where(n => which == null || which.Contains(n)).ToList();
        if (set.Count == 0) return 0;
        double mean = set.Average(n => n.Velocity), k = Math.Max(0, percent) / 100;
        int changed = 0;
        foreach (var n in set)
        {
            int v = Math.Clamp((int)Math.Round(mean + (n.Velocity - mean) * k), 1, 127);
            if (v == n.Velocity) continue;
            n.Velocity = v;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Humanizes the velocities (asked by the user): each note moves at random up to <paramref name="amount"/> up or
    /// down, kept in 1..127, so a written part does not sound like a machine. Only <paramref name="which"/> (null = all).
    /// Returns how many changed.
    /// </summary>
    public static int HumanizeVelocity(IList<MidiNote> notes, int amount, Random random, IReadOnlyCollection<MidiNote>? which = null)
    {
        amount = Math.Clamp(amount, 0, 127);
        if (amount == 0) return 0;
        int changed = 0;
        foreach (var n in notes)
        {
            if (which != null && !which.Contains(n)) continue;
            int v = Math.Clamp(n.Velocity + random.Next(-amount, amount + 1), 1, 127);
            if (v == n.Velocity) continue;
            n.Velocity = v;
            changed++;
        }
        return changed;
    }

    /// <summary>Quantizes every note of a clip (<see cref="Quantize(IList{MidiNote}, double, double, double, IReadOnlyCollection{MidiNote}?)"/>).</summary>
    public static int Quantize(Project p, string clipId, double gridBeats)
    {
        var (_, c) = Find(p, clipId);
        int moved = Quantize(c.Notes, c.AtSec, p.Settings.Bpm, gridBeats);
        c.Normalize();
        MidiPatterns.Sync(p, c);
        return moved;
    }

    /// <summary>
    /// A take's notes in seconds, ready for <see cref="AddRecorded"/>: from the bar where it began (<paramref name="startSec"/>,
    /// the count-in's notes start there) to <paramref name="endSec"/>, where a key still held ends.
    /// </summary>
    public static List<(double Sec, double LengthSec, int Pitch, int Velocity)> TakeNotes(Project p, Audio.Midi.MidiTake take, double startSec, double endSec, int sampleRate)
    {
        double barSec = 4 * 60 / Math.Max(1, p.Settings.Bpm);
        double barStart = Math.Floor(startSec / barSec + 1e-9) * barSec;
        double sr = sampleRate;
        return take.Notes((long)Math.Round(barStart * sr), (long)Math.Round(endSec * sr)).Select(n => (n.Start / sr, n.Length / sr, n.Pitch, n.Velocity)).ToList();
    }

    /// <summary>
    /// A clip made from notes played on a keyboard (start and length in seconds on the timeline, pitch, velocity), on
    /// a MIDI track: it starts on the bar where the take began (<paramref name="fromSec"/>, the count-in's notes come
    /// in there) and ends on the bar after the take (<paramref name="toSec"/>), so it lines up with the song. With
    /// <paramref name="gridBeats"/> &gt; 0 the notes are quantized to it. Null when nothing was played.
    /// <paramref name="clipId"/> = the id its take's journal has, to know later whether the project got it.
    /// </summary>
    public static MidiClip? AddRecorded(Project p, string trackId, double fromSec, double toSec,
        IReadOnlyList<(double Sec, double LengthSec, int Pitch, int Velocity)> played, double gridBeats = 0, string? clipId = null)
    {
        if (played.Count == 0) return null;
        var t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "Pista no encontrada."));
        if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Los clips MIDI van en pistas MIDI."));
        double bpm = Math.Max(1, p.Settings.Bpm);
        double barSec = 4 * 60 / bpm;
        double start = Math.Floor(Math.Max(0, fromSec) / barSec + 1e-9) * barSec;
        double end = Math.Max(start + barSec, Math.Ceiling(Math.Max(toSec, played.Max(x => x.Sec + x.LengthSec)) / barSec - 1e-9) * barSec);
        var clip = new MidiClip { AtSec = ClipEditing.ToSample(start), LengthBeats = (end - start) / 60 * bpm };
        if (clipId != null) clip.Id = clipId;
        foreach (var (sec, len, pitch, velocity) in played)
            clip.Notes.Add(new MidiNote
            {
                Pitch = pitch,
                Beat = Math.Max(0, sec - clip.AtSec) / 60 * bpm,
                Beats = Math.Max(MidiNote.MinBeats, len / 60 * bpm),
                Velocity = velocity,
            });
        if (gridBeats > 0) Quantize(clip.Notes, clip.AtSec, bpm, gridBeats);
        clip.Normalize();
        (t.MidiClips ??= []).Add(clip);
        return clip;
    }

    /// <summary>
    /// A clip made from the notes taken out of a take's clip (<see cref="AudioToMidi"/>, "Extraer MIDI"): it covers the
    /// same stretch of the timeline (<paramref name="fromSec"/>..<paramref name="toSec"/>) and its notes, in seconds
    /// from <paramref name="fromSec"/>, land where they were heard. It goes on <paramref name="trackId"/>, a MIDI track,
    /// or (null) on a new one right under <paramref name="belowTrackId"/>, in its folder, named
    /// <paramref name="name"/>. With <paramref name="gridBeats"/> &gt; 0 the notes are quantized to it. Returns the
    /// track and the clip.
    /// </summary>
    public static (string TrackId, MidiClip Clip) AddExtracted(Project p, string? trackId, string belowTrackId, double fromSec, double toSec,
        IReadOnlyList<AudioToMidi.Note> notes, string? name = null, double gridBeats = 0)
    {
        var below = p.Tracks.Find(x => x.Id == belowTrackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "Pista no encontrada."));
        Track t;
        if (trackId != null)
        {
            t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "Pista no encontrada."));
            if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Los clips MIDI van en pistas MIDI."));
        }
        else
        {
            t = AddTrackBelow(p, below);
            if (!string.IsNullOrWhiteSpace(name) && p.Tracks.All(x => x.Name != name)) t.Name = name;
        }
        double bpm = Math.Max(1, p.Settings.Bpm);
        var clip = new MidiClip { AtSec = ClipEditing.ToSample(Math.Max(0, fromSec)) };
        double shift = fromSec - clip.AtSec;
        clip.LengthBeats = Math.Max(toSec - clip.AtSec, notes.Select(n => n.EndSec + shift).DefaultIfEmpty(0).Max()) / 60 * bpm;
        foreach (var n in notes)
            clip.Notes.Add(new MidiNote
            {
                Pitch = n.Pitch,
                Beat = Math.Max(0, n.StartSec + shift) / 60 * bpm,
                Beats = Math.Max(MidiNote.MinBeats, (n.EndSec - n.StartSec) / 60 * bpm),
                Velocity = n.Velocity,
            });
        if (gridBeats > 0) Quantize(clip.Notes, clip.AtSec, bpm, gridBeats);
        clip.Normalize();
        (t.MidiClips ??= []).Add(clip);
        return (t.Id, clip);
    }

    /// <summary>The end of the last MIDI clip of the project, in seconds (0 without any).</summary>
    public static double EndSec(Project p) =>
        p.Tracks.SelectMany(t => t.MidiClips ?? []).Select(c => c.EndSec(p.Settings.Bpm)).DefaultIfEmpty(0).Max();
}

/// <summary>
/// Patterns (asked by the user, like FL Studio's but per track): clips linked to each other. Editing the notes of one
/// changes them all, and a clip longer than a pass repeats it. A pattern has no list of its own in the project: it is
/// the clips that share a <see cref="MidiClip.PatternId"/>, each with a full copy of the notes, so every reader of
/// <see cref="MidiClip.Notes"/> just works, an older version still opens the project, and removing the last clip
/// leaves nothing behind. Whatever changes the shared part goes through <see cref="Sync"/>.
/// <para>
/// A pattern can hold several instruments (asked by the user, 2026-11-02): each is a part
/// (<see cref="MidiClip.PatternPart"/>) with its own notes, played by the MIDI track its clips are on, so it keeps its
/// channel, effects and automation and the engine knows nothing of it. Name and pass are the pattern's; the notes, the
/// part's. Clips of different parts starting together are one use of the pattern (<see cref="Together"/>).
/// </para>
/// </summary>
public static class MidiPatterns
{
    public sealed record Info(string Id, string Name, double Beats, int Clips, int Parts);

    /// <summary>One instrument of a pattern: its part, the track that plays it and a clip of it.</summary>
    public sealed record Part(string? Id, Track Track, MidiClip Clip);

    /// <summary>Starts this close (seconds) make one use of a pattern.</summary>
    public const double SameStart = 1e-4;

    /// <summary>The lengths offered for a pass, in bars.</summary>
    public static readonly int[] Bars = [1, 2, 4, 8, 16];

    static IEnumerable<MidiClip> AllClips(Project p) => p.Tracks.SelectMany(t => t.MidiClips ?? []);

    public static IEnumerable<MidiClip> Clips(Project p, string patternId) => AllClips(p).Where(c => c.PatternId == patternId);

    /// <summary>The project's patterns, in the order they first appear.</summary>
    public static List<Info> All(Project p) =>
        [.. AllClips(p).Where(c => c.PatternId != null).GroupBy(c => c.PatternId!).Select(g =>
            new Info(g.Key, g.First().PatternName!, g.First().CellBeats, g.Count(), g.Select(c => c.PatternPart).Distinct().Count()))];

    /// <summary>A pattern's instruments, in the order they first appear; a part on several tracks counts by the first.</summary>
    public static List<Part> Parts(Project p, string patternId)
    {
        List<Part> parts = [];
        foreach (var t in p.Tracks)
            foreach (var c in t.MidiClips ?? [])
                if (c.PatternId == patternId && parts.All(x => x.Id != c.PatternPart)) parts.Add(new Part(c.PatternPart, t, c));
        return parts;
    }

    /// <summary>The other parts' clips that start with <paramref name="clip"/>: they move and repeat with it. Empty on most clips.</summary>
    public static List<MidiClip> Together(IEnumerable<Track> tracks, MidiClip clip)
    {
        if (clip.PatternId == null) return [];
        return [.. tracks.SelectMany(t => t.MidiClips ?? []).Where(c => c.PatternId == clip.PatternId && c.PatternPart != clip.PatternPart && Math.Abs(c.AtSec - clip.AtSec) < SameStart)];
    }

    static MidiClip Clip(Project p, string clipId) => MidiEditing.Find(p, clipId).Clip;

    static MidiClip InPattern(Project p, string clipId) =>
        Clip(p, clipId) is { PatternId: not null } c ? c : throw new ArgumentException(Texts.T("err.midi.notPattern", "Ese clip no es un patrón."));

    /// <summary>Makes a clip the first use of a new pattern ("Patrón 1", "Patrón 2"…), a pass as long as the clip.</summary>
    public static MidiClip Create(Project p, string clipId)
    {
        var c = Clip(p, clipId);
        if (c.PatternId != null) return c;
        var used = All(p).Select(x => x.Name).ToHashSet();
        int n = 1;
        while (used.Contains($"Patrón {n}")) n++;
        c.PatternId = Ids.New("pat");
        c.PatternName = $"Patrón {n}";
        c.PatternBeats = c.CellBeats;
        c.LoopBeats = null;
        return c;
    }

    /// <summary>Hands <paramref name="source"/>'s name and pass to the rest of its pattern, and its notes to the rest of its part.</summary>
    public static void Sync(Project p, MidiClip source)
    {
        if (source.PatternId == null) return;
        foreach (var c in Clips(p, source.PatternId))
            if (c != source) CopyShared(source, c);
    }

    static void CopyShared(MidiClip from, MidiClip to)
    {
        if (from.PatternPart == to.PatternPart) to.Notes = [.. from.Notes.Select(n => n.Copy())];
        to.PatternName = from.PatternName;
        to.PatternBeats = from.PatternBeats;
    }

    /// <summary>A clip that just came into the project (a paste) takes what its pattern holds now: the block may be older.</summary>
    public static void Adopt(Project p, MidiClip clip)
    {
        if (clip.PatternId == null) return;
        var others = Clips(p, clip.PatternId).Where(c => c != clip).ToList();
        if ((others.Find(c => c.PatternPart == clip.PatternPart) ?? others.FirstOrDefault()) is { } current) CopyShared(current, clip);
    }

    /// <summary>
    /// A new instrument in the pattern, played by <paramref name="trackId"/>: an empty clip beside each clip of
    /// <paramref name="clipId"/>'s part, so every use of the pattern gets it. Returns the one beside that clip.
    /// </summary>
    public static MidiClip AddPart(Project p, string clipId, string trackId)
    {
        var source = InPattern(p, clipId);
        var t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "Pista no encontrada."));
        if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Los clips MIDI van en pistas MIDI."));
        string part = Ids.New("prt");
        MidiClip made = null!;
        foreach (var c in Clips(p, source.PatternId!).Where(c => c.PatternPart == source.PatternPart).ToList())
        {
            var clip = new MidiClip
            {
                AtSec = c.AtSec, LengthBeats = c.LengthBeats, PatternId = c.PatternId, PatternName = c.PatternName,
                PatternBeats = c.PatternBeats, PatternPart = part,
            };
            (t.MidiClips ??= []).Add(clip);
            if (c == source) made = clip;
        }
        return made;
    }

    /// <summary>Takes an instrument out of the pattern with all its clips; says how many there were (0 on its only instrument).</summary>
    public static int RemovePart(Project p, string clipId)
    {
        var c = InPattern(p, clipId);
        string? pattern = c.PatternId, part = c.PatternPart;
        if (Parts(p, pattern!).Count < 2) return 0;
        return p.Tracks.Sum(t => t.MidiClips?.RemoveAll(x => x.PatternId == pattern && x.PatternPart == part) ?? 0);
    }

    public static void Rename(Project p, string clipId, string name)
    {
        var c = InPattern(p, clipId);
        c.PatternName = string.IsNullOrWhiteSpace(name) ? c.PatternName : name.Trim();
        Sync(p, c);
    }

    /// <summary>
    /// Changes how long a pass is. A clip that held exactly one pass keeps holding one; a longer one repeats it more
    /// or less often. Notes past the new pass stay, not heard, in case it grows back.
    /// </summary>
    public static void SetBeats(Project p, string clipId, double beats)
    {
        var source = InPattern(p, clipId);
        double old = source.CellBeats;
        beats = Math.Clamp(beats, MidiClip.MinBeats, MidiClip.MaxBeats);
        foreach (var c in Clips(p, source.PatternId!))
        {
            if (Math.Abs(c.LengthBeats - old) < 1e-9) c.LengthBeats = beats;
            c.PatternBeats = beats;
        }
    }

    /// <summary>
    /// One more use of a pattern, a pass long: on <paramref name="trackId"/>, a MIDI track, or, with several instruments,
    /// each on its own track. Returns the clip on that track, or the first.
    /// </summary>
    public static MidiClip Insert(Project p, string trackId, string patternId, double atSec)
    {
        var t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "Pista no encontrada."));
        if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Los clips MIDI van en pistas MIDI."));
        var parts = Parts(p, patternId);
        if (parts.Count == 0) throw new ArgumentException(Texts.T("err.midi.clipNotFound", "Clip MIDI no encontrado."));
        MidiClip? made = null;
        foreach (var part in parts)
        {
            var c = part.Clip.Copy();
            c.AtSec = ClipEditing.ToSample(Math.Max(0, atSec));
            c.LengthBeats = c.CellBeats;
            c.Normalize();
            var on = parts.Count == 1 ? t : part.Track;
            (on.MidiClips ??= []).Add(c);
            if (made == null || on == t) made = c;
        }
        return made!;
    }

    /// <summary>Takes a clip out of its pattern: it keeps sounding the same, its repeats written out as notes of its own.</summary>
    public static void MakeUnique(Project p, string clipId)
    {
        var c = InPattern(p, clipId);
        var notes = new List<MidiNote>();
        for (int pass = 0; pass < c.Passes; pass++)
            foreach (var n in c.Notes)
            {
                if (!c.Heard(n, pass, out double from, out double to)) continue;
                var copy = n.Copy();
                copy.Beat = from;
                copy.Beats = to - from;
                notes.Add(copy);
            }
        c.Notes = notes;
        c.PatternId = null;
        c.Normalize();
    }

    /// <summary>Ends a pattern keeping its clips: each one stays where it is, sounding the same, with notes of its own.</summary>
    public static void Dissolve(Project p, string patternId)
    {
        foreach (var c in Clips(p, patternId).ToList()) MakeUnique(p, c.Id);
    }

    /// <summary>Removes a pattern with all its clips, on every track; says how many there were.</summary>
    public static int Remove(Project p, string patternId) => p.Tracks.Sum(t => t.MidiClips?.RemoveAll(c => c.PatternId == patternId) ?? 0);
}
