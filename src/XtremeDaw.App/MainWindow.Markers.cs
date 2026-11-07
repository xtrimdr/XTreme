using Avalonia;
using Avalonia.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The A–B loop range, the song's markers and the chords (native only, asked by the user), each on its strip over the
/// ruler (<see cref="Controls.TimelineView"/>). Chords come from Detectar escala or are written by hand. Markers are edits of the project, one undo step each; the range is transport
/// state (saved, outside undo) and turns the loop on when it is drawn or set. The keys work on the
/// playhead, so the guitar never has to leave the hands: I and O set A and B, K drops a marker, comma and period jump.
/// </summary>
public partial class MainWindow
{
    /// <summary>Names offered for a marker in its menu, the parts of a song (they stay in Spanish like "Canal 1").</summary>
    static readonly string[] MarkerNames = ["Intro", "Estrofa", "Estribillo", "Puente", "Solo", "Final"];

    void InitMarkers()
    {
        Timeline.LoopOn = LoopToggle.IsChecked == true;
        LoopToggle.IsCheckedChanged += (_, _) => Timeline.LoopOn = LoopToggle.IsChecked == true;
        Timeline.LoopRangeRequested += (a, b) => SetLoopRange(a, b);
        Timeline.MarkerMoveRequested += (id, sec) => MarkerEdit(p => ProjectMarkers.Move(p, id, sec), $"marker:{id}:move");
        Timeline.MarkerAddRequested += sec => AddMarker(sec, rename: true);
        Timeline.MarkerRenameRequested += BeginNameEdit;
        Timeline.StripContextRequested += ShowStripMenu;
        Timeline.LoopClearRequested += ClearLoopRange;
        Timeline.RangeContextRequested += ShowRangeMenu;
        Timeline.ChordContextRequested += ShowChordMenu;
        Timeline.MarkersErased += ids => MarkerEdit(p =>
        {
            foreach (var id in ids) ProjectMarkers.Remove(p, id);
        });
        Timeline.ChordsErased += ids => RemoveChords(ids);
        Timeline.ChordAddRequested += BeginChordEntry;
        Timeline.ChordRenameRequested += BeginNameEdit;
        Timeline.ChordTimesRequested += times => MarkerEdit(p => ProjectMarkers.SetChordTimes(p, times));
    }

    bool CanEditMarkers => store != null && !store.ReadOnly && !restoring && !RecordingBusy;

    /// <summary>Hands the project's markers and range to the timeline and the range to the engine (after opening, undo, every edit).</summary>
    void RefreshMarkers()
    {
        var p = store?.Project;
        Timeline.Markers = p?.Markers ?? [];
        Timeline.Chords = p?.ChordMarkers ?? [];
        var loop = p == null ? null : ProjectMarkers.Loop(p.Settings);
        Timeline.LoopRange = loop;
        engine.SetLoopRange(loop);
    }

    void MarkerEdit(Action<Project> change, string key = "")
    {
        if (!CanEditMarkers) return;
        undo.Checkpoint(store!.Project, key);
        try { change(store.Project); }
        catch (ArgumentException e) { Toast(e.Message); return; }
        MarkDirty();
        UpdateUndoButtons();
        RefreshMarkers();
    }

    /// <summary>A marker at <paramref name="sec"/>; from the mouse its name opens for writing right away, from a key it doesn't steal the keyboard.</summary>
    void AddMarker(double sec, bool rename)
    {
        if (!CanEditMarkers) return;
        Marker? added = null;
        MarkerEdit(p => added = ProjectMarkers.Add(p, sec));
        if (added == null) return;
        Status(Loc.T("marker.added", added.Name, TransportTime(added.Sec)));
        if (rename) Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Timeline.MarkerRectOf(added.Id) is { } r) BeginNameEdit(added.Id, r);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Sets the A–B range and turns the loop on (a range is drawn to be heard).</summary>
    void SetLoopRange(double a, double b)
    {
        if (store == null || restoring) return;
        try { ProjectMarkers.SetLoop(store.Project.Settings, a, b); }
        catch (ArgumentException e) { Toast(e.Message); return; }
        MarkDirty();
        RefreshMarkers();
        LoopToggle.IsChecked = true;
    }

