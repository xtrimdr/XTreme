using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The Patrones tab of the project bar (asked by the user, 2026-11-01): the project's MIDI patterns, each with a small
/// picture of its notes. Carrying one to a MIDI track adds a linked clip where it lands; from its row it can be renamed,
/// opened in the piano roll or deleted, and deleting asks in the row what happens to its clips, as a pattern is nothing
/// but those clips (<see cref="MidiPatterns"/>). Built while the tab is on screen, alongside <see cref="RefreshHistory"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>A pattern carried from its tab: its id.</summary>
    static readonly DataFormat<string> PatternDrag = DataFormat.CreateStringApplicationFormat("xtreme-pattern");

    /// <summary>How far a row is dragged before it is carried, like a sample.</summary>
    const double PatternDragStart = 6;

    readonly StackPanel patternsPanel = new() { Margin = new Thickness(0, 8) };
    string? patternDeleting;

    bool PatternsVisible => projectTab == "patterns" && projectBarExpanded.IsVisible;

    /// <summary>Rebuilds the list (only while the tab is on screen; showing it refreshes it).</summary>
    void RefreshPatterns()
    {
        if (!PatternsVisible) return;
        patternsPanel.Children.Clear();
        var st = store;
        var patterns = st == null ? [] : MidiPatterns.All(st.Project);
        if (patterns.All(x => x.Id != patternDeleting)) patternDeleting = null;
        if (st == null || patterns.Count == 0)
        {
            patternsPanel.Children.Add(PatternHelp(Loc.T("ptn.empty")));
            return;
        }
        bool editable = !st.ReadOnly && !restoring && !RecordingTake;
        foreach (var x in patterns) patternsPanel.Children.Add(x.Id == patternDeleting ? PatternDeleteRow(x) : PatternRow(st, x, editable));
        patternsPanel.Children.Add(PatternHelp(Loc.T("ptn.help")));
    }

    static TextBlock PatternHelp(string text)
    {
        var help = Help(text);
        help.Margin = new Thickness(16, 8);
        return help;
    }

    static string PatternClipsLabel(int n) => n == 1 ? Loc.T("ptn.clips.one") : Loc.T("ptn.clips.many", n);

    Control PatternRow(ProjectStore st, MidiPatterns.Info x, bool editable)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 4, RowSpacing = 4 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = x.Name, FontSize = 13, Foreground = Palette.Fg, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis,
            Text = x.Parts > 1 ? $"{BarsLabel(x.Beats / 4)} · {PartsLabel(x.Parts)} · {PatternClipsLabel(x.Clips)}" : $"{BarsLabel(x.Beats / 4)} · {PatternClipsLabel(x.Clips)}",
        });
        grid.Children.Add(text);

        Button rename = null!;
        rename = RowButton("", Loc.T("midi.pattern.rename"), () => RenamePattern(x, rename));
        rename.IsEnabled = editable;
        Grid.SetColumn(rename, 1); grid.Children.Add(rename);
        var edit = RowButton("", Loc.T("ptn.open"), () => OpenPattern(x.Id));
        Grid.SetColumn(edit, 2); grid.Children.Add(edit);
        var delete = RowButton("", Loc.T("ptn.delete"), () => { patternDeleting = x.Id; RefreshPatterns(); }, danger: true);
        delete.IsEnabled = editable;
        Grid.SetColumn(delete, 3); grid.Children.Add(delete);

        var preview = new PatternPreview();
        preview.Set([.. MidiPatterns.Parts(st.Project, x.Id).Select(part => (part.Clip.Notes, TrackIcons.ColorOf(part.Track)))], x.Beats);
        Grid.SetRow(preview, 1); Grid.SetColumnSpan(preview, 4);
        grid.Children.Add(preview);

        var row = HistoryRow(grid, [rename, edit, delete], new Thickness(16, 6, 8, 8));
        Point? pressed = null;
        row.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(row);
            if (!point.Properties.IsLeftButtonPressed) return;
            pressed = e.ClickCount == 1 ? point.Position : null;
            if (e.ClickCount == 1) PickPencilPattern(x.Id);
            if (e.ClickCount == 2) OpenPattern(x.Id);
        };
        row.PointerMoved += (_, e) =>
        {
            if (pressed is not { } at || Point.Distance(e.GetPosition(row), at) < PatternDragStart) return;
            pressed = null;
            DragPattern(x.Id, e);
        };
        row.PointerReleased += (_, _) => pressed = null;
        return row;
    }

    /// <summary>The inline choice of deleting a pattern: its clips go with it or stay as clips of their own.</summary>
    Control PatternDeleteRow(MidiPatterns.Info x)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = Loc.T("ptn.delete.ask", x.Name, PatternClipsLabel(x.Clips)), FontSize = 12, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Button Choice(string text, string? tip, Action click, params string[] classes)
        {
            var b = new Button { Classes = { "btn", "sm" }, Content = text, Height = 28, Margin = new Thickness(6, 2, 0, 2) };
            b.Classes.AddRange(classes);
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        Choice(Loc.T("common.cancel"), null, () => { patternDeleting = null; RefreshPatterns(); });
        Choice(Loc.T(x.Clips == 1 ? "ptn.unique.one" : "ptn.unique"), Loc.T("ptn.unique.tip"),
            () => _ = EditPatterns(x.Id, p => MidiPatterns.Dissolve(p, x.Id), "midi-pattern-dissolved", EditSteps.PatternDissolved(x.Name)));
        Choice(Loc.T(x.Clips == 1 ? "ptn.remove.one" : "ptn.remove"), Loc.T("ptn.remove.tip"),
            () => _ = EditPatterns(x.Id, p => MidiPatterns.Remove(p, x.Id), "midi-pattern-removed", EditSteps.PatternRemoved(x.Name)), "danger");
        stack.Children.Add(buttons);
        return new Border { Child = stack, Background = Palette.RecSoft, Padding = new Thickness(16, 8, 8, 6) };
    }

    /// <summary>A whole pattern's edit: one undo step, with the selection as it was.</summary>
    async Task EditPatterns(string patternId, Action<Project> edit, string activity, EditSteps.Label step)
    {
        if (store == null || !MidiPatterns.Clips(store.Project, patternId).Any()) return;
        string? track = sel?.Track.Id, clip = Timeline.SelectedMidiId;
        await EditTimeline(p => { edit(p); return track; }, activity, step: step);
        if (store != null && MidiEditing.TryFind(store.Project, clip) != null) Timeline.SelectMidiClip(TrackIndex(MidiEditing.Find(store.Project, clip!).Track.Id), clip);
        patternDeleting = null;
        RefreshPatterns();
    }

    void RenamePattern(MidiPatterns.Info x, Control anchor) =>
        AskName(anchor, null, Loc.T("midi.pattern.rename"), x.Name, Loc.T("common.ok"), name => _ = EditPatterns(x.Id,
            p => MidiPatterns.Rename(p, MidiPatterns.Clips(p, x.Id).First().Id, name), "midi-pattern-renamed", EditSteps.PatternRenamed(name)));

    /// <summary>The piano roll on the selected clip when it is one of the pattern's, else on its first.</summary>
    void OpenPattern(string patternId)
    {
        if (store == null) return;
        var clips = MidiPatterns.Clips(store.Project, patternId).ToList();
        if ((clips.Find(c => c.Id == Timeline.SelectedMidiId) ?? clips.FirstOrDefault()) is { } clip) OpenInPiano(clip.Id);
    }

    async void DragPattern(string patternId, PointerEventArgs e)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PatternDrag, patternId));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    /// <summary>Where a carried pattern lands: only on a MIDI track.</summary>
    (double Sec, string TrackId)? PatternTarget(DragEventArgs e) =>
        CanImport() && Timeline.DropTarget(e.GetPosition(Timeline), e.KeyModifiers) is { TrackId: { } id } at
            && store!.Project.Tracks.Find(t => t.Id == id)?.IsMidi == true ? (at.Sec, id) : null;

    /// <summary>One more use of a pattern, its clip on that track (or its first) selected.</summary>
    async Task InsertPattern(string trackId, string patternId, double atSec)
    {
        string? made = null, on = null;
        await EditTimeline(p =>
        {
            made = MidiPatterns.Insert(p, trackId, patternId, atSec).Id;
            return on = MidiEditing.Find(p, made).Track.Id;
        }, "midi-pattern-inserted");
        if (made != null) Timeline.SelectMidiClip(TrackIndex(on), made);
    }
}
