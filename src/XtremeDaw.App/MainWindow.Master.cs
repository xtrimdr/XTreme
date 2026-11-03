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
/// (<see cref="MixerHost"/>, so it keeps the mixer's height and its spot in solo audio), and what the master strip's
/// button opens. It lays the master's chain out left to right, a module per effect in the order the sound goes: the
/// same list as the master strip's (<see cref="MasterSettings.Slots"/>), so adding, removing or dragging one here or
/// there is the same edit. Each module is the panel the effect has everywhere (<see cref="FxBody"/>) with its live meters
/// around it. After the Look, which writes into the chain's knobs. The master's ON turns the chain on or off, with the soft
/// clip after the Empuje or the Destino's gain, in Salida (the soft clip, the limiter and the Destino, always last and in
/// that order, as in <see cref="MixGraphBus"/>). The view is as tall as the chain. Nothing is measured: each band follows
/// its own level as it plays (<see cref="MasterBandComp"/>), so a knob is heard the moment it moves.
/// </summary>
public partial class MainWindow
{
    /// <summary>A module's live reduction: a whole effect's (<see cref="Band"/> -1) or one band of a Multibanda's (3 = the Púa).</summary>
    sealed class ModuleMeter(string id, int band, Border bar, TextBlock text)
    {
        public readonly string Id = id;
        public readonly int Band = band;
        public readonly Border Bar = bar;
        public readonly TextBlock Text = text;
        public double Show, Shown = double.NaN;
    }

    readonly List<ModuleMeter> moduleMeters = [];
    /// <summary>The gap between modules.</summary>
    const double ModuleGap = 4;
    /// <summary>An EQ module's width: its editor needs it.</summary>
    const double EqModuleW = 320;

    /// <summary>Width of the reduction bars and the reduction they show full (dB).</summary>
    const double GrBarW = 72, GrBarH = 5, GrBarFullDb = 9;
    /// <summary>The view's knobs, bigger than the strips' (asked by the user), and the column each one takes.</summary>
    const double MasterKnobSize = 44, MasterKnobColW = 64;
    /// <summary>Room left under the chain for the horizontal scroll bar of a narrow window (App.axaml: ScrollBarSize, 8).</summary>
    const double MasteringScrollRoom = 10;
    /// <summary>The chain's margin inside the view. A margin on the content, not the scroll viewer's padding: that
    /// padding is left out of the scrolled extent, so the end of the chain could not be scrolled into view.</summary>
    static readonly Thickness MasteringPad = new(14, 10);
    /// <summary>The height of both boxes' heads (the master's ON chip) and the gap under them.</summary>
    const double MasterHeadH = 30, BoxGap = 12;
    /// <summary>The accent edge along the top of a stage's card while it is on, as a strip card shows its color.</summary>
    const double StageEdgeH = 2;
    /// <summary>The live meter's readouts column: wide enough for "−88.8 LUFS", so it does not jump as the numbers change.</summary>
    const double ReadoutsW = 84;
    /// <summary>The output meter pinned at the view's right (asked by the user: always in sight, as the master in the mixer).</summary>
    readonly Border masteringPin = PinnedColumn();
    MixerMeter? masteringMeter;
    TextBlock? masterCorText;
    int masterCorShown = int.MaxValue;

    readonly List<Action> masterRefresh = [];
    readonly ScrollViewer masteringScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
    Border? masterLimBar;
    MasterBandsGraph? masterGraph;
    /// <summary>The Look panel's width.</summary>
    const double LookPanelW = 120;
    /// <summary>The knobs a look sets (the end of <see cref="MasterEdit"/>'s keys): moving one by hand leaves the look.</summary>
    static readonly HashSet<string> LookKeys = ["lowCut", "mud", "lows", "presence", "air", "tilt", "low", "mid", "high", "amount", "width", "monoBass", "pick"];
    TextBlock? masterLimText;
    /// <summary>The limiter's reduction: as <see cref="TickMixer"/> took it (dB, ≤ 0), as shown and as written.</summary>
    double masterLimDb, masterLimShow, masterLimShown = double.NaN;
    Border? masterClipBar;
    TextBlock? masterClipText, masterShortText, masterIntText, masterPeakText, masterToGoText, masterTargetText;
    /// <summary>The Volumen panel's width: the Destino's text wraps in it.</summary>
    const double VolumePanelW = 190;
    double masterClipShow, masterClipShown = double.NaN;
    /// <summary>The master's output peak this pass (linear, from <see cref="TickMixer"/>) and what the meter last wrote.</summary>
    float masterOutPeak;
    double masterShortShown = double.NaN, masterIntShown = double.NaN, masterPeakShown = double.NaN;
    bool masteringView;
    /// <summary>What social platforms normalise to; the Volumen panel tells how far the mix is.</summary>
    const double SocialLufs = -14;