    void ClearLoopRange()
    {
        if (store == null || restoring || ProjectMarkers.Loop(store.Project.Settings) == null) return;
        ProjectMarkers.ClearLoop(store.Project.Settings);
        MarkDirty();
        RefreshMarkers();
        Status(Loc.T("loop.cleared"));
    }

    /// <summary>The bar that starts at <paramref name="sec"/>, in seconds, on the tempo map: the B a lone A gets when the song has nothing after it.</summary>
    double BarSec(double sec)
    {
        var map = store?.Project.Settings.TempoMap ?? engine.Tempo;
        double beat = map.BeatAt(Math.Max(0, sec));
        return map.SecAt(beat + map.BarBeats(map.BarAt(beat))) - map.SecAt(beat);
    }

    /// <summary>I: A at the playhead; B stays if it is after it, else the song's end (or a bar later).</summary>
    void SetLoopStartHere()
    {
        if (store == null) return;
        double at = engine.PositionSec;
        double end = ProjectMarkers.Loop(store.Project.Settings) is { } r && r.End - at >= ProjectMarkers.MinLoopSec ? r.End
            : engine.LengthSec - at >= ProjectMarkers.MinLoopSec ? engine.LengthSec : at + BarSec(at);
        SetLoopRange(at, end);
    }

    /// <summary>O: B at the playhead; A stays if it is before it, else the song's start.</summary>
    void SetLoopEndHere()
    {
        if (store == null) return;
        double at = engine.PositionSec;
        double start = ProjectMarkers.Loop(store.Project.Settings) is { } r && at - r.Start >= ProjectMarkers.MinLoopSec ? r.Start : 0;
        SetLoopRange(start, at);
    }

    void JumpToMarker(bool next)
    {
        if (store == null) return;
        double at = engine.PositionSec;
        var m = next ? ProjectMarkers.Next(store.Project, at) : ProjectMarkers.Previous(store.Project, at, engine.Playing && !engine.Stopping);
        if (m != null) engine.Seek(m.Sec);
        else if (!next) engine.Seek(0);
    }

