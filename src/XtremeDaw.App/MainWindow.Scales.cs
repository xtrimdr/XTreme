using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
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
    readonly ToggleButton pianoChordButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Button pianoChordKindButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The scale a root takes when none was on: the last one used.</summary>
    string lastScaleId = "major";

    static string ScaleName(string id) => Loc.T($"scale.kind.{id}");

    /// <summary>"Sol menor natural", or that there is none.</summary>
    string ScaleLabel() => virtualKeys.Scale is { } s
        ? Loc.T("scale.current", TunerView.NoteName(s.Root), ScaleName(s.Kind.Id).ToLowerInvariant())
        : Loc.T("scale.off");

    /// <summary>The project's scale reaches the piano roll and the keyboard, if it changed.</summary>
    void ApplyScale()
    {
        var s = store?.Project.Settings;
        var scale = s == null ? null : MusicScale.Find(s.ScaleRoot, s.Scale);
        if (scale?.Root == virtualKeys.Scale?.Root && scale?.Kind == virtualKeys.Scale?.Kind) return;
        pianoRoll.Scale = scale;
        virtualKeys.Scale = scale;
        pianoScaleButton.Content = Loc.T("scale.chip", ScaleLabel());
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
    void ShowScalePicker(Control anchor)
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
            var b = new ToggleButton { Classes = { "seg", "pick", "flat" }, Content = text, Height = ScaleRowH, MinHeight = 0, MinWidth = 0, Padding = new Thickness(padding, 0), FontSize = 11.5, Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch };
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
            var b = Choice(TunerView.NoteName(r), () => root == picked, () => { root = picked; id ??= lastScaleId; }, padding: 0);
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

        var body = new StackPanel { Spacing = 6, Width = ScalePickerW };
        body.Children.Add(Title(Loc.T("scale.pick.root")));
        body.Children.Add(roots);
        body.Children.Add(lists);
        var fit = new Button { Classes = { "chip" }, Content = Loc.T("scale.fit"), Height = ScaleRowH + 2, MinHeight = 0, Padding = new Thickness(8, 0), FontSize = 11.5, Focusable = false };
        ToolTip.SetTip(fit, Loc.T("scale.fit.tip"));
        marks.Add(() => fit.IsEnabled = id != null);
        fit.Click += (_, _) =>
        {
            int moved = pianoRoll.FitToScale();
            Status(Loc.T(moved < 0 ? "scale.fit.noClip" : "scale.fit.done", moved));
        };
        var bottom = new DockPanel { LastChildFill = false };
        var off = Choice(Loc.T("scale.off"), () => id == null, () => id = null);
        off.HorizontalAlignment = HorizontalAlignment.Left;
        off.Padding = new Thickness(8, 0);
        DockPanel.SetDock(fit, Dock.Right);
        bottom.Children.Add(fit);
        bottom.Children.Add(off);
        body.Children.Add(bottom);
        Show();
        var card = new Border
        {
            Padding = new Thickness(8), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.BottomEdgeAlignedLeft }.ShowAt(anchor);
    }

    /// <summary>The piano roll's scale and chords, in its header. The tool and its kind are remembered in config.json.</summary>
    Control BuildPianoScaleTools()
    {
        pianoScaleButton.Content = Loc.T("scale.chip", ScaleLabel());
        ToolTip.SetTip(pianoScaleButton, Loc.T("scale.pick.tip"));
        pianoScaleButton.Click += (_, _) => ShowScalePicker(pianoScaleButton);

        pianoRoll.ChordTool = AppConfig.Get("pianoChordTool") == "true";
        string saved = AppConfig.Get("pianoChordKind") ?? Chords.InScale;
        pianoRoll.ChordKind = Array.Exists(Chords.Kinds, k => k.Id == saved) ? saved : Chords.InScale;
        pianoChordButton.Content = Loc.T("piano.chords");
        ToolTip.SetTip(pianoChordButton, Shortcuts.Tip(Loc.T("piano.chords.tip"), "chordTool"));
        pianoChordButton.Click += (_, _) => SetChordTool(pianoChordButton.IsChecked == true);
        ToolTip.SetTip(pianoChordKindButton, Loc.T("piano.chords.kind.tip"));
        pianoChordKindButton.Click += (_, _) => ShowChordKindMenu();
        ShowChordTool();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { pianoScaleButton, pianoChordButton, pianoChordKindButton },
        };
    }

    void SetChordTool(bool on)
    {
        pianoRoll.ChordTool = on;
        ShowChordTool();
        SaveViewPref("pianoChordTool", on ? "true" : "false");
    }

    void ShowChordTool()
    {
        pianoChordButton.IsChecked = pianoRoll.ChordTool;
        pianoChordKindButton.IsVisible = pianoRoll.ChordTool;
        pianoChordKindButton.Content = Loc.T($"chord.kind.{pianoRoll.ChordKind}") + "  ▾";
    }

    void ShowChordKindMenu()
    {
        var refresh = new List<Action>();
        var list = new List<object>();
        foreach (var kind in Chords.Kinds)
        {
            list.Add(MenuCheck(Loc.T($"chord.kind.{kind.Id}"), () => pianoRoll.ChordKind == kind.Id, () =>
            {
                pianoRoll.ChordKind = kind.Id;
                ShowChordTool();
                SaveViewPref("pianoChordKind", kind.Id);
            }, refresh));
            if (kind.Id == Chords.InScale7) list.Add(new Separator());
        }
        var menu = Popup(list, 220);
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        OpenAnchoredMenu(menu, pianoChordKindButton);
    }
}
