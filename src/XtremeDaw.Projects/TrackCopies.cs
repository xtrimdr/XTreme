using System.Text.RegularExpressions;
using XtremeDaw.Audio;

namespace XtremeDaw.Projects;

/// <summary>
/// Duplicating tracks (asked by the user, ticket 110): each copy goes right under its original, in the same folder, with
/// a free name ("Bajo" → "Bajo 2"). It keeps the channel (fader, pan, effects, sends, output), the instrument and the
/// track's look; with <c>content</c> also its takes' clips, MIDI clips and automation, without them an empty track with
/// the same sound. Every id inside is new, so the engine never shares an effect or a clip between the two; automation
/// that pointed at the original's slots points at the copy's.
/// </summary>
public static partial class TrackCopies
{
    /// <summary>Duplicates the tracks (in project order, whatever order the ids come in). Returns the copies' ids, in that order.</summary>
    public static List<string> Duplicate(Project p, IReadOnlyCollection<string> ids, bool content)
    {
        var originals = p.Tracks.Where(t => ids.Contains(t.Id)).ToList();
        if (originals.Count == 0) throw new ArgumentException(Texts.T("err.clip.trackNotFound", "Pista no encontrada."));
        var pairs = new List<(Track Original, Track Copy)>();
        var names = p.Tracks.Select(t => t.Name).ToHashSet();
        foreach (var t in originals)
        {
            var copy = Copy(t, content);
            copy.Name = FreeName(names, t.Name);
            names.Add(copy.Name);
            pairs.Add((t, copy));
        }
        TrackFolders.PlaceAfter(p, pairs);
        foreach (var (_, copy) in pairs)
            if (copy.MidiClips != null)
                foreach (var c in copy.MidiClips) MidiPatterns.Adopt(p, c);
        return [.. pairs.Select(x => x.Copy.Id)];
    }

    /// <summary>
    /// A clip dropped under the last track lands on a new track of its kind (asked by the user, #309): a take's on an
    /// empty audio track (with a video panel if its old one had one), a MIDI clip's on a MIDI track with the same
    /// instrument, so it sounds the same. Nothing else of the old track comes along. The new track goes last, outside
    /// any folder. Returns its id.
    /// </summary>
    public static string MoveToNew(Project p, string clipId)
    {
        if (MidiEditing.TryFind(p, clipId) != null)
        {
            var (from, _) = MidiEditing.Find(p, clipId);
            if (from.EditLocked) throw new ArgumentException(Texts.T("err.clip.trackLocked", "La pista «{0}» está bloqueada.", from.Name));
            string made = MidiEditing.AddTrack(p);
            var t = p.Tracks.Find(x => x.Id == made)!;
            t.Instrument = from.Instrument == null ? null : ProjectLibrary.Clone(from.Instrument);
            t.Sampler = from.Sampler == null ? null : ProjectLibrary.Clone(from.Sampler);
            t.Granular = from.Granular == null ? null : ProjectLibrary.Clone(from.Granular);
            t.Gm = from.Gm == null ? null : ProjectLibrary.Clone(from.Gm);
            if (from.Icon != null) t.Icon = from.Icon;
            MidiEditing.MoveToTrack(p, clipId, made);
            return made;
        }
        var (track, clip) = ClipEditing.Find(p, clipId);
        // refused before the track is made, so a locked clip leaves nothing behind
        ClipEditing.Unlocked(track, clip);
        string id = ClipEditing.AddEmpty(p, track.Video);
        return ClipEditing.MoveToTrack(p, clipId, id);
    }

    /// <summary>A copy of <paramref name="t"/> with new ids everywhere, not yet in the project.</summary>
    static Track Copy(Track t, bool content)
    {
        var copy = ProjectLibrary.Clone(t);
        copy.Id = Ids.New("trk");
        // the slots get new ids and the lanes that drive them follow
        var slotIds = new Dictionary<string, string>();
        foreach (var s in copy.Channel.Inserts ?? [])
        {
            string id = Ids.New("fx");
            slotIds[s.Id] = id;
            s.Id = id;
        }
        if (!content)
        {
            copy.Clips = [];
            copy.MidiClips = copy.IsMidi ? [] : null;
            copy.Automation = null;
            copy.Locked = false;
            // the original's render is its content too: an empty copy still frozen would stay locked on it
            copy.Freeze = null;
            return copy;
        }
        var clipIds = new Dictionary<string, string>();
        foreach (var c in copy.Clips)
        {
            string id = Ids.New("clp");
            clipIds[c.Id] = id;
            c.Id = id;
        }
        // a loop's passes follow their source
        foreach (var c in copy.Clips)
            if (c.LoopOf != null) c.LoopOf = clipIds.GetValueOrDefault(c.LoopOf);
        if (copy.MidiClips != null)
            foreach (var c in copy.MidiClips) c.Id = Ids.New("mid");
        if (copy.Automation != null)
            foreach (var lane in copy.Automation)
            {
                lane.Id = Ids.New("aut");
                foreach (var b in lane.Blocks) b.Id = Ids.New("atb");
                lane.Target = Retarget(lane.Target, slotIds);
                lane.Touch();
            }
        return copy;
    }

    /// <summary>A lane's target on the copy: an effect's parameter moves to the copy's slot; the rest is the same.</summary>
    static string Retarget(string target, Dictionary<string, string> slotIds)
    {
        if (AutoTarget.FxOf(target) is { } fx && slotIds.TryGetValue(fx.Slot, out var f)) return AutoTarget.Fx(f, fx.Param);
        if (AutoTarget.VstOf(target) is { } vst && slotIds.TryGetValue(vst.Slot, out var v)) return AutoTarget.Vst(v, vst.Id);
        return target;
    }

    /// <summary>"Bajo" → "Bajo 2", "Pista 1" → "Pista 2": the next number free among the tracks' names.</summary>
    static string FreeName(HashSet<string> used, string name)
    {
        var m = Numbered().Match(name);
        string stem = m.Success ? m.Groups[1].Value : name;
        int n = m.Success && int.TryParse(m.Groups[2].Value, out int k) ? k + 1 : 2;
        while (used.Contains($"{stem} {n}")) n++;
        return $"{stem} {n}";
    }

    [GeneratedRegex(@"^(.*\S)\s+(\d{1,6})$")]
    private static partial Regex Numbered();
}