    /// <summary>
    /// Copies the markers as YouTube chapters ("0:00 Intro"…, <see cref="ProjectMarkers.Chapters"/>) to paste in the
    /// video's description; the toast says if YouTube would ignore them and why.
    /// </summary>
    async Task CopyChapters()
    {
        if (store == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var (text, problem) = ProjectMarkers.Chapters(store.Project, engine.LengthSec);
        try
        {
            await clipboard.SetTextAsync(text);
            Toast(problem == null ? Loc.T("marker.chapters.done") : Loc.T("marker.chapters.warn", problem));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Toast(Loc.T("marker.chapters.fail"));
        }
    }

    /// <summary>The chords' strip: writing one where there is room, renaming the one under the pointer, and what is
    /// marked goes, or all of them, one undo step either way.</summary>
    void ShowChordMenu(ChordMarker? chord, double sec, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = CanEditMarkers;
        var marks = Timeline.ChordMarks.ToList();
        var list = new List<object>();
        if (chord != null)
        {
            string id = chord.Id;
            list.Add(new MenuItem { Header = NoteNames.Chord(chord.Name), IsEnabled = false, FontSize = 11 });
            list.Add(MenuAction(Loc.T("marker.go"), () => { engine.Seek(chord.Sec); return Task.CompletedTask; }));
            list.Add(MenuAction(Loc.T("marker.rename"), () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Timeline.ChordRectOf(id) is { } r) BeginNameEdit(id, r); },
                    Avalonia.Threading.DispatcherPriority.Background);
                return Task.CompletedTask;
            }, enabled: enabled, icon: ""));
            list.Add(new Separator());
        }
        else
        {
            var slot = Timeline.ChordSlotAt(sec);
            list.Add(MenuAction(Loc.T("chords.addHere"), () =>
            {
                if (slot is { } s) Avalonia.Threading.Dispatcher.UIThread.Post(() => BeginChordEntry(sec, s.End, s.Box), Avalonia.Threading.DispatcherPriority.Background);
                return Task.CompletedTask;
            }, enabled: enabled && slot != null));
            list.Add(new Separator());
        }
        if (marks.Count > 0)
            list.Add(MenuAction(marks.Count > 1 ? Loc.T("chords.removeMany", marks.Count) : Loc.T("chords.remove"), () =>
            {
                RemoveChords(marks);
                return Task.CompletedTask;
            }, Shortcuts.Label("delete"), enabled, danger: true));
        list.Add(MenuAction(Loc.T("chords.removeAll", Timeline.Chords.Count), () =>
        {
            MarkerEdit(ProjectMarkers.ClearChords);
            return Task.CompletedTask;
        }, enabled: enabled && Timeline.Chords.Count > 0, danger: true));
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }

    void RemoveChords(IReadOnlyCollection<string> ids)
    {
        MarkerEdit(p => ProjectMarkers.RemoveChords(p, ids));
        Timeline.ClearChordMarks();
    }

    /// <summary>The markers' strip's right click: on a marker, the marker's things; elsewhere, adding one.</summary>
    void ShowStripMenu(Marker? marker, double sec, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = CanEditMarkers;
        var list = new List<object>();
        if (marker != null)
        {
            string id = marker.Id;
            list.Add(new MenuItem { Header = marker.Name, IsEnabled = false, FontSize = 11 });
            list.Add(MenuAction(Loc.T("marker.go"), () => { engine.Seek(marker.Sec); return Task.CompletedTask; }, icon: ""));
            list.Add(MenuAction(Loc.T("marker.rename"), () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Timeline.MarkerRectOf(id) is { } r) BeginNameEdit(id, r); },
                    Avalonia.Threading.DispatcherPriority.Background);
                return Task.CompletedTask;
            }, enabled: enabled, icon: ""));
            var names = MarkerNames.Select(n => (object)MenuAction(n, () => { MarkerEdit(p => ProjectMarkers.Rename(p, id, n), $"marker:{id}:name"); return Task.CompletedTask; },
                enabled: enabled && marker.Name != n)).ToList();
            list.Add(new MenuItem { Header = MenuHeader(Loc.T("marker.call"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = names, IsEnabled = enabled });
            var next = ProjectMarkers.Next(store.Project, marker.Sec);
            list.Add(MenuAction(Loc.T("marker.loopToNext"), () => { SetLoopRange(marker.Sec, next?.Sec ?? engine.LengthSec); return Task.CompletedTask; },
                enabled: (next?.Sec ?? engine.LengthSec) - marker.Sec >= ProjectMarkers.MinLoopSec, icon: "", tip: Loc.T("marker.loopToNext.tip")));
            list.Add(new Separator());
            list.Add(MenuAction(Loc.T("marker.remove"), () => { MarkerEdit(p => ProjectMarkers.Remove(p, id)); return Task.CompletedTask; }, enabled: enabled, danger: true));
        }
        else list.Add(MenuAction(Loc.T("marker.addHere"), () => { AddMarker(sec, rename: true); return Task.CompletedTask; }, MenuKey("marker"), enabled, icon: ""));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("marker.chapters"), CopyChapters, enabled: store.Project.Markers.Count > 0, icon: "", tip: Loc.T("marker.chapters.tip")));
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }

    /// <summary>The range's strip's right click: A or B there, or no range.</summary>
    void ShowRangeMenu(double sec, Point point)
    {
        if (store == null || restoring) return;
        var loop = ProjectMarkers.Loop(store.Project.Settings);
        var list = new List<object>
        {
            MenuAction(Loc.T("loop.startHere"), () =>
            {
                double end = loop is { } r && r.End - sec >= ProjectMarkers.MinLoopSec ? r.End : Math.Max(engine.LengthSec, sec + BarSec(sec));
                SetLoopRange(sec, end);
                return Task.CompletedTask;
            }, MenuKey("loopIn"), icon: ""),
            MenuAction(Loc.T("loop.endHere"), () =>
            {
                SetLoopRange(loop is { } r && sec - r.Start >= ProjectMarkers.MinLoopSec ? r.Start : 0, sec);
                return Task.CompletedTask;
            }, MenuKey("loopOut"), sec >= ProjectMarkers.MinLoopSec),
            MenuAction(Loc.T("loop.clear"), () => { ClearLoopRange(); return Task.CompletedTask; }, enabled: loop != null, tip: Loc.T("loop.clear.tip")),
        };
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }

    static string? MenuKey(string id) => Shortcuts.Label(id) is { Length: > 0 } k ? k : null;
}
