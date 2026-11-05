using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Port of Mixer.tsx and MiniTimeline.tsx: the bottom view that alternates with the timeline (X). One strip per track,
/// empty ones too ("vacía": what you set there goes to the next take), its effects as a list over the fader at any
/// width (the mixer's zoom); the buses (MainWindow.Buses.cs); the master with its LIM light and its card (MainWindow.Master.cs); pan templates for the whole
/// mix. Every change is an undo step (same keys as the inspector), saved and heard live. Same as the web except: the
/// toggle sits in the bottom transport bar (where the native transport is), the EQ summary has no live spectrum yet
/// (like the inspector), and empty strips edit their channel without the take analysis (as in the web). Also unlike
/// the web (asked by the user): every strip is a card, all strips share the same rows so every meter has the same
/// height, and the strips give way part by part as they narrow (<see cref="NarrowParts"/>) instead of cutting their
/// controls. An effect opens in a small window of its own (<see cref="ShowSlotWindow"/>).
/// Track folders (native only) show as a tinted group with their name above it: a folder strip first (a channel like a
/// bus's: its output, effects, sends, pan, M S and fader over its tracks' sum), then its tracks, unless the group is folded.
/// </summary>
public partial class MainWindow
{
    // Strip widths (px). Tracks and buses take one width, the mixer's zoom (mixerStripW, asked by the user: one flow
    // instead of Compacto / Amplio, and one strip, the list, at every width): MinStripW to WideMax (asked by the user:
    // wider than that adds nothing). Folder strips follow it too.
    const double CompactW = 104, WideMax = 150, MasterW = 76;
    /// <summary>The narrowest strip (px): a slim fader and the meter's bars, the effects as icons, M and S, the badge.</summary>
    const double MinStripW = 42;
    /// <summary>Under this width (px) a compact strip drops its meter's scale and its dB, and tightens its margins.</summary>
    const double NarrowW = 90;
    /// <summary>Under this width (px) a compact strip takes the slim fader and its thinnest margins.</summary>
    const double TinyW = 64;
    /// <summary>The strips' width before the user zooms the mixer (px).</summary>
    const double DefaultStripW = 120;
    /// <summary>How much one wheel notch widens or narrows the strips (the timeline's zoom is 1.18 per notch).</summary>
    const double MixerZoomStep = 1.12;
    /// <summary>Rows every strip shares (takes, returns and master), so all faders and meters start and end at the same
    /// height: head, controls, fader, foot.</summary>
    const double StripHeadH = 38, StripFootH = 22;
    /// <summary>The compact strip's fixed rows (px): a list's title, the pan bar and M S ●.</summary>
    const double ListHeadH = 20, PanRowH = 26, TogglesRowH = 30;
    /// <summary>The default heights of the effects and sends lists (px): four effects and two sends.</summary>
    const double FxListH = 4 * (CompactRowH + 1), SendsListH = 2 * (CompactRowH + 1);
    /// <summary>The most a list can be stretched to (px).</summary>
    const double ListMaxH = 600;

    /// <summary>The least a list section shows (px): one row and the room its grip keeps under it.</summary>
    const double ListMinH = CompactRowH + 6;

    /// <summary>
    /// The compact strips' controls row (px), the same on every strip so faders stay level: the effects and the sends
    /// (each shown or not, at the height it is shown, <see cref="ShownLists"/>) plus pan and M S ●.
    /// </summary>
    double StripTopH
    {
        get
        {
            var (fx, sends) = ShownLists();
            return StripHeadsH + fx + sends + PanRowH + TogglesRowH;
        }
    }

    /// <summary>The titles of the shown list sections (px).</summary>
    double StripHeadsH => (mixerFx ? ListHeadH : 0) + (SendsRow ? ListHeadH : 0);

    /// <summary>What a strip takes besides its fader and its lists (px): card margin and border, the grid's margin, head,
    /// the lists' titles, pan, M S ●, the fader's margin and the foot.</summary>
    double StripBaseH => StripGap * 2 + 2 + 14 + StripHeadH + StripHeadsH + PanRowH + TogglesRowH + 6 + StripFootH;

    /// <summary>
    /// The room the lists have in the mixer's height as it is now (px), leaving <see cref="FaderRoomH"/> to the fader;
    /// unbounded before the strips are measured.
    /// </summary>
    double ListRoom
    {
        get
        {
            double h = mixerScroll.Viewport.Height;
            return h > 0 ? h - StripBaseH - FaderRoomH : double.PositiveInfinity;
        }
    }

    /// <summary>
    /// The effects and sends sections as shown (0 when hidden): the heights the user chose (<see cref="mixerFxH"/>,
    /// <see cref="mixerSendsH"/>), shrunk alike down to <see cref="ListMinH"/> when the mixer is shorter than they and a
    /// usable fader need. The lists never make the mixer taller (asked by the user): it is made taller by hand, and then
    /// they get their height back.
    /// </summary>
    (double Fx, double Sends) ShownLists()
    {
        double fx = mixerFx ? mixerFxH : 0, sends = SendsRow ? mixerSendsH : 0, room = ListRoom;
        if (fx + sends <= room) return (fx, sends);
        double minFx = mixerFx ? ListMinH : 0, minSends = SendsRow ? ListMinH : 0;
        double extra = fx + sends - minFx - minSends;
        double k = extra > 0 ? Math.Clamp((room - minFx - minSends) / extra, 0, 1) : 0;
        return (Math.Floor(minFx + (fx - minFx) * k), Math.Floor(minSends + (sends - minSends) * k));
    }

    /// <summary>The most one list can take now (px): what the mixer's height leaves beside the other one as shown.</summary>
    double ListCap(bool sends)
    {
        var (fx, s) = ShownLists();
        return Math.Max(ListMinH, Math.Min(ListMaxH, ListRoom - (sends ? fx : s)));
    }
    /// <summary>The sends' section takes its room when shown and there is a bus to send to.</summary>
    bool SendsRow => mixerSends && Buses.Count > 0;
    /// <summary>Space between strip cards.</summary>
    const double StripGap = 6;
    /// <summary>The line that sets the pinned master apart (px).</summary>
    const double PinLineW = 2;
    /// <summary>The extra room (px) a folder keeps from a channel or separator beside it.</summary>
    const double FolderGap = 6;
    /// <summary>Below this width an effect panel drops its ON/OFF text and icon, and puts its knob over its other controls.</summary>
    const double FxNarrowW = 150;
    /// <summary>The mixer keeps its own height: its strips want more room than the timeline. The Piano roll and Mastering
    /// views that share its place stop at <see cref="ViewMinH"/>.</summary>
    const double MixerDefaultH = 440, ViewMinH = 250;
    /// <summary>The least a strip's fader gets (px): shorter, the mixer is awkward to use (asked by the user).</summary>
    const double FaderRoomH = 120;
    /// <summary>What the mixer's place takes around the strips (host margin, mini timeline, header, scroll bar) until it is measured.</summary>
    const double MixerChromeGuess = 92;
    /// <summary>More than this around the strips is a measure taken mid-layout, never the real chrome (px).</summary>
    const double MixerChromeMax = 240;
    /// <summary>The share of the window the mixer's minimum never goes past, so a small window can still show the timeline.</summary>
    const double MixerMinShare = 0.7;
    double mixerChromeH = MixerChromeGuess;
    /// <summary>
    /// The mixer's least height: room for every strip's head, a row of each shown list and a usable fader. It doesn't
    /// follow the lists' heights (asked by the user: a list dragged taller never pushes the mixer; past the mixer's
    /// height they shrink, <see cref="ShownLists"/>). Capped in a small window; stacked, by what the timeline above keeps.
    /// </summary>
    double MixerMinH
    {
        get
        {
            if (masteringView || pianoView) return ViewMinH;
            double lists = (mixerFx ? ListMinH : 0) + (SendsRow ? ListMinH : 0);
            double min = Math.Ceiling(mixerChromeH + StripBaseH + lists + FaderRoomH);
            if (stackedViews && AudioArea.Bounds.Height > 0) min = Math.Min(min, AudioArea.Bounds.Height - TimelineMinH);
            else if (Root.Bounds.Height > 0) min = Math.Min(min, Root.Bounds.Height * MixerMinShare);
            return Math.Max(ViewMinH, min);
        }
    }

    /// <summary>
    /// Applies <see cref="MixerMinH"/> to the row the mixer is in (after the lists change, or back from another view). The
    /// row grows to it, but the height the user chose stays: a minimum that shrinks again gives it back.
    /// </summary>
    /// <summary>
    /// What the mixer's place takes besides the strips, as laid out now (the mini timeline comes and goes with the layout).
    /// Not while maximized: there the mini timeline shows and the header fits in one row, and that minimum would bring the
    /// mixer back taller and stuck at it when the panel is restored. Nor in its own window, with its mini timeline.
    /// </summary>
    void MeasureMixerChrome()
    {
        if (!MixerShown || maxPanel != MaxPanel.None || MixerOut || mixerScroll.Viewport.Height <= 0) return;
        double chrome = MixerHost.Bounds.Height + MixerHost.Margin.Top + MixerHost.Margin.Bottom - mixerScroll.Viewport.Height;
        if (chrome <= 0 || chrome > MixerChromeMax || Math.Abs(chrome - mixerChromeH) <= 0.5) return;
        mixerChromeH = chrome;
        ApplyMixerMinHeight();
    }

    void ApplyMixerMinHeight()
    {
        if (!MixerShown || maxPanel == MaxPanel.Views || MixerOut || (stackedViews && !AudioSplitter.IsVisible)) return;
        (stackedViews ? AudioArea.RowDefinitions[2] : Root.RowDefinitions[3]).MinHeight = MixerMinH;
    }

