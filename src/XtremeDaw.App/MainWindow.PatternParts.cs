using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// A pattern's instruments in the piano roll (asked by the user, 2026-11-02): a chip in its header opens, with one click,
/// the list of what the pattern holds. A row edits that instrument, its eye shows or hides its ghost notes under the ones
/// being edited, and its bin takes it out. Each instrument is a track of its own (<see cref="MidiPatterns"/>), so adding
/// one is a choice of track. Which ghosts are hidden is a view setting: not saved, all shown when a project opens.
/// </summary>
public partial class MainWindow
{
    readonly Button pianoPartsButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    /// <summary>The parts whose ghost notes are hidden (<see cref="GhostKey"/>).</summary>
    readonly HashSet<string> ghostsHidden = [];

    static string GhostKey(string patternId, string? partId) => $"{patternId}/{partId}";

    static string PartsLabel(int n) => n == 1 ? Loc.T("piano.parts.one") : Loc.T("piano.parts.many", n);

    /// <summary>What the piano roll draws under <paramref name="clip"/>: the other instruments, except the hidden ones.</summary>
    List<(List<MidiNote> Notes, Color? Color)> PartGhosts(Project p, MidiClip clip)
    {
        if (clip.PatternId is not { } id) return [];
        return [.. MidiPatterns.Parts(p, id).Where(x => x.Id != clip.PatternPart && !ghostsHidden.Contains(GhostKey(id, x.Id)))
            .Select(x => (x.Clip.Notes, TrackIcons.ColorOf(x.Track)))];
    }

    /// <summary>The chip: the instrument being edited and how many the pattern holds.</summary>
    void RefreshPianoParts(Project? p, MidiClip? clip, XtremeDaw.Projects.Track? track)
    {
        pianoPartsButton.IsVisible = clip?.PatternId != null;
        if (clip?.PatternId is { } id) pianoPartsButton.Content = $"{track?.Name} · {PartsLabel(MidiPatterns.Parts(p!, id).Count)}  ▾";
    }

    /// <summary>The instrument's clip beside the one on screen, so moving between them stays in the same use of the pattern.</summary>
    MidiClip PartClip(Project p, MidiClip clip, MidiPatterns.Part part) =>
        MidiPatterns.Together(p.Tracks, clip).Find(c => c.PatternPart == part.Id) ?? part.Clip;

    void PickPart(MidiClip clip)
    {
        if (store == null) return;
        pianoClipId = clip.Id;
        RefreshPiano();
        Timeline.SelectMidiClip(TrackIndex(MidiEditing.Find(store.Project, clip.Id).Track.Id), clip.Id);
    }

