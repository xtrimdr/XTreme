using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using System.Text.Json;

namespace XtremeDaw.Projects;

/// <summary>The clip handles: the five of the web app's Timeline.tsx, the stretch ones and the loop (<see cref="ClipLoops"/>).</summary>
public enum ClipEditMode { Move, TrimStart, TrimEnd, FadeIn, FadeOut, StretchStart, StretchEnd, Loop }

/// <summary>Which edges of a moved clip stick to the edges of the others (the snap to other clips).</summary>
[Flags]
public enum SnapEdges { None = 0, Start = 1, End = 2, Both = Start | End }

/// <summary>
/// What a time snaps to: lines every <paramref name="Step"/> seconds (0 = none), the nearest one or, with
/// <paramref name="Cell"/>, the start of the cell it falls in. With <paramref name="Bar"/> (a bar's length) the lines
/// start over at each bar, which only changes anything for a step that doesn't fit a whole number of times in it (the
/// dotted values, <see cref="Subdivisions"/>). A plain number is a step with nearest lines.
/// </summary>
public readonly record struct SnapGrid(double Step, bool Cell = false, double Bar = 0)
{
    public static implicit operator SnapGrid(double step) => new(step);
}

/// <summary>
/// Port of Timeline.tsx clip gestures and App.tsx lane operations. Times use native seconds instead of
/// audioOffsetMs. Native tracks keep their IDs/channels; on multi-clip tracks only the clip under the
/// pointer swaps, and dropping on empty space moves the clip without disturbing the other clips.
/// lastTimelineClip / lastTimelineChannel in media extension data retain the web history's last edits
/// when a native clip leaves the timeline (the recording files remain immutable).
/// Media files, the media collection and recording sync corrections are never modified.
/// </summary>
public static class ClipEditing
{
    public static Clip Copy(Clip clip) => ProjectLibrary.Clone(clip);

    /// <param name="durationSec">The clip's length on the timeline, when the caller knows it better than the media does.</param>
    public static ClipEnvelope.ClipWindow Window(Clip c, Media? m, double? durationSec = null) =>
        ClipEnvelope.Window(durationSec ?? (m?.DurationSec ?? 0) * c.Stretch, c.AtSec, c.TrimStartSec, c.TrimEndSec, c.EffectiveSyncMs(m));

    public static double GridSeconds(string mode, double bpm) => Subdivisions.Beats(mode) * 60 / Math.Max(1, bpm);

    /// <summary>A value's lines at <paramref name="bpm"/>: its step, starting over at each bar.</summary>
    public static SnapGrid GridOf(string mode, double bpm) => new(GridSeconds(mode, bpm), Bar: Subdivisions.BarBeats * 60 / Math.Max(1, bpm));

    /// <summary>Whether lines every <paramref name="step"/> starting over at each <paramref name="bar"/> are just evenly spaced.</summary>
    public static bool FitsBar(double step, double bar) => bar <= 0 || step >= bar - 1e-9 || Math.Abs(bar / step - Math.Round(bar / step)) < 1e-6;

    // JavaScript Math.round: half ties go toward positive infinity, including negative offsets.
    static double JsRound(double v) => Math.Floor(v + 0.5);
    /// <summary>
    /// The finest step a clip moves, trims or fades by: one sample at the engine's 48 kHz. It was the millisecond, which
    /// at the deepest zoom (96 000 px/s) is 96 px, far coarser than the grid's finest lines, even moving free with Alt.
    /// </summary>
    const double SamplesPerSec = 48000;
    public static double ToSample(double sec) => JsRound(sec * SamplesPerSec) / SamplesPerSec;
    static double Limit(double v, double max) => Math.Max(0, Math.Min(max, v));
    public static double Snap(double sec, SnapGrid grid, bool free)
    {
        if (free || grid.Step <= 0) return sec;
        double step = grid.Step;
        if (FitsBar(step, grid.Bar)) return (grid.Cell ? Math.Floor(sec / step + CellSlack) : JsRound(sec / step)) * step;
        // a dotted step: the lines of the bar it falls in, and the next bar's first one
        double barStart = Math.Floor(sec / grid.Bar + CellSlack) * grid.Bar, r = sec - barStart;
        double lo = Math.Floor(r / step + CellSlack) * step, hi = Math.Min(lo + step, grid.Bar);
        return barStart + (grid.Cell || r - lo < hi - r ? lo : hi);
    }
    /// <summary>In steps: a time on a line that floating point left just short of it is still in that line's cell.</summary>
    const double CellSlack = 1e-9;

    /// <summary>Px within which a moved clip's edge sticks to another clip's edge (SNAP_PX in Timeline.tsx).</summary>
    public const double EdgePullPx = 8;

