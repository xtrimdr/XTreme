using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>Port of EqEditor.tsx: presets, interactive log graph with the track's live spectrum behind it, a chip per
/// band and selected band controls, with its dynamics. The graph uses the shared DSP response formulas. With
/// <c>big</c> it is the editor of the big window: the same, on a graph that takes the window's height.</summary>
public sealed class EqEditor : StackPanel
{
    EqSettings eq = new();
    int selected;
    bool filling;
    bool customPreset;
    readonly SelectButton preset = new() { Height = 26, MinHeight = 26 };
    readonly SelectButton type = new() { Height = 28, MinHeight = 28 };
    readonly PowerButton power;
    readonly Button[] chips = new Button[TrackEq.BandCount];
    /// <summary>The 12 and 24 dB/oct buttons of a cut, in <see cref="Slopes"/>' order.</summary>
    readonly Button[] slopeButtons = new Button[Slopes.Length];
    static readonly int[] Slopes = [12, 24];
    readonly EqGraph graph;
    readonly InspectorSlider freq, gain, q, dynThreshold, dynRange, dynAttack, dynRelease;
    readonly Button dynButton = new() { Classes = { "chip" }, FontSize = 11, Padding = new Thickness(8, 2), Focusable = false, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel dynBox = new() { Spacing = 10 };
    readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(LiveMs) };
    readonly double[] liveDb = new double[TrackEq.BandCount];
    /// <summary>How often the graph reads what the dynamics are doing.</summary>
    const int LiveMs = 33;
    /// <summary>The big window's graph: how tall it starts and the least it gets.</summary>
    const double BigGraphH = 340;
    public const double BigGraphMinH = 190;
    readonly StackPanel slope = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    static readonly string[] Types = ["highpass", "lowshelf", "peaking", "highshelf", "lowpass"];
    public event Action<EqSettings>? Changed;
    /// <summary>The expand button: the window opens the big view.</summary>
    public event Action? BigRequested;
    /// <summary>The big view's slot went away (<see cref="Follow"/> gave null).</summary>
    public event Action? Gone;
    /// <summary>The running effect, for the dynamics on the graph; null while it isn't in the engine.</summary>
    public Func<EqInsert?>? Live { get; init; }
    /// <summary>The big view: the settings as they are now, so it follows what changes elsewhere.</summary>
    public Func<EqSettings?>? Follow { get; init; }
    /// <summary>The graph's height in the big view.</summary>
    public double GraphHeight { set => graph.Height = value; }
    /// <summary>Its own preset row (the big window's); an effect panel has the preset bar every effect has instead (#141).</summary>
    public bool ShowsPresets { init => presetRow.IsVisible = value; }
    readonly Grid presetRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
    /// <summary>The effect windows' band controls (#187): knobs in an inner panel for the band and one for its dynamics,
    /// side by side in the full view (<c>wide</c>), instead of a column of sliders. Null in the inspector and the big window.</summary>
    readonly EqKnob? kFreq, kGain, kQ, kThreshold, kRange, kAttack, kRelease;
    readonly TextBlock? bandTitle;
    readonly Control? dynPanel;
    readonly StackPanel? dynKnobs;
    readonly bool wide;
    /// <summary>The graph's height in an effect window's full view: low enough that the band's knobs fit under it.</summary>
    const double WideGraphH = 220;
    /// <summary>The size of the band's knobs and of its dynamics'.</summary>
    const double BandKnobW = 48, DynKnobW = 40;
    /// <summary>An inner panel, as in the full view of every effect: a step down from the effect's box, the room between
    /// two and how much of the family's color its border takes.</summary>
    const double InnerAlpha = 0.35, InnerGap = 8, InnerTintLine = 0.3;

