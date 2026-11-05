using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The song's scale and the chord tool (asked by the user: super simple). There is one scale, in the project: the same
/// picker opens from the piano roll and from the virtual keyboard, and choosing it reaches both at once
/// (<see cref="ApplyScale"/>, on every refresh, so an undo or another project brings theirs). The picker stays open, so
/// you can try roots and scales while you play. The chord tool is a chip and a kind in the piano roll's header; the
/// inversions are picked on the chord itself (<see cref="PianoRollView"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>The strip shows at least this much with a scale: the three rows of keys.</summary>
    const int ScaleOctaves = 3;
    /// <summary>The picker's width and the height of its rows, like a menu's; the space above a family's title.</summary>
    const double ScalePickerW = 372, ScaleRowH = 22, ScaleGroupGap = 6;

    readonly Button pianoScaleButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The scale a root takes when none was on: the last one used.</summary>
    string lastScaleId = "major";
    /// <summary>The picker open from a plugin's panel, and what it shows.</summary>
    (Flyout Flyout, Control Card)? pluginPicker;

    static string ScaleName(string id) => Loc.T($"scale.kind.{id}");

    /// <summary>"Sol menor natural", or that there is none.</summary>
    string ScaleLabel() => virtualKeys.Scale is { } s
        ? Loc.T("scale.current", NoteNames.Class(s.Root), ScaleName(s.Kind.Id).ToLowerInvariant())
        : Loc.T("scale.off");

    /// <summary>The project's scale reaches the piano roll and the keyboard, if it changed.</summary>
    void ApplyScale()
    {
        var s = store?.Project.Settings;
        var scale = s == null ? null : MusicScale.Find(s.ScaleRoot, s.Scale);
        engine.KeySnap = AppConfig.Get("midiScaleSnap") == "true" ? scale : null;
        if (scale?.Root == virtualKeys.Scale?.Root && scale?.Kind == virtualKeys.Scale?.Kind) return;
        pianoRoll.Scale = scale;
        virtualKeys.Scale = scale;
        ShowChordTool();
    }

    /// <summary>A new scale for the song; one step of undo.</summary>
    async Task SetScale(int root, string? id)
    {
        if (store == null) return;
        bool first = store.Project.Settings.Scale == null;
        await EditTimeline(p =>
        {
            p.Settings.Scale = id;
            p.Settings.ScaleRoot = root;
            return sel?.Track.Id;
        }, "midi-scale");
        if (id == null || !first) return;
        // the first time, the three rows have to be in sight
        SetVirtualRange(Math.Max(virtualRange, ScaleOctaves));
        foreach (var p in virtualPanels) p.Strip.ShowFrom(virtualKeys.BasePitch + (virtualKeys.Scale?.Root ?? 0) - 12);
    }

    /// <summary>
    /// The picker, as compact as a menu (asked by the user): the 12 roots on one row, the scales as plain rows in three
    /// columns, the modes split over the last two, and at the foot none and taking the notes that stayed out to the scale.
    /// </summary>
    /// <param name="anchor">The button it opens from.</param>
    /// <param name="inPlugin">It opens from the panel inside a plugin's window, at the bottom of it: above the button, and <see cref="DismissPluginPicker"/> closes it.</param>
    /// <param name="above">It opens upward: from the bar at the bottom of the piano roll.</param>
    void ShowScalePicker(Control anchor, bool inPlugin = false, bool above = false)
    {
        var settings = store?.Project.Settings;
        int root = settings?.ScaleRoot ?? 0;
        string? id = settings?.Scale;
        var marks = new List<Action>();
        void Show()
        {
            foreach (var mark in marks) mark();
        }
        ToggleButton Choice(string text, Func<bool> on, Action pick, double padding = 6)
        {
            var b = new ToggleButton { Classes = { "seg", "pick", "flat" }, Content = text, Height = ScaleRowH, MinHeight = 0, MinWidth = 0, Padding = new Thickness(padding, 0), FontSize = 11, Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            marks.Add(() => b.IsChecked = on());
            b.Click += (_, _) => { pick(); Show(); _ = SetScale(root, id); };
            return b;
        }
        static TextBlock Title(string text, double top = 0) => new() { Classes = { "label" }, Text = text.ToUpperInvariant(), Margin = new Thickness(6, top, 0, 2) };
        Control Row(ScaleKind kind)
        {
            var b = Choice(ScaleName(kind.Id), () => id == kind.Id, () => id = lastScaleId = kind.Id);
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            return b;
        }

        var roots = new UniformGrid { Columns = 12 };
        for (int r = 0; r < 12; r++)
        {
            int picked = r;
            var b = Choice(NoteNames.Class(r), () => root == picked, () => { root = picked; id ??= lastScaleId; }, padding: 0);
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            roots.Children.Add(b);
        }

        var families = new StackPanel();
        families.Children.Add(Title(Loc.T($"scale.group.{MusicScale.Major}")));
        foreach (var kind in MusicScale.Kinds.Where(k => k.Group == MusicScale.Major)) families.Children.Add(Row(kind));
        families.Children.Add(Title(Loc.T($"scale.group.{MusicScale.Minor}"), ScaleGroupGap));
        foreach (var kind in MusicScale.Kinds.Where(k => k.Group == MusicScale.Minor)) families.Children.Add(Row(kind));
        var others = MusicScale.Kinds.Where(k => k.Group == MusicScale.Other).ToList();
        var modes = new StackPanel();
        modes.Children.Add(Title(Loc.T($"scale.group.{MusicScale.Other}")));
        foreach (var kind in others.Take((others.Count + 1) / 2)) modes.Children.Add(Row(kind));
        // the same height as the title beside it, so the rows line up
        var rest = new StackPanel();
        rest.Children.Add(Title(" "));
        foreach (var kind in others.Skip((others.Count + 1) / 2)) rest.Children.Add(Row(kind));
        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 4 };
        Grid.SetColumn(modes, 1);
        Grid.SetColumn(rest, 2);
        lists.Children.Add(families);
        lists.Children.Add(modes);
        lists.Children.Add(rest);

        // none on top, as a chip of its own (asked by the user, 2026-11-04: as a row at the foot it got lost)
        var off = new ToggleButton { Classes = { "chip" }, Content = Loc.T("scale.off"), Height = ScaleRowH + 2, MinHeight = 0, Padding = new Thickness(8, 0), FontSize = 11, Focusable = false };
        marks.Add(() => off.IsChecked = id == null);
        off.Click += (_, _) => { id = null; Show(); _ = SetScale(root, id); };
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(off, Dock.Right);
        head.Children.Add(off);
        var rootTitle = Title(Loc.T("scale.pick.root"));
        rootTitle.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(rootTitle);
        var body = new StackPanel { Spacing = 6, Width = ScalePickerW };
        body.Children.Add(head);
        body.Children.Add(roots);
        body.Children.Add(lists);
        var fit = new Button { Classes = { "chip" }, Content = Loc.T("scale.fit"), Height = ScaleRowH + 2, MinHeight = 0, Padding = new Thickness(8, 0), FontSize = 11, Focusable = false };
        ToolTip.SetTip(fit, Loc.T("scale.fit.tip"));
        marks.Add(() => fit.IsEnabled = id != null);
        fit.Click += (_, _) =>
        {
            int moved = pianoRoll.FitToScale();
            if (moved < 0) Toast(Loc.T("scale.fit.noClip"));
            else Status(Loc.T("scale.fit.done", moved));
        };
        var snapKeys = new ToggleButton { Classes = { "chip" }, Content = Loc.T("scale.snapKeys"), Height = ScaleRowH + 2, MinHeight = 0, Padding = new Thickness(8, 0), FontSize = 11, Focusable = false, IsChecked = AppConfig.Get("midiScaleSnap") == "true", Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(snapKeys, Loc.T("scale.snapKeys.tip"));
        snapKeys.Click += (_, _) =>
        {
            SaveViewPref("midiScaleSnap", snapKeys.IsChecked == true ? "true" : "false");
            engine.KeySnap = snapKeys.IsChecked == true ? virtualKeys.Scale : null;
        };
        var bottom = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(fit, Dock.Right);
        DockPanel.SetDock(snapKeys, Dock.Right);
        bottom.Children.Add(fit);
        bottom.Children.Add(snapKeys);
        body.Children.Add(bottom);
        Show();
        var card = new Border
        {
            Padding = new Thickness(8), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        var flyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = inPlugin || above ? PlacementMode.TopEdgeAlignedLeft : PlacementMode.BottomEdgeAlignedLeft };
        if (inPlugin)
        {
            pluginPicker = (flyout, card);
            flyout.Closed += (_, _) =>
            {
                if (pluginPicker?.Flyout == flyout) pluginPicker = null;
            };
        }
        flyout.ShowAt(anchor);
    }

    /// <summary>Every tick: the picker on a plugin's panel closes on a click anywhere else. The plugin's window is another process's, so that click never reaches the picker.</summary>
    void DismissPluginPicker()
    {
        if (pluginPicker is not { } open || !ButtonHeld || TopLevel.GetTopLevel(open.Card) is not { } top || !GetCursorPos(out var at)) return;
        var room = new PixelRect(open.Card.PointToScreen(default), PixelSize.FromSize(open.Card.Bounds.Size, top.RenderScaling));
        if (!room.Contains(new PixelPoint(at.X, at.Y))) open.Flyout.Hide();
    }

    readonly ToggleButton pianoChordButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    readonly Button pianoChordKindButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The chord card's width and the height of its choices.</summary>
    const double ChordCardW = 360, ChordChoiceH = 26;

    /// <summary>The chord read from the keys, in the piano roll's header.</summary>
    Control BuildPianoScaleTools() => pianoChordLabel;

    void SetChordTool(bool on)
    {
        pianoRoll.ChordTool = on;
        ShowChordTool();
        SaveViewPref("pianoChordTool", on ? "true" : "false");
    }

    /// <summary>
    /// The scale and the chord tool side by side on the bar under the roll (asked by the user, 2026-11-04: always at
    /// hand, the scale next to the chords), and what a click writes, shown only while the tool is on. Remembered in config.json.
    /// </summary>
    Control BuildPianoChordTool()
    {
        pianoRoll.ChordTool = AppConfig.Get("pianoChordTool") == "true";
        string saved = AppConfig.Get("pianoChordKind") ?? Chords.InScale;
        pianoRoll.ChordKind = Array.Exists(Chords.Kinds, k => k.Id == saved) || MidiStamps.Find(saved) != null ? saved : Chords.InScale;
        ToolTip.SetTip(pianoScaleButton, Loc.T("scale.pick.tip"));
        pianoScaleButton.Click += (_, _) => ShowScalePicker(pianoScaleButton, above: true);
        pianoChordButton.Content = IconText("", Loc.T("piano.chords"));
        ToolTip.SetTip(pianoChordButton, Shortcuts.Tip(Loc.T("piano.chords.tip"), "chordTool"));
        pianoChordButton.Click += (_, _) => SetChordTool(pianoChordButton.IsChecked == true);
        ToolTip.SetTip(pianoChordKindButton, Loc.T("piano.chords.kind.tip"));
        pianoChordKindButton.Click += (_, _) => ShowChordKindCard();
        ShowChordTool();
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { pianoScaleButton, pianoChordButton, pianoChordKindButton } };
    }

    /// <summary>The scale chip (lit while the song has a scale), the chord tool's state and what a click writes.</summary>
    void ShowChordTool()
    {
        bool on = virtualKeys.Scale != null;
        pianoScaleButton.Content = IconText("", (on ? ScaleLabel() : Loc.T("scale.off")) + "  ▾");
        pianoScaleButton.Classes.Set("on", on);
        pianoChordButton.IsChecked = pianoRoll.ChordTool;
        pianoChordKindButton.Content = IconText("", Loc.T($"chord.kind.{pianoRoll.ChordKind}") + "  ▾");
        pianoChordKindButton.IsVisible = pianoRoll.ChordTool;
    }

    /// <summary>
    /// What a click writes, as a card instead of a long menu (asked by the user, 2026-11-04: the list said nothing): the
    /// scale's own chords first, with what they follow; the fixed shapes, the runs and the drum figures, each in its grid;
    /// and at the foot the notes a click on the scale's root would write. It stays open, like the scale picker beside it.
    /// </summary>
    void ShowChordKindCard()
    {
        var marks = new List<Action>();
        var preview = new TextBlock { FontSize = 11, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap };
        var scaleHint = new TextBlock { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 2) };
        void Show()
        {
            foreach (var mark in marks) mark();
            var scale = virtualKeys.Scale;
            scaleHint.Text = scale != null ? Loc.T("piano.chords.follows", ScaleLabel()) : Loc.T("piano.chords.noScale");
            int root = 60 + (scale?.Root ?? 0);
            if (MidiStamps.Find(pianoRoll.ChordKind) is { } stamp)
                preview.Text = Loc.T("piano.chords.preview.stamp", MidiStamps.Notes(stamp, root, 0, scale, 100).Count);
            else
                preview.Text = Loc.T("piano.chords.preview", NoteNames.Class(root),
                    string.Join(" · ", Chords.Build(pianoRoll.ChordKind, root, scale).Select(p => NoteNames.Class(p))));
        }
        ToggleButton Choice(string id)
        {
            var b = new ToggleButton
            {
                // under its group's title the scale's own say only what they stack
                Classes = { "seg", "pick" }, Content = Loc.T(id == Chords.InScale ? "piano.chords.triad" : id == Chords.InScale7 ? "piano.chords.seventh" : $"chord.kind.{id}"), Height = ChordChoiceH, MinHeight = 0, MinWidth = 0, Padding = new Thickness(4, 0),
                FontSize = 11, Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(2),
            };
            marks.Add(() => b.IsChecked = pianoRoll.ChordKind == id);
            b.Click += (_, _) =>
            {
                pianoRoll.ChordKind = id;
                if (!pianoRoll.ChordTool) SetChordTool(true);
                ShowChordTool();
                SaveViewPref("pianoChordKind", id);
                Show();
            };
            return b;
        }
        static TextBlock Title(string text) => new() { Classes = { "label" }, Text = text.ToUpperInvariant(), Margin = new Thickness(6, 2, 0, 2) };
        Control Choices(IEnumerable<string> ids, int columns)
        {
            var g = new UniformGrid { Columns = columns };
            foreach (var id in ids) g.Children.Add(Choice(id));
            return g;
        }
        Control Group(string title, Control content, double top = 8) => new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4), Margin = new Thickness(0, top, 0, 0),
            Child = new StackPanel { Children = { Title(title), content } },
        };

        var fromScale = new StackPanel { Children = { Choices([Chords.InScale, Chords.InScale7], 2), scaleHint } };
        var shapes = Chords.Kinds.Where(k => k.Steps.Length > 0).Select(k => k.Id);
        var body = new StackPanel { Width = ChordCardW };
        body.Children.Add(Group(Loc.T("piano.chords.fromScale"), fromScale, 0));
        body.Children.Add(Group(Loc.T("piano.chords.shapes"), Choices(shapes, 5)));
        body.Children.Add(Group(Loc.T("piano.chords.stamps"), Choices(MidiStamps.All.Where(s => s.Group == MidiStamps.Scales).Select(s => s.Id), 4)));
        body.Children.Add(Group(Loc.T("piano.chords.drums"), Choices(MidiStamps.All.Where(s => s.Group == MidiStamps.Drums).Select(s => s.Id), 3)));
        body.Children.Add(new Border { Margin = new Thickness(6, 10, 6, 2), Child = preview });
        Show();
        var card = new Border
        {
            Padding = new Thickness(8), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        // the chip sits on the bar at the very bottom: the card opens upward
        new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.TopEdgeAlignedLeft }.ShowAt(pianoChordKindButton);
    }
}
