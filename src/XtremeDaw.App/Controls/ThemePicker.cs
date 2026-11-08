using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The top bar's palette button and its popover, port of ThemePicker.tsx: the theme's controls (<see cref="ThemeEditor"/>:
/// accent presets plus "Color a elección", the surface palettes and their adjustments) and "Restablecer", applied live
/// and remembered (<see cref="UiTheme"/>). Same as the web
/// except: "Color a elección" opens the Windows color dialog (the web's &lt;input type="color"&gt;), so the color
/// applies when you accept it instead of while you drag; the swatches don't grow on hover and the popover has
/// no shadow (no decorative effects in the native app); eight extra accent presets and three extra dark
/// surface palettes are available in the same grids.
/// </summary>
public sealed class ThemePicker : Panel
{
    readonly Button button;
    readonly Popup popup;

    /// <summary>The whole themes' tile (XTreme sets it: <c>TronLegacy.Tile</c>; the bench, which hasn't those looks, doesn't),
    /// shown only in Configuración → Apariencia (#317: not in the popover).</summary>
    public static Func<Control>? Presets { get; set; }

    public ThemePicker() : this(null) { }

    /// <summary>
    /// With <paramref name="language"/>, the popover also lists the languages above the colors and calls it with the
    /// code picked (the bench's own, asked by the user, #151; XTreme's language stays in Configuración).
    /// </summary>
    public ThemePicker(Action<string>? language)
    {
        button = new Button { Classes = { "ghost" }, Content = new TextBlock { Classes = { "icon" }, Text = "" } };
        string tip = Loc.T(language == null ? "theme.title" : "test.list.look");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        var body = new StackPanel { Spacing = 12 };
        if (language != null)
        {
            var row = new UniformGrid { Rows = 1 };
            foreach (var (code, name) in Loc.Languages)
            {
                var b = new ToggleButton
                {
                    Classes = { "seg" }, Content = name, IsChecked = code == Loc.Language,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                b.Click += (_, _) =>
                {
                    b.IsChecked = true;
                    if (code == Loc.Language) return;
                    popup!.IsOpen = false;
                    language(code);
                };
                row.Children.Add(b);
            }
            body.Children.Add(ThemeEditor.Group(Loc.T("test.list.language"), new Border { Classes = { "seg" }, Margin = new Thickness(4), Child = row }));
        }
        var editor = new ThemeEditor(presets: false);
        body.Children.Add(editor);
        var reset = new Button
        {
            Classes = { "ghost" }, Content = Loc.T("color.reset"), FontSize = 11, Height = 28, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(reset, Loc.T("color.reset"));
        reset.Click += (_, _) => UiTheme.Apply(UiTheme.Default);
        body.Children.Add(reset);
        var card = new Border
        {
            Width = 256, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
            Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), Child = body,
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            popup!.IsOpen = false;
            e.Handled = true;
        };
        popup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = 8,
            IsLightDismissEnabled = true, Child = card,
        };
        editor.AfterDialog = () => popup.IsOpen = true;
        popup.Opened += (_, _) => card.Focus();
        popup.Closed += (_, _) => button.Classes.Set("on", false);
        button.Click += (_, _) =>
        {
            popup.IsOpen = !popup.IsOpen;
            button.Classes.Set("on", popup.IsOpen);
        };
        Children.Add(button);
        Children.Add(popup);
    }

    internal static Button Swatch(Control content, string tip, double margin = 4)
    {
        var b = new Button { Classes = { "swatch" }, Content = content, Margin = new Thickness(margin), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>
    /// The ring-2 ring-fg ring-offset-2 of the selected swatch: a 2 px outline 2 px away. It takes the swatch's
    /// margin (its button gets none) instead of hanging outside it, as the button's presenter clips its content.
    /// </summary>
    internal static Border Ring(bool on, Control inner) => new()
    {
        BorderBrush = on ? Palette.Fg : Brushes.Transparent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(2), Child = inner,
    };

    /// <summary>The Windows color dialog (ChooseColor), modal to the window.</summary>
    internal static class ColorDialog
    {
        static readonly int[] Custom = new int[16];

        public static Color? Show(TopLevel? owner, Color initial)
        {
            var handle = GCHandle.Alloc(Custom, GCHandleType.Pinned);
            try
            {
                var cc = new ChooseColorStruct
                {
                    Size = Marshal.SizeOf<ChooseColorStruct>(),
                    Owner = owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
                    Rgb = initial.R | initial.G << 8 | initial.B << 16,
                    CustColors = handle.AddrOfPinnedObject(),
                    Flags = RgbInit | FullOpen,
                };
                if (!ChooseColor(ref cc)) return null;
                return Color.FromRgb((byte)(cc.Rgb & 0xff), (byte)(cc.Rgb >> 8 & 0xff), (byte)(cc.Rgb >> 16 & 0xff));
            }
            finally { handle.Free(); }
        }

        const int RgbInit = 0x1, FullOpen = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        struct ChooseColorStruct
        {
            public int Size;
            public IntPtr Owner, Instance;
            public int Rgb;
            public IntPtr CustColors;
            public int Flags;
            public IntPtr CustData, Hook, TemplateName;
        }

        [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")] static extern bool ChooseColor(ref ChooseColorStruct cc);
    }
}

/// <summary>
/// The theme's controls, the same in the top bar's popover and in Configuración → Apariencia (asked by the user, #317):
/// the accent presets plus "Color a elección", the surface palettes, four adjustments over them (brightness, contrast,
/// saturation, warmth: <see cref="UiTheme.Settings"/>) and, only in Configuración, the whole themes. Everything applies
/// live; a slider applies while it moves and is remembered once it rests (<see cref="SaveDelay"/>), so a drag doesn't
/// write config.json on every step. A double click on a slider puts it back at 0.
/// </summary>
public sealed class ThemeEditor : StackPanel
{
    readonly UniformGrid accents = new() { Columns = 5 };
    readonly UniformGrid surfaces = new() { Columns = 3 };
    readonly Border presets = new();
    readonly bool withPresets;
    readonly List<(Slider Slider, TextBlock Value, Func<UiTheme.Settings, double> Get)> adjusters = [];
    readonly DispatcherTimer saveTimer;
    bool syncing;

    /// <summary>How long a slider rests before the theme is remembered.</summary>
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Runs after the Windows color dialog closes (the popover opens itself again).</summary>
    public Action? AfterDialog { get; set; }

    public ThemeEditor(bool presets)
    {
        withPresets = presets;
        Spacing = 12;
        saveTimer = new DispatcherTimer { Interval = SaveDelay };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); UiTheme.Save(); };
        Children.Add(Group(Loc.T("theme.accent"), accents));
        Children.Add(Group(Loc.T("theme.surface"), surfaces));
        var adjust = new StackPanel { Spacing = 2, Margin = new Thickness(4, 0) };
        adjust.Children.Add(Adjuster("brightness", t => t.Brightness, (t, v) => t with { Brightness = v }));
        adjust.Children.Add(Adjuster("contrast", t => t.Contrast, (t, v) => t with { Contrast = v }));
        adjust.Children.Add(Adjuster("saturation", t => t.Saturation, (t, v) => t with { Saturation = v }));
        adjust.Children.Add(Adjuster("warmth", t => t.Warmth, (t, v) => t with { Warmth = v }));
        Children.Add(Group(Loc.T("theme.adjust"), adjust));
        // the bench has no whole themes (they change XTreme's looks too)
        if (presets && ThemePicker.Presets != null) Children.Add(Group(Loc.T("theme.presets"), this.presets));
        AttachedToVisualTree += (_, _) => { UiTheme.Changed += Build; Build(); };
        DetachedFromVisualTree += (_, _) =>
        {
            UiTheme.Changed -= Build;
            if (saveTimer.IsEnabled) { saveTimer.Stop(); UiTheme.Save(); }
        };
    }

    internal static StackPanel Group(string title, Control content)
    {
        var g = new StackPanel { Spacing = 8 };
        g.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, LetterSpacing = 0.6, Foreground = Palette.Faint });
        g.Children.Add(new Border { Margin = new Thickness(-4), Child = content });
        return g;
    }

    static string Number(double v) => v > 0 ? "+" + v.ToString("0", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.CurrentCulture);

    /// <summary>One adjustment: its name, a slider from -100 to +100 (0 in the middle, as the palette comes) and its value.</summary>
    Control Adjuster(string key, Func<UiTheme.Settings, double> get, Func<UiTheme.Settings, double, UiTheme.Settings> set)
    {
        string name = Loc.T("theme.adjust." + key);
        var slider = new Slider
        {
            Minimum = -100, Maximum = 100, SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(slider, name);
        var value = new TextBlock { Classes = { "dim", "mono" }, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "0" };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = Number(e.NewValue);
            if (syncing) return;
            UiTheme.Apply(set(UiTheme.Current, e.NewValue / 100), persist: false);
            saveTimer.Stop();
            saveTimer.Start();
        };
        slider.AddHandler(DoubleTappedEvent, (_, _) => slider.Value = 0, handledEventsToo: true);
        adjusters.Add((slider, value, get));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*,36"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    void Build()
    {
        var current = UiTheme.Current;
        accents.Children.Clear();
        foreach (var (name, hex) in UiTheme.Accents)
        {
            bool on = current.Accent == hex;
            var fill = new Border { Height = 32, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse(hex)) };
            if (on) fill.Child = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xb3, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center };
            var b = ThemePicker.Swatch(ThemePicker.Ring(on, fill), Loc.Or("theme.accent." + name, name), 0);
            string pick = hex;
            b.Click += (_, _) => UiTheme.Apply(UiTheme.Current with { Accent = pick });
            accents.Children.Add(b);
        }
        bool preset = UiTheme.Accents.Any(a => a.Hex == current.Accent);
        var custom = new Border
        {
            Height = 32, CornerRadius = new CornerRadius(6),
            Background = preset
                ? new ConicGradientBrush
                {
                    GradientStops = { new(Color.Parse("#f87171"), 0), new(Color.Parse("#facc15"), 1 / 6.0), new(Color.Parse("#4ade80"), 2 / 6.0),
                        new(Color.Parse("#22d3ee"), 3 / 6.0), new(Color.Parse("#818cf8"), 4 / 6.0), new(Color.Parse("#f472b6"), 5 / 6.0), new(Color.Parse("#f87171"), 1) },
                }
                : new SolidColorBrush(Color.Parse(current.Accent)),
        };
        var choose = ThemePicker.Swatch(ThemePicker.Ring(!preset, custom), Loc.T("theme.custom"), 0);
        choose.Click += (_, _) =>
        {
            if (ThemePicker.ColorDialog.Show(TopLevel.GetTopLevel(this), Color.Parse(UiTheme.Current.Accent)) is { } c)
                UiTheme.Apply(UiTheme.Current with { Accent = $"#{c.R:x2}{c.G:x2}{c.B:x2}" });
            AfterDialog?.Invoke();
        };
        accents.Children.Add(choose);

        surfaces.Children.Clear();
        foreach (var s in UiTheme.Surfaces)
        {
            var bars = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
            bars.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Panel) });
            var second = new Border { Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Raised) };
            Grid.SetColumn(second, 2);
            bars.Children.Add(second);
            var stack = new DockPanel();
            DockPanel.SetDock(bars, Dock.Top);
            stack.Children.Add(bars);
            stack.Children.Add(new TextBlock { Text = Loc.Or("theme.surface." + s.Id, s.Name), FontSize = 10, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(s.Dim), VerticalAlignment = VerticalAlignment.Bottom });
            var tile = new Border
            {
                Height = 48, CornerRadius = new CornerRadius(6), Padding = new Thickness(6), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(s.App), BorderBrush = new SolidColorBrush(current.Surface == s.Id ? Color.Parse(current.Accent) : s.Line), Child = stack,
            };
            var b = ThemePicker.Swatch(tile, Loc.Or("theme.surface." + s.Id, s.Name));
            string id = s.Id;
            b.Click += (_, _) => UiTheme.Apply(UiTheme.Current with { Surface = id });
            surfaces.Children.Add(b);
        }

        presets.Child = withPresets ? ThemePicker.Presets?.Invoke() : null;
        syncing = true;
        foreach (var (slider, value, get) in adjusters)
        {
            slider.Value = Math.Round(get(current) * 100);
            value.Text = Number(slider.Value);
        }
        syncing = false;
    }

}