    /// <param name="big">The big window's editor: a graph that takes the window's height.</param>
    /// <param name="knobs">An effect window's editor: the band's controls as knobs, in the family's <paramref name="tint"/>.</param>
    /// <param name="wide">With <paramref name="knobs"/>, the full view: a lower graph and the two panels side by side.</param>
    public EqEditor(bool big = false, bool knobs = false, bool wide = false, Color? tint = null)
    {
        Spacing = 10;
        this.wide = wide = knobs && wide;
        graph = new EqGraph(height: big ? BigGraphH : wide ? WideGraphH : 150);
        liveTimer.Tick += (_, _) => TickLive();
        var row = presetRow;
        row.Children.Add(new TextBlock { Text = Loc.T("eq.preset"), Classes = { "dim" }, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        preset.SetItems(TrackEq.Presets.Select(p => p.Label).ToArray());
        Grid.SetColumn(preset, 1); row.Children.Add(preset); Children.Add(row);
        if (!big)
        {
            var open = new Button { Classes = { "ghost" }, Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Classes = { "icon" }, Text = "\uE740", FontSize = 12 } };
            ToolTip.SetTip(open, Loc.T("eq.big.tip"));
            open.Click += (_, _) => BigRequested?.Invoke();
            Grid.SetColumn(open, 2); row.Children.Add(open);
        }
        preset.Selected += i =>
        {
            int index = i - (customPreset ? 1 : 0);
            if (filling || index < 0) return;
            Publish(FromPreset(index, eq));
        };
        Children.Add(graph);
        graph.Selected += i => { selected = i; Set(eq); };
        graph.Edited += (i, edit) => Patch(i, edit);
        var bands = new UniformGrid { Columns = TrackEq.BandCount };
        for (int i = 0; i < TrackEq.BandCount; i++)
        {
            int index = i;
            var b = new Button { Content = (i + 1).ToString(), Classes = { "chip", "eqband", "mono" }, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0), MinHeight = 28 };
            b.Click += (_, _) => { selected = index; Set(eq); };
            b.DoubleTapped += (_, _) => Patch(index, x => x.On = !x.On);
            chips[i] = b; bands.Children.Add(b);
        }
        Children.Add(bands);
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        type.SetItems(Types.Select(t => TypeName(BiquadSpec.ParseType(t))).ToArray());
        type.Selected += i => { if (!filling && i >= 0) Patch(selected, b => { b.Type = Types[i]; b.On = true; }); };
        controls.Children.Add(type);
        power = new PowerButton(() => eq.Bands.Count > selected && eq.Bands[selected].On, () => Patch(selected, b => b.On = !b.On),
            on => Loc.T(on ? "eq.band.off" : "eq.band.on")) { Height = 28, MinHeight = 28, Margin = new Thickness(4, 0) };
        Grid.SetColumn(power, 1); controls.Children.Add(power);
        var reset = new Button { Content = Loc.T("eq.reset"), Classes = { "chip" }, FontSize = 11 };
        ToolTip.SetTip(reset, Loc.T("eq.reset.tip"));
        reset.Click += (_, _) => Patch(selected, b =>
        {
            var d = EqSettings.Defaults()[selected]; b.On = d.On; b.Type = d.Type; b.Freq = d.Freq; b.Gain = d.Gain; b.Q = d.Q; b.Slope = d.Slope;
        });
        Grid.SetColumn(reset, 2); controls.Children.Add(reset); Children.Add(controls);
        freq = new InspectorSlider(Loc.T("eq.freq"), 0, 1000, 1, 0, p => $"{TrackEq.FmtHz(FromPos(p))} Hz",
            p => Patch(selected, b => { b.Freq = FromPos(p); b.On = true; }));
        gain = new InspectorSlider(Loc.T("eq.gain"), -18, 18, 0.1, 0, v => $"{v:+0.0;-0.0;0.0} dB", v => Patch(selected, b => { b.Gain = v; b.On = true; }));
        q = new InspectorSlider(Loc.T("eq.q"), 0.3, 12, 0.05, 1, v => $"{v:0.00}", v => Patch(selected, b => { b.Q = v; b.On = true; }),
            Loc.T("eq.q.tip"));
        if (knobs)
        {
            slope.Orientation = Orientation.Vertical;
            slope.VerticalAlignment = VerticalAlignment.Center;
            slope.Margin = new Thickness(6, 0);
            slope.Children.Add(EqKnob.Label(Loc.T("eq.slope"), null, tint));
        }
        else slope.Children.Add(new TextBlock { Text = Loc.T("eq.slope"), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        for (int k = 0; k < Slopes.Length; k++)
        {
            int s = Slopes[k];
            var b = new Button { Content = $"{s} dB/oct", Classes = { "chip" }, FontSize = 11, Padding = new Thickness(5, 2) };
            ToolTip.SetTip(b, Loc.T(s == 12 ? "eq.cut.soft" : "eq.cut.firm"));
            b.Click += (_, _) => Patch(selected, x => { x.Slope = s; x.On = true; });
            slopeButtons[k] = b; slope.Children.Add(b);
        }
        ToolTip.SetTip(dynButton, Loc.T("eq.dyn.tip"));
        dynButton.Content = Loc.T("eq.dyn");
        dynButton.Click += (_, _) => Patch(selected, b => { b.Dyn = !b.Dyn; b.On = true; });
        dynThreshold = new InspectorSlider(Loc.T("eq.dyn.threshold"), EqDynamics.ThresholdMinDb, 0, 0.5, EqDynamics.DefaultThresholdDb, v => $"{v:0.0} dB", v => Patch(selected, b => b.DynThreshold = v));
        dynRange = new InspectorSlider(Loc.T("eq.dyn.range"), -EqDynamics.RangeMaxDb, EqDynamics.RangeMaxDb, 0.5, EqDynamics.DefaultRangeDb, v => $"{v:+0.0;-0.0;0.0} dB", v => Patch(selected, b => b.DynRange = v),
            Loc.T("eq.dyn.range.tip"));
        dynAttack = new InspectorSlider(Loc.T("eq.dyn.attack"), EqDynamics.AttackMinMs, EqDynamics.AttackMaxMs, 0.5, EqDynamics.DefaultAttackMs, v => $"{v:0.0} ms", v => Patch(selected, b => b.DynAttackMs = v));
        dynRelease = new InspectorSlider(Loc.T("eq.dyn.release"), EqDynamics.ReleaseMinMs, EqDynamics.ReleaseMaxMs, 5, EqDynamics.DefaultReleaseMs, v => $"{v:0} ms", v => Patch(selected, b => b.DynReleaseMs = v));
        if (!knobs)
        {
            Children.Add(freq); Children.Add(gain); Children.Add(q);
            Children.Add(slope);
            Children.Add(dynButton);
            dynBox.Children.Add(dynThreshold); dynBox.Children.Add(dynRange); dynBox.Children.Add(dynAttack); dynBox.Children.Add(dynRelease);
            Children.Add(dynBox);
            if (!big) Children.Add(new TextBlock { Text = Loc.T("eq.help"),
                FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
            return;
        }

        // the band: frequency, gain and Q (a cut's slope instead of the last two)
        static double Lin(double v, double min, double max) => (v - min) / (max - min);
        static double FromLin(double k, double min, double max) => min + Math.Clamp(k, 0, 1) * (max - min);
        static double Log(double v, double min, double max) => Math.Log(Math.Clamp(v, min, max) / min) / Math.Log(max / min);
        static double FromLog(double k, double min, double max) => min * Math.Exp(Math.Clamp(k, 0, 1) * Math.Log(max / min));
        static double Snap(double v, double step) => Math.Round(v / step) * step;
        const double qMin = 0.3, qMax = 12;
        kFreq = new EqKnob(Loc.T("eq.freq"), BandKnobW, tint, hz => ToPos(hz) / 1000, k => FromPos(Math.Round(k * 1000)), hz => $"{TrackEq.FmtHz(hz)} Hz",
            EqSettings.Defaults()[0].Freq, hz => Patch(selected, b => { b.Freq = hz; b.On = true; }));
        kGain = new EqKnob(Loc.T("eq.gain"), BandKnobW, tint, v => Lin(v, -18, 18), k => Snap(FromLin(k, -18, 18), 0.1), v => $"{v:+0.0;-0.0;0.0} dB",
            0, v => Patch(selected, b => { b.Gain = v; b.On = true; }));
        kQ = new EqKnob(Loc.T("eq.q.short"), BandKnobW, tint, v => Log(v, qMin, qMax), k => Snap(FromLog(k, qMin, qMax), 0.01), v => $"{v:0.00}",
            1, v => Patch(selected, b => { b.Q = v; b.On = true; }), Loc.T("eq.q.tip"));
        var bandRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { kFreq.Cell, kGain.Cell, kQ.Cell, slope } };
        bandTitle = EqKnob.Label("", null, tint);
        bandTitle.HorizontalAlignment = HorizontalAlignment.Left;
        var bandPanel = Inner(new StackPanel { Spacing = 6, Children = { bandTitle, bandRow } }, tint);

        // its dynamics: the switch on the panel's title, the knobs dimmed while it is off (they keep their place)
        kThreshold = new EqKnob(Loc.T("eq.dyn.threshold"), DynKnobW, tint, v => Lin(v, EqDynamics.ThresholdMinDb, 0), k => Snap(FromLin(k, EqDynamics.ThresholdMinDb, 0), 0.5),
            v => $"{v:0.0} dB", EqDynamics.DefaultThresholdDb, v => Patch(selected, b => b.DynThreshold = v), arcFrom: 0);
        kRange = new EqKnob(Loc.T("eq.dyn.range"), DynKnobW, tint, v => Lin(v, -EqDynamics.RangeMaxDb, EqDynamics.RangeMaxDb), k => Snap(FromLin(k, -EqDynamics.RangeMaxDb, EqDynamics.RangeMaxDb), 0.5),
            v => $"{v:+0.0;-0.0;0.0} dB", EqDynamics.DefaultRangeDb, v => Patch(selected, b => b.DynRange = v), Loc.T("eq.dyn.range.tip"));
        kAttack = new EqKnob(Loc.T("eq.dyn.attack"), DynKnobW, tint, v => Log(v, EqDynamics.AttackMinMs, EqDynamics.AttackMaxMs), k => Snap(FromLog(k, EqDynamics.AttackMinMs, EqDynamics.AttackMaxMs), 0.5),
            v => $"{v:0.0} ms", EqDynamics.DefaultAttackMs, v => Patch(selected, b => b.DynAttackMs = v));
        kRelease = new EqKnob(Loc.T("eq.dyn.release"), DynKnobW, tint, v => Log(v, EqDynamics.ReleaseMinMs, EqDynamics.ReleaseMaxMs), k => Snap(FromLog(k, EqDynamics.ReleaseMinMs, EqDynamics.ReleaseMaxMs), 5),
            v => $"{v:0} ms", EqDynamics.DefaultReleaseMs, v => Patch(selected, b => b.DynReleaseMs = v));
        dynKnobs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { kThreshold.Cell, kRange.Cell, kAttack.Cell, kRelease.Cell } };
        var dynTitle = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var dynLabel = EqKnob.Label(Loc.T("fx.panel.dynamics"), null, tint);
        dynLabel.HorizontalAlignment = HorizontalAlignment.Left;
        dynLabel.VerticalAlignment = VerticalAlignment.Center;
        dynTitle.Children.Add(dynLabel);
        Grid.SetColumn(dynButton, 1);
        dynTitle.Children.Add(dynButton);
        dynPanel = Inner(new StackPanel { Spacing = 6, Children = { dynTitle, dynKnobs } }, tint);

        if (wide)
        {
            var panels = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,4*") };
            dynPanel.Margin = new Thickness(InnerGap, 0, 0, 0);
            Grid.SetColumn(dynPanel, 1);
            panels.Children.Add(bandPanel); panels.Children.Add(dynPanel);
            Children.Add(panels);
        }
        else { Children.Add(bandPanel); Children.Add(dynPanel); }
    }

    /// <summary>An inner panel of an effect window, as the full view of every effect draws them.</summary>
    static Border Inner(Control child, Color? tint) => new()
    {
        Child = child, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        BorderBrush = tint is { } t ? Palette.Alpha(t, InnerTintLine) : Palette.Line, Background = Palette.Alpha(Palette.AppColor, InnerAlpha),
    };
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Live != null || Follow != null) liveTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        liveTimer.Stop();
    }

