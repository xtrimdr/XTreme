using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.App.Video;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>Code-behind studio window: owns editable project data on the UI thread and publishes playback state.</summary>
public partial class MainWindow : Window
{
    /// <summary>Edits are saved this long after the last one (in <see cref="SaveMode.OnEdit"/>; also a failed save's retry).</summary>
    static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1.5);

    readonly PlaybackEngine engine = new();
    readonly AudioGapLog gapLog = new();
    bool resumeAfterScrub;
    readonly LiveCamera camera = new();
    readonly UndoManager undo = new();
    readonly DispatcherTimer timer;
    readonly DispatcherTimer saveTimer;
    ProjectStore? store;
    Task saving = Task.CompletedTask; // writes run in order, one after the other
    Exception? saveFailure;
    bool dirty;
    bool closeConfirmed; // the unsaved-edits question was answered: the next Closing goes through
    List<LiveTrack> tracks = [];
    LiveTrack? sel;
    bool syncing;
    bool restoring;
    double grShown;

    public MainWindow() : this(null) { }

    /// <param name="dir">A project folder to open directly (otherwise it starts on the manager).</param>
    public MainWindow(string? dir)
    {
        InitializeComponent();

        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.Source is Control c && c.FindAncestorOfType<Button>(true) != null) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (WindowState == WindowState.FullScreen)
            {
                if (e.ClickCount == 2) ToggleFullScreen();
            }
            else if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
        InitFullScreen();
        InitWindowPlace();
        InitLive();

        SessionChip.Click += async (_, _) => await ShowProjects();
        ExportButton.Click += async (_, _) =>
        {
            if (store == null || restoring || Projects.IsVisible || RecordingBusy) return;
            // the export opens its own copies of the plugins with their saved state: what was just changed in a plugin's
            // window goes in first
            if (PluginStatesDue && !store.ReadOnly) await SavePluginStates();
            if (store == null || !store.Project.Settings.AudioOnly && NeedFfmpeg()) return;
            if (Export.TryOpen(store, tracks, out var error)) { engine.Pause(); engine.Seek(0); }
            else if (error != null) Toast(error);
        };
        Export.ExportCompleted += res =>
        {
            MarkDirty();
            RefreshHistory();
            _ = SaveExportVersion(res.File); // saves the project too
            bool video = res.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            Toast(res.FileName, Loc.T(video ? "exp.done.video" : "exp.done.audio"),
                action: (Loc.T("proj.reveal"), () => RevealInExplorer(null, res.FullPath)));
        };
        Export.Dismissed += () => Timeline.Focus();
        Settings.Attach(engine, camera, () => RecordingBusy);
        Settings.AudioChanged += ShowAudioStatus;
        Settings.Dismissed += () => Timeline.Focus();
        // the floating Configuración (#184) leaves the menus free: the manager and Exportar, which cover the window, close it
        foreach (var cover in new Control[] { Projects, Export })
            cover.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && cover.IsVisible && Settings.IsVisible) Settings.HideDialog(); };
        SettingsButton.Click += (_, _) =>
        {
            if (!MenuFree) return;
            Settings.Show();
        };
        About.Dismissed += () => Timeline.Focus();
        LogoButton.Click += (_, _) =>
        {
            if (Projects.IsVisible || Export.IsVisible || RecordingBusy) return;
            About.Show();
        };
        About.FeedbackRequested += OpenFeedback;
        Feedback.Sent += copied => Toasts.Show(Loc.T(copied ? "feedback.copied" : "feedback.sent"));
        Feedback.Dismissed += () =>
        {
            if (About.IsVisible) About.Focus();
            else if (Projects.IsVisible) Projects.Focus();
            else Timeline.Focus();
        };
        Projects.HelpRequested += OpenHelp;
        HelpPanel.Dismissed += () => { if (Projects.IsVisible) Projects.Focus(); else Timeline.Focus(); };
        HelpPanel.SettingsRequested += tab => { if (!RecordingBusy) Settings.Show(tab); };
        HelpPanel.NewsRequested += OpenNews;
        Projects.NewsRequested += OpenNews;
        News.TopicRequested += topic => HelpPanel.Show(topic, canOpenSettings: !Projects.IsVisible);
        News.Dismissed += () =>
        {
            if (HelpPanel.IsVisible) HelpPanel.Focus();
            else if (ComponentsHelp.IsVisible) ComponentsHelp.Focus();
            else if (Projects.IsVisible) Projects.Focus();
            else Timeline.Focus();
        };
        Live.OpenInputs += () =>
        {
            if (Projects.IsVisible || Export.IsVisible || RecordingBusy) return;
            Settings.Show("audio");
        };
        // the web's md / lg / xl widths of the monitor
        SizeChanged += (_, e) => Live.Width = e.NewSize.Width >= 1280 ? 256 : 224;
        InitRecording();
        InitMenuBar();
        InitMidiInput();
        InitVirtualKeys();
        PlayButton.Click += (_, _) => TogglePlay();
        ToStartButton.Click += (_, _) => engine.Seek(0);
        FitButton.Click += (_, _) => Timeline.FitAll();
        UndoButton.Click += async (_, _) => await RestoreEdit(false);
        RedoButton.Click += async (_, _) => await RestoreEdit(true);
        ClickToggle.IsCheckedChanged += (_, _) => engine.ClickOn = ClickToggle.IsChecked == true;

        Timeline.SeekRequested += engine.Seek;
        Timeline.ScrubChanged += Scrub;
        Timeline.SelectionChanged += i =>
        {
            BusSelectionLost();
            ShowTrack(i >= 0 && i < tracks.Count ? tracks[i] : null);
        };
        Timeline.TracksPicked += RefreshInspectorText;
        Preview.ClipSelected += (track, clip) => Timeline.SelectClip(tracks.IndexOf(track), clip.Clip.Id);
        Preview.FramingRequested += (clip, zoom, x, y) =>
        {
            if (store == null || store.ReadOnly || restoring || Projects.IsVisible) return;
            var f = clip.Clip.Framing;
            if ((zoom == null || zoom == f.Zoom) && (x == null || x == f.PanX) && (y == null || y == f.PanY)) return;
            undo.Checkpoint(store.Project, $"clip:{clip.Clip.Id}:framing");
            InspectorEditing.Frame(clip.Clip, zoom, x, y);
            MarkDirty();
            UpdateUndoButtons();
            Preview.InvalidateVisual();
            RefreshInspectorText();
        };
        Timeline.TrackToggleRequested += (t, solo) => EditTrack(t, x =>
        {
            if (solo) x.Channel.Solo = !x.Channel.Solo;
            else x.Channel.Muted = !x.Channel.Muted;
        }, solo ? "solo" : "mute");
        ClearMutesButton.Click += (_, _) => ClearAllToggles(false);
        ClearSolosButton.Click += (_, _) => ClearAllToggles(true);
        InitTimelineEditing();
        InitTransport();
        InitTransportShow();
        InitMarkers();
        InitTools();
        InitTempo();
        InitLocks();
        InitPreview();
        InitMode();
        InitImport();

        InitInspector();
        InitTrackLook();
        InitTransitions();
        InitProjectBar();
        InitSidebarToggles();
        InitMixer();
        InitLayout();
        InitMaxPanel();
        InitPanelWindows();
        InitTopBar();
        InitUpdates();
        InitMeters();
        InitShortcutTips();

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPressCloseMenu, RoutingStrategies.Tunnel, handledEventsToo: true);

        Projects.OpenProject = async d =>
        {
            await OpenProject(d);
            Projects.Hide();
        };
        // closed with nothing open (asked by the user: it shouldn't force creating a project): an empty one
        Projects.Dismissed += async () =>
        {
            if (store == null) await StartUntitled();
            else Timeline.Focus();
        };
        Projects.FlushCurrent = FlushProjectOperation;
        Projects.SaveCopy = SaveProjectCopy;
        Projects.CloseCurrent = async () =>
        {
            await FlushProjectOperation();
            CloseProject(clearUndo: false);
        };
        Projects.ReopenCurrent = d => OpenProject(d, preserveUndo: true);
        Projects.CurrentTemplate = name => ProjectLibrary.TemplateOf(store!.Project, name);
        // the manager covers the title bar: its top strip still drags the window
        Projects.PointerPressed += (_, e) =>
        {
            if (e.Source == Projects && e.GetPosition(this).Y < 44 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => SafeTick());
        saveTimer = new DispatcherTimer { Interval = SaveDelay };
        saveTimer.Tick += (_, _) => SaveSoon();
        SaveMode.Changed += () => ArmSave(restart: false);
        SaveButton.Click += (_, _) => SaveNow();
        ShowTrack(null);

        Opened += async (_, _) =>
        {
            Status(Loc.T("main.audioOpening"));
            await Task.Delay(50);
            string? driver = AppConfig.Get("audio.driver");
            int.TryParse(AppConfig.Get("audio.outputOffset"), out int outputOffset);
            if (double.TryParse(AppConfig.Get("audio.inputGainDb"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gainDb))
                engine.InputGainDb = Math.Clamp(gainDb, -24, 24);
            if (double.TryParse(AppConfig.Get("audio.transportFadeMs"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fadeMs))
                engine.TransportFadeMs = fadeMs;
            engine.Open(driver, 0, Math.Max(0, outputOffset));
            ShowAudioStatus();
            _ = camera.Refresh();
            timer.Start();
            // the project manager, or straight into a project without a place (StartupMode); a folder on the command
            // line opens directly
            if (dir != null && ProjectStore.IsProject(dir)) await LoadProject(dir);
            else if (StartupMode.Mode == StartupMode.Empty) await StartUntitled();
            else await ShowProjects();
            if (NewsGuide.Unseen) OpenNews();
            ShellOpen.Listen(path => _ = OpenFromShell(path));
        };
        Closing += async (_, e) =>
        {
            SessionLog.Write($"cerrar  motivo {e.CloseReason}  desde código {e.IsProgrammatic}"
                + (e.IsProgrammatic ? Environment.NewLine + SessionLog.Caller() : ""));
            if (RecordingBusy)
            {
                // keep the take: stop, put it in the project and save, then close
                e.Cancel = true;
                if (recStopping) return;
                await StopRecording(engine.IsCountingIn);
                Close();
                return;
            }
            if (Export.IsRunning)
            {
                e.Cancel = true;
                Toast(Loc.T("main.exportBusy"));
                return;
            }
            // what was just changed in a plugin's window goes in the project before it closes
            if (PluginStatesDue && store is { ReadOnly: false })
            {
                e.Cancel = true;
                await SavePluginStates();
                Close();
                return;
            }
            // a project without a place: empty, it just goes; with something in it, ask where it goes
            if (!closeConfirmed && store is { ReadOnly: false } un && UntitledProjects.Is(un.Dir))
            {
                if (!UntitledProjects.HasContent(un.Project))
                {
                    untitledToDrop = (un.Dir, false);
                    CloseProject();
                    return;
                }
                e.Cancel = true;
                if (Unsaved.IsVisible) return;
                FlushSave();
                var answer = await Unsaved.Ask(un.Project.Name, untitled: true);
                if (answer == Dialogs.UnsavedDialog.Choice.Cancel || un != store) return;
                if (answer == Dialogs.UnsavedDialog.Choice.Save && !await SaveUntitled()) return;
                if (answer == Dialogs.UnsavedDialog.Choice.Discard) untitledToDrop = (un.Dir, true);
                closeConfirmed = true;
                Close();
                return;
            }
            if (!closeConfirmed && store is { ReadOnly: false } st)
            {
                // after each edit a pending save is just written; in the other modes (or if it can't be written) ask
                if (SaveMode.Mode == SaveMode.OnEdit && saveFailure == null) FlushSave();
                if (dirty || saveFailure != null)
                {
                    e.Cancel = true;
                    if (Unsaved.IsVisible) return;
                    var choice = await Unsaved.Ask(st.Project.Name);
                    if (choice == Dialogs.UnsavedDialog.Choice.Cancel || st != store) return;
                    if (choice == Dialogs.UnsavedDialog.Choice.Discard)
                    {
                        saveTimer.Stop();
                        await saving;
                        dirty = false;
                        saveFailure = null;
                    }
                    else
                    {
                        FlushSave();
                        if (dirty || saveFailure != null)
                        {
                            Toast(Loc.T("unsaved.saveFail"), sticky: true);
                            return;
                        }
                    }
                    closeConfirmed = true;
                    Close();
                    return;
                }
            }
            CloseProject();
        };
        Closed += (_, _) =>
        {
            SessionLog.Write("ventana cerrada");
            EndLive(null);
            if (untitledToDrop is var (dropDir, recycle)) DropUntitled(dropDir, recycle);
            timer.Stop();
            SaveWindowPlace();
            // stacked (solo audio, Vertical layout) the bottom row is closed: the heights remembered before stay
            try { if (!stackedViews && !BottomEmpty && !(MixerHere && masteringView)) AppConfig.Set(MixerHere ? PlaceKey : "timelineHeight", Root.RowDefinitions[3].ActualHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* View preference only. */ }
            MidiSetup.Stop();
            engine.Dispose();
            camera.Dispose();
        };
    }

    /// <summary>A message nobody needs to see now (what an action did that already shows, a progress note): only the
    /// session log keeps it. The bell went (asked by the user: nobody opened it); what the user should see is a
    /// <see cref="Toast"/>: a failure, an action that couldn't be done, a result that shows nowhere else.</summary>
    void Status(string s)
    {
        if (!string.IsNullOrWhiteSpace(s)) SessionLog.Write(s);
    }

    void Toast(string s, string? title = null, bool sticky = false, (string Label, Action Run)? action = null) =>
        Toasts.Show(s, title, sticky, action);

    // --- saving ----------------------------------------------------------------------------------

    void MarkDirty()
    {
        editStamp++;
        if (store == null || store.ReadOnly) return;
        dirty = true;
        ArmSave(restart: true);
        LoudnessSoon();
        LiveSoon();
    }

    /// <summary>
    /// Starts the save timer for <see cref="SaveMode"/>: after each edit it restarts, so a burst saves once; every
    /// few minutes it counts from the first unsaved edit (later ones don't push it back); by hand it never runs.
    /// </summary>
    void ArmSave(bool restart)
    {
        if (!dirty) return;
        switch (SaveMode.Mode)
        {
            case SaveMode.Manual:
                saveTimer.Stop();
                break;
            case SaveMode.Interval:
                var every = TimeSpan.FromMinutes(SaveMode.Minutes);
                if (saveTimer.IsEnabled && saveTimer.Interval == every) break;
                saveTimer.Stop();
                saveTimer.Interval = every;
                saveTimer.Start();
                break;
            default:
                if (!restart && saveTimer.IsEnabled && saveTimer.Interval == SaveDelay) break;
                saveTimer.Stop();
                saveTimer.Interval = SaveDelay;
                saveTimer.Start();
                break;
        }
    }

    /// <summary>Serializes here (the project belongs to the UI thread) and writes in the background.</summary>
    void SaveSoon()
    {
        saveTimer.Stop();
        if (store == null || !dirty) return;
        dirty = false;
        var st = store;
        int logged = st.Project.Log.Count;
        string json = st.Serialize();
        if (st.Project.Log.Count != logged) RefreshHistory(); // the save logged takes that left or came back
        saving = saving.ContinueWith(_ =>
        {
            try
            {
                st.Write(json);
                saveFailure = null;
            }
            catch (Exception e)
            {
                // only the first failure in a row pops up: the retries would bring one every 1.5 s
                bool first = saveFailure == null;
                saveFailure = e;
                Dispatcher.UIThread.Post(() =>
                {
                    if (st != store) return;
                    if (first) Toast(Loc.T("main.saveRetry", e.Message));
                    else Toast(Loc.T("main.saveRetry", e.Message));
                    // it retries soon in every mode: what the user asked to save has to get there
                    dirty = true;
                    saveTimer.Stop();
                    saveTimer.Interval = SaveDelay;
                    saveTimer.Start();
                });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Flushes before a manager operation without blocking the UI; a failed save aborts the operation.</summary>
    async Task FlushProjectOperation()
    {
        if (store == null) return;
        saveTimer.Stop();
        await saving;
        saveTimer.Stop();
        if ((!dirty && saveFailure == null) || store.ReadOnly) return;
        var st = store;
        string json = st.Serialize();
        try
        {
            saving = Task.Run(() => st.Write(json));
            await saving;
            dirty = false;
            saveFailure = null;
        }
        catch (Exception e)
        {
            saving = Task.CompletedTask;
            saveFailure = e;
            MarkDirty();
            throw new IOException(Loc.T("main.saveRetry", e.Message), e);
        }
    }

    /// <summary>Writes whatever is pending and waits for it: before closing or switching projects.</summary>
    void FlushSave()
    {
        if (store == null) return;
        saveTimer.Stop();
        saving.Wait();
        if ((dirty || saveFailure != null) && !store.ReadOnly)
        {
            try
            {
                store.Save();
                dirty = false;
                saveFailure = null;
            }
            catch (Exception e)
            {
                Toast(Loc.T("main.saveFail", e.Message));
            }
        }
    }

    void CloseProject(bool clearUndo = true)
    {
        engine.Stop();
        takePreview.Stop();
        StopSample();
        DropAllInstruments();
        DropAllSamplers();
        DropAllEffects();
        CloseSlotWindows();
        historyConfirmPurge = false;
        historyDeletingExport = null;
        FlushSave();
        foreach (var t in tracks) t.CloseVideo();
        store?.Dispose();
        store = null;
        ResetMaster();
        RefreshMarkers();
        RefreshColorPanel();
        if (clearUndo)
        {
            undo.Clear();
            armedIds = null;
            ResetSteps(Loc.T("steps.origin.open"));
        }
        UpdateUndoButtons();
    }

    // --- projects ---------------------------------------------------------------------------------

    async Task ShowProjects()
    {
        Status("");
        await Projects.Show(store?.Dir);
    }

    async Task LoadProject(string dir)
    {
        try
        {
            await OpenProject(dir);
        }
        catch (Exception e)
        {
            Toast(Loc.T("main.openFail", e.Message));
            await ShowProjects();
        }
    }

    /// <summary>Opens a project in place of the current one (throws, so the manager can show why not).</summary>
    async Task OpenProject(string dir, bool preserveUndo = false)
    {
        // one at a time: two opens that overlap (two .xtp from Explorer) would leave one project's tracks on screen
        // under the other's store. The second waits for the first and then takes its place
        var before = opening;
        var done = new TaskCompletionSource();
        opening = done.Task;
        try
        {
            await before;
            await OpenProjectNow(dir, preserveUndo);
        }
        finally
        {
            done.SetResult();
        }
    }

    /// <summary>The open in course, if any (never fails: it only tells when it ended).</summary>
    Task opening = Task.CompletedTask;

    async Task OpenProjectNow(string dir, bool preserveUndo)
    {
        LiveBeforeOpen();
        await FlushProjectOperation();
        if (PluginStatesDue && store is { ReadOnly: false }) await SavePluginStates();
        Status(Loc.T("main.loading"));
        var next = await Task.Run(() => ProjectStore.Open(dir));
        var left = store is { } prev && UntitledProjects.Is(prev.Dir) && !ProjectLibrary.SamePath(prev.Dir, dir)
            ? (prev.Dir, prev.Project.Name, UntitledProjects.HasContent(prev.Project)) : default((string Dir, string Name, bool Content)?);
        CloseProject(clearUndo: !preserveUndo);
        if (left is var (leftDir, leftName, leftContent)) LeaveUntitled(leftDir, leftName, leftContent);
        store = next;
        if (!UntitledProjects.Is(dir)) RecentProjects.Touch(dir, store.Project.Name);
        await ShowProject(store);
        ResetMaster();
        UpdateUndoButtons();
        // once the screen is built: the load's garbage, collected now and not during the first play
        Dispatcher.UIThread.Post(() => { if (!engine.Playing && !RecordingBusy) AudioWarmUp.Compact(); }, DispatcherPriority.Background);
    }

    /// <summary>Loads every clip's audio (offline if its file is missing) and puts the project on screen.</summary>
    async Task ShowProject(ProjectStore st, Dictionary<string, MediaAudio?>? cachedAudio = null, bool preserveView = false)
    {
        engine.Stop();
        var p = st.Project;
        var failed = new List<string>();
        var list = await Task.Run(() =>
        {
            var audio = cachedAudio ?? new Dictionary<string, MediaAudio?>();
            int total = p.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaId).Distinct().Count();
            MediaAudio? AudioOf(Media m)
            {
                if (audio.TryGetValue(m.Id, out var a)) return a;
                int index = audio.Count + 1;
                Dispatcher.UIThread.Post(() => Projects.ReportProgress(Loc.T("main.loadingChannels", index, total, m.Name)));
                try
                {
                    a = st.Resolve(m.Audio) is { } path && File.Exists(path) ? new MediaAudio(path) : null;
                }
                catch
                {
                    a = null;
                    lock (failed) failed.Add(m.Name);
                }
                return audio[m.Id] = a;
            }
            return p.Tracks.Select(t => new LiveTrack(t, t.Clips.Select(c =>
            {
                var m = p.FindMedia(c.MediaId);
                return new LiveClip(c, m, m == null ? null : AudioOf(m)) { AudioPath = st.Resolve(m?.Audio), VideoPath = st.Resolve(m?.Video) };
            }).ToList(), PlaybackEngine.SampleRate)).ToList();
        });
        var previous = tracks;
        tracks = [];
        await Task.Run(() => { foreach (var old in previous) old.CloseVideo(); });
        tracks = list;
        if (!preserveView) dirty = false;
        foreach (var t in list) t.SetMidi(p.Settings.TempoMap);
        engine.SetTracks(list, p.Settings);
        ResetFreezes(st);
        SyncFreezes();
        SyncInstruments();
        SyncSamplers();
        SyncEffects();
        RefreshTranspose();
        SessionName.Text = p.Name;
        Title = WindowTitle(p.Name);
        Timeline.Tempo = p.Settings.TempoMap;
        Tempo.Set(ShownBpm(engine.PositionSec));
        Timeline.LengthSec = engine.LengthSec;
        ApplyRouting(p, list);
        SyncBusRows();
        Timeline.SetTracks(list, p.Folders, preserveView);
        RefreshPiano();
        RefreshMarkers();
        UpdateResetHeights();
        Preview.SetProject(list, p.Settings);
        RefreshFormat();
        RefreshColorPanel();
        ShowTrack(list.FirstOrDefault());
        ApplyMode();

        var notes = new List<string>();
        if (st.ReadOnlyReason != null) notes.Add(st.ReadOnlyReason);
        notes.AddRange(st.Notices);
        if (failed.Count > 0) notes.Add(Loc.T("main.cantRead", string.Join(", ", failed)));
        // what went wrong opening it is worth seeing; the summary only goes to the log
        if (notes.Count > 0) Toast(string.Join(" · ", notes));
        else Status(Loc.T("main.summary", list.Count, $"{engine.LengthSec / 60:0}:{engine.LengthSec % 60:00}", p.Settings.Aspect));

        if (p.Settings.AudioOnly) return;
        var clips = list.Where(t => t.Track.Video).SelectMany(t => t.Clips).ToList();
        await Task.WhenAll(clips.Select(c => Task.Run(() => c.OpenVideo(st))));
        var noVideo = clips.Where(c => c.Video == null && c.Media?.Video != null).Select(c => c.Name).ToList();
        if (noVideo.Count > 0) Toast(Loc.T("main.noVideo", string.Join(", ", noVideo)));
    }

    // --- transport and inspector -----------------------------------------------------------------

    void TogglePlay()
    {
        if (!engine.IsOpen)
        {
            Toast(engine.Status);
            return;
        }
        if (engine.Playing && !engine.Stopping) engine.StopPlaying();
        else
        {
            StopSample(); // the library's listen gives way to the song
            engine.Play();
        }
    }

    /// <summary>Controls whose tooltip names a shortcut: text and action, refreshed when the user changes a key.</summary>
    readonly List<(Control Control, string Text, string Action)> shortcutTips = [];

    /// <summary>Tooltips with keys come from <see cref="Shortcuts"/>, so a key changed in Configuración shows up everywhere.</summary>
    void InitShortcutTips()
    {
        shortcutTips.AddRange(
        [
            (SaveButton, Loc.T("title.save.tip"), "save"), (UndoButton, Loc.T("title.undo"), "undo"), (RedoButton, Loc.T("title.redo"), "redo"), (TimelineFitButton, Loc.T("timeline.fit.tip"), "fit"), (NewTrackButton, Loc.T("timeline.newTrack"), "newTrack"), (VirtualKeysButton, Loc.T("sc.virtualKeys"), "virtualKeys"),
            (ToStartButton, Loc.T("transport.start.tip"), "toStart"), (LoopToggle, Loc.T("transport.loop.tip"), "loop"), (ReturnToggle, Loc.T("transport.return.tip"), "returnOnStop"), (CountInToggle, Loc.T("transport.countin.tip"), "countIn"),
            (ClickToggle, Loc.T("transport.click.tip"), "click"), (TunerToggle, Loc.T("transport.tuner.tip"), "tuner"), (MaxPanelButton, Loc.T("sc.maximize"), "maximize"), (FitButton, Loc.T("transport.fit.tip"), "fit"),
        ]);
        ApplyShortcutTips();
        Shortcuts.Changed += ApplyShortcutTips;
    }

    void ApplyShortcutTips()
    {
        foreach (var (c, text, action) in shortcutTips) ToolTip.SetTip(c, Shortcuts.Tip(text, action));
        foreach (var c in new Control[] { ZoomIcon, ZoomSlider }) ToolTip.SetTip(c, Shortcuts.WheelTip(Loc.T("timeline.zoom"), "wheelZoom"));
        foreach (var c in new Control[] { mixerZoomIcon, mixerZoomSlider }) ToolTip.SetTip(c, Shortcuts.WheelTip(Loc.T("mixer.zoom"), "wheelZoom"));
        foreach (var c in new Control[] { HeightIcon, TrackHeightSlider }) ToolTip.SetTip(c, Shortcuts.WheelTip(Loc.T("timeline.height"), "wheelHeight"));
        string keys = string.Join(" / ", new[] { Shortcuts.Label("play"), Shortcuts.Label("record") }.Where(k => k.Length > 0));
        ToolTip.SetTip(RecStopButton, keys.Length > 0 ? $"{Loc.T("transport.rec.stop.tip")} ({keys})" : Loc.T("transport.rec.stop.tip"));
        ToolTip.SetTip(PlayButton, Shortcuts.Tip(Loc.T(engine.Playing ? "transport.pause.tip" : "transport.play.head.tip"), "play"));
        recTip = null;
        mixerKey = "";
        RefreshMixer();
    }

    /// <summary>Over the manager too, the first thing someone new sees; not while a dialog or a take has the keys.</summary>
    void OpenHelp()
    {
        if (Export.IsVisible || About.IsVisible || RecordingBusy) return;
        HelpPanel.Show(canOpenSettings: !Projects.IsVisible);
    }

    /// <summary>Like the help: over the manager too, and over the help itself.</summary>
    void OpenNews()
    {
        if (Export.IsVisible || About.IsVisible || RecordingBusy) return;
        News.Show();
    }

    /// <summary>Over the manager and "Acerca de" too; not while a dialog or a take has the keys.</summary>
    void OpenFeedback()
    {
        if (Export.IsVisible || RecordingBusy) return;
        Feedback.Show([$"XTreme {Dialogs.AboutDialog.AppVersion()}", System.Runtime.InteropServices.RuntimeInformation.OSDescription, $"ASIO: {engine.DriverName ?? "-"}"]);
    }

    async void OnKey(object? sender, KeyEventArgs e)
    {
        TellTimelineKeys(e, down: true);
        if (Unsaved.HandleKey(e)) return;
        if (TempoAsk.HandleKey(e)) return;
        if (LiveAsk.HandleKey(e)) return;
        if (MidiAsk.HandleKey(e)) return;
        if (ImportAsk.HandleKey(e)) return;
        if (Feedback.HandleKey(e)) return;
        if (News.HandleKey(e)) return;
        if (Stems.HandleKey(e)) return;
        if (ScaleResult.HandleKey(e)) return;
        if (ComponentsHelp.HandleKey(e)) return;
        if (HelpPanel.IsVisible)
        {
            HelpPanel.HandleKey(e);
            return;
        }
        if (Export.IsVisible)
        {
            Export.HandleKey(e);
            return;
        }
        // Configuración floats over a usable window (#184): it takes the keys only while it has the focus
        if (Settings.HandleKey(e)) return;
        if (About.IsVisible)
        {
            About.HandleKey(e);
            return;
        }
        // shortcuts are off while the manager is open; Esc goes to it
        bool typing = e.Source is Control source && source.FindAncestorOfType<TextBox>(true) != null;
        if (Projects.IsVisible)
        {
            if (!typing && Shortcuts.Find(e) == "help") OpenHelp();
            if (Projects.HandleKey(e)) e.Handled = true;
            return;
        }
        if (typing) return;
        if (e.Source == sampleList && sampleList.HandleKey(e)) { e.Handled = true; return; }
        if (VirtualKeyDown(e)) { e.Handled = true; return; }
        if (restoring) { e.Handled = true; return; }
        if (RecordingBusy && await RecordingKey(e)) return;
        // in its own window, only while that window has the keys
        if ((PianoOut ? PanelActive(FloatPiano) : PianoShown && !ViewsCovered) && pianoRoll.HandleKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            Timeline.CancelGesture();
            Preview.CancelGesture();
            e.Handled = true;
            return;
        }
        var action = Shortcuts.Find(e);
        if (action == null) return;
        // held transport keys don't repeat (App.tsx ignores e.repeat)
        if (action is "play" or "toStart" or "click" or "countIn" or "tap" or "record" && !transportKeys.Add(e.Key)) { e.Handled = true; return; }
        switch (action)
        {
            case "undo" or "redo":
                e.Handled = true;
                await RestoreEdit(action == "redo");
                return;
            case "save":
                SaveNow();
                break;
            case "saveAs":
                e.Handled = true;
                await SaveAsShortcut();
                return;
            case "newTrack":
                e.Handled = true;
                await NewTrackShortcut();
                return;
            case "duplicateTrack":
                e.Handled = true;
                await DuplicateTracksShortcut();
                return;
            case "folderGroup":
                e.Handled = true;
                await GroupInFolderShortcut();
                return;
            case "split":
                e.Handled = true;
                await SplitAtPlayhead();
                return;
            case "joinClips":
                e.Handled = true;
                await JoinMidi();
                return;
            case "duplicate" or "delete" or "deleteClose" or "copy" or "cut" or "paste" or "selectAll" or "deselect" or "alignGrid":
                e.Handled = true;
                await RunEdit(action);
                return;
            case "toolSelect" or "toolDraw" or "toolPaint" or "toolRange" or "toolSplit" or "toolErase" or "toolListen" or "toolZoom" or "toolHand":
                ToolKey(action);
                break;
            case "play":
                TogglePlay();
                break;
            case "cutAudio":
                CutAudio();
                break;
            case "record":
                StartRecording();
                break;
            case "arm" when sel != null:
                ToggleArm(sel);
                break;
            case "chordTool":
                SetChordTool(!pianoRoll.ChordTool);
                break;
            case "quantize":
                // handled before the await, or the key also reaches the focused control
                e.Handled = true;
                await Quantize();
                return;
            case "renameClip":
                e.Handled = true;
                RenameShortcut();
                return;
            case "virtualKeys":
                VirtualKeysButton.IsChecked = !VirtualKeysOn;
                break;
            case "toStart":
                engine.Seek(0);
                break;
            case "loop":
                LoopToggle.IsChecked = LoopToggle.IsChecked != true;
                break;
            case "returnOnStop":
                ReturnToggle.IsChecked = ReturnToggle.IsChecked != true;
                break;
            case "loopIn":
                SetLoopStartHere();
                break;
            case "loopOut":
                SetLoopEndHere();
                break;
            case "marker":
                AddMarker(engine.PositionSec, rename: false);
                break;
            case "prevMarker" or "nextMarker":
                JumpToMarker(action == "nextMarker");
                break;
            case "fit":
                Timeline.FitAll();
                break;
            case "zoom1" or "zoom2" or "zoom3" or "zoom4" or "zoom5":
                Timeline.ZoomPreset(action[^1] - '1');
                break;
            case "mixer":
                ToggleMixer();
                break;
            case "click":
                ClickToggle.IsChecked = ClickToggle.IsChecked != true;
                break;
            case "countIn":
                CountInToggle.IsChecked = CountInToggle.IsChecked != true;
                break;
            case "tap":
                Tempo.Tap();
                break;
            case "tuner":
                TunerToggle.IsChecked = TunerToggle.IsChecked != true;
                break;
            case "mute" when sel != null:
                TogglePicked(false);
                break;
            case "solo" when sel != null:
                TogglePicked(true);
                break;
            // a hidden panel comes back first, as it was; shown, the key folds or opens it; in its own window, brings it up
            case "leftPanel" when InspectorOut:
                ActivatePanel(FloatInspector);
                break;
            case "leftPanel" when inspectorHidden:
                SetInspectorHidden(false);
                break;
            case "leftPanel":
                SetInspectorCollapsed(inspectorExpanded.IsVisible);
                break;
            case "rightPanel" when ProjectBarOut:
                ActivatePanel(FloatProject);
                break;
            case "rightPanel" when projectBarHidden:
                SetProjectBarHidden(false);
                break;
            case "rightPanel":
                SetProjectBarCollapsed(projectBarExpanded.IsVisible);
                break;
            // an open list takes Enter to pick
            case "maximize" when e.Source is Control list && list.FindAncestorOfType<ComboBox>(true) is { IsDropDownOpen: true }:
                return;
            case "maximize":
                ToggleMaxPanel();
                break;
            case "fullScreen":
                ToggleFullScreen();
                break;
            case "help":
                OpenHelp();
                break;
            case "uiLarger":
                Controls.UiFonts.Step(1);
                break;
            case "uiSmaller":
                Controls.UiFonts.Step(-1);
                break;
            case "uiReset":
                Controls.UiFonts.Step(0);
                break;
            default:
                return;
        }
        e.Handled = true;
        Timeline.InvalidateVisual();
    }

    /// <summary>The editing actions, the same from their keys and from the Edición menu.</summary>
    async Task RunEdit(string action)
    {
        if (action is "duplicate" or "delete" or "copy" or "cut" or "paste" or "alignGrid" && BlockShortcut(action)) return;
        switch (action)
        {
            case "delete" when Timeline.PickedTempoBeat is > 0 and var tempoBeat:
                await RemoveTempoPoint(tempoBeat);
                break;
            case "delete" when Timeline.ChordMarks.Count > 0:
                RemoveChords(Timeline.ChordMarks.ToList());
                break;
            // boxed automation blocks go with the boxed clips, whatever row is selected (#281)
            case "delete" when Timeline.Range == null && Timeline.BoxedBlockIds.Count > 0:
                await DeleteTimelineSelection();
                break;
            case "delete":
                await (Timeline.Range != null ? DeleteRangeSelection() : sel == null && SelectedFolder is { } gone ? RemoveFolder(gone) : DeleteTimelineSelection());
                break;
            case "deleteClose":
                if (Timeline.Range != null) await DeleteRangeSelection(close: true);
                break;
            case "duplicate":
                await DuplicateSelection();
                break;
            case "alignGrid":
                await AlignClips(PickedClipIds());
                break;
            case "selectAll" when store != null:
                Timeline.BoxClips([.. store.Project.Tracks.SelectMany(t => t.Clips.Select(c => c.Id).Concat((t.MidiClips ?? []).Select(c => c.Id))), .. AllBlockIds(store.Project)]);
                break;
            case "deselect":
                Timeline.BoxClips([]);
                Timeline.SelectClip(Timeline.Selected, null);
                break;
            case "copy" or "cut":
                await CopySelection(cut: action == "cut");
                break;
            case "paste":
                await PasteClips();
                break;
        }
        Timeline.InvalidateVisual();
    }

    string? lastTickError;

    /// <summary>
    /// <see cref="Tick"/> behind a catch: a timer whose handler throws is never rescheduled, and with it the playhead,
    /// the meters and the picture stop for the rest of the session. The error goes to the session log (once per kind).
    /// </summary>
    void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex)
        {
            string key = $"{ex.GetType().Name} {ex.TargetSite}";
            if (key == lastTickError) return;
            lastTickError = key;
            SessionLog.Write($"error en el tick: {ex}");
        }
    }

    void Tick()
    {
        // what is shown goes with what is heard; the lanes on the native effects, with the tracks (FollowLanes)
        double pos = engine.HeardSec;
        bool videoFade = Preview.Playhead != pos && AnyFade();
        Preview.Playhead = pos;
        bool timed = engine.Playing || RecordingBusy;
        videoFade |= timed != Preview.Timed;
        Preview.Timed = timed;
        TakeArmedPeaks();
        RefreshClearButtons();
        RefreshSplitAllButton();
        foreach (var t in tracks)
        {
            t.ShowL = Math.Max(Math.Max(t.MeterL, t.InputL), t.ShowL * 0.82f);
            t.ShowR = Math.Max(Math.Max(t.MeterR, t.InputR), t.ShowR * 0.82f);
            t.MeterL = t.MeterR = t.InputL = t.InputR = 0;
            double gr = t.GrDb;
            t.GrDb = 0;
            t.GrPeak = gr;
            t.GrShow = gr < t.GrShow ? gr : t.GrShow + (gr - t.GrShow) * 0.12;
        }
        FollowLanes(engine.PositionSec);
        TickListen();
        Timeline.SetPlayhead(pos, engine.Playing);
        // where ▶ / Space will send the playhead back (#271)
        Timeline.SetReturnMark(engine.ReturnOnStop && engine.Playing && !engine.Stopping && !RecordingBusy ? engine.PlayStartSec : double.NaN);
        pianoRoll.SetPlayhead(pos);
        pianoRoll.ShowLive(PianoKeys());
        bool newFrame = false;
        var look = store?.Project.Settings;
        foreach (var t in tracks)
        {
            // first the transitions, so a crossfade's two clips both ask their decoders for a frame
            newFrame |= t.UpdateTransitions(pos);
            // by index: foreach over the IReadOnlyList boxes an enumerator per track per tick
            var clips = t.Clips;
            for (int i = 0; i < clips.Count; i++)
                newFrame |= clips[i].UpdateVideo(pos, look?.Color, look?.VideoFx);
        }
        if (newFrame || videoFade) Preview.InvalidateVisual();
        RefreshTransport(pos);
        if (RecordingBusy) RefreshRecording();
        var m = engine.TakeMeters();
        TickMixer(pos, m.OutL, m.OutR);
        TickMaster();
        InputMeter.Push(m.InL, m.InR);
        Live.Meter.Push(m.InL, m.InR);
        TickCamera();
        TickAudioError();
        TickAudioDriver();
        TickTopBar();
        TickInspectorSpectrum();
        TickInspectorStrip();
        TickInspectorPointer();
        TickMeters();
        // a VST3 effect added, loaded or moved: the rest of the mix waits for it
        engine.UpdateLatency();
        if (Settings.IsVisible) Settings.Meter.Push(m.InL, m.InR);
        if (engine.IsOpen) ShowDsp(engine.LoadPercent);
        MeasureTrackLoad();
        gapLog.Tick(engine);

        if (sel != null)
        {
            grShown = Math.Min(sel.GrPeak, grShown * 0.85);
            // the text only when its tenth (or the compressor's switch) changes: the tick allocates nothing
            // (twice the tenths, one less when negative: a reduction under 0.05 dB reads "-0.0", not "0.0")
            int gr = sel.Channel.Comp.On ? (int)Math.Round(grShown * 10, MidpointRounding.AwayFromZero) * 2 - (grShown < 0 ? 1 : 0) : int.MinValue;
            if (gr != grTextTenths)
            {
                grTextTenths = gr;
                GrText.Text = gr == int.MinValue ? "—" : $"{grShown:0.0} dB";
            }
            GrBar.Width = GrTrack.Bounds.Width * Math.Clamp(-grShown / 24, 0, 1);
        }
    }

    /// <summary>The compressor reduction <see cref="GrText"/> shows, as Tick encodes it (int.MinValue = "—", int.MaxValue = not shown yet).</summary>
    int grTextTenths = int.MaxValue;

    /// <summary>Whether any clip has a fade (audio or video); for the tick, so loops and no LINQ.</summary>
    bool AnyFade()
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var clips = tracks[i].Clips;
            for (int k = 0; k < clips.Count; k++)
            {
                var c = clips[k].Clip;
                if (c.FadeInSec > 0 || c.FadeOutSec > 0 || c.VideoFadeInSec > 0 || c.VideoFadeOutSec > 0) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Holds playback while the playhead is dragged and plays on from where it is let go, like Studio Pro and
    /// Logic: a seek per pointer move faded the sound out and in ~60 times a second, a buzz. The hold and the
    /// restart are both faded. A plain click (no drag) stays a faded jump. Stopped, the drag just moves the playhead.
    /// </summary>
    void Scrub(bool start)
    {
        if (start)
        {
            if (!engine.Playing || engine.Stopping || RecordingBusy) return;
            resumeAfterScrub = true;
            engine.FadeOutAndPause();
        }
        else if (resumeAfterScrub)
        {
            resumeAfterScrub = false;
            engine.Play();
        }
    }

    void Edit(Action<LiveTrack> change, string key)
    {
        if (syncing || sel == null) return;
        EditTrack(sel, change, key);
    }

    void EditTrack(LiveTrack track, Action<LiveTrack> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, $"track:{track.Track.Id}:{key}");
        change(track);
        track.ApplyChannel();
        SyncEffects();
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
    }

    /// <summary>Cubase's "deactivate all mute / solo states" (native only): one undo step for every track.</summary>
    void ClearAllToggles(bool solo)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        var lit = tracks.Where(t => solo ? t.Channel.Solo : t.Channel.Muted).ToList();
        // the folders are channels too (#134): their M and S go with the tracks'
        var litFolders = store.Project.Folders.Where(f => !f.Separator && (solo ? f.Solo : f.Muted)).ToList();
        if (lit.Count == 0 && litFolders.Count == 0) return;
        undo.Checkpoint(store.Project);
        foreach (var t in lit)
        {
            if (solo) t.Channel.Solo = false; else t.Channel.Muted = false;
            t.ApplyChannel();
        }
        foreach (var f in litFolders)
            if (solo) f.Solo = false; else f.Muted = false;
        if (litFolders.Count > 0) ApplyRouting(store.Project, tracks);
        RefreshMixer();
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
        int count = lit.Count + litFolders.Count;
        string what = count == 1 ? Loc.T("tracks.one") : Loc.T("tracks.many", count);
        Status(Loc.T(solo ? "mute.clearedSolo" : "mute.clearedMute", what));
    }

    /// <summary>The timeline corner's clear-all M / S, lit like the track toggles while any track has one.</summary>
    void RefreshClearButtons()
    {
        bool editable = store != null && !store.ReadOnly && !restoring;
        // every tick: no array of pairs, no LINQ
        bool anyMute = false, anySolo = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            anyMute |= tracks[i].Channel.Muted;
            anySolo |= tracks[i].Channel.Solo;
        }
        var folders = store?.Project.Folders;
        for (int i = 0; folders != null && i < folders.Count; i++)
        {
            anyMute |= folders[i].Muted;
            anySolo |= folders[i].Solo;
        }
        ShowClearButton(ClearMutesButton, false, anyMute, editable);
        ShowClearButton(ClearSolosButton, true, anySolo, editable);
    }

    /// <summary>One of <see cref="RefreshClearButtons"/>' two buttons; touches it only when its state changed.</summary>
    void ShowClearButton(Button b, bool solo, bool lit, bool editable)
    {
        if (b.Classes.Contains("lit") == lit && b.IsEnabled == editable) return;
        b.IsEnabled = editable;
        b.Classes.Set("lit", lit);
        b.Background = lit ? (solo ? Palette.Accent : Palette.Mute) : Brushes.Transparent;
        b.BorderBrush = lit ? Brushes.Transparent : Palette.Line;
        ((TextBlock)b.Content!).Foreground = lit ? (solo ? Palette.AccentFg : Palette.App) : Palette.Dim;
        ToolTip.SetTip(b, lit ? Loc.T(solo ? "mixer.clearSolos" : "mixer.clearMutes") : Loc.T(solo ? "timeline.noSolos" : "timeline.noMutes"));
    }

    /// <summary>
    /// Whether a restored track sounds with what the live one already has loaded (its kind, instrument, sampler,
    /// granular, GM and freeze): if not, undo reloads the tracks instead of copying the settings over (#262).
    /// </summary>
    static bool SameSound(Track live, Track restored) => live.Kind == restored.Kind
        && JsonSerializer.Serialize(new object?[] { live.Instrument, live.Sampler, live.Granular, live.Gm, live.Freeze }, ProjectStore.Json)
            == JsonSerializer.Serialize(new object?[] { restored.Instrument, restored.Sampler, restored.Granular, restored.Gm, restored.Freeze }, ProjectStore.Json);

    void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = !restoring && !RecordingBusy && store?.ReadOnly == false && undo.CanUndo;
        RedoButton.IsEnabled = !restoring && !RecordingBusy && store?.ReadOnly == false && undo.CanRedo;
        ScheduleSteps();
    }

    Task RestoreEdit(bool redo) => RestoreEdit(p => redo ? undo.Redo(p) : undo.Undo(p), Loc.T(redo ? "main.redone" : "main.undone"));

    /// <summary>
    /// Restores editable data with cached audio; recordings and project history never roll back. <paramref name="move"/>
    /// is the undo, redo or jump to a step (Historial → Pasos); <paramref name="done"/> what the status says after.
    /// </summary>
    async Task RestoreEdit(Func<Project, bool> move, string done)
    {
        if (restoring || RecordingBusy || store == null || store.ReadOnly || Projects.IsVisible) return;
        var st = store;
        string? selected = sel?.Track.Id;
        var places = st.Project.Media.ToDictionary(m => m.Id, m => (m.Audio, m.Video));
        if (!move(st.Project)) return;
        // a shared session (#291) may bring media that wasn't there
        ForgetFileFacts(st.Project.Media.Where(m => !places.TryGetValue(m.Id, out var was) || was != (m.Audio, m.Video)).Select(m => m.Id));
        restoring = true;
        MarkDirty();
        saveTimer.Stop();
        Inspector.IsEnabled = Timeline.IsEnabled = TimelineToolbar.IsEnabled = TimelineFooter.IsEnabled = SessionChip.IsEnabled = false;
        UpdateUndoButtons();
        try
        {
            bool sameClips = !tracks.Any(t => t.Clips.Any(c => FilesMoved(st, c))) && tracks.Count == st.Project.Tracks.Count && tracks.Select((t, i) =>
            {
                var restored = st.Project.Tracks[i];
                return t.Track.Id == restored.Id && t.Track.Video == restored.Video
                    && JsonSerializer.Serialize(t.Track.Clips, ProjectStore.Json) == JsonSerializer.Serialize(restored.Clips, ProjectStore.Json)
                    && JsonSerializer.Serialize(t.Track.MidiClips, ProjectStore.Json) == JsonSerializer.Serialize(restored.MidiClips, ProjectStore.Json)
                    && SameSound(t.Track, restored);
            }).All(same => same);
            if (sameClips)
            {
                // Channel edits keep their decoded audio/video and playhead; no files need reopening.
                for (int i = 0; i < tracks.Count; i++)
                {
                    var restored = st.Project.Tracks[i];
                    tracks[i].Track.Name = restored.Name;
                    tracks[i].Track.FolderId = restored.FolderId;
                    tracks[i].Track.Color = restored.Color;
                    tracks[i].Track.Icon = restored.Icon;
                    tracks[i].Track.Input = restored.Input;
                    tracks[i].Track.Transpose = restored.Transpose;
                    tracks[i].Track.FollowTempo = restored.FollowTempo;
                    // the rest of a track's own settings (#262: undoing a lock, the arpeggiator or the MIDI input did nothing)
                    tracks[i].Track.Locked = restored.Locked;
                    tracks[i].Track.MixerSide = restored.MixerSide;
                    tracks[i].Track.HeightScale = restored.HeightScale;
                    tracks[i].Track.SplitWave = restored.SplitWave;
                    tracks[i].Track.MidiInput = restored.MidiInput;
                    tracks[i].Track.MidiChannel = restored.MidiChannel;
                    tracks[i].Track.Arp = restored.Arp;
                    tracks[i].Track.KeyNames = restored.KeyNames;
                    // the lanes before the channel: ApplyChannel hands them to the engine
                    tracks[i].Track.Automation = restored.Automation;
                    tracks[i].Track.AutomationFolded = restored.AutomationFolded;
                    tracks[i].ApplyChannel(restored.Channel);
                    tracks[i].Track.Extra = restored.Extra;
                }
                st.Project.Tracks = tracks.Select(t => t.Track).ToList();
                ApplyRouting(st.Project, tracks);
                Timeline.UpdateTracks(tracks, st.Project.Folders);
                // the buses came back as new objects: their rows show them, or a bus's lane looks unchanged (#288)
                SyncBusRows();
                UpdateResetHeights();
                engine.SetSettings(st.Project.Settings, glideDelay: true);
                if (MasteringShown) BuildMastering();
                Timeline.Tempo = st.Project.Settings.TempoMap;
                // a tempo map undone with nothing musical on the timeline: the MIDI tracks still read it
                foreach (var t in tracks) t.SetMidi(st.Project.Settings.TempoMap);
                SyncSamplers(); // an LFO in time with the song's tempo, which a step may bring back
                ShowMidiListeners();
                RefreshPiano();
                Timeline.InvalidateVisual();
                Preview.SetProject(tracks, st.Project.Settings);
                RefreshFormat();
                RefreshColorPanel();
            }
            else await ApplyTracks(st);
            int index = tracks.FindIndex(t => t.Track.Id == selected);
            Timeline.Select(index >= 0 ? index : tracks.Count - 1);
            Status(done);
        }
        catch (Exception e)
        {
            Toast(Loc.T("main.restoredNoView", e.Message));
        }
        finally
        {
            restoring = false;
            Timeline.IsEnabled = TimelineToolbar.IsEnabled = TimelineFooter.IsEnabled = SessionChip.IsEnabled = true;
            Inspector.IsEnabled = sel != null || SelectedFolder != null || SelectedBus != null;
            // the strip and name box copied the disabled state while restoring (#192)
            RefreshInspectorText();
            RefreshColorPanel();
            ApplyMode();
            RefreshMixer();
            RefreshMarkers();
            RefreshTranspose();
            MarkDirty();
            UpdateUndoButtons();
            RefreshHistory();
        }
    }

}
