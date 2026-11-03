using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using XtremeDaw.App.Model;

namespace XtremeDaw.App;

/// <summary>
/// The menu bar in the title bar (asked by the user: without it the app looked like a toy): Archivo, Edición, Pista,
/// Ver, Transporte, Herramientas and Ayuda, as in any DAW. It replaced the project chip's menu and the title bar's
/// icons for Configuración, Ayuda, Novedades and Comentarios. Each menu is built again when it opens, so what can't be
/// done now shows disabled, and its keys are read from <see cref="Shortcuts"/>. The entries run the same code as their
/// keys and buttons: nothing here does the work itself. The titles are plain buttons that open the app's own popup
/// menu (Avalonia's Menu didn't take clicks in the title bar).
/// </summary>
public partial class MainWindow
{
    /// <summary>How wide a menu of the bar opens: the labels and their keys don't crowd.</summary>
    const double MenuBarWidth = 270;

    /// <summary>The title of the bar whose menu is open now, so hovering another one opens that one instead.</summary>
    Button? barOpen;

    void InitMenuBar()
    {
        foreach (var (key, build) in new (string, Func<List<object>>)[]
        {
            ("menubar.file", FileMenu), ("menubar.edit", EditMenu), ("menubar.track", TrackMenu), ("menubar.view", ViewMenu),
            ("menubar.transport", TransportMenu), ("menubar.tools", ToolsMenu), ("menubar.help", HelpMenu),
        })
        {
            // no keyboard focus, like the strip buttons: Space and Enter stay shortcuts after using the menu
            var b = new Button { Classes = { "menubar" }, Content = Loc.T(key), Focusable = false };
            b.Click += (_, _) => OpenBarMenu(b, build);
            // like any menu bar: with one open, moving to another title opens that one
            b.PointerEntered += (_, _) =>
            {
                if (barOpen != null && barOpen != b && timelineMenu is { IsOpen: true }) OpenBarMenu(b, build);
            };
            AppMenu.Children.Add(b);
        }
    }

    /// <summary>
    /// Opens a menu of the bar under its title, built now (it reads the state of this moment). It is the same kind
    /// of menu as the timeline's right click (<see cref="Popup"/>, one open at a time); a second press on the title
    /// closes it.
    /// </summary>
    void OpenBarMenu(Button title, Func<List<object>> build)
    {
        if (PressClosedMenuOf(title)) return;
        var menu = Popup(build(), MenuBarWidth);
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.MaxHeight = Bounds.Height - 60;
        menu.Closed += (_, _) =>
        {
            title.Classes.Set("open", false);
            if (barOpen == title) barOpen = null;
        };
        OpenAnchoredMenu(menu, title);
        title.Classes.Set("open", true);
        barOpen = title;
    }

    /// <summary>An entry with its key from <see cref="Shortcuts"/> (the first one, when it has two).</summary>
    static MenuItem Entry(string label, Func<Task> run, string? shortcut = null, bool enabled = true, string? tip = null)
    {
        string? key = shortcut != null && Shortcuts.Keys(shortcut) is [var first, ..] ? Shortcuts.Label(first) : null;
        return MenuAction(label, run, key, enabled, tip: tip);
    }

    static MenuItem Entry(string label, Action run, string? shortcut = null, bool enabled = true, string? tip = null) =>
        Entry(label, () => { run(); return Task.CompletedTask; }, shortcut, enabled, tip);

    /// <summary>An option that is on or off (bucle, click, the panels): checked while it is on.</summary>
    static MenuItem Check(string label, bool on, Action toggle, string? shortcut = null, bool enabled = true)
    {
        string? key = shortcut != null && Shortcuts.Keys(shortcut) is [var first, ..] ? Shortcuts.Label(first) : null;
        return MenuAction(label, () => { toggle(); return Task.CompletedTask; }, key, enabled, icon: on ? "" : " ");
    }

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Nothing covers the window: no dialog open and no take running.</summary>
    bool MenuFree => !Projects.IsVisible && !Export.IsVisible && !Settings.IsVisible && !RecordingBusy;
    bool HasProject => store != null && !restoring;
    bool CanEdit => store is { ReadOnly: false } && !restoring && MenuFree;

