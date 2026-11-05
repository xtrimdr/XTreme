using Avalonia.Controls;
using XtremeDaw.App.Video;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The project's mode (native only, asked by the user): "Video" or "Solo audio", switched from the title bar at
/// any time and saved with the project as one undo step. Solo audio puts the timeline (or the mixer) in the stage's
/// place, lets go of the camera, hides what only makes sense with a picture (thumbnails, Color, Encuadre, camera
/// latency, video fades, "En el video") and turns "Exportar MP4" into "Exportar audio". Nothing is deleted: back in
/// video, everything is where it was.
/// </summary>
public partial class MainWindow
{
    bool audioMode;
    /// <summary>The timeline and the mixer live in <see cref="AudioArea"/>, one over the other (<see cref="MoveBottomView"/>).</summary>
    bool stackedViews;
    /// <summary>Inspector controls that only mean something with a picture; hidden in solo audio.</summary>
    readonly List<Control> videoOnly = [];

    bool AudioOnly => store?.Project.Settings.AudioOnly == true;

    void InitMode()
    {
        ModeVideoButton.Click += (_, _) => ChangeMode("video");
        ModeAudioButton.Click += (_, _) => ChangeMode("audio");
        RefreshModeButtons();
    }

    void ChangeMode(string mode)
    {
        if (store != null && !store.ReadOnly && !restoring && !Projects.IsVisible && !RecordingBusy && store.Project.Settings.Mode != mode)
        {
            undo.Checkpoint(store.Project);
            store.Project.Settings.SetMode(mode);
            MarkDirty();
            UpdateUndoButtons();
            ApplyMode();
            Status(mode == "audio"
                ? Loc.T("mode.status.audio")
                : Loc.T("mode.status.video"));
        }
        // a click on the checked option would uncheck it: always show the project's mode again
        RefreshModeButtons();
    }

    void RefreshModeButtons()
    {
        ModeVideoButton.IsChecked = store != null && !AudioOnly;
        ModeAudioButton.IsChecked = AudioOnly;
        ModeVideoButton.IsEnabled = ModeAudioButton.IsEnabled = store?.ReadOnly == false && !RecordingBusy;
    }

    /// <summary>Puts the window in the project's mode: on open, after undo / redo and from the switch.</summary>
    void ApplyMode()
    {
        RefreshModeButtons();
        bool audio = AudioOnly;
        ExportButtonText.Text = audio ? Loc.T("export.audio") : Loc.T("export.mp4");
        ToolTip.SetTip(ExportButton, audio ? Loc.T("export.audio.tip") : Loc.T("export.mp4"));
        Timeline.ShowVideo = !audio;
        foreach (var c in videoOnly) c.IsVisible = !audio;
        SetColorTabVisible(!audio);
        RefreshInspectorText();
        RefreshHistory();
        _ = camera.SetPaused(audio);
        ApplyLayout();
        if (audio == audioMode) return;
        audioMode = audio;
        if (audio)
        {
            // the decoders are FFmpeg processes: none runs while nothing is shown. Detached here, on the UI thread,
            // because the tick keeps drawing these clips; only the slow closing goes to the background
            var decoders = tracks.SelectMany(t => t.Clips).Select(c => c.DetachVideo()).OfType<VideoDecoder>().ToList();
            _ = Task.Run(() => { foreach (var d in decoders) d.Dispose(); });
        }
        else if (store != null) _ = OpenMissingVideos(store);
    }

    /// <summary>
    /// Stacked (solo audio, the stage in its own window, or the video in its own column in a Vertical layout): the
    /// timeline moves up beside the inspector, the mixer opens under it and the bottom row closes; otherwise both go back
    /// to the bottom with the height they had (the remembered heights stay untouched meanwhile). One in its own window
    /// stays there (<see cref="PutPanel"/> brings it back to wherever this left the other).
    /// </summary>
    void MoveBottomView(bool stacked)
    {
        if (stacked == stackedViews)
        {
            ApplyViewHosts();
            return;
        }
        stackedViews = stacked;
        var row = Root.RowDefinitions[3];
        if (stacked)
        {
            KeepBottomRow();
            // the timeline always stays on screen; the mixer opens under it (so no mini timeline there)
            if (!TimelineOut)
            {
                Root.Children.Remove(TimelineHost);
                Grid.SetRow(TimelineHost, 0);
                AudioArea.Children.Add(TimelineHost);
            }
            if (!MixerOut)
            {
                Root.Children.Remove(MixerHost);
                Grid.SetRow(MixerHost, 2);
                AudioArea.Children.Add(MixerHost);
            }
            row.MinHeight = 0;
            row.Height = new GridLength(0);
        }
        else
        {
            // under the dialogs and toasts: added at the end they'd be drawn over them and keep taking the clicks
            foreach (var host in new Control[] { TimelineHost, MixerHost })
            {
                if (host == TimelineHost ? TimelineOut : MixerOut) continue;
                AudioArea.Children.Remove(host);
                Grid.SetRow(host, 3);
                Root.Children.Insert(Root.Children.IndexOf(Projects), host);
            }
            ApplyBottomRow();
        }
        ApplyViewHosts();
        RefreshMixer();
    }

