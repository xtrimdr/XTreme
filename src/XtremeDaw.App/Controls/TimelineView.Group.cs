using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// An edit on the edge, fade or loop corner of one of several boxed clips does the same to every boxed clip (asked by
/// the user, as in any DAW): the grabbed one follows the pointer with the grid, and each of the others moves the same
/// edge by the same time, so they keep their offsets. Takes and MIDI clips go together: a MIDI clip's right edge is a
/// take's end, its left edge a take's start (a pattern's clip has none) and fades are only for takes. A locked clip
/// keeps its length but its fades still change, as on its own. Boxed automation blocks go too (#281): their right edge
/// or loop with a clip's end or loop, their left edge with a clip's start (fades are only for takes), and a boxed
/// block's edge grabbed in its lane edits the boxed clips the same way. The release hands everything to the window in
/// one event (<see cref="ClipsEdited"/>), so the whole gesture is one undo step.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>A group edit ended: each take as it ends (the grabbed one too), each take's loop (its source and where
    /// it ends now), each MIDI clip's change and each automation block as it ends (same id, #281).</summary>
    public event Action<IReadOnlyList<Clip>, IReadOnlyList<(string Source, double End)>, IReadOnlyList<MidiGroupEdit>, IReadOnlyList<AutomationBlock>>? ClipsEdited;

    public enum MidiEditKind { Resize, Trim, Loop }

    /// <summary>What a group edit does to a MIDI clip: its new start and length in beats, and how (<see cref="MidiEditing"/>).</summary>
    public readonly record struct MidiGroupEdit(string Id, double AtSec, double Beats, MidiEditKind Kind);

    /// <summary>The edit every clip of a group edit gets, in the terms of a take (null = no group edit going on).</summary>
    ClipEditMode? groupMode;
    /// <summary>The other boxed MIDI clips a group edit changes: where each started and its length, and where it is now.</summary>
    readonly Dictionary<MidiClip, (double FromSec, double FromBeats, double AtSec, double Beats)> midiGroup = [];
    /// <summary>The boxed automation blocks a group edit changes (#281): each one as it is now (a copy, same id) and whether its lane steps.</summary>
    readonly Dictionary<AutomationBlock, (AutomationBlock Now, bool Stepped)> autoGroup = [];
    /// <summary>The group edit started on a block's edge (<see cref="autoBlock"/>), not on a clip's.</summary>
    bool autoGrouped;

    /// <summary>Whether a press on an edge, fade or loop corner of the boxed clip <paramref name="id"/> edits all the boxed ones.</summary>
    bool GroupEdits(string id) => boxed.Count > 1 && boxed.Contains(id);

    /// <summary>
    /// Notes what a group edit changes besides the grabbed clip (<paramref name="take"/> on lane <paramref name="lane"/>,
    /// or <paramref name="midi"/>): the other boxed takes (<see cref="carriedAlong"/>, or their loops) and MIDI clips.
    /// </summary>
    void StartGroup(ClipEditMode mode, int lane, LiveClip? take, MidiClip? midi)
    {
        groupMode = mode;
        carriedAlong.Clear();
        carriedPreview.Clear();
        midiGroup.Clear();
        bool fade = mode is ClipEditMode.FadeIn or ClipEditMode.FadeOut;
        string? grabbedSource = take != null ? LoopSourceOf(lane, take).Clip.Id : null;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            foreach (var c in t.Clips)
            {
                if (c == take || !boxed.Contains(c.Clip.Id)) continue;
                if (!fade && ClipEditing.IsLocked(t.Track, c.Clip)) continue;
                if (mode != ClipEditMode.Loop)
                {
                    // a repeat follows its source (ClipLoops.Keep): it is never edited on its own
                    if (c.Clip.LoopOf == null) carriedAlong.Add((c, ClipEditing.Copy(c.Clip)));
                    continue;
                }
                var source = LoopSourceOf(i, c);
                if (source.Clip.Id == grabbedSource || ClipEditing.IsLocked(t.Track, source.Clip) || loopRuns.Exists(r => r.Source == source.Clip.Id)) continue;
                if (loopRuns.Count == 0) loopRuns.Add(new LoopRun());
                loopRuns.Add(LoopRunOf(i, source));
            }
            if (fade || t.Track.EditLocked || t.Track.MidiClips is not { } clips) continue;
            foreach (var c in clips)
            {
                if (c == midi || !boxed.Contains(c.Id) || midiAlong.Contains(c)) continue;
                // a pattern's clip has no left edge: its notes count from its start
                if (mode is ClipEditMode.TrimStart or ClipEditMode.StretchStart && c.PatternId != null) continue;
                midiGroup[c] = (c.AtSec, c.LengthBeats, c.AtSec, c.LengthBeats);
            }
        }
        autoGroup.Clear();
        if (fade) return;
        foreach (var (owner, l) in AllOwnedLanes())
        {
            bool stepped = Automations.Stepped(owner, l.Target);
            foreach (var b in l.Blocks)
                if (b != autoBlock && boxed.Contains(b.Id)) autoGroup[b] = (Automate(b), stepped);
        }
    }

    /// <summary>The group's edit as a MIDI clip's: its right edge, its left edge or its loop.</summary>
    MidiEditKind MidiKind => groupMode switch
    {
        ClipEditMode.TrimStart or ClipEditMode.StretchStart => MidiEditKind.Trim,
        ClipEditMode.Loop => MidiEditKind.Loop,
        _ => MidiEditKind.Resize,
    };

    /// <summary>
    /// How far the grabbed clip's edge went (seconds), as the pointer move that does it without the grid: a fade's
    /// length change (the fade out's, negated: its tab moves the other way), or how far its loop's end or edge moved.
    /// </summary>
    double GroupShift()
    {
        if (autoGrouped && autoBlock is { } held0 && autoDraft is { } now0)
            return groupMode is ClipEditMode.TrimStart ? now0.AtSec - held0.AtSec : now0.EndSec - held0.EndSec;
        if (dragged != null && original != null && preview != null)
        {
            if (groupMode == ClipEditMode.Loop) return loopRuns.Count > 0 ? loopRuns[0].End - loopRuns[0].From : 0;
            ClipEnvelope.ClipWindow was = Window(dragged, original), now = Window(dragged, preview);
            return groupMode switch
            {
                ClipEditMode.TrimStart or ClipEditMode.StretchStart => now.Start - was.Start,
                ClipEditMode.FadeIn => ClipEnvelope.FitFades(preview.FadeInSec, preview.FadeOutSec, now.End - now.Start).FadeIn
                    - ClipEnvelope.FitFades(original.FadeInSec, original.FadeOutSec, was.End - was.Start).FadeIn,
                ClipEditMode.FadeOut => ClipEnvelope.FitFades(original.FadeInSec, original.FadeOutSec, was.End - was.Start).FadeOut
                    - ClipEnvelope.FitFades(preview.FadeInSec, preview.FadeOutSec, now.End - now.Start).FadeOut,
                _ => now.End - was.End,
            };
        }
        if (midiHeld == null) return 0;
        return midiTrimStart ? midiAtSec - midiFromSec : SecPlus(midiFromSec, midiBeats) - SecPlus(midiFromSec, midiFromBeats);
    }

    /// <summary>Moves the edge of every other clip of the group by <see cref="GroupShift"/>, each within its own limits.</summary>
    void DragGroup()
    {
        if (groupMode is not { } mode) return;
        double d = GroupShift();
        foreach (var (c, o) in carriedAlong)
            carriedPreview[c] = ClipEditing.Drag(o, c.Media, mode, d, Grid, true, c.LengthSec);
        for (int k = 1; k < loopRuns.Count; k++)
            loopRuns[k].End = Math.Max(loopRuns[k].Start + ClipEnvelope.MinClipSec, loopRuns[k].From + d);
        var kind = MidiKind;
        foreach (var c in midiGroup.Keys.ToList())
        {
            var g = midiGroup[c];
            if (kind == MidiEditKind.Trim)
            {
                double end = SecPlus(g.FromSec, g.FromBeats);
                double at = Math.Clamp(g.FromSec + d, 0, SecPlus(end, -MidiClip.MinBeats));
                midiGroup[c] = g with { AtSec = at, Beats = BeatsBetween(at, end) };
            }
            else midiGroup[c] = g with { Beats = Math.Clamp(BeatsBetween(g.FromSec, SecPlus(g.FromSec, g.FromBeats) + d), MidiClip.MinBeats, MidiClip.MaxBeats) };
        }
        foreach (var b in autoGroup.Keys.ToList()) autoGroup[b] = (GroupBlock(b, mode, d, autoGroup[b].Stepped), autoGroup[b].Stepped);
    }

    /// <summary>A boxed block with the group's edge moved by <paramref name="d"/>, as its own edge would (<see cref="DragAuto"/>).</summary>
    static AutomationBlock GroupBlock(AutomationBlock b, ClipEditMode mode, double d, bool stepped)
    {
        var now = Automate(b);
        switch (mode)
        {
            case ClipEditMode.TrimStart or ClipEditMode.StretchStart:
            {
                double at = Math.Clamp(b.AtSec + d, 0, b.EndSec - AutomationBlock.MinSec);
                var trimmed = b.Cut(at - b.AtSec, b.LengthSec, stepped);
                (now.AtSec, now.LengthSec, now.Points, now.LoopSec) = (trimmed.AtSec, trimmed.LengthSec, trimmed.Points, trimmed.LoopSec);
                break;
            }
            case ClipEditMode.Loop:
                now.LoopSec = b.LoopSec ?? b.LengthSec;
                now.LengthSec = Math.Max(AutomationBlock.MinSec, b.LengthSec + d);
                if (now.LengthSec <= now.LoopSec + 1e-9) now.LoopSec = null;
                break;
            case ClipEditMode.FadeIn or ClipEditMode.FadeOut:
                break;
            default:
                now.LengthSec = Math.Max(AutomationBlock.MinSec, b.LengthSec + d);
                if (now.LoopSec is { } pass && now.LengthSec <= pass + 1e-9) now.LoopSec = null;
                break;
        }
        return now;
    }

    /// <summary>What the group edit ends in, the grabbed clip included (taken before the gesture is cleared): null when
    /// there was none.</summary>
    (List<Clip> Takes, List<(string Source, double End)> Loops, List<MidiGroupEdit> Midi, List<AutomationBlock> Blocks)? TakeGroup()
    {
        if (groupMode == null) return null;
        List<AutomationBlock> blocks = [];
        if (autoGrouped && autoDraft != null) blocks.Add(autoDraft);
        foreach (var (b, g) in autoGroup)
            if (g.Now.AtSec != b.AtSec || g.Now.LengthSec != b.LengthSec || g.Now.LoopSec != b.LoopSec) blocks.Add(g.Now);
        List<Clip> takes = [];
        List<(string, double)> loops = [];
        List<MidiGroupEdit> midi = [];
        var kind = MidiKind;
        if (dragged != null && preview != null && groupMode != ClipEditMode.Loop) takes.Add(preview);
        takes.AddRange(carriedPreview.Values);
        foreach (var run in loopRuns)
            if (run.Source.Length > 0 && Math.Abs(run.End - run.From) > 1e-6) loops.Add((run.Source, run.End));
        if (midiHeld is { } held && (midiAtSec != held.AtSec || midiBeats != held.LengthBeats))
        {
            midi.Add(new(held.Id, midiAtSec, midiBeats, held.PatternId != null && kind == MidiEditKind.Loop ? MidiEditKind.Resize : kind));
            // the other instruments of its pattern's use take its length, as on its own
            foreach (var o in midiAlong) midi.Add(new(o.Id, o.AtSec, midiBeats, MidiEditKind.Resize));
        }
        foreach (var (c, g) in midiGroup)
            if (g.AtSec != c.AtSec || g.Beats != c.LengthBeats)
                midi.Add(new(c.Id, g.AtSec, g.Beats, c.PatternId != null && kind == MidiEditKind.Loop ? MidiEditKind.Resize : kind));
        return (takes, loops, midi, blocks);
    }

    void ClearGroup()
    {
        groupMode = null;
        midiGroup.Clear();
        autoGroup.Clear();
        autoGrouped = false;
    }

    void RaiseClipsEdited(List<Clip> takes, List<(string Source, double End)> loops, List<MidiGroupEdit> midi, List<AutomationBlock> blocks)
    {
        if (takes.Count > 0 || loops.Count > 0 || midi.Count > 0 || blocks.Count > 0) ClipsEdited?.Invoke(takes, loops, midi, blocks);
    }
}