    List<object> FileMenu() =>
    [
        Entry(Loc.T("menubar.projects"), ShowProjects, enabled: !RecordingBusy && !Export.IsVisible),
        new Separator(),
        Entry(Loc.T("menubar.save"), SaveNow, "save", HasProject && store!.ReadOnly == false),
        Entry(Loc.T("proj.saveAs"), SaveAsShortcut, "saveAs", HasProject && MenuFree),
        Entry(Loc.T("proj.saveCopy"), SaveCopyOfOpen, enabled: HasProject && MenuFree),
        VersionsItem(),
        new Separator(),
        Entry(Loc.T("menu.import"), PickAudioToImport, enabled: CanImport()),
        Entry(Loc.T("menubar.export"), () => Press(ExportButton), enabled: HasProject && MenuFree),
        new Separator(),
        Entry(Loc.T("settings.title.more"), () => Settings.Show(), enabled: MenuFree),
        new Separator(),
        Entry(Loc.T("menubar.exit"), Close),
    ];

    List<object> EditMenu()
    {
        bool edit = CanEdit;
        return
        [
            Entry(Loc.T("sc.undo"), () => RestoreEdit(false), "undo", edit && UndoButton.IsEnabled),
            Entry(Loc.T("sc.redo"), () => RestoreEdit(true), "redo", edit && RedoButton.IsEnabled),
            new Separator(),
            Entry(Loc.T("menubar.cut"), () => RunEdit("cut"), "cut", edit),
            Entry(Loc.T("menubar.copy"), () => RunEdit("copy"), "copy", HasProject && MenuFree),
            Entry(Loc.T("menubar.paste"), () => RunEdit("paste"), "paste", edit),
            Entry(Loc.T("menubar.duplicate"), () => RunEdit("duplicate"), "duplicate", edit),
            Entry(Loc.T("menubar.split"), SplitAtPlayhead, "split", edit),
            Entry(Loc.T("menubar.delete"), () => RunEdit("delete"), "delete", edit),
            new Separator(),
            Entry(Loc.T("menubar.selectAll"), () => RunEdit("selectAll"), "selectAll", HasProject && MenuFree),
            Entry(Loc.T("menubar.deselect"), () => RunEdit("deselect"), "deselect", HasProject && MenuFree),
            Entry(Loc.T("menubar.alignGrid"), () => RunEdit("alignGrid"), "alignGrid", edit),
        ];
    }

    List<object> TrackMenu()
    {
        var list = MenuFree ? NewTrackItems() ?? [] : [];
        if (list.Count == 0) list.Add(Entry(Loc.T("sc.newTrack"), () => { }, enabled: false));
        bool picked = sel != null && CanEdit;
        list.Add(new Separator());
        list.Add(Entry(Loc.T("menubar.arm"), () => { if (sel != null) ToggleArm(sel); }, "arm", picked));
        list.Add(Entry(Loc.T("menubar.mute"), () => TogglePicked(false), "mute", picked));
        list.Add(Entry(Loc.T("menubar.solo"), () => TogglePicked(true), "solo", picked));
        return list;
    }

    List<object> ViewMenu()
    {
        bool piano = PianoShown && !ViewsCovered, mastering = MasteringShown && !ViewsCovered;
        // a view in its own window is on screen along with what the place shows
        bool mixer = MixerShown && !ViewsCovered, timeline = !mixerView || TimelineOut || MixerOut;
        bool audioOnly = store != null && AudioOnly, modes = store is { ReadOnly: false } && MenuFree;
        return
        [
            Check(Loc.T("transport.view.timeline"), timeline, () => Press(TimelineViewButton)),
            Check(Loc.T("transport.view.piano"), piano, () => Press(PianoViewButton)),
            Check(Loc.T("transport.view.mixer"), mixer, () => Press(MixerViewButton), "mixer"),
            Check(Loc.T("transport.view.mastering"), mastering, () => Press(MasteringViewButton)),
            new Separator(),
            Check(Loc.T("menubar.leftPanel"), !inspectorHidden || InspectorOut, ToggleInspectorShown, "leftPanel"),
            Check(Loc.T("menubar.rightPanel"), !projectBarHidden || ProjectBarOut, ToggleProjectBarShown, "rightPanel"),
            PanelsOutItem(),
            new Separator(),
            Entry(Loc.T("sc.fit"), () => Timeline.FitAll(), "fit", HasProject),
            Entry(Loc.T("sc.maximize"), ToggleMaxPanel, "maximize"),
            Check(Loc.T("sc.fullScreen"), WindowState == WindowState.FullScreen, ToggleFullScreen, "fullScreen"),
            new Separator(),
            Check(Loc.T("mode.video"), store != null && !audioOnly, () => ChangeMode("video"), enabled: modes),
            Check(Loc.T("mode.audio"), audioOnly, () => ChangeMode("audio"), enabled: modes),
        ];
    }

