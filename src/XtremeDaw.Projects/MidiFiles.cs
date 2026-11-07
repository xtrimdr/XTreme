using NAudio.Midi;
using XtremeDaw.Audio;

namespace XtremeDaw.Projects;

/// <summary>
/// A standard MIDI file as the project takes it: its first tempo (null = it has none), its tracks with notes and its
/// tempo and meter changes as a map from its start (#214; null = it has neither).
/// </summary>
public sealed record MidiFileData(double? Bpm, List<MidiFileTrack> Tracks, TempoMap? Map = null);

/// <summary>
/// A track of a MIDI file: notes in beats from its start, its length in whole bars, the GM program its channel asks for
/// (null = none said) and whether it is on channel 10, the drums.
/// </summary>
public sealed record MidiFileTrack(string Name, List<MidiNote> Notes, double LengthBeats, int? Program = null, bool Drums = false);

/// <summary>
/// Standard MIDI files in and out, through NAudio. Notes are kept in beats (ticks over ticks per quarter), never in
/// seconds, so an imported file follows the project's tempo. The pairing of note-ons and note-offs is done here and not
/// taken from NAudio's, which drops the end of a note that never closes and has no room for the note-off's velocity.
/// </summary>
public static class MidiFiles
{
    /// <summary>Ticks per quarter of what we write: divides evenly by 2, 3, 4, 5, 6 and 8, enough for any grid of the roll.</summary>
    const int WritePpq = 960;
    /// <summary>A ramp of the map goes out as steps this long (beats), each as long as the ramp is there: a file has no ramps.</summary>
    const double RampStepBeats = 0.25;
    /// <summary>Under a file's own rounding, so a note ending right on a bar doesn't add a bar.</summary>
    const double BeatEpsilon = 1e-9;
    /// <summary>Beats before a bar line that still are the bar before it (over <see cref="TempoMap.BarAt"/>'s own slack).</summary>
    const double BarSlack = 1e-6;

