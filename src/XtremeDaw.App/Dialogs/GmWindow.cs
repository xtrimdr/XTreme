using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The General MIDI instrument's window (asked by the user, #200: it had no interface), in the place the sampler's has:
/// one per track, floating over the app. The 16 families and the drum kits down the left, the programs of the one being
/// browsed as tiles on the right (a click sets it on the track), and a keyboard to try it. Browsing a family doesn't
/// change the track; only picking a tile does, through <see cref="Pick"/>, so <see cref="MainWindow"/> keeps the undo.
/// </summary>
public sealed class GmWindow : Window
{
    /// <summary>The list of families, wide enough for "Efectos de sintetizador".</summary>
    const double FamilyWidth = 220;
    /// <summary>The family index of the drum kits in <see cref="families"/>.</summary>
    const int DrumsFamily = 16;

    readonly GmSynth synth;
    readonly SamplerKeysView keys = new();
    readonly TextBlock title = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Ellipse dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock nowName = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock nowInfo = new() { Classes = { "dim", "mono" }, FontSize = 11 };
    readonly TextBlock familyTitle = new() { Classes = { "label" } };
    readonly ToggleButton[] families = new ToggleButton[DrumsFamily + 1];
    readonly UniformGrid tiles = new() { Columns = 2 };
    /// <summary>Each tone control showing what the track has now.</summary>
    readonly List<Action<GmSettings>> refresh = [];
    int browsing = -1;
    /// <summary>The program the tiles were built for, so a knob's drag doesn't rebuild them.</summary>
    (int Program, bool Drums)? shown;
    const double KnobSize = 44;

    /// <summary>What the track plays now; null once it no longer has the GM instrument.</summary>
    public Func<GmSettings?> Current { get; init; } = null!;
    /// <summary>Sets this program (or kit, with drums) on the track.</summary>
    public Action<int, bool> Pick { get; init; } = null!;
    /// <summary>One edit of the tone, undone with the ones of the same key that follow it.</summary>
    public Action<Action<GmSettings>, string> Edit { get; init; } = null!;
    /// <summary>A key pressed in this window goes to the app's shortcuts.</summary>
    public Action<KeyEventArgs> Shortcut { get; init; } = null!;
    /// <summary>The window took or left the front: the keyboards play this track while it is there.</summary>
    public Action<bool> Front { get; init; } = null!;

    /// <summary>Read only: everything shows, nothing changes; the keyboard still plays.</summary>
    public bool IsEnabledForEdits { get; set; } = true;

    public GmWindow(GmSynth synth)
    {
        this.synth = synth;
        Width = 860;
        Height = 760;
        MinWidth = 820;
        MinHeight = 640;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.App;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 36;
        Content = Build();
        AddHandler(KeyDownEvent, (_, e) => Shortcut(e), RoutingStrategies.Tunnel);
        Activated += (_, _) => Front(true);
        Deactivated += (_, _) => Front(false);
        Opened += (_, _) => Refresh();
    }

