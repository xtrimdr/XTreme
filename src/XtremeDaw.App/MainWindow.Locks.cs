using Avalonia.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Locking takes and tracks (native only): a locked clip, or every clip of a locked track, keeps its place and length
/// (<see cref="ClipEditing.IsLocked"/>, the same rule xtlab uses). The timeline doesn't start the gesture, the commands
/// skip it or are greyed out, and a new take never replaces it: here the window says why, in a toast with a button that
/// unlocks it, so a locked take is never a dead end.
/// </summary>
public partial class MainWindow
{
    void InitLocks() => Timeline.LockedTouched += (t, c) => ToastLocked(t.Track, c.Clip.Id);

    /// <summary>A slider dragged over a locked clip asks many times a second: one toast is enough.</summary>
    static readonly TimeSpan LockToastGap = TimeSpan.FromSeconds(2);
    DateTime lastLockToast;

    /// <summary>"Ese clip está bloqueado…" (or the track), with a button that unlocks it.</summary>
    void ToastLocked(Track t, string? clipId = null)
    {
        if (DateTime.UtcNow - lastLockToast < LockToastGap) return;
        lastLockToast = DateTime.UtcNow;
        if (t.Locked)
            Toast(Loc.T("lock.toast.track", t.Name), action: (Loc.T("lock.unlock"), () => _ = SetTrackLocked(t.Id, false)));
        else if (clipId != null)
            Toast(Loc.T("lock.toast.clip"), action: (Loc.T("lock.unlock"), () => _ = SetClipLocked(t.Id, clipId, false)));
        else Toast(Loc.T("lock.toast.some", t.Name));
    }

    /// <summary>True (and says so) when the clip can't be moved, trimmed, split or deleted.</summary>
    bool RefuseLocked(LiveTrack t, LiveClip c)
    {
        if (!ClipEditing.IsLocked(t.Track, c.Clip)) return false;
        ToastLocked(t.Track, c.Clip.Id);
        return true;
    }

    /// <summary>
    /// The ids an edit over several clips may touch: the locked takes stay out, and the MIDI clips of a locked track (they
    /// have no lock of their own yet). Says how many stayed when some did.
    /// </summary>
    List<string> WithoutLocked(IReadOnlyList<string> ids)
    {
        if (store == null) return [.. ids];
        var p = store.Project;
        var free = ids.Where(id => !ClipEditing.IsLocked(p, id) && (MidiEditing.TryFind(p, id) == null || !MidiEditing.Find(p, id).Track.Locked)).ToList();
        if (free.Count < ids.Count) Toast(Loc.T("lock.toast.skipped", ids.Count - free.Count));
        return free;
    }

    Task SetClipLocked(string trackId, string clipId, bool locked) =>
        EditTimeline(p => { ClipEditing.SetLocked(p, clipId, locked); return trackId; }, locked ? "clip-locked" : "clip-unlocked", clipId);

    Task SetTrackLocked(string trackId, bool locked) =>
        EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == trackId) is { } t) t.Locked = locked;
            return trackId;
        }, locked ? "track-locked" : "track-unlocked");

    bool ClipIsLocked(string clipId) => store != null && store.Project.Tracks.Any(t => t.Clips.Any(c => c.Id == clipId && c.Locked));

    bool TrackIsLocked(string trackId) => store?.Project.Tracks.Find(t => t.Id == trackId)?.Locked == true;

    /// <summary>The clip menu's "Bloquear clip", ticked while it is locked; on a locked track it says the track holds it.</summary>
    MenuItem ClipLockItem(LiveTrack track, LiveClip clip, bool enabled)
    {
        string trackId = track.Track.Id, clipId = clip.Clip.Id;
        if (track.Track.Locked)
            return MenuAction(Loc.T("lock.clip.byTrack"), () => SetTrackLocked(trackId, false), enabled: enabled, icon: "",
                tip: Loc.T("lock.clip.byTrack.tip"));
        return MenuCheck(Loc.T("lock.clip.on"), () => ClipIsLocked(clipId), () => _ = SetClipLocked(trackId, clipId, !ClipIsLocked(clipId)), [],
            tip: Loc.T("lock.clip.tip"), enabled: enabled);
    }

    /// <summary>The track menu's "Bloquear pista", ticked while it is locked.</summary>
    MenuItem TrackLockItem(string trackId, bool enabled, List<Action> refresh) =>
        MenuCheck(Loc.T("lock.track.on"), () => TrackIsLocked(trackId), () => _ = SetTrackLocked(trackId, !TrackIsLocked(trackId)), refresh,
            tip: Loc.T("lock.track.tip"), enabled: enabled);
}
