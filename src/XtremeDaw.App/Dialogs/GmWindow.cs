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
/// one per track, floating over the app. On top, what the track plays with its family's icon, the previous and next
/// program, the bank and the instrument's volume (#212). Below, the browser: the 16 families and the drum kits down the
/// left, the programs of the one being browsed (or what the search finds) on the right; it folds away (#212: "compactable"),
/// and then one menu by family picks the program. Then the sound in three panels, the envelope and the filter drawn as
/// they sound, and a keyboard to try it. Browsing doesn't change the track; only picking does, through <see cref="Pick"/>,
/// so <see cref="MainWindow"/> keeps the undo.
/// </summary>
public sealed class GmWindow : Window
{
    /// <summary>The list of families, wide enough for "Efectos de sintetizador" next to its icon.</summary>
    const double FamilyWidth = 214;
    /// <summary>The family index of the drum kits in <see cref="families"/>.</summary>
    const int DrumsFamily = 16;
    const double KnobSize = 44, VolumeKnobSize = 52;
    /// <summary>The window's height with the browser open, and its least.</summary>
    const double OpenHeight = 800, OpenMinHeight = 680;
    /// <summary>Where the envelope's graph ends: the longest time a knob gives, plus room.</summary>
    const double EnvelopeSec = GmTone.MaxAddSec + 10;
    /// <summary>The SF2 envelope's floor in centibels, as the synth reads it.</summary>
    const double SilentCb = 960;
    /// <summary>The key and velocity the panels show the preset at.</summary>
    const int ShownKey = 60, ShownVelocity = 100;
    /// <summary>What the browser's choice of open or folded is kept as in config.json.</summary>
    const string CompactKey = "gmCompact";
    /// <summary>The option of <see cref="bank"/> that asks for a .sf2 file.</summary>
    const string FileOption = "\0file";

    /// <summary>The icon of each family (and the kits), from the instrument icons of the tracks.</summary>
    static readonly string[] FamilyIcons =
    [
        "grand-piano", "xylophone", "pipe-organ", "electric-guitar", "bass-guitar", "violin", "sheet-music", "trumpet",
        "saxophone", "flute", "synthesizer", "sparkles", "star", "banjo", "drum", "speaker", "drum-kit",
    ];

