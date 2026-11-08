using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>Port of TransportBar.tsx and App.tsx transport actions (the Timeline / Mixer toggle is in
/// MainWindow.Mixer.cs). The click menu auditions through <see cref="PlaybackEngine.PreviewClick"/>. View preferences and
/// click settings belong to the app, while tempo belongs to the project (an undo step, #262).</summary>
public partial class MainWindow
{
    readonly HashSet<Key> transportKeys = [];
    Flyout? clickMenu;
    readonly List<Button> clickChoices = [];
    InspectorSlider? clickVolume;
    // like ClickSoundMenu's volTimer: one audition 150 ms after the volume stops moving
    readonly Avalonia.Threading.DispatcherTimer clickVolumeTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    /// <summary>How long the tempo box stays still before asking: the wheel and typing give no sign that they ended.</summary>
    static readonly TimeSpan TempoAskDelay = TimeSpan.FromMilliseconds(700);
    readonly Avalonia.Threading.DispatcherTimer tempoAskTimer = new() { Interval = TempoAskDelay };
    double tempoAsked;
    /// <summary>The initial tempo <see cref="tempoAsked"/> means (<see cref="InitialFor"/>).</summary>
    double tempoAskedInitial;
    ProjectStore? tempoAskedIn;
    Task clickPrefsSaving = Task.CompletedTask;
    bool? transportPlaying;
    string? clockTip;
    string? recTip;
    static readonly string[] ClickLabels = [Loc.T("click.label.0"), Loc.T("click.label.1"), Loc.T("click.label.2"), Loc.T("click.label.3"), Loc.T("click.label.4"), Loc.T("click.label.5")];
    static readonly string[] ClickHints = [Loc.T("click.hint.0"), Loc.T("click.hint.1"), Loc.T("click.hint.2"), Loc.T("click.hint.3"), Loc.T("click.hint.4"), Loc.T("click.hint.5")];

    /// <summary>The panic (asked by the user): stops and silences what still rings, tails of every slot and held notes,
    /// the virtual keyboard's included. Not while recording.</summary>
    void CutAudio()
    {
        if (engine.Recording) return;
        virtualKeys.AllOff();
        virtualKeys.ReleaseSustain();
        engine.CutAudio();
        Status(Loc.T("transport.cutAudio.done"));
    }

    /// <summary>config.json key of the return-on-stop toggle (#271): a habit of the user, not of a project.</summary>
    const string ReturnOnStopKey = "transport.returnOnStop";

    void InitTransport()
    {
        engine.Loop = LoopToggle.IsChecked == true;
        LoopToggle.IsCheckedChanged += (_, _) => engine.Loop = LoopToggle.IsChecked == true;
        ReturnToggle.IsChecked = engine.ReturnOnStop = AppConfig.Get(ReturnOnStopKey) == "1";
        ReturnToggle.IsCheckedChanged += (_, _) =>
        {
            engine.ReturnOnStop = ReturnToggle.IsChecked == true;
            try { AppConfig.Set(ReturnOnStopKey, engine.ReturnOnStop ? "1" : null); }
            catch (Exception ex) { Status(ex.Message); }
        };
        StopButton.Click += (_, _) => engine.StopTransport();
        CutAudioButton.Click += (_, _) => CutAudio();
        ToolTip.SetTip(CutAudioButton, Shortcuts.Tip(Loc.T("transport.cutAudio.tip"), "cutAudio"));
        ClockButton.Click += (_, _) => SetRulerMode(!Timeline.RulerInBars);
        Tempo.Changed += TempoEdited;
        tempoAskTimer.Tick += (_, _) => AskTempo();
        AddHandler(KeyUpEvent, OnTransportKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Deactivated += (_, _) => transportKeys.Clear();
        if (!Enum.TryParse<ClickSound>(AppConfig.Get("click.sound"), out var sound)) sound = ClickSound.Bip;
        if (!double.TryParse(AppConfig.Get("click.volumeDb"), NumberStyles.Float, CultureInfo.InvariantCulture, out double volume) || !double.IsFinite(volume)) volume = 0;
        engine.SetClickSound(sound, Math.Clamp(volume, -24, 9));
        BuildClickMenu();
        Closed += (_, _) => clickMenu?.Hide();
        RefreshClickMenu();
    }

    /// <summary>Releasing a transport key ends it here: a focused button clicks on Space's key-up (the last M or S
    /// clicked in the mixer), so the key that just played or stopped must not reach it.</summary>
    void OnTransportKeyUp(object? sender, KeyEventArgs e)
    {
        TellTimelineKeys(e, down: false);
        if (transportKeys.Remove(e.Key)) e.Handled = true;
    }

    /// <summary>The timeline only hears the modifiers with the pointer: this tells it the moment Ctrl goes down or up. The key's own event may not count it yet.</summary>
    void TellTimelineKeys(KeyEventArgs e, bool down)
    {
        var now = e.KeyModifiers;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) now = down ? now | KeyModifiers.Control : now & ~KeyModifiers.Control;
        Timeline.SetModifiers(now);
    }

    /// <summary>
    /// A new value in the tempo box. With clips on the timeline it only shows until the box goes still, and then asks
    /// once whether they stretch (asked by the user), so a drag or the wheel don't ask at every step. Recording, only
    /// the grid can move, so there is nothing to ask.
    /// </summary>
    void TempoEdited(double value)
    {
        tempoAskTimer.Stop();
        if (store == null || store.ReadOnly || restoring) return;
        if (RecordingBusy || store.Project.Tracks.All(t => t.Clips.Count == 0))
        {
            SetTempo(InitialFor(value), follow: true);
            return;
        }
        Tempo.Set(value);
        tempoAsked = value;
        tempoAskedInitial = InitialFor(value);
        tempoAskedIn = store;
        tempoAskTimer.Start();
    }

    async void AskTempo()
    {
        if (Tempo.Busy) return; // the timer ticks again
        tempoAskTimer.Stop();
        if (store is not { } st || st != tempoAskedIn) return;
        double before = ShownBpm(engine.PositionSec), value = tempoAsked, initial = tempoAskedInitial;
        if (initial == st.Project.Settings.Bpm) return;
        var choice = await TempoAsk.Ask(Bpm(before), Bpm(value));
        if (store != st) return;
        if (choice == Dialogs.TempoDialog.Choice.Cancel) Tempo.Set(before);
        else SetTempo(initial, follow: choice == Dialogs.TempoDialog.Choice.Stretch);
    }

    /// <summary>
    /// The project's tempo. With <paramref name="follow"/> the whole song changes speed and the playhead stays on its
    /// bar (<see cref="TempoFollow"/>; the clips' sounds are made in the background, <see cref="RefreshTranspose"/>);
    /// without it, or while recording, only the grid moves.
    /// </summary>
    void SetTempo(double value, bool follow)
    {
        if (store == null || store.ReadOnly || restoring || store.Project.Settings.Bpm == value) return;
        var p = store.Project;
        follow &= !RecordingBusy;
        double f = p.Settings.Bpm / value, at = engine.PositionSec;
        // one undo step (#262); the wheel or a drag on the box make one
        undo.Checkpoint(p, "tempo");
        if (follow) TempoFollow.SetBpm(p, value);
        else TempoFollow.SetGrid(p, value);
        engine.Bpm = value;
        engine.Tempo = p.Settings.TempoMap;
        Timeline.Tempo = p.Settings.TempoMap;
        // MIDI notes are in beats: they follow the tempo
        foreach (var t in tracks) t.SetMidi(p.Settings.TempoMap);
        SyncSamplers(); // an LFO in time with the song
        if (follow)
        {
            foreach (var t in tracks)
            {
                foreach (var c in t.Clips) c.RefreshCurve();
                t.ApplyChannel(); // the lanes moved with the clips
            }
            RefreshMarkers();
            RefreshTranspose();
            engine.Seek(at * f);
        }
        engine.RefreshClipTiming();
        Timeline.LengthSec = engine.LengthSec;
        Tempo.Set(ShownBpm(engine.PositionSec));
        RefreshPiano();
        Timeline.InvalidateVisual();
        MarkDirty();
        UpdateUndoButtons();
    }

    void BuildClickMenu()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Loc.T("transport.clickSound"), Classes = { "label" }, Margin = new Thickness(4, 0) });
        var sounds = new StackPanel();
        for (int i = 0; i < ClickLabels.Length; i++)
        {
            var sound = (ClickSound)i;
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = ClickLabels[i], FontSize = 12 });
            text.Children.Add(new TextBlock { Text = ClickHints[i], FontSize = 11, Foreground = Palette.Faint, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var b = new Button { Content = text, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6) };
            ToolTip.SetTip(b, Loc.T("click.pick.tip"));
            b.Click += (_, _) =>
            {
                engine.SetClickSound(sound, engine.ClickVolumeDb);
                RefreshClickMenu(); SaveClickPreferences();
                PreviewClick();
            };
            clickChoices.Add(b); sounds.Children.Add(b);
        }
        body.Children.Add(sounds);
        clickVolume = new InspectorSlider(Loc.T("click.volume"), -24, 9, .5, 0, Db, v =>
        {
            engine.SetClickSound(engine.ClickSound, v);
            RefreshClickMenu(); SaveClickPreferences();
            clickVolumeTimer.Stop();
            clickVolumeTimer.Start();
        });
        clickVolumeTimer.Tick += (_, _) => { clickVolumeTimer.Stop(); PreviewClick(); };
        body.Children.Add(new Border { Child = clickVolume, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 8, 4, 0) });
        var panel = new Border { Width = 240, Padding = new Thickness(8), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = body };
        clickMenu = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedRight };
        ClickSoundButton.Flyout = clickMenu;
    }

    void RefreshClickMenu()
    {
        ToolTip.SetTip(ClickSoundButton, Loc.T("click.sound.now", ClickLabels[(int)engine.ClickSound]));
        for (int i = 0; i < clickChoices.Count; i++)
        {
            clickChoices[i].Classes.Set("lit", (int)engine.ClickSound == i);
            clickChoices[i].Background = (int)engine.ClickSound == i ? Palette.Alpha(Palette.AccentColor, .14) : Avalonia.Media.Brushes.Transparent;
            clickChoices[i].Foreground = (int)engine.ClickSound == i ? Palette.Accent : Palette.Fg;
        }
        clickVolume?.Set(engine.ClickVolumeDb);
    }

    void SaveClickPreferences()
    {
        string sound = engine.ClickSound.ToString(), volume = engine.ClickVolumeDb.ToString(CultureInfo.InvariantCulture);
        clickPrefsSaving = clickPrefsSaving.ContinueWith(_ =>
        {
            try { AppConfig.Set("click.sound", sound); AppConfig.Set("click.volumeDb", volume); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Keep this session's choice. */ }
        }, TaskScheduler.Default);
    }

    void PreviewClick()
    {
        if (engine.IsOpen) engine.PreviewClick();
        else Toast(engine.Status);
    }

    /// <summary>"bar.unit.sixteenth" (from 1) on the tempo map: the unit is the meter's (eighths in 6/8), split in sixteenths.</summary>
    string TransportBars(double sec)
    {
        long n = BarsKey(sec);
        return $"{n / BarsKeyBar + 1}.{n % BarsKeyBar / BarsKeyUnit + 1}.{n % BarsKeyUnit + 1}";
    }

    /// <summary>The tempo map the clock reads: the engine's (the project's, set with it).</summary>
    TempoMap ClockMap => engine.Tempo;

    /// <summary>The tempo the box shows: the song's tempo under the playhead (the initial one with a single tempo).</summary>
    double ShownBpm(double pos)
    {
        var map = store?.Project.Settings.TempoMap ?? ClockMap;
        return map.SingleTempo ? engine.Bpm : Math.Round(map.BpmAtSec(Math.Max(0, pos)), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>A value typed in the tempo box, as the song's initial tempo: with tempo changes, the whole map scales so the tempo under the playhead becomes it.</summary>
    double InitialFor(double shown)
    {
        if (store == null) return shown;
        var map = store.Project.Settings.TempoMap;
        if (map.SingleTempo) return shown;
        double here = map.BpmAtSec(Math.Max(0, engine.PositionSec));
        return Math.Round(Math.Clamp(map.Bpm * shown / here, TempoMap.MinBpm, TempoMap.MaxBpm), 3);
    }
    static string TransportTime(double sec) => $"{(long)(Math.Max(0, sec) / 60):00}:{Math.Max(0, sec) % 60:00.0}";

    /// <summary>How <see cref="BarsKey"/> packs a bar, a unit and a sixteenth.</summary>
    const long BarsKeyBar = 64 * 64, BarsKeyUnit = 64;

    /// <summary>The bar, unit and sixteenth <see cref="TransportBars"/> shows: its text is a function of this number alone. Allocates nothing.</summary>
    long BarsKey(double sec)
    {
        ClockMap.BarBeatAt(Math.Max(0, sec), out int bar, out double unit);
        var (_, den) = ClockMap.MeterAt(bar);
        int units = (int)Math.Floor(unit + 1e-6);
        int sixteenths = Math.Max(1, 16 / den);
        int sub = Math.Clamp((int)Math.Floor((unit - units) * sixteenths + 1e-6), 0, sixteenths - 1);
        return Math.Max(0, bar) * BarsKeyBar + Math.Min(units, (int)BarsKeyUnit - 1) * BarsKeyUnit + sub;
    }

    /// <summary>What <see cref="TransportTime"/> shows as one number: its minutes and its rounded tenth of a second.</summary>
    static long TimeKey(double sec)
    {
        sec = Math.Max(0, sec);
        return (long)(sec / 60) * 1000 + (long)Math.Round(sec % 60 * 10, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The numbers the clock, its total and its tooltip were last built from (<see cref="BarsKey"/> or <see cref="TimeKey"/>):
    /// <see cref="RefreshTransport"/> runs every tick and rebuilds a text only when its number changes. -1 = rebuild.
    /// </summary>
    long clockShown = -1, clockTotalShown = -1, clockTipShown = -1;
    /// <summary>Whether those texts are in bars: switching the ruler rebuilds them all.</summary>
    bool? clockBarsShown;
    /// <summary>
    /// <see cref="RecordSignature"/> when the record button was last set up: its targets are lists and its tooltip a
    /// join, so they are rebuilt only when the signature changes (or <see cref="recTip"/> was cleared).
    /// </summary>
    int recordSignatureShown;

    /// <summary>
    /// A hash of everything <see cref="RecordTargets"/>, <see cref="MidiRecordTargets"/> and the record tooltip read:
    /// the armed ids, the selected track and each track's id, name, clips, kind and lock. Allocates nothing.
    /// </summary>
    int RecordSignature()
    {
        var h = new HashCode();
        h.Add(store);
        if (store == null) return h.ToHashCode();
        var ids = armedIds;
        h.Add(ids);
        h.Add(ids?.Count ?? -1);
        h.Add(sel?.Track.Id);
        var list = store.Project.Tracks;
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            h.Add(t.Id);
            h.Add(t.Name);
            h.Add(t.Clips.Count);
            h.Add(t.IsMidi);
            h.Add(t.Locked);
            h.Add(ids != null && ids.Contains(t.Id));
        }
        return h.ToHashCode();
    }

    // under the input meter, small: the format without the driver ("Focusrite USB ASIO · 48 kHz · …"), which goes to the tooltip
    void ShowAudioStatus()
    {
        var parts = engine.Status.Split(" · ", 2);
        AudioStatus.Text = parts.Length == 2 ? $"· {parts[1]}" : "";
        ToolTip.SetTip(AudioCell, engine.Status);
    }

    bool audioStallShown;

    /// <summary>The interface stopped calling back (unplugged, driver hung): a toast once, until it calls again or is reopened.</summary>
    void TickAudioDriver()
    {
        bool stalled = engine.Stalled;
        if (stalled == audioStallShown) return;
        audioStallShown = stalled;
        if (stalled) Toast(Loc.T("toast.audio.lost", engine.DriverName));
    }

    void RefreshTransport(double pos)
    {
        bool bars = Timeline.RulerInBars;
        if (bars != clockBarsShown)
        {
            clockBarsShown = bars;
            clockShown = clockTotalShown = clockTipShown = -1;
        }
        long now = bars ? BarsKey(pos) : TimeKey(pos);
        if (now != clockShown)
        {
            clockShown = now;
            TimeText.Text = bars ? TransportBars(pos) : TransportTime(pos);
        }
        // while recording the total is the take so far, like the web
        double total = RecordingBusy ? pos : engine.LengthSec;
        long end = bars ? BarsKey(total) : TimeKey(total);
        if (end != clockTotalShown)
        {
            clockTotalShown = end;
            BarText.Text = "/ " + (bars ? TransportBars(total) : TransportTime(total));
        }
        long tipKey = bars ? TimeKey(pos) : BarsKey(pos);
        if (tipKey != clockTipShown)
        {
            clockTipShown = tipKey;
            string tip = bars ? Loc.T("transport.clock.bars.tip", TransportTime(pos))
                : Loc.T("transport.clock.time.tip", TransportBars(pos));
            if (tip != clockTip) { clockTip = tip; ToolTip.SetTip(ClockButton, tip); }
        }
        if (transportPlaying != engine.Playing)
        {
            transportPlaying = engine.Playing;
            PlayIcon.Text = engine.Playing ? "\uF8AE" : "\uF5B0";
            PlayButton.Classes.Set("on", engine.Playing);
            ToolTip.SetTip(PlayButton, Shortcuts.Tip(engine.Playing ? Loc.T("transport.pause.tip") : Loc.T("transport.play.head.tip"), "play"));
        }
        StopButton.IsEnabled = !engine.Recording && (engine.Playing || pos > 0);
        CutAudioButton.IsEnabled = !engine.Recording;
        PlayButton.IsEnabled = ToStartButton.IsEnabled = !engine.Recording;
        // a value waiting for its question (TempoEdited) stays in the box: the engine only gets it with the answer
        Tempo.Set(tempoAskTimer.IsEnabled || TempoAsk.IsVisible ? tempoAsked : ShownBpm(pos));
        Tempo.IsEnabled = store != null && !store.ReadOnly && !restoring && !RecordingBusy;
        ShowArm();
        ShowMidiListeners();
        UpdateVirtualLive();
        int signature = RecordSignature();
        if (signature == recordSignatureShown && recTip != null) return;
        recordSignatureShown = signature;
        var targets = RecordTargets();
        // armed MIDI tracks record the keyboard's notes; a MIDI clip already there stays (the new one goes on top)
        targets.AddRange(MidiRecordTargets());
        // nothing armed, nothing to record into: armed tracks are the only way to say where a take goes
        RecButton.IsEnabled = targets.Count > 0;
        string recordTip = Shortcuts.Tip(targets.Count == 0 ? Loc.T("transport.rec.none")
            : targets.Count > 1 ? Loc.T("transport.rec.many", targets.Count, string.Join(", ", targets.Select(t => Loc.T("common.quoted", t.Name))))
            : targets[0].Clips.Count > 0 ? Loc.T("transport.rec.replace", targets[0].Name) : Loc.T("transport.rec.one", targets[0].Name), "record");
        if (recordTip != recTip) { recTip = recordTip; ToolTip.SetTip(RecButton, recordTip); }
    }
}
