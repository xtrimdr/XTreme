using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;

namespace XtremeDaw.App;

/// <summary>
/// The Canal tab with several tracks picked on the timeline (asked by the user): it shows what every track has, audio or
/// MIDI (Mezcla, the output and sends they share, Transponer; En el video only without MIDI tracks), under a note that
/// says how many it edits; an effect or a clip belongs to one track, so those stay hidden. An edit sets the same value
/// on all of them in one undo step, and a value they don't share reads as "Varios". Locked tracks stay out, like in
/// every edit over several clips (<see cref="WithoutLocked"/>), and the note says so instead of a toast per frame of a drag.
/// </summary>
public partial class MainWindow
{
    readonly TextBlock groupNote = new() { FontSize = 12, Foreground = Palette.Accent, TextWrapping = TextWrapping.Wrap };
    readonly Border groupHead = new() { Background = Palette.AccentSoft, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7), IsVisible = false };
    readonly TextBlock groupMuted = new() { FontSize = 11, Foreground = Palette.Faint };

    /// <summary>The picked tracks in order, locked ones included; null with a track on its own.</summary>
    List<LiveTrack>? PickedGroup()
    {
        if (sel == null || TrackGroup(sel) is not { } ids) return null;
        var list = tracks.Where(t => ids.Contains(t.Track.Id)).ToList();
        return list.Count > 1 ? list : null;
    }

    /// <summary>What an edit of the inspector's common controls reaches: the selected track, or the picked ones that aren't locked.</summary>
    List<LiveTrack> PickTargets() => PickedGroup() is { } group ? group.Where(t => !t.Track.Locked).ToList() : sel == null ? [] : [sel];

    /// <summary>The track the common controls read: the selected one, unless it is locked out of the group.</summary>
    LiveTrack PickLead => PickTargets().FirstOrDefault() ?? sel!;

    /// <summary>The selected track while it is on its own: what only makes sense for one (its automation lanes).</summary>
    LiveTrack? SingleTrack => PickedGroup() == null ? sel : null;

    /// <summary>The tracks reached don't share this value.</summary>
    bool PicksDiffer(Func<LiveTrack, double> value)
    {
        var list = PickTargets();
        return list.Any(t => value(t) != value(list[0]));
    }

    void InitGroupInspector()
    {
        groupHead.Child = groupNote;
        Inspector.Children.Add(groupHead);
    }

    /// <summary>"3 pistas · los cambios se aplican a todas", or who stays out.</summary>
    void RefreshGroupNote(List<LiveTrack>? group)
    {
        groupHead.IsVisible = group != null;
        if (group == null) return;
        int locked = group.Count(t => t.Track.Locked);
        groupNote.Text = locked == group.Count ? Loc.T("insp.multi.allLocked", group.Count)
            : locked == 0 ? Loc.T("insp.multi", group.Count)
            : locked == 1 ? Loc.T("insp.multi.locked.one", group.Count)
            : Loc.T("insp.multi.locked", group.Count, locked);
    }

    /// <summary>An edit of the inspector's common controls: on the selected track alone, or on the picked ones.</summary>
    void EditPicked(Action<LiveTrack> change, string key)
    {
        if (syncing || sel == null) return;
        if (PickedGroup() == null) EditTrack(sel, change, key);
        else EditTracks(PickTargets(), change, key);
    }

    /// <summary>The same edit on several tracks: one undo step per gesture, however many they are.</summary>
    void EditTracks(IReadOnlyList<LiveTrack> list, Action<LiveTrack> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly || list.Count == 0) return;
        undo.Checkpoint(store.Project, $"tracks:{string.Join(",", list.Select(t => t.Track.Id))}:{key}");
        foreach (var t in list)
        {
            change(t);
            t.ApplyChannel();
        }
        SyncEffects();
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
    }

    /// <summary>Mute (or solo) of the selection: all on, unless all of them already are.</summary>
    void TogglePicked(bool solo)
    {
        bool to = !PickTargets().All(t => solo ? t.Channel.Solo : t.Channel.Muted);
        EditPicked(t => { if (solo) t.Channel.Solo = to; else t.Channel.Muted = to; }, solo ? "solo" : "mute");
    }

    /// <summary>"En el video" of several tracks at once: one undo step.</summary>
    async Task SetInVideo(IReadOnlyList<LiveTrack> list, bool on)
    {
        if (store == null || store.ReadOnly || restoring || list.All(t => t.Track.Video == on)) return;
        undo.Checkpoint(store.Project);
        foreach (var t in list) t.Track.Video = on;
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
        await OpenMissingVideos(store);
    }

    /// <summary>The output and sends of the picked tracks: reads the first one reached, edits all of them.</summary>
    Router GroupRouter(List<LiveTrack> group) => new()
    {
        Get = () => PickTargets().FirstOrDefault()?.Channel,
        Group = () => [.. PickTargets().Select(t => (Projects.IRouted)t.Channel)],
        // who they are, and whether the output is theirs to choose
        Key = "group|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group[0]) + "|"
            + string.Join(",", group.Select(t => t.Track.Id + (t.Track.Locked ? "!" : ""))) + "|" + group.Any(t => t.Track.FolderId != null),
        Edit = (change, key) =>
        {
            EditTracks(PickTargets(), x => change(store!.Project, x.Channel), key);
            if (key == "output" && store != null) ApplyRouting(store.Project, tracks);
            RefreshMixer();
        },
    };
}
