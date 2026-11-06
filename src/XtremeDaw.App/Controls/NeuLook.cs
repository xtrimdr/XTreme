using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The neumorphic surface the Relieve knobs and faders are made of (asked by the user, #191, after the panels'
/// <see cref="PanelLooks"/>): a cap in the panel's own color, raised by a dark shadow down-right and a light one
/// up-left, or the same over a 145° gradient, and a slot sunk into the panel. Its colors come from the theme, so they
/// are rebuilt when the theme's surfaces change, on the first draw after it (never on every redraw: a draw only
/// compares two colors). UI thread only, like every draw.
/// </summary>
public static class NeuLook
{
    /// <summary>How far each shadow's color moves from its surface, toward black or white: the panels' values, a touch
    /// lighter on the light side, since a knob is small.</summary>
    const double DarkMix = 0.5, LightMix = 0.09;

    /// <summary>The gradient's ends, the panels' (a touch lighter up-left, darker down-right).</summary>
    const double GradTop = 0.06, GradBottom = 0.24;

    /// <summary>The sunk slot: darker than the panel, with a lighter lip at the bottom right.</summary>
    const double SlotMix = 0.38, LipMix = 0.1;

    /// <summary>A knob's scale, sunk like the slot: darker than the panel, and a little less under the pointer (still
    /// well under the panel, so it never melts into it).</summary>
    const double GrooveMix = 0.55, GrooveHotMix = 0.35;

    static Color app, panel;
    static bool built;

    public static Color Dark { get; private set; }
    public static Color Light { get; private set; }
    public static IBrush Cap { get; private set; } = Brushes.Transparent;
    public static IBrush CapGradient { get; private set; } = Brushes.Transparent;
    /// <summary>The gradient turned around: a dish pressed into a gradient cap.</summary>
    public static IBrush Dish { get; private set; } = Brushes.Transparent;
    public static IBrush Slot { get; private set; } = Brushes.Transparent;
    public static IBrush Groove { get; private set; } = Brushes.Transparent;
    public static IBrush GrooveHot { get; private set; } = Brushes.Transparent;
    public static IPen SlotEdge { get; private set; } = new ImmutablePen(Brushes.Transparent);
    public static IPen CapEdge { get; private set; } = new ImmutablePen(Brushes.Transparent);

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    static Color A(byte a, Color c) => Color.FromArgb(a, c.R, c.G, c.B);

    static IImmutableBrush Diagonal(Color from, Color to) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.2, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.8, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    }.ToImmutable();

    /// <summary>Rebuilds the brushes if the theme's surfaces changed since the last draw.</summary>
    public static void Ensure()
    {
        Color a = Palette.AppColor, p = Palette.PanelColor;
        if (built && a == app && p == panel) return;
        built = true;
        app = a;
        panel = p;
        Dark = Mix(a, Colors.Black, DarkMix);
        Light = Mix(p, Colors.White, LightMix);
        Cap = new ImmutableSolidColorBrush(p);
        Color top = Mix(p, Colors.White, GradTop), bottom = Mix(p, Colors.Black, GradBottom);
        CapGradient = Diagonal(top, bottom);
        Dish = Diagonal(bottom, top);
        Slot = new ImmutableSolidColorBrush(Mix(a, Colors.Black, SlotMix));
        Groove = new ImmutableSolidColorBrush(Mix(p, Colors.Black, GrooveMix));
        GrooveHot = new ImmutableSolidColorBrush(Mix(p, Colors.Black, GrooveHotMix));
        SlotEdge = new ImmutablePen(Diagonal(A(140, Colors.Black), A(255, Mix(p, Colors.White, LipMix))));
        CapEdge = new ImmutablePen(Diagonal(A(40, Colors.White), A(60, Colors.Black)));
    }

    /// <summary>
    /// A raised shape: <paramref name="rr"/> filled with <paramref name="fill"/> over its two shadows,
    /// <paramref name="offset"/> px away and <paramref name="blur"/> px soft. Drawn twice, a shadow each time, so no
    /// shadow list is allocated: the second fill covers the first.
    /// </summary>
    public static void Raised(DrawingContext ctx, RoundedRect rr, IBrush fill, double offset, double blur)
    {
        ctx.DrawRectangle(fill, null, rr, new BoxShadows(new BoxShadow { OffsetX = offset, OffsetY = offset, Blur = blur, Color = Dark }));
        ctx.DrawRectangle(fill, CapEdge, rr, new BoxShadows(new BoxShadow { OffsetX = -offset, OffsetY = -offset, Blur = blur, Color = Light }));
    }
}
