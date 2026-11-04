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
/// The sampler's window, in the place a plugin's window has: one per track, floating over the app, so the piano roll and
/// the mixer stay in sight. Everything fits on one screen, no tabs (asked by the user: nice, functional and easy): the
/// sample on top, four cards below, and a keyboard to try it. A control knows how to read and write its value on the
/// <see cref="SamplerSettings"/> of the moment, which <see cref="Current"/> looks up each time because an undo replaces
/// that object; every edit goes through <see cref="Edit"/>, and <see cref="Refresh"/> shows what is there now.
/// <see cref="MainWindow"/> owns the sound and the project: here there are no audio decisions.
/// </summary>
public sealed class SamplerWindow : Window
{
    /// <summary>How often the voices' heads move on the wave.</summary>
    const int HeadsMs = 33;
    const double KnobSize = 34;
    /// <summary>How wide the view goes on a loop point: some 40 ms, a few cycles of a low note.</summary>
    const double LoopCloseFrames = 2000;

    readonly List<Action> refresh = [];
    /// <summary>The knobs' lanes, hooked up once <see cref="Automate"/> is there.</summary>
    readonly List<Action> lanes = [];
    readonly SamplerWaveView wave = new();
    readonly SamplerKeysView keys = new();
    readonly TextBlock title = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Ellipse dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock sampleName = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock sampleInfo = new() { Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock markerInfo = new() { Classes = { "dim", "mono" }, FontSize = 11, Foreground = Palette.Faint };
    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(HeadsMs) };
    readonly float[] heads = new float[Sampler.MaxVoices];
    readonly TextBlock pitchText = new() { Classes = { "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button pitchFix = new() { Classes = { "chip" }, Height = 26, MinHeight = 26, Padding = new Thickness(8, 0), FontSize = 11, Focusable = false };
    readonly Sampler player;
    double sampleSec;
    float[]? soundLeft, soundRight;
    bool soundReversed;
    /// <summary>The flags land on zero crossings; a preference of this machine, on by default.</summary>
    bool zeroSnap = AppConfig.Get("samplerZero") != "0";
    (int Key, double Cents)? heard;
    bool measuring;
    /// <summary>Under this many cents it counts as in tune: no ear tells that much on a held note.</summary>
    const double TunedCents = 2;

    /// <summary>The settings of the track now; null once it no longer has a sampler.</summary>
    public Func<SamplerSettings?> Current { get; init; } = null!;
    /// <summary>One edit, undone with the ones of the same key that follow it.</summary>
    public Action<Action<SamplerSettings>, string> Edit { get; init; } = null!;
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
    public Action<Control, SamplerKnob, Action<double>> Automate { get; init; } = null!;

    public SamplerWindow(Sampler player)
    {
        this.player = player;
        Width = 1060;
        Height = 730;
        MinWidth = 900;
        MinHeight = 670;
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
        wave.MarkerMoved += (m, f, free) =>
        {
            double point = ZeroPoint(m, f, free);
            switch (m)
            {
                case SamplerWaveView.Marker.Start: Edit(s => s.Start = point, "start"); break;
                case SamplerWaveView.Marker.End: Edit(s => s.End = point, "end"); break;
                case SamplerWaveView.Marker.LoopStart: Edit(s => s.LoopStart = point, "loopStart"); break;
                case SamplerWaveView.Marker.LoopEnd: Edit(s => s.LoopEnd = point, "loopEnd"); break;
            }
        };
        top.Children.Add(wave);
        top.Children.Add(WaveBar());
        refresh.Add(() =>
        {
            if (Current() is not { } s) return;
            wave.SetMarkers(s.Start, s.End, s.Mode == SamplerSettings.Gate && s.Loop != SamplerSettings.LoopOff, s.LoopStart, s.LoopEnd);
            string span = sampleSec <= 0 ? "" : Loc.T("sampler.span", Time(s.Start), Time(s.End));
            bool looped = sampleSec > 0 && s.Mode == SamplerSettings.Gate && s.Loop != SamplerSettings.LoopOff;
            markerInfo.Text = looped ? $"{span} · {Loc.T("sampler.loopSpan", Time(s.LoopStart), Time(s.LoopEnd))}" : span;
        });
        Grid.SetRow(top, 1);
        root.Children.Add(top);

        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8, Margin = new Thickness(10, 0, 10, 8) };
        cards.Children.Add(PlayCard());
        var amp = AmpCard();
        Grid.SetColumn(amp, 1);
        cards.Children.Add(amp);
        var filter = FilterCard();
        Grid.SetColumn(filter, 2);
        cards.Children.Add(filter);
        var mod = ModCard();
        Grid.SetColumn(mod, 3);
        cards.Children.Add(mod);
        Grid.SetRow(cards, 2);
        root.Children.Add(cards);

        keys.Pressed += (p, v) => player.Press(p, v);
        keys.Lifted += p => player.Lift(p);
        keys.RootPicked += p => Edit(s => s.Root = p, "root");
        ToolTip.SetTip(keys, Loc.T("sampler.keys.tip"));
        refresh.Add(() => { if (Current() is { } s) keys.Root = s.Root; });
        var bottom = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6), Child = keys };
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        return root;
    }

    string Time(double fraction) => (fraction * sampleSec).ToString("0.000", CultureInfo.CurrentCulture) + " s";

    /// <summary>Under the wave: where the flags are, the ways to reach a loop point up close, and the magnet that lands the flags on zero crossings.</summary>
    Control WaveBar()
    {
        Button Chip(string text, string tip, Action click)
        {
            var b = new Button { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), FontSize = 11, Content = text, Focusable = false };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        var whole = Chip(Loc.T("sampler.view.all"), Loc.T("sampler.view.all.tip"), wave.ShowAll);
        var from = Chip(Loc.T("sampler.view.loopStart"), Loc.T("sampler.view.loop.tip"), () => wave.ShowAround(wave.LoopStart, LoopCloseFrames));
        var to = Chip(Loc.T("sampler.view.loopEnd"), Loc.T("sampler.view.loop.tip"), () => wave.ShowAround(wave.LoopEnd, LoopCloseFrames));
        var zero = new ToggleButton { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), FontSize = 11, Content = Loc.T("sampler.zero"), Focusable = false, IsChecked = zeroSnap };
        ToolTip.SetTip(zero, Loc.T("sampler.zero.tip"));
        wave.ShowCrossings = zeroSnap;
        zero.Click += (_, _) =>
        {
            wave.ShowCrossings = zeroSnap = zero.IsChecked == true;
            try { AppConfig.Set("samplerZero", zeroSnap ? "1" : "0"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* View preference only. */ }
        };
        void ShowWhole() => whole.IsEnabled = !wave.ShowsAll;
        wave.ViewChanged += ShowWhole;
        ShowWhole();
        refresh.Add(() =>
        {
            bool looped = sampleSec > 0 && Current() is { Mode: SamplerSettings.Gate } s && s.Loop != SamplerSettings.LoopOff;
            from.IsEnabled = to.IsEnabled = looped;
            zero.IsEnabled = sampleSec > 0;
        });
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        markerInfo.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(markerInfo);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { whole, from, to, zero } };
        Grid.SetColumn(tools, 1);
        bar.Children.Add(tools);
        return bar;
    }

    Control SampleBar()
    {
        var load = new Button { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Content = Labeled("", Loc.T("sampler.load")) };
        ToolTip.SetTip(load, Loc.T("sampler.load.tip"));
        load.Click += (_, _) => PickFile();
        var clear = new Button { Classes = { "chip" }, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 } };
        ToolTip.SetTip(clear, Loc.T("sampler.clear"));
        clear.Click += (_, _) => ClearSample();
        refresh.Add(() => clear.IsEnabled = Current()?.MediaId != null);
        var reverse = Toggle(Loc.T("sampler.reverse"), Loc.T("sampler.reverse.tip"), s => s.Reverse, (s, on) => s.Reverse = on, "reverse");
        var normalize = Toggle(Loc.T("sampler.normalize"), Loc.T("sampler.normalize.tip"), s => s.Normalize, (s, on) => s.Normalize = on, "normalize");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { sampleName, sampleInfo } });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { reverse, normalize, load, clear } };
        Grid.SetColumn(tools, 2);
        grid.Children.Add(tools);
        return grid;
    }

    // --- the four cards -------------------------------------------------------------------------

    Control PlayCard()
    {
        var box = Card(Loc.T("sampler.card.play"));
        box.Children.Add(Row(Loc.T("sampler.mode"), Seg([(SamplerSettings.Gate, Loc.T("sampler.mode.gate"), Loc.T("sampler.mode.gate.tip")), (SamplerSettings.OneShot, Loc.T("sampler.mode.oneShot"), Loc.T("sampler.mode.oneShot.tip"))],
            s => s.Mode, (s, v) => s.Mode = v, "mode")));
        var loop = Seg([(SamplerSettings.LoopOff, Loc.T("sampler.loop.off"), null), (SamplerSettings.LoopForward, "→", Loc.T("sampler.loop.forward")), (SamplerSettings.LoopPingPong, "↔", Loc.T("sampler.loop.pingPong"))],
            s => s.Loop, (s, v) => s.Loop = v, "loop");
        box.Children.Add(Row(Loc.T("sampler.loop"), loop));
        refresh.Add(() => loop.IsEnabled = Current()?.Mode == SamplerSettings.Gate);
        var root = new ValueBar(0, 127, 60, NoteName) { Height = 26 };
        ToolTip.SetTip(root, Loc.T("sampler.root.tip"));
        root.Moved += v => Edit(s => s.Root = v, "root");
        refresh.Add(() => { if (Current() is { } s) root.Value = s.Root; });
        box.Children.Add(Row(Loc.T("sampler.root"), root));
        box.Children.Add(Row(Loc.T("sampler.pitch"), PitchRow()));
        var tune = new ValueBar((int)-SamplerSettings.MaxTune, (int)SamplerSettings.MaxTune, 0, v => v == 0 ? "0 st" : v.ToString("+0;-0") + " st") { Height = 26 };
        ToolTip.SetTip(tune, Loc.T("sampler.tune.tip"));
        tune.Moved += v => Edit(s => s.Transpose = v, "transpose");
        refresh.Add(() => { if (Current() is { } s) tune.Value = s.Transpose; });
        box.Children.Add(Row(Loc.T("sampler.tune"), tune));
        var voices = new ValueBar(1, Sampler.MaxVoices, 16, v => v == 1 ? Loc.T("sampler.mono") : v.ToString()) { Height = 26 };
        ToolTip.SetTip(voices, Loc.T("sampler.voices.tip"));
        voices.Moved += v => Edit(s => s.Voices = v, "voices");
        refresh.Add(() => { if (Current() is { } s) voices.Value = s.Voices; });
        box.Children.Add(Row(Loc.T("sampler.voices"), voices));
        var track = Toggle(Loc.T("sampler.keyTrack"), Loc.T("sampler.keyTrack.tip"), s => s.KeyTrack, (s, on) => s.KeyTrack = on, "keyTrack");
        var legato = Toggle(Loc.T("sampler.legato"), Loc.T("sampler.legato.tip"), s => s.Legato, (s, on) => s.Legato = on, "legato");
        refresh.Add(() => legato.IsEnabled = Current()?.Voices == 1);
        box.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { track, legato } });
        var glide = Dial(Loc.T("sampler.glide"), Loc.T("sampler.glide.tip"), Scale.Power(SamplerSettings.MaxGlideSec, 2), 0, s => s.GlideSec, (s, v) => s.GlideSec = v, Sec, "glide");
        refresh.Add(() => glide.IsEnabled = Current()?.Voices == 1);
        var fine = Dial(Loc.T("sampler.fine"), Loc.T("sampler.fine.tip"), Scale.Linear(-100, 100), 0, s => s.Fine, (s, v) => s.Fine = v, v => $"{v:+0;-0;0} ct", "fine");
        var fade = Dial(Loc.T("sampler.fade"), Loc.T("sampler.fade.tip"), Scale.Linear(0, 1), 0, s => s.LoopFade, (s, v) => s.LoopFade = v, Percent, "loopFade");
        refresh.Add(() => fade.IsEnabled = Current() is { Mode: SamplerSettings.Gate, Loop: SamplerSettings.LoopForward });
        box.Children.Add(Dials(fine, glide, fade));
        return Wrap(box);
    }

    /// <summary>The note the sound is on and how far off, and the button that puts it right.</summary>
    Control PitchRow()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        ToolTip.SetTip(pitchText, Loc.T("sampler.pitch.tip"));
        row.Children.Add(pitchText);
        pitchFix.Content = Loc.T("sampler.pitch.fix");
        ToolTip.SetTip(pitchFix, Loc.T("sampler.pitch.fix.tip"));
        pitchFix.Click += (_, _) =>
        {
            if (heard is { } h) Edit(s => (s.Root, s.Fine) = (h.Key, -Math.Round(h.Cents)), "fineTune");
        };
        Grid.SetColumn(pitchFix, 1);
        row.Children.Add(pitchFix);
        refresh.Add(ShowTuning);
        return row;
    }

    void ShowTuning()
    {
        pitchFix.IsEnabled = false;
        if (measuring)
        {
            pitchText.Text = Loc.T("sampler.pitch.reading");
            pitchText.Foreground = Palette.Faint;
            return;
        }
        if (heard is not { } h || Current() is not { } s)
        {
            pitchText.Text = sampleSec > 0 ? Loc.T("sampler.pitch.none") : "";
            pitchText.Foreground = Palette.Faint;
            return;
        }
        // what a key plays with this root and this fine tuning, against that same key
        bool tuned = Math.Abs((h.Key - s.Root) * 100 + h.Cents + s.Fine) < TunedCents;
        pitchFix.IsEnabled = !tuned;
        pitchText.Text = tuned ? $"{NoteName(h.Key)} · {Loc.T("sampler.pitch.tuned")}" : $"{NoteName(h.Key)} {h.Cents:+0;-0;0} ct";
        pitchText.Foreground = tuned ? Palette.InTune : Palette.Mute;
    }

    /// <summary>What the detector found in the part of the sample in use; null = no clear pitch.</summary>
    public void ShowPitch((int Key, double Cents)? found, bool busy)
    {
        heard = found;
        measuring = busy;
        ShowTuning();
    }

    Control AmpCard()
    {
        var box = Card(Loc.T("sampler.card.amp"));
        var env = new SamplerEnvelopeView(SamplerSettings.MaxEnvSec);
        env.Moved += (a, d, s, r) => Edit(x => (x.Attack, x.Decay, x.Sustain, x.Release) = (a, d, s, r), "ampEnv");
        refresh.Add(() => { if (Current() is { } s) env.Set(s.Attack, s.Decay, s.Sustain, s.Release); });
        box.Children.Add(env);
        var time = Scale.Power(SamplerSettings.MaxEnvSec, 4);
        box.Children.Add(Dials(
            Dial("A", Loc.T("sampler.attack"), time, 0, s => s.Attack, (s, v) => s.Attack = v, Sec, "attack"),
            Dial("D", Loc.T("sampler.decay"), time, 0.1, s => s.Decay, (s, v) => s.Decay = v, Sec, "decay"),
            Dial("S", Loc.T("sampler.sustain"), Scale.Linear(0, 1), 1, s => s.Sustain, (s, v) => s.Sustain = v, Percent, "sustain"),
            Dial("R", Loc.T("sampler.release"), time, 0.15, s => s.Release, (s, v) => s.Release = v, Sec, "release")));
        box.Children.Add(Dials(
            Dial(Loc.T("sampler.gain"), Loc.T("sampler.gain.tip"), Scale.Linear(SamplerSettings.MinGainDb, SamplerSettings.MaxGainDb), 0, s => s.GainDb, (s, v) => s.GainDb = v, v => $"{v:+0.0;-0.0;0.0} dB", "gain", SamplerKnob.Gain),
            Dial(Loc.T("sampler.pan"), Loc.T("sampler.pan.tip"), Scale.Linear(-1, 1), 0, s => s.Pan, (s, v) => s.Pan = Math.Abs(v) < 0.04 ? 0 : v, ValueFormat.Pan, "pan", SamplerKnob.Pan),
            Dial(Loc.T("sampler.velAmp"), Loc.T("sampler.velAmp.tip"), Scale.Linear(0, 1), 0.7, s => s.VelAmp, (s, v) => s.VelAmp = v, Percent, "velAmp")));
        return Wrap(box);
    }

    Control FilterCard()
    {
        var box = Card(Loc.T("sampler.card.filter"));
        var type = new SelectButton { Height = 28, MinHeight = 28 };
        type.SetItems([.. SamplerSettings.Filters.Select(f => Loc.T("sampler.filter." + f))]);
        bool filling = false;
        type.Selected += i =>
        {
            if (!filling && i >= 0) Edit(s => s.Filter = SamplerSettings.Filters[i], "filter");
        };
        refresh.Add(() =>
        {
            filling = true;
            if (Current() is { } s) type.SelectedIndex = Array.IndexOf(SamplerSettings.Filters, s.Filter);
            filling = false;
        });
        box.Children.Add(type);
        var rest = new StackPanel { Spacing = 8 };
        var env = new SamplerEnvelopeView(SamplerSettings.MaxEnvSec) { Height = 60 };
        env.Moved += (a, d, s, r) => Edit(x => (x.FilterAttack, x.FilterDecay, x.FilterSustain, x.FilterRelease) = (a, d, s, r), "filterShape");
        refresh.Add(() => { if (Current() is { } s) env.Set(s.FilterAttack, s.FilterDecay, s.FilterSustain, s.FilterRelease); });
        rest.Children.Add(Dials(
            Dial(Loc.T("sampler.cutoff"), Loc.T("sampler.cutoff.tip"), Scale.Exponential(SamplerSettings.MinCutoff, SamplerSettings.MaxCutoff), 2000, s => s.Cutoff, (s, v) => s.Cutoff = v, HzText, "cutoff", SamplerKnob.Cutoff),
            Dial(Loc.T("sampler.resonance"), Loc.T("sampler.resonance.tip"), Scale.Linear(0, 1), 0, s => s.Resonance, (s, v) => s.Resonance = v, Percent, "resonance", SamplerKnob.Resonance),
            Dial(Loc.T("sampler.drive"), Loc.T("sampler.drive.tip"), Scale.Linear(0, 1), 0, s => s.Drive, (s, v) => s.Drive = v, Percent, "drive", SamplerKnob.Drive)));
        rest.Children.Add(env);
        rest.Children.Add(Dials(
            Dial(Loc.T("sampler.filterEnv"), Loc.T("sampler.filterEnv.tip"), Scale.Linear(-1, 1), 0, s => s.FilterEnv, (s, v) => s.FilterEnv = Math.Abs(v) < 0.02 ? 0 : v, Percent, "filterEnv", SamplerKnob.FilterEnv),
            Dial(Loc.T("sampler.filterKey"), Loc.T("sampler.filterKey.tip"), Scale.Linear(0, 1), 0, s => s.FilterKey, (s, v) => s.FilterKey = v, Percent, "filterKey"),
            Dial(Loc.T("sampler.filterVel"), Loc.T("sampler.filterVel.tip"), Scale.Linear(0, 1), 0, s => s.FilterVel, (s, v) => s.FilterVel = v, Percent, "filterVel")));
        refresh.Add(() =>
        {
            bool on = Current()?.Filter is { } f && f != SamplerSettings.FilterOff;
            rest.IsEnabled = on;
            rest.Opacity = on ? 1 : 0.45;
            env.InvalidateVisual();
        });
        box.Children.Add(rest);
        return Wrap(box);
    }

    Control ModCard()
    {
        var box = Card(Loc.T("sampler.card.mod"));
        box.Children.Add(Seg([("sine", "M0,5 C2.5,-3 5.5,-3 8,5 S13.5,13 16,5", Loc.T("sampler.lfo.sine")), ("triangle", "M0,5 L4,0 L12,10 L16,5", Loc.T("sampler.lfo.triangle")), ("saw", "M0,10 L8,0 L8,10 L16,0 L16,10", Loc.T("sampler.lfo.saw")),
            ("square", "M0,10 L0,0 L8,0 L8,10 L16,10 L16,0", Loc.T("sampler.lfo.square")), ("random", "M0,6 L3,6 L3,1 L6,1 L6,9 L10,9 L10,3 L13,3 L13,7 L16,7", Loc.T("sampler.lfo.random"))],
            s => s.LfoShape, (s, v) => s.LfoShape = v, "lfoShape"));
        var rate = Dial(Loc.T("sampler.lfoRate"), Loc.T("sampler.lfoRate.tip"), Scale.Exponential(SamplerSettings.MinLfoHz, SamplerSettings.MaxLfoHz), 5, s => s.LfoHz, (s, v) => s.LfoHz = v, HzText, "lfoHz");
        refresh.Add(() => rate.IsEnabled = Current()?.LfoBeat == 0);
        var sync = new SelectButton { Height = 26, MinHeight = 26 };
        sync.SetItems([.. SamplerSettings.LfoBeats.Select(BeatText)]);
        ToolTip.SetTip(sync, Loc.T("sampler.lfoSync.tip"));
        bool filling = false;
        sync.Selected += i =>
        {
            if (!filling && i >= 0) Edit(s => s.LfoBeat = SamplerSettings.LfoBeats[i], "lfoBeat");
        };
        refresh.Add(() =>
        {
            filling = true;
            if (Current() is { } s) sync.SelectedIndex = Array.IndexOf(SamplerSettings.LfoBeats, s.LfoBeat);
            filling = false;
        });
        box.Children.Add(Row(Loc.T("sampler.lfoSync"), sync));
        box.Children.Add(Dials(
            rate,
            Dial(Loc.T("sampler.lfoDelay"), Loc.T("sampler.lfoDelay.tip"), Scale.Power(SamplerSettings.MaxLfoDelay, 2), 0, s => s.LfoDelay, (s, v) => s.LfoDelay = v, Sec, "lfoDelay")));
        box.Children.Add(Dials(
            Dial(Loc.T("sampler.lfoPitch"), Loc.T("sampler.lfoPitch.tip"), Scale.Power(SamplerSettings.MaxLfoPitch, 2), 0, s => s.LfoPitch, (s, v) => s.LfoPitch = v, v => $"{v:0.00} st", "lfoPitch", SamplerKnob.LfoPitch),
            Dial(Loc.T("sampler.lfoFilter"), Loc.T("sampler.lfoFilter.tip"), Scale.Linear(0, SamplerSettings.MaxLfoFilter), 0, s => s.LfoFilter, (s, v) => s.LfoFilter = v, v => $"{v:0.0} oct", "lfoFilter", SamplerKnob.LfoFilter),
            Dial(Loc.T("sampler.lfoAmp"), Loc.T("sampler.lfoAmp.tip"), Scale.Linear(0, 1), 0, s => s.LfoAmp, (s, v) => s.LfoAmp = v, Percent, "lfoAmp", SamplerKnob.LfoAmp)));
        box.Children.Add(new TextBlock { Text = Loc.T("sampler.pitchEnv"), Classes = { "label" }, Margin = new Thickness(0, 2, 0, 0) });
        box.Children.Add(Dials(
            Dial(Loc.T("sampler.pitchAmount"), Loc.T("sampler.pitchAmount.tip"), Scale.Linear(-SamplerSettings.MaxPitchEnv, SamplerSettings.MaxPitchEnv), 0, s => s.PitchEnv, (s, v) => s.PitchEnv = Math.Round(v), v => $"{v:+0;-0;0} st", "pitchEnv"),
            Dial(Loc.T("sampler.pitchDecay"), Loc.T("sampler.pitchDecay.tip"), Scale.Power(SamplerSettings.MaxPitchDecay, 3), 0.05, s => s.PitchDecay, (s, v) => s.PitchDecay = v, Sec, "pitchDecay")));
        return Wrap(box);
    }

    // --- the parts --------------------------------------------------------------------------------

    /// <summary>How a knob's travel maps to its value: times and frequencies need room at the bottom end.</summary>
    readonly record struct Scale(Func<double, double> ToValue, Func<double, double> ToKnob)
    {
        public static Scale Linear(double min, double max) => new(x => min + x * (max - min), v => (v - min) / (max - min));
        public static Scale Power(double max, double power) => new(x => Math.Pow(x, power) * max, v => Math.Pow(v / max, 1 / power));
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
    Control Dial(string label, string tip, Scale scale, double reset, Func<SamplerSettings, double> get, Action<SamplerSettings, double> set, Func<double, string> text, string key, SamplerKnob? lane = null)
    {
        var value = new TextBlock { Classes = { "dim", "mono" }, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
        var knob = new Knob(KnobSize, label) { Min = 0, Max = 1, ResetTo = Math.Clamp(scale.ToKnob(reset), 0, 1) };
        knob.Changed += x => Edit(s => set(s, scale.ToValue(x)), key);
        refresh.Add(() =>
        {
            if (Current() is not { } s) return;
            knob.Value = Math.Clamp(scale.ToKnob(get(s)), 0, 1);
            value.Text = text(get(s));
        });
        var cell = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock { Text = label, FontSize = 10.5, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent };
        ToolTip.SetTip(name, tip);
        cell.Children.Add(name);
        knob.HorizontalAlignment = HorizontalAlignment.Center;
        cell.Children.Add(knob);
        cell.Children.Add(value);
        if (lane is { } moved)
            lanes.Add(() => Automate(cell, moved, k =>
            {
                knob.Value = k;
                value.Text = text(SamplerKnobs.Value(moved, k));
            }));
        return cell;
    }

    ToggleButton Toggle(string text, string tip, Func<SamplerSettings, bool> get, Action<SamplerSettings, bool> set, string key)
    {
        var b = new ToggleButton { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Content = text, Focusable = false };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            bool on = b.IsChecked == true;
            Edit(s => set(s, on), key);
        };
        refresh.Add(() => { if (Current() is { } s) b.IsChecked = get(s); });
        return b;
    }

    Control Seg((string Value, string Text, string? Tip)[] options, Func<SamplerSettings, string> get, Action<SamplerSettings, string> set, string key)
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var (value, text, tip) in options)
        {
            object content = text.StartsWith('M') && text.Contains(',') ? WavePath(text) : text;
            var b = new ToggleButton { Classes = { "seg" }, Content = content, Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Focusable = false };
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Edit(s => set(s, value), key);
            refresh.Add(() => { if (Current() is { } s) b.IsChecked = get(s) == value; });
            row.Children.Add(b);
        }
        return new Border { Classes = { "seg" }, Child = row, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>The shape of an LFO, as a drawing: no font has these.</summary>
    static Avalonia.Controls.Shapes.Path WavePath(string data) => new()
    {
        Data = Geometry.Parse(data), Stroke = Palette.Dim, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        Width = 16, Height = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    static StackPanel Labeled(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children =
        {
            new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    static string NoteName(int pitch) => NoteNames.Of(pitch);
    static string Percent(double v) => $"{v * 100:0}%";
    static string Sec(double v) => v < 1 ? $"{v * 1000:0} ms" : $"{v:0.00} s";
    static string HzText(double v) => v >= 1000 ? $"{v / 1000:0.0} kHz" : v >= 10 ? $"{v:0} Hz" : $"{v:0.00} Hz";

    /// <summary>A knob's value as its lane reads it, the way the knob shows it.</summary>
    public static string KnobText(SamplerKnob knob, double v) => knob switch
    {
        SamplerKnob.Cutoff => HzText(v),
        SamplerKnob.Gain => $"{v:+0.0;-0.0;0.0} dB",
        SamplerKnob.Pan => ValueFormat.Pan(v),
        SamplerKnob.LfoPitch => $"{v:0.00} st",
        SamplerKnob.LfoFilter => $"{v:0.0} oct",
        _ => Percent(v),
    };

    static string BeatText(double beats) => beats switch
    {
        0 => Loc.T("sampler.lfoSync.free"),
        8 => "2/1",
        4 => "1/1",
        2 => "1/2",
        1 => "1/4",
        0.5 => "1/8",
        _ => "1/16",
    };

    // --- what the app shows here ------------------------------------------------------------------

    /// <summary>Whose it is: the title and the track's color.</summary>
    public void ShowTrack(string name, string? color)
    {
        title.Text = Title = $"{Loc.T("sampler.name")} · {name}";
        dot.Fill = color != null ? Palette.Brush(color) : Palette.Faint;
    }

    /// <summary>The sample on screen; null peaks = none. <paramref name="state"/> says why, when something is wrong or on its way.</summary>
    public void ShowSample(string? name, byte[]? peaks, float[]? left, float[]? right, bool reverse, double seconds, int sampleRate, int channels, string? state)
    {
        sampleSec = seconds;
        (soundLeft, soundRight, soundReversed) = (left, right, reverse);
        sampleName.Text = name ?? Loc.T("sampler.none");
        sampleInfo.Text = state ?? (peaks == null ? "" : $"{seconds.ToString("0.00", CultureInfo.CurrentCulture)} s · {sampleRate / 1000.0:0.#} kHz · {(channels > 1 ? Loc.T("sampler.stereo") : Loc.T("sampler.monoFile"))}");
        wave.SetSample(peaks, left, right, reverse);
        Refresh();
    }

    /// <summary>A flag on the nearest crossing, if the magnet is on and there is one near and it fits between the flags.</summary>
    double ZeroPoint(SamplerWaveView.Marker m, double f, bool free)
    {
        if (free || !zeroSnap || soundLeft is not { Length: > 0 } l || soundRight is not { } r) return f;
        int frame = SamplerSound.ZeroCrossing(l, r, soundReversed, (int)Math.Round(f * l.Length));
        if (frame < 0) return f;
        double snapped = frame / (double)l.Length;
        return wave.Limit(m, snapped) == snapped ? snapped : f;
    }

    /// <summary>Every control shows the settings as they are now. Setting a value here never counts as an edit.</summary>
    public void Refresh()
    {
        foreach (var r in refresh) r();
    }
}