    /// <summary>The tempo and the tracks of a file; empty tracks are dropped. An unreadable file throws <see cref="ArgumentException"/>.</summary>
    public static MidiFileData Read(string path)
    {
        try
        {
            var file = new MidiFile(path, false);
            int ppq = file.DeltaTicksPerQuarterNote;
            if (ppq is <= 0 or >= 0x8000) throw new FormatException("SMPTE time division");
            string baseName = Path.GetFileNameWithoutExtension(path);
            var tracks = new List<MidiFileTrack>();
            double? bpm = null;
            var programs = Programs(file);
            var map = Map(file, ppq);
            for (int i = 0; i < file.Events.Tracks; i++)
            {
                var events = file.Events[i];
                bpm ??= events.OfType<TempoEvent>().FirstOrDefault() is { MicrosecondsPerQuarterNote: > 0 } t ? Math.Round(60_000_000.0 / t.MicrosecondsPerQuarterNote, 3) : null;
                if (file.FileFormat == 0) AddChannels(tracks, events, ppq, baseName, programs, map);
                else if (Notes(events, ppq, null) is { Count: > 0 } notes)
                    tracks.Add(Track(TrackName(events), notes, events.OfType<NoteEvent>().First().Channel, programs, map));
            }
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].Name.Length == 0)
                    tracks[i] = tracks[i] with { Name = tracks.Count == 1 ? baseName : GmName(tracks[i]) ?? Texts.T("midi.file.track", "Pista {0}", i + 1) };
            return new MidiFileData(bpm, tracks, map);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException(Texts.T("err.midi.file", "No se pudo leer el archivo MIDI: {0}", Path.GetFileName(path)), e);
        }
    }

    /// <summary>
    /// Every tempo and time signature of the file (on whatever track), as a map from its start; null when it has none.
    /// A file's tempos are steps. A time signature that falls inside a bar starts at the next bar line.
    /// </summary>
    static TempoMap? Map(MidiFile file, int ppq)
    {
        var tempos = new List<(long At, double Bpm)>();
        var meters = new List<(long At, int Num, int Den)>();
        for (int i = 0; i < file.Events.Tracks; i++)
            foreach (var e in file.Events[i])
            {
                if (e is TempoEvent { MicrosecondsPerQuarterNote: > 0 } t) tempos.Add((t.AbsoluteTime, Math.Round(60_000_000.0 / t.MicrosecondsPerQuarterNote, 3)));
                else if (e is TimeSignatureEvent s && s.Denominator is >= 0 and <= 5) meters.Add((s.AbsoluteTime, s.Numerator, 1 << s.Denominator));
            }
        if (tempos.Count == 0 && meters.Count == 0) return null;
        var map = new TempoMap { Points = [.. tempos.OrderBy(x => x.At).Select(x => new TempoPoint { Beat = (double)x.At / ppq, Bpm = x.Bpm })] };
        if (map.Points.Count == 0 || map.Points[0].Beat > 0) map.Points.Insert(0, new() { Beat = 0, Bpm = map.Points.Count > 0 ? map.Points[0].Bpm : 120 });
        // the bars of the meters before each change say on which bar it lands
        int bar = 0;
        double from = 0, len = 4;
        foreach (var (at, num, den) in meters.OrderBy(x => x.At))
        {
            double beat = (double)at / ppq;
            int on = bar + (int)Math.Ceiling((beat - from) / len - BeatEpsilon);
            map.Meters.Add(new() { Bar = on, Num = num, Den = den });
            from += (on - bar) * len;
            bar = on;
            len = Math.Clamp(num, 1, TempoMap.MaxNum) * 4.0 / den;
        }
        map.Normalize();
        return map;
    }

    /// <summary>A type-0 file holds everything on one track: each channel becomes a track.</summary>
    static void AddChannels(List<MidiFileTrack> tracks, IList<MidiEvent> events, int ppq, string baseName, Dictionary<int, int> programs, TempoMap? map)
    {
        var channels = events.OfType<NoteEvent>().Select(e => e.Channel).Distinct().Order().ToList();
        string name = TrackName(events);
        foreach (int channel in channels)
        {
            var notes = Notes(events, ppq, channel);
            if (notes.Count == 0) continue;
            var track = Track("", notes, channel, programs, map);
            string shown = channels.Count > 1 ? GmName(track) ?? Texts.T("midi.file.channel", "Canal {0}", channel) : name.Length > 0 ? name : baseName;
            tracks.Add(track with { Name = shown });
        }
    }

    /// <summary>The GM channel of the drums (10, counted from 1 as NAudio does).</summary>
    const int DrumChannel = 10;

    /// <summary>A track, as long as the whole bars its notes reach (bars of the file's meters, 4/4 without any).</summary>
    static MidiFileTrack Track(string name, List<MidiNote> notes, int channel, Dictionary<int, int> programs, TempoMap? map)
    {
        double end = notes.Max(n => n.EndBeat);
        map ??= TempoMap.Constant(120);
        double length = map.BarStartBeat(Math.Max(1, map.BarAt(end - BarSlack) + 1));
        return new MidiFileTrack(name, notes, length, programs.TryGetValue(channel, out int program) ? program : null, channel == DrumChannel);
    }

    /// <summary>The first program change of each channel, wherever the file has it (often on a track of its own).</summary>
    static Dictionary<int, int> Programs(MidiFile file)
    {
        var first = new Dictionary<int, (long At, int Program)>();
        for (int i = 0; i < file.Events.Tracks; i++)
            foreach (var e in file.Events[i].OfType<PatchChangeEvent>())
                if (!first.TryGetValue(e.Channel, out var had) || e.AbsoluteTime < had.At) first[e.Channel] = (e.AbsoluteTime, e.Patch);
        return first.ToDictionary(p => p.Key, p => Math.Clamp(p.Value.Program, 0, 127));
    }

    /// <summary>A track with no name of its own takes the GM instrument it asks for; null when it asks for none.</summary>
    static string? GmName(MidiFileTrack t) => t.Drums ? GmNames.Kit(t.Program ?? 0) : t.Program is { } p ? GmNames.Program(p) : null;

    /// <summary>The GM instrument a track of a file plays with when it comes in as a new track: what its channel asked for, the piano if nothing.</summary>
    public static GmSettings GmOf(MidiFileTrack t) => new() { Program = t.Program ?? 0, Drums = t.Drums };

    /// <summary>The GM names of the keys a drum track plays, for its piano roll; null when none is on the map.</summary>
    static Dictionary<int, string>? DrumKeyNames(List<MidiNote> notes)
    {
        var names = notes.Select(n => n.Pitch).Distinct().Select(k => (k, GmNames.DrumKey(k))).Where(x => x.Item2 != null).ToDictionary(x => x.k, x => x.Item2!);
        return names.Count > 0 ? names : null;
    }

    static string TrackName(IList<MidiEvent> events) =>
        events.OfType<TextEvent>().FirstOrDefault(e => e.MetaEventType == MetaEventType.SequenceTrackName)?.Text.Trim() ?? "";

    /// <summary>
    /// The notes of a track (of one channel, when given), in order. Two notes open on the same key close first to first;
    /// what never closes ends with the track.
    /// </summary>
    static List<MidiNote> Notes(IList<MidiEvent> events, int ppq, int? channel)
    {
        var open = new Dictionary<(int Channel, int Pitch), Queue<(long At, int Velocity)>>();
        var notes = new List<MidiNote>();
        long last = 0;
        void Add(int pitch, long at, long end, int velocity, int release) => notes.Add(new MidiNote
        {
            Pitch = pitch, Beat = (double)at / ppq, Beats = Math.Max(MidiNote.MinBeats, (double)(end - at) / ppq),
            Velocity = Math.Clamp(velocity, 1, 127), Release = release,
        });
        foreach (var e in events)
        {
            last = Math.Max(last, e.AbsoluteTime);
            if (e is not NoteEvent n || channel is { } only && n.Channel != only) continue;
            bool on = n.CommandCode == MidiCommandCode.NoteOn && n.Velocity > 0;
            if (!on && n.CommandCode is not (MidiCommandCode.NoteOn or MidiCommandCode.NoteOff)) continue;
            var key = (n.Channel, n.NoteNumber);
            if (on)
            {
                if (!open.TryGetValue(key, out var queue)) open[key] = queue = new();
                queue.Enqueue((n.AbsoluteTime, n.Velocity));
            }
            else if (open.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var (at, velocity) = queue.Dequeue();
                Add(n.NoteNumber, at, n.AbsoluteTime, velocity, n.CommandCode == MidiCommandCode.NoteOff ? n.Velocity : 0);
            }
        }
        foreach (var ((_, pitch), queue) in open)
            foreach (var (at, velocity) in queue) Add(pitch, at, last, velocity, 0);
        notes.Sort(MidiNote.Order);
        return notes;
    }

    /// <summary>A type-1 file at one tempo, in 4/4 (<see cref="Write(string, IEnumerable{ValueTuple{string, IReadOnlyList{MidiNote}}}, TempoMap)"/>).</summary>
    public static void Write(string path, IEnumerable<(string Name, IReadOnlyList<MidiNote> Notes)> tracks, double bpm) =>
        Write(path, tracks, TempoMap.Constant(Math.Clamp(bpm, TempoMap.MinBpm, TempoMap.MaxBpm)));

    /// <summary>
    /// A type-1 file with the tempo map <paramref name="map"/> (beat 0 = the notes' 0; <see cref="TempoMap.From"/> gives
    /// a clip's): the first track holds its tempos (a ramp as short steps) and meters, then one per item, named, on
    /// channel 1. Muted notes aren't written. Atomic: an old file stays whole if this fails.
    /// </summary>
    public static void Write(string path, IEnumerable<(string Name, IReadOnlyList<MidiNote> Notes)> tracks, TempoMap map)
    {
        var events = new MidiEventCollection(1, WritePpq);
        events.AddTrack();
        for (int i = 0; i < map.Points.Count; i++)
        {
            var point = map.Points[i];
            if (!point.Ramp || i + 1 >= map.Points.Count)
            {
                events.AddEvent(Tempo(point.Beat, point.Bpm), 0);
                continue;
            }
            // each step lasts what the ramp lasts there, so every step's start falls where the ramp has it
            double end = map.Points[i + 1].Beat;
            for (double b = point.Beat; b < end - BeatEpsilon; b += RampStepBeats)
            {
                double next = Math.Min(end, b + RampStepBeats);
                events.AddEvent(Tempo(b, 60 * (next - b) / (map.SecAt(next) - map.SecAt(b))), 0);
            }
        }
        foreach (var m in map.Meters)
            events.AddEvent(new TimeSignatureEvent(Tick(map.BarStartBeat(m.Bar)), m.Num, (int)Math.Log2(m.Den), 24 * 4 / m.Den, 8), 0);
        int index = 1;
        foreach (var (name, notes) in tracks)
        {
            int track = index++;
            events.AddTrack();
            events.AddEvent(new TextEvent(name ?? "", MetaEventType.SequenceTrackName, 0), track);
            var heard = notes.Where(n => !n.Muted).Select(On).ToList();
            // the sort that follows keeps this order on a tick: a note ending where the same key starts again closes first
            foreach (var on in heard) events.AddEvent(on.OffEvent, track);
            foreach (var on in heard) events.AddEvent(on, track);
        }
        events.PrepareForExport();
        string temp = path + ".tmp";
        try
        {
            MidiFile.Export(temp, events);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    static long Tick(double beat) => (long)Math.Round(Math.Max(0, beat) * WritePpq);

    static TempoEvent Tempo(double beat, double bpm) => new((int)Math.Round(60_000_000 / Math.Max(1, bpm)), Tick(beat));

    /// <summary>
    /// Puts a read file's tempo map in the project (<see cref="TempoFollow.SetMap"/>: what lives in beats keeps its
    /// beat), the file's start on the song's. False when the file has no tempo or meter of its own.
    /// </summary>
    public static bool UseTempo(Project p, MidiFileData data) => data.Map is { } map && TempoFollow.SetMap(p, map);

    static NoteOnEvent On(MidiNote n)
    {
        long at = (long)Math.Round(Math.Max(0, n.Beat) * WritePpq);
        var on = new NoteOnEvent(at, 1, Math.Clamp(n.Pitch, MidiNote.MinPitch, MidiNote.MaxPitch), Math.Clamp(n.Velocity, 1, 127),
            (int)Math.Max(1, Math.Round(n.Beats * WritePpq)));
        on.OffEvent.Velocity = Math.Clamp(n.Release, 0, 127);
        return on;
    }

    /// <summary>
    /// What a clip plays as plain notes from its start: a pattern's passes laid out one after the other and everything
    /// cut at the clip's end. The notes are copies; muted ones stay, for whoever reads this to skip them.
    /// </summary>
    public static List<MidiNote> Heard(MidiClip clip)
    {
        var notes = new List<MidiNote>();
        for (int pass = 0; pass < clip.Passes; pass++)
            foreach (var n in clip.Notes)
                if (clip.Heard(n, pass, out double from, out double to) && to > from)
                {
                    var copy = n.Copy();
                    copy.Beat = from;
                    copy.Beats = to - from;
                    notes.Add(copy);
                }
        notes.Sort(MidiNote.Order);
        return notes;
    }

    /// <summary>
    /// Brings a file into the project at <paramref name="atSec"/>. On a MIDI track (<paramref name="trackId"/>) all its
    /// notes make one clip; otherwise each of its tracks becomes a new MIDI track with its name and its clip. Returns the
    /// clips' ids. Each new track plays with the GM instrument its channel asks for (asked by the user, #200); a target
    /// track that only has the test tone takes the first one's. <paramref name="pick"/> says which of the file's tracks
    /// come in, by index (null = all).
    /// </summary>
    public static List<string> Import(Project p, MidiFileData data, string? trackId, double atSec, IReadOnlyCollection<int>? pick = null)
    {
        var chosen = data.Tracks.Where((_, i) => pick == null || pick.Contains(i)).ToList();
        if (chosen.Count == 0) return [];
        if (p.Tracks.Find(t => t.Id == trackId) is { IsMidi: true } target)
        {
            var notes = chosen.SelectMany(t => t.Notes);
            if (target is { Instrument: null, Sampler: null, Granular: null, Gm: null }) target.Gm = GmOf(chosen[0]);
            return [AddClip(p, target.Id, atSec, chosen.Max(t => t.LengthBeats), notes)];
        }
        var ids = new List<string>();
        foreach (var t in chosen)
        {
            string id = MidiEditing.AddTrack(p);
            var track = p.Tracks.Find(x => x.Id == id)!;
            track.Name = t.Name;
            track.Gm = GmOf(t);
            if (t.Drums) track.KeyNames = DrumKeyNames(t.Notes);
            ids.Add(AddClip(p, id, atSec, t.LengthBeats, t.Notes));
        }
        return ids;
    }

    static string AddClip(Project p, string trackId, double atSec, double beats, IEnumerable<MidiNote> notes)
    {
        var clip = MidiEditing.AddClip(p, trackId, atSec, beats);
        MidiEditing.SetNotes(p, clip.Id, notes);
        return clip.Id;
    }
}
