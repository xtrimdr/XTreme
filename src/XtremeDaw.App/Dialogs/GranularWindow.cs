using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The granular's window, in the place a plugin's window has, like the sampler's (<see cref="SamplerWindow"/>): one per
/// track, floating over the app, everything on one screen. The sample on top with the part the playhead goes over and
/// where each voice is, four cards below (the grain, how the playhead moves, the pitch, the level) and a keyboard to try
/// it. A control reads and writes its value on the <see cref="GranularSettings"/> of the moment, which
/// <see cref="Current"/> looks up each time because an undo replaces that object; every edit goes through
/// <see cref="Edit"/>. <see cref="MainWindow"/> owns the sound and the project: here there are no audio decisions.
/// </summary>
public sealed class GranularWindow : Window
{
    /// <summary>How often the voices' playheads move on the wave.</summary>
    const int HeadsMs = 33;
    const double KnobSize = 34;

    readonly List<Action> refresh = [];
    /// <summary>The knobs' lanes, hooked up once <see cref="Automate"/> is there.</summary>
    readonly List<Action> lanes = [];
    readonly SamplerWaveView wave = new();
    readonly SamplerKeysView keys = new();
    readonly GrainShapeView grain = new();
    readonly TextBlock title = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Ellipse dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock sampleName = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock sampleInfo = new() { Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock markerInfo = new() { Classes = { "dim", "mono" }, FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(HeadsMs) };
    readonly float[] heads = new float[Granular.MaxVoices];
    readonly Granular player;
    double sampleSec;

    /// <summary>The settings of the track now; null once it no longer has a granular.</summary>
    public Func<GranularSettings?> Current { get; init; } = null!;
    /// <summary>One edit, undone with the ones of the same key that follow it.</summary>
    public Action<Action<GranularSettings>, string> Edit { get; init; } = null!;
    /// <summary>Asks for a file and loads it.</summary>
    public Action PickFile { get; init; } = null!;
    /// <summary>Loads this file.</summary>
    public Action<string> LoadFile { get; init; } = null!;
    /// <summary>The file of something dragged over the window, or null.</summary>
    public Func<DragEventArgs, string?> DroppedFile { get; init; } = null!;
    /// <summary>Takes the sample out.</summary>
    public Action ClearSample { get; init; } = null!;
    /// <summary>A key pressed in this window goes to the app's shortcuts.</summary>
    public Action<KeyEventArgs> Shortcut { get; init; } = null!;
    /// <summary>The window took or left the front: the keyboards play this track while it is there.</summary>
    public Action<bool> Front { get; init; } = null!;
    /// <summary>A knob offers its lane on a right click and shows it (a position, 0..1) while it plays.</summary>
    public Action<Control, GranularKnob, Action<double>> Automate { get; init; } = null!;

    public GranularWindow(Granular player)
    {
        this.player = player;
        Width = 1060;
        Height = 720;
        MinWidth = 900;
        MinHeight = 660;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.App;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 36;
        Opened += (_, _) =>
        {
            foreach (var hook in lanes) hook();
        };
        Content = Build();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool can = IsEnabledForEdits && DroppedFile(e) != null;
            e.DragEffects = can ? DragDropEffects.Copy : DragDropEffects.None;
            wave.DropHot = can;
            e.Handled = true;
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => wave.DropHot = false);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            wave.DropHot = false;
            e.Handled = true;
            if (DroppedFile(e) is { } file) LoadFile(file);
        });
        AddHandler(KeyDownEvent, (_, e) => Shortcut(e), RoutingStrategies.Tunnel);
        clock.Tick += (_, _) => wave.SetHeads(heads.AsSpan(0, player.Heads(heads)));
        Opened += (_, _) => clock.Start();
        Closed += (_, _) => clock.Stop();
        Activated += (_, _) => Front(true);
        Deactivated += (_, _) => Front(false);
    }

    /// <summary>Read only: everything shows, nothing changes; the keyboard still plays.</summary>
    public bool IsEnabledForEdits { get; set; } = true;

    Control Build()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("36,Auto,*,Auto") };
        var header = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        header.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 150, 0), IsHitTestVisible = false, Children = { dot, title } };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        root.Children.Add(header);

        var top = new StackPanel { Spacing = 6, Margin = new Thickness(10, 10, 10, 8) };
        top.Children.Add(SampleBar());
        wave.SetEmpty(Loc.T("sampler.drop"), Loc.T("sampler.drop.hint"));
        wave.PickRequested += () => PickFile();
        wave.MarkerMoved += (m, f, _) =>
        {
            if (m == SamplerWaveView.Marker.Start) Edit(g => g.Start = f, "start");
            else if (m == SamplerWaveView.Marker.End) Edit(g => g.End = f, "end");
        };
        ToolTip.SetTip(wave, Loc.T("granular.wave.tip"));
        top.Children.Add(wave);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bar.Children.Add(markerInfo);
        var whole = new Button { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), FontSize = 11, Content = Loc.T("sampler.view.all"), Focusable = false };
        ToolTip.SetTip(whole, Loc.T("sampler.view.all.tip"));
        whole.Click += (_, _) => wave.ShowAll();
        wave.ViewChanged += () => whole.IsEnabled = !wave.ShowsAll;
        whole.IsEnabled = false;
        Grid.SetColumn(whole, 1);
        bar.Children.Add(whole);
        top.Children.Add(bar);
        refresh.Add(() =>
        {
            if (Current() is not { } g) return;
            wave.SetMarkers(g.Start, g.End, false, 0, 1);
            markerInfo.Text = sampleSec <= 0 ? "" : Loc.T("sampler.span", Time(g.Start), Time(g.End));
        });
        Grid.SetRow(top, 1);
        root.Children.Add(top);

        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8, Margin = new Thickness(10, 0, 10, 8) };
        Control[] all = [GrainCard(), PlayheadCard(), PitchCard(), AmpCard()];
        for (int i = 0; i < all.Length; i++)
        {
            Grid.SetColumn(all[i], i);
            cards.Children.Add(all[i]);
        }
        Grid.SetRow(cards, 2);
        root.Children.Add(cards);

        keys.Pressed += (p, v) => player.Press(p, v);
        keys.Lifted += p => player.Lift(p);
        keys.RootPicked += p => Edit(g => g.Root = p, "root");
        ToolTip.SetTip(keys, Loc.T("sampler.keys.tip"));
        refresh.Add(() => { if (Current() is { } g) keys.Root = g.Root; });
        var bottom = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6), Child = keys };
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        return root;
    }

    string Time(double fraction) => (fraction * sampleSec).ToString("0.000", CultureInfo.CurrentCulture) + " s";

    Control SampleBar()
    {
        var load = new Button { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Content = Labeled("", Loc.T("sampler.load")) };
        ToolTip.SetTip(load, Loc.T("sampler.load.tip"));
        load.Click += (_, _) => PickFile();
        var clear = new Button { Classes = { "chip" }, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 } };
        ToolTip.SetTip(clear, Loc.T("sampler.clear"));
        clear.Click += (_, _) => ClearSample();
        refresh.Add(() => clear.IsEnabled = Current()?.MediaId != null);
        var normalize = Toggle(Loc.T("sampler.normalize"), Loc.T("sampler.normalize.tip"), g => g.Normalize, (g, on) => g.Normalize = on, "normalize");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { sampleName, sampleInfo } });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { normalize, load, clear } };
        Grid.SetColumn(tools, 2);
        grid.Children.Add(tools);
        return grid;
    }

    // --- the four cards -------------------------------------------------------------------------

    Control GrainCard()
    {
        var box = Card(Loc.T("granular.card.grain"));
        ToolTip.SetTip(grain, Loc.T("granular.shape.view.tip"));
        refresh.Add(() => { if (Current() is { } g) grain.Set(g.Shape, g.Skew); });
        box.Children.Add(grain);
        var density = Dial(Loc.T("granular.density"), Loc.T("granular.density.tip"), Scale.Exponential(GranularSettings.MinDensity, GranularSettings.MaxDensity), 20,
            g => g.Density, (g, v) => g.Density = v, PerSec, "density", GranularKnob.Density);
        refresh.Add(() => density.IsEnabled = Current()?.DensityBeat == 0);
        var sync = new SelectButton { Height = 26, MinHeight = 26 };
        sync.SetItems([.. GranularSettings.DensityBeats.Select(BeatText)]);
        ToolTip.SetTip(sync, Loc.T("granular.densitySync.tip"));
        bool filling = false;
        sync.Selected += i =>
        {
            if (!filling && i >= 0) Edit(g => g.DensityBeat = GranularSettings.DensityBeats[i], "densityBeat");
        };
        refresh.Add(() =>
        {
            filling = true;
            if (Current() is { } g) sync.SelectedIndex = Array.IndexOf(GranularSettings.DensityBeats, g.DensityBeat);
            filling = false;
        });
        box.Children.Add(Row(Loc.T("granular.densitySync"), sync));
        box.Children.Add(Dials(
            density,
            Dial(Loc.T("granular.size"), Loc.T("granular.size.tip"), Scale.Exponential(GranularSettings.MinSizeSec, GranularSettings.MaxSizeSec), 0.08,
                g => g.SizeSec, (g, v) => g.SizeSec = v, Sec, "size", GranularKnob.Size),
            Dial(Loc.T("granular.jitter"), Loc.T("granular.jitter.tip"), Scale.Linear(0, 1), 0, g => g.Jitter, (g, v) => g.Jitter = v, Percent, "jitter", GranularKnob.Jitter)));
        box.Children.Add(Dials(
            Dial(Loc.T("granular.shape"), Loc.T("granular.shape.tip"), Scale.Linear(0, 1), 0.5, g => g.Shape, (g, v) => g.Shape = v, Percent, "shape"),
            Dial(Loc.T("granular.skew"), Loc.T("granular.skew.tip"), Scale.Linear(-1, 1), 0, g => g.Skew, (g, v) => g.Skew = Math.Abs(v) < 0.04 ? 0 : v, Signed, "skew")));
        return Wrap(box);
    }

    Control PlayheadCard()
    {
        var box = Card(Loc.T("granular.card.playhead"));
        box.Children.Add(Seg([(GranularSettings.Stretch, Loc.T("granular.mode.stretch"), Loc.T("granular.mode.stretch.tip")), (GranularSettings.Scrub, Loc.T("granular.mode.scrub"), Loc.T("granular.mode.scrub.tip"))],
            g => g.Mode, (g, v) => g.Mode = v, "mode"));
        var speed = Dial(Loc.T("granular.speed"), Loc.T("granular.speed.tip"), Scale.Power(GranularSettings.MaxSpeed, 2), 1, g => g.Speed, (g, v) => g.Speed = Math.Abs(v - 1) < 0.03 ? 1 : v, Times, "speed", GranularKnob.Speed);
        box.Children.Add(Dials(
            Dial(Loc.T("granular.position"), Loc.T("granular.position.tip"), Scale.Linear(0, 1), 0, g => g.Position, (g, v) => g.Position = v, Percent, "position", GranularKnob.Position),
            speed,
            Dial(Loc.T("granular.spray"), Loc.T("granular.spray.tip"), Scale.Power(GranularSettings.MaxSpraySec, 2), 0, g => g.SpraySec, (g, v) => g.SpraySec = v, Sec, "spray", GranularKnob.Spray)));
        var sync = Toggle(Loc.T("granular.speedSync"), Loc.T("granular.speedSync.tip"), g => g.SpeedSync, (g, on) => g.SpeedSync = on, "speedSync");
        var reverse = Toggle(Loc.T("sampler.reverse"), Loc.T("granular.reverse.tip"), g => g.Reverse, (g, on) => g.Reverse = on, "reverse");
        box.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { sync, reverse } });
        var bpm = new ValueBar((int)GranularSettings.MinSampleBpm, (int)GranularSettings.MaxSampleBpm, 120, v => $"{v} BPM") { Height = 26 };
        ToolTip.SetTip(bpm, Loc.T("granular.sampleBpm.tip"));
        bpm.Moved += v => Edit(g => g.SampleBpm = v, "sampleBpm");
        refresh.Add(() => { if (Current() is { } g) bpm.Value = (int)Math.Round(g.SampleBpm); });
        box.Children.Add(Row(Loc.T("granular.sampleBpm"), bpm));
        refresh.Add(() =>
        {
            bool stretch = Current()?.Mode != GranularSettings.Scrub;
            speed.IsEnabled = sync.IsEnabled = reverse.IsEnabled = stretch;
            bpm.IsEnabled = stretch && Current()?.SpeedSync == true;
        });
        return Wrap(box);
    }

    Control PitchCard()
    {
        var box = Card(Loc.T("granular.card.pitch"));
        var root = new ValueBar(0, 127, 60, NoteNames.Of) { Height = 26 };
        ToolTip.SetTip(root, Loc.T("sampler.root.tip"));
        root.Moved += v => Edit(g => g.Root = v, "root");
        refresh.Add(() => { if (Current() is { } g) root.Value = g.Root; });
        box.Children.Add(Row(Loc.T("sampler.root"), root));
        var tune = new ValueBar((int)-GranularSettings.MaxTune, (int)GranularSettings.MaxTune, 0, v => v == 0 ? "0 st" : v.ToString("+0;-0") + " st") { Height = 26 };
        ToolTip.SetTip(tune, Loc.T("sampler.tune.tip"));
        tune.Moved += v => Edit(g => g.Transpose = v, "transpose");
        refresh.Add(() => { if (Current() is { } g) tune.Value = g.Transpose; });
        box.Children.Add(Row(Loc.T("sampler.tune"), tune));
        var voices = new ValueBar(1, Granular.MaxVoices, 8, v => v == 1 ? Loc.T("sampler.mono") : v.ToString()) { Height = 26 };
        ToolTip.SetTip(voices, Loc.T("granular.voices.tip"));
        voices.Moved += v => Edit(g => g.Voices = v, "voices");
        refresh.Add(() => { if (Current() is { } g) voices.Value = g.Voices; });
        box.Children.Add(Row(Loc.T("sampler.voices"), voices));
        box.Children.Add(Toggle(Loc.T("sampler.keyTrack"), Loc.T("sampler.keyTrack.tip"), g => g.KeyTrack, (g, on) => g.KeyTrack = on, "keyTrack"));
        box.Children.Add(Dials(
            Dial(Loc.T("sampler.fine"), Loc.T("sampler.fine.tip"), Scale.Linear(-100, 100), 0, g => g.Fine, (g, v) => g.Fine = v, v => $"{v:+0;-0;0} ct", "fine"),
            Dial(Loc.T("granular.spread"), Loc.T("granular.spread.tip"), Scale.Linear(0, 1), 0, g => g.Spread, (g, v) => g.Spread = v, Percent, "spread", GranularKnob.Spread)));
        return Wrap(box);
    }

    Control AmpCard()
    {
        var box = Card(Loc.T("sampler.card.amp"));
        var env = new SamplerEnvelopeView(GranularSettings.MaxEnvSec);
        env.Moved += (a, d, s, r) => Edit(x => (x.Attack, x.Decay, x.Sustain, x.Release) = (a, d, s, r), "ampEnv");
        refresh.Add(() => { if (Current() is { } g) env.Set(g.Attack, g.Decay, g.Sustain, g.Release); });
        box.Children.Add(env);
        var time = Scale.Power(GranularSettings.MaxEnvSec, 4);
        box.Children.Add(Dials(
            Dial("A", Loc.T("sampler.attack"), time, 0.01, g => g.Attack, (g, v) => g.Attack = v, Sec, "attack"),
            Dial("D", Loc.T("sampler.decay"), time, 0.1, g => g.Decay, (g, v) => g.Decay = v, Sec, "decay"),
            Dial("S", Loc.T("sampler.sustain"), Scale.Linear(0, 1), 1, g => g.Sustain, (g, v) => g.Sustain = v, Percent, "sustain"),
            Dial("R", Loc.T("sampler.release"), time, 0.3, g => g.Release, (g, v) => g.Release = v, Sec, "release")));
        box.Children.Add(Dials(
            Dial(Loc.T("sampler.gain"), Loc.T("sampler.gain.tip"), Scale.Linear(GranularSettings.MinGainDb, GranularSettings.MaxGainDb), 0, g => g.GainDb, (g, v) => g.GainDb = v, Db, "gain", GranularKnob.Gain),
            Dial(Loc.T("sampler.pan"), Loc.T("sampler.pan.tip"), Scale.Linear(-1, 1), 0, g => g.Pan, (g, v) => g.Pan = Math.Abs(v) < 0.04 ? 0 : v, ValueFormat.Pan, "pan", GranularKnob.Pan),
            Dial(Loc.T("sampler.velAmp"), Loc.T("sampler.velAmp.tip"), Scale.Linear(0, 1), 0.7, g => g.VelAmp, (g, v) => g.VelAmp = v, Percent, "velAmp")));
        return Wrap(box);
    }

    // --- the parts --------------------------------------------------------------------------------

    /// <summary>How a knob's travel maps to its value: times and rates need room at the bottom end.</summary>
    readonly record struct Scale(Func<double, double> ToValue, Func<double, double> ToKnob)
    {
        public static Scale Linear(double min, double max) => new(x => min + x * (max - min), v => (v - min) / (max - min));
        public static Scale Power(double max, double power) => new(x => Math.Pow(x, power) * max, v => Math.Pow(Math.Max(0, v) / max, 1 / power));
        public static Scale Exponential(double min, double max) => new(x => min * Math.Pow(max / min, x), v => Math.Log(v / min) / Math.Log(max / min));
    }

    static StackPanel Card(string name)
    {
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(new TextBlock { Text = name, Classes = { "label" } });
        return box;
    }

    static Control Wrap(Control box) => new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = box } };

    static Control Row(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("62,*") };
        grid.Children.Add(new TextBlock { Text = label, Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    static Control Dials(params Control[] cells)
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var c in cells) row.Children.Add(c);
        return row;
    }

    /// <summary>A knob with its name and its value. <paramref name="reset"/> is what a double click gives back; with <paramref name="lane"/> it can be automated.</summary>
    Control Dial(string label, string tip, Scale scale, double reset, Func<GranularSettings, double> get, Action<GranularSettings, double> set, Func<double, string> text, string key, GranularKnob? lane = null)
    {
        var value = new TextBlock { Classes = { "dim", "mono" }, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
        var knob = new Knob(KnobSize, tip) { Min = 0, Max = 1, ResetTo = Math.Clamp(scale.ToKnob(reset), 0, 1) };
        knob.Changed += x => Edit(g => set(g, scale.ToValue(x)), key);
        refresh.Add(() =>
        {
            if (Current() is not { } g) return;
            knob.Value = Math.Clamp(scale.ToKnob(get(g)), 0, 1);
            value.Text = text(get(g));
        });
        var cell = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        cell.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        knob.HorizontalAlignment = HorizontalAlignment.Center;
        cell.Children.Add(knob);
        cell.Children.Add(value);
        if (lane is { } moved)
            lanes.Add(() => Automate(cell, moved, k =>
            {
                knob.Value = k;
                value.Text = text(GranularKnobs.Value(moved, k));
            }));
        return cell;
    }

    ToggleButton Toggle(string text, string tip, Func<GranularSettings, bool> get, Action<GranularSettings, bool> set, string key)
    {
        var b = new ToggleButton { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Content = text, Focusable = false };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            bool on = b.IsChecked == true;
            Edit(g => set(g, on), key);
        };
        refresh.Add(() => { if (Current() is { } g) b.IsChecked = get(g); });
        return b;
    }

    Control Seg((string Value, string Text, string? Tip)[] options, Func<GranularSettings, string> get, Action<GranularSettings, string> set, string key)
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var (value, text, tip) in options)
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = text, Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Focusable = false };
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Edit(g => set(g, value), key);
            refresh.Add(() => { if (Current() is { } g) b.IsChecked = get(g) == value; });
            row.Children.Add(b);
        }
        return new Border { Classes = { "seg" }, Child = row, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    static StackPanel Labeled(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children =
        {
            new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    static string Percent(double v) => $"{v * 100:0}%";
    static string Signed(double v) => $"{v * 100:+0;-0;0}%";
    static string Sec(double v) => v < 1 ? $"{v * 1000:0} ms" : $"{v:0.00} s";
    static string PerSec(double v) => v < 10 ? $"{v:0.0} /s" : $"{v:0} /s";
    static string Times(double v) => $"{v * 100:0}%";
    static string Db(double v) => $"{v:+0.0;-0.0;0.0} dB";

    /// <summary>A knob's value as its lane reads it, the way the knob shows it.</summary>
    public static string KnobText(GranularKnob knob, double v) => knob switch
    {
        GranularKnob.Density => PerSec(v),
        GranularKnob.Size or GranularKnob.Spray => Sec(v),
        GranularKnob.Gain => Db(v),
        GranularKnob.Pan => ValueFormat.Pan(v),
        _ => Percent(v),
    };

    static string BeatText(double beats) => beats switch
    {
        0 => Loc.T("sampler.lfoSync.free"),
        1 => "1/4",
        0.5 => "1/8",
        0.25 => "1/16",
        0.125 => "1/32",
        0.0625 => "1/64",
        _ => "1/128",
    };

    // --- what the app shows here ------------------------------------------------------------------

    /// <summary>Whose it is: the title and the track's color.</summary>
    public void ShowTrack(string name, string? color)
    {
        title.Text = Title = $"{Loc.T("granular.name")} · {name}";
        dot.Fill = color != null ? Palette.Brush(color) : Palette.Faint;
    }

    /// <summary>The sample on screen; null peaks = none. <paramref name="state"/> says why, when something is wrong or on its way.</summary>
    public void ShowSample(string? name, byte[]? peaks, float[]? left, float[]? right, double seconds, int sampleRate, int channels, string? state)
    {
        sampleSec = seconds;
        sampleName.Text = name ?? Loc.T("sampler.none");
        sampleInfo.Text = state ?? (peaks == null ? "" : $"{seconds.ToString("0.00", CultureInfo.CurrentCulture)} s · {sampleRate / 1000.0:0.#} kHz · {(channels > 1 ? Loc.T("sampler.stereo") : Loc.T("sampler.monoFile"))}");
        wave.SetSample(peaks, left, right, false);
        Refresh();
    }

    /// <summary>Every control shows the settings as they are now. Setting a value here never counts as an edit.</summary>
    public void Refresh()
    {
        foreach (var r in refresh) r();
    }
}
