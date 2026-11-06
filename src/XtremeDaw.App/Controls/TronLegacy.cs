using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The Tron Legacy theme (asked by the user, #191: "un tema para todos los elementos con el aspecto de la película"):
/// one pick in the theme picker sets every look at once, the blue-black surface and the light-line cyan
/// (<see cref="UiTheme"/>), panels, knobs and faders edged in glowing light, meters in LEDs of light, outlined waves and Orbitron for
/// the numbers. Each piece stays a normal choice of its own in Configuración → Apariencia, so the user can change any
/// of them afterwards; "on" means every piece is still Tron's.
/// </summary>
public static class TronLegacy
{
    public static bool IsOn =>
        UiTheme.Current.Surface == UiTheme.TronSurface && UiTheme.Current.Accent == UiTheme.TronAccent
        && PanelLooks.Current == PanelLooks.Tron && KnobLooks.Current == KnobLooks.Tron && FaderLooks.Current == FaderLooks.Tron
        && MeterLooks.Current == MeterLooks.Tron && WaveLooks.Current == WaveLooks.Outline && UiFonts.NumberFont == NumberFont;

    const string NumberFont = "orbitron";

    public static void Apply()
    {
        PanelLooks.Set(PanelLooks.Tron);
        KnobLooks.Set(KnobLooks.Tron);
        FaderLooks.Set(FaderLooks.Tron);
        MeterLooks.Set(MeterLooks.Tron);
        WaveLooks.Set(WaveLooks.Outline);
        UiFonts.SetNumberFont(NumberFont);
        // last: its Changed repaints what the looks above left in the old colors
        UiTheme.Apply(new UiTheme.Settings(UiTheme.TronAccent, UiTheme.TronSurface));
    }

    /// <summary>The Tron Legacy tile: its name in the film's light on its black, edged in light when it's the one on.</summary>
    public static Control Tile()
    {
        var tron = UiTheme.Surfaces.First(s => s.Id == UiTheme.TronSurface);
        var cyan = Color.Parse(UiTheme.TronAccent);
        bool on = TronLegacy.IsOn;
        var tile = new Border
        {
            Height = 40, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(on ? 2 : 1),
            Background = new SolidColorBrush(tron.App), BorderBrush = new SolidColorBrush(on ? cyan : Color.FromArgb(0x99, cyan.R, cyan.G, cyan.B)),
            Child = new TextBlock
            {
                Text = "TRON LEGACY", FontSize = 12, FontWeight = FontWeight.SemiBold, LetterSpacing = 2.4, Foreground = new SolidColorBrush(cyan),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var b = ThemePicker.Swatch(tile, Loc.T("theme.preset.tron"));
        b.Click += (_, _) => TronLegacy.Apply();
        return b;
    }
}
