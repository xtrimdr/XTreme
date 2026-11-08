using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Dividing and joining MIDI clips from the window: the Dividir tool, Alt+X, Ctrl+J and the clip's menu all end here,
/// one undo step that never changes what sounds (<see cref="MidiEditing.Split"/>, <see cref="MidiEditing.Join"/>). What
/// the cut does to a pattern's clip is said in a toast, since the right piece may leave the pattern.
/// </summary>
public partial class MainWindow
{
    /// <summary>Cuts the clip at <paramref name="at"/> and selects the right piece.</summary>
    async Task SplitMidi(LiveTrack track, string clipId, double at)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) is not { } clip) return;
        if (track.Track.EditLocked) { ToastLocked(track.Track); return; }
        if (!MidiEditing.CanSplit(clip, at, store.Project.Settings.TempoMap)) { Toast(Loc.T("midi.split.edge")); return; }
        bool pattern = clip.PatternId != null;
        string? right = null;
        await EditTimeline(p => { right = MidiEditing.Split(p, clipId, at); return track.Track.Id; }, "midi-clip-split");
        if (right == null || store == null) return;
        Timeline.SelectMidiClip(TrackIndex(track.Track.Id), right);
        if (pattern && MidiEditing.TryFind(store.Project, right) is { PatternId: null }) Toast(Loc.T("midi.split.unique"));
    }

    /// <summary>The boxed MIDI clips that can be joined (#311): those on unlocked tracks that have two or more boxed.</summary>
    List<string> JoinableMidi()
    {
        if (store == null) return [];
        var ids = Timeline.BoxedClipIds.ToHashSet();
        return [.. store.Project.Tracks.Where(t => !t.EditLocked)
            .Select(t => (t.MidiClips ?? []).Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToList())
            .Where(l => l.Count > 1).SelectMany(l => l)];
    }

    /// <summary>
    /// Ctrl+J and the clip's menu (#311): the boxed MIDI clips of each track become one, sounding as they did
    /// (<see cref="MidiEditing.Join"/>), one undo step; the joined clips stay selected.
    /// </summary>
    async Task JoinMidi()
    {
        if (store == null) return;
        var ids = JoinableMidi();
        if (ids.Count < 2) { Toast(Loc.T("midi.join.few")); return; }
        List<string> joined = [];
        await EditTimeline(p => { joined = MidiEditing.Join(p, ids); return MidiEditing.Find(p, joined[0]).Track.Id; }, "midi-clip-join");
        if (store == null || joined.Count == 0 || MidiEditing.TryFind(store.Project, joined[0]) == null) return;
        if (joined.Count > 1) Timeline.BoxClips(joined);
        else Timeline.SelectMidiClip(TrackIndex(MidiEditing.Find(store.Project, joined[0]).Track.Id), joined[0]);
    }

    /// <summary>Alt+X on a MIDI track: the selected clip if the playhead is on it, else the one on top there.</summary>
    Task SplitMidiAtPlayhead()
    {
        if (sel == null || store == null) return Task.CompletedTask;
        double at = engine.PositionSec;
        var map = store.Project.Settings.TempoMap;
        bool Inside(MidiClip c) => at > c.AtSec && at < c.EndSec(map);
        var clips = sel.Track.MidiClips ?? [];
        if ((clips.Find(c => c.Id == Timeline.SelectedMidiId && Inside(c)) ?? clips.FindLast(Inside)) is { } clip) return SplitMidi(sel, clip.Id, at);
        Toast(Loc.T("timeline.split.nowhere"));
        return Task.CompletedTask;
    }
}