    MasterSettings MasterSet => store?.Project.Settings.Master ?? new();

    static readonly (Func<double, double> To, Func<double, double> From) LimReleaseMap = LogMap(MasterLimiter.ReleaseMinMs, MasterLimiter.ReleaseMaxMs);

    static string FmtDb(double db) => $"{db:+0.0;-0.0;0.0} dB";
    /// <summary>A reduction (dB, ≥ 0) as the meters show it.</summary>
    static string FmtGr(double db) => db <= 0 ? $"{0.0:0.0} dB" : $"{-db:0.0} dB";

    void InitMastering()
    {
        InitLoudness();
        MasteringViewButton.Click += (_, _) =>
        {
            if (mixerView && masteringView && !ViewsCovered) SetMixerView(false);
            else ShowBottomView(mastering: true);
        };
    }

    /// <summary>Mixer or Mastering: both live in the mixer's place, with different content.</summary>
    void ShowBottomView(bool mastering)
    {
        masteringView = mastering;
        pianoView = false;
        SetMixerView(true);
        ApplyMasteringView();
    }

    /// <summary>Shows the mixer's or the mastering's content in <see cref="MixerHost"/> and lights the right button.</summary>
    void ApplyMasteringView()
    {
        if (!mixerView) masteringView = pianoView = false; // back on the timeline, the next Mixer is the mixer
        bool m = masteringView && mixerView, piano = PianoShown;
        masteringScroll.IsVisible = masteringPin.IsVisible = m;
        masterPin.IsVisible = mixerView && !m && !piano;
        pianoHost.IsVisible = piano;
        mixerScroll.IsVisible = !m && !piano;
        if (mixerHeader != null) mixerHeader.IsVisible = !m && !piano;
        MixerViewButton.Classes.Set("on", mixerView && !m && !piano);
        MasteringViewButton.Classes.Set("on", m);
        PianoViewButton.Classes.Set("on", piano);
        BottomSplitter.IsEnabled = AudioSplitter.IsEnabled = !m;
        if (m)
        {
            BuildMastering();
            FitMasteringHeight();
            miniTimeline.SetTracks(tracks);
        }
        else
        {
            masteringScroll.Content = masteringPin.Child = null;
            masteringMeter = null;
            if (mixerView) SetBottomRowHeight(mixerH);
            RefreshMixer();
            ApplyMixerMinHeight();
        }
    }

    /// <summary>The view takes just the chain's height (plus its scroll bar's room): taller there is nothing to show.</summary>
    void FitMasteringHeight()
    {
        if (masteringScroll.Content is not Control body) return;
        body.Measure(Size.Infinity);
        SetBottomRowHeight(Math.Ceiling(body.DesiredSize.Height + MasteringScrollRoom + (stackedViews ? 0 : miniRow.Height)));
    }

    /// <summary>The height of the place the mixer and the mastering share: the bottom row, or the row under the timeline when stacked.</summary>
    void SetBottomRowHeight(double h)
    {
        if (maxPanel == MaxPanel.Views) return; // it fills the place, whatever its height
        if (stackedViews) AudioArea.RowDefinitions[2].Height = new GridLength(Math.Max(0, h));
        else
        {
            var row = Root.RowDefinitions[3];
            row.MinHeight = Math.Min(MixerMinH, h);
            row.Height = new GridLength(h);
        }
    }