    readonly GmSynth synth;
    readonly SamplerKeysView keys = new();
    readonly TextBlock title = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Ellipse dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock nowName = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock nowInfo = new() { Classes = { "dim", "mono" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Border nowIcon = new() { Width = 48, Height = 48, CornerRadius = new CornerRadius(10), Background = Palette.AccentSoft, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock familyTitle = new() { Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };
    readonly ToggleButton[] families = new ToggleButton[DrumsFamily + 1];
    readonly UniformGrid tiles = new() { Columns = 2 };
    readonly TextBox search = new() { Classes = { "field" }, Width = 220, Height = 28, MinHeight = 28, FontSize = 12 };
    readonly SelectButton bank = new() { Width = 190, Height = 28, MinHeight = 28 };
    /// <summary>The program menu by family, in place of the browser while it is folded.</summary>
    readonly SelectButton programs = new() { Width = 220, Height = 28, MinHeight = 28 };
    readonly ToggleButton fold = new() { Classes = { "chip" }, Width = 30, Height = 28, MinHeight = 28, Padding = new Thickness(0), Focusable = false };
    readonly Border browser = new();
    readonly SamplerEnvelopeView envelope = new(EnvelopeSec) { IsHitTestVisible = false };
    readonly GmFilterView filter = new();
    /// <summary>What each option of <see cref="bank"/> is: a bank (null = the shipped one), or <see cref="FileOption"/>.</summary>
    readonly List<string?> bankIds = [];
    /// <summary>What each option of <see cref="programs"/> picks.</summary>
    readonly List<(int Program, bool Drums)> programIds = [];
    /// <summary>Each control showing what the track has now.</summary>
    readonly List<Action<GmSettings>> refresh = [];
    int browsing = -1;
    /// <summary>The program the tiles were built for, so a knob's drag doesn't rebuild them.</summary>
    (int Program, bool Drums)? shown;
    string? shownIcon;
    double openHeight = OpenHeight;

    /// <summary>What the track plays now; null once it no longer has the GM instrument.</summary>
    public Func<GmSettings?> Current { get; init; } = null!;
    /// <summary>Sets this program (or kit, with drums) on the track.</summary>
    public Action<int, bool> Pick { get; init; } = null!;
    /// <summary>One edit of the tone, undone with the ones of the same key that follow it.</summary>
    public Action<Action<GmSettings>, string> Edit { get; init; } = null!;
    /// <summary>Sets the bank the track plays (null = the shipped one).</summary>
    public Action<string?> PickBank { get; init; } = null!;
    /// <summary>Downloads a bank of <see cref="GmBank.Downloads"/> and sets it once it is here.</summary>
    public Action<string> DownloadBank { get; init; } = null!;
    /// <summary>Asks for a .sf2 of the user and sets it.</summary>
    public Action LoadBankFile { get; init; } = null!;
    /// <summary>A key pressed in this window goes to the app's shortcuts.</summary>
    public Action<KeyEventArgs> Shortcut { get; init; } = null!;
    /// <summary>The window took or left the front: the keyboards play this track while it is there.</summary>
    public Action<bool> Front { get; init; } = null!;

    /// <summary>Read only: everything shows, nothing changes; the keyboard still plays.</summary>
    public bool IsEnabledForEdits { get; set; } = true;

    public GmWindow(GmSynth synth)
    {
        this.synth = synth;
        Width = 900;
        Height = OpenHeight;
        MinWidth = 860;
        MinHeight = OpenMinHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.App;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 36;
        Content = Build();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            // typing in the search is text, not shortcuts
            if (e.Source is TextBox) return;
            Shortcut(e);
        }, RoutingStrategies.Tunnel);
        Activated += (_, _) => Front(true);
        Deactivated += (_, _) => Front(false);
        Opened += (_, _) =>
        {
            Fold(AppConfig.Get(CompactKey) == "1");
            Refresh();
        };
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

        var hero = Hero();
        Grid.SetRow(hero, 1);
        root.Children.Add(hero);

        browser.Child = Browser();
        Grid.SetRow(browser, 2);
        root.Children.Add(browser);

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

    /// <summary>What the track plays, its neighbours, the bank and the volume.</summary>
    Control Hero()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 14 };
        grid.Children.Add(nowIcon);

        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { nowName, nowInfo } };
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        var prev = Step("", Loc.T("gm.prev.tip"), -1);
        var next = Step("", Loc.T("gm.next.tip"), 1);
        fold.Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13 };
        ToolTip.SetTip(fold, Loc.T("gm.fold.tip"));
        fold.Click += (_, _) => Fold(fold.IsChecked != true);
        ToolTip.SetTip(programs, Loc.T("gm.programs.tip"));
        programs.Selected += i =>
        {
            if (i >= 0 && i < programIds.Count) Pick(programIds[i].Program, programIds[i].Drums);
        };
        var browse = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { programs, prev, next, fold },
        };
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);

        var bankLabel = new TextBlock { Text = Loc.T("gm.bank.label"), Classes = { "label" } };
        ToolTip.SetTip(bankLabel, Loc.T("gm.bank.tip"));
        bank.Selected += PickBankAt;
        var bankBox = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { bankLabel, bank } };
        Grid.SetColumn(bankBox, 3);
        grid.Children.Add(bankBox);

        var volume = Dial(Loc.T("gm.volume"), Loc.T("gm.volume.tip"), GmTone.MinVolumeDb, GmTone.MaxVolumeDb, false, g => g.VolumeDb,
            (g, v) => g.VolumeDb = v, v => v <= GmTone.MinVolumeDb ? ValueFormat.Silence : ValueFormat.Gain(v), "volume", size: VolumeKnobSize);
        Grid.SetColumn(volume, 4);
        grid.Children.Add(volume);

        return new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 8), Margin = new Thickness(0, 0, 0, 10), Child = grid,
        };
    }

    Button Step(string glyph, string tip, int by)
    {
        var b = new Button
        {
            Classes = { "chip" }, Width = 30, Height = 28, MinHeight = 28, Padding = new Thickness(0), Focusable = false,
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 11 },
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => StepBy(by);
        refresh.Add(_ => b.IsEnabled = IsEnabledForEdits);
        return b;
    }

    /// <summary>The program next to the track's (a kit next to its kit), round the ends.</summary>
    void StepBy(int by)
    {
        if (Current() is not { } g) return;
        // the browser follows what plays
        browsing = -1;
        if (g.Drums)
        {
            var kits = GmNames.Kits;
            int at = Array.FindIndex(kits, k => k.Program == g.Program);
            Pick(kits[(Math.Max(0, at) + by + kits.Length) % kits.Length].Program, true);
        }
        else Pick((g.Program + by + 128) % 128, false);
    }

    /// <summary>Folds the browser away (the window shrinks to what's left) or opens it again; kept for the next window.</summary>
    void Fold(bool on)
    {
        fold.IsChecked = !on;
        bool was = !browser.IsVisible;
        browser.IsVisible = !on;
        programs.IsVisible = on;
        AppConfig.Set(CompactKey, on ? "1" : null);
        if (on == was) return;
        if (on)
        {
            openHeight = Height;
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            MinHeight = OpenMinHeight;
            Height = Math.Max(openHeight, OpenMinHeight);
        }
    }

    Control Browser()
    {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions($"{FamilyWidth},*"), ColumnSpacing = 8, Margin = new Thickness(10, 0, 10, 10) };
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 6, 0) };
        for (int f = 0; f <= DrumsFamily; f++)
        {
            int family = f;
            if (f == DrumsFamily) list.Children.Add(new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(4, 4) });
            var icon = new TrackIconView { Icon = FamilyIcons[f], Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            var b = new ToggleButton
            {
                Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(8, 0), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { icon, new TextBlock { Text = FamilyName(f), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } },
                },
            };
            // the icon lights with the button: accent while it is the family shown
            b.IsCheckedChanged += (_, _) => icon.Foreground = b.IsChecked == true ? Palette.Accent : Palette.Dim;
            b.Click += (_, _) =>
            {
                search.Text = "";
                Browse(family);
            };
            families[f] = b;
            list.Children.Add(b);
        }
        body.Children.Add(new Border { Classes = { "section" }, Padding = new Thickness(6), Child = new ScrollViewer { Content = list } });

        search.Watermark = Loc.T("gm.search");
        search.TextChanged += (_, _) => Search();
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 0, 0, 8) };
        top.Children.Add(familyTitle);
        Grid.SetColumn(search, 1);
        top.Children.Add(search);
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        right.Children.Add(new ScrollViewer { Content = tiles });
        var rightCard = new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = right };
        Grid.SetColumn(rightCard, 1);
        body.Children.Add(rightCard);
        return body;
    }

    static string FamilyName(int f) => f == DrumsFamily ? Loc.T("gm.drums") : GmNames.Family(f);

    // --- the sound (asked by the user, #200: "no tengo control de casi nada"; #212: better panels) --------------------

    /// <summary>The three panels over the keyboard: envelope and filter with their graphs, and how it plays.</summary>
    Control Tone()
    {
        string Time(double v) => v == 0 ? Loc.T("gm.bank") : v < 0 ? $"{v * 100:0}%" : "+" + Sec(GmTone.MaxAddSec * v * v);
        string Signed(double v) => v == 0 ? Loc.T("gm.bank") : $"{v * 100:+0;-0}%";

        var env = Card(Loc.T("gm.card.env"), null, envelope, 4,
            Dial(Loc.T("gm.attack"), Loc.T("gm.attack.tip"), -1, 1, true, g => g.Attack, (g, v) => g.Attack = v, Time, "attack"),
            Dial(Loc.T("gm.decay"), Loc.T("gm.decay.tip"), -1, 1, true, g => g.Decay, (g, v) => g.Decay = v, Time, "decay"),
            Dial(Loc.T("gm.sustain"), Loc.T("gm.sustain.tip"), -1, 1, true, g => g.Sustain, (g, v) => g.Sustain = v, Signed, "sustain"),
            Dial(Loc.T("gm.release"), Loc.T("gm.release.tip"), -1, 1, true, g => g.Release, (g, v) => g.Release = v, Time, "release"));
        var filt = Card(Loc.T("gm.card.filter"), null, filter, 3,
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
        // no graph here: its four knobs go in two rows, about as tall as a graph and a row
        var play = Card(Loc.T("gm.card.play"), mono, null, 2,
            Dial(Loc.T("gm.velocity"), Loc.T("gm.velocity.tip"), 0, 1, false, g => g.Velocity, (g, v) => g.Velocity = v, Percent, "velocity", reset: 1),
            transpose,
            Dial(Loc.T("gm.fine"), Loc.T("gm.fine.tip"), -100, 100, true, g => g.Fine, (g, v) => g.Fine = Math.Round(v), v => $"{v:+0;-0;0} ct", "fine"),
            glide);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("4*,3*,3*"), ColumnSpacing = 8 };
        row.Children.Add(env);
        Grid.SetColumn(filt, 1);
        row.Children.Add(filt);
        Grid.SetColumn(play, 2);
        row.Children.Add(play);

        var reset = new Button { Classes = { "link" }, Content = Loc.T("gm.reset"), FontSize = 11, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        ToolTip.SetTip(reset, Loc.T("gm.reset.tip"));
        reset.Click += (_, _) => Edit(g => g.ResetTone(), "reset");
        refresh.Add(_ => reset.IsEnabled = IsEnabledForEdits);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 0) };
        head.Children.Add(new TextBlock { Text = Loc.T("gm.card.sound"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(reset, 1);
        head.Children.Add(reset);
        return new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 10, 10), Children = { head, row } };
    }

    /// <summary>A panel of the sound: its name (and a toggle) on top, its graph, and its knobs in <paramref name="columns"/>.</summary>
    static Border Card(string name, Control? extra, Control? graph, int columns, params Control[] dials)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 2), MinHeight = 26 };
        head.Children.Add(new TextBlock { Text = name, Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        if (extra != null)
        {
            Grid.SetColumn(extra, 1);
            head.Children.Add(extra);
        }
        var stack = new StackPanel { Spacing = 8, Children = { head } };
        if (graph != null) stack.Children.Add(graph);
        var cells = new UniformGrid { Columns = columns };
        foreach (var d in dials) cells.Children.Add(d);
        stack.Children.Add(cells);
        return new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = stack };
    }

    /// <summary>
    /// A knob over a value from <paramref name="min"/> to <paramref name="max"/> (with <paramref name="power"/>, more
    /// room at the bottom), its name and its value; a double click gives back <paramref name="reset"/>, the bank's.
    /// </summary>
    Control Dial(string label, string tip, double min, double max, bool centered, Func<GmSettings, double> get, Action<GmSettings, double> set,
        Func<double, string> text, string key, double reset = 0, double power = 1, double size = KnobSize)
    {
        double ToKnob(double v) => Math.Clamp(power == 1 ? (v - min) / (max - min) : Math.Pow(Math.Max(0, v / max), 1 / power), 0, 1);
        double ToValue(double x) => power == 1 ? min + x * (max - min) : Math.Pow(x, power) * max;
        var value = new TextBlock { Classes = { "dim", "mono" }, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
        var knob = new Knob(size, label) { Min = 0, Max = 1, ResetTo = ToKnob(reset), ArcFrom = centered ? 0.5 : null, HorizontalAlignment = HorizontalAlignment.Center };
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
        return new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { name, knob, value } };
    }

    static string Percent(double v) => $"{v * 100:0}%";
    static string Sec(double v) => v < 1 ? $"{v * 1000:0} ms" : $"{v:0.00} s";

    /// <summary>The graphs: the preset's envelope and filter (at middle C, a firm note) with the track's tone on them.</summary>
    void ShowGraphs(GmSettings g)
    {
        SfRegion? r = null;
        if (synth.Preset is { } p)
        {
            foreach (var x in p.Regions)
                if (x.Covers(ShownKey, ShownVelocity))
                {
                    r = x;
                    break;
                }
            r ??= p.Regions.Length > 0 ? p.Regions[0] : null;
        }
        r ??= new SfRegion();
        double a = GmTone.Time(r.Attack, g.Attack), d = GmTone.Time(r.Decay, g.Decay), rel = GmTone.Time(r.Release, g.Release);
        double cb = GmTone.SustainCb(r.SustainCb, g.Sustain, SilentCb);
        envelope.Set(Math.Min(a, EnvelopeSec), Math.Min(d, EnvelopeSec), cb >= SilentCb ? 0 : Math.Pow(10, -cb / 200), Math.Min(rel, EnvelopeSec));

        static double Hz(double cents) => cents >= SoundFont.OpenCutoff - 1 ? double.MaxValue : 8.176 * Math.Pow(2, cents / 1200);
        double cutoff = Math.Min(r.Cutoff, SoundFont.OpenCutoff);
        double hard = cutoff + 1200 * GmTone.CutoffOctaves * g.Bright, soft = hard - 1200 * GmTone.CutoffOctaves * g.VelBright;
        double resCb = r.Resonance + 10 * GmTone.MaxResonanceDb * g.Resonance;
        filter.Set(Hz(hard), Hz(soft), Math.Pow(10, (resCb / 10 - 3.01) / 20));
    }

    /// <summary>The bank options: each download (with its size while it isn't here; GeneralUser GS as null), the track's own file and "Cargar .sf2…".</summary>
    void ShowBanks(GmSettings g)
    {
        bankIds.Clear();
        var labels = new List<string>();
        var hints = new List<string?>();
        foreach (var d in GmBank.Downloads)
        {
            bool here = GmBank.Available(d.Id);
            bankIds.Add(GmBank.IsDefault(d.Id) ? null : d.Id);
            labels.Add(here ? d.Name : Loc.T("gm.bank.download", d.Name, ComponentInstall.Size(d.Bytes)));
            hints.Add(Loc.T("gm.bank." + d.Id + ".tip"));
        }
        if (GmBank.IsFile(g.Bank))
        {
            bankIds.Add(g.Bank);
            labels.Add(GmBank.NameOf(g.Bank));
            hints.Add(g.Bank);
        }
        bankIds.Add(FileOption);
        labels.Add(Loc.T("gm.bank.file"));
        hints.Add(Loc.T("gm.bank.file.tip"));
        bank.BreaksBefore = [bankIds.Count - 1];
        bank.SetItems([.. labels], [.. hints]);
        string key = GmBank.Key(g.Bank);
        bank.SelectedIndex = bankIds.FindIndex(b => b != FileOption && GmBank.Key(b) == key);
        bank.IsEnabled = IsEnabledForEdits;
    }

    void PickBankAt(int i)
    {
        if (i < 0 || i >= bankIds.Count) return;
        string? id = bankIds[i];
        if (id == FileOption) LoadBankFile();
        else if (GmBank.Download(id) is { } d && !GmBank.Available(d.Id)) DownloadBank(d.Id);
        else PickBank(id);
        // the choice shows once the track has it (a download or a file picker may not end in one)
        if (Current() is { } g) ShowBanks(g);
    }

    /// <summary>The folded browser's menu: every program in its family's submenu, and the kits in theirs.</summary>
    void ShowPrograms(GmSettings g)
    {
        if (programIds.Count == 0)
        {
            var labels = new List<string>();
            var groups = new List<string?>();
            for (int n = 0; n < 128; n++)
            {
                programIds.Add((n, false));
                labels.Add($"{n + 1:000}  {GmNames.Program(n)}");
                groups.Add(GmNames.Family(n / 8));
            }
            foreach (var (program, _) in GmNames.Kits)
            {
                programIds.Add((program, true));
                labels.Add(GmNames.KitOnly(program));
                groups.Add(Loc.T("gm.drums"));
            }
            programs.SetItems([.. labels], null, [.. groups]);
        }
        int at = programIds.IndexOf((g.Program, g.Drums));
        if (at >= 0) programs.SelectedIndex = at;
        else programs.ShowNone(g.Name);
        programs.IsEnabled = IsEnabledForEdits;
    }

    /// <summary>Shows a family's programs (or the kits) without changing what the track plays.</summary>
    void Browse(int family)
    {
        browsing = family;
        for (int f = 0; f <= DrumsFamily; f++) families[f].IsChecked = f == family;
        familyTitle.Text = FamilyName(family).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        tiles.Children.Clear();
        var cur = Current();
        if (family == DrumsFamily)
            foreach (var (program, _) in GmNames.Kits)
                tiles.Children.Add(Tile(program, true, GmNames.KitOnly(program), cur is { Drums: true } g && g.Program == program));
        else
            for (int n = family * 8; n < family * 8 + 8; n++)
                tiles.Children.Add(Tile(n, false, GmNames.Program(n), cur is { Drums: false } g && g.Program == n));
    }

    /// <summary>The search over every program and kit, by name, family or number; an empty one goes back to the family.</summary>
    void Search()
    {
        string q = (search.Text ?? "").Trim();
        if (q.Length == 0)
        {
            var now = Current();
            Browse(browsing >= 0 ? browsing : now == null ? 0 : now.Drums ? DrumsFamily : now.Program / 8);
            return;
        }
        for (int f = 0; f <= DrumsFamily; f++) families[f].IsChecked = false;
        tiles.Children.Clear();
        var cur = Current();
        var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        const System.Globalization.CompareOptions loose = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        bool Has(string text) => compare.IndexOf(text, q, loose) >= 0;
        int found = 0;
        for (int n = 0; n < 128; n++)
            if (Has(GmNames.Program(n)) || Has(GmNames.Family(n / 8)) || (n + 1).ToString() == q)
            {
                tiles.Children.Add(Tile(n, false, GmNames.Program(n), cur is { Drums: false } g && g.Program == n));
                found++;
            }
        foreach (var (program, _) in GmNames.Kits)
            if (Has(GmNames.KitOnly(program)) || Has(Loc.T("gm.drums")))
            {
                tiles.Children.Add(Tile(program, true, GmNames.Kit(program), cur is { Drums: true } g && g.Program == program));
                found++;
            }
        familyTitle.Text = Loc.T("gm.search.found", found).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
    }

    Control Tile(int program, bool drums, string name, bool on)
    {
        var number = new TextBlock { Classes = { "mono" }, FontSize = 11, Width = 30, VerticalAlignment = VerticalAlignment.Center, Text = (program + 1).ToString("000"), Opacity = 0.7 };
        var text = new TextBlock { Text = name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var b = new ToggleButton
        {
            Classes = { "chip" }, Height = 34, MinHeight = 34, Margin = new Thickness(3), Padding = new Thickness(10, 0),
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
        int family = g.Drums ? DrumsFamily : g.Program / 8;
        nowInfo.Text = state ?? (g.Drums ? $"{Loc.T("gm.drums")} · {GmBank.NameOf(g.Bank)}"
            : $"{g.Program + 1:000} · {GmNames.Family(family)} · {GmBank.NameOf(g.Bank)}");
        if (shownIcon != FamilyIcons[family])
        {
            shownIcon = FamilyIcons[family];
            nowIcon.Child = new TrackIconView { Icon = shownIcon, Width = 28, Height = 28, Foreground = Palette.Accent };
        }
        foreach (var r in refresh) r(g);
        ShowBanks(g);
        ShowPrograms(g);
        ShowGraphs(g);
        if (shown == (g.Program, g.Drums)) return;
        shown = (g.Program, g.Drums);
        if (!string.IsNullOrWhiteSpace(search.Text)) Search();
        else Browse(browsing < 0 ? family : browsing);
    }
}
