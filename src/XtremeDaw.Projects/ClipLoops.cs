using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>
/// Looping a take's clip (asked by the user, like GarageBand): dragging the loop handle at its top right corner repeats
/// it after its end, the last repeat cut where the drag stops. Each repeat is a whole clip of its own with
/// <see cref="Clip.LoopOf"/> pointing at the source, so playback, the video and the export play it like any other clip
/// and nothing in the engine knows about loops. The repeats are never edited one by one: they are remade from the
/// source (<see cref="Remake"/>) whenever the source changes, keeping how far the loop reaches past its start; a repeat
/// edited by itself leaves the loop and becomes a plain clip. A MIDI clip loops on its own (<see cref="MidiClip.LoopBeats"/>).
/// </summary>
public static class ClipLoops
{
    /// <summary>The most repeats one loop makes, so a drag to the end of a long song stays a sane number of clips.</summary>
    public const int MaxRepeats = 512;

    /// <summary>The clip a loop gesture on <paramref name="c"/> acts on: its source when it is a repeat.</summary>
    public static string SourceId(Clip c) => c.LoopOf ?? c.Id;

    /// <summary>The repeats of <paramref name="sourceId"/> on <paramref name="t"/>, in timeline order.</summary>
    public static IEnumerable<Clip> Repeats(Track t, string sourceId) => t.Clips.Where(c => c.LoopOf == sourceId).OrderBy(c => c.AtSec);

    public static bool IsLooped(Track t, Clip c) => c.LoopOf == null && t.Clips.Any(o => o.LoopOf == c.Id);

    /// <summary>Where one pass of <paramref name="source"/> starts and how long it is (what a cut at 0 hides counts).</summary>
    public static (double Start, double Length) Pass(Project p, Clip source, double? durationSec = null)
    {
        var w = ClipEditing.Window(source, p.FindMedia(source.MediaId), durationSec);
        double start = w.Origin + w.TrimStart;
        return (start, Math.Max(0, w.End - start));
    }

    /// <summary>Where the looped stretch of <paramref name="source"/> ends: its last repeat's end, or its own.</summary>
    public static double EndSec(Project p, Track t, Clip source, double? durationSec = null)
    {
        double end = ClipEditing.Window(source, p.FindMedia(source.MediaId), durationSec).End;
        foreach (var r in Repeats(t, source.Id)) end = Math.Max(end, ClipEditing.Window(r, p.FindMedia(r.MediaId), durationSec).End);
        return end;
    }

    /// <summary>
    /// Loops the clip <paramref name="clipId"/> (or its source, when it is a repeat) up to <paramref name="endSec"/>:
    /// its repeats are made again, the last one cut there. An end inside the source itself takes the loop away.
    /// </summary>
    public static void LoopTo(Project p, string clipId, double endSec, double? durationSec = null)
    {
        var (track, clip) = ClipEditing.Find(p, clipId);
        var source = clip.LoopOf is { } id ? track.Clips.Find(c => c.Id == id) ?? clip : clip;
        if (ClipEditing.IsLocked(track, source)) throw new ArgumentException(Texts.T("err.clip.locked", "El clip está bloqueado."));
        Remake(p, track, source, endSec, durationSec);
    }

    /// <summary>Takes the loop away: the repeats go and the source stays as it was.</summary>
    public static void Unloop(Project p, string clipId)
    {
        var (_, clip) = ClipEditing.Find(p, clipId);
        string id = SourceId(clip);
        foreach (var t in p.Tracks) t.Clips.RemoveAll(c => c.LoopOf == id);
    }