    void MasterEdit(Action<MasterSettings> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, "master:" + key);
        var m = store.Project.Settings.Master;
        string slots = MasterSlotIds;
        change(m);
        // a knob the look set, moved by hand: the look's Cuánto no longer describes it
        if (LookKeys.Contains(key[(key.LastIndexOf('.') + 1)..])) m.Look = MasterLooks.Custom;
        engine.ApplyMaster(m);
        MarkDirty();
        UpdateUndoButtons();
        // a new list of effects: the view shows it (the strip rebuilds on its own)
        if (slots != MasterSlotIds && masteringView && mixerView)
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
        if (masteringView && mixerView) BuildMastering();
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
        masterGraph = null;
        var body = MasterBody();
        // a module that grows (the compressor's fine controls) takes the view with it
        body.SizeChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (masteringScroll.IsVisible) FitMasteringHeight(); });
        masteringScroll.Content = body;
        masteringPin.Child = LiveMeter();
        RefreshMasterPanel();
    }

    /// <summary>
    /// Two groups left to right, in the order the sound goes: the master's box (title, its ON, "+ Efecto", the Look and a
    /// module per effect of its chain) and, outside it, Salida (Soft clip → Limitador → Volumen), which is always last.
    /// Arrows between the panels.
    /// </summary>
    Control MasterBody()
    {
        bool editable = store is { ReadOnly: false };
        // the head on top and the chain filling the rest, so its panels stretch to the box's full height
        var suite = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        // the title with some presence, as the mixer's, and its ON beside it (the ON and the box's edge tell its state)
        var head = new StackPanel { Orientation = Orientation.Horizontal, Height = MasterHeadH, Margin = new Thickness(0, 0, 0, BoxGap) };
        head.Children.Add(BoxTitle(Loc.T("master.title")));
        var on = OnChip(() => MasterSet.On, () => MasterEdit(m => m.On = !m.On, "on"));
        on.IsEnabled = editable;
        on.Margin = new Thickness(10, 0, 0, 0);
        on.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(on);
        var owner = MasterSlots();
        var addSlot = AddSlotButton(owner);
        addSlot.Margin = new Thickness(8, 0, 0, 0);
        addSlot.IsEnabled = editable;
        head.Children.Add(addSlot);
        suite.Children.Add(head);

        var chain = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        static TextBlock Arrow() => new() { Classes = { "icon" }, Text = "", FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
        void Add(Control panel)
        {
            if (chain.Children.Count > 0 && chain.Children[^1] is not TextBlock) chain.Children.Add(Arrow());
            chain.Children.Add(panel);
        }

        // 0. the look: a starting point with its Cuánto, or what "Sugerime" proposes for this mix
        var lookBox = new StackPanel { Spacing = 10, Width = LookPanelW };
        var lookSel = new SelectButton { IsEnabled = editable };
        lookSel.SetItems([Loc.T("master.look.custom"), .. MasterLooks.All.Select(l => Loc.Or("master.look." + l.Id, l.Label))],
            [Loc.T("master.look.custom.tip"), .. MasterLooks.All.Select(l => (string?)Loc.T("master.look." + l.Id + ".tip"))]);
        ToolTip.SetTip(lookSel, Loc.T("master.look.tip"));
        lookSel.Selected += i =>
        {
            if (i == 0) return; // "by hand" is what moving a knob gives, not something to pick
            MasterEdit(m =>
            {
                m.ApplyLook(MasterLooks.All[i - 1], MasterLooks.Get(m.Look) != null ? m.LookAmount : MasterLooks.DefaultAmount);
                m.On = true;
            }, "look");
        };
        masterRefresh.Add(() => lookSel.SelectedIndex = MasterLooks.All.ToList().FindIndex(l => l.Id == MasterSet.Look) + 1);
        lookBox.Children.Add(lookSel);
        lookBox.Children.Add(MasterKnob(Loc.T("master.look.amount"), 0, 1, MasterLooks.DefaultAmount, () => MasterSet.LookAmount,
            () => MasterLooks.Get(MasterSet.Look) != null ? $"{MasterSet.LookAmount * 100:0}%" : "—",
            v => MasterEdit(m => { if (MasterLooks.Get(m.Look) is { } l) m.ApplyLook(l, Math.Round(v * 100) / 100); }, "lookAmount"),
            Loc.T("master.look.amount.tip"), enabled: () => MasterLooks.Get(MasterSet.Look) != null));
        var suggest = new Button
        {
            Classes = { "chip" }, Focusable = false, Height = 26, MinHeight = 26, Padding = new Thickness(10, 0), FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = Loc.T("master.suggest"), IsEnabled = editable,
        };
        ToolTip.SetTip(suggest, Loc.T("master.suggest.tip"));
        suggest.Click += (_, _) => _ = Suggest(suggest);
        lookBox.Children.Add(suggest);
        Add(InfoPanel(LookIcon, Loc.T("master.look"), Loc.T("master.look.tip"), lookBox));

        // the master's chain, a module per effect; dragged, they change the order the sound goes through them
        Add(MasterModules(owner));
        Grid.SetRow(chain, 1);
        suite.Children.Add(chain);
        var suiteChain = chain;
        masterRefresh.Add(() => suiteChain.Opacity = MasterSet.On ? 1 : 0.45);

        // what the master's ON does not reach: the limiter and the meter, after the box
        var outHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Height = MasterHeadH, Margin = new Thickness(0, 0, 0, BoxGap) };
        var outTitle = BoxTitle(Loc.T("master.out"));
        ToolTip.SetTip(outTitle, Loc.T("master.out.note"));
        outHead.Children.Add(outTitle);
        chain = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        // the soft clip, after the gain and into the limiter
        var clip = new StackPanel { Spacing = 10 };
        clip.Children.Add(MasterKnob(Loc.T("master.clip.amount"), 0, SoftClipper.MaxDb, 0, () => MasterSet.ClipDb, ClipReadout,
            v => MasterEdit(m => m.ClipDb = Math.Round(v * 10) / 10, "clip"), Loc.T("master.clip.amount.tip")));
        clip.Children.Add(GrMeter(out var clipBar, out var clipText, Loc.T("master.clip.gr.tip")));
        masterClipBar = clipBar;
        masterClipText = clipText;
        masterClipShown = double.NaN;
        var clipCard = StagePanel(ClipIcon, Loc.T("master.stage.clip"), Loc.T("master.stage.clip.tip"), () => MasterSet.ClipOn,
            () => MasterEdit(m => m.ClipOn = !m.ClipOn, "clipOn"), clip);
        // it needs the master on, like the chain before it
        masterRefresh.Add(() => clipCard.Opacity = MasterSet.On ? 1 : 0.45);
        Add(clipCard);

        // the limiter, with its own ON apart from the master's
        var lim = new StackPanel { Spacing = 10 };
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
        masterLimBar = limBar;
        masterLimText = limText;
        masterLimShown = double.NaN;
        Add(StagePanel(FxIcon.Comp, Loc.T("master.stage.lim"), Loc.T("master.stage.lim.tip"), () => MasterSet.LimOn,
            () => MasterEdit(m => m.LimOn = !m.LimOn, "limOn"), lim));

        // what comes out: where it is going (Destino) and a meter
        var volume = new StackPanel { Spacing = 8, Width = VolumePanelW };
        var destino = new SelectButton { IsEnabled = editable };
        destino.SetItems(MasterLoudness.All.Select(t => Loc.Or(TargetKey(t.Id), t.Label) + (t.Lufs is { } tl ? $" · {Lufs0(tl)} LUFS" : "")).ToArray());
        ToolTip.SetTip(destino, Loc.T("master.target.tip"));
        destino.Selected += i => MasterEdit(m => m.Target = MasterLoudness.All[i].Id, "target");
        masterRefresh.Add(() => destino.SelectedIndex = Math.Max(0, MasterLoudness.All.ToList().FindIndex(t => t.Id == MasterLoudness.Get(MasterSet.Target).Id)));
        var destRow = new StackPanel { Spacing = 4 };
        destRow.Children.Add(new TextBlock { Text = Loc.T("master.target").ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint });
        destRow.Children.Add(destino);
        masterTargetText = new TextBlock { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };
        destRow.Children.Add(masterTargetText);
        volume.Children.Add(destRow);
        // how far the mix is from the Destino's loudness (the readings themselves are in the pinned meter)
        var toGo = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var toGoName = new TextBlock { FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        ToolTip.SetTip(toGoName, Loc.T("master.meter.togo.tip"));
        toGo.Children.Add(toGoName);
        masterToGoText = new TextBlock { Classes = { "mono" }, FontSize = 14, Foreground = Palette.Fg, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "—" };
        Grid.SetColumn(masterToGoText, 1); toGo.Children.Add(masterToGoText);
        masterRefresh.Add(() =>
        {
            toGoName.Text = Loc.T("master.meter.togo", Lufs0(GoalLufs));
            masterIntShown = double.NaN; // the "to go" is against the new goal
        });
        // with a Destino, the text above already says where the mix lands
        masterRefresh.Add(() => toGo.IsVisible = !HasTarget);
        volume.Children.Add(toGo);
        Add(InfoPanel(VolumeIcon, Loc.T("master.stage.meter"), Loc.T("master.stage.meter.tip"), volume));

        // Salida gets the same box as the master's, with a head as tall: both stretch to the taller one and their
        // chains fill them, so every panel's top and bottom line up across the two
        var output = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        output.Children.Add(outHead);
        Grid.SetRow(chain, 1);
        output.Children.Add(chain);
        static Border Box(Control child) => new()
        {
            Padding = new Thickness(10), BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = child,
        };
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left, Margin = MasteringPad };
        var master = Box(suite);
        // the master's box takes the accent's edge while its ON is on, as a selected strip does
        masterRefresh.Add(() => master.BorderBrush = MasterSet.On ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line);
        body.Children.Add(master);
        body.Children.Add(Arrow());
        body.Children.Add(Box(output));
        return body;
    }

    /// <summary>The stages' icons that are not effects (<see cref="FxIcon"/> takes Segoe glyphs too).</summary>
    const string LookIcon = "\ue771", WidthIcon = "\ue8ab", ClipIcon = "\ue945", VolumeIcon = "\ue767";

    /// <summary>A box's title, with the presence of the mixer's.</summary>
    static TextBlock BoxTitle(string text) => new()
    {
        Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The live output, pinned at the view's right: the master strip's meter (L/R, RMS and peak, peak hold and clip
    /// lights) beside the readings of the pass (short-term and integrated LUFS, true peak), the correlation and MONO.
    /// </summary>
    Control LiveMeter()
    {
        masteringMeter = new MixerMeter(wide: true) { Margin = new Thickness(0, 0, 12, 0) };
        var readouts = new StackPanel { Spacing = 8, Width = ReadoutsW };
        TextBlock Readout(string label, string unit, string tip)
        {
            var value = new TextBlock { Classes = { "mono" }, FontSize = 14, Foreground = Palette.Fg, Text = "—" };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { value, new TextBlock { Text = unit, FontSize = 10, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) } } };
            var col = new StackPanel { Spacing = 1, Background = Brushes.Transparent,
                Children = { new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint }, line } };
            ToolTip.SetTip(col, tip);
            readouts.Children.Add(col);
            return value;
        }
        masterShortText = Readout(Loc.T("master.meter.short"), "LUFS", Loc.T("master.meter.short.tip"));
        masterIntText = Readout(Loc.T("master.meter.int"), "LUFS", Loc.T("master.meter.int.tip"));
        masterPeakText = Readout(Loc.T("master.meter.peak"), "dBTP", Loc.T("master.meter.peak.tip"));
        masterCorText = Readout("COR", "", Loc.T("mix.master.cor.tip"));
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

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,*") };
        var title = new TextBlock { Classes = { "label" }, Text = Loc.T("master.live").ToUpperInvariant(), MinHeight = 26, Margin = new Thickness(0, 0, 0, 10) };
        ToolTip.SetTip(title, Loc.T("master.live.tip"));
        Grid.SetColumnSpan(title, 2); grid.Children.Add(title);
        Grid.SetRow(masteringMeter, 1); grid.Children.Add(masteringMeter);
        Grid.SetRow(readouts, 1); Grid.SetColumn(readouts, 1); grid.Children.Add(readouts);
        var card = StageCard(grid, null);
        card.Margin = new Thickness(StripGap / 2 + 4, MasteringPad.Top, MasteringPad.Right, 0);
        return card;
    }

    /// <summary>One stage of the chain: a card with its icon, name and ON, the accent along its top while on (its content dimmed while off).</summary>
    Border StagePanel(string icon, string title, string tip, Func<bool> on, Action toggle, Control content)
    {
        var head = StageHead(icon, title, tip, on);
        var chip = OnChip(on, toggle);
        chip.IsEnabled = store is { ReadOnly: false };
        chip.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(chip, 2); head.Children.Add(chip);
        masterRefresh.Add(() => content.Opacity = on() ? 1 : 0.45);
        return StageCard(new StackPanel { Children = { head, content } }, on);
    }

    /// <summary>A panel without ON (Look, Volumen): like a stage, without the edge.</summary>
    Border InfoPanel(string icon, string title, string tip, Control content) =>
        StageCard(new StackPanel { Children = { StageHead(icon, title, tip, null), content } }, null);

    /// <summary>A stage's head: its icon (in the accent while on) and its name; column 2 is left for its ON.</summary>
    Grid StageHead(string icon, string title, string tip, Func<bool>? on)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 10), MinHeight = 26 };
        var mark = FxIcon.Create(icon, 12);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 0, 7, 0);
        FxIcon.SetColor(mark, Palette.Accent);
        if (on != null) masterRefresh.Add(() => FxIcon.SetColor(mark, on() ? Palette.Accent : Palette.Faint));
        head.Children.Add(mark);
        var name = new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, tip);
        Grid.SetColumn(name, 1); head.Children.Add(name);
        return head;
    }

    /// <summary>A stage's card; with <paramref name="lit"/>, the accent along its top while it is on, so what is working reads at a glance.</summary>
    Border StageCard(Control content, Func<bool>? lit)
    {
        content.Margin = new Thickness(12, 10);
        var grid = new Grid { Children = { content } };
        if (lit != null)
        {
            var edge = new Border { Height = StageEdgeH, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            masterRefresh.Add(() => edge.Background = lit() ? Palette.Accent : Brushes.Transparent);
            grid.Children.Add(edge);
        }
        return new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Child = grid, VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>The ON / OFF of the master or a stage: the effects' one <see cref="PowerButton"/>, its tooltip following the state.</summary>
    PowerButton OnChip(Func<bool> on, Action click)
    {
        var b = new PowerButton(on, click);
        masterRefresh.Add(b.Refresh);
        return b;
    }

    /// <summary>A knob of the view: label on top, the knob (<see cref="MasterKnobSize"/>, smaller for the crossovers), the value under it.</summary>
    Control MasterKnob(string label, double min, double max, double reset, Func<double> get, Func<string> readout, Action<double> set, string tip,
        bool small = false, Func<bool>? enabled = null)
    {
        var knob = new Knob(small ? 30 : MasterKnobSize, tip) { Min = min, Max = max, ResetTo = reset, HorizontalAlignment = HorizontalAlignment.Center };
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
        var col = FxCell(label, knob, value);
        col.MinWidth = small ? 72 : MasterKnobColW;
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

    /// <summary>The master's chain, a module per effect in the order it sounds, each dragged by its grip to another place.</summary>
    Control MasterModules(SlotOwner o)
    {
        var slots = o.Slots();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var drag = new SlotDrag(0, sideways: true);
        drag.Moved += (id, anchor, after) => SlotsEdit(o, x => ChannelInserts.MoveNextTo(x, id, anchor, after), "move");
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            row.Children.Add(MasterModule(o, slot, nth, drag, i > 0));
        }
        if (row.Children.Count > 0) return row;
        var empty = new TextBlock { Text = Loc.T("master.empty"), FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Width = LookPanelW + 40, VerticalAlignment = VerticalAlignment.Center };
        return InfoPanel(FxIcon.Vst, Loc.T("mix.list.fx"), Loc.T("mix.slot.add.master.tip"), empty);
    }

    /// <summary>One effect of the chain: its card, with the panel it has everywhere, an arrow in front and its grip, ON and ⋯ on top.</summary>
    Control MasterModule(SlotOwner o, InsertSlot slot, int nth, SlotDrag drag, bool arrow)
    {
        string id = slot.Id, kind = slot.Type;
        bool on() => o.Slot(id)?.On == true;
        string title = SlotTitle(SlotName(kind), nth);
        var head = StageHead(kind, title, title, on);
        var chip = OnChip(on, () => SlotEdit(o, id, s => s.On = !s.On, "on"));
        var menu = SlotMenuButton(o, id);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(12, 0, 0, 0), Children = { chip, menu } };
        Grid.SetColumn(actions, 2); head.Children.Add(actions);
        var grip = new GripDots { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(grip, Loc.T("mix.slot.drag.tip"));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { grip, head } };
        Grid.SetColumn(head, 1);
        head.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right || store == null) return;
            e.Handled = true;
            ShowSlotMenu(o, id, head, null);
        };

        bool bands = kind == TrackInserts.Bands && masterGraph == null;
        var body = new StackPanel { Spacing = 8, Width = ModuleWidth(kind, bands) };
        FxBody(body, o, slot, masterRefresh, hide: bands ? ["lowX", "highX"] : null);
        if (bands) BandMeters(body, o, id);
        else if (kind is TrackInserts.Clip or TrackInserts.Limit or TrackInserts.Bands or InsertSlot.CompKind or InsertSlot.GateKind)
        {
            body.Children.Add(GrMeter(out var bar, out var text, Loc.T("master.gr.tip")));
            moduleMeters.Add(new ModuleMeter(id, -1, bar, text));
        }
        masterRefresh.Add(() => body.Opacity = on() ? 1 : 0.45);
        var card = StageCard(new StackPanel { Children = { top, body } }, on);
        var module = new StackPanel { Orientation = Orientation.Horizontal, Spacing = ModuleGap };
        if (arrow) module.Children.Add(new TextBlock { Classes = { "icon" }, Text = "\ue76c", FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ModuleGap, 0, 0, 0) });
        module.Children.Add(card);
        return drag.Add(module, id, grip);
    }

    /// <summary>A module's width: its knobs in two rows at most, the Multibanda's in one over its graph.</summary>
    static double ModuleWidth(string kind, bool bands)
    {
        if (kind == InsertSlot.EqKind) return EqModuleW;
        int knobs = FxDefs.Params(kind).Count(p => !p.Fine) + FxDefs.Common(kind).Length - (bands ? 2 : 0);
        int cols = bands ? knobs : Math.Clamp((knobs + 1) / 2, 2, 4);
        return cols * FxCellW;
    }

    /// <summary>The first Multibanda's meters: a reduction per band and the Púa, and the curves with the crossovers to drag.</summary>
    void BandMeters(StackPanel body, SlotOwner o, string id)
    {
        var meters = new UniformGrid { Rows = 1 };
        for (int i = 0; i <= MasterBands.Count; i++)
        {
            var meter = GrMeter(out var bar, out var text, Loc.T(i < MasterBands.Count ? "master.gr.tip" : "master.band.pick.gr.tip"));
            meters.Children.Add(meter);
            moduleMeters.Add(new ModuleMeter(id, i, bar, text));
        }
        body.Children.Add(meters);
        var graph = new MasterBandsGraph { IsEnabled = store is { ReadOnly: false } };
        ToolTip.SetTip(graph, Loc.T("master.cross.tip"));
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
    /// <summary>What the Volumen panel measures against: the Destino's loudness, or the social one without it.</summary>
    double GoalLufs => MasterLoudness.Get(MasterSet.Target).Lufs ?? SocialLufs;
    static string Lufs0(double v) => $"{v:0.#}".Replace('-', '−');

    /// <summary>The Recorte's value: at 0 the clip still shares the hottest peaks with the limiter (<see cref="SoftClipper"/>'s Auto).</summary>
    string ClipReadout() => MasterSet.ClipDb <= 0 ? Loc.T("master.clip.auto") : $"{MasterSet.ClipDb:0.0} dB";

    /// <summary>Under the Destino: what it is doing (measuring, the gain it found, or nothing to do).</summary>
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
    /// The Volumen panel: short-term and integrated LUFS of the master (the same <see cref="LoudnessMeter"/> as the
    /// transport's LUFS cell, so they start over together each pass), the pass's true peak and how far from −14 LUFS.
    /// Texts change only by 0,1, so it does not allocate every frame.
    /// </summary>
    void TickVolume()
    {
        var lufs = engine.Loudness;
        if (double.IsNegativeInfinity(lufs.Integrated)) masterOutPeak = 0;
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
                masterToGoText.Text = double.IsFinite(lufs.Integrated) ? FmtDb(GoalLufs - lufs.Integrated) : "—";
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

    /// <summary>From the window tick: the bands' and the limiter's live reduction, only while the view is on screen. Text changes only by 0,5 dB.</summary>
    void TickMaster()
    {
        if (!masteringScroll.IsVisible)
        {
            engine.Buses.Clip.TakeReductionDb();
            return;
        }
        Meter(-masterLimDb, ref masterLimShow, ref masterLimShown, masterLimBar, masterLimText);
        Meter(-engine.Buses.Clip.TakeReductionDb(), ref masterClipShow, ref masterClipShown, masterClipBar, masterClipText);
        TickVolume();
        // a for over the list itself: the tick doesn't allocate
        for (int i = 0; i < moduleMeters.Count; i++)
        {
            var m = moduleMeters[i];
            var insert = engine.Buses.Slots.Find(m.Id);
            double gr = insert == null ? 0 : m.Band < 0 ? insert.TakeGrDb() : insert is FxShell { Inner: BandsInsert bands } ? bands.TakeBandDb(m.Band) : 0;
            Meter(-gr, ref m.Show, ref m.Shown, m.Bar, m.Text);
            if (m.Band is >= 0 and < MasterBands.Count) masterGraph?.SetReduction(m.Band, m.Show);
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