    /// <summary>The list, in a card that stays open: it fills again after each click.</summary>
    void ShowPianoParts()
    {
        var list = new StackPanel { Spacing = 2, Width = 300 };
        var card = new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = list,
        };
        var flyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.BottomEdgeAlignedLeft };

        void Fill()
        {
            list.Children.Clear();
            if (store == null || MidiEditing.TryFind(store.Project, pianoClipId) is not { PatternId: { } id } clip) { flyout.Hide(); return; }
            var p = store.Project;
            bool editable = !store.ReadOnly && !restoring;
            var parts = MidiPatterns.Parts(p, id);
            list.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("piano.parts.title").ToUpperInvariant(), Margin = new Thickness(0, 0, 0, 6) });
            foreach (var part in parts) list.Children.Add(PartRow(p, id, clip, part, parts.Count > 1 && editable, Fill));
            var add = new Button { Classes = { "chip" }, Content = Loc.T("piano.parts.add") + "  ▾", Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), IsEnabled = editable };
            add.Click += (_, _) =>
            {
                flyout.Hide();
                var menu = Popup(AddPartItems(clip.Id), 240);
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                OpenAnchoredMenu(menu, pianoPartsButton);
            };
            list.Children.Add(add);
        }
        Fill();
        flyout.ShowAt(pianoPartsButton);
    }

    /// <summary>An instrument of the list: its name and sound to edit it, and on the others, its ghost notes; the bin takes it out.</summary>
    Control PartRow(Project p, string patternId, MidiClip clip, MidiPatterns.Part part, bool removable, Action fill)
    {
        bool editing = part.Id == clip.PatternPart;
        string key = GhostKey(patternId, part.Id);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,28"), ColumnSpacing = 2 };

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(TrackIcons.ColorOf(part.Track) ?? Palette.AccentColor), VerticalAlignment = VerticalAlignment.Center });
        var names = new StackPanel();
        names.Children.Add(new TextBlock { Text = part.Track.Name, FontSize = 12.5, Foreground = editing ? Palette.Accent : Palette.Fg, FontWeight = editing ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
        names.Children.Add(new TextBlock { Text = InstrumentName(part.Track), FontSize = 11, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
        line.Children.Add(names);
        var pick = new Button { Classes = { "ghost", "rowmain" }, Content = line, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4) };
        ToolTip.SetTip(pick, Loc.T("piano.parts.pick"));
        pick.Click += (_, _) =>
        {
            PickPart(PartClip(p, clip, part));
            fill();
        };
        grid.Children.Add(pick);

        if (!editing)
        {
            bool hidden = ghostsHidden.Contains(key);
            var eye = RowButton(hidden ? "" : "", Loc.T(hidden ? "piano.parts.ghost.show" : "piano.parts.ghost.hide"), () =>
            {
                if (!ghostsHidden.Remove(key)) ghostsHidden.Add(key);
                RefreshPiano();
                fill();
            });
            eye.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(eye, 1);
            grid.Children.Add(eye);
        }
        if (removable)
        {
            var remove = RowButton("", Loc.T("piano.parts.remove"), () => _ = RemovePart(part, fill), danger: true);
            remove.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
        }
        return grid;
    }

    /// <summary>Takes an instrument out; the piano roll goes on to another one if it was on it.</summary>
    async Task RemovePart(MidiPatterns.Part part, Action fill)
    {
        if (store == null || MidiEditing.TryFind(store.Project, pianoClipId) is not { PatternId: { } id } clip) return;
        var next = clip.PatternPart != part.Id ? clip.Id
            : PartClip(store.Project, clip, MidiPatterns.Parts(store.Project, id).First(x => x.Id != part.Id)).Id;
        string trackId = MidiEditing.Find(store.Project, next).Track.Id;
        await EditTimeline(p => { MidiPatterns.RemovePart(p, part.Clip.Id); return trackId; }, "midi-pattern-part-removed");
        pianoClipId = next;
        Timeline.SelectMidiClip(TrackIndex(trackId), next);
        RefreshPiano();
        fill();
    }

    /// <summary>The tracks an instrument can be added on: the MIDI ones that play nothing of the pattern, or a new one.</summary>
    List<object> AddPartItems(string clipId)
    {
        List<object> items = [];
        if (store == null || MidiEditing.TryFind(store.Project, clipId) is not { PatternId: { } id }) return items;
        foreach (var t in store.Project.Tracks.Where(t => t.IsMidi && t.MidiClips?.Any(c => c.PatternId == id) != true))
            items.Add(MenuAction(t.Name, () => AddPart(clipId, t.Id), InstrumentName(t)));
        if (items.Count > 0) items.Add(new Separator());
        items.Add(MenuAction(Loc.T("piano.parts.newTrack"), () => AddPart(clipId, null)));
        return items;
    }

    /// <summary>A new instrument on a track (null = a new one under the clip's), open in the piano roll to write it.</summary>
    async Task AddPart(string clipId, string? trackId)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
        string? made = null;
        await EditTimeline(p =>
        {
            string on = trackId ?? MidiEditing.AddTrackBelow(p, MidiEditing.Find(p, clipId).Track).Id;
            made = MidiPatterns.AddPart(p, clipId, on).Id;
            return on;
        }, "midi-pattern-part-added");
        if (made != null) OpenInPiano(made);
    }
}
