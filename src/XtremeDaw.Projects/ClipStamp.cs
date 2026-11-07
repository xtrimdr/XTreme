namespace XtremeDaw.Projects;

/// <summary>
/// The timeline's Lápiz (asked by a collaborator, like FL Studio's pencil): copies of one take, MIDI clip or pattern, the
/// brush, put on a track at the given starts. A copy that would cover a clip already there is skipped, so dragging the
/// pencil over a stretch only fills its gaps. The brush is a snapshot, so it keeps painting after its clip is moved or
/// removed; a pattern's copies are taken from the pattern as it is now, so they stay in it.
/// </summary>
public static class ClipStamp
{
    /// <summary>How much two clips may share (s) and still count as side by side: a copy's start is rounded to the sample.</summary>
    const double TouchSec = 1e-3;

    /// <summary>
    /// Copies of the take <paramref name="brush"/>, each starting (where it starts to sound) at one of
    /// <paramref name="starts"/>, on the audio track <paramref name="trackId"/>. Returns the copies' ids.
    /// </summary>
    /// <param name="durationSec">The brush's media length as heard (its stretch counted), when the caller knows it better than the media does.</param>
    /// <param name="apply">False only counts: the ids it would make, without touching the project (no empty undo step).</param>
    public static List<string> Audio(Project p, Clip brush, string trackId, IReadOnlyList<double> starts, double? durationSec = null, bool apply = true)
    {
        var t = p.Tracks.Find(x => x.Id == trackId);
        if (t == null || t.IsMidi || t.EditLocked || p.FindMedia(brush.MediaId) is not { } media) return [];
        var w = ClipEditing.Window(brush, media, durationSec);
        double length = w.End - w.Start;
        List<string> made = [];
        foreach (double at in starts)
        {
            double start = Math.Max(0, at);
            if (!AudioFree(p, t, start, start + length)) continue;
            var c = ClipEditing.Copy(brush);
            c.Id = Ids.New("clp");
            c.LoopOf = null;
            c.AtSec = ClipEditing.ToSample(brush.AtSec + start - w.Start);
            if (apply) t.Clips.Add(c);
            made.Add(c.Id);
        }
        return made;
    }

    /// <summary>
    /// Copies of the MIDI clip <paramref name="brush"/> (its length and notes) at each of <paramref name="starts"/>, on
    /// the MIDI track <paramref name="trackId"/>. A pattern's brush makes uses of that pattern: with several
    /// instruments, each part on its own track, as <see cref="MidiPatterns.Insert"/> does. Returns the ids of the copies
    /// on <paramref name="trackId"/> (or each stamp's first).
    /// </summary>
    /// <param name="apply">False only counts, as in <see cref="Audio"/>.</param>
    public static List<string> Midi(Project p, MidiClip brush, string trackId, IReadOnlyList<double> starts, bool apply = true)
    {
        var t = p.Tracks.Find(x => x.Id == trackId);
        if (t is not { IsMidi: true } || t.EditLocked) return [];
        var map = p.Settings.TempoMap;
        var parts = brush.PatternId is { } id ? MidiPatterns.Parts(p, id) : [];
        List<string> made = [];
        foreach (double at in starts)
        {
            double start = ClipEditing.ToSample(Math.Max(0, at)), end = map.SecAt(map.BeatAt(start) + brush.LengthBeats);
            if (parts.Count > 1)
            {
                if (parts.Any(x => !MidiFree(x.Track, start, end, map))) continue;
                string? first = null;
                foreach (var part in parts)
                {
                    var c = Place(part.Clip, brush, start);
                    if (apply) (part.Track.MidiClips ??= []).Add(c);
                    if (first == null || part.Track == t) first = c.Id;
                }
                made.Add(first!);
            }
            else
            {
                if (!MidiFree(t, start, end, map)) continue;
                var c = Place(parts.Count == 1 ? parts[0].Clip : brush, brush, start);
                // a pattern that is gone leaves the brush a clip of its own
                if (parts.Count == 0) c.PatternId = c.PatternName = c.PatternPart = null;
                c.PatternBeats = c.PatternId == null ? null : c.PatternBeats;
                if (apply) (t.MidiClips ??= []).Add(c);
                made.Add(c.Id);
            }
        }
        return made;
    }

    /// <summary>A copy of <paramref name="source"/> (its notes) as long as the brush, at <paramref name="start"/>.</summary>
    static MidiClip Place(MidiClip source, MidiClip brush, double start)
    {
        var c = source.Copy();
        c.AtSec = start;
        c.LengthBeats = brush.LengthBeats;
        c.Normalize();
        return c;
    }

    /// <summary>The brush of a pattern picked from the Patrones list: one pass of its first part.</summary>
    public static MidiClip? PatternBrush(Project p, string patternId)
    {
        var parts = MidiPatterns.Parts(p, patternId);
        if (parts.Count == 0) return null;
        var c = parts[0].Clip.Copy();
        c.LengthBeats = c.CellBeats;
        c.Normalize();
        return c;
    }

    static bool AudioFree(Project p, Track t, double start, double end) => t.Clips.All(c =>
    {
        var w = ClipEditing.Window(c, p.FindMedia(c.MediaId));
        return w.End <= start + TouchSec || w.Start >= end - TouchSec;
    });

    static bool MidiFree(Track t, double start, double end, TempoMap map) => !t.EditLocked && (t.MidiClips ?? []).All(c =>
        c.EndSec(map) <= start + TouchSec || c.AtSec >= end - TouchSec);
}
