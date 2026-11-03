using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Threading;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>Timeline commands and popup menus from Timeline.tsx; all model changes remain owned by the window.</summary>
public partial class MainWindow
{
    bool timelineSyncing;
    DispatcherTimer? timelinePrefs;
    ContextMenu? timelineMenu;
    /// <summary>The control a menu opened from (the input chip) and the one whose press just dismissed it, so a second
    /// press on the same chip closes its menu instead of opening it again.</summary>
    Control? menuAnchor, menuDismissedBy;
    /// <summary>The snap to other clips: which edges of a moved clip stick (stored by these ids in config.json).</summary>
    static readonly string[] ClipSnapIds = ["off", "start", "end", "both"];
    static readonly SnapEdges[] ClipSnapValues = [SnapEdges.None, SnapEdges.Start, SnapEdges.End, SnapEdges.Both];
    static readonly string[] ClipSnapLabels = [.. ClipSnapIds.Select(id => Loc.T($"snap.clips.label.{id}"))];
    static readonly string[] ClipSnapHints = [.. ClipSnapIds.Select(id => Loc.T($"snap.clips.hint.{id}"))];
    /// <summary>The grid's spacing options for the ruler's menu (<see cref="TimelineView.GridMode"/>): auto, the snap's, or fixed.</summary>
    static readonly string[] GridIds = ["auto", "snap", "bar", "1/2", "1/4", "1/8", "1/8T", "1/16", "1/32", "1/64"];

    /// <summary>Column widths from which each piece of the corner toolbar fits (their widths add up from the left, the
    /// clear toggles at the right edge) and from which the footer keeps its height slider and its zoom icon.</summary>
    const double ToolbarSnapClipsAt = TimelineView.DefaultHeaderW, ToolbarFitAt = 248, ToolbarClearAt = 220, ToolbarSnapAt = 160, FooterHeightAt = 236, FooterZoomIconAt = 156;

    /// <summary>The bars above and below the track headers are as wide as the column: as it narrows, what doesn't fit
    /// goes, least used first, so nothing overlaps and the ruler mode and the zoom always stay.</summary>
    void FitTimelineBars(double w)
    {
        SnapClipsPanel.IsVisible = w >= ToolbarSnapClipsAt;
        TimelineFitButton.IsVisible = w >= ToolbarFitAt;
        ClearTogglesPanel.IsVisible = w >= ToolbarClearAt;
        SnapPanel.IsVisible = w >= ToolbarSnapAt;
        bool height = w >= FooterHeightAt;
        HeightIcon.IsVisible = TrackHeightSlider.IsVisible = ResetHeightsButton.IsVisible = height;
        // a star column keeps its share with nothing in it
        TimelineFooterGrid.ColumnDefinitions[4].Width = height ? GridLength.Star : new GridLength(0);
        ZoomSlider.Margin = new Thickness(0, 0, height ? 16 : 0, 0);
        ZoomIcon.IsVisible = w >= FooterZoomIconAt;
    }

    void SetRulerMode(bool bars)
    {
        Timeline.RulerInBars = bars;
        ShowRulerMode();
        timelinePrefs?.Stop(); timelinePrefs?.Start();
    }

    /// <summary>The web's two-button ruler switch: the active mode on AccentSoft (Button.ghost.on).</summary>
    void ShowRulerMode()
    {
        bool bars = Timeline.RulerInBars;
        BarsRulerButton.Classes.Set("on", bars);
        TimeRulerButton.Classes.Set("on", !bars);
        miniBarsButton.Classes.Set("on", bars);
        miniTimeButton.Classes.Set("on", !bars);
        miniTimeline.RulerInBars = bars;
    }

