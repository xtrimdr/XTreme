using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The level meters' colors (#202, asked by the user, picked in Configuración → Apariencia): "Por defecto", the colors
/// they always had, which can't be touched, or "Custom", three colors of the user's own (Bajo, Caliente, Clip) for every
/// <see cref="MixerMeter"/> and <see cref="StereoMeter"/> in every <see cref="MeterLooks"/> look, Tron included. Only the
/// colors change: where each one starts (−6 dB, near 0 dB) stays as it is, so a meter still says the same. The brushes
/// are built once per change and handed out as they are each frame, never rebuilt while drawing. Kept in config.json
/// ("meterColors" says if the custom ones are on, "meterColorsCustom" keeps them while the default is shown).
/// </summary>
public static class MeterColors
{
    const string ModeKey = "meterColors", ColorsKey = "meterColorsCustom", CustomMode = "custom";

    public static readonly Color DefaultLow = Palette.MeterGreen, DefaultHot = Palette.MeterYellow, DefaultClip = Palette.MeterRed;

    /// <summary>The custom colors are on (otherwise every meter shows its own default ones).</summary>
    public static bool Custom { get; private set; }
    /// <summary>The custom colors, kept while the default ones are shown.</summary>
    public static Color Low { get; private set; } = DefaultLow;
    public static Color Hot { get; private set; } = DefaultHot;
    public static Color Clip { get; private set; } = DefaultClip;

    /// <summary>On or off, or a color changed (also while a color is being dragged): every meter redraws through
    /// <see cref="MeterLooks.Changed"/>; this one is for the editor and Apariencia's "Restablecer".</summary>
    public static event Action? Changed;

    /// <summary><see cref="MixerMeter"/>'s colors as they always were, bottom to top.</summary>
    static readonly IBrush DefaultLevels = Levels(DefaultLow, DefaultHot, DefaultClip);
    static IBrush customLevels = DefaultLevels;
    static IBrush lowBrush = new ImmutableSolidColorBrush(DefaultLow), hotBrush = new ImmutableSolidColorBrush(DefaultHot);

    public static void Load()
    {
        try
        {
            if (AppConfig.Get(ColorsKey) is { } s && Parse(s) is { } c) Store(c.Low, c.Hot, c.Clip);
            Custom = AppConfig.Get(ModeKey) == CustomMode;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the default */ }
    }

    static (Color Low, Color Hot, Color Clip)? Parse(string s)
    {
        var parts = s.Split(',');
        if (parts.Length != 3) return null;
        Span<Color> c = stackalloc Color[3];
        for (int i = 0; i < 3; i++)
            if (!Color.TryParse(parts[i].Trim(), out c[i])) return null;
        return (c[0], c[1], c[2]);
    }

    static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    static void Store(Color low, Color hot, Color clip)
    {
        Low = low; Hot = hot; Clip = clip;
        customLevels = Levels(low, hot, clip);
        lowBrush = new ImmutableSolidColorBrush(low);
        hotBrush = new ImmutableSolidColorBrush(hot);
    }

    /// <summary>Turns the custom colors on or off (off = the default, the "Restablecer" of Apariencia).</summary>
    public static void SetCustom(bool on)
    {
        if (on == Custom) return;
        Custom = on;
        Notify();
        Save(ModeKey, on ? CustomMode : null);
    }

    /// <summary>Sets the custom colors; <paramref name="save"/> false while a color is being dragged (saved when it's let go).</summary>
    public static void SetColors(Color low, Color hot, Color clip, bool save = true)
    {
        if (low != Low || hot != Hot || clip != Clip)
        {
            Store(low, hot, clip);
            if (Custom) Notify(); else Changed?.Invoke();
        }
        if (save) Save(ColorsKey, $"{Hex(low)},{Hex(hot)},{Hex(clip)}");
    }

    static void Notify()
    {
        MeterLooks.Redraw();
        Changed?.Invoke();
    }