    /// <summary>What the dynamics are doing now, on the graph; only redrawn if something moved.</summary>
    void TickLive()
    {
        if (Follow != null)
        {
            var now = Follow();
            if (now == null) { liveTimer.Stop(); Gone?.Invoke(); return; }
            if (!ReferenceEquals(now, eq)) Set(now);
        }
        var insert = eq.Bands.Exists(b => b.Dyn) ? Live?.Invoke() : null;
        bool moved = false;
        for (int i = 0; i < liveDb.Length; i++)
        {
            double db = insert?.Eq.DynamicDb(i) ?? 0;
            if (Math.Abs(db - liveDb[i]) < 0.05) continue;
            liveDb[i] = db;
            moved = true;
        }
        if (moved) graph.SetDynamics(liveDb);
    }

    /// <summary>One frame of the live spectrum (dB per bin, as <see cref="SpectrumTap.Read"/> gives it), or null while
    /// the track isn't playing, so the last one falls away.</summary>
    public void PushSpectrum(float[]? bins, int sampleRate) => graph.PushSpectrum(bins, sampleRate);
    /// <summary>eqPresetOf: the factory preset these bands are exactly, or -1 ("Personalizado").</summary>
    public static int PresetIndex(EqSettings eq) => Array.FindIndex(TrackEq.Presets, p => p.Bands.Select((x, i) =>
        x.On == eq.Bands[i].On && !eq.Bands[i].Dyn && BiquadSpec.TypeToString(x.Type) == eq.Bands[i].Type && x.Freq == eq.Bands[i].Freq
        && x.Gain == eq.Bands[i].Gain && x.Q == eq.Bands[i].Q && x.Slope == eq.Bands[i].Slope).All(x => x));

