using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
    /// <summary>The folder the strip shows (#134: a folder is a channel), or null.</summary>
    Folder? stripFolder;
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
        static double Snap(double x) => Math.Abs(x) < 0.04 ? 0 : x;
        stripPan.Changed += v => StripEdit(v, (b, x) => b.Pan = Snap(x), (f, x) => f.Pan = Snap(x), (t, x) => t.Channel.Pan = Snap(x), "pan");
        AutomateOn(stripPan, StripLanes, AutoTarget.Pan, k => { stripPan.Value = k * 2 - 1; stripPan.Readout = ValueFormat.PanShort(k * 2 - 1); });
        stripFader.Changed += v => StripEdit(v, (b, x) => b.Volume = x, (f, x) => f.Volume = x, (t, x) => t.Channel.Volume = x, "volume");
        AutomateOn(stripFader, StripLanes, AutoTarget.Volume, k => { stripFader.Value = Fader.ToVol(k); ShowStripDb(Fader.ToVol(k)); });
        var mute = StripToggle("M", Shortcuts.Tip(Loc.T("track.mute"), "mute"), 26,
            () => stripBus?.Muted ?? stripFolder?.Muted ?? (PickTargets() is { Count: > 0 } p && p.All(t => t.Channel.Muted)), true,
            () =>
            {
                if (stripBus is { } b) BusEdit(b.Id, x => x.Muted = !x.Muted, "mute");
                else if (stripFolder is { } f) FolderChannelEdit(f.Id, x => x.Muted = !x.Muted, "muted");
                else TogglePicked(false);
            }, stripRefresh);
        stripSolo = StripToggle("S", Shortcuts.Tip(Loc.T("track.solo"), "solo"), 26,
            () => stripBus == null && (stripFolder?.Solo ?? (PickTargets() is { Count: > 0 } p && p.All(t => t.Channel.Solo))), false,
            () =>
            {
                if (stripFolder is { } f) FolderChannelEdit(f.Id, x => x.Solo = !x.Solo, "solo");
                else TogglePicked(true);
            }, stripRefresh);
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
        // each list ends in its "+" (#150), not on its title
        var stripFxAdd = ListAddRow(Loc.T("mix.slot.add.tip"), StripRowH, b => { if (stripOwner != null) ShowAddSlotMenu(stripOwner, b); });
        var stripSendsAdd = ListAddRow(Loc.T("send.add.tip"), StripRowH, b => { if (stripRouter != null) OpenAnchoredMenu(Popup(SendChoices(stripRouter), 220), b); });
        stripFxAdd.Margin = stripSendsAdd.Margin = new Thickness(0, StripRowGap - 4, 0, 0);
        stripFxPart = new StackPanel
        {
            Spacing = 4,
            Children = { StripListHead(StripGlyph(""), Loc.T("mix.list.fx"), stripFxCount), stripFx, stripFxAdd },
        };
        stripSendsPart = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(0, 12, 0, 0),
            Children = { StripListHead(FxIcon.Create(FxIcon.Bus, 12), Loc.T("mix.list.sends"), stripSendsCount), stripSends, stripSendsAdd },
        };
        var outputPart = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0), Children = { StripListHead(StripGlyph(""), Loc.T("route.output"), null), stripOutput } };
        var lists = new StackPanel { Margin = new Thickness(8, 10, 10, 10), Children = { stripFxPart, stripSendsPart, outputPart } };
        var right = new ScrollViewer { Content = lists, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var leftBox = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        grid.Children.Add(leftBox);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        inspectorStrip.Child = grid;
        // the rail still lists the mix first, and a click on it opens the inspector: the strip is always in sight
        sectionLooks["Mezcla"] = ("", null);
    }

    /// <summary>Whose lanes the strip's fader and pan offer and follow: the folder it shows or the single track; none for a bus.</summary>
    IAutomated? StripLanes() => stripBus != null ? null : stripFolder ?? (IAutomated?)SingleTrack?.Track;

    /// <summary>An edit of the strip's fader or pan: the bus or folder it shows, or the selected (or picked) tracks.</summary>
    void StripEdit(double v, Action<Bus, double> bus, Action<Folder, double> folder, Action<LiveTrack, double> track, string key)
    {
        if (syncing) return;
        if (stripBus is { } b) BusEdit(b.Id, x => bus(x, v), key);
        else if (stripFolder is { } f) FolderChannelEdit(f.Id, x => folder(x, v), key);
        else EditPicked(t => track(t, v), key);
    }

    void ShowStripDb(double v)
    {
        if (v == stripDbShown) return;
        stripDbShown = v;
        stripDb.Text = ValueFormat.Db(v);
    }

    static TextBlock StripGlyph(string glyph) => new() { Classes = { "icon" }, Text = glyph, FontSize = 12 };

    /// <summary>The strip's part titles (Efectos, Envíos, Salida): bigger than the rows and in italics (asked by the user).</summary>
    const double StripHeadSize = 13;

    /// <summary>
    /// A part's title in the strip (asked by the user: what each one is, at a glance): its icon and name in the accent
    /// over a thin rule, and how many it holds; its "+" is the list's last row (#150, <see cref="ListAddRow"/>).
    /// </summary>
    static Border StripListHead(Control icon, string title, TextBlock? count)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Height = 24 };
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(2, 0, 7, 0);
        FxIcon.SetColor(icon, Palette.Accent);
        g.Children.Add(icon);
        var name = new TextBlock { Text = title.ToUpperInvariant(), FontSize = StripHeadSize, FontStyle = FontStyle.Italic, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(name, 1); g.Children.Add(name);
        if (count != null)
        {
            count.Classes.Add("mono");
            count.FontSize = 11;
            count.Foreground = Palette.Faint;
            count.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(count, 2); g.Children.Add(count);
        }
        return new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 3), Margin = new Thickness(0, 0, 0, 2), Child = g };
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
            stripFx.Children.Add(drag.Add(WithStripTrash(StripSlotRow(o, title, slot.Type, id), Loc.T("mix.slot.remove.tip"),
                () => SlotsEdit(o, x => ChannelInserts.Remove(x, id), "remove")), id, stripRows[id]));
            // the folded inspector's column shows them too, by their name
            string railId = "slot:" + id;
            SendTitles[railId] = title;
            sectionLooks[railId] = (slot.Type, () => o.Slot(id)?.On == true);
            chainSections.Add(railId);
            chainIds[railId] = id;
        }
    }

    /// <summary>
    /// A row of the strip's effects or sends with a trash can at its right (asked by the user): takes the effect out, or
    /// the send out of the list, at one click, as their menus do.
    /// </summary>
    static Grid WithStripTrash(Control row, string tip, Func<Task> remove)
    {
        var trash = new Button { Classes = { "act", "danger" }, Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), Focusable = false,
            Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(trash, tip);
        Avalonia.Automation.AutomationProperties.SetName(trash, tip);
        trash.Click += (_, _) => _ = remove();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(row);
        Grid.SetColumn(trash, 1); g.Children.Add(trash);
        return g;
    }

    readonly List<Action> chainRefresh = [];
    /// <summary>The effects' ids for the folded inspector's column ("slot:" + the slot's id), in order.</summary>
    readonly List<string> chainSections = [];
    readonly Dictionary<string, string> chainIds = [];
    string inspectorChainKey = "";

    /// <summary>A slot's row in the strip: power apart, as a send's (asked by the user), then icon, name and what it is set to.</summary>
    Border StripSlotRow(SlotOwner o, string name, string type, string id)
    {
        var row = ListRow(name, type, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, x => x.On = !x.On, "on"),
            () => o.Slot(id) is { } s ? SlotBrief(s) : "", chainRefresh, apart: true);
        row.Height = StripRowH;
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
    void LayoutInspector(LiveClip? clip, List<LiveTrack>? group, Bus? bus, Folder? folder)
    {
        stripBus = bus;
        stripFolder = folder;
        stripTrack = bus != null || folder != null ? null : group != null ? PickLead : sel;
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

        stripWanted = channel || bus != null || folder != null;
        ShowInspectorPanes();
        // a folder's strip goes with its own panel (takeSections is a track's)
        inspectorStrip.IsEnabled = Inspector.IsEnabled && (folder != null || takeSections.IsEnabled);
        stripFxPart.IsVisible = group == null;
        // a folder's sends and output are a track's (#149), made by RebuildInspectorSends
        stripSendsPart.IsVisible = bus == null && (folder != null || channel);
        stripSolo.IsVisible = bus == null;
        if (bus != null)
        {
            // a bus only goes to the Master for now (ProjectBuses.CanFeed)
            stripOutput.Child = StripOutputRow(new TextBlock { Text = Loc.T("route.master"), Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }
        stripFxCount.Text = stripRows.Count > 0 ? stripRows.Count.ToString() : "";
        stripSendsCount.Text = stripSends.Children.Count > 0 ? stripSends.Children.Count.ToString() : "";

        if (!stripWanted) return;
        bool single = bus != null || folder != null;
        bool mixed = !single && PicksDiffer(t => t.Channel.Volume);
        double volume = bus?.Volume ?? folder?.Volume ?? PickLead.Channel.Volume, pan = bus?.Pan ?? folder?.Pan ?? PickLead.Channel.Pan;
        stripFader.Value = volume;
        stripFader.Opacity = mixed ? 0.6 : 1;
        if (mixed) { stripDb.Text = Loc.T("insp.mixed"); stripDbShown = double.NaN; }
        else ShowStripDb(volume);
        bool panMixed = !single && PicksDiffer(t => t.Channel.Pan);
        stripPan.Value = pan;
        stripPan.Readout = panMixed ? Loc.T("insp.mixed") : ValueFormat.PanShort(pan);
    }

    /// <summary>What the channel goes to, under the strip's "SALIDA" title (<see cref="StripListHead"/>).</summary>
    static Control StripOutputRow(Control what)
    {
        what.Margin = new Thickness(2, 0, 0, 0);
        return what;
    }

    /// <summary>The strip's meter, from the tick: the selected track's (or the first picked one's) or the bus's. Allocates nothing.</summary>
    void TickInspectorStrip()
    {
        if (!inspectorStrip.IsVisible || inspectorFolded && !InspectorOut) return;
        if ((stripBus?.Id ?? stripFolder?.Id) is { } node)
        {
            stripMeter.Mono = false;
            if (engine.Node(node) is { } r) stripMeter.Set(r.ShowL, r.ShowR, Math.Sqrt(r.Rms.MeanSqL), Math.Sqrt(r.Rms.MeanSqR));
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
    /// <summary>
    /// The divider over the strip. Not a GridSplitter: that one limits the drag by what the sections over it measure,
    /// which differs with each kind of channel (a MIDI track's would not move); this one sets the strip's height itself.
    /// </summary>
    readonly Border stripSplitter = new()
    {
        Height = 5, Background = Palette.Line, BorderThickness = new Thickness(0, 2), BorderBrush = Palette.Panel, Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
    };
    /// <summary>Where a drag of the divider started (y in the inspector's body) and the strip's height then; NaN = no drag.</summary>
    double splitFromY = double.NaN, splitFromH;
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
        stripSplitter.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(stripSplitter).Properties.IsLeftButtonPressed) return;
            splitFromY = e.GetPosition(inspectorBody).Y;
            splitFromH = inspectorBody.RowDefinitions[3].ActualHeight;
            e.Pointer.Capture(stripSplitter);
            e.Handled = true;
        };
        stripSplitter.PointerMoved += (_, e) =>
        {
            if (double.IsNaN(splitFromY)) return;
            // the sections over it keep their least, whatever they hold
            double room = inspectorBody.Bounds.Height - inspectorBody.RowDefinitions[0].ActualHeight - stripSplitter.Bounds.Height - EditorMinH;
            stripHeight = Math.Max(StripMinH, Math.Min(room, splitFromH - (e.GetPosition(inspectorBody).Y - splitFromY)));
            inspectorBody.RowDefinitions[3].Height = new GridLength(stripHeight);
        };
        void EndSplit()
        {
            if (double.IsNaN(splitFromY)) return;
            splitFromY = double.NaN;
            SaveViewPref("inspector.stripH", stripHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        stripSplitter.PointerReleased += (_, e) => { e.Pointer.Capture(null); EndSplit(); };
        stripSplitter.PointerCaptureLost += (_, _) => EndSplit();
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
    /// Sizes the panes. With the strip, it keeps its own height (dragged by the divider, the same for every kind of
    /// channel: audio with or without the clip, MIDI, a bus, picked tracks) and the sections over it take the rest,
    /// scrolling what doesn't fit. Without the strip, the sections take it all.
    /// </summary>
    void ShowInspectorPanes()
    {
        bool strip = stripWanted && stripShown;
        var rows = inspectorBody.RowDefinitions;
        rows[1].Height = new GridLength(1, GridUnitType.Star);
        rows[1].MinHeight = strip ? EditorMinH : 0;
        inspectorStrip.IsVisible = strip;
        rows[3].Height = strip ? new GridLength(stripHeight) : new GridLength(0);
        rows[3].MinHeight = strip ? StripMinH : 0;
        stripSplitter.IsVisible = strip;
        if (clipToggle == null) return;
        clipToggle.Foreground = clipShown ? Palette.Accent : Palette.Faint;
        stripToggle.Foreground = stripShown ? Palette.Accent : Palette.Faint;
        ToolTip.SetTip(clipToggle, Loc.T(clipShown ? "insp.pane.clip.hide" : "insp.pane.clip.show"));
        ToolTip.SetTip(stripToggle, Loc.T(stripShown ? "insp.pane.strip.hide" : "insp.pane.strip.show"));
    }
}
