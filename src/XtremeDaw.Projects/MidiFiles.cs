using NAudio.Midi;
using XtremeDaw.Audio;

namespace XtremeDaw.Projects;

/// <summary>A standard MIDI file as the project takes it: its first tempo (null = it has none) and its tracks with notes.</summary>
public sealed record MidiFileData(double? Bpm, List<MidiFileTrack> Tracks);

/// <summary>A track of a MIDI file: notes in beats from its start, and its length in whole bars.</summary>
public sealed record MidiFileTrack(string Name, List<MidiNote> Notes, double LengthBeats);

/// <summary>
/// Standard MIDI files in and out, through NAudio. Notes are kept in beats (ticks over ticks per quarter), never in
/// seconds, so an imported file follows the project's tempo. The pairing of note-ons and note-offs is done here and not
/// taken from NAudio's, which drops the end of a note that never closes and has no room for the note-off's velocity.
/// </summary>
public static class MidiFiles
{
    /// <summary>Ticks per quarter of what we write: divides evenly by 2, 3, 4, 5, 6 and 8, enough for any grid of the roll.</summary>
    const int WritePpq = 960;
    const int BeatsPerBar = 4;
    /// <summary>Under a file's own rounding, so a note ending right on a bar doesn't add a bar.</summary>
    const double BeatEpsilon = 1e-9;

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
            for (int i = 0; i < file.Events.Tracks; i++)
            {
                var events = file.Events[i];
                bpm ??= events.OfType<TempoEvent>().FirstOrDefault() is { MicrosecondsPerQuarterNote: > 0 } t ? Math.Round(60_000_000.0 / t.MicrosecondsPerQuarterNote, 3) : null;
                if (file.FileFormat == 0) AddChannels(tracks, events, ppq, baseName);
                else if (Notes(events, ppq, null) is { Count: > 0 } notes) tracks.Add(Track(TrackName(events), notes));
            }
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].Name.Length == 0)
                    tracks[i] = tracks[i] with { Name = tracks.Count == 1 ? baseName : Texts.T("midi.file.track", "Pista {0}", i + 1) };
            return new MidiFileData(bpm, tracks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException(Texts.T("err.midi.file", "No se pudo leer el archivo MIDI: {0}", Path.GetFileName(path)), e);
        }
    }

    /// <summary>A type-0 file holds everything on one track: each channel becomes a track.</summary>
    static void AddChannels(List<MidiFileTrack> tracks, IList<MidiEvent> events, int ppq, string baseName)
    {
        var channels = events.OfType<NoteEvent>().Select(e => e.Channel).Distinct().Order().ToList();
        string name = TrackName(events);
        foreach (int channel in channels)
        {
            var notes = Notes(events, ppq, channel);
            if (notes.Count == 0) continue;
            string shown = channels.Count > 1 ? Texts.T("midi.file.channel", "Canal {0}", channel) : name.Length > 0 ? name : baseName;
            tracks.Add(Track(shown, notes));
        }
    }

    static MidiFileTrack Track(string name, List<MidiNote> notes)
    {
        double end = notes.Max(n => n.EndBeat);
        return new MidiFileTrack(name, notes, Math.Max(1, Math.Ceiling(end / BeatsPerBar - BeatEpsilon)) * BeatsPerBar);
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

    /// <summary>
    /// A type-1 file at <paramref name="bpm"/>: the first track holds the tempo, then one per item, named, on channel 1.
    /// Muted notes aren't written. Atomic: an old file stays whole if this fails.
    /// </summary>
    public static void Write(string path, IEnumerable<(string Name, IReadOnlyList<MidiNote> Notes)> tracks, double bpm)
    {
        var events = new MidiEventCollection(1, WritePpq);
        events.AddTrack();
        events.AddEvent(new TempoEvent((int)Math.Round(60_000_000 / Math.Max(1, bpm)), 0), 0);
        events.AddEvent(new TimeSignatureEvent(0, BeatsPerBar, 2, 24, 8), 0);
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
    /// clips' ids.
    /// </summary>
    public static List<string> Import(Project p, MidiFileData data, string? trackId, double atSec)
    {
        if (data.Tracks.Count == 0) return [];
        if (p.Tracks.Find(t => t.Id == trackId) is { IsMidi: true } target)
        {
            var notes = data.Tracks.SelectMany(t => t.Notes);
            return [AddClip(p, target.Id, atSec, data.Tracks.Max(t => t.LengthBeats), notes)];
        }
        var ids = new List<string>();
        foreach (var t in data.Tracks)
        {
            string id = MidiEditing.AddTrack(p);
            p.Tracks.Find(x => x.Id == id)!.Name = t.Name;
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