    /// <summary>
    /// Which of the timeline and the mixer's place is on screen. Stacked shows both; a maximized view, only itself; one
    /// in its own window, always (and the mixer's window its mini timeline).
    /// </summary>
    void ApplyViewHosts()
    {
        bool alone = maxPanel == MaxPanel.Views;
        AudioArea.IsVisible = stackedViews && !ViewsCovered;
        TimelineHost.IsVisible = ToolBox.IsEnabled = TimelineOut || !MixerHere || (stackedViews && !alone);
        // stacked the timeline is always on screen, so its view button would do nothing (asked by the user): only
        // maximized, covered by the stage or in its own window does it bring the timeline back
        TimelineViewButton.IsVisible = !stackedViews || alone || ViewsCovered || TimelineOut;
        miniRow.IsVisible = MixerOut || !stackedViews || alone;
        BottomSplitter.IsVisible = !stackedViews && !BottomEmpty;
        ApplyAudioMixerRows();
        if (mixerView && masteringView) FitMasteringHeight();
    }

    /// <summary>
    /// Stacked: the mixer's row under the timeline (resizable with its splitter) is open only while the mixer is;
    /// maximized, or with the timeline in its own window, it is all there is.
    /// </summary>
    void ApplyAudioMixerRows()
    {
        bool timeline = !TimelineOut && !(maxPanel == MaxPanel.Views && MixerHere), open = stackedViews && timeline && MixerHere;
        AudioSplitter.IsVisible = open;
        AudioArea.RowDefinitions[0].MinHeight = open ? TimelineMinH : 0;
        AudioArea.RowDefinitions[0].Height = timeline ? GridLength.Star : new GridLength(0);
        AudioArea.RowDefinitions[2].MinHeight = open ? MixerMinH : 0;
        AudioArea.RowDefinitions[2].Height = !MixerHere ? new GridLength(0) : timeline ? new GridLength(StackedMixerH) : GridLength.Star;
    }

    /// <summary>
    /// The mixer's row under the timeline: its remembered height, never past what leaves the timeline its minimum (a
    /// height kept from a taller window pushed the timeline out and left the splitter with nowhere to go).
    /// </summary>
    double StackedMixerH
    {
        get
        {
            double h = Math.Max(MixerMinH, mixerH), area = AudioArea.Bounds.Height;
            return area > 0 ? Math.Max(MixerMinH, Math.Min(h, area - TimelineMinH)) : h;
        }
    }

    /// <summary>Opens the decoders of the clips shown in the video that don't have one yet (after an edit, or back from solo audio).</summary>
    async Task OpenMissingVideos(ProjectStore st)
    {
        if (st.Project.Settings.AudioOnly) return;
        var open = tracks.Where(t => t.Track.Video).SelectMany(t => t.Clips).Where(c => !c.VideoTried && c.Media?.Video != null).ToList();
        if (open.Count > 0) await Task.WhenAll(open.Select(c => Task.Run(() => c.OpenVideo(st))));
        Preview.SetProject(tracks, st.Project.Settings);
        Preview.InvalidateVisual();
    }

    /// <summary>"En el video" of the inspector and the timeline menu: whether the track gets a panel (native only).</summary>
    async Task SetInVideo(Model.LiveTrack t, bool on)
    {
        if (store == null || store.ReadOnly || restoring || t.Track.Video == on) return;
        undo.Checkpoint(store.Project);
        t.Track.Video = on;
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
        await OpenMissingVideos(store);
        Status(Loc.T(on ? "status.inVideo.on" : "status.inVideo.off", t.Track.Name));
    }
}