    /// <summary>
    /// Calculates a gesture from its original clip, never incrementally from the previous pointer move. With
    /// <paramref name="others"/> and <paramref name="sticky"/> (the snap to other clips) a moved clip's visible start
    /// and/or end sticks to their visible edges within <see cref="EdgePullPx"/>.
    /// </summary>
    public static Clip Drag(Clip original, Media? media, ClipEditMode mode, double dx, SnapGrid grid, bool free,
        double? durationSec = null, IEnumerable<ClipEnvelope.ClipWindow>? others = null, SnapEdges sticky = SnapEdges.None, double pps = 100) =>
        Drag(original, media, mode, dx, grid, free, out _, durationSec, others, sticky, pps);

    /// <summary>The same, saying in <paramref name="stuckTo"/> the other clip's edge it stuck to (the timeline draws a guide there).</summary>
    public static Clip Drag(Clip original, Media? media, ClipEditMode mode, double dx, SnapGrid grid, bool free, out double? stuckTo,
        double? durationSec = null, IEnumerable<ClipEnvelope.ClipWindow>? others = null, SnapEdges sticky = SnapEdges.None, double pps = 100)
    {
        stuckTo = null;
        var c = Copy(original);
        var w = Window(original, media, durationSec);
        var fades = ClipEnvelope.FitFades(c.FadeInSec, c.FadeOutSec, w.End - w.Start);
        double len = w.End - w.Start;
        switch (mode)
        {
            case ClipEditMode.Move:
                // With a grid its start lands on a line, so a clip off the grid goes back on it just by moving it (asked
                // by the user, who had asked for the opposite, keeping the offset: lining up was all by hand); Alt keeps
                // it free. The start is the anchor, the same as "Alinear a la grilla". Other clips pull only with their own snap on (asked by the user), and only their visible edges: the
                // take's start hides left of a trimmed clip. Both magnets together are seamless (asked by the user):
                // the grid places the clip and an edge only fine-tunes it, when the clip as placed is within
                // EdgePullPx of it, so it never jumps more than that. Measured from the pointer instead, with a
                // bar's step the clip leapt ~26 px off the grid to an edge the pointer was near but the clip wasn't.
                double shift = Snap(w.Anchor + dx, grid, free) - w.Anchor;
                if (!free && others != null && sticky != SnapEdges.None)
                {
                    double best = EdgePullPx / Math.Max(0.001, pps), placed = shift;
                    foreach (var other in others)
                        for (int k = 0; k < 4; k++)
                        {
                            bool fromStart = k < 2;
                            if (!sticky.HasFlag(fromStart ? SnapEdges.Start : SnapEdges.End)) continue;
                            double target = k % 2 == 0 ? other.Start : other.End;
                            double delta = target - ((fromStart ? w.Start : w.End) + placed);
                            if (Math.Abs(delta) >= best) continue;
                            best = Math.Abs(delta);
                            shift = placed + delta;
                            stuckTo = target;
                        }
                }
                if (shift < ClipEnvelope.MinClipSec - w.End) { shift = ClipEnvelope.MinClipSec - w.End; stuckTo = null; }
                c.AtSec = ToSample(c.AtSec + shift);
                break;
            case ClipEditMode.TrimStart:
                double from = dx > 0 ? w.TrimStart + w.Cut : w.TrimStart;
                double start = Limit(from + dx, w.Dur - c.TrimEndSec - ClipEnvelope.MinClipSec);
                c.TrimStartSec = ToSample(free ? start : Limit(Snap(w.Origin + start, grid, false) - w.Origin,
                    w.Dur - c.TrimEndSec - ClipEnvelope.MinClipSec));
                break;
            case ClipEditMode.TrimEnd:
                double trim = Limit(c.TrimEndSec - dx, w.Dur - c.TrimStartSec - ClipEnvelope.MinClipSec);
                c.TrimEndSec = ToSample(Limit(w.Origin + w.Dur - Snap(w.Origin + w.Dur - trim, grid, free),
                    w.Dur - c.TrimStartSec - ClipEnvelope.MinClipSec));
                break;
            case ClipEditMode.FadeIn:
                double fi = Limit(fades.FadeIn + dx, len - fades.FadeOut);
                c.FadeInSec = ToSample(Limit(Snap(w.Start + fi, grid, free) - w.Start, len - fades.FadeOut));
                break;
            case ClipEditMode.FadeOut:
                double fo = Limit(fades.FadeOut - dx, len - fades.FadeIn);
                c.FadeOutSec = ToSample(Limit(w.End - Snap(w.End - fo, grid, free), len - fades.FadeIn));
                break;
            case ClipEditMode.StretchStart or ClipEditMode.StretchEnd:
                StretchEdge(c, w, mode == ClipEditMode.StretchEnd, dx, grid, free);
                break;
        }
        return c;
    }

