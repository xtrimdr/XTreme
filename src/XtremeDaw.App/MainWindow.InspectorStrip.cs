using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The inspector laid out like Fender Studio Pro's (asked by the user, with XTreme's own look): the channel's name fixed
/// on top, then three sections stacked: the track's (always; its settings as compact rows), the selected clip's and the
/// channel strip (pan, M S, the fader with its meter, and beside it the effects in the order they sound, the sends and
/// the output). Two buttons in the header show or hide the clip and the strip, and the divider between the clip and
/// the strip sets the strip's height. An effect opens in its own window, as in the mixer.
/// </summary>
public partial class MainWindow
{
    readonly StackPanel inspectorTop = new() { Spacing = 10 };
    readonly Border inspectorTopBox = new() { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 12, 14, 12) };

    /// <summary>The clip's section: its file's name and facts over its settings, or that no clip is selected.</summary>
    readonly StackPanel clipArea = new() { Spacing = 0 };
    readonly TextBlock clipAreaName = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock noClip = new() { FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
    /// <summary>The track's Transponer row, moved to where it belongs (<see cref="LayoutInspector"/>).</summary>
    readonly StackPanel trackTranspose = new() { Spacing = 2 };
    readonly Border clipTransposeHost = new() { Margin = new Thickness(0, 0, 0, 6) };

    readonly Border inspectorStrip = new() { Background = Palette.App, IsVisible = false };
    readonly StackPanel stripFx = new() { Spacing = StripRowGap }, stripSends = new() { Spacing = StripRowGap };
    readonly Dictionary<string, Border> stripRows = [];
    readonly TextBlock stripFxEmpty = new() { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0) };
    readonly TextBlock stripSendsEmpty = new() { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0) };
    readonly TextBlock stripFxCount = new(), stripSendsCount = new();
    readonly Decorator stripOutput = new();
    readonly Fader stripFader = new();
    readonly MixerMeter stripMeter = new();
    readonly HBar stripPan = new() { Bipolar = true, ResetTo = 0, Height = 20 };
    readonly TextBlock stripDb = new() { Classes = { "mono" }, FontSize = 11, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center };
    Button stripSolo = null!;
    Control stripFxPart = null!, stripSendsPart = null!;

    /// <summary>The bus the strip shows, or null for the selected track (or picked ones).</summary>
    Bus? stripBus;
    /// <summary>The track whose meter the strip shows (read every tick, so no list is made there).</summary>
    LiveTrack? stripTrack;
    /// <summary>Whose slots and sends the strip's "+" add to.</summary>
    SlotOwner? stripOwner;
    Router? stripRouter;
    double stripDbShown = double.NaN;
    /// <summary>The strip's own controls (M S), refreshed whenever it shows, for a track or a bus.</summary>
    readonly List<Action> stripRefresh = [];

    /// <summary>The strip's height (px) until the user drags it: its fader, pan, M S and readout, with room around.</summary>
    const double StripH = 252;
    /// <summary>The least the strip and the sections over it shrink to when the divider between them is dragged (px).</summary>
    const double StripMinH = 170, EditorMinH = 80;
    /// <summary>A row of the strip's lists (px), roomier than a mixer strip's.</summary>
    const double StripRowH = 24;
    const double StripRowGap = 2;

    void InitInspectorStrip()
    {
        inspectorTopBox.Child = inspectorTop;

        // the clip's section: its name with the clip icon, then its facts and settings (InitClipInspector)
        noClip.Text = Loc.T("insp.clip.none");
        var clipIcon = new TextBlock { Classes = { "icon" }, Text = "\uE8D6", FontSize = 12, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var clipHead = new DockPanel { Margin = new Thickness(0, 4, 0, 8), Children = { clipIcon, clipAreaName } };
        DockPanel.SetDock(clipIcon, Dock.Left);
        var clipBand = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(-14, 4, -14, 0), Padding = new Thickness(14, 10, 14, 0), Child = clipHead };
        clipArea.Children.Add(clipBand);
        clipArea.Children.Add(clipTransposeHost);
        clipArea.Children.Add(noClip);
        clipArea.Children.Add(clipSections);

        // left: pan, M S, the fader and its meter, the level
        stripPan.Changed += v => StripEdit(v, (b, x) => b.Pan = Math.Abs(x) < 0.04 ? 0 : x, (t, x) => t.Channel.Pan = Math.Abs(x) < 0.04 ? 0 : x, "pan");
        AutomateOn(stripPan, () => SingleTrack, AutoTarget.Pan, k => { stripPan.Value = k * 2 - 1; stripPan.Readout = ValueFormat.PanShort(k * 2 - 1); });
        stripFader.Changed += v => StripEdit(v, (b, x) => b.Volume = x, (t, x) => t.Channel.Volume = x, "volume");
        AutomateOn(stripFader, () => SingleTrack, AutoTarget.Volume, k => { stripFader.Value = Fader.ToVol(k); ShowStripDb(Fader.ToVol(k)); });
        var mute = StripToggle("M", Shortcuts.Tip(Loc.T("track.mute"), "mute"), 26, () => stripBus?.Muted ?? PickTargets() is { Count: > 0 } p && p.All(t => t.Channel.Muted), true,
            () => { if (stripBus is { } b) BusEdit(b.Id, x => x.Muted = !x.Muted, "mute"); else TogglePicked(false); }, stripRefresh);
        stripSolo = StripToggle("S", Shortcuts.Tip(Loc.T("track.solo"), "solo"), 26, () => stripBus == null && PickTargets() is { Count: > 0 } p && p.All(t => t.Channel.Solo), false,
            () => TogglePicked(true), stripRefresh);
        var toggles = new UniformGrid { Rows = 1, HorizontalAlignment = HorizontalAlignment.Center, Children = { mute, stripSolo } };
        mute.Margin = stripSolo.Margin = new Thickness(2, 0);
        // the fader takes what the strip has left, so a taller strip gives it a longer travel
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { stripFader, stripMeter } };
        var left = new Grid { Width = 84, RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(8, 10, 8, 8) };
        toggles.Margin = new Thickness(0, 8);
        stripDb.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(stripPan);
        Grid.SetRow(toggles, 1); left.Children.Add(toggles);
        Grid.SetRow(faders, 2); left.Children.Add(faders);
        Grid.SetRow(stripDb, 3); left.Children.Add(stripDb);

        // right: the effects in the order they sound, the sends, the output
        stripFxEmpty.Text = Loc.T("insp.tray.fx.empty");
        stripSendsEmpty.Text = Loc.T("insp.tray.sends.empty");
        stripFxPart = new StackPanel
        {
            Spacing = 4,
            Children = { StripListHead(Loc.T("mix.list.fx"), stripFxCount, Loc.T("mix.slot.add.tip"), b => { if (stripOwner != null) ShowAddSlotMenu(stripOwner, b); }), stripFx, stripFxEmpty },
        };
        stripSendsPart = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(0, 10, 0, 0),
            Children = { StripListHead(Loc.T("mix.list.sends"), stripSendsCount, Loc.T("send.add.tip"), b => { if (stripRouter != null) OpenAnchoredMenu(Popup(SendChoices(stripRouter), 220), b); }),
                stripSends, stripSendsEmpty },
        };
        stripOutput.Margin = new Thickness(0, 10, 0, 0);
        var lists = new StackPanel { Margin = new Thickness(8, 10, 10, 10), Children = { stripFxPart, stripSendsPart, stripOutput } };
        var right = new ScrollViewer { Content = lists, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var leftBox = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        grid.Children.Add(leftBox);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        inspectorStrip.Child = grid;
        // the rail still lists the mix first, and a click on it opens the inspector: the strip is always in sight
        sectionLooks["Mezcla"] = ("", null);
    }

    /// <summary>An edit of the strip's fader or pan: the bus it shows, or the selected (or picked) tracks.</summary>
    void StripEdit(double v, Action<Bus, double> bus, Action<LiveTrack, double> track, string key)
    {
        if (syncing) return;
        if (stripBus is { } b) BusEdit(b.Id, x => bus(x, v), key);
        else EditPicked(t => track(t, v), key);
    }

    void ShowStripDb(double v)
    {
        if (v == stripDbShown) return;
        stripDbShown = v;
        stripDb.Text = ValueFormat.Db(v);
    }

    /// <summary>A list's title in the strip, how many it holds and its "+".</summary>
    Grid StripListHead(string title, TextBlock count, string addTip, Action<Button> add)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 22 };
        g.Children.Add(new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) });
        count.Classes.Add("mono");
        count.FontSize = 11;
        count.Foreground = Palette.Faint;
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 1); g.Children.Add(count);
        var plus = SlotHeadButton("", addTip);
        plus.Width = plus.Height = plus.MinHeight = 22;
        plus.Click += (_, _) => { if (!PressClosedMenuOf(plus)) add(plus); };
        Grid.SetColumn(plus, 2); g.Children.Add(plus);
        return g;
    }

    /// <summary>
    /// The channel's effects, of a track or a bus, as a row per insert slot in the strip, in the order the sound goes
    /// through them: a click opens it in its window (a loaded VST3, its own), dragged it moves, a right click opens its
    /// menu. Rebuilt only when the channel or its list of slots changes (<see cref="inspectorChainKey"/>).
    /// </summary>
    void RebuildInspectorChain(SlotOwner o, object owner)
    {
        // the object itself too: a reopened project builds new ones with the same ids
        string key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner)}|{o.OwnerId}|" + string.Join("/", o.Slots().Select(s => s.Id));
        if (key == inspectorChainKey) return;
        inspectorChainKey = key;
        stripOwner = o;
        foreach (string title in chainSections) sectionLooks.Remove(title);
        chainSections.Clear();
        chainIds.Clear();
        chainRefresh.Clear();
        stripFx.Children.Clear();
        stripRows.Clear();
        var drag = new SlotDrag(StripRowGap);
        drag.Moved += (id, anchor, after) => SlotsEdit(o, x => ChannelInserts.MoveNextTo(x, id, anchor, after), "move");
        drag.Clicked += id => OpenStripSlot(o, id);
        var slots = o.Slots();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            string id = slot.Id, title = SlotLabel(slots, slot, nth);
            stripFx.Children.Add(drag.Add(StripSlotRow(o, title, slot.Type, id), id, stripRows[id]));
            // the folded inspector's column shows them too, by their name
            string railId = "slot:" + id;
            SendTitles[railId] = title;
            sectionLooks[railId] = (slot.Type, () => o.Slot(id)?.On == true);
            chainSections.Add(railId);
            chainIds[railId] = id;
        }
    }

    readonly List<Action> chainRefresh = [];
    /// <summary>The effects' ids for the folded inspector's column ("slot:" + the slot's id), in order.</summary>
    readonly List<string> chainSections = [];
    readonly Dictionary<string, string> chainIds = [];
    string inspectorChainKey = "";

    /// <summary>A slot's row in the strip: power, icon, name and what it is set to.</summary>
    Border StripSlotRow(SlotOwner o, string name, string type, string id)
    {
        var row = ListRow(name, type, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, x => x.On = !x.On, "on"),
            () => o.Slot(id) is { } s ? SlotBrief(s) : "", chainRefresh);
        row.Height = StripRowH;
        row.Padding = new Thickness(3, 0, 6, 0);
        row.Cursor = new Cursor(StandardCursorType.Hand);
        row.Tag = id;
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || store == null) return;
            e.Handled = true;
            ShowSlotMenu(o, id, row, row);
        };
        stripRows[id] = row;
        return row;
    }

    /// <summary>An effect from the strip, like the mixer's: a loaded VST3 opens its own window, the others (and a
    /// plugin still loading or that failed) their panel in a small window.</summary>
    void OpenStripSlot(SlotOwner o, string id)
    {
        if (o.Slot(id)?.Type == InsertSlot.VstKind && EffectStateOf(o.OwnerId, id) == EffectState.Ready) OpenEffectWindow(o.OwnerId, id);
        else if (stripRows.TryGetValue(id, out var row)) ShowSlotWindow(o, id, row);
    }

    /// <summary>
    /// Lays the inspector out for what is selected: the clip's section for a single audio track, the strip for a channel
    /// (a track, picked tracks or a bus). Called from <see cref="RefreshInspectorText"/> once the chain and the sends are
    /// rebuilt, before the refreshes run.
    /// </summary>
    void LayoutInspector(LiveClip? clip, List<LiveTrack>? group, Bus? bus)
    {
        stripBus = bus;
        stripTrack = bus != null ? null : group != null ? PickLead : sel;
        bool channel = sel != null || group != null;

        inspectorTopBox.IsVisible = channel || bus != null;
        inspectorTopBox.IsEnabled = Inspector.IsEnabled;
        busHead.IsVisible = bus != null;
        takeSections.IsVisible = channel;
        takeSections.IsEnabled = group == null || group.Any(t => !t.Track.Locked);
        busPanel.IsVisible = bus != null;
        // the clip's section: a single audio track's, shown or not from the header
        clipArea.IsVisible = clipShown && sel is { Track.IsMidi: false } && group == null;
        clipSections.IsVisible = clip != null;
        noClip.IsVisible = clip == null;
        clipAreaName.Text = clip == null ? SectionTitle("Clip") : clip.Media?.Name ?? SectionTitle("Clip");
        // an audio track's Transponer goes with its clips, as Studio Pro's Transpose does per event; a MIDI track, or
        // several tracks, keep it in the track's section
        bool withClip = clipArea.IsVisible;
        if (withClip != (trackTranspose.Parent == clipTransposeHost))
        {
            (trackTranspose.Parent as Panel)?.Children.Remove(trackTranspose);
            clipTransposeHost.Child = null;
            if (withClip) clipTransposeHost.Child = trackTranspose;
            else takeSections.Children.Add(trackTranspose);
        }

        stripWanted = channel || bus != null;
        ShowInspectorPanes();
        inspectorStrip.IsEnabled = Inspector.IsEnabled && takeSections.IsEnabled;
        stripFxPart.IsVisible = group == null;
        stripSendsPart.IsVisible = bus == null && channel;
        stripSolo.IsVisible = bus == null;
        if (bus != null)
        {
            // a bus only goes to the Master for now (ProjectBuses.CanFeed)
            stripOutput.Child = StripOutputRow(new TextBlock { Text = Loc.T("route.master"), Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }
        stripFxCount.Text = stripRows.Count > 0 ? stripRows.Count.ToString() : "";
        stripFxEmpty.IsVisible = stripRows.Count == 0;
        stripSendsCount.Text = stripSends.Children.Count > 0 ? stripSends.Children.Count.ToString() : "";
        stripSendsEmpty.IsVisible = stripSends.Children.Count == 0;

        if (!stripWanted) return;
        bool mixed = bus == null && PicksDiffer(t => t.Channel.Volume);
        double volume = bus?.Volume ?? PickLead.Channel.Volume, pan = bus?.Pan ?? PickLead.Channel.Pan;
        stripFader.Value = volume;
        stripFader.Opacity = mixed ? 0.6 : 1;
        if (mixed) { stripDb.Text = Loc.T("insp.mixed"); stripDbShown = double.NaN; }
        else ShowStripDb(volume);
        bool panMixed = bus == null && PicksDiffer(t => t.Channel.Pan);
        stripPan.Value = pan;
        stripPan.Readout = panMixed ? Loc.T("insp.mixed") : ValueFormat.PanShort(pan);
    }

    /// <summary>"SALIDA" and what the channel goes to, the last row of the strip's lists.</summary>
    static Grid StripOutputRow(Control what)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("route.output.upper"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) });
        Grid.SetColumn(what, 1); g.Children.Add(what);
        return g;
    }

    /// <summary>The strip's meter, from the tick: the selected track's (or the first picked one's) or the bus's. Allocates nothing.</summary>
    void TickInspectorStrip()
    {
        if (!inspectorStrip.IsVisible || inspectorFolded && !InspectorOut) return;
        if (stripBus is { } b)
        {
            if (engine.Node(b.Id) is { } r) stripMeter.Set(r.ShowL, r.ShowR, Math.Sqrt(r.Rms.MeanSqL), Math.Sqrt(r.Rms.MeanSqR));
        }
        else if (stripTrack is { } t)
        {
            stripMeter.Mono = TrackMono(t);
            stripMeter.Set(t.ShowL, t.ShowR, Math.Sqrt(t.Chain.Rms.MeanSqL), Math.Sqrt(t.Chain.Rms.MeanSqR));
        }
    }

    /// <summary>Opens the inspector on an effect of the folded column: its window, once the strip is laid out.</summary>
    void OpenRailSlot(string railId)
    {
        if (stripOwner is not { } o || !chainIds.TryGetValue(railId, out var id)) return;
        stripShown = true;
        ShowInspectorPanes();
        Dispatcher.UIThread.Post(() => OpenStripSlot(o, id), DispatcherPriority.Loaded);
    }

    // --- the panes: the sections over the strip, and the strip, each shown or hidden from the header

    readonly Grid inspectorBody = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
    ScrollViewer? inspectorScroll;
    readonly GridSplitter stripSplitter = new()
    {
        ResizeDirection = GridResizeDirection.Rows, Height = 5, Background = Palette.Line, BorderThickness = new Thickness(0, 2), BorderBrush = Palette.Panel,
    };
    Button clipToggle = null!, stripToggle = null!;
    /// <summary>The selection has a strip to show (a track, picked tracks or a bus).</summary>
    bool stripWanted;
    bool clipShown = AppConfig.Get("inspector.clip") != "0", stripShown = AppConfig.Get("inspector.strip") != "0";
    double stripHeight = double.TryParse(AppConfig.Get("inspector.stripH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h)
        && h >= StripMinH ? h : StripH;

    /// <summary>Under the header: the channel's name, the track's and the clip's sections (the part that scrolls), the divider and the strip.</summary>
    Grid InspectorBody(ScrollViewer scroll)
    {
        inspectorScroll = scroll;
        inspectorBody.Children.Add(inspectorTopBox);
        Grid.SetRow(scroll, 1); inspectorBody.Children.Add(scroll);
        Grid.SetRow(stripSplitter, 2); inspectorBody.Children.Add(stripSplitter);
        Grid.SetRow(inspectorStrip, 3); inspectorBody.Children.Add(inspectorStrip);
        stripSplitter.DragCompleted += (_, _) =>
        {
            stripHeight = Math.Max(StripMinH, inspectorBody.RowDefinitions[3].ActualHeight);
            SaveViewPref("inspector.stripH", stripHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        ShowInspectorPanes();
        return inspectorBody;
    }

    /// <summary>The header's two switches (asked by the user, like Fender Studio Pro's): show or hide the clip's section and the strip.</summary>
    StackPanel InspectorPaneButtons()
    {
        clipToggle = RailButton("\uE8D6", "", () => { clipShown = !clipShown; SaveViewPref("inspector.clip", clipShown ? "1" : "0"); RefreshInspectorText(); });
        stripToggle = RailButton("", "", () => { stripShown = !stripShown; SaveViewPref("inspector.strip", stripShown ? "1" : "0"); ShowInspectorPanes(); });
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 2, Margin = new Thickness(0, 0, 6, 0),
            Children = { clipToggle, stripToggle } };
    }

    /// <summary>
    /// Sizes the panes. With the clip's section shown, the strip keeps its height and the sections over it take the rest,
    /// the divider between them dragged; without it, the track's section takes what it needs and the strip the rest.
    /// Without the strip, the sections take it all.
    /// </summary>
    void ShowInspectorPanes()
    {
        bool strip = stripWanted && stripShown, roomy = clipArea.IsVisible || !strip;
        var rows = inspectorBody.RowDefinitions;
        rows[1].Height = roomy ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        rows[1].MinHeight = strip && roomy ? EditorMinH : 0;
        inspectorStrip.IsVisible = strip;
        rows[3].Height = !strip ? new GridLength(0) : roomy ? new GridLength(stripHeight) : new GridLength(1, GridUnitType.Star);
        rows[3].MinHeight = strip ? StripMinH : 0;
        stripSplitter.IsVisible = strip && roomy;
        // the strip's own top edge when the divider isn't there
        inspectorStrip.BorderBrush = Palette.Line;
        inspectorStrip.BorderThickness = new Thickness(0, strip && !roomy ? 1 : 0, 0, 0);
        if (clipToggle == null) return;
        clipToggle.Foreground = clipShown ? Palette.Accent : Palette.Faint;
        stripToggle.Foreground = stripShown ? Palette.Accent : Palette.Faint;
        ToolTip.SetTip(clipToggle, Loc.T(clipShown ? "insp.pane.clip.hide" : "insp.pane.clip.show"));
        ToolTip.SetTip(stripToggle, Loc.T(stripShown ? "insp.pane.strip.hide" : "insp.pane.strip.show"));
    }
}