    Control Build()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("36,Auto,*,Auto,Auto") };
        var header = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        header.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 150, 0), IsHitTestVisible = false, Children = { dot, title } };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        root.Children.Add(header);

        var now = new StackPanel { Spacing = 2, Margin = new Thickness(14, 12, 14, 10), Children = { nowName, nowInfo } };
        Grid.SetRow(now, 1);
        root.Children.Add(now);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions($"{FamilyWidth},*"), ColumnSpacing = 8, Margin = new Thickness(10, 0, 10, 10) };
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 6, 0) };
        for (int f = 0; f <= DrumsFamily; f++)
        {
            int family = f;
            if (f == DrumsFamily) list.Children.Add(new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(4, 4) });
            var b = new ToggleButton
            {
                Classes = { "chip" }, Height = 26, MinHeight = 26, Padding = new Thickness(10, 0), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = f == DrumsFamily ? Loc.T("gm.drums") : GmNames.Family(f), Focusable = false,
            };
            b.Click += (_, _) => Browse(family);
            families[f] = b;
            list.Children.Add(b);
        }
        var listCard = new Border { Classes = { "section" }, Padding = new Thickness(6), Child = new ScrollViewer { Content = list } };
        body.Children.Add(listCard);

        var right = new DockPanel();
        familyTitle.Margin = new Thickness(2, 0, 0, 8);
        DockPanel.SetDock(familyTitle, Dock.Top);
        right.Children.Add(familyTitle);
        right.Children.Add(new ScrollViewer { Content = tiles });
        var rightCard = new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = right };
        Grid.SetColumn(rightCard, 1);
        body.Children.Add(rightCard);
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var tone = Tone();
        Grid.SetRow(tone, 3);
        root.Children.Add(tone);

        keys.Pressed += (p, v) => synth.Press(p, v);
        keys.Lifted += p => synth.Lift(p);
        var bottom = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6), Child = keys };
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);
        return root;
    }

    // --- the tone (asked by the user, #200: "no tengo control de casi nada") --------------------------

    /// <summary>The strip of knobs over the keyboard: envelope, filter and how it plays, each over what the bank does.</summary>
    Control Tone()
    {
        string Time(double v) => v == 0 ? Loc.T("gm.bank") : v < 0 ? $"{v * 100:0}%" : "+" + Sec(GmTone.MaxAddSec * v * v);
        string Signed(double v) => v == 0 ? Loc.T("gm.bank") : $"{v * 100:+0;-0}%";

        var env = Card(Loc.T("gm.card.env"), null,
            Dial(Loc.T("gm.attack"), Loc.T("gm.attack.tip"), -1, 1, true, g => g.Attack, (g, v) => g.Attack = v, Time, "attack"),
            Dial(Loc.T("gm.decay"), Loc.T("gm.decay.tip"), -1, 1, true, g => g.Decay, (g, v) => g.Decay = v, Time, "decay"),
            Dial(Loc.T("gm.sustain"), Loc.T("gm.sustain.tip"), -1, 1, true, g => g.Sustain, (g, v) => g.Sustain = v, Signed, "sustain"),
            Dial(Loc.T("gm.release"), Loc.T("gm.release.tip"), -1, 1, true, g => g.Release, (g, v) => g.Release = v, Time, "release"));
        var filter = Card(Loc.T("gm.card.filter"), null,
            Dial(Loc.T("gm.bright"), Loc.T("gm.bright.tip"), -1, 1, true, g => g.Bright, (g, v) => g.Bright = v,
                v => v == 0 ? Loc.T("gm.bank") : $"{v * GmTone.CutoffOctaves:+0.0;-0.0} oct", "bright"),
            Dial(Loc.T("gm.resonance"), Loc.T("gm.resonance.tip"), 0, 1, false, g => g.Resonance, (g, v) => g.Resonance = v,
                v => $"+{v * GmTone.MaxResonanceDb:0.0} dB", "resonance"),
            Dial(Loc.T("gm.velBright"), Loc.T("gm.velBright.tip"), 0, 1, false, g => g.VelBright, (g, v) => g.VelBright = v, Percent, "velBright"));

        var mono = new ToggleButton { Classes = { "chip" }, Height = 26, MinHeight = 26, Padding = new Thickness(10, 0), FontSize = 12, Content = Loc.T("gm.mono"), Focusable = false };
        ToolTip.SetTip(mono, Loc.T("gm.mono.tip"));
        mono.Click += (_, _) =>
        {
            bool on = mono.IsChecked == true;
            Edit(g => g.Mono = on, "mono");
        };
        refresh.Add(g =>
        {
            mono.IsChecked = g.Mono;
            mono.IsEnabled = IsEnabledForEdits;
        });
        var transpose = Dial(Loc.T("gm.transpose"), Loc.T("gm.transpose.tip"), -GmTone.MaxTranspose, GmTone.MaxTranspose, true, g => g.Transpose,
            (g, v) => g.Transpose = (int)Math.Round(v), v => v == 0 ? "0 st" : $"{v:+0;-0} st", "transpose");
        refresh.Add(g => transpose.IsEnabled = !g.Drums && IsEnabledForEdits);
        var glide = Dial(Loc.T("gm.glide"), Loc.T("gm.glide.tip"), 0, GmTone.MaxGlideSec, false, g => g.GlideSec, (g, v) => g.GlideSec = v, Sec, "glide", power: 2);
        refresh.Add(g => glide.IsEnabled = g.Mono && IsEnabledForEdits);
        var play = Card(Loc.T("gm.card.play"), mono,
            Dial(Loc.T("gm.velocity"), Loc.T("gm.velocity.tip"), 0, 1, false, g => g.Velocity, (g, v) => g.Velocity = v, Percent, "velocity", reset: 1),
            transpose,
            Dial(Loc.T("gm.fine"), Loc.T("gm.fine.tip"), -100, 100, true, g => g.Fine, (g, v) => g.Fine = Math.Round(v), v => $"{v:+0;-0;0} ct", "fine"),
            glide);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("4*,3*,4*"), ColumnSpacing = 8 };
        row.Children.Add(env);
        Grid.SetColumn(filter, 1);
        row.Children.Add(filter);
        Grid.SetColumn(play, 2);
        row.Children.Add(play);

        var reset = new Button { Classes = { "link" }, Content = Loc.T("gm.reset"), FontSize = 11, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, Focusable = false };
        ToolTip.SetTip(reset, Loc.T("gm.reset.tip"));
        reset.Click += (_, _) => Edit(g => g.ResetTone(), "reset");
        refresh.Add(_ => reset.IsEnabled = IsEnabledForEdits);
        var strip = new StackPanel { Spacing = 4, Margin = new Thickness(10, 0, 10, 10), Children = { reset, row } };
        return strip;
    }

    static Border Card(string name, Control? extra, params Control[] dials)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6), MinHeight = 26 };
        head.Children.Add(new TextBlock { Text = name, Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        if (extra != null)
        {
            Grid.SetColumn(extra, 1);
            head.Children.Add(extra);
        }
        var cells = new UniformGrid { Rows = 1 };
        foreach (var d in dials) cells.Children.Add(d);
        return new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = new StackPanel { Children = { head, cells } } };
    }

    /// <summary>
    /// A knob over a value from <paramref name="min"/> to <paramref name="max"/> (with <paramref name="power"/>, more
    /// room at the bottom), its name and its value; a double click gives back <paramref name="reset"/>, the bank's.
    /// </summary>
    Control Dial(string label, string tip, double min, double max, bool centered, Func<GmSettings, double> get, Action<GmSettings, double> set,
        Func<double, string> text, string key, double reset = 0, double power = 1)
    {
        double ToKnob(double v) => Math.Clamp(power == 1 ? (v - min) / (max - min) : Math.Pow(Math.Max(0, v / max), 1 / power), 0, 1);
        double ToValue(double x) => power == 1 ? min + x * (max - min) : Math.Pow(x, power) * max;
        var value = new TextBlock { Classes = { "dim", "mono" }, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
        var knob = new Knob(KnobSize, label) { Min = 0, Max = 1, ResetTo = ToKnob(reset), ArcFrom = centered ? 0.5 : null, HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += x =>
        {
            double v = ToValue(x);
            // the centre catches, so the bank's own sound is easy to find again
            if (centered && Math.Abs(x - 0.5) < 0.02) v = 0;
            Edit(g => set(g, v), key);
        };
        refresh.Add(g =>
        {
            knob.Value = ToKnob(get(g));
            value.Text = text(get(g));
        });
        var name = new TextBlock { Text = label, FontSize = 10.5, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent };
        ToolTip.SetTip(name, tip);
        return new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, Children = { name, knob, value } };
    }

    static string Percent(double v) => $"{v * 100:0}%";
    static string Sec(double v) => v < 1 ? $"{v * 1000:0} ms" : $"{v:0.00} s";

    /// <summary>Shows a family's programs (or the kits) without changing what the track plays.</summary>
    void Browse(int family)
    {
        browsing = family;
        for (int f = 0; f <= DrumsFamily; f++) families[f].IsChecked = f == family;
        familyTitle.Text = (family == DrumsFamily ? Loc.T("gm.drums") : GmNames.Family(family)).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        tiles.Children.Clear();
        var cur = Current();
        if (family == DrumsFamily)
            foreach (var (program, _) in GmNames.Kits)
                tiles.Children.Add(Tile(program, true, GmNames.KitOnly(program), cur is { Drums: true } g && g.Program == program));
        else
            for (int n = family * 8; n < family * 8 + 8; n++)
                tiles.Children.Add(Tile(n, false, GmNames.Program(n), cur is { Drums: false } g && g.Program == n));
    }

    Control Tile(int program, bool drums, string name, bool on)
    {
        var number = new TextBlock { Classes = { "mono" }, FontSize = 11, Width = 30, VerticalAlignment = VerticalAlignment.Center, Text = (program + 1).ToString("000"), Opacity = 0.7 };
        var text = new TextBlock { Text = name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var b = new ToggleButton
        {
            Classes = { "chip" }, Height = 40, MinHeight = 40, Margin = new Thickness(3), Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            IsChecked = on, IsEnabled = IsEnabledForEdits, Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { number, text } },
        };
        b.Click += (_, _) =>
        {
            b.IsChecked = true;
            Pick(program, drums);
        };
        return b;
    }

    /// <summary>Shows the track's name and color.</summary>
    public void ShowTrack(string name, string? color)
    {
        title.Text = Title = $"{Loc.T("gm.name")} · {name}";
        dot.Fill = color != null ? Palette.Brush(color) : Palette.Faint;
    }

    /// <summary>Shows what the track plays now and the bank's state (null when it is ready); keeps the family being browsed.</summary>
    public void Refresh(string? state = null)
    {
        if (Current() is not { } g) return;
        nowName.Text = g.Name;
        string where = g.Drums ? Loc.T("gm.drums") : $"{g.Program + 1:000} · {GmNames.Family(g.Program / 8)}";
        nowInfo.Text = state ?? where;
        foreach (var r in refresh) r(g);
        if (shown == (g.Program, g.Drums)) return;
        shown = (g.Program, g.Drums);
        Browse(browsing < 0 ? (g.Drums ? DrumsFamily : g.Program / 8) : browsing);
    }
}