    static void Save(string key, string? value) => _ = Task.Run(() =>
    {
        try { AppConfig.Set(key, value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
    });

    // --- the brushes the meters draw with -------------------------------------------------------

    /// <summary>A meter's colors bottom to top: the low one up to 70 % of the scale (−6 dB), the hot one by 84 %, the clip
    /// one at the top. The thresholds are the meters' own and don't change with the colors.</summary>
    static IBrush Levels(Color low, Color hot, Color clip) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(low, 0), new GradientStop(low, 0.7), new GradientStop(hot, 0.84), new GradientStop(clip, 0.96) },
    }.ToImmutable();

    /// <summary><see cref="MixerMeter"/>'s gradient: the custom one, or green, yellow and red.</summary>
    public static IBrush MixerLevels() => Custom ? customLevels : DefaultLevels;

    /// <summary><see cref="StereoMeter"/>'s bar under −6 dB: the custom low color, or the accent.</summary>
    public static IBrush StereoLow => Custom ? lowBrush : Palette.Accent;
    /// <summary><see cref="StereoMeter"/>'s bar over −6 dB: the custom hot color, or the amber.</summary>
    public static IBrush StereoHot => Custom ? hotBrush : Palette.Mute;

    // --- the editor -----------------------------------------------------------------------------

    /// <summary>Saturation and brightness of the dice's colors: the low one calmer, the clip one the brightest.</summary>
    static readonly (double S, double V)[] DiceShades = [(0.7, 0.85), (0.85, 0.97), (0.9, 1)];