    /// <summary>
    /// Makes <paramref name="source"/>'s repeats again from it, from its end to <paramref name="endSec"/>. The new
    /// repeats take the old ones' ids in order: the window keeps a selection by id, and a selected repeat that vanished
    /// under it made the next edit fail (#281).
    /// </summary>
    static void Remake(Project p, Track track, Clip source, double endSec, double? durationSec)
    {
        var ids = new Queue<string>(Repeats(track, source.Id).Select(r => r.Id));
        foreach (var t in p.Tracks) t.Clips.RemoveAll(c => c.LoopOf == source.Id);
        source.LoopOf = null;
        var w = ClipEditing.Window(source, p.FindMedia(source.MediaId), durationSec);
        var (start, pass) = Pass(p, source, durationSec);
        if (pass < ClipEnvelope.MinClipSec) return;
        int index = track.Clips.IndexOf(source) + 1;
        for (int k = 1; k <= MaxRepeats; k++)
        {
            double from = start + k * pass;
            if (endSec - from < ClipEnvelope.MinClipSec) break;
            var r = ClipEditing.Copy(source);
            r.Id = ids.Count > 0 ? ids.Dequeue() : Ids.New("clp");
            r.LoopOf = source.Id;
            r.Locked = false;
            r.Transition = ClipTransitions.CutId; // a repeat follows its pass straight on
            r.AtSec = ClipEditing.ToSample(source.AtSec + k * pass);
            double cut = from + pass - endSec;
            if (cut > 0)
            {
                r.TrimEndSec = ClipEditing.ToSample(w.TrimEnd + cut);
                r.FadeOutSec = r.VideoFadeOutSec = 0; // the cut end isn't the source's end
            }
            track.Clips.Insert(index++, r);
        }
    }

    /// <summary>A looped source as it was before an edit: its clip as JSON, its repeats' and how far the loop reached.</summary>
    public sealed record Snapshot(string SourceId, string Source, Dictionary<string, string> Repeats, double Reach);

    /// <summary>Every loop of the project before an edit, for <see cref="Keep"/>.</summary>
    public static List<Snapshot> Take(Project p, Func<Clip, double?>? lengthOf = null)
    {
        List<Snapshot> loops = [];
        foreach (var t in p.Tracks)
            foreach (var source in t.Clips.Where(c => IsLooped(t, c)).ToList())
            {
                double reach = EndSec(p, t, source, lengthOf?.Invoke(source)) - Pass(p, source, lengthOf?.Invoke(source)).Start;
                loops.Add(new(source.Id, Json(source), Repeats(t, source.Id).ToDictionary(r => r.Id, Json), reach));
            }
        return loops;
    }

    /// <summary>
    /// After an edit: a source that changed gets its repeats made again, reaching as far past its start as before; a
    /// repeat changed by itself leaves the loop; a repeat whose source went is a plain clip from now on.
    /// </summary>
    public static void Keep(Project p, IReadOnlyList<Snapshot> before, Func<Clip, double?>? lengthOf = null)
    {
        foreach (var s in before)
        {
            var found = p.Tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c))).FirstOrDefault(x => x.Clip.Id == s.SourceId);
            if (found.Clip == null) continue;
            var (track, source) = found;
            if (Json(source) != s.Source)
            {
                if (ClipEditing.IsLocked(track, source)) continue;
                Remake(p, track, source, Pass(p, source, lengthOf?.Invoke(source)).Start + s.Reach, lengthOf?.Invoke(source));
                continue;
            }
            foreach (var t in p.Tracks)
                foreach (var r in t.Clips)
                    if (r.LoopOf == s.SourceId && s.Repeats.TryGetValue(r.Id, out var was) && (t != track || Json(r) != was)) r.LoopOf = null;
        }
        foreach (var t in p.Tracks) Normalize(t);
    }

    /// <summary>
    /// After copying clips together (D, paste, #281): a copied repeat whose source was copied too repeats that copy, so
    /// the loop goes along; any other copied repeat is a plain clip. <paramref name="copies"/> maps each original's id to
    /// its copy, whose <see cref="Clip.LoopOf"/> is still the original's.
    /// </summary>
    public static void Relink(IReadOnlyDictionary<string, Clip> copies)
    {
        foreach (var copy in copies.Values)
            copy.LoopOf = copy.LoopOf is { } source && copies.TryGetValue(source, out var s) ? s.Id : null;
    }

    /// <summary>A repeat whose source isn't on its track (or is a repeat itself) is a plain clip.</summary>
    internal static void Normalize(Track t)
    {
        foreach (var c in t.Clips)
            if (c.LoopOf != null && t.Clips.Find(o => o.Id == c.LoopOf) is not { LoopOf: null }) c.LoopOf = null;
    }

    static string Json(Clip c) => System.Text.Json.JsonSerializer.Serialize(c, ProjectStore.Json);
}