    /// <summary>applyEqPreset: the preset's bands, switched on; unknown fields of the current EQ survive.</summary>
    public static EqSettings FromPreset(int index, EqSettings current)
    {
        var p = TrackEq.Presets[index];
        return new EqSettings { On = true, Bands = p.Bands.Select(b => new EqBand { On = b.On,
            Type = BiquadSpec.TypeToString(b.Type), Freq = b.Freq, Gain = b.Gain, Q = b.Q, Slope = b.Slope }).ToList(), Extra = current.Extra };
    }

    internal static double ToPos(double hz) => Math.Round(Math.Log(Math.Clamp(hz, 20, 20000) / 20) / Math.Log(1000) * 1000);
    internal static double FromPos(double pos) => Math.Round(20 * Math.Exp(pos / 1000 * Math.Log(1000)));
    static string TypeName(BiquadType t) => Loc.Or("eq.type." + t, TrackEq.TypeLabel(t));
    void Publish(EqSettings value) { Changed?.Invoke(value); }
    void Patch(int i, Action<EqBand> edit)
    {
        var next = System.Text.Json.JsonSerializer.Deserialize<EqSettings>(System.Text.Json.JsonSerializer.Serialize(eq, ProjectStore.Json), ProjectStore.Json)!;
        next.On = true; edit(next.Bands[i]); Publish(next);
    }
    public void Set(EqSettings value)
    {
        filling = true; eq = value;
        var b = eq.Bands[selected];
        int p = PresetIndex(eq);
        customPreset = p < 0;
        preset.SetItems((customPreset ? new[] { Loc.T("mix.custom.m") } : Array.Empty<string>()).Concat(TrackEq.Presets.Select(x => Loc.Or("mix.eq." + x.Id, x.Label))).ToArray());
        preset.SelectedIndex = customPreset ? 0 : p;
        ToolTip.SetTip(preset, p < 0 ? Loc.T("mix.eq.manual") : Loc.Or("mix.eq." + TrackEq.Presets[p].Id + ".hint", TrackEq.Presets[p].Hint));
        type.SelectedIndex = Array.IndexOf(Types, b.Type);
        power.Refresh();
        for (int i = 0; i < chips.Length; i++)
        {
            var band = eq.Bands[i];
            // classes, not colors from code: the hover would cover them (App.axaml's Button.chip.eqband)
            chips[i].Classes.Set("on", band.On);
            chips[i].Classes.Set("picked", i == selected);
            ToolTip.SetTip(chips[i], Loc.T("eq.chip.tip", TypeName(BiquadSpec.ParseType(band.Type)), TrackEq.FmtHz(band.Freq), band.On ? "" : Loc.T("eq.chip.off")));
        }
        freq.ResetValue = ToPos(EqSettings.Defaults()[selected].Freq);
        freq.Set(ToPos(b.Freq)); gain.Set(b.Gain); q.Set(b.Q);
        bool cut = b.Type is "highpass" or "lowpass";
        gain.IsVisible = !cut; q.IsVisible = b.Type == "peaking"; slope.IsVisible = cut;
        dynButton.IsVisible = !cut;
        dynButton.Classes.Set("on", b.Dyn && !cut);
        dynBox.IsVisible = b.Dyn && !cut;
        dynThreshold.Set(b.DynThreshold); dynRange.Set(b.DynRange); dynAttack.Set(b.DynAttackMs); dynRelease.Set(b.DynReleaseMs);
        for (int k = 0; k < Slopes.Length; k++) slopeButtons[k].Classes.Set("on", Slopes[k] == b.Slope);
        if (kFreq != null)
        {
            bandTitle!.Text = Loc.T("eq.panel.band", selected + 1, TypeName(BiquadSpec.ParseType(b.Type))).ToUpperInvariant();
            kFreq.Reset = EqSettings.Defaults()[selected].Freq;
            kFreq.Set(b.Freq); kGain!.Set(b.Gain); kQ!.Set(b.Q);
            kGain.Cell.IsVisible = !cut; kQ.Cell.IsVisible = b.Type == "peaking";
            kThreshold!.Set(b.DynThreshold); kRange!.Set(b.DynRange); kAttack!.Set(b.DynAttackMs); kRelease!.Set(b.DynReleaseMs);
            // a cut has no dynamics: the panel stays (the layout doesn't jump) but off
            dynButton.IsVisible = true;
            dynPanel!.IsEnabled = !cut;
            dynPanel.Opacity = cut ? 0.4 : 1;
            dynKnobs!.IsEnabled = b.Dyn && !cut;
            dynKnobs.Opacity = b.Dyn || cut ? 1 : 0.4;
            // compact: the knobs only while it is on, to stay short
            if (!wide) dynKnobs.IsVisible = b.Dyn && !cut;
        }
        graph.Set(eq, selected);
        filling = false;
    }
}