    List<object> TransportMenu()
    {
        bool open = HasProject && MenuFree;
        return
        [
            Entry(Loc.T("menubar.play"), TogglePlay, "play", HasProject),
            Entry(Loc.T("menubar.record"), StartRecording, "record", open && RecButton.IsEnabled),
            Entry(Loc.T("sc.toStart"), () => engine.Seek(0), "toStart", HasProject),
            new Separator(),
            Check(Loc.T("menubar.loop"), LoopToggle.IsChecked == true, () => LoopToggle.IsChecked = LoopToggle.IsChecked != true, "loop"),
            Check(Loc.T("menubar.click"), ClickToggle.IsChecked == true, () => ClickToggle.IsChecked = ClickToggle.IsChecked != true, "click"),
            Check(Loc.T("menubar.countIn"), CountInToggle.IsChecked == true, () => CountInToggle.IsChecked = CountInToggle.IsChecked != true, "countIn"),
            new Separator(),
            Entry(Loc.T("sc.marker"), () => AddMarker(engine.PositionSec, rename: false), "marker", CanEdit),
            Check(Loc.T("sc.virtualKeys"), VirtualKeysOn, () => VirtualKeysButton.IsChecked = !VirtualKeysOn, "virtualKeys"),
            Check(Loc.T("sc.tuner"), TunerToggle.IsChecked == true, () => TunerToggle.IsChecked = TunerToggle.IsChecked != true, "tuner"),
        ];
    }

    /// <summary>The analysis of the selected audio clip (the same entries as its menu), then plugins and components.</summary>
    List<object> ToolsMenu()
    {
        var list = new List<object>();
        if (sel is { } track && SelectedInspectorClip is { } clip && HasProject && MenuFree)
        {
            bool enabled = store!.ReadOnly == false;
            // the tempo's answer opens over the timeline, near its top left corner
            list.Add(DetectTempoItem(clip, new Point(Timeline.HeaderW + 24, 32), enabled));
            list.Add(DetectScaleItem(clip, enabled));
            list.Add(ExtractMidiItem(track, clip, enabled));
            list.Add(StemsItem(track, clip, enabled));
        }
        else
            foreach (var key in new[] { "tempo.detect.more", "scale.detect", "toMidi.menu", "stems.menu" })
                list.Add(Entry(Loc.T(key), () => { }, enabled: false, tip: Loc.T("menubar.needClip")));
        list.Add(new Separator());
        list.Add(Entry(Loc.T("menubar.plugins"), () => Settings.Show("plugins"), enabled: MenuFree));
        list.Add(Entry(Loc.T("menubar.components"), () => ComponentsHelp.Show(), enabled: MenuFree));
        return list;
    }

    List<object> HelpMenu() =>
    [
        Entry(Loc.T("help.title"), OpenHelp, "help"),
        Entry(Loc.T("menubar.shortcuts"), () => Settings.Show("shortcuts"), enabled: MenuFree),
        Entry(Loc.T("news.title"), OpenNews),
        Entry(Loc.T("feedback.title.more"), OpenFeedback, enabled: !RecordingBusy),
        new Separator(),
        Entry(Loc.T("menubar.about"), () => About.Show(), enabled: MenuFree),
    ];

    /// <summary>Archivo → "Guardar una copia…": the copy is written elsewhere and the window stays in this project.</summary>
    async Task SaveCopyOfOpen()
    {
        if (store == null || Projects.IsVisible) return;
        try
        {
            if (await SaveProjectCopy(store.Dir, store.Project.Name, saveAs: false) is { } dir)
                Toast(Loc.T("proj.saveCopy.done", dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Toast(Loc.T("proj.err.saveAs", e.Message.TrimEnd('.')));
        }
    }
}
