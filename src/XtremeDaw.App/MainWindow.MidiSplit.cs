using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Dividing MIDI clips from the window: the Dividir tool, Alt+X and the clip's menu all end here, one undo step that
/// never changes what sounds (<see cref="MidiEditing.Split"/>). What the cut does to a pattern's clip is said in the
/// bell, since the right piece may leave the pattern.
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