/// <summary>
/// One knob of the EQ's band in an effect window (#187): its label on top, the knob and its value under it, the cell every
/// effect panel uses. It works in the value's units; <c>toKnob</c> and <c>fromKnob</c> map them to the knob's 0–1 (a
/// frequency, a Q or a time on a log scale) and <c>fromKnob</c> rounds to the value's step.
/// </summary>
sealed class EqKnob
{
    public readonly StackPanel Cell;
    readonly Knob knob;
    readonly TextBlock value;
    readonly Func<double, double> toKnob;
    readonly Func<double, string> format;
    /// <summary>A cell's width (label, knob and value): the same for every knob, so the rows line up.</summary>
    const double CellW = 70;

    public EqKnob(string label, double size, Color? tint, Func<double, double> toKnob, Func<double, double> fromKnob, Func<double, string> format,
        double reset, Action<double> changed, string? tip = null, double? arcFrom = null)
    {
        this.toKnob = toKnob;
        this.format = format;
        knob = new Knob(size, label) { Min = 0, Max = 1, ResetTo = toKnob(reset), ArcFrom = arcFrom is { } a ? toKnob(a) : null, Tint = tint, HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += k => changed(fromKnob(k));
        value = new TextBlock { Classes = { "mono" }, FontSize = 11, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center };
        Cell = new StackPanel { Spacing = 5, Width = CellW, VerticalAlignment = VerticalAlignment.Bottom, Children = { Label(label, tip, null), knob, value } };
    }

    /// <summary>Where a double-click takes it (a frequency knob's depends on the band).</summary>
    public double Reset { set => knob.ResetTo = toKnob(value); }

    public void Set(double v)
    {
        knob.Value = toKnob(v);
        value.Text = format(v);
    }

    /// <summary>A cell's or a panel's label: small capitals; what it does on its tooltip, never on the knob (it would cover the drag).</summary>
    public static TextBlock Label(string text, string? tip, Color? tint)
    {
        var name = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Background = Brushes.Transparent,
            Foreground = tint is { } t ? Palette.Alpha(t, 0.85) : Palette.Faint };
        if (tint != null) name.FontWeight = FontWeight.SemiBold;
        ToolTip.SetTip(name, tip);
        return name;
    }
}

