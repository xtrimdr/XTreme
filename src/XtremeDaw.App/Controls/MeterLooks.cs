using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The looks the level meters can take (asked by the user, picked in Configuración → Apariencia), the same for every
/// <see cref="MixerMeter"/> and <see cref="StereoMeter"/> in the window. Kept in config.json ("meterLook"); an unknown id
/// falls back to the first, which is the meter as it always was. A look only changes how the bar is painted: the
/// colors (<see cref="MeterColors"/>), their thresholds, the clip lights, the peak hold and the scale stay each meter's. The pieces that cost
/// something (the LEDs' clip geometry, the stereo gradient) are built once per size or accent, never per frame.
/// </summary>
public static class MeterLooks
{
    public const string Solid = "solid", Led = "led", Gradient = "gradient", Thin = "thin", Tron = "tron";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] All = [Solid, Led, Gradient, Thin, Tron];

    /// <summary>The looks drawn in LEDs: <see cref="Led"/> and <see cref="Tron"/>.</summary>
    public static bool Segmented(string look) => look is Led or Tron;

    const string ConfigKey = "meterLook";

    /// <summary>The look the app comes with (Apariencia's "Restablecer").</summary>
    public const string Default = Solid;

    public static string Current { get; private set; } = Default;

    /// <summary>The look changed: every meter redraws.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try { if (AppConfig.Get(ConfigKey) is { } id && All.Contains(id)) Current = id; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the default */ }
        MeterColors.Load();
    }

    /// <summary>Every meter redraws (the colors changed, <see cref="MeterColors"/>).</summary>
    internal static void Redraw() => Changed?.Invoke();

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

    // --- shared pieces --------------------------------------------------------------------------

    /// <summary>LEDs: one every this many px, with <see cref="SegGap"/> px dark between them.</summary>
    public const double SegStep = 3, SegGap = 1;
    /// <summary>How strong the whole scale shows under the level in <see cref="Gradient"/>.</summary>
    public const double GhostOpacity = 0.16;
    /// <summary>How much of a bar's thickness <see cref="Thin"/> lights.</summary>
    public const double ThinShare = 0.4;
    /// <summary>
    /// <see cref="Tron"/> (part of the Tron Legacy theme, #191): LEDs of light on black, the unlit ones still showing
    /// their color this faintly, each bar edged by a line of the accent this strong.
    /// </summary>
    public const double TronGhost = 0.14, TronEdge = 0.55;

    static IBrush? tronLevels;
    static Color tronAccent, tronMute;

    /// <summary>
    /// <see cref="MixerMeter"/>'s colors in <see cref="Tron"/>: the accent's light up to the same thresholds as its green,
    /// then amber and red, bottom to top; rebuilt only when the theme changes the accent or the amber.
    /// </summary>
    public static IBrush TronLevels()
    {
        if (MeterColors.Custom) return MeterColors.MixerLevels();
        Color a = Palette.AccentColor, m = Palette.MuteColor;
        if (tronLevels != null && a == tronAccent && m == tronMute) return tronLevels;
        tronAccent = a;
        tronMute = m;
        return tronLevels = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(a, 0.7), new GradientStop(m, 0.84), new GradientStop(Palette.RecColor, 0.96) },
        }.ToImmutable();
    }

    /// <summary>
    /// The LEDs of <paramref name="area"/> as one clip geometry: stacked from the bottom (<paramref name="vertical"/>) or
    /// lined up from the left. The meter keeps it until its size changes.
    /// </summary>
    public static StreamGeometry Segments(Rect area, bool vertical)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        double len = vertical ? area.Height : area.Width;
        for (double at = 0; at < len; at += SegStep)
        {
            double size = Math.Min(SegStep - SegGap, len - at);
            var r = vertical ? new Rect(area.X, area.Bottom - at - size, area.Width, size) : new Rect(area.X + at, area.Y, size, area.Height);
            g.BeginFigure(r.TopLeft, true);
            g.LineTo(r.TopRight);
            g.LineTo(r.BottomRight);
            g.LineTo(r.BottomLeft);
            g.EndFigure(true);
        }
        return geo;
    }

    /// <summary>A length snapped to whole LEDs (the lit part of a <see cref="Led"/> meter lights whole segments).</summary>
    public static double SnapLed(double len) => Math.Round(len / SegStep) * SegStep;

    static IBrush? stereoGradient;
    static Color gradAccent, gradMute, gradClip;

    /// <summary>
    /// <see cref="StereoMeter"/>'s colors as one left-to-right gradient over the whole scale (accent, amber at −6 dB, red
    /// at the top, or the user's <see cref="MeterColors"/>), rebuilt only when one of the three changes.
    /// </summary>
    public static IBrush StereoGradient(double split)
    {
        bool custom = MeterColors.Custom;
        Color a = custom ? MeterColors.Low : Palette.AccentColor, m = custom ? MeterColors.Hot : Palette.MuteColor, r = custom ? MeterColors.Clip : Palette.RecColor;
        if (stereoGradient != null && a == gradAccent && m == gradMute && r == gradClip) return stereoGradient;
        gradAccent = a;
        gradMute = m;
        gradClip = r;
        return stereoGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(a, split * 0.7), new GradientStop(m, split), new GradientStop(r, 1) },
        }.ToImmutable();
    }

    // --- picker ---------------------------------------------------------------------------------

    /// <summary>One chip per look with a sample of it (a mixer meter and a stereo one at fixed levels); a click picks it.</summary>
    public static Control Picker()
    {
        var grid = new UniformGrid { Columns = All.Length };
        var buttons = new List<ToggleButton>();
        foreach (string id in All)
        {
            var mixer = new MixerMeter(wide: true) { Look = id, Scale = false, Height = 64, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            mixer.Set(0.89, 0.71, 0.42, 0.32);
            var stereo = new StereoMeter { Look = id, Width = 72, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            stereo.Push(0.45f, 0.8f);
            var sample = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Children = { mixer, stereo } };
            string name = Loc.T("set.look.meter." + id);
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 12, 6, 8), IsChecked = id == Current,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel { Spacing = 8, Children = { sample, new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) =>
            {
                Set(id);
                foreach (var o in buttons) o.IsChecked = (string)o.Tag! == Current;
            };
            buttons.Add(b);
            grid.Children.Add(b);
        }
        return grid;
    }
}
