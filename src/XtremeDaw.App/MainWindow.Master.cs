using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The Mastering view (native only, asked by the user): the third view beside Timeline and Mixer, in the mixer's place
/// (<see cref="MixerHost"/>, so it keeps the mixer's height and its spot in solo audio) or in its own window, and what
/// the master strip's button opens. Redesigned as a rack with an editor (asked by the user, 2026-11-03: the old one was
/// too wide and too boxed in): a bar on top with the master's one ON, which turns the whole chain off, Salida included,
/// and the Destino; under it the chain as a row of tiles in the order the sound goes, the master's effects (the same
/// list as the master strip's, <see cref="MasterSettings.Slots"/>: adding, removing or dragging one here or there is the
/// same edit) and then Salida (Soft clip → Limitador, always last, as in <see cref="MixGraphBus"/>); and under the tiles
/// the panel of the one picked, the panel the effect has everywhere (<see cref="FxBody"/>) with its live meters around
/// it. The output meter is pinned at the right with the loudness over time. The view is as tall as its tallest panel
/// (<see cref="PagedPanel"/>), so picking another never moves it. Nothing is measured: each band follows its own level
/// as it plays (<see cref="MasterBandComp"/>), so a knob is heard the moment it moves.
/// </summary>
public partial class MainWindow
{
    /// <summary>A module's live reduction: a whole effect's (<see cref="Band"/> -1) or one band of a Multibanda's (3 = the Púa).</summary>
    sealed class ModuleMeter(string id, int band, Border bar, TextBlock text, TileMeter? tile)
    {
        public readonly string Id = id;
        public readonly int Band = band;
        public readonly Border Bar = bar;
        public readonly TextBlock Text = text;
        /// <summary>The tile's bar, which shows the deepest of its module's meters.</summary>
        public readonly TileMeter? Tile = tile;
        public double Show, Shown = double.NaN;
    }

    /// <summary>The thin reduction bar along a tile's foot.</summary>
    sealed class TileMeter(Border track, Border bar)
    {
        public readonly Border Track = track;
        public readonly Border Bar = bar;
        public double Db;
    }

    readonly List<ModuleMeter> moduleMeters = [];
    readonly List<TileMeter> tileMeters = [];
    /// <summary>A slot's tile bar by the slot's id, for its panel's meters to feed.</summary>
    readonly Dictionary<string, TileMeter> tileOf = [];
    TileMeter? clipTile, limTile;

    /// <summary>Width of the reduction bars and the reduction they show full (dB).</summary>
    const double GrBarW = 72, GrBarH = 5, GrBarFullDb = 9;
    /// <summary>The view's knobs, bigger than the strips' (asked by the user), and the column each one takes.</summary>
    const double MasterKnobSize = 44, MasterKnobColW = 64;
    /// <summary>The view's margin. A margin on the content, not the scroll viewer's padding: that padding is left out of
    /// the scrolled extent, so the end of the chain could not be scrolled into view.</summary>
    static readonly Thickness MasteringPad = new(14, 10);
    /// <summary>A tile of the chain: its narrowest (it grows with its name, never cut), and its reduction bar's height.</summary>
    const double TileMinW = 120, TileBarH = 3;
    /// <summary>An EQ's panel in the editor: its editor wants room.</summary>
    const double EqEditorW = 560;
    /// <summary>The fewest knob columns a panel takes in the editor, so a small effect doesn't look lost.</summary>
    const int EditorMinCols = 4;
    /// <summary>The live meter's readouts column: wide enough for "−88.8 LUFS", so it does not jump as the numbers change.</summary>
    const double ReadoutsW = 84;
    /// <summary>The loudness graph's width beside the readouts.</summary>
    const double HistoryW = 190;
    /// <summary>Salida's two stages, as the tiles and the editor know them (a slot's id never starts with '#').</summary>
    const string ClipPage = "#clip", LimPage = "#limit";

    /// <summary>The output meter pinned at the view's right (asked by the user: always in sight, as the master in the mixer).</summary>
    readonly Border masteringPin = PinnedColumn();
    MixerMeter? masteringMeter;
    LoudnessHistory? masterHistory;
    /// <summary>When the loudness graph last took a reading (ms, <see cref="Environment.TickCount64"/>).</summary>
    long masterHistoryAt;
    TextBlock? masterCorText;
    int masterCorShown = int.MaxValue;