    /// <summary>
    /// One edge pulled (with the grid) and the other left where it is: everything the clip has on the timeline scales
    /// around that fixed edge, as a tempo change scales it around zero, so <see cref="Clip.Stretch"/> stays one number
    /// whichever made it. There is no limit on the amount (asked by the user: rhythm loops go far); the clip only keeps
    /// its shortest length, and its start can't be pulled past zero.
    /// </summary>
    static void StretchEdge(Clip c, ClipEnvelope.ClipWindow w, bool end, double dx, SnapGrid grid, bool free)
    {
        double len = w.End - w.Start, anchor = end ? w.Start : w.End;
        if (len <= 0) return;
        double f = (end ? Snap(w.End + dx, grid, free) - anchor : anchor - Snap(w.Start + dx, grid, free)) / len;
        double low = ClipEnvelope.MinClipSec / len;
        f = Math.Max(f, low);
        if (!end) f = Math.Max(low, Math.Min(f, Math.Max(1, anchor / len)));
        double at = anchor + (c.AtSec - anchor) * f;
        TempoFollow.Rescale(c, f);
        c.AtSec = ToSample(at);
    }

    /// <summary>Longest name a clip keeps.</summary>
    public const int MaxNameLength = 120;

    /// <summary>A clip's name as it is kept: trimmed and not too long; blank = none.</summary>
    public static string? CleanName(string? name)
    {
        name = name?.Trim();
        return string.IsNullOrEmpty(name) ? null : name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    /// <summary>
    /// Gives a clip a name of its own (blank takes it away). A loop's repeat names its source, which names the repeats
    /// in turn (<see cref="ClipLoops.Keep"/>). A locked clip can be renamed: its place doesn't change.
    /// </summary>
    public static void Rename(Project p, string id, string? name)
    {
        var (track, c) = Find(p, id);
        if (c.LoopOf != null && track.Clips.Find(o => o.Id == c.LoopOf) is { } source) c = source;
        c.Name = CleanName(name);
    }

    public static (Track Track, Clip Clip) Find(Project p, string clipId)
    {
        foreach (var t in p.Tracks)
            if (t.Clips.Find(c => c.Id == clipId) is { } c) return (t, c);
        throw new ArgumentException(Texts.T("err.clip.clipNotFound", "Clip no encontrado."));
    }

    /// <summary>
    /// The lock rule, the same for the window and xtlab: a clip of a locked track, or a locked clip, keeps its place,
    /// its length and its spot on the track. The edits that would change those refuse it (<see cref="Unlocked"/>) and
    /// the ones over several clips skip it; its gain, fades, curve and framing still change, so a locked take can still
    /// be mixed.
    /// </summary>
    public static bool IsLocked(Track t, Clip c) => t.Locked || c.Locked;

    /// <summary>Whether the clip with this id is locked (false when there is none).</summary>
    public static bool IsLocked(Project p, string clipId)
    {
        foreach (var t in p.Tracks)
            if (t.Clips.Find(c => c.Id == clipId) is { } c) return IsLocked(t, c);
        return false;
    }

    /// <summary>A track with a lock on it: its own, or one of its clips'. It can't be removed or recorded over.</summary>
    public static bool HasLock(Track t) => t.Locked || t.Clips.Any(c => c.Locked);

    static void Unlocked(Track t, Clip c)
    {
        if (IsLocked(t, c))
            throw new ArgumentException(t.Locked
                ? Texts.T("err.clip.trackLocked", "La pista «{0}» está bloqueada.", t.Name)
                : Texts.T("err.clip.locked", "El clip está bloqueado."));
    }

    /// <summary>Locks or unlocks a clip.</summary>
    public static void SetLocked(Project p, string id, bool locked) => Find(p, id).Clip.Locked = locked;

    /// <summary>Writes a clip's new values; a locked one refuses a new place, trim or stretch (its other values change).</summary>
    public static void Replace(Project p, Clip value)
    {
        var (track, clip) = Find(p, value.Id);
        if (value.AtSec != clip.AtSec || value.TrimStartSec != clip.TrimStartSec || value.TrimEndSec != clip.TrimEndSec || value.Stretch != clip.Stretch) Unlocked(track, clip);
        track.Clips[track.Clips.IndexOf(clip)] = Copy(value);
    }

    public static string MoveToTrack(Project p, string clipId, string targetId, string? swapId = null)
    {
        var (from, clip) = Find(p, clipId);
        var to = p.Tracks.Find(t => t.Id == targetId) ?? throw new ArgumentException(Texts.T("err.clip.trackNotFound", "Pista no encontrada."));
        if (from == to) return to.Id;
        if (to.IsMidi) throw new ArgumentException(Texts.T("err.midi.noAudio", "El audio no va en pistas MIDI."));
        Unlocked(from, clip);
        if (to.Locked) throw new ArgumentException(Texts.T("err.clip.trackLocked", "La pista «{0}» está bloqueada.", to.Name));
        Clip? other = swapId == null ? null : to.Clips.Find(c => c.Id == swapId)
            ?? throw new ArgumentException(Texts.T("err.clip.swapNotFound", "Clip a intercambiar no encontrado."));
        if (other != null) Unlocked(to, other);
        int index = from.Clips.IndexOf(clip);
        from.Clips.RemoveAt(index);
        if (other != null)
        {
            int targetIndex = to.Clips.IndexOf(other);
            to.Clips[targetIndex] = clip;
            from.Clips.Insert(index, other);
        }
        else to.Clips.Add(clip);
        return to.Id;
    }

    public static void RemoveClip(Project p, string id)
    {
        var (track, clip) = Find(p, id);
        Unlocked(track, clip);
        Remember(p, track, clip);
        track.Clips.Remove(clip);
        // a loop goes with its source
        if (clip.LoopOf == null) track.Clips.RemoveAll(c => c.LoopOf == clip.Id); // the track keeps its name (asked by the user, #163)
    }

    public static string AddEmpty(Project p, bool video = true)
    {
        var track = new Track { Name = FreeTrackName(p, p.Tracks.Count + 1), Video = video };
        p.Tracks.Add(track);
        return track.Id;
    }

    static string FreeTrackName(Project p, int n)
    {
        var used = p.Tracks.Select(t => t.Name).ToHashSet();
        while (used.Contains($"Pista {n}")) n++;
        return $"Pista {n}";
    }

    public static string AddMedia(Project p, string mediaId, string? selectedTrackId)
    {
        var media = p.FindMedia(mediaId) ?? throw new ArgumentException(Texts.T("err.clip.mediaNotFound", "Canal no encontrado en el historial."));
        if (p.Tracks.Any(t => t.Clips.Any(c => c.MediaId == mediaId))) throw new ArgumentException(Texts.T("err.clip.mediaInUse", "Ese canal ya está en el timeline."));
        var target = p.Tracks.Find(t => t.Id == selectedTrackId && t.Clips.Count == 0 && !t.IsMidi);
        Channel? previousChannel = null;
        Clip? previousClip = null;
        if (media.Extra?.TryGetValue("lastTimelineChannel", out var channelJson) == true)
            previousChannel = channelJson.Deserialize<Channel>(ProjectStore.Json);
        if (media.Extra?.TryGetValue("lastTimelineClip", out var clipJson) == true)
            previousClip = clipJson.Deserialize<Clip>(ProjectStore.Json);
        if (target == null)
        {
            target = NewTrackFor(p, media, previousChannel);
            p.Tracks.Add(target);
        }
        var clip = previousClip ?? new Clip { MediaId = media.Id, AtSec = media.SyncMs / 1000 };
        clip.Id = Ids.New("clp");
        target.Clips.Add(clip);
        return target.Id;
    }

    /// <summary>An imported file comes back as it was imported: a neutral strip, and a panel only if it brought a picture.</summary>
    static Track NewTrackFor(Project p, Media media, Channel? previous)
    {
        bool imported = media.Kind == "import";
        return new Track { Name = media.Name, Video = !imported || media.Video != null, Channel = previous ?? (imported ? new Channel()
            : new Channel { Comp = new CompSettings { On = true } }) };
    }

    /// <summary>
    /// A take dragged to the timeline: a clip of it starting at <paramref name="atSec"/>, as many times as asked (unlike
    /// <see cref="AddMedia"/>). <paramref name="trackId"/> takes it when it is empty or that stretch is free; if not, or
    /// with no track, it goes on a new one. A take off the timeline keeps the cut and the strip it had.
    /// </summary>
    public static string PlaceMedia(Project p, string mediaId, double atSec, string? trackId)
    {
        var media = p.FindMedia(mediaId) ?? throw new ArgumentException(Texts.T("err.clip.mediaNotFound", "Canal no encontrado en el historial."));
        var (clip, track) = Placement(p, media, atSec, trackId);
        clip.Id = Ids.New("clp");
        if (track == null)
        {
            Channel? previousChannel = null;
            if (!p.Tracks.Any(t => t.Clips.Any(c => c.MediaId == mediaId)) && media.Extra?.TryGetValue("lastTimelineChannel", out var channelJson) == true)
                previousChannel = channelJson.Deserialize<Channel>(ProjectStore.Json);
            track = NewTrackFor(p, media, previousChannel);
            p.Tracks.Add(track);
        }
        track.Clips.Add(clip);
        return track.Id;
    }

    /// <summary>Where <see cref="PlaceMedia"/> would put the take, without placing it: the track that takes it (null = a
    /// new one), how long the clip is and its name, for the ghost drawn while it is carried. Null if the media is gone.</summary>
    public static (string? TrackId, double LengthSec, string Name)? PlanMedia(Project p, string mediaId, double atSec, string? trackId)
    {
        if (p.FindMedia(mediaId) is not { } media) return null;
        var (clip, track) = Placement(p, media, atSec, trackId);
        var w = Window(clip, media);
        return (track?.Id, Math.Max(0, w.End - w.Start), media.Name);
    }

    /// <summary>The clip a take dropped at <paramref name="atSec"/> becomes (the cut it had off the timeline, if any) and
    /// the track that takes it; null = it needs a new one. Changes nothing.</summary>
    static (Clip Clip, Track? Track) Placement(Project p, Media media, double atSec, string? trackId)
    {
        bool used = p.Tracks.Any(t => t.Clips.Any(c => c.MediaId == media.Id));
        Clip? previousClip = null;
        if (!used && media.Extra?.TryGetValue("lastTimelineClip", out var clipJson) == true)
            previousClip = clipJson.Deserialize<Clip>(ProjectStore.Json);
        var clip = previousClip ?? new Clip { MediaId = media.Id };
        clip.AtSec = ToSample(clip.AtSec + Math.Max(0, atSec) - Window(clip, media).Start);
        var window = Window(clip, media);
        var track = p.Tracks.Find(t => t.Id == trackId && !t.IsMidi && (t.Clips.Count == 0 || IsFree(p, t, window.Start, window.End)));
        return (clip, track);
    }

    /// <summary>A clip can land on that stretch of the track without covering anything; a locked track takes none.</summary>
    public static bool IsFree(Project p, Track t, double start, double end) => !t.Locked && t.Clips.All(c =>
    {
        var w = Window(c, p.FindMedia(c.MediaId));
        return w.End <= start || w.Start >= end;
    });

    static void Remember(Project p, Track track, Clip clip)
    {
        if (p.FindMedia(clip.MediaId) is not { } media) return;
        media.Extra ??= [];
        media.Extra["lastTimelineClip"] = JsonSerializer.SerializeToElement(clip, ProjectStore.Json);
        media.Extra["lastTimelineChannel"] = JsonSerializer.SerializeToElement(track.Channel, ProjectStore.Json);
    }

    public static void RemoveTrack(Project p, string id)
    {
        if (p.Tracks.Find(t => t.Id == id) is not { } track) return;
        if (HasLock(track))
            throw new ArgumentException(Texts.T("err.track.locked", "«{0}» tiene clips bloqueados.", track.Name));
        foreach (var c in track.Clips) Remember(p, track, c);
        int index = p.Tracks.IndexOf(track);
        p.Tracks.Remove(track);
        TrackFolders.TrackRemoved(p, index);
    }

    public static void Reorder(Project p, int from, int to)
    {
        if (from < 0 || to < 0 || from >= p.Tracks.Count || to >= p.Tracks.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        var track = p.Tracks[from];
        p.Tracks.RemoveAt(from);
        p.Tracks.Insert(to, track);
        TrackFolders.Normalize(p);
    }

    public static void ToStart(Project p, string id, double? durationSec = null)
    {
        var (track, clip) = Find(p, id);
        Unlocked(track, clip);
        clip.AtSec = ToSample(clip.AtSec - Window(clip, p.FindMedia(clip.MediaId), durationSec).Anchor);
    }

    /// <summary>Puts a clip's start (its anchor, where the take begins) on the nearest line of the grid.</summary>
    public static void AlignToGrid(Project p, string id, SnapGrid grid, double? durationSec = null)
    {
        var (track, clip) = Find(p, id);
        Unlocked(track, clip);
        double anchor = Window(clip, p.FindMedia(clip.MediaId), durationSec).Anchor;
        clip.AtSec = ToSample(clip.AtSec + Snap(anchor, grid, false) - anchor);
    }

    public static void FixCut(Project p, string id, double? durationSec = null)
    {
        var (track, clip) = Find(p, id);
        Unlocked(track, clip);
        var w = Window(clip, p.FindMedia(clip.MediaId), durationSec);
        clip.TrimStartSec = ToSample(w.TrimStart + w.Cut);
    }

    /// <summary>Only sound goes backward: a clip with picture stays in step with its video.</summary>
    public static bool CanReverse(Media? m) => string.IsNullOrEmpty(m?.Video);

    /// <summary>The clip's own pitch, in whole semitones. A locked clip refuses it, as it refuses turning around: what it plays stays.</summary>
    public static void SetPitch(Project p, string id, double semitones)
    {
        var (track, c) = Find(p, id);
        Unlocked(track, c);
        c.Pitch = Math.Clamp(Math.Round(semitones), -XtremeDaw.Audio.Dsp.PitchShifter.MaxSemitones, XtremeDaw.Audio.Dsp.PitchShifter.MaxSemitones);
    }

    /// <summary>
    /// Turns a clip around where it stands (native only): the same stretch of the timeline plays the same audio backward.
    /// What was hidden at its end is now hidden at its start and the volume curve goes with the sound; its fades
    /// stay at the clip's edges. Applied twice it is the clip it was.
    /// </summary>
    public static void Reverse(Project p, string id, double? durationSec = null)
    {
        var (track, c) = Find(p, id);
        Unlocked(track, c);
        var m = p.FindMedia(c.MediaId);
        if (!CanReverse(m)) throw new ArgumentException(Texts.T("err.clip.reverseVideo", "Un clip con video no se puede invertir."));
        var w = Window(c, m, durationSec);
        c.AtSec = ToSample(w.Start - w.TrimEnd);
        c.TrimStartSec = w.TrimEnd;
        c.TrimEndSec = w.TrimStart + w.Cut;
        c.GainCurve = [.. c.GainCurve.Select(g => new GainPoint { Sec = w.Dur - g.Sec, Db = g.Db, Extra = g.Extra }).OrderBy(g => g.Sec)];
        c.Reversed = !c.Reversed;
    }

    /// <summary>
    /// Splits a clip at a timeline time into two clips of the same media (native only: the web has one take
    /// per lane). The left piece keeps the fade in, the right one the fade out and everything else (gain,
    /// framing, sync); the boundary is shared exactly, so playback and export carry on without a gap.
    /// Returns the right piece's id.
    /// </summary>
    public static string Split(Project p, string id, double atSec, double? durationSec = null)
    {
        var (track, left) = Find(p, id);
        Unlocked(track, left);
        var w = Window(left, p.FindMedia(left.MediaId), durationSec);
        if (atSec < w.Start + ClipEnvelope.MinClipSec || atSec > w.End - ClipEnvelope.MinClipSec)
            throw new ArgumentException(Texts.T("err.clip.splitEdge", "El cabezal tiene que estar dentro del clip, a más de 0,1 s de los bordes."));
        double trimStart = ToSample(atSec - w.Origin);
        var right = Copy(left);
        right.Id = Ids.New("clp");
        right.TrimStartSec = trimStart;
        right.FadeInSec = right.VideoFadeInSec = 0;
        right.Transition = ClipTransitions.CutId; // the transition in stays with the left piece's start
        left.TrimEndSec = w.Dur - trimStart;
        left.FadeOutSec = left.VideoFadeOutSec = 0;
        track.Clips.Insert(track.Clips.IndexOf(left) + 1, right);
        return right.Id;
    }

    /// <summary>
    /// Deletes what is between two timeline times on one track (native only): every clip the range touches is cut
    /// there, the middle goes and the rest stays where it was, leaving a gap (nothing slides to fill it). A clip
    /// inside the range goes whole; one that sticks out on one side is trimmed; one that sticks out on both is
    /// split in two, the left piece keeping the fade in and the right one the fade out (the cut edges get none).
    /// A leftover shorter than <see cref="ClipEnvelope.MinClipSec"/> goes too, like the rest of the editing does not
    /// allow smaller clips. Media files are never touched. Returns how many clips changed (0 = the range was empty).
    /// </summary>
    public static int DeleteRange(Project p, string trackId, double startSec, double endSec, Func<Clip, double?>? lengthOf = null)
    {
        var track = p.Tracks.Find(t => t.Id == trackId) ?? throw new ArgumentException(Texts.T("err.clip.trackNotFound", "Pista no encontrada."));
        if (!(endSec > startSec)) return 0;
        int changed = 0;
        foreach (var clip in track.Clips.ToList())
        {
            var w = Window(clip, p.FindMedia(clip.MediaId), lengthOf?.Invoke(clip));
            if (endSec <= w.Start || startSec >= w.End || IsLocked(track, clip)) continue;
            changed++;
            bool keepLeft = startSec - w.Start >= ClipEnvelope.MinClipSec;
            bool keepRight = w.End - endSec >= ClipEnvelope.MinClipSec;
            if (!keepLeft && !keepRight) { RemoveClip(p, clip.Id); continue; }
            if (keepLeft && keepRight)
            {
                var right = Copy(clip);
                right.Id = Ids.New("clp");
                right.TrimStartSec = ToSample(endSec - w.Origin);
                right.FadeInSec = right.VideoFadeInSec = 0;
                right.Transition = ClipTransitions.CutId;
                clip.TrimEndSec = ToSample(w.Dur - (startSec - w.Origin));
                clip.FadeOutSec = clip.VideoFadeOutSec = 0;
                track.Clips.Insert(track.Clips.IndexOf(clip) + 1, right);
            }
            else if (keepLeft)
            {
                clip.TrimEndSec = ToSample(w.Dur - (startSec - w.Origin));
                clip.FadeOutSec = clip.VideoFadeOutSec = 0;
            }
            else
            {
                clip.TrimStartSec = ToSample(endSec - w.Origin);
                clip.FadeInSec = clip.VideoFadeInSec = 0;
            }
        }
        return changed + MidiEditing.DeleteRange(p, track, startSec, endSec);
    }

    /// <summary>
    /// Deletes a range on several tracks at once and slides what comes after it on those tracks to the left by the
    /// range's length, closing the gap (native only, "Borrar y cerrar el hueco"). Only the given tracks move: the
    /// others stay, so a range over some tracks shifts them against the rest (as in any DAW). When it covers every
    /// track the song itself got shorter, so the markers follow (<see cref="ProjectMarkers.CloseGap"/>); the A–B
    /// range stays, since it is transport state outside undo. Returns how many clips changed (cut, removed or moved).
    /// A track with a locked clip from the range on stays as it is (closing the gap would move that clip).
    /// </summary>
    public static int DeleteRangeClosing(Project p, IEnumerable<string> trackIds, double startSec, double endSec, Func<Clip, double?>? lengthOf = null)
    {
        if (!(endSec > startSec)) return 0;
        double len = endSec - startSec;
        int changed = 0;
        bool Holds(Track t) => t.Clips.Any(c => IsLocked(t, c) && Window(c, p.FindMedia(c.MediaId), lengthOf?.Invoke(c)).End > startSec)
            || t.Locked && t.MidiClips?.Any(c => c.EndSec(p.Settings.Bpm) > startSec) == true;
        var ids = trackIds.Distinct().Where(id => p.Tracks.Find(t => t.Id == id) is { } t && !Holds(t)).ToList();
        if (p.Tracks.All(t => ids.Contains(t.Id)))
        {
            ProjectMarkers.CloseGap(p, startSec, endSec);
            // the whole song moved: the folders' lanes go with it
            foreach (var f in p.Folders) Automations.CloseGap(f, startSec, endSec);
        }
        foreach (string id in ids)
        {
            changed += DeleteRange(p, id, startSec, endSec, lengthOf);
            Automations.CloseGap(p.Tracks.Find(t => t.Id == id)!, startSec, endSec);
            // after the cut every clip on the track is either before the range or from its end on
            foreach (var clip in p.Tracks.Find(t => t.Id == id)!.Clips)
            {
                if (Window(clip, p.FindMedia(clip.MediaId), lengthOf?.Invoke(clip)).Start < endSec - HalfSample) continue;
                clip.AtSec = ToSample(clip.AtSec - len);
                changed++;
            }
            foreach (var clip in p.Tracks.Find(t => t.Id == id)!.MidiClips ?? [])
            {
                if (clip.AtSec < endSec - HalfSample) continue;
                clip.AtSec = ToSample(clip.AtSec - len);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Half a sample at 48 kHz: what <see cref="ToSample"/> may move an edge by, so a clip cut at a time still counts as starting there.</summary>
    const double HalfSample = 0.5 / SamplesPerSec;

    /// <summary>
    /// Puts a clip last on its track (native only, "Traer al frente"): the last one is drawn on top and is the one whose
    /// picture shows where clips overlap, in the preview (<c>LiveTrack.ShownClip</c>) and the export. The sound does not
    /// change: overlapping clips both play. Returns false if it was already in front.
    /// </summary>
    public static bool BringToFront(Project p, string id)
    {
        var (track, clip) = Find(p, id);
        if (track.Clips[^1] == clip) return false;
        track.Clips.Remove(clip);
        track.Clips.Add(clip);
        return true;
    }

    /// <summary>
    /// Copies a clip on its own track right after its end (D in Studio One), including the part a cut at 0
    /// hides, so the copy starts exactly where the original stops. Returns the copy's id.
    /// </summary>
    public static string Duplicate(Project p, string id, double? durationSec = null) =>
        DuplicateMany(p, [id], durationSec is { } d ? _ => d : null)[0];

    /// <summary>
    /// D on several clips (a selection box, even across tracks): they are copied as one block right after the block's
    /// end, each copy on its original's track and at the same distance from the others, so a phrase played on several
    /// tracks repeats whole. With one clip it is <see cref="Duplicate"/>. Returns the copies' ids, in the given order.
    /// </summary>
    /// <param name="shift">How far the copies go; null = right after the block the clips make (<see cref="DuplicateShift"/>).</param>
    /// <param name="step">The magnet's step in seconds (0 = off).</param>
    public static List<string> DuplicateMany(Project p, IReadOnlyList<string> ids, Func<Clip, double?>? lengthOf = null, double? shift = null, double step = 0)
    {
        var picked = ids.Distinct().Select(id => Find(p, id)).ToList();
        if (picked.Count == 0) return [];
        var (start, end) = Span(p, picked.Select(x => x.Clip), lengthOf);
        shift ??= DuplicateShift(end - start, step);
        List<string> copies = [];
        foreach (var (track, clip) in picked)
        {
            var copy = Copy(clip);
            copy.Id = Ids.New("clp");
            copy.LoopOf = null;
            copy.AtSec = ToSample(clip.AtSec + shift.Value);
            track.Clips.Insert(track.Clips.IndexOf(clip) + 1, copy);
            copies.Add(copy.Id);
        }
        return copies;
    }

    /// <summary>
    /// How far D moves a block <paramref name="length"/> long: with the magnet on, the next whole number of its steps
    /// (asked by the user), so a copy keeps its place on the grid and the next ones go at that spacing; with it off, right
    /// after the block.
    /// </summary>
    public static double DuplicateShift(double length, double step) =>
        step > 0 ? Math.Max(1, Math.Ceiling(length / step - StepSlack)) * step : length;
    /// <summary>In steps: a block that floating point left a hair over a whole number of steps still fits in it.</summary>
    const double StepSlack = 1e-6;

    /// <summary>Where a group of clips starts and ends on the timeline, counting the part a cut at 0 hides.</summary>
    public static (double Start, double End) Span(Project p, IEnumerable<Clip> clips, Func<Clip, double?>? lengthOf = null)
    {
        double start = double.MaxValue, end = double.MinValue;
        foreach (var c in clips)
        {
            var w = Window(c, p.FindMedia(c.MediaId), lengthOf?.Invoke(c));
            start = Math.Min(start, w.Origin + w.TrimStart);
            end = Math.Max(end, w.End);
        }
        return (start, end);
    }

    /// <summary>Clip gain in dB, within <see cref="Clip.MinGainDb"/>..<see cref="Clip.MaxGainDb"/>.</summary>
    public static void SetGain(Project p, string id, double db) =>
        Find(p, id).Clip.GainDb = Math.Clamp(double.IsFinite(db) ? db : 0, Clip.MinGainDb, Clip.MaxGainDb);

    /// <summary>Replaces the clip's volume curve (a new list, sorted and clamped like <see cref="Clip"/>'s Normalize).</summary>
    public static void SetCurve(Project p, string id, IEnumerable<GainPoint> points)
    {
        var c = Find(p, id).Clip;
        c.GainCurve = [.. points.Where(g => double.IsFinite(g.Sec) && double.IsFinite(g.Db)).OrderBy(g => g.Sec)
            .Select(g => new GainPoint { Sec = g.Sec, Db = Math.Clamp(g.Db, ClipGainCurve.MinDb, ClipGainCurve.MaxDb) })];
    }

    /// <summary>
    /// How a clip comes in from the one before it (native only, <see cref="ClipTransitions"/>): the
    /// kind ("cut", "crossfade", "dip") and its length in seconds; null keeps that part. Never touches the clip's own fades.
    /// </summary>
    public static void SetTransition(Project p, string id, string? kind, double? sec)
    {
        var c = Find(p, id).Clip;
        if (kind != null) c.Transition = ClipTransitions.Id(ClipTransitions.Parse(kind));
        if (sec is { } s) c.TransitionSec = Math.Round(ClipTransitions.ClampSec(s), 3);
    }

    /// <summary>
    /// The joins of a track where one clip ends and another starts (native only), with the transition each one
    /// has (<see cref="ClipTransitions.Find"/>; null = a cut). <paramref name="lengthOf"/> gives a clip's media length when known.
    /// </summary>
    public static List<(Clip Out, Clip In, ClipTransitions.Join? Join)> Joins(Project p, Track track, Func<Clip, double?>? lengthOf = null)
    {
        var w = track.Clips.Select(c => Window(c, p.FindMedia(c.MediaId), lengthOf?.Invoke(c))).ToArray();
        var list = new List<(Clip, Clip, ClipTransitions.Join?)>();
        for (int i = 0; i < w.Length; i++)
        {
            int o = ClipTransitions.FindOut(w, i);
            if (o < 0) continue;
            var c = track.Clips[i];
            list.Add((track.Clips[o], c, ClipTransitions.Find(w, i, ClipTransitions.Parse(c.Transition), c.TransitionSec)));
        }
        return list;
    }

    public static void Reset(Project p, string id)
    {
        var (track, c) = Find(p, id);
        Unlocked(track, c);
        c.TrimStartSec = c.TrimEndSec = c.FadeInSec = c.FadeOutSec = c.VideoFadeInSec = c.VideoFadeOutSec = c.GainDb = 0;
        c.GainCurve = [];
    }
}