    /// <summary>The strips with what the window updates on them (and an effect's window, <see cref="ShowSlotWindow"/>, its own).</summary>
    sealed class MixerLayout
    {
        /// <summary>Its margin adds to the cards' own, so the first and last strips sit a whole <see cref="StripGap"/> from the edges.</summary>
        public readonly Grid Strips = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(StripGap / 2, 0) };
        /// <summary>The order and sides of its strips, for dragging them (MainWindow.MixerOrder.cs).</summary>
        public readonly MixerOrder Order = new();
        public readonly List<Action> Refresh = [];
        /// <summary>The strips' Transponer tags, refreshed while a transpose is being made (its progress).</summary>
        public readonly List<Action> Transpose = [];
        public readonly Dictionary<string, (MixerMeter Meter, Action<double> Gr)> Meters = [];
        /// <summary>The track and bus strips' cards, resized in place when only the strip width changes.</summary>
        public readonly List<Border> Boxes = [];
        /// <summary>Every strip's controls row and the compact lists' rows with what they hold, resized in place (<see cref="ApplyMixerHeights"/>).</summary>
        public readonly List<RowDefinition> TopRows = [];
        public readonly List<(RowDefinition Row, Control What, bool Head)> FxRows = [], SendRows = [];
        /// <summary>The effects and sends lists themselves, to fit their sections to the longest one (<see cref="FitListH"/>).</summary>
        public readonly List<Panel> FxLists = [], SendLists = [];
        /// <summary>The folders' and buses' meters, by node id (<see cref="Audio.PlaybackEngine.Node"/>), and what writes a bus's "Comprime".</summary>
        public readonly List<(string Id, MixerMeter Meter, Action<double>? Gr)> NodeMeters = [];
        /// <summary>What a strip changes with its width (the zoom) without being rebuilt.</summary>
        public readonly List<Action<double>> Resize = [];
        public MixerMeter? Master;
        public TextBlock? Lim;
        /// <summary>The master's correlation readout and the hundredth it shows (so the text changes only with it).</summary>
        public TextBlock? Cor;
        public int CorHundredths = int.MaxValue;
        /// <summary>The master's short-term LUFS readout and the tenth it shows (int.MaxValue = not yet written).</summary>
        public TextBlock? Lufs;
        public int LufsTenths = int.MaxValue;
    }

    // Built whenever the tracks change, and only resized by the zoom: building a mixer's worth of controls makes the GC
    // stop every thread, the audio callback's too, for 10-40 ms.
    readonly MixerLayout mixerLayout = new();
    /// <summary>The layout being filled: the mixer's, or an effect's window while it is built.</summary>
    MixerLayout mx = null!;
    readonly Panel mixerStrips = new();
    /// <summary>The views that share the mixer's place and their pinned columns; the piano roll and Mastering leave them for their own windows.</summary>
    readonly Panel mixerViews = new(), mixerPins = new();
    readonly ScrollViewer mixerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly MiniTimeline miniTimeline = new();
    /// <summary>The mini timeline's own ruler switch (asked by the user), the same mode as the timeline's (<see cref="SetRulerMode"/>).</summary>
    readonly Button miniBarsButton = RulerButton("", "ruler.bars.tip"), miniTimeButton = RulerButton("", "ruler.time.tip");
    /// <summary>The switch and the mini timeline, the row over the views in the mixer's place.</summary>
    Border miniRow = null!;
    /// <summary>The mixer's own mini timeline, over it in its own window (the place keeps the other for the piano roll and Mastering).</summary>
    readonly MiniTimeline mixerMini = new();
    readonly Button mixerMiniBars = RulerButton("\uEC4F", "ruler.bars.tip"), mixerMiniTime = RulerButton("\uE823", "ruler.time.tip");
    Border mixerMiniRow = null!;
    /// <summary>The mixer itself: head, strips and pinned master, what goes to its own window.</summary>
    readonly DockPanel mixerPane = new();
    Button? panButton;
    string mixerKey = "";
    /// <summary>Every track and bus strip's width (px, <see cref="MinStripW"/>..<see cref="WideMax"/>), kept in config.json ("mixerStripW").</summary>
    double mixerStripW = DefaultStripW;
    /// <summary>The header's zoom bar (like the timeline's) and its icon; the wheel gesture is the timeline's zoom.</summary>
    readonly Slider mixerZoomSlider = new() { Minimum = 0, Maximum = 1, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock mixerZoomIcon = new() { Classes = { "icon" }, Text = "", FontSize = 15, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
    bool mixerZoomSyncing;
    /// <summary>Saves the width once the wheel or the bar stops, not on every notch.</summary>
    readonly DispatcherTimer mixerZoomSaver = new() { Interval = TimeSpan.FromMilliseconds(500) };
    double panWidth = 0.3; // the templates' opening; asked by the user (the web starts at 60%)
    const double TimelineDefaultH = 280, TimelineMinH = 110; // web: TIMELINE_MIN_H (its default is 240; the native layout keeps 280)
    double timelineH = TimelineDefaultH, mixerH = MixerDefaultH, pianoH = MixerDefaultH;

    /// <summary>
    /// The height of what the mixer's place shows now, mixer or piano roll: each keeps its own (asked by the user, #139),
    /// like Mastering (<see cref="masteringH"/>), in config.json ("mixerHeight", "pianoHeight").
    /// </summary>
    double PlaceH
    {
        get => pianoView ? pianoH : mixerH;
        set { if (pianoView) pianoH = value; else mixerH = value; }
    }

    string PlaceKey => pianoView ? "pianoHeight" : "mixerHeight";
    float masterShowL, masterShowR;
    bool mixerView;
    /// <summary>The mixer's head row (title, width…): hidden while the Mastering view uses the mixer's place.</summary>
    Control? mixerHeader;
    /// <summary>The master strip, pinned to the right of the scrolled strips (asked by the user: always in sight). It
    /// lives in <see cref="mixerPane"/>, which decides when the mixer shows, so the column itself is always visible.</summary>
    readonly Border masterPin = Visible(PinnedColumn(StripGap / 2));

    static Border Visible(Border b) { b.IsVisible = true; return b; }

    /// <summary>Preset names and hints come from the engine in Spanish: they are looked up by id (or by name for the buses) and fall back to that text.</summary>
    static string CompPresetName(CompPresetDef p) => Loc.Or("insp.comp." + p.Id, p.Label);
    static string CompPresetHint(CompPresetDef p) => Loc.Or("insp.comp." + p.Id + ".hint", p.Hint);
    static string PresetLabel(string? id) => id == "custom" ? Loc.T("insp.comp.manual") : CompPresets.All.FirstOrDefault(p => p.Id == id) is { } p ? CompPresetName(p) : CompPresets.PresetLabel(id);
    static string EqPresetName(EqPresetDef p) => Loc.Or("mix.eq." + p.Id, p.Label);
    static string EqPresetHint(EqPresetDef p) => Loc.Or("mix.eq." + p.Id + ".hint", p.Hint);
    static string BusPresetName(string kind, string label) => Loc.Or($"mix.{kind}.preset.{label}", label);
    static string BusPresetHint(string kind, string label, string hint) => Loc.Or($"mix.{kind}.preset.{label}.hint", hint);

    static string FmtHz(double v) => v >= 1000 ? $"{v / 1000:0.0}k" : $"{Math.Round(v):0}";

    /// <summary>Knobs for wide ranges move on a log scale (0..1 knob position), like the web's logMap.</summary>
    static (Func<double, double> To, Func<double, double> From) LogMap(double lo, double hi) =>
        (v => Math.Log(Math.Clamp(v, lo, hi) / lo) / Math.Log(hi / lo), p => Math.Round(lo * Math.Pow(hi / lo, p) * 100) / 100);

    static ReverbParams ToParams(ReverbSettings r) => new(r.Decay, r.PreDelayMs, r.Tone, r.LowCut, r.Width, r.ReturnGain, r.Muted);
    static DelayParams ToParams(DelaySettings d) => new(DelayParams.ParseMode(d.Mode), d.Sync, d.Division, d.TimeMs, d.Feedback, d.Tone, d.LowCut, d.Width, d.Duck, d.ReturnGain, d.Muted);

    void InitMixer()
    {
        // before the zoom there were three widths: "Compacto" keeps its narrow strips
        mixerStripW = double.TryParse(AppConfig.Get("mixerStripW"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sw)
            ? Math.Round(Math.Clamp(sw, MinStripW, WideMax)) : AppConfig.Get("mixerWidth") == "compact" ? CompactW : DefaultStripW;
        mixerZoomSaver.Tick += (_, _) =>
        {
            mixerZoomSaver.Stop();
            SaveViewPref("mixerStripW", mixerStripW.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        mixerZoomSlider.ValueChanged += (_, e) => { if (!mixerZoomSyncing) ZoomMixer(MinStripW * Math.Pow(WideMax / MinStripW, e.NewValue)); };
        Avalonia.Automation.AutomationProperties.SetName(mixerZoomSlider, Loc.T("mixer.zoom"));
        mixerZoomIcon.Margin = new Thickness(0, 0, 8, 0);
        mixerScroll.AddHandler(PointerWheelChangedEvent, OnMixerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (double.TryParse(AppConfig.Get("mixerHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h))
            mixerH = Math.Max(MixerMinH, h);
        // until it is given one, the piano roll opens as tall as the mixer did
        pianoH = double.TryParse(AppConfig.Get("pianoHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ph)
            ? Math.Max(MixerMinH, ph) : mixerH;
        if (double.TryParse(AppConfig.Get("masteringHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mh) && mh > 0)
            masteringH = Math.Max(MasteringMinH, mh);
        if (double.TryParse(AppConfig.Get("timelineHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double th))
            timelineH = Math.Max(TimelineMinH, th);
        var bottom = Root.RowDefinitions[3];
        bottom.MinHeight = TimelineMinH;
        bottom.Height = new GridLength(timelineH);
        // Like the web's resize handle: the bottom view keeps its height, double click goes back to the default.
        BottomSplitter.DragStarted += (_, _) => splitterDragging = true;
        AudioSplitter.DragStarted += (_, _) => splitterDragging = true;
        BottomSplitter.DragCompleted += (_, _) =>
        {
            splitterDragging = false;
            SaveBottomHeight();
        };
        AudioSplitter.DragCompleted += (_, _) =>
        {
            splitterDragging = false;
            double h = AudioArea.RowDefinitions[2].ActualHeight;
            if (masteringView) { KeepMasteringHeight(h); return; }
            PlaceH = h;
            SaveViewPref(PlaceKey, h.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        // stacked, the minimum is a share of this place
        AudioArea.SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged) return;
            ApplyMixerMinHeight();
            // a shorter place takes it back under the timeline; a taller one gives it its height again
            if (stackedViews && AudioSplitter.IsVisible && !splitterDragging && mixerView && !masteringView) AudioArea.RowDefinitions[2].Height = new GridLength(StackedMixerH);
        };
        AudioSplitter.DoubleTapped += (_, _) =>
        {
            if (masteringView) { KeepMasteringHeight(double.NaN); return; }
            PlaceH = MixerDefaultH;
            ApplyAudioMixerRows();
            SaveViewPref(PlaceKey, MixerDefaultH.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        BottomSplitter.DoubleTapped += (_, _) =>
        {
            // the Mastering view goes back to fitting its content
            if (MixerHere && masteringView) { KeepMasteringHeight(double.NaN); return; }
            bottom.Height = new GridLength(MixerHere ? MixerDefaultH : TimelineDefaultH);
            Dispatcher.UIThread.Post(SaveBottomHeight, DispatcherPriority.Background);
        };

        foreach (var mini in new[] { miniTimeline, mixerMini })
        {
            mini.SeekRequested += sec => engine.Seek(sec);
            mini.ScrubChanged += Scrub;
        }
        var header = mixerHeader = BuildMixerHeader();
        mx = mixerLayout;
        mixerStrips.Children.Add(mixerLayout.Strips);
        mixerScroll.Content = mixerStrips;
        InitMixerMenu();
        mixerScroll.SizeChanged += (_, _) =>
        {
            mixerStrips.MinWidth = mixerScroll.Viewport.Width;
            // the host's bounds are set after its children's: read here they still hold the last pass's height (the
            // maximized one, coming back from it), so the chrome is measured once the whole layout is done
            Dispatcher.UIThread.Post(MeasureMixerChrome, DispatcherPriority.Background);
            RefreshMixer();
        };
        miniRow = MiniRow(miniTimeline, miniBarsButton, miniTimeButton);
        mixerMiniRow = MiniRow(mixerMini, mixerMiniBars, mixerMiniTime);
        mixerMiniRow.IsVisible = false;
        MixerDock.Children.Add(miniRow);
        // the mixer itself (its head, the strips and the master pinned at their right, outside what scrolls) is one
        // pane, so it goes to its own window alone (asked by the user, #139): the piano roll and Mastering stay
        DockPanel.SetDock(mixerMiniRow, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        Grid.SetColumn(masterPin, 1);
        mixerPane.Children.Add(mixerMiniRow);
        mixerPane.Children.Add(header);
        mixerPane.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { mixerScroll, masterPin } });
        // the DockPanel fills only its last child: the views share it, so each fills the place; Mastering keeps its
        // meter pinned at its right
        var area = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        area.Children.Add(mixerViews);
        mixerViews.Children.Add(mixerPane);
        mixerViews.Children.Add(masteringScroll);
        mixerViews.Children.Add(pianoHost);
        var pins = mixerPins;
        pins.Children.Add(masteringPin);
        Grid.SetColumn(pins, 1);
        area.Children.Add(pins);
        MixerDock.Children.Add(area);
        AlignPin(mixerScroll, masterPin, 0);
        AlignPin(masteringScroll, masteringPin, MasteringPad.Bottom);
        InitMastering();
        InitPiano();
        InitInstruments();
        InitEffects();

        TimelineViewButton.Click += (_, _) => SetMixerView(false);
        MixerViewButton.Click += (_, _) => ToggleMixer();
    }

    /// <summary>A mini timeline with its ruler switch (the same two buttons as the timeline's corner), the row over a view.</summary>
    Border MiniRow(MiniTimeline mini, Button bars, Button time)
    {
        var rulerSwitch = new Border
        {
            Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { bars, time } },
        };
        bars.Click += (_, _) => SetRulerMode(true);
        time.Click += (_, _) => SetRulerMode(false);
        DockPanel.SetDock(rulerSwitch, Dock.Left);
        var row = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Height = mini.Height + 1,
            Child = new DockPanel { Children = { rulerSwitch, mini } },
        };
        DockPanel.SetDock(row, Dock.Top);
        return row;
    }

    /// <summary>The strips are on screen and kept up to date: in the mixer's place (also under the piano roll), or in the mixer's own window.</summary>
    bool StripsLive => MixerOut || (mixerView && !masteringView);

    /// <summary>The mixer itself is on screen, not the Mastering or the Piano roll that share its place.</summary>
    bool MixerShown => mixerView && !masteringView && !pianoView;

    /// <summary>The mixer's place is open in the main window (the mixer, or the piano roll or Mastering there); the mixer's own window doesn't take it.</summary>
    bool MixerHere => mixerView;

    /// <summary>The bottom row has nothing to hold: the timeline is in its own window and the mixer's place is closed or out too.</summary>
    bool BottomEmpty => TimelineOut && !MixerHere;

    /// <summary>The Mixer button and shortcut: like Piano roll and Mastering, pressing the view on screen goes back to the timeline (asked by the user).</summary>
    void ToggleMixer()
    {
        if (ActivatePanel(FloatMixer)) return;
        if (MixerShown && !ViewsCovered) SetMixerView(false);
        else ShowBottomView(mastering: false);
    }

    /// <summary>
    /// The view buttons: the timeline or the mixer's place in the bottom row. A view in its own window comes to the front
    /// instead (the mixer's own window holds only the mixer: the place stays for the piano roll and Mastering).
    /// </summary>
    void SetMixerView(bool mixer)
    {
        UncoverViews();
        if (!mixer && TimelineOut)
        {
            // nothing left in the place: a maximized one goes back first
            if (maxPanel == MaxPanel.Views) SetMaxPanel(MaxPanel.None);
            ActivatePanel(FloatTimeline);
        }
        if (mixer == mixerView) return;
        // stacked (solo audio, Vertical layout) both views fill the place beside the inspector and the bottom row stays closed
        if (!stackedViews) KeepBottomRow();
        mixerView = mixer;
        if (!stackedViews) ApplyBottomRow();
        MixerHost.IsVisible = mixer;
        ApplyViewHosts();
        TimelineViewButton.Classes.Set("on", !mixer);
        MixerViewButton.Classes.Set("on", mixer);
        SaveViewPref(mixer ? "timelineHeight" : PlaceKey, (mixer ? timelineH : PlaceH).ToString(System.Globalization.CultureInfo.InvariantCulture));
        RefreshMixer();
        ApplyMasteringView();
        // with the timeline out, the place opening or closing may give the video its column back or take it
        if (TimelineOut) ApplyLayout();
    }

    /// <summary>Remembers the height of the view on screen (gc.timelineHeight / gc.mixerHeight in the web).</summary>
    void SaveBottomHeight()
    {
        if (stackedViews || BottomEmpty) return;
        double h = Root.RowDefinitions[3].ActualHeight;
        if (MixerHere && masteringView)
        {
            KeepMasteringHeight(h);
            return;
        }
        if (MixerHere) PlaceH = h; else timelineH = h;
        SaveViewPref(MixerHere ? PlaceKey : "timelineHeight", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Before the bottom row changes what it holds: the height of what it holds now (the Mastering view's fitted height is not the mixer's).</summary>
    void KeepBottomRow()
    {
        double h = Root.RowDefinitions[3].ActualHeight;
        if (h <= 0 || BottomEmpty) return;
        if (MixerHere) { if (!masteringView) PlaceH = h; } else timelineH = h;
    }

    /// <summary>The bottom row with the height of what it holds now, closed when it holds nothing.</summary>
    void ApplyBottomRow()
    {
        var row = Root.RowDefinitions[3];
        row.MinHeight = BottomEmpty ? 0 : MixerHere ? MixerMinH : TimelineMinH;
        row.Height = new GridLength(BottomEmpty ? 0 : MixerHere ? PlaceH : timelineH);
    }

    Control BuildMixerHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        // the title (asked by the user: one with some presence): the mixer's glyph in the accent and its name
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Classes = { "icon" }, Text = "\ue9e9", FontSize = 16, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = Loc.T("mixer.title.name"), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(title);
        row.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line });
        row.Children.Add(panButton = BuildPanMenu());
        row.Children.Add(AddSeparatorButton());
        row.Children.Add(SectionSwitch(FxIcon.Eq, "mixer.fx", () => mixerFx, v => mixerFx = v, false));
        row.Children.Add(SectionSwitch(FxIcon.Reverb, "mixer.sends", () => mixerSends, v => mixerSends = v, true));

        // the strips' width, like the timeline's zoom bar (the header's own refreshers survive strip rebuilds)
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children = { mixerZoomIcon, mixerZoomSlider } };
        // the mixer's place to its own window and back (MainWindow.PanelWindows.cs)
        var floatButton = FloatButton(FloatMixer, chip: true);
        floatButton.Margin = new Thickness(12, 0, 0, 0);
        zoom.Children.Add(floatButton);
        headerRefresh.Add(() =>
        {
            mixerZoomSyncing = true;
            mixerZoomSlider.Value = Math.Log(mixerStripW / MinStripW) / Math.Log(WideMax / MinStripW);
            mixerZoomSyncing = false;
        });

        // Cubase's "deactivate all mute / solo states" (native only), lit while any track has one
        var clear = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        clear.Children.Add(FxBypassButton());
        foreach (bool solo in new[] { false, true })
        {
            var b = new Button { Classes = { "chip" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = solo ? "S" : "M", Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold } };
            b.Click += (_, _) => ClearAllToggles(solo);
            headerRefresh.Add(() =>
            {
                bool lit = tracks.Any(t => solo ? t.Channel.Solo : t.Channel.Muted) || Folders.Any(f => solo ? f.Solo : f.Muted);
                b.IsEnabled = store != null && !store.ReadOnly && !restoring && tracks.Any(t => t.Track.HasContent);
                b.Classes.Set("lit", lit);
                b.Background = lit ? (solo ? Palette.Accent : Palette.Mute) : Brushes.Transparent;
                b.BorderBrush = lit ? (solo ? Palette.Accent : Palette.Mute) : Palette.Line;
                ((TextBlock)b.Content!).Foreground = lit ? (solo ? Palette.AccentFg : Palette.App) : Palette.Faint;
                ToolTip.SetTip(b, lit ? Loc.T(solo ? "mixer.clearSolos" : "mixer.clearMutes") : Loc.T(solo ? "timeline.noSolos" : "timeline.noMutes"));
            });
            clear.Children.Add(b);
        }

        // what doesn't fit beside the title goes down a row instead of under it (ToolbarPanel)
        var bar = new ToolbarPanel { Margin = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(row);
        bar.Children.Add(clear);
        // the mix's A | B (MainWindow.MixCompare.cs)
        bar.Children.Add(BuildMixCompare());
        bar.Children.Add(zoom);
        return new Border { MinHeight = 40, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
    }

    readonly List<Action> headerRefresh = [];


    /// <summary>
    /// The mixer's FX switch (asked by the user): every effect of every track and bus in bypass, or back, to hear the mix
    /// without them. Only what you hear (<see cref="Audio.PlaybackEngine.FxBypass"/>): lit amber like a mute, and the
    /// strips' effect lists dim while it is on.
    /// </summary>
    Button FxBypassButton()
    {
        var text = new TextBlock { Text = "FX", Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold };
        var b = new Button { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(4), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = text };
        b.Click += (_, _) => SetFxBypass(!Audio.PlaybackEngine.FxBypass);
        headerRefresh.Add(() =>
        {
            bool off = Audio.PlaybackEngine.FxBypass;
            b.Classes.Set("lit", off);
            b.Background = off ? Palette.Mute : Brushes.Transparent;
            b.BorderBrush = off ? Palette.Mute : Palette.Line;
            text.Foreground = off ? Palette.App : Palette.Faint;
            ToolTip.SetTip(b, Loc.T(off ? "mixer.fxBypass.on.tip" : "mixer.fxBypass.off.tip"));
        });
        return b;
    }

    void SetFxBypass(bool on)
    {
        Audio.PlaybackEngine.FxBypass = on;
        foreach (var t in tracks) t.ApplyInserts();
        engine.ApplyBusSlots();
        engine.ApplyMaster(MasterSet);
        RefreshMixer();
    }

    /// <summary>Whether the strips show their effects and their sends to the buses (the header's switches, kept in config.json).</summary>
    bool mixerFx = AppConfig.Get("mixer.fx") != "off", mixerSends = AppConfig.Get("mixer.sends") != "off";
    /// <summary>The compact lists' heights (px), the same on every strip: the user drags them (<see cref="ListBox"/>).</summary>
    double mixerFxH = ListHeight("mixer.fxH", FxListH), mixerSendsH = ListHeight("mixer.sendsH", SendsListH);

    static double ListHeight(string key, double fallback) =>
        double.TryParse(AppConfig.Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h)
            ? Math.Clamp(h, ListMinH, ListMaxH) : fallback;

    /// <summary>
    /// A switch of the mixer's header (asked by the user): "Efectos" or "Envíos" shows or hides that section on every
    /// strip. <paramref name="key"/> names its text, its tooltips (".show" / ".hide") and its config.json entry. Its
    /// right click sets the section's height on every strip: fitted to the longest list, or back to the default.
    /// </summary>
    ToggleButton SectionSwitch(string iconKey, string key, Func<bool> get, Action<bool> set, bool sends)
    {
        var icon = FxIcon.Create(iconKey, 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var b = new ToggleButton { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon,
                new TextBlock { Text = Loc.T(key), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
        b.Click += (_, _) =>
        {
            set(!get());
            SaveViewPref(key, get() ? "on" : "off");
            ApplyMixerHeights();
            RefreshMixer();
        };
        // the toggle may take the release as its own: listened to even when handled
        b.AddHandler(PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            e.Handled = true;
            void Height(double h)
            {
                // a hidden section comes back to show the height it got
                if (!get()) { set(true); SaveViewPref(key, "on"); }
                SetListH(sends, h, true);
                RefreshMixer();
            }
            OpenAnchoredMenu(Popup(new object[]
            {
                MenuAction(Loc.T("mixer.section.fit"), () => { Height(FitListH(sends)); return Task.CompletedTask; }, icon: ""),
                MenuAction(Loc.T("mixer.section.default"), () => { Height(sends ? SendsListH : FxListH); return Task.CompletedTask; }, icon: ""),
            }, 220), b);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        headerRefresh.Add(() =>
        {
            b.IsChecked = get();
            FxIcon.SetColor(icon, get() ? Palette.Accent : Palette.Faint);
            ToolTip.SetTip(b, Loc.T(get() ? key + ".hide" : key + ".show") + " · " + Loc.T("mixer.section.tip"));
        });
        return b;
    }

    /// <summary>The pan templates in one drop-down with their opening (asked by the user; the web shows a chip per
    /// template and the "Apertura" slider in the header). Picking a template applies it and closes the menu.</summary>
    Button BuildPanMenu()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new TextBlock { Text = Loc.T("mixer.pan"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Classes = { "chip" }, Content = label, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0) };
        ToolTip.SetTip(button, Loc.T("mixer.pan.tip"));
        Avalonia.Automation.AutomationProperties.SetName(button, Loc.T("mixer.pan"));

        var list = new StackPanel { Width = 280, Spacing = 2 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = list };
        var open = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(8, 4, 8, 8) };
        ToolTip.SetTip(open, Loc.T("mixer.pan.width.tip"));
        open.Children.Add(new TextBlock { Text = Loc.T("mixer.pan.width"), FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var width = new InspectorRange { Minimum = 0.1, Maximum = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true, SmallChange = 0.05, Value = panWidth, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(width, Loc.T("mixer.pan.width"));
        var widthText = new TextBlock { Text = $"{panWidth * 100:0}%", Classes = { "mono" }, FontSize = 11, Foreground = Palette.Dim, Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        width.ValueChanged += (_, e) => { panWidth = Math.Round(e.NewValue * 20) / 20; widthText.Text = $"{panWidth * 100:0}%"; };
        Grid.SetColumn(width, 1); open.Children.Add(width);
        Grid.SetColumn(widthText, 2); open.Children.Add(widthText);
        list.Children.Add(open);
        list.Children.Add(new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var t in PanTemplates.All)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = t.Label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg });
            text.Children.Add(new TextBlock { Text = t.Hint, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
            var item = new Button { Classes = { "ghost" }, Content = text, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6) };
            string id = t.Id;
            item.Click += (_, _) => { flyout.Hide(); ApplyPanTemplate(id); };
            list.Children.Add(item);
        }
        button.Click += (_, _) => flyout.ShowAt(button);
        return button;
    }

    void ApplyPanTemplate(string id)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        var takes = tracks.Where(t => t.Track.HasContent).ToList();
        if (takes.Count == 0) return;
        undo.Checkpoint(store.Project);
        var pans = PanTemplates.Positions(id, takes.Count, panWidth);
        for (int i = 0; i < takes.Count; i++) { takes[i].Channel.Pan = pans[i]; takes[i].ApplyChannel(); }
        MarkDirty(); UpdateUndoButtons(); RefreshInspectorText(); Timeline.InvalidateVisual();
    }

    /// <summary>The compressor from a strip: like the inspector, its values follow its preset.</summary>
    void StripComp(LiveTrack t, Action<CompSettings> edit, string key) => EditTrack(t, x =>
    {
        edit(x.Channel.Comp);
        if (x.Channel.Comp.Preset != "custom") x.RecalculateCompPreset();
    }, "comp." + key);

    void RefreshMixer()
    {
        if (mixerView || MasteringOut) miniTimeline.SetTracks(tracks);
        if (MixerOut) mixerMini.SetTracks(tracks);
        // the effects' windows follow their slots wherever the mixer is (with the strips, at the end)
        if (!StripsLive)
        {
            RefreshSlotWindows();
            return;
        }
        // tracks and buses take the zoom's width (folder strips, separators and the master keep their own)
        double stripW = mixerStripW;
        // a new width or layout only resizes the cards or shows the other layout (see compactMixer)
        string key = string.Join(",", tracks.Select(t => t.Track.Id + ":" + t.Clips.Count + ":" + t.Track.FolderId + ":" + t.Track.MixerSide + ":" + string.Join("/", t.Channel.Slots.Select(s => s.Id))
                + ":" + string.Join("/", t.Channel.Sends.Where(s => !s.Hidden).Select(s => s.BusId))))
            + "|" + string.Join(",", Folders.Select(f => f.Id + ":" + f.Index + ":" + f.MixerCollapsed + ":" + f.Separator + ":" + f.MixerSide + ":" + string.Join("/", f.Sends.Where(s => !s.Hidden).Select(s => s.BusId))
                + ":" + string.Join("/", f.Inserts.Select(s => s.Id + s.Type))))
            // a bus's routing changes which buses the others can send to: their send panels come and go
            + "|" + string.Join(",", Buses.Select(b => b.Id + ":" + b.Name + ":" + b.MixerSide + ":" + b.Output + ":" + string.Join("/", b.Sends.Select(x => x.BusId)) + ":" + string.Join("/", b.Inserts.Select(s => s.Id + s.Type))))
            + "|" + string.Join("/", MasterSet.Slots.Select(s => s.Id + s.Type));
        if (key != mixerKey)
        {
            mixerKey = key;
            BuildStrips(stripW);
        }
        mx = mixerLayout;
        ApplyMixerHeights();
        foreach (var box in mx.Boxes) box.Width = stripW;
        foreach (var resize in mx.Resize) resize(stripW);
        bool was = syncing;
        syncing = true;
        try
        {
            bool editable = store != null && !store.ReadOnly && !restoring;
            mixerStrips.IsEnabled = masterPin.IsEnabled = editable;
            if (panButton != null) panButton.IsEnabled = editable && tracks.Any(t => t.Track.HasContent);
            foreach (var r in headerRefresh) r();
            foreach (var r in mx.Refresh) r();
        }
        finally { syncing = was; }
        RefreshSlotWindows();
    }

    /// <summary>
    /// The mixer's zoom (asked by the user: one flow, no Compacto / Amplio): every track and bus strip takes width
    /// <paramref name="w"/>, kept between <see cref="MinStripW"/> and <see cref="WideMax"/>, without rebuilding them.
    /// What sits under <paramref name="atX"/> (px from the view's left; its middle by default) stays there, as the
    /// timeline's zoom keeps the playhead.
    /// </summary>
    void ZoomMixer(double w, double? atX = null)
    {
        w = Math.Round(Math.Clamp(w, MinStripW, WideMax));
        if (w == mixerStripW || !StripsLive) return;
        double x = atX ?? mixerScroll.Viewport.Width / 2;
        // the strip under that point and where in it; elsewhere (a folder strip, a gap) the same share of the whole width
        int under = -1;
        double frac = 0, share = mixerStrips.Bounds.Width > 0 ? (mixerScroll.Offset.X + x) / mixerStrips.Bounds.Width : 0;
        for (int i = 0; i < mx.Boxes.Count; i++)
        {
            var box = mx.Boxes[i];
            if (!box.IsEffectivelyVisible || box.Bounds.Width <= 0 || box.TranslatePoint(default, mixerScroll) is not { } p) continue;
            if (x < p.X || x >= p.X + box.Bounds.Width) continue;
            under = i;
            frac = (x - p.X) / box.Bounds.Width;
            break;
        }
        mixerStripW = w;
        mixerZoomSaver.Stop();
        mixerZoomSaver.Start();
        RefreshMixer();
        mixerScroll.UpdateLayout();
        double content = under >= 0 && under < mx.Boxes.Count && mx.Boxes[under].TranslatePoint(default, mixerStrips) is { } at
            ? at.X + frac * mx.Boxes[under].Bounds.Width
            : share * mixerStrips.Bounds.Width;
        mixerScroll.Offset = new Vector(Math.Clamp(content - x, 0, Math.Max(0, mixerScroll.Extent.Width - mixerScroll.Viewport.Width)), mixerScroll.Offset.Y);
    }

    /// <summary>
    /// The mixer's wheel, with the timeline's modifiers (<see cref="Shortcuts.Wheel"/>): its zoom widens or narrows the
    /// strips around the pointer; its sideways gesture, and the plain wheel where nothing under the pointer scrolls,
    /// move the mixer sideways. Over a knob, fader or bar the wheel is theirs (Shift = fine), as over the timeline's knobs.
    /// </summary>
    void OnMixerWheel(object? sender, PointerWheelEventArgs e)
    {
        string? wheel = Shortcuts.FindWheel(e.KeyModifiers);
        bool listScrolls = false;
        for (var v = e.Source as Visual; v != null && v != mixerScroll; v = v.GetVisualParent())
        {
            if (v is Knob or Fader or HBar or EqEditor) return;
            if (v is ScrollViewer s && s.Extent.Height > s.Viewport.Height + 0.5) listScrolls = true;
        }
        // some mice and drivers turn a modified wheel into a sideways delta, so take whichever axis moved
        double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (wheel == "wheelZoom") ZoomMixer(mixerStripW * Math.Pow(MixerZoomStep, d * Shortcuts.WheelSign(wheel)), e.GetPosition(mixerScroll).X);
        // wheel up goes right, as in the timeline (reversed: down)
        else if (wheel == "wheelSideways") ScrollMixerBy((e.Delta.Y + e.Delta.X) * Shortcuts.WheelSign(wheel) * 80);
        // a list or an effects column that has more to show takes the plain wheel; a touchpad's sideways swipe never
        else if (wheel == null && (!listScrolls || e.Delta.Y == 0)) ScrollMixerBy(-(e.Delta.Y * Shortcuts.WheelSign(Shortcuts.WheelScroll) + e.Delta.X) * 80);
        else return;
        e.Handled = true;
    }

    void ScrollMixerBy(double dx) =>
        mixerScroll.Offset = new Vector(Math.Clamp(mixerScroll.Offset.X + dx, 0, Math.Max(0, mixerScroll.Extent.Width - mixerScroll.Viewport.Width)), mixerScroll.Offset.Y);

    /// <summary>Called from the window tick: meters, compressor reduction, LIM and the mini timeline's playhead.</summary>
    void TickMixer(double pos, float outL, float outR)
    {
        double lim = masterLimDb = engine.Limiter.TakeReductionDb();
        masterOutPeak = Math.Max(masterOutPeak, engine.TruePeak.TakeMax());
        TakeMixerRms();
        if (!mixerView && !MasteringOut && !MixerOut) return;
        // the mini timeline sits over the Mastering view too
        double end = TracksEndSec();
        miniTimeline.Bpm = Timeline.Bpm;
        miniTimeline.SetPlayhead(pos, end, RecordingBusy);
        if (MixerOut)
        {
            mixerMini.Bpm = Timeline.Bpm;
            mixerMini.SetPlayhead(pos, end, RecordingBusy);
        }
        masterShowL = Math.Max(outL, masterShowL * 0.82f); masterShowR = Math.Max(outR, masterShowR * 0.82f);
        double rmsL = Math.Sqrt(engine.OutRms.MeanSqL), rmsR = Math.Sqrt(engine.OutRms.MeanSqR);
        // Mastering in its own window keeps its meter going beside the mixer
        masteringMeter?.Set(masterShowL, masterShowR, rmsL, rmsR);
        if (!StripsLive) return;
        foreach (var t in tracks)
            if (mx.Meters.TryGetValue(t.Track.Id, out var m))
            {
                m.Meter.Mono = TrackMono(t);
                m.Meter.Set(t.ShowL, t.ShowR, Math.Sqrt(t.Chain.Rms.MeanSqL), Math.Sqrt(t.Chain.Rms.MeanSqR));
                m.Gr(t.GrShow);
            }
        mx.Master?.Set(masterShowL, masterShowR, rmsL, rmsR);
        TickNodes();
        ShowMasterLufs();
        ShowCorrelation(mx.Cor, ref mx.CorHundredths);
        if (mx.Lim != null) mx.Lim.Opacity = lim < -0.5 ? 1 : 0.25;
    }

    long rmsStamp;

    /// <summary>
    /// Where the last track ends (<see cref="LiveTrack.EndSec"/> of the longest; 0 without tracks), for the tick: by
    /// index, because EndSec's LINQ over an IReadOnlyList allocates an enumerator per track.
    /// </summary>
    double TracksEndSec()
    {
        if (tracks.Count == 0) return 0;
        double end = double.NegativeInfinity;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var clips = t.Clips;
            double clipsEnd = 0;
            if (clips.Count > 0)
            {
                clipsEnd = double.NegativeInfinity;
                for (int k = 0; k < clips.Count; k++) clipsEnd = Math.Max(clipsEnd, clips[k].ClipEndSec);
            }
            end = Math.Max(end, Math.Max(t.MidiEndSec, clipsEnd));
        }
        return end;
    }

    /// <summary>
    /// Whether a track's strip shows one bar: every take it plays is mono (<see cref="MediaAudio.Mono"/>); with none, its
    /// input is (a single one, or Configuración's input in a mono mode). Runs in the tick, so it allocates nothing.
    /// </summary>
    bool TrackMono(LiveTrack t)
    {
        int online = 0;
        var clips = t.Clips;
        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i].Audio is not { } a) continue;
            if (!a.Mono) return false;
            online++;
        }
        if (online > 0) return true;
        return t.Track.Input is { } input ? !input.Contains('-') : engine.InputChannelMode != XtremeDaw.Audio.InputChannelMode.Stereo;
    }

    /// <summary>
    /// Integrates every meter's RMS (<see cref="RmsTap"/>) once per tick, shown or not, so a meter that comes back
    /// into view starts from the last 300 ms and not from everything that played while it was hidden.
    /// </summary>
    void TakeMixerRms()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = rmsStamp == 0 ? 0 : (now - rmsStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        rmsStamp = now;
        for (int i = 0; i < tracks.Count; i++) tracks[i].Chain.Rms.Take(dt);
        engine.OutRms.Take(dt);
        engine.Correlation.Take(dt);
        TakeNodesRms(dt);
    }

    /// <summary>The master strip's short-term LUFS: the transport's live measurement (<see cref="Audio.PlaybackEngine.Loudness"/>),
    /// not a second meter. The text changes only with the shown tenth.</summary>
    void ShowMasterLufs()
    {
        if (mx.Lufs == null) return;
        int s = Tenths(engine.Loudness.ShortTerm);
        if (s == mx.LufsTenths) return;
        mx.LufsTenths = s;
        mx.Lufs.Text = s == int.MinValue ? "—" : $"{s / 10.0:0.0}";
    }

    /// <summary>
    /// The master's COR: the correlation in tenths, red under zero (what cancels in mono), amber under +0.3 (wide), dim
    /// otherwise. Changes only with the shown hundredth, so the tick allocates nothing while it holds.
    /// </summary>
    static readonly IBrush CorWide = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Palette.MeterYellow);

    /// <summary>Writes the output's correlation in <paramref name="text"/> (the master strip's or the Mastering meter's).</summary>
    void ShowCorrelation(TextBlock? text, ref int shown)
    {
        if (text == null) return;
        double c = engine.Correlation.Value;
        int h = double.IsNaN(c) ? int.MinValue : (int)Math.Round(c * 10) * 10;
        if (h == shown) return;
        shown = h;
        text.Text = h == int.MinValue ? "—" : h == 0 ? "0.0" : $"{h / 100.0:+0.0;-0.0}";
        text.Foreground = h == int.MinValue ? Palette.Faint : h < 0 ? Palette.Rec : h < 30 ? CorWide : Palette.Dim;
    }

    // --- strips ----------------------------------------------------------------------------------------

    /// <summary>Empties a layout: its controls and everything the window updates on them.</summary>
    static void ClearStrips(MixerLayout l)
    {
        l.Refresh.Clear();
        l.Transpose.Clear();
        l.Meters.Clear();
        l.Boxes.Clear();
        l.TopRows.Clear();
        l.FxRows.Clear();
        l.SendRows.Clear();
        l.FxLists.Clear();
        l.SendLists.Clear();
        l.NodeMeters.Clear();
        l.Resize.Clear();
        l.Order.Top.Clear();
        l.Order.Inner.Clear();
        l.Order.FolderStrips.Clear();
        l.Strips.Children.Clear();
    }

    void BuildStrips(double stripW)
    {
        mx = mixerLayout;
        ClearStrips(mixerLayout);
        var order = mx.Order;
        var mixerStrips = mx.Strips;
        // the two sides (asked by the user): tracks and folders on the left by default, buses on the right, and
        // anything can be moved to the other one; the free space between them grows with the window
        var strips = order.Left = new StackPanel { Orientation = Orientation.Horizontal };
        var returns = order.Right = new StackPanel { Orientation = Orientation.Horizontal };
        mixerStrips.Children.Add(strips);
        Grid.SetColumn(returns, 2); mixerStrips.Children.Add(returns);
        if (tracks.Count == 0)
        {
            var empty = new TextBlock { Text = Loc.T("mixer.empty"), FontSize = 12, Foreground = Palette.Faint,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(empty, 1);
            mixerStrips.Children.Add(empty);
        }
        // with folders every column gets the band their names sit in, so all faders stay level
        bool bands = Folders.Any(f => !f.Separator);
        Control Column(Control c) => bands ? Banded(null, c) : c;
        void Put(Control column, object item, bool right)
        {
            (right ? returns : strips).Children.Add(column);
            order.Top.Add((column, item, right));
        }
        foreach (var item in store == null ? [] : TrackFolders.Sequence(tracks.Select(t => t.Track).ToList(), Folders))
        {
            if (item is Folder { Separator: true } sep)
            {
                var card = SeparatorStrip(sep);
                MixerDraggable(card, sep);
                Put(Column(card), sep, MixerSides.IsRight(sep));
            }
            else if (item is Folder f) Put(FolderGroup(f, stripW), f, MixerSides.IsRight(f));
            else if (item is XtremeDaw.Projects.Track { FolderId: null } tr && tracks.FirstOrDefault(t => t.Track == tr) is { } lt)
            {
                var card = Strip(lt, stripW);
                MixerDraggable(card, tr);
                Put(Column(card), tr, MixerSides.IsRight(tr));
            }
        }

        // the buses, on their side after the tracks (a new one comes from the mixer's right-click menu)
        foreach (var b in Buses)
        {
            var card = BusStrip(b, stripW);
            MixerDraggable(card, b);
            Put(Column(card), b, MixerSides.IsRight(b));
        }
        // a folder keeps a little room from the column beside it (asked by the user), not from the mixer's start nor
        // the free space: on the left side, from its neighbours; on the right one, also from the master after it
        foreach (var (side, right) in new[] { (strips, false), (returns, true) })
            for (int i = 0; i < side.Children.Count; i++)
            {
                var column = side.Children[i];
                if (order.Top.Find(x => x.Column == column).Item is Folder { Separator: false })
                    column.Margin = new Thickness(i > 0 ? FolderGap : 0, 0, right || i < side.Children.Count - 1 ? FolderGap : 0, 0);
            }
        masterPin.Child = Column(Master());
    }

    /// <summary>The column a view pins at its right, apart from what scrolls beside it by a line heavier than any other
    /// border (asked by the user): it divides the view, it doesn't outline a card. <paramref name="right"/>: its room at the edge.</summary>
    static Border PinnedColumn(double right = 0) => new()
    {
        BorderBrush = Palette.Faint, BorderThickness = new Thickness(PinLineW, 0, 0, 0), Padding = new Thickness(StripGap / 2, 0, right, 0), IsVisible = false,
    };

    /// <summary>
    /// Keeps a pinned column's foot level with the scrolled content beside it: when the horizontal scroll bar shows,
    /// the content loses its height and the column has to lose it too, or its meter would end lower than the others.
    /// </summary>
    static void AlignPin(ScrollViewer scroll, Control pin, double bottom)
    {
        void Align() => pin.Margin = new Thickness(0, 0, 0, bottom + Math.Max(0, scroll.Bounds.Height - scroll.Viewport.Height));
        scroll.ScrollChanged += (_, _) => Align();
        scroll.SizeChanged += (_, _) => Align();
    }

    /// <summary>A strip as a card on the mixer's background, so each channel reads apart from its neighbours.</summary>
    static Border StripCard(double width) => new()
    {
        Width = width, Margin = new Thickness(StripGap / 2, StripGap), Background = Palette.Panel, BorderBrush = Palette.Line,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true,
    };

    /// <summary>Puts a strip's parts on the rows every strip shares (<see cref="StripHeadH"/>…), so all faders and meters
    /// start and end at the same height whatever the name, the strip type or its controls.</summary>
    Grid StripLayout(Control head, Control? controls, Control faders, Control foot)
    {
        var g = new Grid { Margin = new Thickness(6, 8, 6, 6) };
        g.RowDefinitions.Add(new RowDefinition(StripHeadH, GridUnitType.Pixel));
        var top = new RowDefinition(StripTopH, GridUnitType.Pixel);
        mx.TopRows.Add(top);
        g.RowDefinitions.Add(top);
        g.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        g.RowDefinitions.Add(new RowDefinition(StripFootH, GridUnitType.Pixel));
        void Put(Control c, int row)
        {
            Grid.SetRow(c, row);
            g.Children.Add(c);
        }
        head.VerticalAlignment = VerticalAlignment.Top;
        foot.VerticalAlignment = VerticalAlignment.Bottom;
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        Put(head, 0);
        if (controls != null) { controls.VerticalAlignment = VerticalAlignment.Top; Put(controls, 1); }
        faders.HorizontalAlignment = HorizontalAlignment.Center;
        faders.Margin = new Thickness(0, 6, 0, 0);
        Put(faders, 2);
        Put(foot, 3);
        return g;
    }

    Control Strip(LiveTrack t, double width)
    {
        var box = StripCard(width);
        mx.Boxes.Add(box);
        var top = StripCardLook(box, () => sel == t, () => Timeline.ArmedIndices.Contains(tracks.IndexOf(t)), () => TrackIcons.ColorOf(t.Track), () =>
        {
            int i = tracks.IndexOf(t);
            if (i >= 0 && sel != t) Timeline.Select(i);
        });

        bool empty = !t.Track.HasContent;
        bool Silent() => empty || t.Silenced || (tracks.Any(x => x.Track.HasContent && x.Soloed) && !t.Soloed);
        var name = new TextBlock { Text = t.Track.Name, FontSize = 11, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Background = Brushes.Transparent };
        mx.Refresh.Add(() => { name.Text = t.Track.Name; name.Foreground = Silent() ? Palette.Faint : Palette.Fg; });
        RenameOnDoubleClick(name, () => RenameTrackIn(t, name));
        box.PointerReleased += (_, e) =>
        {
            if (e.Handled || e.InitialPressMouseButton != MouseButton.Right || store == null || restoring) return;
            e.Handled = true;
            int i = tracks.IndexOf(t);
            if (i >= 0 && sel != t) Timeline.Select(i);
            var items = new List<object> { new MenuItem { Header = t.Track.Name, IsEnabled = false, FontSize = 11 } };
            items.AddRange(TrackMenuItems(t, box, null, () => RenameTrackIn(t, name), look: true));
            OpenAnchoredMenu(Popup(items, 240), box);
        };
        var emptyHint = new TextBlock { Text = Loc.T("mixer.emptyTag"), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, IsVisible = empty, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(emptyHint, Loc.T("mixer.emptyTag.tip"));
        // armed, the strip shows where it records from in the place of "VACÍA" (native only)
        var input = InputChip(t);
        mx.Refresh.Add(() =>
        {
            bool armed = Timeline.ArmedIndices.Contains(tracks.IndexOf(t)) && !RecordingTake;
            input.IsVisible = armed;
            emptyHint.IsVisible = empty && !armed;
        });
        var badge = new TrackBadge { Width = 22, Height = 22 };
        badge.Clicked += () => ShowTrackLook(t, badge);
        mx.Refresh.Add(() => badge.Set(t.Track, tracks.IndexOf(t) + 1, sel == t, !AudioOnly));
        var fader = new Fader();
        fader.Changed += v => EditTrack(t, x => x.Channel.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        // the readout is a function of the volume alone: rebuilt only when it changes, since a lane moves it every tick
        double dbShown = double.NaN;
        void ShowDb(double v)
        {
            if (v == dbShown) return;
            dbShown = v;
            db.Text = ValueFormat.Db(v);
        }
        mx.Refresh.Add(() => { fader.Value = t.Channel.Volume; ShowDb(t.Channel.Volume); });
        AutomateOn(fader, () => t.Track, AutoTarget.Volume, k =>
        {
            fader.Value = Fader.ToVol(k);
            ShowDb(Fader.ToVol(k));
        });
        var meter = new MixerMeter();
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        faders.Children.Add(fader); faders.Children.Add(meter);

        // its icon, its name and "VACÍA", its input while armed or its transpose under the name
        var under = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        under.Children.Add(emptyHint); under.Children.Add(input); under.Children.Add(TransposeTagText(t));
        var head = StripHead(badge, name, under);
        // like a console's strip (asked by the user): its effects and its sends as lists, the pan as a bar, then M S ●
        var (controls, gr) = CompactChain(SlotsOf(t), RouterOf(t), () => t.Channel.Pan, v => EditTrack(t, x => x.Channel.Pan = v, "pan"),
            StripToggle("M", Shortcuts.Tip(Loc.T("track.mute"), "mute"), 26, () => t.Channel.Muted, true, () => EditTrack(t, x => x.Channel.Muted = !x.Channel.Muted, "mute")),
            StripToggle("S", Shortcuts.Tip(Loc.T("track.solo"), "solo"), 26, () => t.Channel.Solo, false, () => EditTrack(t, x => x.Channel.Solo = !x.Channel.Solo, "solo")),
            ArmToggle(t, 26));
        mx.Meters[t.Track.Id] = (meter, gr);
        var content = new Grid();
        var layout = StripLayout(head, controls, faders, db);
        NarrowParts(layout, faders, fader, meter, db);
        content.Children.Add(layout);
        content.Children.Add(top);
        box.Child = content;
        return box;
    }

    /// <summary>
    /// A track's or a bus's head, in its fixed row (asked by the user: the channel's icon always in sight, its size
    /// following the strip): the badge beside the name and the line under it, or, under <see cref="HeadNameW"/>, the
    /// badge over the name, both centred (the line under the name gives way).
    /// </summary>
    Control StripHead(TrackBadge badge, TextBlock name, Control under)
    {
        var text = new StackPanel { Spacing = 1, Children = { name, under } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = StripHeadH };
        g.Children.Add(badge);
        g.Children.Add(text);
        mx.Resize.Add(w =>
        {
            bool stacked = w < HeadNameW;
            // stacked, the badge stays small so the name still fits under it in the fixed row
            badge.Width = badge.Height = stacked ? StackedBadge : Math.Clamp(Math.Round(16 + w * 0.08), 22, 28);
            Grid.SetColumnSpan(badge, stacked ? 2 : 1);
            badge.HorizontalAlignment = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            badge.VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center;
            Grid.SetColumn(text, stacked ? 0 : 1);
            Grid.SetColumnSpan(text, stacked ? 2 : 1);
            text.VerticalAlignment = stacked ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            text.Margin = new Thickness(stacked ? 0 : 7, 0, 0, 0);
            // the name under the badge, smaller and centred; the line under it (VACÍA, input, BUS) gives way
            name.FontSize = stacked ? 10 : 11;
            name.TextAlignment = stacked ? TextAlignment.Center : TextAlignment.Left;
            under.IsVisible = !stacked;
        });
        return g;
    }

    /// <summary>A compact strip's parts that give way as it narrows: under <see cref="NarrowW"/> the meter's scale, the
    /// dB under the fader and some margin; under <see cref="TinyW"/> the fader's cap slims down.</summary>
    void NarrowParts(Grid layout, StackPanel? faders, Fader? fader, MixerMeter meter, Control db) => mx.Resize.Add(w =>
    {
        bool narrow = w < NarrowW, tiny = w < TinyW;
        meter.Scale = !narrow;
        db.IsVisible = !narrow;
        // a strip without a fader (none left: kept for one that has only its meter)
        if (fader != null) fader.Slim = tiny;
        if (faders != null) faders.Spacing = tiny ? 2 : 4;
        double side = tiny ? 2 : narrow ? 3 : 6;
        layout.Margin = new Thickness(side, 8, side, 6);
    });

    /// <summary>
    /// A strip's card look, the same for every channel (asked by the user: a bus selects like a track): hover, selected
    /// (raised, accent border), armed (tinted red), and its color along the top edge. A press selects it.
    /// Returns the top edge, which the strip lays over its content.
    /// </summary>
    Border StripCardLook(Border box, Func<bool> selected, Func<bool> armed, Func<Color?> color, Action select)
    {
        var top = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        box.Cursor = new Cursor(StandardCursorType.Hand);
        bool hover = false;
        box.PointerEntered += (_, _) => { hover = true; Paint(); };
        box.PointerExited += (_, _) => { hover = false; Paint(); };
        box.PointerPressed += (_, e) => { if (e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) select(); };
        void Paint()
        {
            bool on = selected(), rec = armed();
            // the armed strip (where R records) is tinted red, like its lane in the timeline
            box.Background = rec && !on ? Palette.Alpha(Palette.RecColor, 0.08)
                : on ? Palette.Raised : hover ? Palette.Alpha(Palette.RaisedColor, 0.6) : Palette.Panel;
            box.BorderBrush = rec ? Palette.Alpha(Palette.RecColor, 0.5) : on ? Palette.Accent : Palette.Line;
            // a channel with a color shows it along the card's top edge, selected or not
            top.Background = color() is { } c ? Palette.Alpha(c, 1) : Brushes.Transparent;
        }
        mx.Refresh.Add(Paint);
        return top;
    }

    /// <summary>A title in the mixer: a double click renames. Caught on its way down, because the press also lands on a
    /// card that captures the pointer to drag and the gesture's double tap can miss it.</summary>
    static void RenameOnDoubleClick(Control title, Action rename) => title.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
    {
        if (e.ClickCount != 2 || !e.GetCurrentPoint(title).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        rename();
    }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

    /// <summary>A track's name from the mixer: a double click on it asks for the new one (one undo step).</summary>
    void RenameTrackIn(LiveTrack t, Control anchor)
    {
        if (store?.ReadOnly != false || restoring) return;
        AskName(anchor, null, Loc.T("menu.renameTrack"), t.Track.Name, Loc.T("common.ok"), n =>
        {
            if (n.Trim() is { Length: > 0 } name && name != t.Track.Name) EditTrack(t, x => x.Track.Name = name, "name");
        });
    }

    /// <summary>Height (px) of a row of the compact strip's lists.</summary>
    const double CompactRowH = 18;
    /// <summary>The compact strip's M S ● at their usual width (px); narrower strips squeeze them down to <see cref="MinToggleW"/>.</summary>
    const double ToggleMaxW = 26, MinToggleW = 16;
    /// <summary>
    /// What a compact row shows by its own width (px): its icon always; its power switch from <see cref="RowPowerW"/>
    /// (under it a click opens the effect, where it is switched); its name from <see cref="RowNameW"/>; and what the
    /// effect is set to from <see cref="RowBriefW"/>, in at most <see cref="RowBriefShare"/> of the row. A list's title
    /// shows from <see cref="RowNameW"/>.
    /// </summary>
    const double RowPowerW = 40, RowNameW = 58, RowBriefW = 110, RowBriefShare = 0.42;
    /// <summary>A strip's head shows its name beside its icon from this width (px), the icon's size following the
    /// strip's; under it, the icon centred over the name (asked by the user, #158: both always in sight), the icon at
    /// <see cref="StackedBadge"/> so the two fit the head's row.</summary>
    const double HeadNameW = 100, StackedBadge = 20;

    /// <summary>
    /// The compact strip's controls, the same for a track and a bus (asked by the user, like a console's strip): the
    /// effects as a list (a click opens one in its window, <see cref="ShowSlotWindow"/>; dragged, it moves in the chain;
    /// its power switch turns it on or off), the sends as bars with the bus's name (shown with the mixer's "Envíos"; a
    /// bus, which has none, keeps the section empty so its fader stays level), the pan as a bar from the centre, and its
    /// <paramref name="toggles"/> (M S ● or a bus's M). The action writes the first compressor's reduction under its row.
    /// </summary>
    (Control Controls, Action<double> Gr) CompactChain(SlotOwner o, Router? sends, Func<double> panOf, Action<double> setPan, params Control[] toggles)
    {
        var g = new Grid();
        void Section(List<(RowDefinition, Control, bool)> rows, Control head, Control list) => ListSection(g, rows, head, list);

        var (fxList, grBar) = SlotList(o);
        fxList.Children.Add(ListAddRow(Loc.T("mix.slot.add.tip"), CompactRowH, b => ShowAddSlotMenu(o, b)));
        var fxBox = ListBox(fxList, false);
        // the mixer's FX switch puts them all in bypass
        mx.Refresh.Add(() => fxBox.Opacity = Audio.PlaybackEngine.FxBypass ? 0.4 : 1);
        Section(mx.FxRows, ListHead(Loc.T("mix.list.fx")), fxBox);

        if (sends != null)
        {
            var sendList = CompactSends(sends);
            sendList.Children.Add(ListAddRow(Loc.T("send.add.tip"), CompactRowH, b => OpenAnchoredMenu(Popup(SendChoices(sends), 220), b)));
            Section(mx.SendRows, ListHead(Loc.T("mix.list.sends")), ListBox(sendList, true));
        }
        else Section(mx.SendRows, new Border(), new Border());

        var pan = new HBar { Bipolar = true, ResetTo = 0, Margin = new Thickness(0, 4, 0, 6) };
        pan.Changed += setPan;
        // the readout comes from ValueFormat's tables: following a lane every tick allocates nothing
        AutomateOn(pan, () => TrackOf(o), AutoTarget.Pan, k => { pan.Value = k * 2 - 1; pan.Readout = ValueFormat.PanShort(k * 2 - 1); });
        mx.Refresh.Add(() =>
        {
            double v = panOf();
            pan.Value = v;
            pan.Readout = ValueFormat.PanShort(v);
        });
        Grid.SetRow(pan, g.RowDefinitions.Count); g.RowDefinitions.Add(new RowDefinition(PanRowH, GridUnitType.Pixel)); g.Children.Add(pan);

        // M S ● share the width (each at most its usual size), so they still fit a very narrow strip
        var buttons = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, MaxWidth = toggles.Length * (ToggleMaxW + 2) };
        foreach (var toggle in toggles)
        {
            toggle.Width = double.NaN;
            toggle.MaxWidth = ToggleMaxW;
            toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
            toggle.Margin = new Thickness(1, 0);
            Grid.SetColumn(toggle, buttons.ColumnDefinitions.Count);
            buttons.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            buttons.Children.Add(toggle);
        }
        // too narrow for three, ● gives way (it is also in the track's header in the timeline)
        if (toggles.Length > 2)
            buttons.SizeChanged += (_, e) =>
            {
                bool fits = e.NewSize.Width >= toggles.Length * MinToggleW;
                toggles[^1].IsVisible = fits;
                buttons.ColumnDefinitions[^1].Width = fits ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            };
        Grid.SetRow(buttons, g.RowDefinitions.Count); g.RowDefinitions.Add(new RowDefinition(TogglesRowH, GridUnitType.Pixel)); g.Children.Add(buttons);
        var meterBar = grBar;
        return (g, gr => { if (meterBar != null && meterBar.Parent is Control p) meterBar.Width = p.Bounds.Width * Math.Clamp(-gr / 24, 0, 1); });
    }

    /// <summary>
    /// A chain as a list of rows, in a strip or the Mastering view: a click opens the effect in its window, dragged it
    /// moves in the chain, its power switch turns it on or off. With the bar under its first compressor's row.
    /// </summary>
    (StackPanel List, Border? Gr) SlotList(SlotOwner o)
    {
        var fxList = new StackPanel { Spacing = 1 };
        var drag = new SlotDrag(1);
        drag.Moved += (id, anchor, after) => SlotsEdit(o, x => ChannelInserts.MoveNextTo(x, id, anchor, after), "move");
        Border? grBar = null;
        var slots = o.Slots();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            string id = slot.Id, name = SlotLabel(slots, slot, nth);
            var row = ListRow(name, slot.Type, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, x => x.On = !x.On, "on"),
                () => o.Slot(id) is { } s ? SlotBrief(s) : "");
            if (slot.Type == InsertSlot.CompKind && grBar == null)
            {
                grBar = new Border { Height = 2, Width = 0, Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
                // over the row, not in its grid: there its width would widen the Auto columns and push the text
                var line = row.Child!;
                row.Child = null;
                row.Child = new Panel { Children = { line, grBar } };
            }
            row.Tag = id;
            row.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right || store == null) return;
                e.Handled = true; // not the strip's or the mixer's menu too
                ShowSlotMenu(o, id, row, row);
            };
            fxList.Children.Add(drag.Add(row, id, row));
        }
        // a VST3 that is loaded opens its own window at once (asked by the user: no window in between); the others,
        // and a plugin still loading or that failed, open their panel
        drag.Clicked += id =>
        {
            if (o.Slot(id)?.Type == InsertSlot.VstKind && EffectStateOf(o.OwnerId, id) == EffectState.Ready) OpenEffectWindow(o.OwnerId, id);
            else if (fxList.Children.OfType<Border>().FirstOrDefault(c => c.Tag as string == id) is { } r) ShowSlotWindow(o, id, r);
        };
        foreach (var c in fxList.Children.OfType<Border>()) c.Cursor = new Cursor(StandardCursorType.Hand);
        return (fxList, grBar);
    }

    /// <summary>
    /// A compact list's section (its title and its list) as two more rows of <paramref name="g"/>. Every strip's
    /// sections are as tall as the others' (asked by the user: all level), whatever each one holds.
    /// </summary>
    static void ListSection(Grid g, List<(RowDefinition, Control, bool)> rows, Control head, Control list)
    {
        var headRow = new RowDefinition(ListHeadH, GridUnitType.Pixel);
        var listRow = new RowDefinition(0, GridUnitType.Pixel);
        Grid.SetRow(head, g.RowDefinitions.Count); g.RowDefinitions.Add(headRow); g.Children.Add(head);
        Grid.SetRow(list, g.RowDefinitions.Count); g.RowDefinitions.Add(listRow); g.Children.Add(list);
        rows.Add((headRow, head, true));
        rows.Add((listRow, list, false));
    }

    /// <summary>The compact strip's sends: a bar per bus in the list of <paramref name="router"/> (a track or a folder), its power switch turning it on or off.</summary>
    StackPanel CompactSends(Router router, List<Action>? refresh = null, Action<string, Control>? rowAdded = null)
    {
        var sends = new StackPanel { Spacing = 1 };
        foreach (var bus in SendTargets(router))
        {
            string id = bus.Id;
            (double Level, bool On, bool Pre) S() => router.Get() is { } r ? ProjectBuses.SendOf(r, id) : (0, true, false);
            var bar = new HBar { ResetTo = 0 };
            bar.Changed += v => router.Edit((p, x) => ProjectBuses.SetSend(p, x, id, v, v > 0 ? true : null) != null, $"send.{id}.level");
            var power = ListPower(() => S().On && S().Level > 0, () => router.Edit((p, x) => ProjectBuses.SetSend(p, x, id, on: !S().On) != null, $"send.{id}.on"), refresh);
            // a send taken before the fader says so after its bar, like the inspector's PRE chip, while the strip has room
            var preTag = new TextBlock { Text = "PRE", Classes = { "mono" }, FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = Palette.Dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0), IsHitTestVisible = false };
            bool preRoom = true;
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = CompactRowH };
            line.SizeChanged += (_, e) =>
            {
                power.IsVisible = e.NewSize.Width >= RowPowerW;
                preRoom = e.NewSize.Width >= RowNameW;
                preTag.IsVisible = preRoom && S().Pre;
            };
            // a right click: the send's menu (asked by the user, like an effect's)
            line.Background = Brushes.Transparent;
            line.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right || store == null) return;
                e.Handled = true;
                ShowSendMenu(router, id, line);
            };
            line.Children.Add(power);
            Grid.SetColumn(bar, 1); line.Children.Add(bar);
            Grid.SetColumn(preTag, 2); line.Children.Add(preTag);
            // the readout's whole percent: a lane moves the bar every tick, the text changes only with this
            double percentShown = double.NaN;
            void ShowPercent(double level)
            {
                double p = Math.Round(level * 100, MidpointRounding.AwayFromZero);
                if (p == percentShown) return;
                percentShown = p;
                bar.Readout = ValueFormat.Percent(level);
            }
            if (router.Lanes is { } lanes) FollowLane(bar, () => lanes is Folder lf ? FolderById(lf.Id) : lanes, AutoTarget.Send(id), k =>
            {
                bar.Value = k;
                ShowPercent(k);
            });
            (refresh ?? mx.Refresh).Add(() =>
            {
                var (level, on, pre) = S();
                bar.Value = level;
                bar.Lit = on;
                bar.Label = BusById(id)?.Name ?? bus.Name;
                ShowPercent(level);
                preTag.IsVisible = preRoom && pre;
                preTag.Foreground = on ? Palette.Dim : Palette.Faint;
                ToolTip.SetTip(bar, $"{Loc.T("mix.list.send.tip", bar.Label, $"{level * 100:0}")} · {ValueFormat.Db(level)} · {Loc.T(pre ? "mix.list.send.pre" : "mix.list.send.post")}");
            });
            rowAdded?.Invoke(id, line);
            sends.Children.Add(line);
        }
        return sends;
    }

    /// <summary>
    /// A compact list in its section: it scrolls when it holds more than fits, and a grip along its bottom (the divider
    /// to the next section, with a handle in its middle so it is found) stretches the section on every strip at once
    /// (asked by the user), remembered in config.json; a double click fits it to the longest list, and a second one
    /// puts the default back.
    /// </summary>
    Control ListBox(Control list, bool sends)
    {
        if (list is Panel panel) (sends ? mx.SendLists : mx.FxLists).Add(panel);
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 6) };
        var line = new Border { Height = 1, Background = Palette.Line, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var handle = new Border { Width = 18, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Palette.Hover,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var grip = new Border { Height = 6, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Bottom, Child = new Panel { Children = { line, handle } },
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
        grip.PointerEntered += (_, _) => line.Background = handle.Background = Palette.Accent;
        grip.PointerExited += (_, _) => { line.Background = Palette.Line; handle.Background = Palette.Hover; };
        double fromY = 0, fromH = 0;
        bool dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                // what fits in the mixer's height: past it, the mixer is made taller by hand first
                double fit = Math.Round(Math.Min(FitListH(sends), ListCap(sends)));
                var (fxNow, sendsNow) = ShownLists();
                SetListH(sends, fit == Math.Round(sends ? sendsNow : fxNow) ? sends ? SendsListH : FxListH : fit, true);
                return;
            }
            dragging = true;
            fromY = e.GetPosition(this).Y;
            var (fxFrom, sendsFrom) = ShownLists();
            fromH = sends ? sendsFrom : fxFrom;
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (dragging) SetListH(sends, fromH + e.GetPosition(this).Y - fromY, false);
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            SetListH(sends, sends ? mixerSendsH : mixerFxH, true);
        };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        return new Grid { Children = { scroll, grip } };
    }

    /// <summary>The height (px) that shows the longest effects or sends list of the mixer whole; the default with none.</summary>
    double FitListH(bool sends)
    {
        int n = 0;
        foreach (var l in (sends ? mixerLayout.SendLists : mixerLayout.FxLists)) n = Math.Max(n, l.Children.Count);
        // the rows, the 1 px between them and the room the grip keeps under the list (ListBox)
        return n == 0 ? sends ? SendsListH : FxListH : Math.Min(ListMaxH, n * (CompactRowH + 1) - 1 + 6);
    }

    /// <summary>
    /// A list section's new height, on every strip (and saved when <paramref name="save"/>), no taller than what the
    /// mixer's height leaves (<see cref="ListCap"/>). The other list keeps the height it is shown at, so what shows is
    /// what was chosen.
    /// </summary>
    void SetListH(bool sends, double h, bool save)
    {
        var (fx, s) = ShownLists();
        h = Math.Round(Math.Clamp(h, ListMinH, ListCap(sends)));
        if (sends)
        {
            mixerSendsH = h;
            if (mixerFx) mixerFxH = Math.Max(ListMinH, fx);
        }
        else
        {
            mixerFxH = h;
            if (SendsRow) mixerSendsH = Math.Max(ListMinH, s);
        }
        ApplyMixerHeights();
        if (save)
        {
            SaveViewPref("mixer.fxH", mixerFxH.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SaveViewPref("mixer.sendsH", mixerSendsH.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Sets every strip's controls row and the lists' sections to the shown sections and their heights: only row
    /// heights change, nothing is rebuilt (it runs on every move of a grip).
    /// </summary>
    void ApplyMixerHeights()
    {
        var layout = mixerLayout;
        var (fx, sends) = ShownLists();
        double top = StripTopH;
        foreach (var r in layout.TopRows) r.Height = new GridLength(top, GridUnitType.Pixel);
        foreach (var (row, what, head) in layout.FxRows)
        {
            row.Height = new GridLength(mixerFx ? head ? ListHeadH : fx : 0, GridUnitType.Pixel);
            what.IsVisible = mixerFx;
        }
        foreach (var (row, what, head) in layout.SendRows)
        {
            row.Height = new GridLength(SendsRow ? head ? ListHeadH : sends : 0, GridUnitType.Pixel);
            what.IsVisible = SendsRow;
        }
        ApplyMixerMinHeight();
    }

    /// <summary>What a slot is called in the compact list: the musician's word (Saturación, Comp…).</summary>
    static string SlotName(string type) => type switch
    {
        InsertSlot.SatKind => Loc.T("fx.life"),
        InsertSlot.CompKind => Loc.T("fx.comp"),
        InsertSlot.ReverbKind => "Reverb",
        InsertSlot.DelayKind => "Delay",
        InsertSlot.GateKind => Loc.T("fx.gate"),
        TrackInserts.Clean => Loc.T("master.stage.clean"),
        TrackInserts.Bands => Loc.T("master.bands"),
        TrackInserts.Color => Loc.T("master.stage.color"),
        TrackInserts.Tape => Loc.T("master.stage.tape"),
        TrackInserts.Width => Loc.T("master.stage.width"),
        TrackInserts.Clip => Loc.T("master.stage.clip"),
        TrackInserts.Limit => Loc.T("master.stage.lim"),
        TrackInserts.Chorus or TrackInserts.Flanger or TrackInserts.Phaser or TrackInserts.Tremolo or TrackInserts.Pump => Loc.T("fx." + type),
        _ when TrackInserts.IsModel(type) => Loc.T("fx." + type),
        _ => "EQ",
    };

    /// <summary>A compact list's title ("EFECTOS", "ENVÍOS"); its "+" is the list's last row (<see cref="ListAddRow"/>).</summary>
    static Grid ListHead(string title)
    {
        var g = new Grid { Height = 20 };
        var text = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 9, LetterSpacing = 0.8, FontWeight = FontWeight.SemiBold, Foreground = Palette.Faint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(text, title);
        g.Children.Add(text);
        g.SizeChanged += (_, e) => text.IsVisible = e.NewSize.Width >= RowNameW;
        return g;
    }

    /// <summary>
    /// The "+" that adds an effect or a send, as the last row of its list (asked by the user, #150: where the list is,
    /// not up on its title), in the mixer's strips and the inspector's: faint, the accent under the pointer.
    /// </summary>
    Button ListAddRow(string tip, double height, Action<Button> add)
    {
        var b = new Button { Classes = { "ghost", "slothead" }, Height = height, MinHeight = height, Padding = new Thickness(0), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Palette.Faint, Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => { if (!PressClosedMenuOf(b)) add(b); };
        return b;
    }

    /// <summary>
    /// The power switch of a list's row (asked by the user: the ⏻ of a real switch, the same glyph as an effect panel's
    /// ON/OFF): an accent glyph on a soft accent block when on (class power-on, App.axaml), a grey glyph when off.
    /// </summary>
    Button ListPower(Func<bool> on, Action toggle, List<Action>? refresh = null)
    {
        const double PowerW = 20;
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        // the row's whole left end, full height, with the plain arrow (asked by the user: the hand and the drag's cursor
        // start after it, so it never reads as part of what is dragged)
        var b = new Button { Classes = { "ghost", "slothead" }, Width = PowerW, MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(0), Focusable = false,
            Margin = new Thickness(0, 0, 3, 0), Cursor = new Cursor(StandardCursorType.Arrow), Content = glyph,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => toggle();
        (refresh ?? mx.Refresh).Add(() =>
        {
            bool lit = on();
            glyph.Foreground = lit ? Palette.Accent : Palette.Faint;
            b.Classes.Set("power-on", lit);
            ToolTip.SetTip(b, Loc.T(lit ? "mix.list.off" : "mix.list.on"));
        });
        return b;
    }

    /// <summary>
    /// A row of the compact effects list: its power switch and its name, on a block that lights under the pointer. With
    /// <paramref name="apart"/> (the inspector's strip, asked by the user: like its sends) the switch sits outside, left
    /// of a block drawn as a send's bar.
    /// </summary>
    Border ListRow(string name, string type, Func<bool> on, Action toggle, Func<string> brief, List<Action>? refresh = null, bool apart = false)
    {
        // its icon stays at any width (asked by the user); the name and what it is set to come in as the row widens
        var icon = FxIcon.Create(type, 11);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 5, 0);
        var text = new TextBlock { Text = name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { FontSize = 10.5, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), IsVisible = false };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var power = ListPower(on, toggle, refresh);
        Grid.SetColumn(icon, 1); g.Children.Add(icon);
        Grid.SetColumn(text, 2); g.Children.Add(text);
        Grid.SetColumn(detail, 3); g.Children.Add(detail);
        Border row, block;
        if (apart)
        {
            // the block is drawn as HBar draws a send: Raised over a Line border, radius 3
            block = new Border { CornerRadius = new CornerRadius(3), BorderBrush = Palette.Line, BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 6, 0),
                Child = g, Background = Palette.Raised, ClipToBounds = true };
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            line.Children.Add(power);
            Grid.SetColumn(block, 1); line.Children.Add(block);
            row = new Border { Height = CompactRowH, Child = line, Background = Brushes.Transparent };
        }
        else
        {
            g.Children.Add(power);
            row = block = new Border { Height = CompactRowH, CornerRadius = new CornerRadius(4), Padding = new Thickness(1, 0, 3, 0), Child = g, Background = Palette.Raised, ClipToBounds = true };
        }
        // apart, only the block lights, as a send's bar does
        block.PointerEntered += (_, _) => block.Background = Palette.Hover;
        block.PointerExited += (_, _) => block.Background = Palette.Raised;
        row.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            power.IsVisible = w >= RowPowerW;
            text.IsVisible = w >= RowNameW;
            detail.IsVisible = w >= RowBriefW;
            detail.MaxWidth = Math.Floor(w * RowBriefShare);
            // alone, the icon sits in the middle of the row
            bool alone = !power.IsVisible;
            Grid.SetColumnSpan(icon, alone ? 3 : 1);
            icon.HorizontalAlignment = alone ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            icon.Margin = new Thickness(0, 0, alone ? 0 : 5, 0);
        };
        (refresh ?? mx.Refresh).Add(() =>
        {
            bool lit = on();
            text.Foreground = lit ? Palette.Fg : Palette.Faint;
            FxIcon.SetColor(icon, lit ? Palette.Accent : Palette.Faint);
            detail.Text = brief();
        });
        return row;
    }

    /// <summary>What a slot is set to, in a few words (a list row's detail).</summary>
    string SlotBrief(InsertSlot s)
    {
        switch (s.Type)
        {
            case InsertSlot.SatKind when s.Sat is { } x:
                return $"{Loc.T(x.Mode == "tape" ? "insp.sat.tape" : "insp.sat.tube")} · {x.Amount * 100:0}%";
            case InsertSlot.CompKind when s.Comp is { } c:
                return PresetLabel(c.Preset);
            case InsertSlot.GateKind when s.Gate is { } x:
                return $"{Loc.T("gate.mode." + x.Mode)} · {x.Threshold:0} dB";
            case InsertSlot.ReverbKind when s.Reverb is { } r:
                string p = ReverbBus.PresetOf(ToParams(r));
                return $"{(p == "" ? Loc.T("mix.custom.f") : BusPresetName("reverb", p))} · {r.Decay:0.0}s";
            case InsertSlot.DelayKind when s.Delay is { } d:
                string time = !d.Sync ? $"{Math.Round(d.TimeMs):0} ms"
                    : Array.Find(DelayBus.Divisions, x => x.Id == d.Division) is { } div ? Loc.Or("mix.div." + div.Id, div.Label) : d.Division;
                return $"{time} · {d.Feedback * 100:0}%";
            case InsertSlot.EqKind when s.Eq is { } e:
                return EqEditor.PresetIndex(e) is var i and >= 0 ? EqPresetName(TrackEq.Presets[i]) : Loc.T("mix.custom.m");
            default:
                // a master effect: its first knob
                return FxDefs.Params(s.Type) is { Length: > 0 } ps ? FxText(ps[0], ps[0].Get(s)) : "";
        }
    }

    /// <summary>
    /// Whose insert slots the panels edit: a track's channel or a bus. The panels only know this, so a bus strip shows
    /// its slots exactly like a track strip.
    /// </summary>
    sealed class SlotOwner
    {
        /// <summary>The track's or the bus's id: whose VST3 effects these are (<see cref="LiveVst"/>).</summary>
        public required string OwnerId { get; init; }
        /// <summary>The slots as they are now (the channel or the bus object changes with every undo).</summary>
        public required Func<List<InsertSlot>> Slots { get; init; }
        /// <summary>An edit of the slots: one undo step per key, heard at once, saved.</summary>
        public required Action<Action<List<InsertSlot>>, string> Edit { get; init; }
        /// <summary>Sets a compressor's values from its preset (after its preset or amount changed, or a new one).</summary>
        public required Action<CompSettings> Recalculate { get; init; }
        /// <summary>What the compressor's analyze button measures; null = nothing to measure.</summary>
        public Func<MediaAudio?> Take { get; init; } = () => null;
        /// <summary>The "+ Efecto" tooltip: what a new effect works on.</summary>
        public string AddTip { get; init; } = Loc.T("mix.slot.add.tip");
        /// <summary>A bus's or the master's chain: a new reverb or delay starts all wet (on a track, mixed with the dry signal).</summary>
        public bool ReturnKinds { get; init; }
        /// <summary>The master's chain: "+ Efecto" also offers the whole chain, and no VST3 (no process runs for it yet).</summary>
        public bool Master { get; init; }
        /// <summary>A slot's running effect by its id, for what the window shows live.</summary>
        public Func<string, ITrackInsert?> Insert { get; init; } = _ => null;
        /// <summary>Called before an edit of a compressor (a clip remembers what role it had).</summary>
        public Action<CompSettings>? CompEdited { get; init; }
        public InsertSlot? Slot(string id) => Slots().Find(s => s.Id == id);
    }

    /// <summary>A track's slots: its channel's, the compressor measured against its take.</summary>
    SlotOwner SlotsOf(LiveTrack t) => new()
    {
        OwnerId = t.Track.Id,
        Slots = () => t.Channel.Slots,
        Edit = (change, key) => EditTrack(t, x => change(x.Channel.Slots), key),
        Recalculate = c => t.RecalculateCompPreset(c),
        Take = () => t.FirstAudio,
        Insert = t.Chain.Find,
        CompEdited = c =>
        {
            if (SelectedInspectorClip is not { } clip) return;
            clip.Clip.Extra ??= [];
            clip.Clip.Extra.TryAdd("role", System.Text.Json.JsonSerializer.SerializeToElement(c.Preset == "lead" ? "solo" : "base"));
        },
    };

    /// <summary>One slot's panel by its kind, for its window (<see cref="ShowSlotWindow"/>).</summary>
    void SlotPanel(StackPanel fx, SlotOwner o, InsertSlot slot, int nth)
    {
        var menu = SlotMenuButton(o, slot.Id);
        if (slot.Type == InsertSlot.VstKind)
        {
            VstPanel(fx, o, slot.Id, menu);
            return;
        }
        string id = slot.Id;
        bool sized = FxDefs.Layout(slot.Type) != null;
        Control extra = menu;
        if (sized)
        {
            var size = SlotHeadButton(slot.Full ? "" : "", Loc.T(slot.Full ? "fx.full.off" : "fx.full.on"));
            size.Click += (_, _) => ToggleFull(o, id);
            extra = new StackPanel { Orientation = Orientation.Horizontal, Children = { size, menu } };
        }
        var panel = FxPanel(fx, SlotTitle(SlotName(slot.Type), nth), slot.Type, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, s => s.On = !s.On, "on"),
            on => Loc.T(on ? "mix.list.off" : "mix.list.on"), extra);
        panel.Body.Spacing = 8;
        FxBody(panel.Body, o, slot, mx.Refresh, full: sized ? slot.Full : null);
    }

    /// <summary>
    /// A new effect's starting values here, its Default (#141): every kind as its settings start, the compressor's values
    /// computed for this owner, and on a track a reverb or a delay over its dry signal (a bus's send already keeps it).
    /// The same when it is added and when its Default is chosen again.
    /// </summary>
    void StartSlot(SlotOwner o, InsertSlot slot)
    {
        if (slot.Comp is { } c) o.Recalculate(c);
        if (!o.ReturnKinds && !o.Master && slot.Type is InsertSlot.ReverbKind or InsertSlot.DelayKind)
        {
            slot.Wet = TrackFxWet;
            slot.Dry = 1;
        }
    }

    /// <summary>
    /// Switches a slot between its compact and full view (#140) and rebuilds its window at the new size. A view choice:
    /// saved with the project, never an undo step (a folder's fold works the same way).
    /// </summary>
    void ToggleFull(SlotOwner o, string id)
    {
        if (o.Slot(id) is not { } s) return;
        s.Full = !s.Full;
        MarkDirty();
        if (slotWindows.TryGetValue(o.OwnerId + "/" + id, out var w)) w.Rebuild();
    }

    // what a panel reads while its slot is already gone (removed, an undo) and the strips aren't rebuilt yet
    static readonly SatSettings GoneSat = new();
    static readonly CompSettings GoneComp = new();
    static readonly EqSettings GoneEq = new();
    static readonly GateSettings GoneGate = new();

    /// <summary>The gate's modes in their order, with their names' and hints' keys.</summary>
    static readonly string[] GateModes = [GateParams.Soft, GateParams.Normal, GateParams.Hard];

    static string SlotTitle(string name, int nth) => nth > 1 ? $"{name} {nth}" : name;

    /// <summary>An edit of one slot's settings, found by id (the channel object changes with every undo).</summary>
    void SlotEdit(SlotOwner o, string id, Action<InsertSlot> edit, string key) =>
        o.Edit(slots => { if (slots.Find(s => s.Id == id) is { } s) edit(s); }, $"{id}.{key}");

    /// <summary>Adds, removes or moves slots: each its own undo step; the strips rebuild with the new list.</summary>
    Task SlotsEdit(SlotOwner o, Action<List<InsertSlot>> change, string key)
    {
        o.Edit(change, $"fx.{key}.{++slotEdits}");
        return Task.CompletedTask;
    }

    /// <summary>An edit of one compressor slot, found by id; its values follow its preset unless it is manual.</summary>
    void StripComp(SlotOwner o, string id, Action<CompSettings> edit, string key) => o.Edit(slots =>
    {
        if (slots.Find(s => s.Id == id)?.Comp is not { } c) return;
        edit(c);
        if (c.Preset != "custom") o.Recalculate(c);
    }, $"{id}.comp.{key}");

    int slotEdits;

    /// <summary>The ⋯ in a slot's title bar: its menu (<see cref="ShowSlotMenu"/>).</summary>
    Button SlotMenuButton(SlotOwner o, string id)
    {
        var b = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Focusable = false,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(b, Loc.T("mix.slot.menu.tip"));
        b.Click += (_, _) =>
        {
            if (!PressClosedMenuOf(b)) ShowSlotMenu(o, id, b, null);
        };
        return b;
    }

    /// <summary>
    /// A slot's actions, the same from its ⋯ and from a right click on it in the mixer's list or the inspector (asked by
    /// the user): open it (a loaded VST3, its own window; another effect from the mixer, its panel in a window over
    /// <paramref name="panelAnchor"/>), turn it on or off, turn the rest of the chain off, move it, repeat it, remove it.
    /// </summary>
    void ShowSlotMenu(SlotOwner o, string id, Control anchor, Control? panelAnchor)
    {
        var slots = o.Slots();
        int i = slots.FindIndex(s => s.Id == id);
        if (i < 0) return;
        var slot = slots[i];
        bool enabled = store is { ReadOnly: false } && !restoring;
        var items = new List<object>();
        if (slot.Type == InsertSlot.VstKind)
            items.Add(MenuAction(Loc.T("mix.slot.openPlugin"), () => { OpenEffectWindow(o.OwnerId, id); return Task.CompletedTask; },
                enabled: EffectStateOf(o.OwnerId, id) == EffectState.Ready, icon: ""));
        else if (panelAnchor != null)
            items.Add(MenuAction(Loc.T("mix.slot.open"), () => { ShowSlotWindow(o, id, panelAnchor); return Task.CompletedTask; }, icon: ""));
        items.Add(MenuAction(Loc.T(slot.On ? "mix.slot.turnOff" : "mix.slot.turnOn"), () => { SlotEdit(o, id, s => s.On = !s.On, "on"); return Task.CompletedTask; },
            enabled: enabled, icon: ""));
        // to hear it alone in the chain: one undo step, like any edit of the list
        items.Add(MenuAction(Loc.T("mix.slot.others"), () => SlotsEdit(o, x => { foreach (var s in x) if (s.Id != id && s.Known) s.On = false; }, "others"),
            enabled: enabled && slots.Exists(s => s.Id != id && s.Known && s.On), icon: "", tip: Loc.T("mix.slot.others.tip")));
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T("mix.slot.up"), () => SlotsEdit(o, x => ChannelInserts.Move(x, id, -1), "move"), enabled: enabled && i > 0, icon: "",
            tip: Loc.T("mix.slot.up.tip")));
        items.Add(MenuAction(Loc.T("mix.slot.down"), () => SlotsEdit(o, x => ChannelInserts.Move(x, id, 1), "move"), enabled: enabled && i < slots.Count - 1, icon: "",
            tip: Loc.T("mix.slot.down.tip")));
        items.Add(MenuAction(Loc.T("mix.slot.duplicate"), () => SlotsEdit(o, x => ChannelInserts.Duplicate(x, id), "duplicate"), enabled: enabled, icon: "",
            tip: Loc.T("mix.slot.duplicate.tip")));
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T("mix.slot.remove"), () => SlotsEdit(o, x => ChannelInserts.Remove(x, id), "remove"), enabled: enabled, danger: true, icon: "",
            tip: Loc.T("mix.slot.remove.tip")));
        OpenAnchoredMenu(Popup(items, 220), anchor);
    }

    /// <summary>A new reverb or delay on a track: this much of it, over its dry signal whole.</summary>
    const double TrackFxWet = 0.25;

    /// <summary>"+ Efecto" under the slots: adds one at the end of the chain, already on.</summary>
    Button AddSlotButton(SlotOwner o)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.T("mix.slot.add"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
        var b = new Button { Classes = { "chip" }, Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 30, Focusable = false };
        ToolTip.SetTip(b, o.AddTip);
        b.Click += (_, _) => { if (!PressClosedMenuOf(b)) ShowAddSlotMenu(o, b); };
        return b;
    }

    /// <summary>The built-in effects by what they do, in the order "+ Efecto" lists them: a new one goes in its group.</summary>
    static readonly (string Name, string Icon, string[] Kinds)[] FxGroups =
    [
        ("fx.group.dynamics", FxIcon.Comp, [InsertSlot.CompKind, InsertSlot.GateKind, TrackInserts.Pump, TrackInserts.Bands, TrackInserts.Clip, TrackInserts.Limit]),
        ("fx.group.models", FxIcon.Comp, [TrackInserts.Fet, TrackInserts.Opto, TrackInserts.VariMu, TrackInserts.BusComp, TrackInserts.Vca]),
        ("fx.group.eq", FxIcon.Eq, [InsertSlot.EqKind, TrackInserts.Clean, TrackInserts.Color]),
        ("fx.group.sat", FxIcon.Sat, [InsertSlot.SatKind, TrackInserts.Tape]),
        ("fx.group.mod", FxIcon.Chorus, [TrackInserts.Chorus, TrackInserts.Flanger, TrackInserts.Phaser, TrackInserts.Tremolo]),
        ("fx.group.space", FxIcon.Reverb, [InsertSlot.ReverbKind, InsertSlot.DelayKind, TrackInserts.Width]),
    ];

    /// <summary>What "+ Efecto" says of an effect.</summary>
    static string SlotHint(string kind) => Loc.T(kind switch
    {
        InsertSlot.ReverbKind => "mix.slot.add.reverb", InsertSlot.DelayKind => "mix.slot.add.delay", InsertSlot.SatKind => "mix.slot.add.sat",
        InsertSlot.CompKind => "mix.slot.add.comp", InsertSlot.EqKind => "mix.slot.add.eq", InsertSlot.GateKind => "mix.slot.add.gate",
        TrackInserts.Clean => "master.stage.clean.tip", TrackInserts.Bands => "master.bands.on.tip", TrackInserts.Color => "master.stage.color.tip",
        TrackInserts.Tape => "master.stage.tape.tip", TrackInserts.Width => "master.stage.width.tip", TrackInserts.Clip => "master.stage.clip.tip",
        TrackInserts.Limit => "master.stage.lim.tip", _ => "fx.add." + kind,
    });

    /// <summary>
    /// The kinds of effect a chain can take: one more at its end, already on. The built-in ones by what they do (asked by
    /// the user: <see cref="FxGroups"/>), each group opening as the pointer goes over it, then VST3 (the ones found, by
    /// maker when there are many), looking for plugins again and managing them.
    /// </summary>
    void ShowAddSlotMenu(SlotOwner o, Control b)
    {
        // the first time, it looks for plugins on its own
        EnsurePlugins();
        MenuItem Kind(string kind, string name, string hint)
        {
            var icon = FxIcon.Create(kind, 12);
            icon.VerticalAlignment = VerticalAlignment.Center;
            FxIcon.SetColor(icon, Palette.Accent);
            var item = new MenuItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { icon, new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
            ToolTip.SetTip(item, hint);
            ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
            item.Click += (_, _) => SlotsEdit(o, x =>
            {
                if (ChannelInserts.Add(x, kind) is { } slot) StartSlot(o, slot);
            }, "add");
            return item;
        }
        var items = new List<object>();
        foreach (var (group, icon, list) in FxGroups)
            items.Add(AddSlotGroup(Loc.T(group), icon, [.. list.Select(k => (object)Kind(k, k == InsertSlot.CompKind ? Loc.T("insp.section.comp") : SlotName(k), SlotHint(k)))]));
        if (o.Master)
        {
            bool free = TrackInserts.MasterOrder.Any(k => !o.Slots().Exists(s => s.Type == k));
            items.Add(new Separator());
            items.Add(MenuAction(Loc.T("mix.slot.chain"), () => { AddMasterChain(); return Task.CompletedTask; }, enabled: free && store is { ReadOnly: false }, tip: Loc.T("mix.slot.chain.tip")));
        }
        else items.AddRange(VstEffectItems(o));
        OpenAnchoredMenu(Popup(items, 220), b);
    }

    /// <summary>
    /// Port of FxPanel: power, icon and name over the effect's controls, which grey out while it is off. Narrower than
    /// <see cref="FxNarrowW"/> the power button keeps only its glyph and the icon goes, so the name fits.
    /// </summary>
    (Border Box, StackPanel Body) FxPanel(StackPanel host, string title, string icon, Func<bool> on, Action? toggle, Func<bool, string> tip, Control? extra = null)
    {
        // the effect's ON, the same everywhere (PowerButton); a panel without a toggle has none
        var power = new PowerButton(on, () => toggle?.Invoke(), tip);
        power.IsVisible = toggle != null;
        var iconText = FxIcon.Create(icon, 12);
        iconText.VerticalAlignment = VerticalAlignment.Center; iconText.Margin = new Thickness(toggle == null ? 0 : 8, 0, 0, 0);
        var name = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 4, 0) };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 32, Margin = new Thickness(6, 0) };
        head.Children.Add(power);
        Grid.SetColumn(iconText, 1); head.Children.Add(iconText);
        Grid.SetColumn(name, 2); head.Children.Add(name);
        if (extra != null) { extra.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(extra, 3); head.Children.Add(extra); }
        var body = new StackPanel { Margin = new Thickness(8) };
        var stack = new StackPanel();
        var bar = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(5, 5, 0, 0) };
        stack.Children.Add(bar);
        stack.Children.Add(body);
        var box = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack, ClipToBounds = true };
        box.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < FxNarrowW;
            power.ShowLabel = !narrow;
            iconText.IsVisible = !narrow;
            head.Margin = new Thickness(narrow ? 4 : 6, 0);
            name.Margin = new Thickness(narrow ? 4 : 6, 0, narrow ? 2 : 4, 0);
        };
        mx.Refresh.Add(() =>
        {
            bool lit = on();
            power.Refresh();
            FxIcon.SetColor(iconText, lit ? Palette.Accent : Palette.Faint);
            name.Foreground = lit ? Palette.Fg : Palette.Faint;
            body.Opacity = lit ? 1 : 0.4;
            // a panel a step above the card (asked by the user: each effect reads as its own block), its title bar one
            // more step up; off, both sink back toward the card
            box.Background = Palette.Alpha(Palette.HoverColor, lit ? 0.45 : 0.18);
            box.BorderBrush = lit ? Palette.Line : Palette.Alpha(Palette.LineColor, 0.6);
            bar.Background = Palette.Alpha(Palette.HoverColor, lit ? 0.6 : 0.2);
            bar.BorderBrush = box.BorderBrush;
        });
        host.Children.Add(box);
        return (box, body);
    }

    /// <summary>A small icon button of a slot's title row (open in a window, fold): a hand over it, not the bar's drag
    /// cursor, and it lights up in the accent (asked by the user; class <c>slothead</c> in App.axaml).</summary>
    static Button SlotHeadButton(string glyph, string tip)
    {
        var b = new Button { Classes = { "ghost", "slothead" }, Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), Focusable = false, Margin = new Thickness(2, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>An effect's open window: the slot it shows, whose it is, its panel's refreshes and how to build it again.</summary>
    sealed class SlotWindow
    {
        public required Window Window { get; init; }
        public required SlotOwner Owner { get; init; }
        public required string Id { get; init; }
        public List<Action> Refresh { get; set; } = [];
        public Action Rebuild { get; set; } = () => { };
    }

    /// <summary>The effects' open windows by owner and slot: like a VST3's, several at once (asked by the user, #140).</summary>
    readonly Dictionary<string, SlotWindow> slotWindows = [];

    /// <summary>Where the last press on the main window was, on the screen: a window opened from a menu starts there.</summary>
    PixelPoint? lastPressScreen;

    /// <summary>Runs every open effect window's refreshes; one whose slot or owner went away closes.</summary>
    void RefreshSlotWindows()
    {
        bool quiet = syncing;
        syncing = true;
        try
        {
            foreach (var w in slotWindows.Values.ToArray())
            {
                if (w.Owner.Slot(w.Id) is not { Known: true } || !OwnerAlive(w.Owner.OwnerId))
                {
                    w.Window.Close();
                    continue;
                }
                foreach (var r in w.Refresh.ToArray()) r();
            }
        }
        finally { syncing = quiet; }
    }

    /// <summary>Closes every effect window (the project changes).</summary>
    void CloseSlotWindows()
    {
        foreach (var w in slotWindows.Values.ToArray()) w.Window.Close();
    }

    bool OwnerAlive(string id) => id == MasterOwner || tracks.Exists(t => t.Track.Id == id) || Buses.Any(b => b.Id == id) || Folders.Any(f => f.Id == id);

    string OwnerName(string id) => id == MasterOwner ? Loc.T("mix.master")
        : tracks.Find(t => t.Track.Id == id)?.Track.Name ?? Buses.FirstOrDefault(b => b.Id == id)?.Name ?? Folders.FirstOrDefault(f => f.Id == id)?.Name ?? "";

    /// <summary>
    /// One slot's whole panel in a window of its own, like a VST3's (asked by the user, #140): it starts right under what
    /// opened it (or where the click was, from a menu), moves by its title bar, stays open while the rest is used and
    /// several can be open; its X or Esc closes it. The same slot again brings its window to the front. Built into a
    /// layout of its own, it edits the same slot and refreshes with the mixer. It takes the size of its panel: a change
    /// (compact or full, an EQ band with other rows) grows it right and down from where it is, and it never leaves the screen.
    /// </summary>
    void ShowSlotWindow(SlotOwner o, string id, Control anchor, bool atPointer = false)
    {
        string key = o.OwnerId + "/" + id;
        if (slotWindows.TryGetValue(key, out var open))
        {
            open.Window.Activate();
            return;
        }
        var slots = o.Slots();
        int i = slots.FindIndex(s => s.Id == id);
        if (i < 0 || !slots[i].Known) return;
        int nth = 1;
        for (int k = 0; k < i; k++) if (slots[k].Type == slots[i].Type) nth++;
        string kind = slots[i].Type;

        var host = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 10, 2, 10) };
        var scroll = new ScrollViewer { Content = host, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var caption = new TextBlock { FontSize = 12, Foreground = Palette.Dim, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
        // its own X, the only button (asked by the user): Windows' caption buttons are off, so there is no minimize nor maximize
        var close = new Button { Classes = { "act", "close" }, Width = SlotTitleH, Height = SlotTitleH, MinHeight = SlotTitleH, Padding = new Thickness(0), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10 } };
        ToolTip.SetTip(close, Loc.T("common.close"));
        var barRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { caption } };
        Grid.SetColumn(close, 1);
        barRow.Children.Add(close);
        var bar = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = barRow };
        Grid.SetRow(scroll, 1);
        var root = new Grid { RowDefinitions = new RowDefinitions($"{SlotTitleH},*"), Children = { bar, scroll } };
        var window = new Window
        {
            // no minimize nor maximize (asked by the user): it closes or moves, like a plugin's
            SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, CanMinimize = false, CanMaximize = false, ShowInTaskbar = false,
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome, Icon = Icon, Background = Palette.Raised,
            WindowStartupLocation = WindowStartupLocation.Manual, ExtendClientAreaToDecorationsHint = true, ExtendClientAreaTitleBarHeightHint = SlotTitleH,
            Content = root,
        };
        var w = new SlotWindow { Window = window, Owner = o, Id = id };
        void Title()
        {
            string title = $"{SlotTitle(SlotName(kind), nth)} · {OwnerName(o.OwnerId)}";
            window.Title = title;
            caption.Text = title;
        }
        void Build()
        {
            if (o.Slot(id) is not { } slot) return;
            bool built = slot.Full;
            double width = slot.Full && FxDefs.Layout(slot.Type) != null ? SlotFullW : SlotWindowW;
            var saved = mx;
            var own = new MixerLayout();
            host.Children.Clear();
            host.Width = width;
            scroll.Width = width + 10 + 2 + SlotScrollRoom;
            mx = own;
            try { SlotPanel(host, o, slot, nth); }
            finally { mx = saved; }
            // an undo can bring back the other size, and a rename the other name: the window follows them
            own.Refresh.Add(() =>
            {
                Title();
                if (o.Slot(id) is { } now && now.Full != built) Dispatcher.UIThread.Post(() => { if (slotWindows.ContainsKey(key)) w.Rebuild(); });
            });
            bool was = syncing;
            syncing = true;
            try { foreach (var r in own.Refresh) r(); }
            finally { syncing = was; }
            w.Refresh = own.Refresh;
        }
        w.Rebuild = Build;
        Build();

        // right under what opened it, its left edges lined up; from a menu, where the click was
        var start = atPointer && lastPressScreen is { } press ? press : anchor.PointToScreen(new Point(0, anchor.Bounds.Height + SlotWindowGap));
        window.Position = start;
        void Fit()
        {
            if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.ScreenFromPoint(window.Position) ?? window.Screens.Primary) is not { } screen) return;
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            window.MaxHeight = Math.Max(SlotTitleH * 4, area.Height / scale - SlotWindowGap * 2);
            var frame = window.FrameSize ?? window.ClientSize;
            int fw = (int)Math.Ceiling(frame.Width * scale), fh = (int)Math.Ceiling(frame.Height * scale);
            int x = Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.Right - fw));
            int y = Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - fh));
            if (x != window.Position.X || y != window.Position.Y) window.Position = new PixelPoint(x, y);
        }
        window.Opened += (_, _) => Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        window.SizeChanged += (_, _) => Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        close.Click += (_, _) => window.Close();
        bar.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && (v == close || close.IsVisualAncestorOf(v))) return;
            if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
        };
        // Esc closes it; the rest of the keys are the app's shortcuts, as in the other windows
        window.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            window.Close();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.AddHandler(KeyUpEvent, OnVirtualKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.AddHandler(KeyUpEvent, OnTransportKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.Closed += (_, _) =>
        {
            if (slotWindows.TryGetValue(key, out var now) && now == w) slotWindows.Remove(key);
        };
        slotWindows[key] = w;
        window.Show(this);
    }

    /// <summary>An effect window's title bar, and the room between it and what opened it.</summary>
    const double SlotTitleH = 32, SlotWindowGap = 4;

    /// <summary>An effect's window: as wide as four knobs at their full size.</summary>
    const double SlotWindowW = 340;
    /// <summary>An effect's window in its full view (#140): two inner panels side by side, four knobs in each.</summary>
    const double SlotFullW = 680;
    /// <summary>Room on its right for the scroll bar (App.axaml: ScrollBarSize, 8), so the window never changes width.</summary>
    const double SlotScrollRoom = 8;

    static SelectButton StripSelect(string[] items)
    {
        var s = new SelectButton();
        s.SetItems(items);
        return s;
    }

    /// <summary>A lit toggle of a strip: a mute in amber, anything else (S, MONO) in the accent, the same colors as the
    /// timeline's M and S. Like every strip button it takes no keyboard focus, so Space and Enter stay shortcuts after
    /// a click in the mixer.</summary>
    Button StripToggle(string text, string tip, double size, Func<bool> on, bool mute, Action click, List<Action>? refresh = null)
    {
        var b = new Button { Classes = { "chip" }, Focusable = false, Width = size, Height = size, MinHeight = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = text, Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        (refresh ?? mx.Refresh).Add(() =>
        {
            bool lit = on();
            b.Classes.Set("lit", lit);
            b.Background = lit ? (mute ? Palette.Mute : Palette.Accent) : Brushes.Transparent;
            b.BorderBrush = lit ? (mute ? Palette.Mute : Palette.Accent) : Palette.Line;
            ((TextBlock)b.Content!).Foreground = lit ? (mute ? Palette.App : Palette.AccentFg) : Palette.Faint;
        });
        return b;
    }

    /// <summary>The strip's record-arm toggle (native only), the same ● as the timeline header: lit red on the track R records to.</summary>
    Button ArmToggle(LiveTrack t, double size)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 9, Height = 9 };
        var b = new Button { Classes = { "chip" }, Focusable = false, Width = size, Height = size, MinHeight = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = dot };
        b.Click += (_, _) => ToggleArm(t);
        mx.Refresh.Add(() =>
        {
            bool lit = Timeline.ArmedIndices.Contains(tracks.IndexOf(t));
            b.Classes.Set("lit", lit);
            b.Background = lit ? Palette.Rec : Brushes.Transparent;
            b.BorderBrush = lit ? Palette.Rec : Palette.Line;
            dot.Fill = lit ? Palette.Fg : Palette.Alpha(Palette.RecColor, 0.7);
            ToolTip.SetTip(b, lit ? Loc.T(t.Track.IsMidi ? "midi.arm.armed" : "tl.tip.armed", Shortcuts.Paren("record"))
                : t.Track.IsMidi ? Loc.T("midi.arm.tip") + Shortcuts.Note("arm", Loc.T("tl.note.arm"))
                : t.Clips.Count == 0 ? Loc.T("tl.tip.arm.empty") + Shortcuts.Note("arm", Loc.T("tl.note.arm"))
                : Loc.T("tl.tip.arm.replace") + Shortcuts.Note("arm", Loc.T("tl.note.arm")));
        });
        return b;
    }

    /// <summary>The armed strip's input ("1-2 ▾"): opens the same menu as the chip in the timeline header.</summary>
    Border InputChip(LiveTrack t)
    {
        var text = new TextBlock { Classes = { "mono" }, FontSize = 9, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        row.Children.Add(text);
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 8, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { Child = row, Height = 14, Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = Palette.Hover, BorderBrush = Palette.Alpha(Palette.RecColor, 0.45), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false };
        ToolTip.SetTip(chip, Loc.T("tl.tip.input"));
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            ShowInputMenu(t, chip);
        };
        mx.Refresh.Add(() => text.Text = t.Track.IsMidi ? MidiInputShort(t.Track) : InputShort(t.Track));
        return chip;
    }

    Control Master()
    {
        var box = StripCard(MasterW);
        // it follows the mixer's width like every strip, never narrower than its readouts (asked by the user)
        mx.Resize.Add(w => box.Width = Math.Max(MasterW, w));
        // the head's fixed row (StripHeadH): the button, then LIM and MONO side by side
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(MasterButton());
        mx.Lim = new TextBlock { Text = "LIM", Classes = { "mono" }, FontSize = 9, FontWeight = FontWeight.Bold, LetterSpacing = 0.8, Foreground = Palette.Accent, Opacity = 0.25,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(mx.Lim, Loc.T("mix.master.lim.tip"));
        var mono = StripToggle("MONO", Loc.T("mix.master.mono.tip"), 14, () => engine.MonoCheck, false, () =>
        {
            engine.MonoCheck = !engine.MonoCheck;
            RefreshMixer();
        });
        mono.Width = 32;
        mono.CornerRadius = new CornerRadius(4);
        ((TextBlock)mono.Content!).FontSize = 8;
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { mx.Lim, mono } });
        mx.Master = new MixerMeter(wide: true);
        // the foot shows the output's short-term LUFS (the transport's measurement), where "Salida" was, and under it
        // the correlation (two short lines in the fixed StripFootH)
        mx.Lufs = new TextBlock { Text = "—", Classes = { "mono" }, FontSize = 10, LineHeight = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mx.LufsTenths = int.MaxValue;
        var lufs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
            Children = { mx.Lufs, new TextBlock { Text = "LUFS", FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center } } };
        ToolTip.SetTip(lufs, Loc.T("mix.master.lufs.tip"));
        mx.Cor = new TextBlock { Text = "—", Classes = { "mono" }, FontSize = 9, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
        mx.CorHundredths = int.MaxValue;
        var cor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 10, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
            Children = { mx.Cor, new TextBlock { Text = "COR", FontSize = 8, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center } } };
        ToolTip.SetTip(cor, Loc.T("mix.master.cor.tip"));
        var output = new StackPanel { Children = { lufs, cor } };
        var controls = new Grid();
        var o = MasterSlots();
        var (list, _) = SlotList(o);
        list.Children.Add(ListAddRow(o.AddTip, CompactRowH, b => ShowAddSlotMenu(o, b)));
        var fxBox = ListBox(list, false);
        mx.Refresh.Add(() => fxBox.Opacity = Audio.PlaybackEngine.FxBypass ? 0.4 : 1);
        ListSection(controls, mx.FxRows, ListHead(Loc.T("mix.list.fx")), fxBox);
        ListSection(controls, mx.SendRows, new Border(), new Border());
        box.Child = StripLayout(head, controls, mx.Master, output);
        return box;
    }
}