    readonly List<Action> masterRefresh = [];
    readonly ScrollViewer masteringScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsVisible = false, Classes = { "notips" } };
    /// <summary>The module whose panel the editor shows (a slot's id, or <see cref="ClipPage"/> / <see cref="LimPage"/>); kept across rebuilds.</summary>
    string? masterPick;
    PagedPanel? masterEditor;
    /// <summary>The ids of the editor's pages in order, and the tiles that pick them.</summary>
    readonly List<string> masterPages = [];
    readonly List<(string Id, Border Tile)> masterTiles = [];
    Border? masterLimBar;
    MasterBandsGraph? masterGraph;
    TextBlock? masterLimText;
    /// <summary>The limiter's reduction: as <see cref="TickMixer"/> took it (dB, ≤ 0), as shown and as written.</summary>
    double masterLimDb, masterLimShow, masterLimShown = double.NaN;
    Border? masterClipBar;
    TextBlock? masterClipText, masterShortText, masterIntText, masterPeakText, masterToGoText, masterTargetText;
    double masterClipShow, masterClipShown = double.NaN;
    /// <summary>The master's output peak this pass (linear, from <see cref="TickMixer"/>) and what the meter last wrote.</summary>
    float masterOutPeak;
    double masterShortShown = double.NaN, masterIntShown = double.NaN, masterPeakShown = double.NaN;
    bool masteringView;

    /// <summary>The Mastering view is on screen: in the mixer's place or in its own window.</summary>
    bool MasteringShown => MasteringOut || (masteringView && mixerView);
    /// <summary>What social platforms normalise to; without a Destino the meter tells how far the mix is from it.</summary>
    const double SocialLufs = -14;

    MasterSettings MasterSet => store?.Project.Settings.Master ?? new();

    static readonly (Func<double, double> To, Func<double, double> From) LimReleaseMap = LogMap(MasterLimiter.ReleaseMinMs, MasterLimiter.ReleaseMaxMs);

    static string FmtDb(double db) => $"{db:+0.0;-0.0;0.0} dB";
    /// <summary>A reduction (dB, ≥ 0) as the meters show it.</summary>
    static string FmtGr(double db) => db <= 0 ? $"{0.0:0.0} dB" : $"{-db:0.0} dB";

    void InitMastering()
    {
        InitLoudness();
        // a narrower or wider place wraps the chain into more or fewer rows: the view's height follows
        masteringScroll.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && masteringScroll.IsVisible) Avalonia.Threading.Dispatcher.UIThread.Post(FitMasteringHeight);
        };
        // the content keeps one width whether the scroll bar shows or not (see MasteringGutter)
        masteringScroll.PropertyChanged += (_, e) =>
        {
            if ((e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty) && masteringScroll.Content is Border b)
            {
                var pad = MasteringGutter();
                if (b.Padding != pad) b.Padding = pad;
            }
        };
        MasteringViewButton.Click += (_, _) =>
        {
            if (ActivatePanel(FloatMastering)) return;
            if (mixerView && masteringView && !ViewsCovered) SetMixerView(false);
            else ShowBottomView(mastering: true);
        };
    }

    /// <summary>Mixer or Mastering: both live in the mixer's place, with different content.</summary>
    void ShowBottomView(bool mastering)
    {
        if (ActivatePanel(mastering ? FloatMastering : FloatMixer)) return;
        masteringView = mastering;
        pianoView = false;
        SetMixerView(true);
        ApplyMasteringView();
    }

    /// <summary>Shows the mixer's or the mastering's content in <see cref="MixerHost"/> and lights the right button.</summary>
    void ApplyMasteringView()
    {
        if (!mixerView) masteringView = pianoView = false; // back on the timeline, the next Mixer is the mixer
        // in the mixer's place; one in its own window shows there whatever the place does
        bool m = masteringView && mixerView, piano = pianoView && mixerView;
        masteringScroll.IsVisible = masteringPin.IsVisible = m || MasteringOut;
        pianoHost.IsVisible = piano || PianoOut;
        // the mixer in its own window is always on screen there; in the place, when neither of the others is
        mixerPane.IsVisible = MixerOut || (!m && !piano);
        mixerMiniRow.IsVisible = MixerOut;
        MixerViewButton.Classes.Set("on", MixerOut || (mixerView && !m && !piano));
        MasteringViewButton.Classes.Set("on", m || MasteringOut);
        PianoViewButton.Classes.Set("on", piano || PianoOut);
        if (m || MasteringOut)
        {
            if (m || masteringScroll.Content == null) BuildMastering();
            if (m)
            {
                FitMasteringHeight();
                miniTimeline.SetTracks(tracks);
            }
        }
        else
        {
            masteringScroll.Content = masteringPin.Child = null;
            masteringMeter = null;
            masterHistory = null;
        }
        if (!m)
        {
            if (mixerView) SetBottomRowHeight(PlaceH);
            RefreshMixer();
            ApplyMixerMinHeight();
        }
    }

    /// <summary>
    /// The height the Mastering view in the mixer's place was given with the splitters (asked by the user), kept in
    /// config.json ("masteringHeight"), apart from the mixer's; NaN = fitted to its content (<see cref="FitMasteringHeight"/>),
    /// which a double click on the splitter goes back to.
    /// </summary>
    double masteringH = double.NaN;
    /// <summary>A splitter of the mixer's place is being dragged: the Mastering view leaves its height alone until it lets go.</summary>
    bool splitterDragging;
    /// <summary>How low the splitters take the Mastering view: its bar and a row of the chain; the rest scrolls.</summary>
    const double MasteringMinH = 140;

    /// <summary>
    /// The view takes the height it was given (<see cref="masteringH"/>) or, by default, just its content's at its width
    /// (the chain wraps), with the editor as tall as its tallest panel, so picking another never moves it.
    /// </summary>
    void FitMasteringHeight()
    {
        // a splitter being dragged sets the height: fitting it back on every size change fought the drag
        if (splitterDragging || MasteringOut || masteringScroll.Content is not Control body) return;
        if (!double.IsNaN(masteringH))
        {
            SetBottomRowHeight(masteringH, MasteringMinH);
            return;
        }
        double w = masteringScroll.Bounds.Width;
        body.Measure(new Size(w > 0 ? w : double.PositiveInfinity, double.PositiveInfinity));
        double tallest = masterEditor == null ? 0 : Math.Max(0, masterEditor.Tallest - masterEditor.DesiredSize.Height);
        SetBottomRowHeight(Math.Ceiling(body.DesiredSize.Height + tallest + (stackedViews ? 0 : miniRow.Height)), MasteringMinH);
    }

    /// <summary>Room on the content's right for the scroll bar (App.axaml: ScrollBarSize, 8).</summary>
    const double MasteringScrollRoom = 8;

    /// <summary>
    /// The room the scroll bar would take, kept on the content's right while the bar is hidden: the chain and the knobs
    /// wrap by the width, so a bar that came and went with the height changed the width, the wrap and the height again,
    /// and a window being resized flickered between the two.
    /// </summary>
    Thickness MasteringGutter() =>
        new(0, 0, masteringScroll.Extent.Height > masteringScroll.Viewport.Height + 0.5 ? 0 : MasteringScrollRoom, 0);

    /// <summary>The Mastering view in the mixer's place got a height from a splitter (<paramref name="h"/>), or NaN: fitted again.</summary>
    void KeepMasteringHeight(double h)
    {
        masteringH = h > 0 ? h : double.NaN;
        SaveViewPref("masteringHeight", double.IsNaN(masteringH) ? "" : masteringH.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (double.IsNaN(masteringH)) FitMasteringHeight();
    }

    /// <summary>The height of the place the mixer and the mastering share: the bottom row, or the row under the timeline when stacked.</summary>
    void SetBottomRowHeight(double h, double? min = null)
    {
        // it fills the place, whatever its height: maximized, alone under no timeline, or in its own window
        if (maxPanel == MaxPanel.Views || (stackedViews && TimelineOut)) return;
        var row = stackedViews ? AudioArea.RowDefinitions[2] : Root.RowDefinitions[3];
        row.MinHeight = Math.Min(min ?? MixerMinH, Math.Max(0, h));
        row.Height = new GridLength(Math.Max(0, h));
    }

    void MasterEdit(Action<MasterSettings> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, "master:" + key);
        var m = store.Project.Settings.Master;
        string slots = MasterSlotIds;
        change(m);
        engine.ApplyMaster(m);
        MarkDirty();
        UpdateUndoButtons();
        // a new list of effects: the view shows it (the strip rebuilds on its own)
        if (slots != MasterSlotIds && MasteringShown)
        {
            BuildMastering();
            FitMasteringHeight();
        }
        RefreshMasterPanel();
        RefreshMixer();
    }

    string MasterSlotIds => string.Join("/", MasterSet.Slots.Select(s => s.Id));

    /// <summary>A new project or the same one again: the view rebuilds on its settings.</summary>
    void ResetMaster()
    {
        LoudnessSoon();
        if (MasteringShown) BuildMastering();
    }

    /// <summary>The master strip's title: it opens the Mastering view and lights in the accent while the master is on.</summary>
    Button MasterButton()
    {
        var b = new Button
        {
            Classes = { "chip" }, Focusable = false, Height = 22, MinHeight = 22, Padding = new Thickness(8, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Content = new TextBlock { Text = Loc.T("mix.master"), FontSize = 11, FontWeight = FontWeight.SemiBold },
        };
        ToolTip.SetTip(b, Loc.T("master.open.tip"));
        b.Click += (_, _) => ShowBottomView(mastering: true);
        mx.Refresh.Add(() => b.Classes.Set("on", MasterSet.On));
        return b;
    }

    void BuildMastering()
    {
        masterRefresh.Clear();
        moduleMeters.Clear();
        tileMeters.Clear();
        tileOf.Clear();
        masterPages.Clear();
        masterTiles.Clear();
        masterGraph = null;
        var body = MasterBody();
        // a panel that grows (the compressor's fine controls) takes the view with it
        body.SizeChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (masteringScroll.IsVisible) FitMasteringHeight(); });
        masteringScroll.Content = new Border { Child = body, Padding = MasteringGutter() };
        masteringPin.Child = LiveMeter();
        PickModule(masterPick != null && masterPages.Contains(masterPick) ? masterPick : masterPages.FirstOrDefault());
        RefreshMasterPanel();
    }

    /// <summary>
    /// Three rows: the bar (title, the master's ON, "+ Efecto" and the Destino), the chain of tiles (the master's effects,
    /// then Salida) and the editor with the panel of the tile picked. Nothing scrolls sideways: the bar and the chain
    /// wrap to more rows in a narrow window, and the editor's panel lays its knobs out in what it has.
    /// </summary>
    Control MasterBody()
    {
        bool editable = store is { ReadOnly: false };
        var view = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = MasteringPad };

        // the bar: the one ON for the whole chain and a new effect on the left, where the mix is going on the right
        var bar = new ToolbarPanel { Margin = new Thickness(0, 0, 0, 12) };
        var master = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 30 };
        master.Children.Add(new TextBlock { Text = Loc.T("master.title"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        var on = new PowerButton(() => MasterSet.On, () => MasterEdit(m => m.On = !m.On, "on"), _ => Loc.T("master.on.tip")) { IsEnabled = editable, VerticalAlignment = VerticalAlignment.Center };
        masterRefresh.Add(on.Refresh);
        master.Children.Add(on);
        var owner = MasterSlots();
        var addSlot = AddSlotButton(owner);
        addSlot.IsEnabled = editable;
        addSlot.HorizontalAlignment = HorizontalAlignment.Left;
        addSlot.VerticalAlignment = VerticalAlignment.Center;
        addSlot.Margin = new Thickness(6, 0, 0, 0);
        master.Children.Add(addSlot);
        bar.Children.Add(master);
        var target = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, MinHeight = 30 };
        target.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("master.target").ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center });
        var destino = new SelectButton { IsEnabled = editable, Width = 210, VerticalAlignment = VerticalAlignment.Center };
        destino.SetItems(MasterLoudness.All.Select(t => Loc.Or(TargetKey(t.Id), t.Label) + (t.Lufs is { } tl ? $" · {Lufs0(tl)} LUFS" : "")).ToArray());
        ToolTip.SetTip(destino, Loc.T("master.target.tip"));
        destino.Selected += i => MasterEdit(m => m.Target = MasterLoudness.All[i].Id, "target");
        masterRefresh.Add(() => destino.SelectedIndex = Math.Max(0, MasterLoudness.All.ToList().FindIndex(t => t.Id == MasterLoudness.Get(MasterSet.Target).Id)));
        target.Children.Add(destino);
        bar.Children.Add(target);
        view.Children.Add(bar);

        // the chain, in the order the sound goes: the master's effects, then Salida
        var chain = MasterChain(owner);
        chain.Margin = new Thickness(-ChainPanel.Side, 0, -ChainPanel.Side, 12);
        Grid.SetRow(chain, 1);
        view.Children.Add(chain);

        // the editor: the panels of every tile, one in sight
        masterEditor = new PagedPanel();
        foreach (var s in owner.Slots())
            if (s.Known) masterEditor.Children.Add(MasterModule(owner, s));
        masterEditor.Children.Add(ClipPanel());
        masterEditor.Children.Add(LimiterPanel());
        var editor = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 12), Child = masterEditor, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(editor, 2);
        view.Children.Add(editor);

        // off, the master passes the mix through untouched: all of it reads as off
        masterRefresh.Add(() => chain.Opacity = editor.Opacity = MasterSet.On ? 1 : 0.45);
        return view;
    }

    /// <summary>The stages' icons that are not effects (<see cref="FxIcon"/> takes Segoe glyphs too).</summary>
    const string ClipIcon = "";

    /// <summary>
    /// The master's effects as tiles in the order they sound, numbered, each dragged by its grip to another place; then
    /// Salida's two, which never move.
    /// </summary>
    ChainPanel MasterChain(SlotOwner o)
    {
        var slots = o.Slots();
        var chain = new ChainPanel();
        chain.Moved += (id, anchor, after) => SlotsEdit(o, x => ChannelInserts.MoveNextTo(x, id, anchor, after), "move");
        chain.Clicked += PickModule;
        int n = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            string id = slot.Id;
            var grip = new GripDots { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            bool reduces = slot.Type is TrackInserts.Clip or TrackInserts.Limit or TrackInserts.Bands or InsertSlot.CompKind or InsertSlot.GateKind;
            var tile = Tile(id, $"{++n}", slot.Type, SlotTitle(SlotName(slot.Type), nth), SlotHint(slot.Type), () => o.Slot(id)?.On == true,
                () => SlotEdit(o, id, s => s.On = !s.On, "on"), grip, reduces, out var meter);
            if (meter != null) tileOf[id] = meter;
            tile.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right || store == null) return;
                e.Handled = true;
                ShowSlotMenu(o, id, tile, null);
            };
            chain.AddMovable(tile, id, grip);
        }
        if (n == 0)
            chain.Children.Add(new TextBlock { Text = Loc.T("master.empty"), FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center });
        string salida = Loc.T("master.out").ToUpperInvariant();
        chain.Children.Add(Tile(ClipPage, salida, ClipIcon, Loc.T("master.stage.clip"), Loc.T("master.stage.clip.tip") + ". " + Loc.T("master.out.note"),
            () => MasterSet.ClipOn, () => MasterEdit(m => m.ClipOn = !m.ClipOn, "clipOn"), null, true, out clipTile));
        chain.Children.Add(Tile(LimPage, salida, FxIcon.Comp, Loc.T("master.stage.lim"), Loc.T("master.stage.lim.tip") + ". " + Loc.T("master.out.note"),
            () => MasterSet.LimOn, () => MasterEdit(m => m.LimOn = !m.LimOn, "limOn"), null, true, out limTile));
        return chain;
    }

    /// <summary>
    /// One tile: a caption on top (its place in the chain, or Salida), its icon (in the accent while on), its whole name
    /// and ON, and a thin reduction bar along its foot when it has one. A click picks it for the editor; the one picked
    /// takes the accent's edge.
    /// </summary>
    Border Tile(string id, string caption, string icon, string title, string tip, Func<bool> on, Action toggle, Control? grip, bool reduces, out TileMeter? meter)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        if (grip != null) head.Children.Add(grip);
        var mark = FxIcon.Create(icon, 12);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 0, 7, 0);
        masterRefresh.Add(() => FxIcon.SetColor(mark, on() ? Palette.Accent : Palette.Faint));
        Grid.SetColumn(mark, 1); head.Children.Add(mark);
        var name = new TextBlock { Text = title, FontSize = 12, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 2); head.Children.Add(name);
        var chip = new PowerButton(on, toggle) { IsEnabled = store is { ReadOnly: false }, Margin = new Thickness(10, 0, 0, 0) };
        masterRefresh.Add(chip.Refresh);
        Grid.SetColumn(chip, 3); head.Children.Add(chip);
        var top = new TextBlock { Classes = { "label" }, Text = caption, Margin = new Thickness(0, 0, 0, 4) };
        var track = new Border { Height = TileBarH, CornerRadius = new CornerRadius(2), Background = reduces ? Palette.Hover : Brushes.Transparent, Margin = new Thickness(0, 8, 0, 0) };
        meter = null;
        if (reduces)
        {
            var bar = new Border { Width = 0, Height = TileBarH, CornerRadius = new CornerRadius(2), Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Right };
            track.Child = bar;
            ToolTip.SetTip(track, Loc.T("master.gr.tip"));
            meter = new TileMeter(track, bar);
            tileMeters.Add(meter);
        }
        var tile = new Border
        {
            MinWidth = TileMinW, Padding = new Thickness(10, 7, 8, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Background = Palette.Panel, BorderBrush = Palette.Line, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new StackPanel { Children = { top, head, track } },
        };
        ToolTip.SetTip(tile, tip);
        tile.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left && !e.Handled) PickModule(id);
        };
        masterTiles.Add((id, tile));
        return tile;
    }

    /// <summary>Shows a tile's panel in the editor and marks its tile.</summary>
    void PickModule(string? id)
    {
        if (id == null || masterEditor == null) return;
        int i = masterPages.IndexOf(id);
        if (i < 0) return;
        masterPick = id;
        masterEditor.Selected = i;
        foreach (var (tid, tile) in masterTiles)
        {
            bool picked = tid == id;
            tile.BorderBrush = picked ? Palette.Alpha(Palette.AccentColor, 0.7) : Palette.Line;
            tile.Background = picked ? Palette.Raised : Palette.Panel;
        }
    }

    /// <summary>An editor page: its icon and name on top (and the slot's ⋯), the panel under it. Registers it by id.</summary>
    Control EditorPage(string id, string icon, string title, string tip, Func<bool> on, Control body, Control? menu)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 10), MinHeight = 24 };
        var mark = FxIcon.Create(icon, 12);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 0, 7, 0);
        masterRefresh.Add(() => FxIcon.SetColor(mark, on() ? Palette.Accent : Palette.Faint));
        head.Children.Add(mark);
        var name = new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        ToolTip.SetTip(name, tip);
        Grid.SetColumn(name, 1); head.Children.Add(name);
        if (menu != null)
        {
            Grid.SetColumn(menu, 2);
            head.Children.Add(menu);
        }
        masterRefresh.Add(() => body.Opacity = on() ? 1 : 0.45);
        masterPages.Add(id);
        return new StackPanel { Children = { head, body } };
    }

    /// <summary>One effect of the chain in the editor: the panel it has everywhere, with its reduction meters.</summary>
    Control MasterModule(SlotOwner o, InsertSlot slot)
    {
        string id = slot.Id, kind = slot.Type;
        int nth = 1;
        foreach (var s in o.Slots())
        {
            if (s.Id == id) break;
            if (s.Type == kind) nth++;
        }
        string title = SlotTitle(SlotName(kind), nth);
        var tile = tileOf.GetValueOrDefault(id);
        bool bands = kind == TrackInserts.Bands && masterGraph == null;
        var body = new StackPanel { Spacing = 8 };
        FxBody(body, o, slot, masterRefresh, hide: bands ? ["lowX", "highX"] : null);
        if (bands) BandMeters(body, o, id, tile);
        else if (kind is TrackInserts.Clip or TrackInserts.Limit or TrackInserts.Bands or InsertSlot.CompKind or InsertSlot.GateKind)
        {
            body.Children.Add(GrMeter(out var bar, out var text, Loc.T("master.gr.tip")));
            moduleMeters.Add(new ModuleMeter(id, -1, bar, text, tile));
        }
        var menu = SlotMenuButton(o, id);
        var page = EditorPage(id, kind, title, SlotHint(kind), () => o.Slot(id)?.On == true, body, menu);
        // as wide as its knobs in one row, or what the view has (PagedPanel)
        page.MaxWidth = EditorWidth(kind, bands);
        page.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right || store == null || e.Handled) return;
            e.Handled = true;
            ShowSlotMenu(o, id, menu, null);
        };
        return page;
    }

    /// <summary>A panel's width in the editor: its knobs in one row (at least <see cref="EditorMinCols"/> columns' worth), an EQ wider.</summary>
    static double EditorWidth(string kind, bool bands)
    {
        if (kind == InsertSlot.EqKind) return EqEditorW;
        int knobs = FxDefs.Params(kind).Count(p => !p.Fine) + FxDefs.Common(kind).Length - (bands ? 2 : 0);
        return Math.Max(EditorMinCols, knobs) * FxCellW;
    }

    /// <summary>Salida's soft clip: its Recorte and its reduction (it needs the master on, like the rest).</summary>
    Control ClipPanel()
    {
        var clip = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left };
        clip.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { MasterKnob(Loc.T("master.clip.amount"), 0, SoftClipper.MaxDb, 0, () => MasterSet.ClipDb, ClipReadout,
                v => MasterEdit(m => m.ClipDb = Math.Round(v * 10) / 10, "clip"), Loc.T("master.clip.amount.tip")) },
        });
        clip.Children.Add(GrMeter(out var clipBar, out var clipText, Loc.T("master.clip.gr.tip")));
        clip.Children[^1].HorizontalAlignment = HorizontalAlignment.Left;
        masterClipBar = clipBar;
        masterClipText = clipText;
        masterClipShown = double.NaN;
        return EditorPage(ClipPage, ClipIcon, Loc.T("master.stage.clip"), Loc.T("master.stage.clip.tip"), () => MasterSet.ClipOn, clip, null);
    }

    /// <summary>Salida's limiter: Empuje (with a Destino, the measured gain), Techo, Release and its reduction.</summary>
    Control LimiterPanel()
    {
        var lim = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left };
        var limKnobs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        // with a Destino the gain is the measured one: the knob shows it and waits
        limKnobs.Children.Add(MasterKnob(Loc.T("master.lim.drive"), 0, MasterParams.DriveMaxDb, 0,
            () => HasTarget ? Math.Clamp(MasterSet.TargetGainDb, 0, MasterParams.DriveMaxDb) : MasterSet.LimDriveDb,
            () => HasTarget ? Loc.T("master.lim.drive.target", FmtDb(MasterSet.TargetGainDb)) : FmtDb(MasterSet.LimDriveDb),
            v => MasterEdit(m => m.LimDriveDb = Math.Round(v * 2) / 2, "limDrive"), Loc.T("master.lim.drive.tip"), enabled: () => !HasTarget));
        limKnobs.Children.Add(MasterKnob(Loc.T("master.lim.ceiling"), MasterLimiter.CeilingMinDb, 0, MasterLimiter.CeilingDb, () => MasterSet.LimCeilingDb,
            () => $"{MasterSet.LimCeilingDb:0.0} dBTP", v => MasterEdit(m => m.LimCeilingDb = Math.Round(v * 10) / 10, "limCeiling"), Loc.T("master.lim.ceiling.tip")));
        limKnobs.Children.Add(MasterKnob(Loc.T("master.lim.release"), 0, 1, LimReleaseMap.To(MasterLimiter.ReleaseMs), () => LimReleaseMap.To(MasterSet.LimReleaseMs),
            () => $"{MasterSet.LimReleaseMs:0} ms", v => MasterEdit(m => m.LimReleaseMs = Math.Round(LimReleaseMap.From(v)), "limRelease"), Loc.T("master.lim.release.tip")));
        lim.Children.Add(limKnobs);
        lim.Children.Add(GrMeter(out var limBar, out var limText, Loc.T("master.lim.gr.tip")));
        lim.Children[^1].HorizontalAlignment = HorizontalAlignment.Left;
        masterLimBar = limBar;
        masterLimText = limText;
        masterLimShown = double.NaN;
        return EditorPage(LimPage, FxIcon.Comp, Loc.T("master.stage.lim"), Loc.T("master.stage.lim.tip"), () => MasterSet.LimOn, lim, null);
    }

    /// <summary>
    /// The live output, pinned at the view's right: the master strip's meter (L/R, RMS and peak, peak hold and clip
    /// lights) beside the readings of the pass (short-term and integrated LUFS, how far from the Destino, true peak),
    /// the correlation and MONO, and the short-term loudness over the last 30 s against the Destino's line.
    /// </summary>
    Control LiveMeter()
    {
        masteringMeter = new MixerMeter(wide: true) { Margin = new Thickness(0, 0, 12, 0) };
        var readouts = new StackPanel { Spacing = 8, Width = ReadoutsW };
        TextBlock Readout(string label, string unit, string tip, out TextBlock name)
        {
            var value = new TextBlock { Classes = { "mono" }, FontSize = 14, Foreground = Palette.Fg, Text = "—" };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { value, new TextBlock { Text = unit, FontSize = 10, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) } } };
            name = new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint };
            var col = new StackPanel { Spacing = 1, Background = Brushes.Transparent, Children = { name, line } };
            ToolTip.SetTip(col, tip);
            readouts.Children.Add(col);
            return value;
        }
        masterShortText = Readout(Loc.T("master.meter.short"), "LUFS", Loc.T("master.meter.short.tip"), out _);
        masterIntText = Readout(Loc.T("master.meter.int"), "LUFS", Loc.T("master.meter.int.tip"), out _);
        // how far the integrated loudness is from the Destino's (or the social one without it)
        masterToGoText = Readout("", "dB", Loc.T("master.meter.togo.tip"), out var toGoName);
        masterRefresh.Add(() =>
        {
            toGoName.Text = Loc.T("master.meter.togo", Lufs0(GoalLufs)).ToUpperInvariant();
            masterIntShown = double.NaN; // the "to go" is against the new goal
            if (masterHistory != null) masterHistory.Target = GoalLufs;
        });
        masterPeakText = Readout(Loc.T("master.meter.peak"), "dBTP", Loc.T("master.meter.peak.tip"), out _);
        masterCorText = Readout("COR", "", Loc.T("mix.master.cor.tip"), out _);
        masterShortShown = masterIntShown = masterPeakShown = double.NaN;
        masterCorShown = int.MaxValue;
        var mono = StripToggle("MONO", Loc.T("mix.master.mono.tip"), 22, () => engine.MonoCheck, false, () =>
        {
            engine.MonoCheck = !engine.MonoCheck;
            RefreshMasterPanel();
        }, masterRefresh);
        mono.Width = 52;
        mono.HorizontalAlignment = HorizontalAlignment.Left;
        mono.Margin = new Thickness(0, 4, 0, 0);
        readouts.Children.Add(mono);

        // the loudness over time, with what the Destino is doing under it
        masterHistory = new LoudnessHistory { Target = GoalLufs };
        ToolTip.SetTip(masterHistory, Loc.T("master.history.tip"));
        masterTargetText = new TextBlock { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var history = new DockPanel { Width = HistoryW, Margin = new Thickness(12, 0, 0, 0) };
        var historyTitle = new TextBlock { Text = Loc.T("master.history").ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(historyTitle, Dock.Top);
        DockPanel.SetDock(masterTargetText, Dock.Bottom);
        history.Children.Add(historyTitle);
        history.Children.Add(masterTargetText);
        history.Children.Add(masterHistory);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,*") };
        var title = new TextBlock { Classes = { "label" }, Text = Loc.T("master.live").ToUpperInvariant(), MinHeight = 26, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        ToolTip.SetTip(title, Loc.T("master.live.tip"));
        Grid.SetColumnSpan(title, 3); grid.Children.Add(title);
        // the view's top right corner: to its own window and back (MainWindow.PanelWindows.cs)
        var floatButton = MasteringFloatButton();
        Grid.SetColumn(floatButton, 2);
        grid.Children.Add(floatButton);
        Grid.SetRow(masteringMeter, 1); grid.Children.Add(masteringMeter);
        Grid.SetRow(readouts, 1); Grid.SetColumn(readouts, 1); grid.Children.Add(readouts);
        Grid.SetRow(history, 1); Grid.SetColumn(history, 2); grid.Children.Add(history);
        grid.Margin = new Thickness(12, 10);
        var card = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = grid, Margin = new Thickness(StripGap / 2 + 4, MasteringPad.Top, MasteringPad.Right, 0),
        };
        return card;
    }

    /// <summary>A knob of the view: label on top, the knob (<see cref="MasterKnobSize"/>, smaller for the crossovers), the value under it.</summary>
    Control MasterKnob(string label, double min, double max, double reset, Func<double> get, Func<string> readout, Action<double> set, string tip,
        bool small = false, Func<bool>? enabled = null)
    {
        var knob = new Knob(small ? 30 : MasterKnobSize, label) { Min = min, Max = max, ResetTo = reset, HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += set;
        bool editable = store is { ReadOnly: false };
        knob.IsEnabled = editable;
        var value = FxValue();
        masterRefresh.Add(() =>
        {
            knob.Value = get();
            value.Text = readout();
            if (enabled != null) knob.IsEnabled = editable && enabled();
        });
        var col = FxCell(label, knob, value, tip);
        col.MinWidth = small ? 72 : MasterKnobColW;
        col.Width = FxCellW;
        return col;
    }

    /// <summary>A reduction bar with its value under it. It grows from the right, from 0 dB down, as a console's reduction meter.</summary>
    static StackPanel GrMeter(out Border bar, out TextBlock text, string tip)
    {
        var col = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        var track = new Border { Width = GrBarW, Height = GrBarH, CornerRadius = new CornerRadius(2), Background = Palette.Hover, HorizontalAlignment = HorizontalAlignment.Center };
        bar = new Border { Width = 0, Height = GrBarH, CornerRadius = new CornerRadius(2), Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Right };
        track.Child = bar;
        ToolTip.SetTip(track, tip);
        col.Children.Add(track);
        text = new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center, Text = FmtGr(0) };
        col.Children.Add(text);
        return col;
    }

    /// <summary>The first Multibanda's meters: a reduction per band and the Púa, and the curves with the crossovers to drag.</summary>
    void BandMeters(StackPanel body, SlotOwner o, string id, TileMeter? tile)
    {
        var meters = new UniformGrid { Rows = 1 };
        for (int i = 0; i <= MasterBands.Count; i++)
        {
            var meter = GrMeter(out var bar, out var text, Loc.T(i < MasterBands.Count ? "master.gr.tip" : "master.band.pick.gr.tip"));
            meters.Children.Add(meter);
            moduleMeters.Add(new ModuleMeter(id, i, bar, text, tile));
        }
        body.Children.Add(meters);
        var graph = new MasterBandsGraph { IsEnabled = store is { ReadOnly: false } };
        var ps = FxDefs.Params(TrackInserts.Bands);
        FxParam low = ps.First(p => p.Id == "lowX"), high = ps.First(p => p.Id == "highX");
        graph.CrossoverDragged += (isHigh, hz) => FxEdit(o, id, isHigh ? high : low, isHigh ? Math.Round(hz / 100) * 100 : Math.Round(hz));
        masterRefresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            var (lo, hi) = MasterBands.Crossovers(FxDefs.ToMaster(s));
            graph.SetCrossovers(lo, hi);
            graph.Active = MasterSet.On && s.On;
        });
        masterGraph = graph;
        body.Children.Add(graph);
    }

    /// <summary>The Destino's text key ("master.target.asis" for "as it is").</summary>
    static string TargetKey(string id) => "master.target." + (id == MasterLoudness.AsIs ? "asis" : id);
    bool HasTarget => MasterLoudness.Get(MasterSet.Target).Lufs != null;
    /// <summary>What the meter measures against: the Destino's loudness, or the social one without it.</summary>
    double GoalLufs => MasterLoudness.Get(MasterSet.Target).Lufs ?? SocialLufs;
    static string Lufs0(double v) => $"{v:0.#}".Replace('-', '−');

    /// <summary>The Recorte's value: at 0 the clip still shares the hottest peaks with the limiter (<see cref="SoftClipper"/>'s Auto).</summary>
    string ClipReadout() => MasterSet.ClipDb <= 0 ? Loc.T("master.clip.auto") : $"{MasterSet.ClipDb:0.0} dB";

    /// <summary>Under the loudness graph: what the Destino is doing (measuring, the gain it found, or nothing to do).</summary>
    void RefreshTargetStatus()
    {
        if (masterTargetText == null || !masteringScroll.IsVisible) return;
        var t = MasterLoudness.Get(MasterSet.Target);
        masterTargetText.Text = t.Lufs is not { } goal ? Loc.T("master.target.none")
            : loudMeasuring ? Loc.T("master.target.measuring")
            : loudFailed ? Loc.T("master.target.failed")
            : double.IsFinite(loudLufs) ? Loc.T("master.target.done", FmtDb(MasterSet.TargetGainDb), Lufs0(Math.Round(loudLufs, 1)))
            : Loc.T("master.target.kept", FmtDb(MasterSet.TargetGainDb), Lufs0(goal));
    }

    void RefreshMasterPanel()
    {
        if (!masteringScroll.IsVisible) return;
        bool was = syncing;
        syncing = true;
        try { foreach (var r in masterRefresh) r(); }
        finally { syncing = was; }
        RefreshTargetStatus();
    }

    /// <summary>
    /// The meter's readings: short-term and integrated LUFS of the master (the same <see cref="LoudnessMeter"/> as the
    /// transport's LUFS cell, so they start over together each pass), how far from the goal, the pass's true peak, and a
    /// reading for the graph every <see cref="LoudnessHistory.StepSec"/> while it plays. Texts change only by 0,1, so it
    /// does not allocate every frame.
    /// </summary>
    void TickVolume()
    {
        var lufs = engine.Loudness;
        if (double.IsNegativeInfinity(lufs.Integrated))
        {
            masterOutPeak = 0;
            masterHistory?.Clear();
        }
        long now = Environment.TickCount64;
        if (masterHistory != null && engine.Playing && now - masterHistoryAt >= LoudnessHistory.StepSec * 1000)
        {
            masterHistoryAt = now;
            masterHistory.Push(lufs.ShortTerm);
        }
        static bool Changed(double v, ref double shown)
        {
            double r = double.IsFinite(v) ? Math.Round(v * 10) / 10 : double.NegativeInfinity;
            if (r.Equals(shown)) return false;
            shown = r;
            return true;
        }
        static string Lufs(double v) => double.IsFinite(v) ? $"{v:0.0}" : "—";
        if (masterShortText != null && Changed(lufs.ShortTerm, ref masterShortShown)) masterShortText.Text = Lufs(lufs.ShortTerm);
        if (masterIntText != null && Changed(lufs.Integrated, ref masterIntShown))
        {
            masterIntText.Text = Lufs(lufs.Integrated);
            if (masterToGoText != null)
                masterToGoText.Text = double.IsFinite(lufs.Integrated) ? $"{GoalLufs - lufs.Integrated:+0.0;-0.0;0.0}" : "—";
        }
        double peak = masterOutPeak > 0 ? 20 * Math.Log10(masterOutPeak) : double.NegativeInfinity;
        if (masterPeakText != null && Changed(peak, ref masterPeakShown))
        {
            masterPeakText.Text = double.IsFinite(peak) ? $"{peak:0.0}" : "—";
            // over the Techo (plus the meter's rounding) is what the AAC could clip
            masterPeakText.Foreground = peak > MasterSet.LimCeilingDb + 0.05 ? Palette.Rec : Palette.Fg;
        }
        ShowCorrelation(masterCorText, ref masterCorShown);
    }

    /// <summary>From the window tick: the modules' and Salida's live reduction, only while the view is on screen. Text changes only by 0,5 dB.</summary>
    void TickMaster()
    {
        if (!masteringScroll.IsVisible)
        {
            engine.Buses.Clip.TakeReductionDb();
            return;
        }
        Meter(-masterLimDb, ref masterLimShow, ref masterLimShown, masterLimBar, masterLimText);
        Meter(-engine.Buses.Clip.TakeReductionDb(), ref masterClipShow, ref masterClipShown, masterClipBar, masterClipText);
        if (limTile != null) limTile.Db = masterLimShow;
        if (clipTile != null) clipTile.Db = masterClipShow;
        TickVolume();
        // a for over the lists themselves: the tick doesn't allocate
        for (int i = 0; i < tileMeters.Count; i++)
            if (tileMeters[i] != limTile && tileMeters[i] != clipTile) tileMeters[i].Db = 0;
        for (int i = 0; i < moduleMeters.Count; i++)
        {
            var m = moduleMeters[i];
            var insert = engine.Buses.Slots.Find(m.Id);
            double gr = insert == null ? 0 : m.Band < 0 ? insert.TakeGrDb() : insert is FxShell { Inner: BandsInsert bands } ? bands.TakeBandDb(m.Band) : 0;
            Meter(-gr, ref m.Show, ref m.Shown, m.Bar, m.Text);
            if (m.Band is >= 0 and < MasterBands.Count) masterGraph?.SetReduction(m.Band, m.Show);
            if (m.Tile != null) m.Tile.Db = Math.Max(m.Tile.Db, m.Show);
        }
        for (int i = 0; i < tileMeters.Count; i++)
        {
            var t = tileMeters[i];
            double w = Math.Clamp(t.Db / GrBarFullDb, 0, 1) * t.Track.Bounds.Width;
            if (Math.Abs(t.Bar.Width - w) > 0.25) t.Bar.Width = w;
        }

        static void Meter(double gr, ref double show, ref double shown, Border? bar, TextBlock? text)
        {
            show = gr > show ? gr : show + (gr - show) * 0.12;
            if (bar != null) bar.Width = Math.Clamp(show / GrBarFullDb, 0, 1) * GrBarW;
            double s = Math.Round(show * 2) / 2;
            if (s != shown && text != null)
            {
                shown = s;
                text.Text = FmtGr(s);
            }
        }
    }
}
