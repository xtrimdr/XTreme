using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The piano roll's tools (asked by the user: all of FL Studio's, elegant and comfortable). "Herramientas" opens one card with
/// everything in sight, and a tool with settings opens a panel beside the notes, built from its row in <see cref="MidiTools"/>: the
/// result is on the roll and in the speakers as the settings move, Enter keeps it and Esc takes it back, and it is one
/// undo step. The panel never covers the notes and touching them keeps what it shows. A double click on a note opens the
/// same panel with that note's card. "Vista" holds what is only looked at: the lane, other tracks in grey, a wave behind.
/// </summary>
public partial class MainWindow
{
    /// <summary>The panel's width; the card's rows, as high as the scale picker's.</summary>
    const double ToolPanelW = 268, ToolCardW = 640;
    /// <summary>How often the chord under the fingers is read.</summary>
    const int ChordReadMs = 100;

    readonly Border pianoToolPanel = new()
    {
        Width = ToolPanelW, IsVisible = false, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1, 0, 0, 0),
    };
    readonly Button pianoToolsButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Button pianoViewButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The chord under the pointer: a fixed width, so a longer name doesn't push the tools beside it.</summary>
    readonly TextBlock pianoChordLabel = new() { FontSize = 12, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center, Classes = { "mono" },
        Width = ChordLabelW, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Room for a long chord name in mono 12 ("Do#m7(b9)/Sol#").</summary>
    const double ChordLabelW = 110;
    readonly List<ToggleButton> pianoWriteButtons = [];

    /// <summary>The tool on the panel with its settings, and the clip's notes as they were (the project holds the preview meanwhile).</summary>
    MidiTool? openTool;
    Dictionary<string, int> toolValues = [];
    readonly Dictionary<string, string> toolWords = [];
    List<MidiNote>? toolOriginal;
    string? toolClipId;
    TextBlock? toolTarget;
    Flyout? toolCard;

    /// <summary>Tracks shown in grey on the roll, and the one whose wave goes behind it.</summary>
    readonly HashSet<string> shadowTracks = [];
    string? backWaveTrack;

    void InitPianoTools()
    {
        pianoRoll.NotesPreviewed += PreviewNotes;
        pianoRoll.ToolInterrupted += () => CloseTool(accept: true);
        pianoRoll.ToolKeyPressed += PianoKey;
        pianoRoll.NoteDetailsRequested += OpenNoteCard;
        pianoRoll.KeyMenuRequested += ShowKeyMenu;
        var chords = new DispatcherTimer(TimeSpan.FromMilliseconds(ChordReadMs), DispatcherPriority.Background, (_, _) => ReadChord());
        chords.Stop();
        FollowChordTimer(chords);
    }

    Control BuildPianoToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        pianoToolsButton.Content = IconText("", Loc.T("tool.menu") + "  ▾");
        ToolTip.SetTip(pianoToolsButton, Loc.T("tool.menu.tip"));
        pianoToolsButton.Click += (_, _) => ShowToolCard();
        row.Children.Add(pianoToolsButton);

        var writes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (tool, key, glyph) in new[] { (PianoTool.Draw, "draw", ""), (PianoTool.Paint, "paint", ""), (PianoTool.Slice, "slice", "") })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = IconText(glyph, Loc.T($"sc.piano.{key}")), FontSize = 12, IsChecked = tool == PianoTool.Draw };
            ToolTip.SetTip(b, Shortcuts.Tip(Loc.T($"sc.piano.{key}.tip"), Shortcuts.PianoPrefix + key));
            b.Click += (_, _) => SetWriteTool(tool);
            pianoWriteButtons.Add(b);
            writes.Children.Add(b);
        }
        row.Children.Add(new Border { Classes = { "seg" }, Child = writes, VerticalAlignment = VerticalAlignment.Center });

        pianoViewButton.Content = IconText("", Loc.T("tool.view") + "  ▾");
        ToolTip.SetTip(pianoViewButton, Loc.T("tool.view.tip"));
        pianoViewButton.Click += (_, _) => ShowPianoViewMenu();
        return row;
    }

    void SetWriteTool(PianoTool tool)
    {
        pianoRoll.WriteTool = tool;
        for (int i = 0; i < pianoWriteButtons.Count; i++) pianoWriteButtons[i].IsChecked = i == (int)tool;
    }

    /// <summary>A key from the roll: the tools by id, the rest by name.</summary>
    void PianoKey(string id)
    {
        switch (id)
        {
            case "tool-accept": CloseTool(accept: true); break;
            case "tool-cancel": CloseTool(accept: false); break;
            case "draw": SetWriteTool(PianoTool.Draw); break;
            case "paint": SetWriteTool(PianoTool.Paint); break;
            case "slice": SetWriteTool(PianoTool.Slice); break;
            case "mute": pianoRoll.ToggleMute(); break;
            case "group": pianoRoll.GroupNotes(); break;
            case "ungroup": pianoRoll.UngroupNotes(); break;
            case "invert": pianoRoll.Select("invert"); break;
            default: OpenTool(id); break;
        }
    }

    // --- the card with everything ------------------------------------------------------------------

    /// <summary>A row of the card.</summary>
    Button ToolRow(string text, string? tip, Action click, bool enabled = true)
    {
        var b = new Button
        {
            Classes = { "seg", "flat" }, Content = text, Height = 22, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 11, Focusable = false, IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            toolCard?.Hide();
            click();
        };
        return b;
    }

    static string ToolTipOf(string id)
    {
        string tip = Loc.T($"tool.{id}.tip"), key = Shortcuts.PianoPrefix + id;
        return Shortcuts.All.Any(a => a.Id == key) ? Shortcuts.Tip(tip, key) : tip;
    }

    Button ToolEntry(string id) => ToolRow(Loc.T($"tool.{id}"), ToolTipOf(id), () => OpenTool(id));

    static TextBlock CardTitle(string text, double top = 0) => new() { Classes = { "label" }, Text = text.ToUpperInvariant(), Margin = new Thickness(6, top, 0, 2) };

    /// <summary>Everything on one card, like the scale picker: what writes, time and levels, editing and selecting, the notes and files.</summary>
    void ShowToolCard()
    {
        bool open = pianoClipId != null;
        var writes = MidiTools.All.Where(t => t.Group == MidiTools.Write).ToList();
        var first = new StackPanel();
        first.Children.Add(CardTitle(Loc.T("tool.group.write")));
        foreach (var t in writes.Take(writes.Count / 2)) first.Children.Add(ToolEntry(t.Id));
        var second = new StackPanel();
        second.Children.Add(CardTitle(" "));
        foreach (var t in writes.Skip(writes.Count / 2)) second.Children.Add(ToolEntry(t.Id));

        var third = new StackPanel();
        third.Children.Add(CardTitle(Loc.T("tool.group.timing")));
        foreach (var t in MidiTools.All.Where(t => t.Group == MidiTools.Timing)) third.Children.Add(ToolEntry(t.Id));
        third.Children.Add(CardTitle(Loc.T("tool.group.level"), 6));
        foreach (var t in MidiTools.All.Where(t => t.Group == MidiTools.Level)) third.Children.Add(ToolEntry(t.Id));
        third.Children.Add(CardTitle(Loc.T("tool.group.edit"), 6));
        foreach (var t in MidiTools.All.Where(t => t.Group == MidiTools.Edit)) third.Children.Add(ToolEntry(t.Id));

        var fourth = new StackPanel();
        fourth.Children.Add(CardTitle(Loc.T("tool.group.select")));
        foreach (string c in MidiSelect.Commands) fourth.Children.Add(ToolRow(Loc.T($"tool.select.{c}"), null, () => pianoRoll.Select(c)));
        fourth.Children.Add(CardTitle(Loc.T("tool.group.notes"), 6));
        fourth.Children.Add(ToolRow(Loc.T("tool.mute"), Shortcuts.Tip(Loc.T("tool.mute.tip"), Shortcuts.PianoPrefix + "mute"), pianoRoll.ToggleMute));
        fourth.Children.Add(ToolRow(Loc.T("tool.group"), Shortcuts.Tip(Loc.T("tool.group.tip"), Shortcuts.PianoPrefix + "group"), pianoRoll.GroupNotes));
        fourth.Children.Add(ToolRow(Loc.T("tool.ungroup"), null, pianoRoll.UngroupNotes));
        fourth.Children.Add(ToolRow(Loc.T("tool.note"), Loc.T("tool.note.tip"), () => { if (pianoRoll.SelectedNotes.FirstOrDefault() is { } n) OpenNoteCard(n); else Toast(Loc.T("tool.note.none")); }));

        var fifth = new StackPanel();
        fifth.Children.Add(CardTitle(Loc.T("tool.group.files")));
        fifth.Children.Add(ToolRow(Loc.T("tool.import"), Loc.T("tool.import.tip"), () => _ = ImportMidiFile()));
        fifth.Children.Add(ToolRow(Loc.T("tool.export"), Loc.T("tool.export.tip"), () => _ = ExportMidiFile(), open));
        fifth.Children.Add(ToolRow(Loc.T("tool.sheet"), Loc.T("tool.sheet.tip"), () => _ = ExportSheet(), open));
        fifth.Children.Add(CardTitle(Loc.T("tool.group.live"), 6));
        fifth.Children.Add(ToolRow(Loc.T("tool.liveArp"), Loc.T("tool.liveArp.tip"), OpenLiveArp));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 4, Width = ToolCardW };
        Control[] columns = [first, second, third, fourth, fifth];
        for (int i = 0; i < columns.Length; i++)
        {
            Grid.SetColumn(columns[i], i);
            grid.Children.Add(columns[i]);
        }
        var card = new Border
        {
            Padding = new Thickness(8), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = grid,
        };
        toolCard = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.BottomEdgeAlignedLeft };
        toolCard.ShowAt(pianoToolsButton);
    }

    // --- the panel ---------------------------------------------------------------------------------

    /// <summary>Opens a tool on the clip: a tool with no settings just runs; with a tool already open, that one is kept first.</summary>
    void OpenTool(string id, Dictionary<string, int>? start = null)
    {
        if (MidiTools.Find(id) is not { } tool || store == null || store.ReadOnly) return;
        CloseTool(accept: true);
        if (pianoClipId == null) { Toast(Loc.T("scale.fit.noClip")); return; }
        if (tool.Instant)
        {
            pianoRoll.RunTool(tool);
            return;
        }
        if (!pianoRoll.BeginTool()) return;
        toolClipId = pianoClipId;
        toolOriginal = [.. MidiEditing.Find(store.Project, pianoClipId).Clip.Notes.Select(n => n.Copy())];
        openTool = tool;
        toolValues = start ?? LoadToolValues(tool);
        toolWords.Clear();
        BuildToolPanel(tool);
        PreviewTool();
    }

    static Dictionary<string, int> LoadToolValues(MidiTool tool)
    {
        var values = MidiTools.Defaults(tool);
        foreach (string pair in (AppConfig.Get("pianoTool." + tool.Id) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], out int v) || !values.ContainsKey(kv[0])) continue;
            var p = tool.Params.First(x => x.Id == kv[0]);
            values[kv[0]] = Math.Clamp(v, p.Min, p.Max);
        }
        return values;
    }

    void PreviewTool()
    {
        if (openTool == null) return;
        int n = pianoRoll.PreviewTool(openTool, toolValues, toolWords);
        if (toolTarget != null) toolTarget.Text = Loc.T("piano.vel.target", n);
    }

    /// <summary>The notes of a preview go where the engine reads them: heard at once, out of the undo history.</summary>
    void PreviewNotes(string clipId, List<MidiNote> notes)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
        MidiEditing.SetNotes(store.Project, clipId, notes);
        double bpm = store.Project.Settings.Bpm;
        foreach (var t in tracks)
            if (t.Track.IsMidi) t.SetMidi(bpm);
        Timeline.InvalidateVisual();
    }

    /// <summary>Closes the panel keeping what it shows, or going back. Safe with nothing open.</summary>
    void CloseTool(bool accept)
    {
        if (openTool is { } tool)
        {
            if (tool.Group != MidiTools.NoteCard)
                SaveViewPref("pianoTool." + tool.Id, string.Join(",", toolValues.Select(kv => $"{kv.Key}={kv.Value}")));
            openTool = null;
            if (accept) pianoRoll.AcceptTool();
            else pianoRoll.CancelTool();
            toolOriginal = null;
        }
        pianoToolPanel.IsVisible = false;
        pianoToolPanel.Child = null;
        toolTarget = null;
        pianoRoll.Focus();
    }

    /// <summary>What a preview changed goes back before the edit that keeps it, or undoing it would land on the preview.</summary>
    void RestoreBeforeCommit(string clipId)
    {
        if (toolOriginal == null || toolClipId != clipId || store == null) return;
        MidiEditing.SetNotes(store.Project, clipId, toolOriginal);
        toolOriginal = null;
    }

    static string ValueText(ToolParam p, int v) => p.Kind switch
    {
        ToolParamKind.Percent => $"{v} %",
        ToolParamKind.Signed => v > 0 ? $"+{v}" : v.ToString(),
        ToolParamKind.Ms => $"{v} ms",
        ToolParamKind.Key => NoteNames.Of(v),
        _ => v.ToString(),
    };

    static string ChoiceText(string choice) => choice.Contains('/') ? choice : Loc.Or($"tool.c.{choice}", choice);

    /// <summary>One setting: a bar for a value, a segmented control or a list for a choice, a switch, dice, a text box or the note colors.</summary>
    Control ParamRow(string title, ToolParam p, Dictionary<string, int> values, Action changed)
    {
        var block = new StackPanel { Spacing = 3 };
        void Label() => block.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = Palette.Dim });
        switch (p.Kind)
        {
            case ToolParamKind.Toggle:
            {
                var b = new ToggleButton { Classes = { "chip" }, Content = title, IsChecked = values[p.Id] != 0, Height = 26, MinHeight = 26, Padding = new Thickness(10, 0), FontSize = 12, Focusable = false };
                b.Click += (_, _) => { values[p.Id] = b.IsChecked == true ? 1 : 0; changed(); };
                return b;
            }
            case ToolParamKind.Seed:
            {
                var b = new Button { Classes = { "chip" }, Content = title, Height = 26, MinHeight = 26, Padding = new Thickness(10, 0), FontSize = 12, Focusable = false };
                ToolTip.SetTip(b, Loc.T("tool.p.seed.tip"));
                b.Click += (_, _) => { values[p.Id] = Random.Shared.Next(1, int.MaxValue); changed(); };
                return b;
            }
            case ToolParamKind.Text:
            {
                Label();
                var box = new TextBox { Classes = { "field" }, FontSize = 12, Watermark = Loc.T($"tool.p.{p.Id}.hint") };
                box.TextChanged += (_, _) => { toolWords[p.Id] = box.Text ?? ""; changed(); };
                block.Children.Add(box);
                return block;
            }
            case ToolParamKind.Color:
            {
                Label();
                var swatches = new WrapPanel();
                for (int c = 0; c <= MidiNote.Colors; c++)
                {
                    int color = c;
                    var b = new Button
                    {
                        Width = 20, Height = 20, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3), Focusable = false, Classes = { "lit" },
                        Background = c == 0 ? Palette.Raised : new SolidColorBrush(PianoRollView.GroupColor(c)), BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
                    };
                    b.Click += (_, _) => { values[p.Id] = color; changed(); };
                    swatches.Children.Add(b);
                }
                block.Children.Add(swatches);
                return block;
            }
            case ToolParamKind.Choice:
            {
                Label();
                string[] choices = p.Choices!;
                if (choices.Length <= 3)
                {
                    var seg = new StackPanel { Orientation = Orientation.Horizontal };
                    var buttons = new List<ToggleButton>();
                    for (int i = 0; i < choices.Length; i++)
                    {
                        int index = i;
                        var b = new ToggleButton { Classes = { "seg" }, Content = ChoiceText(choices[i]), FontSize = 12, IsChecked = values[p.Id] == i, Focusable = false };
                        b.Click += (_, _) =>
                        {
                            values[p.Id] = index;
                            foreach (var o in buttons) o.IsChecked = o == b;
                            changed();
                        };
                        buttons.Add(b);
                        seg.Children.Add(b);
                    }
                    block.Children.Add(new Border { Classes = { "seg" }, Child = seg, HorizontalAlignment = HorizontalAlignment.Left });
                    return block;
                }
                var chip = new Button { Classes = { "chip" }, Height = 26, MinHeight = 26, Padding = new Thickness(10, 0), FontSize = 12, Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                void Show() => chip.Content = ChoiceText(choices[values[p.Id]]) + "  ▾";
                Show();
                chip.Click += (_, _) =>
                {
                    var refresh = new List<Action>();
                    var list = new List<object>();
                    for (int i = 0; i < choices.Length; i++)
                    {
                        int index = i;
                        list.Add(MenuCheck(ChoiceText(choices[i]), () => values[p.Id] == index, () => { values[p.Id] = index; Show(); changed(); }, refresh));
                    }
                    var menu = Popup(list, ToolPanelW - 28);
                    menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                    OpenAnchoredMenu(menu, chip);
                };
                block.Children.Add(chip);
                return block;
            }
            default:
            {
                Label();
                var bar = new ValueBar(p.Min, p.Max, values[p.Id], v => ValueText(p, v)) { Height = 26, Default = p.Default };
                bar.Moved += v => { values[p.Id] = v; changed(); };
                block.Children.Add(bar);
                return block;
            }
        }
    }

    Control PanelShell(string title, string? tip, Control body, Control foot)
    {
        var head = new DockPanel { Margin = new Thickness(12, 10, 8, 6) };
        var close = new Button
        {
            Classes = { "act" }, Width = 24, Height = 24, MinHeight = 0, Padding = new Thickness(0), Focusable = false,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11 },
        };
        ToolTip.SetTip(close, Loc.T("common.cancel"));
        close.Click += (_, _) => CloseTool(accept: false);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 12, 12) };
        if (tip != null) stack.Children.Add(new TextBlock { Text = tip, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(body);
        var shell = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        shell.Children.Add(head);
        shell.Children.Add(foot);
        shell.Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return shell;
    }

    void BuildToolPanel(MidiTool tool)
    {
        var body = new StackPanel { Spacing = 8 };
        var switches = new WrapPanel();
        foreach (var p in tool.Params)
        {
            var row = ParamRow(Loc.T($"tool.p.{p.Id}"), p, toolValues, PreviewTool);
            if (p.Kind is ToolParamKind.Toggle or ToolParamKind.Seed)
            {
                row.Margin = new Thickness(0, 0, 4, 4);
                switches.Children.Add(row);
            }
            else body.Children.Add(row);
        }
        if (switches.Children.Count > 0) body.Children.Add(switches);

        toolTarget = new TextBlock { FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
        var accept = new Button { Classes = { "btn", "sm", "primary" }, Content = Loc.T("tool.accept"), Focusable = false };
        ToolTip.SetTip(accept, Loc.T("tool.accept.tip"));
        accept.Click += (_, _) => CloseTool(accept: true);
        var reset = new Button { Classes = { "btn", "sm" }, Content = Loc.T("tool.reset"), Focusable = false };
        reset.Click += (_, _) =>
        {
            toolValues = MidiTools.Defaults(tool);
            BuildToolPanel(tool);
            PreviewTool();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(reset);
        buttons.Children.Add(accept);
        var foot = new DockPanel { Margin = new Thickness(12, 6, 12, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(toolTarget);
        pianoToolPanel.Child = PanelShell(Loc.T($"tool.{tool.Id}"), Loc.T($"tool.{tool.Id}.tip"), body, foot);
        pianoToolPanel.IsVisible = true;
    }

    /// <summary>A note's card: the panel with what the note has, for the selected notes (it joins the selection if it wasn't in it).</summary>
    void OpenNoteCard(MidiNote n)
    {
        var values = new Dictionary<string, int>
        {
            ["velocity"] = n.Velocity, ["pan"] = n.Pan, ["release"] = n.Release, ["modX"] = n.ModX, ["modY"] = n.ModY, ["color"] = n.Color,
            ["slide"] = n.Slide ? 1 : 0, ["porta"] = n.Porta ? 1 : 0, ["muted"] = n.Muted ? 1 : 0,
        };
        foreach (var (k, v) in values.ToList()) values["was." + k] = v;
        OpenTool("note", values);
    }

    // --- the live arpeggiator ------------------------------------------------------------------------

    static readonly string[] ArpSteps = ["1/4", "1/8", "1/8T", "1/16", "1/16T", "1/32"];

    /// <summary>The panel, for the track the keyboard plays: it changes the track, with nothing to accept.</summary>
    void OpenLiveArp()
    {
        var t = PianoTrack() ?? sel;
        if (t == null || !t.Track.IsMidi) { Toast(Loc.T("tool.liveArp.none")); return; }
        CloseTool(accept: true);
        var arp = t.Track.Arp ?? new ArpSettings { On = false };
        var values = new Dictionary<string, int>
        {
            ["on"] = arp.On ? 1 : 0, ["pattern"] = Math.Max(0, Array.IndexOf(LiveArp.Patterns, arp.Pattern)),
            ["rate"] = Math.Max(0, Array.FindIndex(ArpSteps, s => Math.Abs(MidiTools.Beats(s) - arp.Step) < 1e-6)),
            ["octaves"] = arp.Octaves, ["gate"] = (int)Math.Round(arp.Gate * 100),
        };
        void Apply()
        {
            arp.On = values["on"] != 0;
            arp.Pattern = LiveArp.Patterns[values["pattern"]];
            arp.Step = MidiTools.Beats(ArpSteps[values["rate"]]);
            arp.Octaves = values["octaves"];
            arp.Gate = values["gate"] / 100.0;
            t.Track.Arp = arp;
            t.SetMidi(store?.Project.Settings.Bpm ?? 120);
            MarkDirty();
        }
        ToolParam[] rows =
        [
            ToolParam.Switch("on", arp.On), ToolParam.Pick("pattern", 0, LiveArp.Patterns), ToolParam.Pick("rate", 3, ArpSteps),
            new("octaves", ToolParamKind.Count, 1, 4, 1), new("gate", ToolParamKind.Percent, 10, 100, 80),
        ];
        var body = new StackPanel { Spacing = 8 };
        foreach (var p in rows) body.Children.Add(ParamRow(Loc.T($"tool.p.{p.Id}"), p, values, Apply));
        pianoToolPanel.Child = PanelShell(Loc.T("tool.liveArp"), Loc.T("tool.liveArp.tip"), body, new Border { Height = 8 });
        pianoToolPanel.IsVisible = true;
    }

    // --- what the roll shows ---------------------------------------------------------------------------

    void ShowPianoViewMenu()
    {
        if (store == null) return;
        var refresh = new List<Action>();
        var list = new List<object> { MenuGroup(Loc.T("tool.view.lane")) };
        foreach (var lane in Enum.GetValues<NoteLane>())
            list.Add(MenuCheck(Loc.T($"tool.c.lane.{lane.ToString().ToLowerInvariant()}"), () => pianoRoll.Lane == lane, () => pianoRoll.Lane = lane, refresh));
        var others = tracks.Where(t => t.Track.IsMidi && t != PianoTrack()).ToList();
        list.Add(new Separator());
        list.Add(MenuGroup(Loc.T("tool.view.shadows")));
        if (others.Count == 0) list.Add(new MenuItem { Header = Loc.T("tool.view.noTracks"), IsEnabled = false, FontSize = 11 });
        foreach (var t in others)
            list.Add(MenuCheck(t.Track.Name, () => shadowTracks.Contains(t.Track.Id), () =>
            {
                if (!shadowTracks.Remove(t.Track.Id)) shadowTracks.Add(t.Track.Id);
                ShowRollExtras();
            }, refresh));
        var audio = tracks.Where(t => !t.Track.IsMidi && t.Clips.Count > 0).ToList();
        list.Add(new Separator());
        list.Add(MenuGroup(Loc.T("tool.view.wave")));
        list.Add(MenuCheck(Loc.T("tool.view.noWave"), () => backWaveTrack == null, () => { backWaveTrack = null; ShowRollExtras(); }, refresh));
        foreach (var t in audio)
            list.Add(MenuCheck(t.Track.Name, () => backWaveTrack == t.Track.Id, () => { backWaveTrack = t.Track.Id; ShowRollExtras(); }, refresh));
        var menu = Popup(list, 240);
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        OpenAnchoredMenu(menu, pianoViewButton);
    }

    /// <summary>The grey notes, the wave and the key names of what is on the roll now; on every refresh, so an edit shows.</summary>
    void ShowRollExtras()
    {
        var t = PianoTrack();
        pianoRoll.KeyNames = t?.Track.KeyNames;
        pianoRoll.SetShadows(tracks.Where(x => x != t && shadowTracks.Contains(x.Track.Id)).SelectMany(x => x.Track.MidiClips ?? []));
        var wave = tracks.FirstOrDefault(x => x.Track.Id == backWaveTrack);
        pianoRoll.SetBackWave(wave == null ? [] : [.. wave.Clips.Where(c => c.Audio != null).Select(c => new BackWave(c.Clip.AtSec + c.Clip.TrimStartSec, c.Clip.TrimStartSec,
            c.Audio!.LengthSec - c.Clip.TrimStartSec - c.Clip.TrimEndSec, c.Audio.PeakMin, c.Audio.PeakMax, c.Audio.SampleRate / (double)MediaAudio.PeakBin))]);
    }

    void ShowKeyMenu(int pitch)
    {
        if (PianoTrack() is not { } t || store == null || store.ReadOnly) return;
        string id = t.Track.Id;
        string now = t.Track.KeyNames?.GetValueOrDefault(pitch) ?? "";
        AskName(pianoRoll, null, Loc.T("tool.keyName", NoteNames.Of(pitch)), now, Loc.T("common.ok"), name =>
            _ = EditTimeline(p =>
            {
                var track = p.Tracks.Find(x => x.Id == id);
                if (track == null) return id;
                track.KeyNames ??= [];
                if (string.IsNullOrWhiteSpace(name)) track.KeyNames.Remove(pitch);
                else track.KeyNames[pitch] = name.Trim();
                if (track.KeyNames.Count == 0) track.KeyNames = null;
                return id;
            }, "midi-key-name"));
    }

    readonly LiveNote[] chordHeld = new LiveNote[16];
    /// <summary>The keys sounding, lowest first, reused on every read; the last set read and the name it gave, so the
    /// same keys held are not read (and their name built) again on every tick.</summary>
    readonly List<int> chordKeys = new(16);
    readonly List<int> chordLastKeys = new(16);
    string chordLastName = "";

    /// <summary>Reads the chord only while the piano roll is on screen: started and stopped as its host shows and hides.</summary>
    void FollowChordTimer(DispatcherTimer timer)
    {
        if (pianoHost.IsVisible) timer.Start();
        pianoHost.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (pianoHost.IsVisible) { timer.Start(); ReadChord(); }
            else timer.Stop();
        };
    }

    /// <summary>The chord being played or, with no keys down, the one selected. Allocates nothing while the keys stay the same.</summary>
    void ReadChord()
    {
        if (!PianoShown) return;
        string name = "";
        int count = (ChordTrack() ?? sel)?.Keys.Sounding(chordHeld) ?? 0;
        if (count >= 2)
        {
            chordKeys.Clear();
            for (int i = 0; i < count; i++) chordKeys.Add(chordHeld[i].Sounds);
            chordKeys.Sort();
            if (!SameKeys(chordKeys, chordLastKeys))
            {
                chordLastKeys.Clear();
                chordLastKeys.AddRange(chordKeys);
                chordLastName = Chords.Read(chordKeys) is { } read ? PianoRollView.ChordLabel(read.Root[0], read.Kind) : "";
            }
            name = chordLastName;
        }
        if (name.Length == 0) name = pianoRoll.SelectedChord;
        if (pianoChordLabel.Text != name) pianoChordLabel.Text = name;
    }

    static bool SameKeys(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>The track of the clip on the roll, as <see cref="PianoTrack"/>, without LINQ: it runs on every chord read.</summary>
    LiveTrack? ChordTrack()
    {
        if (store == null || pianoClipId == null) return null;
        foreach (var t in store.Project.Tracks)
        {
            if (t.MidiClips == null) continue;
            foreach (var c in t.MidiClips)
            {
                if (c.Id != pianoClipId) continue;
                foreach (var live in tracks)
                    if (live.Track.Id == t.Id) return live;
                return null;
            }
        }
        return null;
    }

    // --- files -----------------------------------------------------------------------------------------

    static readonly FilePickerFileType MidiType = new("MIDI") { Patterns = ["*.mid", "*.midi"] };
    static readonly FilePickerFileType PdfType = new("PDF") { Patterns = ["*.pdf"] };

    string PianoFileName() => store != null && MidiEditing.TryFind(store.Project, pianoClipId) is { } c
        ? c.PatternName ?? MidiEditing.Find(store.Project, c.Id).Track.Name : "midi";

    /// <summary>What a file gets: the notes selected or, with none, the clip as it sounds, a pattern's repeats written out.</summary>
    List<MidiNote> NotesToExport() => pianoRoll.SelectedNotes.Count > 1 || store == null || MidiEditing.TryFind(store.Project, pianoClipId) is not { } clip
        ? pianoRoll.NotesForExport() : MidiFiles.Heard(clip);

    /// <summary>A file into the selected MIDI track, or to new tracks, at the playhead.</summary>
    async Task ImportMidiFile()
    {
        if (!CanImport()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("tool.import"), AllowMultiple = false, FileTypeFilter = [MidiType, FilePickerFileTypes.All] });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        await ImportMidi(path, engine.PositionSec, sel?.Track.IsMidi == true ? sel.Track.Id : null);
    }

    async Task ImportMidi(string path, double atSec, string? trackId)
    {
        try
        {
            var data = await Task.Run(() => MidiFiles.Read(path));
            List<string> clips = [];
            await EditTimeline(p =>
            {
                clips = MidiFiles.Import(p, data, trackId, atSec);
                return clips.Count > 0 ? MidiEditing.Find(p, clips[0]).Track.Id : trackId;
            }, "midi-imported");
            if (clips.Count > 0) OpenInPiano(clips[0]);
            Status(Loc.T("tool.import.done", clips.Count));
        }
        catch (ArgumentException e) { Toast(e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast(Loc.T("tool.file.readFailed", Path.GetFileName(path))); }
    }

    async Task ExportMidiFile()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = Loc.T("tool.export"), SuggestedFileName = PianoFileName() + ".mid", DefaultExtension = "mid", FileTypeChoices = [MidiType] });
        if (file?.TryGetLocalPath() is not { } path || store == null) return;
        var notes = NotesToExport();
        string name = PianoFileName();
        double bpm = store.Project.Settings.Bpm;
        try
        {
            await Task.Run(() => MidiFiles.Write(path, [(name, notes)], bpm));
            Toast(Loc.T("tool.export.done", Path.GetFileName(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast(Loc.T("tool.file.writeFailed", Path.GetFileName(path))); }
    }

    async Task ExportSheet()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = Loc.T("tool.sheet"), SuggestedFileName = PianoFileName() + ".pdf", DefaultExtension = "pdf", FileTypeChoices = [PdfType] });
        if (file?.TryGetLocalPath() is not { } path || store == null) return;
        var notes = NotesToExport();
        string name = PianoFileName();
        double beats = MidiEditing.TryFind(store.Project, pianoClipId)?.LengthBeats ?? pianoRoll.ClipBeats;
        var scale = virtualKeys.Scale;
        try
        {
            await Task.Run(() => ScoreSheet.Write(path, name, notes, beats, scale));
            Toast(Loc.T("tool.export.done", Path.GetFileName(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast(Loc.T("tool.file.writeFailed", Path.GetFileName(path))); }
    }
}
