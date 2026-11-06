using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The looks the window's panels (inspector, project bar, timeline, mixer: the <c>card</c>s) can take, picked in
/// Configuración → Apariencia (asked by the user, #191): Plano, the 1 px edge they always had, or one of two
/// neumorphic ones: Relieve (a dark shadow down-right and a light one up-left) and Relieve con degradé (the same over
/// a 145° gradient). Hundido (both shadows inside) was dropped: the user didn't like it (#191); a saved "sunken" falls back to Plano. Kept in config.json ("panelLook"); an unknown id falls back to
/// Plano. The neumorphic looks drop the edge and keep the flat panels' corners (the user: the first
/// try, with 21 px corners and much wider gaps, was too much), with a little more gap than Plano (#191, 4th try); their shadows are short and soft (a touch stronger after #191: more perceived relief), and their colors come from the theme's surfaces (the user's choice: the window stays dark), so they are
/// rebuilt on every theme change. Only the panels themselves change: what is inside them stays flat. Tron (part of the
/// Tron Legacy theme, #191) is a flat panel edged by a line of the accent's light with a soft glow around it.
/// </summary>
public static class PanelLooks
{
    public const string Flat = "flat", Raised = "raised", Gradient = "gradient", Tron = "tron";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] All = [Flat, Raised, Gradient, Tron];

    const string ConfigKey = "panelLook";

    /// <summary>The look the app comes with (Apariencia's "Restablecer").</summary>
    public const string Default = Flat;

    public static string Current { get; private set; } = Default;

    /// <summary>The look changed: the window repaints its panels.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try { if (AppConfig.Get(ConfigKey) is { } id && All.Contains(id)) Current = id; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the default */ }
    }

    public static void Set(string look)
    {
        if (!All.Contains(look) || look == Current) return;
        Current = look;
        Changed?.Invoke();
        _ = Task.Run(() =>
        {
            try { AppConfig.Set(ConfigKey, look); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }

    static bool Neu(string look) => look is Raised or Gradient;

    /// <summary>Not flat: the neumorphic looks and Tron, whose glow needs the same air as their shadows.</summary>
    static bool Spaced(string look) => look != Flat;

    /// <summary>The margin around each panel (px). Half of every gap: two panels add up to twice this, and the window
    /// adds the same around their area, so the gap to the edges and the bars matches. Plano keeps the smallest gap the
    /// window had (#193: 4); the neumorphic looks get more air so their shadows breathe (#191: "quedó demasiado justa").</summary>
    public static double Margin => Spaced(Current) ? NeuMargin : FlatMargin;

    const double FlatMargin = 2, NeuMargin = 5;

    /// <summary>The panels' corner radius (px), the guide's 10 in every look.</summary>
    public const double Radius = 10;

    /// <summary>The shadows' offset and blur (px): short enough to fit in the neumorphic gaps.</summary>
    const double Offset = 3, Blur = 7;

    /// <summary>How far each shadow's color moves from its surface, toward black or white.</summary>
    const double OuterDark = 0.5, Light = 0.07;

    /// <summary>The gradient's ends: a touch lighter up-left, darker down-right (the user's #444 → #3a3a3a over #404040).</summary>
    const double GradTop = 0.05, GradBottom = 0.24;

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Tron's edge and its glow: the accent, a lit line with a soft halo around it (no offset: it's light, not a shadow).</summary>
    const double TronEdge = 0.75, TronGlow = 0.3, TronBlur = 10;

    static BoxShadows Shadows(string look, double scale)
    {
        if (look == Tron) return new BoxShadows(new BoxShadow { Blur = TronBlur * scale, Color = Color.FromArgb((byte)(255 * TronGlow), Palette.AccentColor.R, Palette.AccentColor.G, Palette.AccentColor.B) });
        if (!Neu(look)) return default;
        double o = Offset * scale, blur = Blur * scale;
        var dark = Mix(Palette.AppColor, Colors.Black, OuterDark);
        var light = Mix(Palette.PanelColor, Colors.White, Light);
        return new BoxShadows(
            new BoxShadow { OffsetX = o, OffsetY = o, Blur = blur, Color = dark },
            [new BoxShadow { OffsetX = -o, OffsetY = -o, Blur = blur, Color = light }]);
    }

    static IBrush GradientOf(Color c) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.2, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.8, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Mix(c, Colors.White, GradTop), 0), new GradientStop(Mix(c, Colors.Black, GradBottom), 1) },
    };

    /// <summary>A panel's frame in the current look: its corners, its edge (none when neumorphic) and its shadows. For a
    /// panel that clips its content, this goes on the edge drawn over it (<c>card-edge</c>).</summary>
    public static void Frame(Border b) => Frame(b, Current, 1);

    static void Frame(Border b, string look, double scale)
    {
        b.CornerRadius = new CornerRadius(Radius * scale);
        b.BorderThickness = new Thickness(Neu(look) ? 0 : 1);
        b.BorderBrush = look == Tron ? Palette.Alpha(Palette.AccentColor, TronEdge) : Palette.Line;
        b.BoxShadow = Shadows(look, scale);
    }

    /// <summary>A panel's background: <paramref name="flat"/> (a theme brush, which follows the theme by itself), or
    /// its gradient over <paramref name="surface"/> with Relieve con degradé.</summary>
    public static void Fill(Border b, IBrush flat, Color surface) => b.Background = Current == Gradient ? GradientOf(surface) : flat;

    /// <summary>The corners of a border that clips a panel's content (<c>card-clip</c>).</summary>
    public static void Round(Border b) => b.CornerRadius = new CornerRadius(Radius);

    // --- picker ---------------------------------------------------------------------------------

    /// <summary>A sample panel's size and its shadows' scale in the picker.</summary>
    const double SampleW = 76, SampleH = 46, SampleScale = 1, SamplePad = 12;

    /// <summary>One chip per look with a small panel over the window's background, in the theme's colors; a click picks it.</summary>
    public static Control Picker()
    {
        var grid = new UniformGrid { Columns = All.Length };
        var buttons = new List<ToggleButton>();
        var samples = new List<(Border Panel, string Id)>();
        void Paint()
        {
            foreach (var (panel, id) in samples)
            {
                Frame(panel, id, SampleScale);
                panel.Background = id == Gradient ? GradientOf(Palette.PanelColor) : Palette.Panel;
            }
        }
        foreach (string id in All)
        {
            var panel = new Border { Width = SampleW, Height = SampleH };
            samples.Add((panel, id));
            var stage = new Border
            {
                Background = Palette.App, CornerRadius = new CornerRadius(6), Padding = new Thickness(SamplePad),
                HorizontalAlignment = HorizontalAlignment.Center, Child = panel,
            };
            string name = Loc.T("set.look.panel." + id);
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 10, 6, 8), IsChecked = id == Current,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel { Spacing = 8, Children = { stage, new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => Set(id);
            buttons.Add(b);
            grid.Children.Add(b);
        }
        Paint();
        void Sync() { foreach (var o in buttons) o.IsChecked = (string)o.Tag! == Current; }
        // the samples follow the theme, and the chips a reset or a change from elsewhere, while the picker is shown
        grid.AttachedToVisualTree += (_, _) => { UiTheme.Changed += Paint; Changed += Sync; Sync(); Paint(); };
        grid.DetachedFromVisualTree += (_, _) => { UiTheme.Changed -= Paint; Changed -= Sync; };
        return grid;
    }
}