/// <summary>EqEditor.tsx EqGraph: logarithmic response, captured point drags, wheel Q/slope and double-click bands.
/// With <see cref="Interactive"/> off (the mixer's summary) it only draws: no labels, small dots for the bands that are on.</summary>
sealed class EqGraph : Control
{
    EqSettings eq = new();
    int selected, hover = -1, dragging = -1;
    Point down;
    double freq, gain;
    IPointer? pointer;
    StreamGeometry curve = new();
    StreamGeometry totalFill = new();
    StreamGeometry selectedFill = new();
    EqBandSpec[] currentBands = [];
    /// <summary>What each band's dynamics are doing now (dB).</summary>
    double[] dynamics = new double[TrackEq.BandCount];
    public event Action<int>? Selected;
    public event Action<int, Action<EqBand>>? Edited;
    readonly bool interactive;
    float[] smooth = [];
    bool hadSpectrum;
    public EqGraph(bool interactive = true, double height = 150)
    {
        this.interactive = interactive;
        Height = height; ClipToBounds = true;
    }
    double X(double hz) => Math.Log(Math.Clamp(hz, 20, 20000) / 20) / Math.Log(1000) * Bounds.Width;
    double F(double x) => EqEditor.FromPos(x / Math.Max(1, Bounds.Width) * 1000);
    double Y(double db) => (18 - db) / 36 * Height;
    double Db(double y) => 18 - y / Height * 36;
    double Handle(EqBand b) => b.Type is "highpass" or "lowpass" ? 0 : b.Gain;
    int Hit(Point p) => eq.Bands.Select((b, i) => (b, i, d: Math.Pow(X(b.Freq) - p.X, 2) + Math.Pow(Y(Handle(b)) - p.Y, 2)))
        .Where(x => x.d <= 196).OrderByDescending(x => x.b.On).ThenBy(x => x.d).Select(x => x.i).FirstOrDefault(-1);
    public void SetDynamics(double[] db)
    {
        dynamics = db;
        InvalidateVisual();
    }