    /// <summary>
    /// "Por defecto" / "Custom", a preview of the gradient with the three colors under it (a click picks which one to
    /// change), a row of ready colors, a color field for anything else and a dice that draws a combination that works.
    /// In "Por defecto" everything but the switch is off.
    /// </summary>
    public static Control Editor()
    {
        int sel = 0;
        var root = new StackPanel { Spacing = 10 };

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        ToggleButton Mode(string key, bool custom)
        {
            var b = new ToggleButton { Classes = { "chip" }, Content = Loc.T(key), Padding = new Thickness(12, 5) };
            b.Click += (_, _) => SetCustom(custom);
            modes.Children.Add(b);
            return b;
        }
        var byDefault = Mode("set.look.meterColors.default", false);
        var custom = Mode("set.look.meterColors.custom", true);
        root.Children.Add(modes);

        var body = new StackPanel { Spacing = 10 };
        root.Children.Add(body);

        var preview = new Border { Height = 22, CornerRadius = new CornerRadius(4) };
        body.Children.Add(preview);

        string[] names = ["set.look.meterColors.low", "set.look.meterColors.hot", "set.look.meterColors.clip"];
        var stops = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var dots = new Border[3];
        var stopButtons = new ToggleButton[3];
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            dots[i] = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), BorderBrush = Palette.Line };
            var b = new ToggleButton
            {
                Classes = { "chip" }, Padding = new Thickness(8, 4), Margin = new Thickness(0, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dots[i], new TextBlock { Text = Loc.T(names[i]), VerticalAlignment = VerticalAlignment.Center } } },
            };
            AutomationProperties.SetName(b, Loc.T(names[i]));
            b.Click += (_, _) => { sel = k; Sync(); };
            Grid.SetColumn(b, i);
            stops.Children.Add(b);
            stopButtons[i] = b;
        }
        body.Children.Add(stops);

        Color Selected() => sel switch { 0 => Low, 1 => Hot, _ => Clip };
        void Put(Color c, bool save)
        {
            if (sel == 0) SetColors(c, Hot, Clip, save);
            else if (sel == 1) SetColors(Low, c, Clip, save);
            else SetColors(Low, Hot, c, save);
        }

        var field = new ColorField { Width = 220, Height = 120 };
        // while a color is dragged the field already shows it: setting it back would round its hue
        bool dragging = false;
        field.Changed += _ => dragging = true;
        field.Committed += _ => dragging = false;
        field.Changed += c => Put(c, false);
        field.Committed += c => Put(c, true);

        var swatches = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 8 * 30 };
        for (int i = 0; i < TrackIcons.Colors.Length; i += 4)
        {
            var c = Color.Parse(TrackIcons.Colors[i]);
            var b = new Button
            {
                Classes = { "swatch" }, Width = 26, Height = 26, Margin = new Thickness(2), Padding = new Thickness(0),
                Content = new Border { CornerRadius = new CornerRadius(5), Background = new ImmutableSolidColorBrush(c) },
            };
            b.Click += (_, _) => { Put(c, true); field.Set(c); };
            swatches.Children.Add(b);
        }
        foreach (string hex in (string[])["#ffffff", "#9ca3af", "#22c55e", "#eab308", "#ef4444", "#000000"])
        {
            var c = Color.Parse(hex);
            var b = new Button
            {
                Classes = { "swatch" }, Width = 26, Height = 26, Margin = new Thickness(2), Padding = new Thickness(0),
                Content = new Border { CornerRadius = new CornerRadius(5), Background = new ImmutableSolidColorBrush(c), BorderThickness = new Thickness(1), BorderBrush = Palette.Line },
            };
            b.Click += (_, _) => { Put(c, true); field.Set(c); };
            swatches.Children.Add(b);
        }

        var dice = new Button
        {
            Classes = { "chip" }, Padding = new Thickness(10, 5),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Classes = { "icon" }, Text = "", VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = Loc.T("set.look.meterColors.dice"), VerticalAlignment = VerticalAlignment.Center } } },
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 6, 0, 0),
        };
        ToolTip.SetTip(dice, Loc.T("set.look.meterColors.dice.tip"));
        dice.Click += (_, _) =>
        {
            // a base hue and a walk of 40 to 90° in either direction: neighbors that read as one gradient, never mud
            double h = Random.Shared.NextDouble() * 360, step = (40 + Random.Shared.NextDouble() * 50) * (Random.Shared.Next(2) == 0 ? -1 : 1);
            Span<Color> c = stackalloc Color[3];
            for (int i = 0; i < 3; i++)
                c[i] = new HsvColor(1, ((h + step * i) % 360 + 360) % 360, DiceShades[i].S, DiceShades[i].V).ToRgb();
            SetColors(c[0], c[1], c[2]);
            field.Set(Selected());
        };

        var left = new StackPanel { Children = { swatches, dice } };
        var picks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { left, field } };
        body.Children.Add(picks);

        void Sync()
        {
            byDefault.IsChecked = !Custom;
            custom.IsChecked = Custom;
            body.IsEnabled = Custom;
            body.Opacity = Custom ? 1 : 0.5;
            Color low = Custom ? Low : DefaultLow, hot = Custom ? Hot : DefaultHot, clip = Custom ? Clip : DefaultClip;
            preview.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(low, 0), new GradientStop(low, 0.7), new GradientStop(hot, 0.84), new GradientStop(clip, 0.96) },
            };
            dots[0].Background = new ImmutableSolidColorBrush(low);
            dots[1].Background = new ImmutableSolidColorBrush(hot);
            dots[2].Background = new ImmutableSolidColorBrush(clip);
            for (int i = 0; i < 3; i++) stopButtons[i].IsChecked = i == sel;
        }
        void SyncAll()
        {
            Sync();
            field.Set(Selected());
        }
        SyncAll();
        void OnChanged() { if (dragging) Sync(); else SyncAll(); }
        root.AttachedToVisualTree += (_, _) => { Changed += OnChanged; SyncAll(); };
        root.DetachedFromVisualTree += (_, _) => Changed -= OnChanged;
        // picking which color to change shows it on the field
        foreach (var b in stopButtons) b.Click += (_, _) => field.Set(Selected());
        return root;
    }
}
