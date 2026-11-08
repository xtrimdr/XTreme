using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The looks a separator (<c>Folder.Separator</c>) can take, the same in the timeline and in the mixer, picked in
/// Configuración → Apariencia (asked by the user, #293): Doble línea (the console style the user chose in #289, the
/// default), Línea simple, Banda (a soft band of its color behind the name), Etiqueta (the name on a pill of its color,
/// like a console's paper tape) and Surco (a sunken cut, a dark line with a lit one under it, the name in its color).
/// Kept in config.json ("separatorLook"); an unknown id falls back to the default. The drawing lives here so the
/// timeline's row, the mixer's strip (<see cref="Rule"/>) and the picker's samples can't drift apart; nothing here
/// allocates per frame (brushes, pens and texts come from <see cref="Palette"/>'s caches).
/// </summary>
public static class SeparatorLooks
{
    public const string Double = "double", Single = "single", Band = "band", Label = "label", Groove = "groove";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] All = [Double, Single, Band, Label, Groove];

    const string ConfigKey = "separatorLook";

    /// <summary>The look the app comes with (Apariencia's "Restablecer").</summary>
    public const string Default = Double;

    public static string Current { get; private set; } = Default;

    /// <summary>The look changed: the timeline redraws and the mixer rebuilds its strips once.</summary>
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

    /// <summary>Px between the two lines of Doble línea.</summary>
    public const double LinesApart = 4;
    /// <summary>Opacity of a separator's lines.</summary>
    public const double LineAlpha = 0.7;
    /// <summary>Opacity of Banda's fill.</summary>
    public const double BandAlpha = 0.16;
    /// <summary>Px Banda's fill leaves free above and below, inside the row.</summary>
    const double BandInset = 3;
    /// <summary>Surco: how dark its cut is and how lit the line under it.</summary>
    const double GrooveDark = 0.6, GrooveLit = 0.1;
    /// <summary>Etiqueta's pill: its padding around the name and its corners.</summary>
    const double PillPadX = 5, PillPadY = 1, PillRadius = 3;
    /// <summary>The name's size and the gap between it and the lines.</summary>
    public const double NameSize = 9.5, NameGap = 6;
    /// <summary>Px of line drawn before the name.</summary>
    public const double Lead = 14;

    /// <summary>Whether the look writes the name over the window's background (and the lines must stop around it).</summary>
    static bool LinesBreakForName(string look) => look != Band;

    /// <summary>The line part of a look, from <paramref name="x0"/> to <paramref name="x1"/>, centered on <paramref name="mid"/> (a half pixel).</summary>
    public static void DrawLine(DrawingContext ctx, string look, Color c, double x0, double x1, double mid)
    {
        if (x1 <= x0) return;
        switch (look)
        {
            case Single:
            case Label:
                ctx.DrawLine(Palette.Pen(Palette.Alpha(c, LineAlpha)), new Point(x0, mid), new Point(x1, mid));
                break;
            case Band:
                break;
            case Groove:
                ctx.FillRectangle(Palette.Alpha(Colors.Black, GrooveDark), new Rect(x0, mid - 0.5, x1 - x0, 1));
                ctx.FillRectangle(Palette.Alpha(Palette.FgColor, GrooveLit), new Rect(x0, mid + 0.5, x1 - x0, 1));
                break;
            default:
                var pen = Palette.Pen(Palette.Alpha(c, LineAlpha));
                double h = LinesApart / 2;
                ctx.DrawLine(pen, new Point(x0, mid - h), new Point(x1, mid - h));
                ctx.DrawLine(pen, new Point(x0, mid + h), new Point(x1, mid + h));
                break;
        }
    }

    /// <summary>
    /// A separator's row lying down, in <paramref name="look"/>: Banda's fill from <paramref name="fillX0"/>, the lines
    /// from <paramref name="lineX0"/> to <paramref name="x1"/>, and, when <paramref name="upperName"/> isn't null, a short
    /// lead of line and the name (at most <paramref name="nameMaxW"/> wide) after it. The row is <paramref name="y"/> to
    /// <paramref name="y"/> + <paramref name="h"/>.
    /// </summary>
    public static void DrawRow(DrawingContext ctx, string look, Color c, double fillX0, double lineX0, double x1, double y, double h,
        string? upperName, double nameMaxW)
    {
        if (look == Band && x1 > fillX0)
            ctx.FillRectangle(Palette.Alpha(c, BandAlpha), new Rect(fillX0, y + BandInset, x1 - fillX0, Math.Max(0, h - 2 * BandInset)));
        double mid = Math.Round(y + h / 2) + 0.5;
        double x = lineX0;
        if (upperName is { Length: > 0 })
        {
            DrawLine(ctx, look, c, x, x + Lead, mid);
            x += Lead + NameGap;
            bool pill = look == Label;
            var fg = pill ? Palette.App : Palette.Alpha(c, 1);
            double maxW = Math.Max(20, nameMaxW - (pill ? 2 * PillPadX : 0));
            var name = Palette.Text(upperName, NameSize, fg, Palette.UiBold);
            double w = Math.Min(name.Width, maxW), ty = y + (h - name.Height) / 2;
            if (pill)
            {
                ctx.DrawRectangle(Palette.Alpha(c, 1), null, new Rect(x, ty - PillPadY, w + 2 * PillPadX, name.Height + 2 * PillPadY), PillRadius, PillRadius);
                x += PillPadX;
            }
            ctx.DrawText(upperName, NameSize, fg, new Point(x, ty), Palette.UiBold, maxW);
            x += w + (pill ? PillPadX : 0) + NameGap;
            if (!LinesBreakForName(look)) x = lineX0;
        }
        DrawLine(ctx, look, c, x, x1, mid);
    }

    /// <summary>
    /// A separator's line standing up, for the mixer's strip: the same look as the timeline's, along its height. Banda
    /// fills it (the strip shows the fill); the name goes over it in its own control.
    /// </summary>
    public sealed class Rule : Control
    {
        Color color = Colors.Transparent;

        public Color Color
        {
            get => color;
            set { if (color == value) return; color = value; InvalidateVisual(); }
        }

        public override void Render(DrawingContext ctx)
        {
            var b = Bounds;
            string look = Current;
            if (look == Band)
            {
                ctx.FillRectangle(Palette.Alpha(color, BandAlpha), new Rect(0, 0, b.Width, b.Height));
                return;
            }
            double mid = Math.Round(b.Width / 2) + 0.5;
            switch (look)
            {
                case Single:
                case Label:
                    ctx.DrawLine(Palette.Pen(Palette.Alpha(color, LineAlpha)), new Point(mid, 0), new Point(mid, b.Height));
                    break;
                case Groove:
                    ctx.FillRectangle(Palette.Alpha(Colors.Black, GrooveDark), new Rect(mid - 0.5, 0, 1, b.Height));
                    ctx.FillRectangle(Palette.Alpha(Palette.FgColor, GrooveLit), new Rect(mid + 0.5, 0, 1, b.Height));
                    break;
                default:
                    var pen = Palette.Pen(Palette.Alpha(color, LineAlpha));
                    double h = LinesApart / 2;
                    ctx.DrawLine(pen, new Point(mid - h, 0), new Point(mid - h, b.Height));
                    ctx.DrawLine(pen, new Point(mid + h, 0), new Point(mid + h, b.Height));
                    break;
            }
        }
    }

    /// <summary>How the mixer's strip shows the name in the current look: on a pill of its color (Etiqueta), over
    /// nothing (Banda: the band is behind it), or over the window's background, cutting the line.</summary>
    public static (IBrush Back, IBrush Fore) NameBrushes(Color c) => Current switch
    {
        Label => (Palette.Alpha(c, 1), Palette.App),
        Band => (Brushes.Transparent, Palette.Alpha(c, 1)),
        _ => (Palette.App, Palette.Alpha(c, 1)),
    };

    /// <summary>Etiqueta's pill corners in the mixer (none in the other looks).</summary>
    public static CornerRadius NameCorners => Current == Label ? new CornerRadius(PillRadius) : default;

    // --- picker ---------------------------------------------------------------------------------

    /// <summary>A sample row's height in the picker (its width is the chip's).</summary>
    const double SampleH = 22;

    /// <summary>A separator lying down, in one look and the accent, with a sample name.</summary>
    sealed class Sample(string look, string upperName) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            ctx.FillRectangle(Palette.App, new Rect(Bounds.Size));
            DrawRow(ctx, look, Palette.AccentColor, 0, 4, Bounds.Width, 0, Bounds.Height, upperName, Bounds.Width - Lead - 2 * NameGap - 20);
        }
    }

    /// <summary>One chip per look with a sample separator over the window's background; a click picks it.</summary>
    public static Control Picker()
    {
        var grid = new UniformGrid { Columns = All.Length };
        var buttons = new List<ToggleButton>();
        var samples = new List<Control>();
        string sampleName = Loc.T("set.look.separator.sample").ToUpperInvariant();
        foreach (string id in All)
        {
            var sample = new Sample(id, sampleName) { Height = SampleH };
            samples.Add(sample);
            var stage = new Border
            {
                Background = Palette.App, CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 8), ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch, Child = sample,
            };
            string name = Loc.T("set.look.separator." + id);
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 10, 6, 8), IsChecked = id == Current,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 8, Children = { stage, new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => Set(id);
            buttons.Add(b);
            grid.Children.Add(b);
        }
        void Paint() { foreach (var s in samples) s.InvalidateVisual(); }
        void Sync() { foreach (var o in buttons) o.IsChecked = (string)o.Tag! == Current; }
        // the samples follow the theme's accent, and the chips a reset or a change from elsewhere, while the picker is shown
        grid.AttachedToVisualTree += (_, _) => { UiTheme.Changed += Paint; Changed += Sync; Sync(); Paint(); };
        grid.DetachedFromVisualTree += (_, _) => { UiTheme.Changed -= Paint; Changed -= Sync; };
        return grid;
    }
}