    public void Set(EqSettings value, int index)
    {
        bool selectionChanged = selected != index;
        eq = value; selected = index;
        var bands = eq.Bands.Select(b => EqBandSpec.FromStrings(b.On, b.Type, b.Freq, b.Gain, b.Q, b.Slope)).ToArray();
        if (selectionChanged || !bands.SequenceEqual(currentBands)) { currentBands = bands; Rebuild(); }
        else InvalidateVisual();
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); Rebuild(); }
    void Rebuild()
    {
        var specs = new BiquadSpec[TrackEq.TotalStages];
        TrackEq.GetSpecs(true, currentBands, specs);
        curve = new StreamGeometry();
        using (var g = curve.Open())
        {
            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0 * Bounds.Width;
                double db = specs.Sum(s => Biquad.ResponseDb(s, 48000, F(x)));
                var p = new Point(x, Math.Clamp(Y(db), -2, Height + 2));
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        totalFill = Fill(specs);
        var band = selected < currentBands.Length ? TrackEq.BandSpecs(currentBands[selected]) : (BiquadSpec.Flat, BiquadSpec.Flat);
        selectedFill = Fill([band.Item1, band.Item2]);
        InvalidateVisual();
    }
    StreamGeometry Fill(BiquadSpec[] specs)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(new Point(0, Y(0)), true);
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0 * Bounds.Width;
            g.LineTo(new Point(x, Math.Clamp(Y(specs.Sum(s => Biquad.ResponseDb(s, 48000, F(x)))), -2, Height + 2)));
        }
        g.LineTo(new Point(Bounds.Width, Y(0))); g.EndFigure(true);
        return geo;
    }
    /// <summary>EqGraph's draw loop: max per 2 px column, +3 dB/oct tilt so a guitar reads flat-ish, slow fall;
    /// without a spectrum the last one falls 2 dB per frame until it is gone.</summary>
    public void PushSpectrum(float[]? bins, int sampleRate)
    {
        double w = Bounds.Width;
        if (w <= 0) return;
        int cols = (int)Math.Ceiling(w / 2) + 1;
        if (smooth.Length != cols) { smooth = new float[cols]; Array.Fill(smooth, -120f); }
        if (bins != null)
        {
            double binHz = sampleRate / 2.0 / bins.Length;
            for (int i = 0; i < cols; i++)
            {
                double f0 = Hz(i * 2, w), f1 = Hz(i * 2 + 2, w);
                int b0 = (int)Math.Floor(f0 / binHz);
                int b1 = Math.Max(b0, (int)Math.Floor(f1 / binHz));
                float m = -200;
                for (; b0 <= b1 && b0 < bins.Length; b0++) if (bins[b0] > m) m = bins[b0];
                float db = m + 3 * (float)Math.Log2(Math.Max(20, f0) / 1000);
                smooth[i] = db > smooth[i] ? db : smooth[i] - 1.2f;
            }
            hadSpectrum = true;
        }
        else if (hadSpectrum)
        {
            bool any = false;
            for (int i = 0; i < cols; i++) { smooth[i] -= 2; if (smooth[i] > -110) any = true; }
            hadSpectrum = any;
        }
        else return;
        InvalidateVisual();
    }
    static double Hz(double x, double w) => 20 * Math.Pow(1000, x / w);
    static readonly IBrush SpectrumFill = Palette.Alpha(Colors.White, 0.07);
    static readonly IPen SpectrumLine = Palette.Pen(Palette.Alpha(Colors.White, 0.18));
    /// <summary>The grid and its labels, made once: the graph redraws every frame while the spectrum moves.</summary>
    static readonly double[] GridHz = [50, 100, 200, 500, 1000, 2000, 5000, 10000];
    static readonly string[] GridHzText = [.. GridHz.Select(f => f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}")];
    static readonly int[] GridDb = [-12, -6, 0, 6, 12];
    static readonly string[] GridDbText = [.. GridDb.Select(db => $"{db:+0;-0}")];
    static readonly string[] BandNumbers = [.. Enumerable.Range(1, 16).Select(n => n.ToString())];
    static string BandNumber(int i) => i < BandNumbers.Length ? BandNumbers[i] : (i + 1).ToString();

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), new Rect(Bounds.Size), 6, 6);
        for (int i = 0; i < GridHz.Length; i++)
        {
            double f = GridHz[i];
            ctx.DrawLine(Palette.Pen(Palette.Line), new Point(X(f), 0), new Point(X(f), Height));
            if (interactive) ctx.DrawText(GridHzText[i], 9, Palette.Faint, new Point(X(f) + 2, Height - 12));
        }
        for (int i = 0; i < GridDb.Length; i++)
        {
            int db = GridDb[i];
            ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, Y(db)), new Point(Bounds.Width, Y(db)));
            if (interactive && db != 0) ctx.DrawText(GridDbText[i], 9, Palette.Faint, new Point(3, Y(db) - 10));
        }
        if (hadSpectrum)
        {
            double h = Height;
            var spectrum = new StreamGeometry();
            using (var g = spectrum.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < smooth.Length; i++) g.LineTo(new Point(i * 2, h - Math.Clamp((smooth[i] + 96) / 84, 0, 1) * h));
                g.LineTo(new Point(Bounds.Width, h));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(SpectrumFill, SpectrumLine, spectrum);
        }
        if (interactive) ctx.DrawGeometry(Palette.Alpha(Palette.AccentColor, .16), null, selectedFill);
        ctx.DrawGeometry(Palette.Alpha(Palette.AccentColor, eq.On ? .1 : .04), null, totalFill);
        ctx.DrawGeometry(null, Palette.Pen(eq.On ? Palette.Accent : Palette.Dim, interactive ? 2 : 1.5), curve);
        if (!interactive)
        {
            for (int i = 0; i < eq.Bands.Count; i++)
                if (eq.Bands[i] is { On: true } b) ctx.DrawEllipse(Palette.Alpha(Palette.AccentColor, 0.75), null, new Point(X(b.Freq), Y(Handle(b))), 2.5, 2.5);
            return;
        }
        for (int i = 0; i < eq.Bands.Count; i++)
        {
            var b = eq.Bands[i]; var p = new Point(X(b.Freq), Y(Handle(b))); double r = i == selected ? 8 : 7;
            // a dynamic band: how far it can go, and where it is now
            if (b.On && b.Dyn && b.Type is not ("highpass" or "lowpass"))
            {
                ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.5), 3), p, new Point(p.X, Y(b.Gain + b.DynRange)));
                if (i < dynamics.Length && Math.Abs(dynamics[i]) >= 0.05) ctx.DrawEllipse(Palette.Fg, null, new Point(p.X, Y(b.Gain + dynamics[i])), 3, 3);
            }
            if (i == selected) ctx.DrawEllipse(null, Palette.Pen(Palette.Fg, 1.5), p, r + 3, r + 3);
            ctx.DrawEllipse(b.On ? Palette.Accent : Palette.App, b.On ? null : Palette.Pen(Palette.Dim), p, r, r);
            ctx.DrawText(BandNumber(i), 9, b.On ? Palette.App : Palette.Dim, new Point(p.X - 3, p.Y - 6), Palette.UiBold);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!interactive) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this); int i = Hit(p);
        if (e.ClickCount == 2)
        {
            if (i >= 0) Edited?.Invoke(i, b => b.On = !b.On);
            else
            {
                int free = eq.Bands.Select((b, k) => (b, k)).Where(x => !x.b.On && x.b.Type is not ("highpass" or "lowpass"))
                    .OrderBy(x => Math.Abs(Math.Log(x.b.Freq / F(p.X)))).Select(x => x.k).FirstOrDefault(-1);
                if (free >= 0) { Selected?.Invoke(free); Edited?.Invoke(free, b => { b.On = true; b.Type = "peaking"; b.Freq = Math.Clamp(F(p.X), 20, 20000); b.Gain = Math.Round(Math.Clamp(Db(p.Y), -18, 18), 1); }); }
            }
            return;
        }
        if (i < 0) return;
        Selected?.Invoke(i); down = p; freq = eq.Bands[i].Freq; gain = eq.Bands[i].Gain;
        dragging = i; pointer = e.Pointer; pointer.Capture(this); Edited?.Invoke(i, b => b.On = true); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!interactive) return;
        var p = e.GetPosition(this); hover = Hit(p);
        if (dragging < 0) return;
        double k = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1;
        Edited?.Invoke(dragging, b =>
        {
            b.On = true; b.Freq = Math.Clamp(F(X(freq) + (p.X - down.X) * k), 20, 20000);
            if (b.Type is not ("highpass" or "lowpass")) b.Gain = Math.Round(Math.Clamp(gain + (Db(p.Y) - Db(down.Y)) * k, -18, 18), 1);
        });
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; dragging = -1; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { pointer = null; dragging = -1; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!interactive) return;
        int i = hover >= 0 ? hover : selected; bool up = e.Delta.Y > 0, fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Selected?.Invoke(i); Edited?.Invoke(i, b =>
        {
            b.On = true;
            if (b.Type is "highpass" or "lowpass") b.Slope = up ? 24 : 12;
            else if (b.Type == "peaking") b.Q = Math.Round(Math.Clamp(b.Q * Math.Pow(fine ? 1.03 : 1.12, up ? 1 : -1), 0.3, 12), 2);
            else b.Gain = Math.Round(Math.Clamp(b.Gain + (up ? 1 : -1) * (fine ? 0.1 : 0.5), -18, 18), 1);
        }); e.Handled = true;
    }
}
