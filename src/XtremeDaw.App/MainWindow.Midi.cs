using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// MIDI tracks and the piano roll (native only, stage 1 of the MIDI editor, asked by the user): "+ Nueva pista" adds a
/// MIDI track, its clips are made, moved and resized in the timeline (<see cref="TimelineView"/>) and opened in the
/// piano roll, the fourth view in the mixer's place (<see cref="MixerHost"/>, like Mastering: same height, same spot in
/// solo audio and the Vertical layout). Every edit goes through <see cref="EditTimeline"/>, so it is one undo step and
/// the engine takes the notes again without stopping. Until the sampler (stage 2) a MIDI track plays
/// <see cref="NoteSynth"/>'s test tone.
/// <para>
/// While it is on screen the piano roll follows the timeline (asked by the user, <see cref="FollowPiano"/>): a click on
/// a MIDI clip shows that one, a click elsewhere on another MIDI track shows that track whole, no MIDI track selected
/// leaves it empty, and a click inside another clip in the roll picks it there.
/// </para>
/// </summary>
public partial class MainWindow
{
    readonly PianoRollView pianoRoll = new();
    readonly DockPanel pianoHost = new() { IsVisible = false, Classes = { "notips" } };
    /// <summary>The clip's track, length and notes: trimmed, so a long track or pattern name doesn't widen the bar.</summary>
    readonly TextBlock pianoTitle = new() { FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = PianoTitleMaxW, TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>Room for "Bajo eléctrico · 4 compases · 128 notas"; the rest is in the timeline.</summary>
    const double PianoTitleMaxW = 260;
    bool pianoView;
    /// <summary>The MIDI clip the piano roll shows (it stays when the view closes, so it reopens on it).</summary>
    string? pianoClipId;

    /// <summary>The piano roll is on screen.</summary>
    /// <summary>The piano roll is on screen: in the mixer's place or in its own window.</summary>
    bool PianoShown => PianoOut || (pianoView && mixerView);

    void InitPiano()
    {
        var header = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6),
            MinHeight = 40, Child = BuildPianoHeader(),
        };
        DockPanel.SetDock(header, Dock.Top);
        pianoHost.Children.Add(header);
        DockPanel.SetDock(pianoToolPanel, Dock.Right);
        pianoHost.Children.Add(pianoToolPanel);
        var velocityBar = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 4),
            Child = BuildPianoBottomBar(),
        };
        DockPanel.SetDock(velocityBar, Dock.Bottom);
        pianoHost.Children.Add(velocityBar);
        pianoHost.Children.Add(pianoRoll);
        InitPianoTools();

        pianoRoll.CellLengthChanged += beats =>
        {
            if (pianoClipId is { } id) _ = SetPatternBeats(id, beats);
        };
        pianoRoll.NotesCommitted += async (clipId, notes) =>
        {
            if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
            string trackId = MidiEditing.Find(store.Project, clipId).Track.Id;
            RestoreBeforeCommit(clipId);
            await EditTimeline(p => { MidiEditing.SetNotes(p, clipId, notes); return trackId; }, "midi-notes");
            Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        };
        pianoRoll.NoteAuditioned += (pitch, velocity) => Audition(PianoTrackId(), pitch, velocity);
        pianoRoll.NoteHeld += (pitch, velocity) => AuditionPress(PianoTrackId(), pitch, velocity);
        pianoRoll.NoteReleased += AuditionLift;
        pianoRoll.SeekRequested += engine.Seek;
        pianoRoll.ScrubChanged += Scrub;
        pianoRoll.ClipPicked += clipId =>
        {
            if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
            pianoClipId = clipId;
            RefreshPiano();
            Timeline.SelectMidiClip(TrackIndex(MidiEditing.Find(store.Project, clipId).Track.Id), clipId);
        };
        Timeline.SelectionChanged += FollowPiano;
        pianoRoll.GridMenuRequested += () => ShowPianoGridMenu(pianoRoll);
        if (double.TryParse(AppConfig.Get("pianoVelHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double velH))
            pianoRoll.VelocityHeight = velH;
        pianoRoll.VelocityHeightChanged += h => SaveViewPref("pianoVelHeight", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
        PianoViewButton.Click += (_, _) =>
        {
            if (PianoOut) ShowPiano();
            else if (PianoShown && !ViewsCovered) SetMixerView(false);
            else ShowPiano();
        };

        Timeline.MidiClipChanged += async (clipId, atSec, beats, toTrack) =>
        {
            if (store == null) return;
            string? trackId = null;
            var clones = Timeline.TakeClones();
            var along = Timeline.TakeResizedAlong();
            await EditTimeline(p =>
            {
                MidiEditing.LeaveCopies(p, clones);
                MidiEditing.Place(p, clipId, atSec, beats);
                foreach (string id in along) MidiEditing.Place(p, id, MidiEditing.Find(p, id).Clip.AtSec, beats);
                if (toTrack != null) MidiEditing.MoveToTrack(p, clipId, toTrack);
                return trackId = MidiEditing.Find(p, clipId).Track.Id;
            }, clones.Count > 0 ? "midi-clip-duplicated" : "midi-clip-moved");
            Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        };
        Timeline.MidiClipTrimmed += async (clipId, atSec) =>
        {
            if (store == null) return;
            string? trackId = null;
            await EditTimeline(p =>
            {
                MidiEditing.TrimStart(p, clipId, atSec);
                return trackId = MidiEditing.Find(p, clipId).Track.Id;
            }, "midi-clip-trimmed");
            Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        };
        Timeline.MidiClipLooped += async (clipId, beats) =>
        {
            if (store == null) return;
            string? trackId = null;
            await EditTimeline(p =>
            {
                MidiEditing.Loop(p, clipId, beats);
                return trackId = MidiEditing.Find(p, clipId).Track.Id;
            }, "midi-clip-looped");
            Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        };
        Timeline.MidiClipPassChanged += async (clipId, beats) =>
        {
            if (store == null) return;
            string? trackId = null;
            await EditTimeline(p =>
            {
                MidiEditing.Pass(p, clipId, beats);
                return trackId = MidiEditing.Find(p, clipId).Track.Id;
            }, "midi-clip-looped");
            Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        };
        Timeline.MidiClipOpenRequested += (_, clipId) => OpenInPiano(clipId);
        Timeline.MidiClipCreateRequested += async (track, sec) =>
        {
            if (store == null || store.ReadOnly) return;
            string? made = null;
            await EditTimeline(p => { made = MidiEditing.AddClip(p, track.Track.Id, sec).Id; return track.Track.Id; }, "midi-clip-added");
            if (made == null) return;
            Timeline.SelectMidiClip(TrackIndex(track.Track.Id), made);
            OpenInPiano(made);
        };
        Timeline.MidiClipContextRequested += ShowMidiClipMenu;
        Timeline.ExtractMidiRequested += ExtractMidiDropped;
    }

    Control BuildPianoHeader()
    {
        var row = new ToolbarPanel { VerticalAlignment = VerticalAlignment.Center, AlignBottom = true };
        // as high as a chip, so on the bottom of its row it lines up with the groups' buttons
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Height = 28 };
        title.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 15, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = Loc.T("piano.title"), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line, Margin = new Thickness(4, 0) });
        title.Children.Add(pianoTitle);
        // few chips, each saying what it holds, grouped by theme in menus (asked by the user, 2026-11-04: with every
        // option in sight the bar said nothing); what doesn't fit goes down a row, from the left (ToolbarPanel)
        var clipRow = Chips();
        pianoPartsButton.Click += (_, _) => ShowPianoParts();
        ToolTip.SetTip(pianoPartsButton, Loc.T("piano.parts.tip"));
        clipRow.Children.Add(pianoPartsButton);
        BuildPianoPatternButtons();
        clipRow.Children.Add(pianoMakePatternButton);
        // the track's instrument: which one (a menu to change it) and its window
        pianoInstrumentButton.Click += (_, _) =>
        {
            if (PianoTrack() is not { } t || store == null) return;
            var menu = Popup(InstrumentItems(t, !store.ReadOnly), 280);
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            OpenAnchoredMenu(menu, pianoInstrumentButton);
        };
        ToolTip.SetTip(pianoInstrumentButton, Loc.T("vst.pick.tip"));
        pianoOpenButton.Click += (_, _) => { if (PianoTrack() is { } t) OpenInstrumentWindow(t); };
        ToolTip.SetTip(pianoOpenButton, Loc.T("vst.openWindow.tip"));
        clipRow.Children.Add(pianoInstrumentButton);
        clipRow.Children.Add(pianoOpenButton);

        // the magnet, with the grid in its menu: its own choice, remembered apart from the timeline's (notes want finer
        // steps than takes); the grid is the magnet's steps or its own (the same choices as the timeline's ruler menu)
        pianoRoll.SnapMode = AppConfig.Get("pianoSnap") is { } saved && SnapChoices.PianoIds.Contains(Subdivisions.Migrate(saved)) ? Subdivisions.Migrate(saved) : "1/16";
        pianoRoll.GridMode = AppConfig.Get("pianoGrid") is { } grid && GridIds.Contains(Subdivisions.Migrate(grid)) ? Subdivisions.Migrate(grid) : "snap";
        pianoSnapButton.Click += (_, _) => ShowPianoSnapMenu();
        ShowPianoSnap();
        var viewRow = Chips();
        viewRow.Children.Add(pianoSnapButton);
        viewRow.Children.Add(pianoViewButton);
        // to its own window and back (MainWindow.PanelWindows.cs)
        viewRow.Children.Add(FloatButton(FloatPiano, chip: true));

        row.Children.Add(title);
        row.Children.Add(clipRow);
        row.Children.Add(BuildPianoToolbar());
        row.Children.Add(BuildPianoScaleTools());
        row.Children.Add(viewRow);
        return row;

        static StackPanel Chips() => new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>The velocity lane's step choices (0 = free) in its menu; any other comes from "Otro…".</summary>
    static readonly int[] VelSteps = [0, 4, 8, 10, 16, 32];

    /// <summary>
    /// The bar under the roll: the tools a click uses (draw, paint, slice and chords; asked by the user, 2026-11-04: always
    /// at hand) and the lane's (asked by the user: up in the header they read as unrelated to it): show or hide it (called
    /// Expresión: it shows velocity, mod X/Y, pan or release), which one, pencil or line, and the step of a drag (a chip that
    /// says what it is, with the common values and any other). The lane's tools hide with it. All remembered in config.json.
    /// </summary>
    Control BuildPianoBottomBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var show = new ToggleButton { Classes = { "chip" }, Content = IconText("", Loc.T("piano.vel.show")), Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(show, Loc.T("piano.vel.show.tip"));
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var toolButtons = new List<ToggleButton>();
        foreach (var (tool, key, glyph) in new[] { (PianoRollView.VelTool.Pencil, "pencil", ""), (PianoRollView.VelTool.Line, "line", "") })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = IconText(glyph, Loc.T($"piano.vel.{key}")), FontSize = 12 };
            ToolTip.SetTip(b, Loc.T($"piano.vel.{key}.tip"));
            b.Click += (_, _) =>
            {
                pianoRoll.VelocityTool = tool;
                foreach (var o in toolButtons) o.IsChecked = o == b;
                SaveViewPref("pianoVelTool", key);
            };
            toolButtons.Add(b);
            tools.Children.Add(b);
        }
        var toolSeg = new Border { Classes = { "seg" }, Child = tools, VerticalAlignment = VerticalAlignment.Center };
        var stepChip = new Button { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(stepChip, Loc.T("piano.vel.step.tip"));
        var laneChip = new Button { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(laneChip, Loc.T("piano.lane.tip"));
        void ShowLaneName() => laneChip.Content = IconText("", Loc.T($"tool.c.lane.{pianoRoll.Lane.ToString().ToLowerInvariant()}") + "  ▾");
        laneChip.Click += (_, _) =>
        {
            var refresh = new List<Action>();
            var list = new List<object>();
            foreach (var lane in Enum.GetValues<NoteLane>())
                list.Add(MenuCheck(Loc.T($"tool.c.lane.{lane.ToString().ToLowerInvariant()}"), () => pianoRoll.Lane == lane, () => { pianoRoll.Lane = lane; ShowLaneName(); }, refresh));
            var menu = Popup(list, 160);
            menu.Placement = PlacementMode.TopEdgeAlignedLeft;
            OpenAnchoredMenu(menu, laneChip);
        };
        ShowLaneName();
        Action<bool>? showLane = null;

        void ShowStep() => stepChip.Content = IconText("\uE8CB", Loc.T("piano.vel.step", pianoRoll.VelocityStep > 1 ? pianoRoll.VelocityStep.ToString() : Loc.T("piano.snap.off")) + "  \u25BE");
        void SetStep(int v)
        {
            pianoRoll.VelocityStep = v;
            ShowStep();
            SaveViewPref("pianoVelStep", pianoRoll.VelocityStep.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        void AskStep() => AskName(stepChip, null, Loc.T("piano.vel.step.ask"), pianoRoll.VelocityStep.ToString(), Loc.T("piano.length.ok"), text =>
        {
            if (int.TryParse(text, out int v) && v >= 0 && v <= 127) SetStep(v);
            else Toast(Loc.T("piano.vel.step.bad"));
        });
        stepChip.Click += (_, _) =>
        {
            var refresh = new List<Action>();
            var list = new List<object>();
            foreach (int v in VelSteps)
                list.Add(MenuCheck(v == 0 ? Loc.T("piano.snap.off") : v.ToString(), () => pianoRoll.VelocityStep == v || v == 0 && pianoRoll.VelocityStep <= 1, () => SetStep(v), refresh));
            list.Add(MenuAction(Loc.T("piano.length.other.more"), () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(AskStep);
                return Task.CompletedTask;
            }));
            var menu = Popup(list, 160);
            // the bar sits at the very bottom: its menus open upward
            menu.Placement = PlacementMode.TopEdgeAlignedLeft;
            OpenAnchoredMenu(menu, stepChip);
        };

        void ShowLane(bool on)
        {
            showLane?.Invoke(on);
            pianoRoll.VelocityShown = on;
            show.IsChecked = on;
            laneChip.IsVisible = toolSeg.IsVisible = stepChip.IsVisible = on;
        }
        show.Click += (_, _) =>
        {
            ShowLane(show.IsChecked == true);
            SaveViewPref("pianoVelShown", show.IsChecked == true ? "1" : "0");
        };

        ShowLane(AppConfig.Get("pianoVelShown") != "0");
        var savedTool = AppConfig.Get("pianoVelTool") == "line" ? PianoRollView.VelTool.Line : PianoRollView.VelTool.Pencil;
        pianoRoll.VelocityTool = savedTool;
        toolButtons[(int)savedTool].IsChecked = true;
        pianoRoll.VelocityStep = int.TryParse(AppConfig.Get("pianoVelStep"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int step) ? step : 0;
        ShowStep();

        row.Children.Add(show);
        row.Children.Add(toolSeg);
        row.Children.Add(stepChip);
        var adjust = new Button { Classes = { "chip" }, Content = IconText("", Loc.T("piano.vel.adjust")), Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(adjust, Loc.T("piano.vel.adjust.tip"));
        adjust.Click += (_, _) => ShowVelocityAdjust(adjust);
        row.Children.Add(adjust);
        showLane += on => adjust.IsVisible = on;
        adjust.IsVisible = pianoRoll.VelocityShown;
        // the write tools on the left; Expresión last on the right, so it never moves (asked by the user, 2026-11-04): the
        // lane's tools open to its left. What doesn't fit goes down a row (ToolbarPanel)
        row.Children.Clear();
        var bar = new ToolbarPanel { VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { BuildPianoWriteTools(), BuildPianoChordTool() } });
        var laneTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { laneChip, toolSeg } };
        bar.Children.Add(laneTools);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { stepChip, adjust, show } });
        showLane += on => laneTools.IsVisible = on;
        laneTools.IsVisible = pianoRoll.VelocityShown;
        return bar;
    }

    /// <summary>
    /// The velocity tools' card (asked by the user): Escalar (around the average, in %) and Humanizar (± at random),
    /// each applied with its button as one undo step, on the selected notes or the whole clip. Both values are remembered.
    /// </summary>
    void ShowVelocityAdjust(Control anchor)
    {
        static TextBox Box(string text) => new()
        {
            Classes = { "field", "mono" }, Text = text, Width = 64, Height = 28, MinHeight = 28, FontSize = 12, Padding = new Thickness(6, 0),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        static Button Apply() => new() { Classes = { "btn", "sm", "primary" }, Content = Loc.T("piano.vel.apply"), VerticalAlignment = VerticalAlignment.Center };
        var scaleBox = Box(AppConfig.Get("pianoVelScale") ?? "50");
        var humBox = Box(AppConfig.Get("pianoVelHumanize") ?? "10");
        var scaleOk = Apply();
        var humOk = Apply();
        var target = new TextBlock { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        void ShowTarget() => target.Text = pianoRoll.ToolTargets == 0 ? Loc.T("piano.vel.none") : Loc.T("piano.vel.target", pianoRoll.ToolTargets);
        ShowTarget();

        Control Row(string label, string unit, TextBox box, Button ok, string hint)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new TextBlock { Text = label, Width = 80, FontSize = 12, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(box);
            line.Children.Add(new TextBlock { Text = unit, Classes = { "dim", "mono" }, Width = 16, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(ok);
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(line);
            block.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 });
            return block;
        }
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("piano.vel.adjust.title").ToUpperInvariant() });
        body.Children.Add(Row(Loc.T("piano.vel.scale"), "%", scaleBox, scaleOk, Loc.T("piano.vel.scale.hint")));
        body.Children.Add(Row(Loc.T("piano.vel.humanize"), "±", humBox, humOk, Loc.T("piano.vel.humanize.hint")));
        body.Children.Add(target);
        var card = new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        var flyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.TopEdgeAlignedLeft };

        // the result shows in the card, which stays open to apply again (Ctrl+Z undoes each one)
        void Done(int changed)
        {
            if (changed < 0) return;
            target.Text = changed == 0 ? Loc.T("piano.vel.same") : Loc.T("piano.vel.changed", changed);
        }
        void DoScale()
        {
            if (!double.TryParse(scaleBox.Text?.Trim().TrimEnd('%'), out double pct) || pct < 0 || pct > 400) { scaleBox.Text = "50"; return; }
            SaveViewPref("pianoVelScale", scaleBox.Text!.Trim().TrimEnd('%'));
            Done(pianoRoll.ScaleVelocity(pct));
        }
        void DoHumanize()
        {
            if (!int.TryParse(humBox.Text?.Trim().TrimStart('±'), out int amount) || amount < 0 || amount > 127) { humBox.Text = "10"; return; }
            SaveViewPref("pianoVelHumanize", amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Done(pianoRoll.HumanizeVelocity(amount));
        }
        scaleOk.Click += (_, _) => DoScale();
        humOk.Click += (_, _) => DoHumanize();
        scaleBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { DoScale(); e.Handled = true; } };
        humBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { DoHumanize(); e.Handled = true; } };
        flyout.Opened += (_, _) => { scaleBox.Focus(); scaleBox.SelectAll(); };
        flyout.ShowAt(anchor);
    }

    readonly Button pianoSnapButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    void ShowPianoSnap()
    {
        pianoSnapButton.Content = IconText("", Loc.T("piano.snap.tip", SnapChoices.Label(pianoRoll.SnapMode)));
        ToolTip.SetTip(pianoSnapButton, SnapChoices.Hint(pianoRoll.SnapMode));
    }

    /// <summary>
    /// The piano roll's magnet menu: the timeline's choices, saved apart ("pianoSnap"), plus following the timeline's.
    /// The right button picks and leaves the menu open, like every options menu.
    /// </summary>
    void ShowPianoSnapMenu()
    {
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = Loc.T("piano.snap"), IsEnabled = false, FontSize = 11 } };
        foreach (var id in SnapChoices.PianoIds)
        {
            if (id == "line" || SnapChoices.Breaks.Contains(Array.IndexOf(SnapChoices.Ids, id))) list.Add(new Separator());
            list.Add(MenuCheck(SnapChoices.Label(id), () => pianoRoll.SnapMode == id, () =>
            {
                pianoRoll.SnapMode = id;
                ShowPianoSnap();
                SaveViewPref("pianoSnap", id);
            }, refresh, tip: SnapChoices.Hint(id)));
        }
        // the grid lives here, with the magnet (asked by the user: fewer chips on the bar); also on the ruler's right button
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("piano.grid", GridLabel(pianoRoll.GridMode)) + "…", () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowPianoGridMenu(pianoSnapButton));
            return Task.CompletedTask;
        }));
        var menu = Popup(list, 220);
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        OpenAnchoredMenu(menu, pianoSnapButton);
    }

    static string GridLabel(string id) => id switch
    {
        "auto" => Loc.T("grid.auto"), "snap" => Loc.T("grid.snap"), _ => SnapChoices.Label(id),
    };

    static string GridHint(string id) => id switch
    {
        "auto" => Loc.T("grid.auto.hint"), "snap" => Loc.T("grid.snap.hint"), _ => SnapChoices.Hint(id),
    };

    /// <summary>
    /// The piano roll's grid menu (its header chip or the right button on its ruler): the timeline's choices, saved apart
    /// ("pianoGrid", "Igual que el imán" at first). The right button picks and leaves the menu open, like every options menu.
    /// </summary>
    void ShowPianoGridMenu(Control anchor)
    {
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = Loc.T("grid.title"), IsEnabled = false, FontSize = 11 } };
        foreach (var id in GridIds)
        {
            if (id == "bar" || SnapChoices.BreakBefore(id)) list.Add(new Separator());
            list.Add(MenuCheck(GridLabel(id), () => pianoRoll.GridMode == id, () =>
            {
                pianoRoll.GridMode = id;
                SaveViewPref("pianoGrid", id);
            }, refresh, tip: GridHint(id)));
        }
        var menu = Popup(list, 260);
        if (anchor == pianoSnapButton) menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        OpenAnchoredMenu(menu, anchor);
    }

    /// <summary>
    /// The piano roll in the mixer's place (Mixer, Mastering and Piano roll share it). It shows the selected track
    /// (asked by the user): a MIDI track it doesn't show yet opens whole, and with no MIDI track selected it is empty.
    /// </summary>
    void ShowPiano()
    {
        var frame = PianoRollView.Frame.Keep;
        if (sel?.Track.IsMidi != true) pianoClipId = null;
        else if (sel.Track.Id != PianoTrack()?.Track.Id)
        {
            pianoClipId = PianoClipFor(sel.Track);
            frame = PianoRollView.Frame.Track;
        }
        OpenPianoView(frame);
    }

    void OpenInPiano(string clipId)
    {
        // its track becomes the selected one, so the roll keeps showing it
        if (store != null && MidiEditing.TryFind(store.Project, clipId) != null && Timeline.SelectedMidiId != clipId)
            Timeline.SelectMidiClip(TrackIndex(MidiEditing.Find(store.Project, clipId).Track.Id), clipId);
        pianoClipId = clipId;
        OpenPianoView(PianoRollView.Frame.Clip);
    }

    void OpenPianoView(PianoRollView.Frame frame)
    {
        if (PianoOut)
        {
            ActivatePanel(FloatPiano);
            pianoRoll.SetPlayhead(engine.PositionSec);
            RefreshPiano(frame);
            pianoRoll.Focus();
            return;
        }
        pianoView = true;
        masteringView = false;
        SetMixerView(true);
        ApplyMasteringView();
        pianoRoll.SetPlayhead(engine.PositionSec);
        RefreshPiano(frame);
        pianoRoll.Focus();
    }

    /// <summary>
    /// The piano roll follows the timeline's selection while it is on screen (asked by the user): a MIDI clip clicked
    /// is shown (scrolled into sight if it is on the same track, else with its track whole); a click elsewhere on another
    /// MIDI track (its header, an empty stretch) shows that track whole, on the clip under the playhead; no track, or
    /// an audio one, leaves it empty.
    /// </summary>
    void FollowPiano(int i)
    {
        if (!PianoShown || store == null) return;
        if (i < 0 || i >= tracks.Count || !tracks[i].Track.IsMidi)
        {
            if (pianoClipId == null) return;
            pianoClipId = null;
            RefreshPiano();
            return;
        }
        var track = tracks[i].Track;
        bool sameTrack = PianoTrack()?.Track.Id == track.Id;
        if (Timeline.SelectedMidiId is { } id && track.MidiClips?.Any(c => c.Id == id) == true)
        {
            if (id == pianoClipId) return;
            pianoClipId = id;
            RefreshPiano(sameTrack ? PianoRollView.Frame.Reveal : PianoRollView.Frame.Track);
            return;
        }
        if (sameTrack) return;
        pianoClipId = PianoClipFor(track);
        RefreshPiano(PianoRollView.Frame.Track);
    }

    /// <summary>
    /// The clip to edit when the piano roll opens on a track: the one selected in the timeline, else the one under the
    /// playhead, the next one after it or the last one.
    /// </summary>
    string? PianoClipFor(XtremeDaw.Projects.Track track)
    {
        var clips = track.MidiClips;
        if (clips == null || clips.Count == 0) return null;
        if (clips.Any(c => c.Id == Timeline.SelectedMidiId)) return Timeline.SelectedMidiId;
        double pos = engine.PositionSec;
        var map = store?.Project.Settings.TempoMap ?? TempoMap.Constant(120);
        return (clips.LastOrDefault(c => pos >= c.AtSec && pos < c.EndSec(map))
            ?? clips.Where(c => c.AtSec > pos).MinBy(c => c.AtSec)
            ?? clips.MaxBy(c => c.AtSec))!.Id;
    }

    /// <summary>The track of the piano roll's clip (null = none), for its keys played live.</summary>
    string? pianoTrackId;

    /// <summary>The keys played live on the piano roll's track (a MIDI keyboard or the virtual one), or null. Allocates
    /// nothing: it runs on every tick.</summary>
    XtremeDaw.Audio.Midi.LiveNotes? PianoKeys()
    {
        if (pianoTrackId == null) return null;
        foreach (var t in tracks)
            if (t.Track.Id == pianoTrackId) return t.Keys;
        return null;
    }

    /// <summary>
    /// Hands the piano roll its clip again from the project (after an edit, an undo, a tempo change or opening a
    /// project), with the track's other clips around it; <paramref name="frame"/> says how its view moves.
    /// </summary>
    void RefreshPiano(PianoRollView.Frame frame = PianoRollView.Frame.Keep)
    {
        ApplyScale();
        var p = store?.Project;
        var clip = p == null ? null : MidiEditing.TryFind(p, pianoClipId);
        if (clip == null) pianoClipId = null;
        var track = clip == null ? null : MidiEditing.Find(p!, clip.Id).Track;
        pianoTrackId = track?.Id;
        pianoRoll.SetClip(clip, clip?.PatternId is { } id ? MidiPatterns.Clips(p!, id) : track?.MidiClips, track?.Name, track == null ? null : TrackIcons.ColorOf(track), p?.Settings.TempoMap ?? TempoMap.Constant(120), frame, clip == null ? null : PartGhosts(p!, clip));
        RefreshPianoParts(p, clip, track);
        pianoTitle.Text = clip == null || track == null ? ""
            : clip.PatternId is { } pattern ? Loc.T("piano.pattern", clip.PatternName!, clip.Notes.Count, PatternClipsLabel(MidiPatterns.Clips(p!, pattern).Count()))
            : Loc.T("piano.clip", track.Name, $"{clip.LengthBeats / 4:0.##}", clip.Notes.Count);
        ShowRollExtras();
        RefreshPianoInstrument();
    }

    readonly Button pianoInstrumentButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Button pianoOpenButton = new()
    {
        Classes = { "chip" }, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13 },
    };

    /// <summary>The live track of the clip the piano roll shows, or null.</summary>
    LiveTrack? PianoTrack() => store != null && MidiEditing.TryFind(store.Project, pianoClipId) != null
        ? tracks.FirstOrDefault(t => t.Track.Id == MidiEditing.Find(store.Project, pianoClipId!).Track.Id) : null;

    /// <summary>The instrument chip: its name (or the test tone, or "cargando…"), and the window button lit when it is up.</summary>
    void RefreshPianoInstrument()
    {
        var t = PianoTrack();
        pianoInstrumentButton.IsVisible = pianoOpenButton.IsVisible = t != null;
        if (t == null) return;
        var plugin = t.Track.Instrument;
        string name = plugin != null && instrumentLoads.ContainsKey(t.Track.Id) ? Loc.T("vst.loading", plugin.Name) : InstrumentName(t.Track);
        pianoInstrumentButton.Content = IconText("", name + "  ▾");
        pianoOpenButton.IsEnabled = t.Track.Sampler != null || t.Track.Granular != null || t.Track.Gm != null || instruments.ContainsKey(t.Track.Id);
    }

    int TrackIndex(string? trackId) => tracks.FindIndex(t => t.Track.Id == trackId);

    Task NewMidiTrack()
    {
        if (store == null || store.ReadOnly) return Task.CompletedTask;
        return EditTimeline(p => MidiEditing.AddTrack(p), "track-added");
    }

    /// <summary>A new MIDI track that already plays with an instrument, named after it: one undo step. A native one
    /// opens its window, where its sample is loaded; a plugin loads right after.</summary>
    async Task NewInstrumentTrack(string name, Action<XtremeDaw.Projects.Track> setup)
    {
        if (store == null || store.ReadOnly) return;
        string? id = null;
        await EditTimeline(p =>
        {
            id = MidiEditing.AddTrack(p, name);
            setup(p.Tracks.Find(t => t.Id == id)!);
            return id;
        }, "track-added");
        if (store.Project.Tracks.Find(t => t.Id == id) is not { } made) return;
        if (made.Sampler != null) OpenSamplerWindow(made.Id);
        else if (made.Granular != null) OpenGranularWindow(made.Id);
    }

    async Task DuplicateMidiClip(string clipId)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) is not { } clip) return;
        if (MidiPatterns.Together(store.Project.Tracks, clip).Count > 0)
        {
            await DuplicateClips([clipId]);
            return;
        }
        string trackId = MidiEditing.Find(store.Project, clipId).Track.Id;
        string? copy = null;
        await EditTimeline(p => { copy = MidiEditing.Duplicate(p, clipId, Timeline.SnapSec); return trackId; }, "midi-clip-duplicated");
        if (copy != null) Timeline.SelectMidiClip(TrackIndex(trackId), copy);
    }

    Task RemoveMidiClip(string clipId)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return Task.CompletedTask;
        string trackId = MidiEditing.Find(store.Project, clipId).Track.Id;
        return EditTimeline(p => { MidiEditing.RemoveClip(p, clipId); return trackId; }, "midi-clip-removed");
    }

    /// <summary>A pattern's edit on a clip: one undo step, the clip still selected.</summary>
    async Task EditPattern(string clipId, Action<Project> edit, string activity)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
        string trackId = MidiEditing.Find(store.Project, clipId).Track.Id;
        await EditTimeline(p => { edit(p); return trackId; }, activity);
        Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
    }

    static string BarsLabel(double bars) => bars == 1 ? Loc.T("midi.pattern.bar") : Loc.T("midi.pattern.bars", $"{bars:0.##}");

    /// <summary>A new pass, seen whole when the piano roll is on it.</summary>
    async Task SetPatternBeats(string clipId, double beats)
    {
        await EditPattern(clipId, p => MidiPatterns.SetBeats(p, clipId, beats), "midi-pattern-length");
        if (pianoClipId == clipId) RefreshPiano(PianoRollView.Frame.Clip);
    }

    /// <summary>The lengths offered for a pattern; <paramref name="withOther"/> adds a box for any number of bars.</summary>
    List<object> PatternLengthItems(string clipId, bool withOther)
    {
        var refresh = new List<Action>();
        var items = new List<object>();
        foreach (int bars in MidiPatterns.Bars)
            items.Add(MenuCheck(BarsLabel(bars), () => store != null && MidiEditing.TryFind(store.Project, clipId)?.CellBeats == bars * 4,
                () => _ = SetPatternBeats(clipId, bars * 4), refresh));
        if (withOther) items.Add(MenuAction(Loc.T("piano.length.other.more"), () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AskPatternBars(clipId));
            return Task.CompletedTask;
        }));
        return items;
    }

    void AskPatternBars(string clipId)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) is not { } clip) return;
        AskName(pianoPartsButton, null, Loc.T("piano.length.ask"), $"{clip.CellBeats / 4:0.##}", Loc.T("piano.length.ok"), text =>
        {
            if (double.TryParse(text, out double bars) && bars > 0) _ = SetPatternBeats(clipId, bars * 4);
            else Toast(Loc.T("piano.length.bad"));
        });
    }

    /// <summary>What the clip's menu offers about patterns: making it one or, on a pattern's clip, its name, its pass and leaving it.</summary>
    List<object> PatternItems(string clipId, bool enabled, Point point)
    {
        var clip = MidiEditing.TryFind(store!.Project, clipId);
        // on one of several boxed MIDI clips: one pattern as long as all of them, or one each (#201)
        var many = Timeline.BoxedClipIds.Contains(clipId)
            ? Timeline.BoxedClipIds.Where(id => MidiEditing.TryFind(store.Project, id) != null).ToList() : [];
        if (many.Count > 1)
        {
            var loose = many.Where(id => MidiEditing.TryFind(store.Project, id)?.PatternId == null).ToList();
            return
            [
                MenuAction(Loc.T("midi.pattern.createOne"), () => EditPattern(clipId, p => MidiPatterns.CreateFrom(p, many), "midi-pattern-created"),
                    enabled: enabled, tip: Loc.T("midi.pattern.createOne.tip")),
                MenuAction(Loc.T("midi.pattern.createEach"), async () =>
                {
                    await EditPattern(clipId, p => { foreach (var id in loose) MidiPatterns.Create(p, id); }, "midi-pattern-created");
                    Timeline.BoxClips(many);
                }, enabled: enabled && loose.Count > 0, tip: Loc.T("midi.pattern.createEach.tip")),
            ];
        }
        if (clip?.PatternId == null)
            return [MenuAction(Loc.T("midi.pattern.create"), () => EditPattern(clipId, p => MidiPatterns.Create(p, clipId), "midi-pattern-created"), enabled: enabled, tip: Loc.T("midi.pattern.create.tip"))];
        var lengths = PatternLengthItems(clipId, withOther: false);
        return
        [
            // its name is the clip's "Renombrar…" (MidiEditing.Rename)
            new MenuItem { Header = new TextBlock { Text = Loc.T("midi.pattern.length", BarsLabel(clip.CellBeats / 4)), FontSize = 12 }, ItemsSource = lengths, IsEnabled = enabled },
            new MenuItem { Header = new TextBlock { Text = Loc.T("piano.parts.add"), FontSize = 12 }, ItemsSource = AddPartItems(clipId), IsEnabled = enabled },
            MenuAction(Loc.T("midi.pattern.unique"), () => EditPattern(clipId, p => MidiPatterns.MakeUnique(p, clipId), "midi-pattern-left"), enabled: enabled, tip: Loc.T("midi.pattern.unique.tip")),
        ];
    }

    /// <summary>The MIDI track's menu: a new clip of any of the project's patterns, at the playhead.</summary>
    MenuItem InsertPatternItem(LiveTrack track, bool enabled)
    {
        var patterns = store == null ? [] : MidiPatterns.All(store.Project);
        var items = patterns.Select(x => (object)MenuAction(x.Name, () => InsertPattern(track.Track.Id, x.Id, engine.PositionSec), BarsLabel(x.Beats / 4))).ToList();
        return new MenuItem { Header = new TextBlock { Text = Loc.T("midi.pattern.insert"), FontSize = 12 }, ItemsSource = items, IsEnabled = enabled && items.Count > 0 };
    }

    void ShowMidiClipMenu(LiveTrack track, string clipId, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly;
        // on one of several boxed clips, duplicate and remove act on them all (takes too)
        var boxed = Timeline.BoxedClipIds.Contains(clipId) ? Timeline.BoxedClipIds.ToList() : [];
        var list = new List<object>
        {
            new MenuItem { Header = track.Track.Name, IsEnabled = false, FontSize = 11 },
        };
        AddRangeItems(list, track, enabled);
        double at = engine.PositionSec;
        bool under = MidiEditing.TryFind(store.Project, clipId) is { } clip && MidiEditing.CanSplit(clip, at, store.Project.Settings.TempoMap);
        list.AddRange(
        [
            MenuAction(Loc.T("midi.menu.open"), () => { OpenInPiano(clipId); return Task.CompletedTask; }, icon: ""),
            new Separator(),
            // the same groups and order as an audio clip's menu: edit, place, then its own tools
            MenuAction(Loc.T("menu.cut"), () => CopySelection(cut: true), Shortcuts.Label("cut"), enabled && !track.Track.EditLocked),
            MenuAction(Loc.T("menu.copy"), () => CopySelection(cut: false), Shortcuts.Label("copy")),
            MenuAction(Loc.T("menu.duplicate"), () => boxed.Count > 1 ? DuplicateClips(boxed) : DuplicateMidiClip(clipId), Shortcuts.Label("duplicate"), enabled),
            MenuAction(Loc.T("menu.split"), () => SplitMidi(track, clipId, at), Shortcuts.First("split"), enabled && under && !track.Track.EditLocked),
            MenuAction(Loc.T("menu.renameClip.more"), () => { BeginClipRename(clipId, point); return Task.CompletedTask; }, Shortcuts.Label("renameClip"), enabled: enabled, icon: "\uE8AC",
                tip: MidiEditing.TryFind(store.Project, clipId)?.PatternId != null ? Loc.T("menu.renameClip.pattern") : null),
            new Separator(),
            MenuAction(Loc.T("midi.quantize.menu", PianoSnapLabel()), () => Quantize(boxed.Count > 1 ? boxed : [clipId]), Shortcuts.Label("quantize"),
                enabled && pianoRoll.QuantizeBeats > 0, tip: pianoRoll.QuantizeBeats > 0 ? null : Loc.T("midi.quantize.free")),
            new Separator(),
        ]);
        list.AddRange(PatternItems(clipId, enabled, point));
        list.Add(new Separator());
        list.Add(MenuAction(boxed.Count > 1 ? Loc.T("menu.removeClips", boxed.Count) : Loc.T("menu.removeClip"),
            () => boxed.Count > 1 ? DeleteTimelineSelection() : RemoveMidiClip(clipId), Shortcuts.First("delete"), enabled, danger: true));
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }
}