    /// <summary>One button of a ruler switch built in code, like BarsRulerButton / TimeRulerButton in the XAML.</summary>
    static Button RulerButton(string glyph, string tipKey)
    {
        var b = new Button
        {
            Classes = { "ghost" }, Width = 26, Height = 22, Padding = new Thickness(0), MinWidth = 0, CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 14 },
        };
        ToolTip.SetTip(b, Loc.T(tipKey));
        return b;
    }

    /// <summary>The snap box reads as a toggle: lit with the accent (magnet, text and outline) while the grid is on,
    /// plain with "Sin imán" when it is off; the tooltip says what it snaps to right now.</summary>
    void ShowSnap()
    {
        string mode = Timeline.SnapMode;
        bool on = mode != "off";
        SnapIcon.Foreground = on ? Palette.Accent : Palette.Faint;
        SnapBox.Foreground = on ? Palette.Accent : Palette.Dim;
        SnapBox.Background = on ? Palette.AccentSoft : Palette.Raised;
        SnapBox.BorderBrush = on ? (Avalonia.Media.IBrush)this.FindResource("Accent40")! : Palette.Line;
        ToolTip.SetTip(SnapPanel, on
            ? Loc.T("snap.on.tip", SnapChoices.Label(mode), SnapChoices.Hint(mode).ToLowerInvariant())
            : Loc.T("snap.off.tip"));
        // the snap to other clips, lit the same way while it is on
        int k = Array.IndexOf(ClipSnapValues, Timeline.SnapToClips);
        bool lit = k > 0;
        SnapClipsIcon.Foreground = lit ? Palette.Accent : Palette.Faint;
        SnapClipsBox.Foreground = lit ? Palette.Accent : Palette.Dim;
        SnapClipsBox.Background = lit ? Palette.AccentSoft : Palette.Raised;
        SnapClipsBox.BorderBrush = lit ? (Avalonia.Media.IBrush)this.FindResource("Accent40")! : Palette.Line;
        ToolTip.SetTip(SnapClipsPanel, lit ? Loc.T("snap.clips.on.tip", ClipSnapHints[k].ToLowerInvariant()) : Loc.T("snap.clips.off.tip"));
    }

    /// <summary>The ruler's right click: how the grid is drawn, the current choice checked, and which networks' length
    /// marks the timeline shows (<see cref="SocialLengths"/>, several at once, each one toggled).</summary>
    void ShowGridMenu(Point point)
    {
        string label(string id) => id switch
        {
            "auto" => Loc.T("grid.auto"), "snap" => Loc.T("grid.snap"), "bar" => Loc.T("snap.label.1"), _ => id,
        };
        string hint(string id) => id switch
        {
            "auto" => Loc.T("grid.auto.hint"), "snap" => Loc.T("grid.snap.hint"), _ => Loc.T("grid.fixed.hint", label(id)),
        };
        void Save() { timelinePrefs?.Stop(); timelinePrefs?.Start(); }
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = Loc.T("grid.title"), IsEnabled = false, FontSize = 11 } };
        foreach (var id in GridIds)
        {
            list.Add(MenuCheck(label(id), () => Timeline.GridMode == id, () => { Timeline.GridMode = id; Save(); }, refresh, tip: hint(id)));
            if (id == "snap") list.Add(new Separator());
        }
        list.Add(new Separator());
        list.Add(new MenuItem { Header = Loc.T("social.title"), IsEnabled = false, FontSize = 11 });
        foreach (var s in SocialLengths.All)
            list.Add(MenuCheck(Loc.T($"social.{s.Id}", SocialLengths.Clock(s.Sec)), () => Timeline.SocialMarks.Contains(s), () =>
            {
                bool on = Timeline.SocialMarks.Contains(s);
                Timeline.SocialMarks = [.. SocialLengths.All.Where(x => x == s ? !on : Timeline.SocialMarks.Contains(x))];
                Save();
            }, refresh, tip: Loc.T(s.Repeats ? "social.hint.repeats" : "social.hint", SocialLengths.Clock(s.Sec))));
        OpenTimelineMenu(Popup(list, 330), point, MenuHeight(list));
    }

    void InitTimelineEditing()
    {
        SnapBox.SetItems([.. SnapChoices.Ids.Select(SnapChoices.Label)], [.. SnapChoices.Ids.Select(SnapChoices.Hint)]);
        SnapBox.BreaksBefore = SnapChoices.Breaks;
        SnapBox.SelectedIndex = SnapChoices.Index(AppConfig.Get("timelineSnap") ?? "1/4");
        pianoRoll.MainSnap = Timeline.SnapMode = SnapChoices.Ids[SnapBox.SelectedIndex];
        SnapClipsBox.SetItems(ClipSnapLabels, ClipSnapHints);
        // "true" was the on/off toggle before the selector: both edges
        string clipSnap = AppConfig.Get("snapToClips") is "true" ? "both" : AppConfig.Get("snapToClips") ?? "off";
        SnapClipsBox.SelectedIndex = Math.Max(0, Array.IndexOf(ClipSnapIds, clipSnap));
        Timeline.SnapToClips = ClipSnapValues[SnapClipsBox.SelectedIndex];
        Timeline.GridMode = AppConfig.Get("timelineGrid") is { } gm && GridIds.Contains(gm) ? gm : "auto";
        Timeline.SocialMarks = SocialLengths.Parse(AppConfig.Get("socialMarks"));
        Timeline.RulerMenuRequested += ShowGridMenu;
        Timeline.RulerInBars = AppConfig.Get("rulerMode") != "time";
        if (double.TryParse(AppConfig.Get("trackHeight"), CultureInfo.InvariantCulture, out double height))
        {
            Timeline.SetTrackHeight(height);
            TrackHeightSlider.Value = Timeline.TrackHeight;
        }
        timelinePrefs = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timelinePrefs.Tick += async (_, _) =>
        {
            timelinePrefs.Stop();
            string mode = Timeline.RulerInBars ? "bars" : "time";
            string grid = Timeline.SnapMode;
            string toClips = ClipSnapIds[Math.Max(0, Array.IndexOf(ClipSnapValues, Timeline.SnapToClips))];
            string gridMode = Timeline.GridMode;
            string social = SocialLengths.Join(Timeline.SocialMarks);
            string h = Timeline.TrackHeight.ToString(CultureInfo.InvariantCulture);
            string hw = Timeline.HeaderW.ToString(CultureInfo.InvariantCulture);
            try
            {
                await Task.Run(() =>
                {
                    AppConfig.Set("rulerMode", mode);
                    AppConfig.Set("timelineSnap", grid);
                    AppConfig.Set("snapToClips", toClips);
                    AppConfig.Set("timelineGrid", gridMode);
                    AppConfig.Set("socialMarks", social);
                    AppConfig.Set("trackHeight", h);
                    AppConfig.Set("headerWidth", hw);
                });
            }
            catch (Exception e) { Status(Loc.T("timeline.viewSaveError", e.Message)); }
        };
        Closed += (_, _) => timelinePrefs.Stop();
        void Prefs() { timelinePrefs.Stop(); timelinePrefs.Start(); }
        BarsRulerButton.Click += (_, _) => SetRulerMode(true);
        TimeRulerButton.Click += (_, _) => SetRulerMode(false);
        ShowRulerMode();
        ShowSnap();
        SnapBox.Selected += i =>
        {
            SnapBox.SelectedIndex = i;
            pianoRoll.MainSnap = Timeline.SnapMode = SnapChoices.Ids[Math.Clamp(i, 0, SnapChoices.Ids.Length - 1)];
            ShowSnap();
            Prefs();
        };
        SnapClipsBox.Selected += i =>
        {
            SnapClipsBox.SelectedIndex = i;
            Timeline.SnapToClips = ClipSnapValues[Math.Clamp(i, 0, ClipSnapValues.Length - 1)];
            ShowSnap();
            Prefs();
        };
        TimelineFitButton.Click += (_, _) => Timeline.FitAll();
        NewTrackButton.Click += (_, _) => ShowNewTrackMenu();
        TimelineFitButton.IsEnabled = !Timeline.FitMode;
        Timeline.FitModeChanged += fit => TimelineFitButton.IsEnabled = !fit;
        TrackHeightSlider.ValueChanged += (_, e) => { if (!timelineSyncing) Timeline.SetTrackHeight(e.NewValue); };
        Timeline.TrackHeightChanged += value =>
        {
            timelineSyncing = true;
            TrackHeightSlider.Value = value;
            timelineSyncing = false;
            Prefs();
        };
        ZoomSlider.ValueChanged += (_, e) => { if (!timelineSyncing) Timeline.SetZoom(e.NewValue); };
        Timeline.ZoomChanged += () =>
        {
            timelineSyncing = true;
            ZoomSlider.Value = Timeline.Zoom;
            timelineSyncing = false;
        };
        Timeline.ChannelRequested += (track, volume, pan) => EditTrack(track, t =>
        {
            if (volume is { } v) t.Channel.Volume = v;
            if (pan is { } p) t.Channel.Pan = p;
        }, volume != null ? "volume" : "pan");
        // the header's knobs are the user's pick (Configuración → Vista)
        Timeline.SetKnobs(TrackKnobs.Visible);
        TrackKnobs.Changed += () => Timeline.SetKnobs(TrackKnobs.Visible);
        // a knob per send the track shows (the buses it has, ProjectBuses.Shown)
        Timeline.SendKnobs = SendKnobsOf;
        Timeline.SendState = (t, busId) =>
        {
            var sends = t.Channel.Sends;
            for (int k = 0; k < sends.Count; k++)
                if (sends[k].BusId == busId) return (sends[k].On, sends[k].Level);
            return (false, 0);
        };
        // a send knob: its label turns it on or off, the knob sets its level (and turning it up turns it on)
        Timeline.EffectRequested += (track, fx, toggle, amount) =>
        {
            if (!fx.StartsWith(TimelineView.SendPrefix, StringComparison.Ordinal)) return;
            string busId = fx[TimelineView.SendPrefix.Length..];
            EditTrack(track, t =>
            {
                var (_, on, _) = ProjectBuses.SendOf(t.Channel, busId);
                ProjectBuses.SetSend(store!.Project, t.Channel, busId, amount, toggle ? !on : amount > 0 ? true : null);
            }, fx + (toggle ? ".on" : ".amount"));
        };
        // a folder's send knobs, the same as a track's (its sends leave from the sum of its tracks)
        Timeline.FolderSendKnobs = FolderSendKnobsOf;
        Timeline.FolderSendState = (f, busId) =>
        {
            foreach (var s in f.Sends)
                if (s.BusId == busId) return (s.On, s.Level);
            return (false, 0);
        };
        Timeline.FolderSendRequested += (f, busId, toggle, amount) =>
        {
            if (busId.Length == 0) return;
            FolderEdit(f, x =>
            {
                var (_, on, _) = ProjectBuses.SendOf(x, busId);
                ProjectBuses.SetSend(store!.Project, x, busId, amount, toggle ? !on : amount > 0 ? true : null);
            }, $"send.{busId}.{(toggle ? "on" : "level")}");
        };
        Timeline.HeaderWidthChanged += w =>
        {
            TimelineToolbar.Width = TimelineFooter.Width = w;
            FitTimelineBars(w);
            Prefs();
        };
        if (double.TryParse(AppConfig.Get("headerWidth"), CultureInfo.InvariantCulture, out double headerW))
            Timeline.SetHeaderWidth(headerW);
        Timeline.ClipEditCommitted += async (clip, target, swap) =>
        {
            var clones = Timeline.TakeClones();
            await EditTimeline(p =>
            {
                MidiEditing.LeaveCopies(p, clones);
                ClipEditing.Replace(p, clip);
                if (target != null) return ClipEditing.MoveToTrack(p, clip.Id, target, swap);
                return ClipEditing.Find(p, clip.Id).Track.Id;
            }, clones.Count > 0 ? "clip-duplicated" : "clip-edited", clip.Id);
        };
        Timeline.ClipsMoved += async (clips, midi) =>
        {
            var ids = Timeline.BoxedClipIds.ToList();
            var clones = Timeline.TakeClones();
            await EditTimeline(p =>
            {
                MidiEditing.LeaveCopies(p, clones);
                foreach (var c in clips) ClipEditing.Replace(p, c);
                foreach (var (id, at) in midi) MidiEditing.Move(p, id, at);
                return clips.Count > 0 ? ClipEditing.Find(p, clips[0].Id).Track.Id : MidiEditing.Find(p, midi[0].Id).Track.Id;
            }, clones.Count > 0 ? "clips-duplicated" : "clips-moved", clips.Count > 0 ? clips[0].Id : null);
            // the moved clips stay selected together
            Timeline.BoxClips(ids);
        };
        Timeline.TrackMoveRequested += async (ids, at, folder) =>
        {
            string keep = sel != null && ids.Contains(sel.Track.Id) ? sel.Track.Id : ids[0];
            await EditTimeline(p =>
            {
                TrackFolders.Move(p, ids, at, folder);
                return keep;
            }, "tracks-reordered");
            // the moved tracks stay picked together
            Timeline.PickTracks(ids);
        };
        InitFolders();
        InitAutomation();
        Timeline.RemoveTrackRequested += AskRemoveTrack;
        Timeline.NewTrackRequested += ShowNewTrackMenu;
        Timeline.ClipContextRequested += ShowTimelineMenu;
    }

    /// <summary>
    /// One checkpoint per committed gesture, applied in place (<see cref="ApplyTracks"/>): it keeps playing and
    /// nothing on screen is rebuilt, like the web, where editing a lane is just a state change. <paramref name="step"/>
    /// names the step in Historial → Pasos when the change alone can't tell what it was (a recording).
    /// </summary>
    async Task EditTimeline(Func<Project, string?> edit, string? activity, string? clipId = null, EditSteps.Label? step = null)
    {
        if (restoring || store == null || store.ReadOnly || Projects.IsVisible) return;
        var st = store;
        restoring = true;
        undo.Checkpoint(st.Project, label: step);
        UpdateUndoButtons();
        try
        {
            string? selected = edit(st.Project);
            if (activity != null) st.Project.AddLog(activity);
            MarkDirty();
            await ApplyTracks(st);
            int index = st.Project.Tracks.FindIndex(t => t.Id == selected);
            // an edit may hand back a folder's id (a new folder, a moved one): it stays selected
            if (index < 0 && selected != null && st.Project.Folders.Any(f => f.Id == selected)) Timeline.SelectFolder(selected);
            else Timeline.SelectClip(index, clipId);
        }
        catch (Exception e)
        {
            Status(Loc.T("timeline.editError", e.Message));
        }
        finally
        {
            restoring = false;
            Inspector.IsEnabled = sel != null || SelectedFolder != null;
            // the mixer refreshed while restoring (disabled): bring it back, or it stays dead until the next refresh
            if (mixerView) RefreshMixer();
            MarkDirty();
            UpdateUndoButtons();
            RefreshHistory();
        }
    }

    /// <summary>
    /// Puts the project's tracks in the engine and on screen reusing what is loaded: tracks and clips are
    /// matched by id, so their audio, video decoder, effect state (compressor, tails) and the playhead carry on.
    /// Only media that isn't loaded yet (a take brought back from Historial) is read, in the background;
    /// everything else happens before the next frame, so a released clip never shows at its old place.
    /// </summary>
    async Task ApplyTracks(ProjectStore st)
    {
        var p = st.Project;
        var liveTracks = tracks.ToDictionary(t => t.Track.Id);
        var liveClips = tracks.SelectMany(t => t.Clips).ToDictionary(c => c.Clip.Id);
        // a file found again, or the undo of that: what is loaded came from somewhere else
        var audio = tracks.SelectMany(t => t.Clips).Where(c => c.Media != null && c.AudioPath == st.Resolve(c.Media.Audio))
            .GroupBy(c => c.Media!.Id).ToDictionary(g => g.Key, g => g.First().Audio);
        foreach (var (id, a) in importedAudio) audio[id] = a;
        importedAudio.Clear();
        var missing = p.Tracks.SelectMany(t => t.Clips).Where(c => !audio.ContainsKey(c.MediaId))
            .Select(c => p.FindMedia(c.MediaId)).OfType<Media>().DistinctBy(m => m.Id).ToList();
        if (missing.Count > 0)
        {
            Status(Loc.T("timeline.loading"));
            var loaded = await Task.Run(() => missing.Select(m =>
            {
                try { return (m.Id, Audio: st.Resolve(m.Audio) is { } path && File.Exists(path) ? new MediaAudio(path) : null); }
                catch { return (m.Id, Audio: (MediaAudio?)null); }
            }).ToList());
            foreach (var (id, a) in loaded) audio[id] = a;
            Status("");
        }

        var used = new HashSet<LiveClip>();
        var list = new List<LiveTrack>();
        foreach (var t in p.Tracks)
        {
            var clips = new List<LiveClip>();
            foreach (var c in t.Clips)
            {
                var m = p.FindMedia(c.MediaId);
                if (liveClips.TryGetValue(c.Id, out var lc) && lc.Media?.Id == m?.Id && !FilesMoved(st, lc) && used.Add(lc))
                {
                    lc.Clip = c;
                    lc.Media = m;
                    clips.Add(lc);
                }
                else clips.Add(new LiveClip(c, m, m == null ? null : audio.GetValueOrDefault(m.Id)) { AudioPath = st.Resolve(m?.Audio), VideoPath = st.Resolve(m?.Video) });
            }
            if (liveTracks.TryGetValue(t.Id, out var live))
            {
                live.Track = t;
                live.Clips = clips;
                live.ApplyChannel();
            }
            else live = new LiveTrack(t, clips, PlaybackEngine.SampleRate);
            live.SetMidi(p.Settings.Bpm);
            live.SetProjectSettings(p.Settings);
            list.Add(live);
        }
        RefreshPiano();

        var dropped = liveClips.Values.Where(c => !used.Contains(c)).ToList();
        tracks = list;
        engine.UpdateTracks(list);
        SyncInstruments();
        SyncSamplers();
        SyncEffects();
        RefreshTranspose();
        Timeline.LengthSec = engine.LengthSec;
        ApplyRouting(p, list);
        Timeline.UpdateTracks(list, p.Folders);
        UpdateResetHeights();
        Preview.SetProject(list, p.Settings);
        if (dropped.Count > 0) _ = Task.Run(() => { foreach (var c in dropped) c.CloseVideo(); });

        await OpenMissingVideos(st);
    }

    /// <summary>
    /// Supr or the menu on a range (Rango or Shift+drag, native only): cuts that stretch out of every clip of its tracks
    /// (<see cref="ClipEditing.DeleteRange"/>), leaving a gap; with <paramref name="close"/> (Shift+Supr, "Borrar y
    /// cerrar el hueco") what follows on those tracks slides left over it (<see cref="ClipEditing.DeleteRangeClosing"/>).
    /// One undo step either way.
    /// </summary>
    async Task DeleteRangeSelection(bool close = false)
    {
        if (Timeline.Range is not { } range || store == null) return;
        var ids = range.TrackIds.ToList();
        Timeline.ClearRange();
        var lengths = tracks.SelectMany(t => t.Clips).ToDictionary(c => c.Clip.Id, c => c.LengthSec);
        double? Length(Clip c) => lengths.TryGetValue(c.Id, out double l) ? l : null;
        string keep = sel != null && ids.Contains(sel.Track.Id) ? sel.Track.Id : ids[0];
        int changed = 0;
        await EditTimeline(p =>
        {
            changed = close ? ClipEditing.DeleteRangeClosing(p, ids, range.Start, range.End, Length)
                : ids.Sum(id => ClipEditing.DeleteRange(p, id, range.Start, range.End, Length));
            return keep;
        }, close ? "range-closed" : "range-deleted");
        if (close) RefreshMarkers();
        Status(changed == 0 ? Loc.T("range.nothing") : Loc.T(close ? "range.closed" : "range.deleted", range.End - range.Start));
    }

    Task DeleteTimelineSelection()
    {
        if (sel == null || store == null) return Task.CompletedTask;
        string trackId = sel.Track.Id;
        // the clips caught by the selection box (takes and MIDI clips) go all at once (one step to undo)
        if (Timeline.BoxedClipIds.ToList() is { Count: > 1 } picked)
        {
            var boxed = WithoutLocked(picked);
            if (boxed.Count == 0) return Task.CompletedTask;
            return EditTimeline(p =>
            {
                foreach (var id in boxed) MidiEditing.RemoveAny(p, id);
                return trackId;
            }, "clips-removed");
        }
        if (Timeline.PickedTrackIds is { Count: > 1 } many) return RemoveTracks([.. many]);
        if (Timeline.SelectedMidiId is { } midi) return RemoveMidiClip(midi);
        string? clipId = Timeline.SelectedClipId ?? sel.Clips.FirstOrDefault()?.Clip.Id;
        if (clipId != null ? ClipEditing.IsLocked(store.Project, clipId) : ClipEditing.HasLock(sel.Track))
        {
            ToastLocked(sel.Track, clipId);
            return Task.CompletedTask;
        }
        return EditTimeline(p =>
        {
            if (clipId != null) { ClipEditing.RemoveClip(p, clipId); return trackId; }
            ClipEditing.RemoveTrack(p, trackId);
            return null;
        }, clipId != null ? "clip-removed" : "track-removed");
    }

    /// <summary>The clip of the selected track under the playhead: the selected one if the playhead is on it, else the one on top.</summary>
    LiveClip? ClipUnderPlayhead()
    {
        double at = engine.PositionSec;
        bool Inside(LiveClip c) => at > c.ClipStartSec && at < c.ClipEndSec;
        return sel?.Clips.FirstOrDefault(c => c.Clip.Id == Timeline.SelectedClipId && Inside(c)) ?? sel?.Clips.LastOrDefault(Inside);
    }

    /// <summary>Alt+X, like Studio One: splits the clip under the playhead and selects the right piece.</summary>
    async Task SplitAtPlayhead()
    {
        if (sel == null) { Status(Loc.T("timeline.split.pick")); return; }
        if (sel.Track.IsMidi) { await SplitMidiAtPlayhead(); return; }
        if (ClipUnderPlayhead() is not { } clip) { Status(Loc.T("timeline.split.nowhere")); return; }
        if (RefuseLocked(sel, clip)) return;
        string trackId = sel.Track.Id;
        double at = engine.PositionSec, length = clip.LengthSec;
        string? right = null;
        await EditTimeline(p => { right = ClipEditing.Split(p, clip.Clip.Id, at, length); return trackId; }, "clip-split");
        if (right != null) Timeline.SelectClip(Timeline.Selected, right);
    }

    /// <summary>The clips D, Ctrl+C and Ctrl+X act on: the boxed ones, or the selected one.</summary>
    List<string> PickedClipIds() => Timeline.BoxedClipIds is { Count: > 1 } boxed ? [.. boxed]
        : sel != null && Timeline.SelectedClipId is { } id && sel.Clips.Any(c => c.Clip.Id == id) ? [id] : [];

    /// <summary>The length the timeline knows for each clip (the media's own may still be loading).</summary>
    Func<Clip, double?> LiveLengths()
    {
        var lengths = tracks.SelectMany(t => t.Clips).ToDictionary(c => c.Clip.Id, c => c.LengthSec);
        return c => lengths.TryGetValue(c.Id, out double l) ? l : null;
    }

    /// <summary>D, like Studio One: copies the selected clip right after its end and selects the copy. Boxed clips,
    /// even on several tracks, go as one block after the block's end (<see cref="ClipEditing.DuplicateMany"/>).</summary>
    Task DuplicateSelection()
    {
        if (Timeline.BoxedClipIds.Count <= 1 && Timeline.SelectedMidiId is { } midi) return DuplicateMidiClip(midi);
        var ids = PickedClipIds();
        if (ids.Count == 0) { Status(Loc.T("timeline.dup.pick")); return Task.CompletedTask; }
        return DuplicateClips(ids);
    }

    async Task DuplicateClips(List<string> ids)
    {
        if (sel == null) return;
        string trackId = sel.Track.Id;
        int keep = ids.IndexOf(Timeline.SelectedMidiId ?? Timeline.SelectedClipId ?? "");
        var lengths = LiveLengths();
        List<string> copies = [];
        await EditTimeline(p => { copies = MidiEditing.DuplicateMany(p, ids, lengths); return trackId; },
            ids.Count > 1 ? "clips-duplicated" : "clip-duplicated");
        if (copies.Count == 0) return;
        string? kept = keep >= 0 && keep < copies.Count ? copies[keep] : null;
        if (kept != null && store != null && MidiEditing.TryFind(store.Project, kept) != null) Timeline.SelectMidiClip(Timeline.Selected, kept);
        else Timeline.SelectClip(Timeline.Selected, kept);
        Timeline.BoxClips(copies);
    }

    /// <summary>"Alinear a la grilla" (Shift+Q, like FL Studio's quick quantize): each clip's start on the nearest line of
    /// the magnet. MIDI and locked clips stay where they are.</summary>
    async Task AlignClips(List<string> ids)
    {
        double grid = Timeline.SnapSec;
        if (grid <= 0) { Status(Loc.T("menu.alignGrid.off")); return; }
        if (sel == null || ids.Count == 0) return;
        string trackId = sel.Track.Id;
        var lengths = LiveLengths();
        await EditTimeline(p =>
        {
            foreach (var b in ids.Where(b => MidiEditing.TryFind(p, b) == null && !ClipEditing.IsLocked(p, b)))
                ClipEditing.AlignToGrid(p, b, grid, lengths(ClipEditing.Find(p, b).Clip));
            return trackId;
        }, "clip-aligned", Timeline.SelectedClipId);
        Timeline.BoxClips(ids);
    }

    /// <summary>The clips Ctrl+C / Ctrl+X took, for Ctrl+V (in memory only, like any app's clipboard).</summary>
    ClipClipboard? clipboard;

    /// <summary>Ctrl+C, or Ctrl+X with <paramref name="cut"/> (which removes them in one undo step).</summary>
    async Task CopySelection(bool cut)
    {
        if (store == null) return;
        // takes and MIDI clips alike; a MIDI clip selected alone is not among the picked takes
        var ids = PickedClipIds();
        if (ids.Count == 0 && Timeline.SelectedMidiId is { } midi) ids = [midi];
        if (ids.Count == 0 || ClipClipboard.Take(store.Project, ids, LiveLengths()) is not { } taken)
        {
            Status(Loc.T("clipboard.pick"));
            return;
        }
        if (cut)
        {
            if (store.ReadOnly) return;
            string? trackId = sel?.Track.Id;
            var gone = WithoutLocked(ids);
            await EditTimeline(p =>
            {
                foreach (var id in gone) MidiEditing.RemoveAny(p, id);
                return trackId;
            }, ids.Count > 1 ? "clips-cut" : "clip-cut");
        }
        clipboard = taken;
        Status(Loc.T((cut ? "clipboard.cut" : "clipboard.copied") + (ids.Count > 1 ? ".many" : ""), ids.Count, Shortcuts.Label("paste")));
    }

    /// <summary>
    /// Ctrl+V: the copied block starts at the playhead, its top track on the selected one (or the one it came from);
    /// it ends up selected and, stopped, the playhead goes to its end, so pressing it again lays the next copy after it.
    /// </summary>
    async Task PasteClips()
    {
        if (store == null) return;
        if (clipboard is not { } board) { Status(Loc.T("clipboard.empty", Shortcuts.Label("copy"))); return; }
        double at = engine.PositionSec;
        string? target = sel?.Track.Id;
        List<string> pasted = [];
        await EditTimeline(p =>
        {
            pasted = board.Paste(p, target, at);
            return MidiEditing.TryFind(p, pasted[0]) != null ? MidiEditing.Find(p, pasted[0]).Track.Id : ClipEditing.Find(p, pasted[0]).Track.Id;
        }, board.Count > 1 ? "clips-pasted" : "clip-pasted");
        if (pasted.Count == 0) return;
        if (MidiEditing.TryFind(store.Project, pasted[0]) != null) Timeline.SelectMidiClip(Timeline.Selected, pasted[0]);
        else Timeline.SelectClip(Timeline.Selected, pasted[0]);
        Timeline.BoxClips(pasted);
        if (!engine.Playing) engine.Seek(at + board.LengthSec);
    }

    /// <summary>A menu entry's explanation waits this long under the pointer (asked by the user: the hints beside the
    /// "nueva pista" entries were noise).</summary>
    static readonly TimeSpan MenuTipDelay = TimeSpan.FromSeconds(1);

    static MenuItem MenuAction(string label, Func<Task> action, string? hint = null, bool enabled = true, bool danger = false, string? icon = null, string? tip = null)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        // a grid, not a stack: a long label trims before the hint instead of running under it
        var text = new Grid { ColumnDefinitions = new ColumnDefinitions(icon != null ? "20,*" : "*") };
        if (icon == "empty") text.Children.Add(new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12, Height = 12, Stroke = Palette.Dim, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 2 }, VerticalAlignment = VerticalAlignment.Center,
        });
        else if (icon != null) text.Children.Add(new TextBlock { Text = icon, Classes = { "icon" }, FontSize = 12 });
        var name = new TextBlock { Text = label, FontSize = 12, Foreground = danger ? Palette.Rec : Palette.Fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        if (icon != null) Grid.SetColumn(name, 1);
        text.Children.Add(name);
        header.Children.Add(text);
        if (hint != null)
        {
            var value = new TextBlock { Text = hint, Classes = { "mono" }, FontSize = 10.5, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(value, 1);
            header.Children.Add(value);
        }
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (tip != null)
        {
            ToolTip.SetTip(item, tip);
            ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
        }
        if (danger) item.Classes.Add("danger");
        item.Click += async (_, _) => await action();
        return item;
    }

    /// <summary>
    /// An entry of a menu of options (the ruler's grid and length marks, a track's input, a folder's fold, a separator's
    /// visibility): a left click picks it and closes the menu like any entry; a right click picks it and leaves the menu
    /// open, so several can be changed in a row (asked by the user). <paramref name="look"/> gives its text and icon
    /// now (a fold says "Desplegar" once folded), and after either click every entry that shares
    /// <paramref name="refresh"/> redraws, so the checks follow without rebuilding the menu.
    /// </summary>
    static MenuItem MenuChoice(Func<(string Label, string Icon)> look, Action apply, List<Action> refresh,
        string? hint = null, string? tip = null, bool enabled = true)
    {
        var (label, icon) = look();
        var item = MenuAction(label, () => { apply(); return Task.CompletedTask; }, hint, enabled, icon: icon, tip: tip);
        // MenuAction puts the icon and the label in the header's first row
        var row = (Grid)((Grid)item.Header!).Children[0];
        var (glyph, text) = ((TextBlock)row.Children[0], (TextBlock)row.Children[1]);
        refresh.Add(() => (text.Text, glyph.Text) = look());
        // the menu only clicks with the left button: the right one is left to us, even when something handled it
        item.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || !item.IsEnabled) return;
            e.Handled = true;
            apply();
            foreach (var r in refresh) r();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        return item;
    }

    /// <summary>A <see cref="MenuChoice(Func{ValueTuple{string, string}}, Action, List{Action}, string?, string?, bool)"/>
    /// checked while <paramref name="isOn"/>, one of a set of options.</summary>
    static MenuItem MenuCheck(string label, Func<bool> isOn, Action apply, List<Action> refresh, string? hint = null, string? tip = null, bool enabled = true) =>
        MenuChoice(() => (label, isOn() ? "" : " "), apply, refresh, hint, tip, enabled);

    ContextMenu Popup(IEnumerable<object> items, double width) => new()
    {
        ItemsSource = items,
        Width = width,
        Background = Palette.Panel,
        Foreground = Palette.Fg,
        BorderBrush = Palette.Line,
        BorderThickness = new Thickness(1),
    };

    void OpenTimelineMenu(ContextMenu menu, Point point, double height)
    {
        // Keep menus inside the app viewport, as Timeline.tsx does even near the bottom edge.
        var origin = Timeline.TranslatePoint(default, this) ?? default;
        // No height cap: the menu is as tall as its entries (the user didn't want a scrolling menu); it only scrolls
        // if the window itself is shorter.
        menu.MaxHeight = Bounds.Height - 16;
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementAnchor = PopupAnchor.TopLeft;
        menu.PlacementGravity = PopupGravity.BottomRight;
        menu.HorizontalOffset = Math.Clamp(point.X, 8 - origin.X, Math.Max(8 - origin.X, Bounds.Width - origin.X - menu.Width - 8));
        menu.VerticalOffset = Math.Clamp(point.Y, 8 - origin.Y, Math.Max(8 - origin.Y, Bounds.Height - origin.Y - Math.Min(menu.MaxHeight, height) - 8));
        timelineMenu?.Close();
        timelineMenu = menu;
        menuAnchor = null;
        menu.Open(Timeline);
    }

    /// <summary>Opens a menu under a control (the mixer's input chip), one menu at a time like the timeline's.</summary>
    void OpenAnchoredMenu(ContextMenu menu, Control anchor)
    {
        timelineMenu?.Close();
        timelineMenu = menu;
        menuAnchor = anchor;
        menu.Open(anchor);
    }

    /// <summary>Tunnel press on the window: a menu opened from code doesn't always light-dismiss (they piled up in the
    /// mixer), so any press in the window closes it. Presses inside the menu land on its own popup root, not here.</summary>
    void OnWindowPressCloseMenu(object? sender, PointerPressedEventArgs e)
    {
        menuDismissedBy = null;
        if (timelineMenu is not { IsOpen: true } menu) return;
        if (menuAnchor != null && e.Source is Visual v && (v == menuAnchor || menuAnchor.IsVisualAncestorOf(v))) menuDismissedBy = menuAnchor;
        menu.Close();
    }

    /// <summary>True (once) when the current press just closed the menu that this anchor had opened.</summary>
    bool PressClosedMenuOf(Control anchor)
    {
        bool closed = menuDismissedBy == anchor;
        menuDismissedBy = null;
        return closed;
    }

    /// <summary>The new track menu at a point of the timeline: the empty space under the rows, by right click.</summary>
    void ShowNewTrackMenu(Point point)
    {
        if (NewTrackItems() is { } list) OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }

    /// <summary>The new track menu above the + button at the foot of the headers; a second press closes it.</summary>
    void ShowNewTrackMenu()
    {
        if (PressClosedMenuOf(NewTrackButton) || NewTrackItems() is not { } list) return;
        var menu = Popup(list, 240);
        menu.Placement = PlacementMode.TopEdgeAlignedLeft;
        OpenAnchoredMenu(menu, NewTrackButton);
    }

    List<object>? NewTrackItems()
    {
        if (store == null || restoring) return null;
        bool enabled = !store.ReadOnly;
        var list = new List<object>();
        string tip = Loc.T("menu.emptyTrack.hint");
        if (AudioOnly)
            list.Add(MenuAction(Loc.T("menu.emptyTrack"), () => EditTimeline(p => ClipEditing.AddEmpty(p, false), "track-added"), enabled: enabled, icon: "empty", tip: tip));
        else
        {
            list.Add(MenuAction(Loc.T("menu.trackVideo"), () => EditTimeline(p => ClipEditing.AddEmpty(p, true), "track-added"), enabled: enabled, icon: "empty", tip: tip));
            list.Add(MenuAction(Loc.T("menu.trackAudio"), () => EditTimeline(p => ClipEditing.AddEmpty(p, false), "track-added"), enabled: enabled, icon: "empty", tip: tip));
        }
        list.Add(MenuAction(Loc.T("menu.trackMidi"), NewMidiTrack, enabled: enabled, icon: "", tip: Loc.T("menu.trackMidi.hint")));
        list.Add(MenuAction(Loc.T("menu.folder"), () => NewFolder(), enabled: enabled, icon: "", tip: Loc.T("menu.folder.hint")));
        var presets = PresetSubmenu(enabled);
        ToolTip.SetTip(presets, Loc.T("preset.menu.hint"));
        ToolTip.SetShowDelay(presets, (int)MenuTipDelay.TotalMilliseconds);
        list.Add(presets);
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("menu.import"), PickAudioToImport, enabled: CanImport(), icon: "", tip: Loc.T("menu.import.hint")));
        return list;
    }

    /// <summary>The trash can of a track's header asks first, right there: one click took the track with its clips away.</summary>
    void AskRemoveTrack(string id, Point at)
    {
        if (store == null || restoring || store.ReadOnly || tracks.FirstOrDefault(t => t.Track.Id == id) is not { } track) return;
        if (TrackGroup(track) is { } group)
        {
            OpenTimelineMenu(Popup(
            [
                new MenuItem { Header = Loc.T("track.remove.askMany", group.Count), IsEnabled = false, FontSize = 11 },
                MenuAction(Loc.T("track.remove.yes"), () => RemoveTracks(group), danger: true, tip: Loc.T("track.remove.yes.tip")),
                MenuAction(Loc.T("track.remove.no"), () => Task.CompletedTask),
            ], 240), at, 110);
            return;
        }
        if (ClipEditing.HasLock(track.Track))
        {
            ToastLocked(track.Track);
            return;
        }
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("track.remove.ask", track.Track.Name), IsEnabled = false, FontSize = 11 },
            MenuAction(Loc.T("track.remove.yes"), () => EditTimeline(p => { ClipEditing.RemoveTrack(p, id); return null; }, "track-removed"),
                danger: true, tip: Loc.T("track.remove.yes.tip")),
            MenuAction(Loc.T("track.remove.no"), () => Task.CompletedTask),
        };
        OpenTimelineMenu(Popup(list, 240), at, 110);
    }

    /// <summary>The tracks picked together with this one, in order, or null when it is on its own: what acts on a track then acts on them all.</summary>
    List<string>? TrackGroup(LiveTrack track) =>
        Timeline.PickedTrackIds is { Count: > 1 } ids && ids.Contains(track.Track.Id) ? [.. ids] : null;

    /// <summary>Removes the picked tracks at once (one step to undo). The ones with something locked stay, as a single track would.</summary>
    Task RemoveTracks(List<string> ids)
    {
        var gone = ids.Where(id => tracks.FirstOrDefault(t => t.Track.Id == id) is { } t && !ClipEditing.HasLock(t.Track)).ToList();
        if (gone.Count < ids.Count) Status(Loc.T("lock.removeMany.tip"));
        if (gone.Count == 0) return Task.CompletedTask;
        return EditTimeline(p =>
        {
            foreach (var id in gone) ClipEditing.RemoveTrack(p, id);
            return null;
        }, "track-removed");
    }

    /// <summary>The menu of a track picked with others: only what makes sense for all of them, each entry acting on all.</summary>
    List<object> TrackGroupItems(LiveTrack track, List<string> group)
    {
        bool enabled = store is { ReadOnly: false };
        var picked = tracks.Where(t => group.Contains(t.Track.Id)).ToList();
        var audio = picked.Where(t => !t.Track.IsMidi).ToList();
        var list = new List<object>();
        async Task EditGroup(Action<Project> edit, string type)
        {
            await EditTimeline(p => { edit(p); return track.Track.Id; }, type);
            Timeline.PickTracks(group);
        }
        void Each(Project p, IEnumerable<LiveTrack> those, Action<Track> set)
        {
            foreach (var t in those)
                if (p.Tracks.Find(x => x.Id == t.Track.Id) is { } found) set(found);
        }
        if (!AudioOnly && audio.Count > 0)
        {
            bool shown = audio.All(t => t.Track.Video);
            list.Add(MenuAction(Loc.T(shown ? "menu.hideVideo.many" : "menu.showVideo.many", audio.Count), async () =>
            {
                await EditGroup(p => Each(p, audio, t => t.Video = !shown), "track-video");
                await OpenMissingVideos(store!);
            }, enabled: enabled, icon: ""));
        }
        if (audio.Count > 0)
        {
            bool locked = audio.All(t => t.Track.Locked);
            list.Add(MenuAction(Loc.T(locked ? "lock.tracks.off" : "lock.tracks.on", audio.Count), () => EditGroup(p => Each(p, audio, t => t.Locked = !locked), locked ? "track-unlocked" : "track-locked"),
                enabled: enabled, icon: locked ? "" : "", tip: Loc.T("lock.track.tip")));
            list.Add(new Separator());
        }
        list.Add(FolderSubmenu(track, enabled));
        list.Add(new Separator());
        int free = picked.Count(t => !ClipEditing.HasLock(t.Track));
        list.Add(MenuAction(Loc.T("menu.removeTracks", free), () => RemoveTracks(group), "Supr", enabled && free > 0, danger: true,
            tip: free < group.Count ? Loc.T("lock.removeMany.tip") : null));
        return list;
    }

    /// <summary>The "nueva pista" shortcut: audio-only projects add the track at once; in video mode it asks audio+video or audio only.</summary>
    Task NewTrackShortcut()
    {
        if (store == null || restoring || store.ReadOnly) return Task.CompletedTask;
        if (AudioOnly) return EditTimeline(p => ClipEditing.AddEmpty(p, false), "track-added");
        ShowNewTrackMenu(new Point(8, Math.Max(8, Timeline.Bounds.Height - 120)));
        return Task.CompletedTask;
    }

    /// <summary>What a clip's menu offers when the click falls on a range over its track.</summary>
    void AddRangeItems(List<object> list, LiveTrack track, bool enabled)
    {
        if (Timeline.Range is not { } range || !range.TrackIds.Contains(track.Track.Id)) return;
        list.Add(MenuAction(Loc.T("menu.loopRange"), () => { Timeline.ClearRange(); SetLoopRange(range.Start, range.End); return Task.CompletedTask; },
            icon: "", tip: Loc.T("menu.loopRange.tip")));
        list.Add(MenuAction(Loc.T("menu.deleteRange"), () => DeleteRangeSelection(), Shortcuts.Label("delete"), enabled, danger: true));
        list.Add(MenuAction(Loc.T("menu.deleteRangeClose"), () => DeleteRangeSelection(close: true), Shortcuts.Label("deleteClose"), enabled,
            danger: true, tip: Loc.T("menu.deleteRangeClose.tip")));
        list.Add(new Separator());
    }

    void ShowTimelineMenu(LiveTrack track, LiveClip? clicked, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly;
        // A clip under the pointer gets the clip menu; the header or an empty stretch of lane, the track menu.
        var clip = clicked;
        var group = clip == null ? TrackGroup(track) : null;
        var list = new List<object> { new MenuItem { Header = group != null ? Loc.T("menu.tracks.title", group.Count) : track.Track.Name, IsEnabled = false, FontSize = 11 } };
        Task Command(Action<Project> action, string type) => EditTimeline(p => { action(p); return track.Track.Id; }, type, clip?.Clip.Id);
        if (point.X > Timeline.HeaderW) AddRangeItems(list, track, enabled);
        if (clip != null)
        {
            string id = clip.Clip.Id;
            double at = engine.PositionSec;
            bool under = at > clip.ClipStartSec + ClipEnvelope.MinClipSec && at < clip.ClipEndSec - ClipEnvelope.MinClipSec;
            // a locked clip keeps its place and length: what would change them is greyed out
            bool free = !ClipEditing.IsLocked(track.Track, clip.Clip);
            // the boxed clips, when this is one of them, are duplicated, copied, aligned (or removed) all at once
            var boxed = Timeline.BoxedClipIds.Contains(id) ? Timeline.BoxedClipIds.ToList() : [id];
            if (clip.Offline && clip.Media is { } offline)
            {
                list.Add(MenuAction(Loc.T("hist.findFile"), () => FindFile(store, offline), enabled: enabled));
                list.Add(MenuAction(Loc.T("hist.findFolder"), () => FindInFolder(store), enabled: enabled, tip: Loc.T("hist.findFolder.tip")));
                list.Add(new Separator());
            }
            list.Add(MenuAction(Loc.T("menu.cut"), () => CopySelection(cut: true), Shortcuts.Label("cut"), enabled && free));
            list.Add(MenuAction(Loc.T("menu.copy"), () => CopySelection(cut: false), Shortcuts.Label("copy")));
            list.Add(MenuAction(Loc.T("menu.duplicate"), () => DuplicateClips(boxed), Shortcuts.Label("duplicate"), enabled));
            list.Add(MenuAction(Loc.T("menu.split"), async () =>
            {
                string? right = null;
                await EditTimeline(p => { right = ClipEditing.Split(p, id, at, clip.LengthSec); return track.Track.Id; }, "clip-split");
                if (right != null) Timeline.SelectClip(Timeline.Selected, right);
            }, "Alt+X", enabled && under && free));
            list.Add(new Separator());
            list.Add(MenuAction(Loc.T("menu.toStart"), () => Command(p => ClipEditing.ToStart(p, id, clip.LengthSec), "clip-moved"), enabled: enabled && free));
            // where clips overlap the last one shows: this is on only when one over it covers part of it
            bool covered = track.Clips.SkipWhile(o => o != clip).Skip(1).Any(o => o.ClipStartSec < clip.ClipEndSec && o.ClipEndSec > clip.ClipStartSec);
            list.Add(MenuAction(Loc.T("menu.toFront"), () => Command(p => ClipEditing.BringToFront(p, id), "clip-to-front"),
                enabled: enabled && covered, tip: Loc.T("menu.toFront.tip")));
            double grid = Timeline.SnapSec;
            list.Add(MenuAction(Loc.T(boxed.Count > 1 ? "menu.alignGrid.many" : "menu.alignGrid", boxed.Count), () => AlignClips(boxed), Shortcuts.Label("alignGrid"),
                enabled && grid > 0 && (free || boxed.Count > 1), tip: grid > 0 ? Loc.T("menu.alignGrid.tip", SnapChoices.Label(Timeline.SnapMode)) : Loc.T("menu.alignGrid.off")));
            list.Add(ClipLockItem(track, clip, enabled));
            list.Add(new Separator());
            var w = ClipEditing.Window(clip.Clip, clip.Media, clip.LengthSec);
            list.Add(MenuAction(Loc.T("menu.fixCut"), () => Command(p => ClipEditing.FixCut(p, id, clip.LengthSec), "clip-trimmed"),
                w.AnchorCut > 0.0005 ? $"{w.AnchorCut:0.00} s" : null, enabled && free && w.AnchorCut > 0.0005));
            var c = clip.Clip;
            list.Add(MenuAction(Loc.T("menu.resetClip"), () => Command(p => ClipEditing.Reset(p, id), "clip-reset"),
                enabled: enabled && free && (c.TrimStartSec != 0 || c.TrimEndSec != 0 || c.FadeInSec != 0 || c.FadeOutSec != 0 || c.VideoFadeInSec != 0 || c.VideoFadeOutSec != 0)));
            bool reversible = ClipEditing.CanReverse(clip.Media);
            list.Add(MenuCheck(Loc.T("menu.reverse"), () => clip.Clip.Reversed, () => _ = ReverseClips(track, clip, boxed), [],
                tip: Loc.T(reversible ? "menu.reverse.tip" : "menu.reverse.video"), enabled: enabled && free && reversible));
            list.Add(PitchItem(track, clip, boxed, enabled && free));
            list.AddRange(TransitionItems(track, clip, point));
            list.Add(new Separator());
            list.Add(DetectTempoItem(clip, point, enabled));
            list.Add(DetectScaleItem(clip, enabled));
            list.Add(ExtractMidiItem(track, clip, enabled));
            list.Add(StemsItem(track, clip, enabled));
            list.Add(new Separator());
            list.Add(MenuAction(boxed.Count > 1 ? Loc.T("menu.removeClips", boxed.Count) : Loc.T("menu.removeClip"),
                () => boxed.Count > 1 ? DeleteTimelineSelection() : Command(p => ClipEditing.RemoveClip(p, id), "clip-removed"), "Supr", enabled && (free || boxed.Count > 1), danger: true));
            list.Add(new MenuItem { IsEnabled = false, Header = new TextBlock
            {
                Text = Loc.T(track.Clips.Count > 1 ? "menu.clipKept.many" : "menu.clipKept.last"), FontSize = 10.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 212,
            } });
        }
        else list.AddRange(TrackMenuItems(track, Timeline, point, () => RenameInPlace(track.Track.Id, () => takeName.Focus())));
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }

    /// <summary>The clip's "Invertir": the boxed clips it is one of end up going its new way, skipping what can't turn.</summary>
    async Task ReverseClips(LiveTrack track, LiveClip clip, List<string> boxed)
    {
        bool reversed = !clip.Clip.Reversed;
        var turned = tracks.SelectMany(t => t.Clips).Where(c => boxed.Contains(c.Clip.Id) && c.Clip.Reversed != reversed && ClipEditing.CanReverse(c.Media)).ToList();
        await EditTimeline(p =>
        {
            foreach (var c in turned.Where(c => !ClipEditing.IsLocked(p, c.Clip.Id))) ClipEditing.Reverse(p, c.Clip.Id, c.LengthSec);
            return track.Track.Id;
        }, "clip-reversed", clip.Clip.Id);
        Timeline.BoxClips(boxed);
    }

    /// <summary>The clip's "Tono": semitones of its own, on top of the track's Transponer; every value is one entry, so a change is one step and one render.</summary>
    MenuItem PitchItem(LiveTrack track, LiveClip clip, List<string> boxed, bool enabled)
    {
        var refresh = new List<Action>();
        var choices = new List<object>();
        for (int v = (int)PitchShifter.MaxSemitones; v >= -PitchShifter.MaxSemitones; v--)
        {
            double semis = v;
            choices.Add(MenuCheck(TransposeText(semis), () => clip.Clip.Pitch == semis, () => _ = SetClipPitch(track, clip, boxed, semis), refresh));
        }
        string label = Loc.T("menu.pitch");
        if (clip.Clip.Pitch != 0) label += " · " + TransposeTag(clip.Clip.Pitch);
        var item = new MenuItem { Header = MenuHeader(label, FxIcon.Create(FxIcon.Transpose, 12)), ItemsSource = choices, IsEnabled = enabled };
        ToolTip.SetTip(item, Loc.T("menu.pitch.tip"));
        ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
        return item;
    }

    /// <summary>The boxed clips this one is among all get the value, skipping the locked ones.</summary>
    async Task SetClipPitch(LiveTrack track, LiveClip clip, List<string> boxed, double semis)
    {
        var moved = tracks.SelectMany(t => t.Clips).Where(c => boxed.Contains(c.Clip.Id) && c.Clip.Pitch != semis).ToList();
        if (moved.Count == 0) return;
        await EditTimeline(p =>
        {
            foreach (var c in moved.Where(c => !ClipEditing.IsLocked(p, c.Clip.Id))) ClipEditing.SetPitch(p, c.Clip.Id, semis);
            return track.Track.Id;
        }, "clip-pitch", clip.Clip.Id);
        Timeline.BoxClips(boxed);
    }

    /// <summary>A track's own actions, the same from its timeline header and from its mixer strip (asked by the user).
    /// <paramref name="anchor"/> and <paramref name="point"/> place what they open; <paramref name="look"/> adds
    /// its color and icon, for the strip (the timeline's header has its badge for that).</summary>
    List<object> TrackMenuItems(LiveTrack track, Control anchor, Point? point, Action rename, bool look = false)
    {
        if (TrackGroup(track) is { } group) return TrackGroupItems(track, group);
        bool enabled = store is { ReadOnly: false };
        var list = new List<object>();
        list.Add(MenuAction(Loc.T("menu.paste"), PasteClips, Shortcuts.Label("paste"), enabled && clipboard != null, icon: "", tip: Loc.T("menu.paste.tip")));
        if (track.Track.IsMidi) list.Add(InsertPatternItem(track, enabled));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("menu.renameTrack") + "…", () => { rename(); return Task.CompletedTask; }, enabled: enabled, icon: ""));
        if (look) list.Add(MenuAction(Loc.T("look.bus.menu"), () => { ShowTrackLook(track, anchor); return Task.CompletedTask; }, enabled: enabled, icon: "\ue790"));
        if (!AudioOnly && !track.Track.IsMidi)
            list.Add(MenuAction(track.Track.Video ? Loc.T("menu.hideVideo") : Loc.T("menu.showVideo"), () => SetInVideo(track, !track.Track.Video),
                enabled: enabled, icon: ""));
        if (!track.Track.IsMidi) list.Add(TrackLockItem(track, enabled));
        list.Add(new Separator());
        if (track.Track.IsMidi)
            list.Add(new MenuItem { Header = MenuHeader(Loc.T("vst.menu", InstrumentName(track.Track)), new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 }),
                ItemsSource = InstrumentItems(track, enabled) });
        if (track.Track.IsMidi)
        {
            // its own menu, opened where this one was (it stays open to try one input after the other)
            list.Add(MenuAction(Loc.T("midi.in.menu", MidiInputLabel(track.Track)) + "…", () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowMidiInputMenu(track, anchor, point));
                return Task.CompletedTask;
            }, enabled: enabled, icon: ""));
        }
        var sendsIcon = FxIcon.Create(FxIcon.Bus, 12);
        sendsIcon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        list.Add(new MenuItem { Header = MenuHeader(Loc.T("send.menu"), sendsIcon), ItemsSource = SendChoices(RouterOf(track)), IsEnabled = enabled });
        var fxIcon = FxIcon.Create(FxIcon.Eq, 12);
        fxIcon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        list.Add(new MenuItem { Header = MenuHeader(Loc.T("mix.list.fx"), fxIcon), ItemsSource = TrackFxItems(track, anchor), IsEnabled = enabled });
        list.Add(new MenuItem { Header = MenuHeader(Loc.T("auto.menu"), new TextBlock { Classes = { "icon" }, Text = "\uE9D2", FontSize = 12 }), ItemsSource = AutomationItems(track), IsEnabled = enabled });
        list.Add(new Separator());
        list.Add(FolderSubmenu(track, enabled));
        list.Add(MenuAction(Loc.T("separator.addAbove"), () => NewSeparatorAbove(track), enabled: enabled, icon: "", tip: Loc.T("separator.add.tip")));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("preset.save") + "…", () => { SavePreset(track, anchor, point); return Task.CompletedTask; }, enabled: true, icon: "", tip: Loc.T("preset.save.tip")));
        if (!track.Track.IsMidi) list.Add(ApplyPresetSubmenu(track, enabled));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("menu.removeTrack"), () => EditTimeline(p => { ClipEditing.RemoveTrack(p, track.Track.Id); return null; }, "track-removed"),
            "Supr", enabled && !ClipEditing.HasLock(track.Track), danger: true, tip: ClipEditing.HasLock(track.Track) ? Loc.T("lock.remove.tip") : null));
        return list;
    }

    /// <summary>A track's effects in its menu: each opens in the same window as from the mixer (a loaded VST3, its own), then "+ Efecto".</summary>
    List<object> TrackFxItems(LiveTrack track, Control anchor)
    {
        var o = SlotsOf(track);
        var slots = o.Slots();
        var items = new List<object>();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            string id = slot.Id;
            items.Add(MenuAction(SlotLabel(slots, slot, nth), () =>
            {
                // once the menu has closed, so the window stays open
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (o.Slot(id)?.Type == InsertSlot.VstKind && EffectStateOf(o.OwnerId, id) == EffectState.Ready) OpenEffectWindow(o.OwnerId, id);
                    else ShowSlotWindow(o, id, anchor, atPointer: true);
                });
                return Task.CompletedTask;
            }));
        }
        if (items.Count > 0) items.Add(new Separator());
        items.Add(MenuAction(Loc.T("mix.slot.add") + "…", () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowAddSlotMenu(o, anchor));
            return Task.CompletedTask;
        }, icon: ""));
        return items;
    }

    /// <summary>Rough height of a popup menu, to place it inside the window before it opens (it can't be measured
    /// until then). Errs high, so a menu near the bottom edge opens a little higher instead of off the window.</summary>
    static double MenuHeight(IEnumerable<object> items) => 8 + items.Sum(x => x is Separator ? 9 : 34);
}
