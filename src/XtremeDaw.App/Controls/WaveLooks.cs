using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The looks the timeline's audio waves can take (asked by the user, picked in Configuración → Apariencia). Kept in
/// config.json ("waveLook"); an unknown id falls back to the first, the wave as it always was. The timeline still reads
/// the peaks (or the samples), applies the clip's gain and its curve, and keeps one geometry per clip until something
/// changes; a look only decides how those columns become a geometry (<see cref="Emit"/>) and how it is painted
/// (<see cref="Paint"/>), so a change of look costs one rebuild of each wave, not one per frame.
/// </summary>
public static class WaveLooks
{
    public const string Filled = "filled", Outline = "outline", Rectified = "rectified", Bars = "bars";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] All = [Filled, Outline, Rectified, Bars];

    const string ConfigKey = "waveLook";

    /// <summary>The look the app comes with (Apariencia's "Restablecer").</summary>
    public const string Default = Filled;

    public static string Current { get; private set; } = Default;

    /// <summary>The look changed: whoever caches waves drops them and redraws.</summary>
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

    /// <summary><see cref="Bars"/>: one bar every this many px, <see cref="BarW"/> wide.</summary>
    const int BarStep = 3;
    const double BarW = 2;
    /// <summary>How strong <see cref="Outline"/> fills its shape, as a share of the wave's own alpha.</summary>
    const double OutlineFill = 0.3;

    /// <summary>
    /// The columns of a wave as a geometry in <paramref name="look"/>. Column i is at x = <paramref name="x0"/> + i, its
    /// highest and lowest values (gain applied, −1..1) in <paramref name="hi"/> and <paramref name="lo"/>; NaN = no
    /// column there. <paramref name="origin"/> is the clip's left edge, so the bars stay on the clip while it scrolls.
    /// </summary>
    public static void Emit(string look, StreamGeometryContext g, int x0, int n, double[] hi, double[] lo, double top, double bottom, double origin)
    {
        double mid = (top + bottom) / 2, half = (bottom - top) / 2;
        switch (look)
        {
            case Outline:
            {
                int first = -1, last = -1;
                for (int i = 0; i < n; i++)
                {
                    if (double.IsNaN(hi[i])) continue;
                    var p = new Point(x0 + i + 0.5, mid - hi[i] * half);
                    if (first < 0) { g.BeginFigure(p, true); first = i; }
                    else g.LineTo(p);
                    last = i;
                }
                if (first < 0) return;
                for (int i = last; i >= first; i--)
                    if (!double.IsNaN(lo[i])) g.LineTo(new Point(x0 + i + 0.5, mid - lo[i] * half));
                g.EndFigure(true);
                break;
            }
            case Rectified:
            {
                double span = bottom - top;
                for (int i = 0; i < n; i++)
                {
                    if (double.IsNaN(hi[i])) continue;
                    double ya = bottom - Math.Clamp(Math.Max(hi[i], -lo[i]), 0, 1) * span;
                    if (bottom - ya < 1) ya = bottom - 1;
                    g.BeginFigure(new Point(x0 + i + 0.5, ya), false);
                    g.LineTo(new Point(x0 + i + 0.5, bottom));
                    g.EndFigure(false);
                }
                break;
            }
            case Bars:
            {
                // the first bar starts on the clip's grid of BarStep px
                int phase = ((x0 - (int)Math.Floor(origin)) % BarStep + BarStep) % BarStep;
                for (int s = phase == 0 ? 0 : BarStep - phase; s < n; s += BarStep)
                {
                    double amp = -1;
                    for (int i = s; i < s + BarStep && i < n; i++)
                        if (!double.IsNaN(hi[i])) amp = Math.Max(amp, Math.Max(hi[i], -lo[i]));
                    if (amp < 0) continue;
                    double a = Math.Max(Math.Clamp(amp, 0, 1) * half, 0.5);
                    Rect(g, new Rect(x0 + s, mid - a, BarW, 2 * a));
                }
                break;
            }
            default:
                for (int i = 0; i < n; i++)
                {
                    if (double.IsNaN(hi[i])) continue;
                    double ya = mid - hi[i] * half, yb = mid - lo[i] * half;
                    if (yb - ya < 1) yb = ya + 1;
                    g.BeginFigure(new Point(x0 + i + 0.5, ya), false);
                    g.LineTo(new Point(x0 + i + 0.5, yb));
                    g.EndFigure(false);
                }
                break;
        }
    }

    static void Rect(StreamGeometryContext g, Rect r)
    {
        g.BeginFigure(r.TopLeft, true);
        g.LineTo(r.TopRight);
        g.LineTo(r.BottomRight);
        g.LineTo(r.BottomLeft);
        g.EndFigure(true);
    }

    /// <summary>Where a sample (gain applied, −1..1) goes at the deepest zoom, where the wave is a line through the samples:
    /// from the base in <see cref="Rectified"/>, around the middle in the others.</summary>
    public static double LineY(string look, double v, double top, double bottom) =>
        look == Rectified ? bottom - Math.Abs(v) * (bottom - top) : (top + bottom) / 2 - v * (bottom - top) / 2;

    /// <summary>
    /// Paints a wave built by <see cref="Emit"/> in <paramref name="wave"/>'s color; <paramref name="line"/> = it is the
    /// line through the samples (the deepest zoom), which every look strokes.
    /// </summary>
    public static void Paint(string look, DrawingContext ctx, Geometry geo, IBrush wave, bool line)
    {
        if (line || look is Filled or Rectified)
        {
            ctx.DrawGeometry(null, Palette.Pen(wave, 1), geo);
            return;
        }
        if (look == Bars)
        {
            ctx.DrawGeometry(wave, null, geo);
            return;
        }
        IBrush? fill = wave is ISolidColorBrush s ? Palette.Alpha(s.Color, s.Color.A / 255.0 * s.Opacity * OutlineFill) : null;
        ctx.DrawGeometry(fill, Palette.Pen(wave, 1, PenLineCap.Flat, PenLineJoin.Round), geo);
    }

    // --- picker ---------------------------------------------------------------------------------

    /// <summary>One chip per look with a sample of it (a made-up wave drawn the same way); a click picks it.</summary>
    public static Control Picker()
    {
        var grid = new UniformGrid { Columns = All.Length };
        var buttons = new List<ToggleButton>();
        foreach (string id in All)
        {
            string name = Loc.T("set.look.wave." + id);
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 12, 6, 8), IsChecked = id == Current,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { new Sample(id) { Width = 104, Height = 40, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } },
                },
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

    /// <summary>A made-up clip (three hits that fade out) drawn in one look, in the accent, inside a clip's box. Its
    /// geometry is built once per size.</summary>
    sealed class Sample(string look) : Control
    {
        StreamGeometry? geo;
        Size builtFor;

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            if (size.Width < 4 || size.Height < 4) return;
            var box = new Rect(size);
            ctx.DrawRectangle(Palette.Alpha(Palette.HoverColor, 0.4), Palette.Pen(Palette.Line), box.Deflate(0.5), 5, 5);
            if (geo == null || size != builtFor)
            {
                builtFor = size;
                int n = (int)size.Width - 4;
                var hi = new double[n];
                var lo = new double[n];
                double[] hits = [0.03, 0.36, 0.64];
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)n, env = 0;
                    foreach (double at in hits) if (t >= at) env += Math.Exp(-(t - at) * 9);
                    double a = Math.Min(0.95, env * 0.8);
                    hi[i] = a * (0.75 + 0.25 * Math.Sin(i * 1.7));
                    lo[i] = -a * (0.75 + 0.25 * Math.Cos(i * 2.3));
                }
                geo = new StreamGeometry();
                using (var g = geo.Open()) Emit(look, g, 2, n, hi, lo, 5, size.Height - 5, 2);
            }
            Paint(look, ctx, geo, Palette.Alpha(Palette.AccentColor, 0.8), false);
        }
    }
}
