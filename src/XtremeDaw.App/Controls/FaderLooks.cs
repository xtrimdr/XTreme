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
/// The looks a fader can take (asked by the user, like <see cref="KnobLooks"/>: picked in Configuración → Apariencia),
/// the same for every fader in the window and for the timeline's horizontal volume. Kept in config.json ("faderLook");
/// an unknown id falls back to the first, the console cap the fader always had. Each look only paints the rail and
/// the cap: geometry, dragging and the value stay <see cref="Fader"/>'s, and every cap fits in the same box, so the
/// layout never changes with the look. Their greys don't follow the theme, the same as the knobs'; the lit rail and
/// the glows take the accent. Nothing here allocates on a redraw: the brushes are built once, and what follows the
/// accent comes from <see cref="Palette"/>'s caches.
/// </summary>
public static class FaderLooks
{
    public const string Console = "console", Flat = "flat", Metal = "metal", Line = "line", Bakelite = "bakelite",
        Glass = "glass", Led = "led", Ceramic = "ceramic";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] All = [Console, Flat, Metal, Line, Bakelite, Glass, Led, Ceramic];

    const string ConfigKey = "faderLook";

    /// <summary>The look the app comes with (Apariencia's "Restablecer").</summary>
    public const string Default = Console;

    public static string Current { get; private set; } = Default;

    /// <summary>The look changed: whoever paints faders redraws.</summary>
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

    // --- picker ---------------------------------------------------------------------------------

    /// <summary>A sample fader's height (px) and level in the picker.</summary>
    const double SampleH = 90, SampleValue = 0.8;

    /// <summary>The grid of samples for Configuración: one toggle per look, a small fixed fader over its name.</summary>
    public static Control Picker()
    {
        var grid = new UniformGrid { Columns = 4 };
        var buttons = new List<ToggleButton>();
        foreach (string id in All)
        {
            var sample = new Fader { Look = id, Value = SampleValue, Height = SampleH, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 10, 6, 8), IsChecked = id == Current,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel { Spacing = 8, Children = { sample, new TextBlock { Text = Loc.T("set.look.fader." + id), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            AutomationProperties.SetName(b, Loc.T("set.look.fader." + id));
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

    // --- brushes --------------------------------------------------------------------------------

    static Color C(string hex) => Color.Parse(hex);
    static Color A(byte a, Color c) => Color.FromArgb(a, c.R, c.G, c.B);
    static readonly Color White = Colors.White, Black = Colors.Black;

    static IImmutableBrush Solid(string hex) => new ImmutableSolidColorBrush(C(hex));
    static IImmutableBrush Solid(Color c) => new ImmutableSolidColorBrush(c);

    static IImmutableBrush Linear(double x0, double y0, double x1, double y1, params (Color C, double At)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new RelativePoint(x0, y0, RelativeUnit.Relative), EndPoint = new RelativePoint(x1, y1, RelativeUnit.Relative) };
        foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop(c, at));
        return b.ToImmutable();
    }

    static IImmutableBrush Vertical(params (Color C, double At)[] stops) => Linear(0, 0, 0, 1, stops);
    static IImmutableBrush Horizontal(params (Color C, double At)[] stops) => Linear(0, 0, 1, 0, stops);
    static IPen P(IImmutableBrush b, double w = 1) => new ImmutablePen(b, w);

    static readonly IBrush DropShadow = Solid(A(110, Black)), SoftShadow = Solid(A(70, Black));
    static readonly IPen BlackEdge = P(Solid("#08080a"));

    // Consola (the fader's own cap, unchanged)
    const double Groove = 3;
    static readonly IBrush ConsoleCap = Vertical((C("#c9c9d0"), 0), (C("#9b9ba3"), 0.45), (C("#8d8d95"), 0.55), (C("#606067"), 1));
    static readonly IPen GrooveDark = P(Solid(Color.FromArgb(120, 20, 20, 24))), GrooveLight = P(Solid(Color.FromArgb(90, 255, 255, 255)));
    static readonly IPen WhiteLine = P(Solid(White), 2);
    /// <summary>Under the level line: white alone doesn't read on the grey body.</summary>
    static readonly IBrush LevelBand = Solid("#141417");
    /// <summary>The empty slot's outline: strong enough to read on a selected strip or header too.</summary>
    const double SlotEdge = 0.6;

    // Plano
    static readonly IBrush FlatCap = Solid("#2c2c32"), FlatRail = Solid("#1a1a1e");
    static readonly IPen FlatEdge = P(Solid("#45454d"));

    // Aluminio
    static readonly IBrush MetalCap = Horizontal((C("#8e8e96"), 0), (C("#d8d8de"), 0.3), (C("#eeeef2"), 0.5), (C("#c4c4ca"), 0.75), (C("#7a7a82"), 1));
    static readonly IPen MetalEdge = P(Solid("#0c0c0e")), Brush1 = P(Solid(A(26, Black))), Brush2 = P(Solid(A(40, White)));
    static readonly IBrush MetalBand = Solid("#2a2a30"), MetalRail = Vertical((C("#050506"), 0), (C("#1c1c20"), 1));
    static readonly IPen MetalRailEdge = P(Horizontal((A(160, Black), 0), (A(40, White), 1)));

    // Línea fina
    static readonly IBrush LineRail = Solid("#34343a"), LineCap = Solid("#0f0f12");
    static readonly IPen LineEdge = P(Solid("#6b6b74")), LineTick = P(Solid("#45454d"));

    // Baquelita
    static readonly IBrush BakeliteCap = Linear(0.2, 0, 0.8, 1, (C("#3c3c42"), 0), (C("#121214"), 0.5), (C("#040405"), 1));
    static readonly IBrush BakeliteShine = Vertical((A(55, White), 0), (A(0, White), 1));
    static readonly IBrush Gold = Linear(0.2, 0, 0.8, 1, (C("#f7e2a1"), 0), (C("#c9a24a"), 0.5), (C("#7b5c22"), 1));
    static readonly IPen GoldRim = P((IImmutableBrush)Gold, 1.2), GoldLine = P((IImmutableBrush)Gold, 2);
    static readonly IBrush BakeliteRail = Solid("#0a0907");
    static readonly IPen BakeliteRailEdge = P(Solid("#4a3d22"));

    // Cristal
    static readonly IBrush GlassCap = Vertical((C("#3a3f4a"), 0), (C("#181b22"), 0.5), (C("#0b0c10"), 1));
    static readonly IBrush GlassShine = Vertical((A(80, White), 0), (A(8, White), 1));
    static readonly IPen GlassEdge = P(Vertical((A(80, White), 0), (A(20, White), 0.5), (A(170, Black), 1)));
    static readonly IBrush GlassRail = Solid("#0d0e12");

    // LED
    /// <summary>The rail's segments (px): pitch and lit height.</summary>
    const double LedPitch = 4, LedH = 3, LedW = 7;
    static readonly IBrush LedOff = Solid("#202025"), LedCap = Vertical((C("#2a2a30"), 0), (C("#141417"), 1));
    static readonly IPen LedCapEdge = P(Vertical((A(50, White), 0), (A(200, Black), 1)));

    // Cerámica
    static readonly IBrush CeramicCap = Linear(0.15, 0, 0.85, 1, (C("#f6f6f8"), 0), (C("#b4b4bb"), 1));
    static readonly IPen CeramicEdge = P(Linear(0.15, 0, 0.85, 1, (A(200, White), 0), (A(110, Black), 1)));
    static readonly IBrush CeramicDish = Vertical((C("#c9c9cf"), 0), (C("#f0f0f3"), 1));
    static readonly IPen CeramicLine = P(Solid("#26262b"), 2);
    static readonly IBrush CeramicRail = Solid("#141417");

    // --- drawing --------------------------------------------------------------------------------

    /// <summary>
    /// Draws a vertical fader <paramref name="w"/>×<paramref name="h"/> in <paramref name="look"/>: the rail from
    /// <paramref name="pad"/> to h − pad lit up to <paramref name="pos"/> (0..1), the unity mark at
    /// <paramref name="unity"/> and the cap (at most <paramref name="capW"/>×<paramref name="capH"/>) at the level.
    /// </summary>
    public static void Draw(string look, DrawingContext ctx, double w, double h, double pad, double capW, double capH, double pos, double unity)
    {
        double cx = Math.Round(w / 2), travel = Math.Max(1, h - 2 * pad), bottom = h - pad;
        double capY = Math.Round(bottom - travel * pos);
        var cap = new Rect(cx - capW / 2, capY - capH / 2, capW, capH);
        switch (look)
        {
            case Flat:
                Rail(ctx, FlatRail, null, cx, pad, travel, 4, 1);
                Lit(ctx, cx, bottom, travel * pos, 4, false);
                Unity(ctx, w, cx, h, pad, travel, unity, 4);
                ctx.DrawRectangle(FlatCap, FlatEdge, cap.Deflate(new Thickness(0.5, 6)), 3, 3);
                ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(cap.Left + 3, capY), new Point(cap.Right - 3, capY));
                break;
            case Metal:
                Rail(ctx, MetalRail, MetalRailEdge, cx, pad, travel, 6, 2);
                Lit(ctx, cx, bottom, travel * pos, 2, false);
                Unity(ctx, w, cx, h, pad, travel, unity, 5);
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 2)), 2, 2);
                ctx.DrawRectangle(MetalCap, MetalEdge, cap.Deflate(0.5), 2, 2);
                for (double gy = cap.Top + 2; gy < cap.Bottom - 2; gy += 2)
                    ctx.DrawLine((int)(gy - cap.Top) % 4 == 0 ? Brush1 : Brush2, new Point(cap.Left + 1, gy + 0.5), new Point(cap.Right - 1, gy + 0.5));
                ctx.FillRectangle(MetalBand, new Rect(cap.Left + 1, capY - 1.5, cap.Width - 2, 3));
                break;
            case Line:
                ctx.FillRectangle(LineRail, new Rect(cx - 0.5, pad - 4, 1, travel + 8));
                Lit(ctx, cx, bottom, travel * pos, 2, false);
                for (int k = 0; k <= 10; k++)
                {
                    double ty = Math.Round(bottom - travel * k / 10.0) - 0.5;
                    ctx.DrawLine(LineTick, new Point(cx - 6, ty), new Point(cx - 4, ty));
                    ctx.DrawLine(LineTick, new Point(cx + 4, ty), new Point(cx + 6, ty));
                }
                Unity(ctx, w, cx, h, pad, travel, unity, 7);
                var thin = cap.Deflate(new Thickness(1.5, capH / 2 - 7));
                ctx.DrawRectangle(LineCap, LineEdge, thin, 2, 2);
                ctx.DrawLine(Palette.Pen(Palette.Accent, 1.5), new Point(thin.Left + 2, capY), new Point(thin.Right - 2, capY));
                break;
            case Bakelite:
                Rail(ctx, BakeliteRail, BakeliteRailEdge, cx, pad, travel, 5, 2.5);
                Lit(ctx, cx, bottom, travel * pos, 3, false);
                Unity(ctx, w, cx, h, pad, travel, unity, 5);
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 2)), 5, 5);
                ctx.DrawRectangle(BakeliteCap, GoldRim, cap.Deflate(0.6), 5, 5);
                ctx.DrawRectangle(BakeliteShine, null, new Rect(cap.Left + 2.5, cap.Top + 2.5, cap.Width - 5, capH * 0.38), 3, 3);
                ctx.DrawLine(GoldLine, new Point(cap.Left + 3, capY), new Point(cap.Right - 3, capY));
                break;
            case Glass:
                Rail(ctx, GlassRail, Palette.Pen(Palette.Alpha(Palette.FaintColor, SlotEdge)), cx, pad, travel, 5, 2.5);
                Lit(ctx, cx, bottom, travel * pos, 3, true);
                Unity(ctx, w, cx, h, pad, travel, unity, 5);
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 2)), 6, 6);
                ctx.DrawRectangle(GlassCap, GlassEdge, cap.Deflate(0.5), 6, 6);
                ctx.DrawRectangle(GlassShine, null, new Rect(cap.Left + 2, cap.Top + 2, cap.Width - 4, capH / 2 - 4), 4, 4);
                ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.3), 6), new Point(cap.Left + 3, capY), new Point(cap.Right - 3, capY));
                ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(cap.Left + 3, capY), new Point(cap.Right - 3, capY));
                break;
            case Led:
            {
                double level = bottom - travel * pos;
                for (double y = bottom; y > pad - LedPitch / 2; y -= LedPitch)
                    ctx.FillRectangle(y - LedH / 2 >= level - 0.5 && pos > 0 ? Palette.Accent : LedOff, new Rect(cx - LedW / 2, Math.Round(y - LedH / 2), LedW, LedH));
                Unity(ctx, w, cx, h, pad, travel, unity, LedW / 2 + 2);
                var slim = cap.Deflate(new Thickness(0.5, capH / 2 - 9));
                ctx.DrawRectangle(DropShadow, null, slim.Translate(new Vector(0, 2)), 3, 3);
                ctx.DrawRectangle(LedCap, LedCapEdge, slim, 3, 3);
                ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.3), 5), new Point(slim.Left + 2, capY), new Point(slim.Right - 2, capY));
                ctx.DrawLine(Palette.Pen(Palette.Accent, 1.5), new Point(slim.Left + 2, capY), new Point(slim.Right - 2, capY));
                break;
            }
            case Ceramic:
                Rail(ctx, CeramicRail, null, cx, pad, travel, 5, 2.5);
                Lit(ctx, cx, bottom, travel * pos, 3, false);
                Unity(ctx, w, cx, h, pad, travel, unity, 5);
                ctx.DrawRectangle(SoftShadow, null, cap.Translate(new Vector(1, 3)), 7, 7);
                ctx.DrawRectangle(CeramicCap, CeramicEdge, cap.Deflate(0.5), 7, 7);
                ctx.DrawRectangle(CeramicDish, null, cap.Deflate(new Thickness(4, 7)), 4, 4);
                ctx.DrawLine(CeramicLine, new Point(cap.Left + 2, capY), new Point(cap.Right - 2, capY));
                break;
            default:
                DrawConsole(ctx, w, h, pad, cx, travel, pos, unity, cap, capY);
                break;
        }
    }

    /// <summary>The console cap the fader always had: a dark slot lit in the accent, a grey shaded body with grooves
    /// and a white line on a dark band.</summary>
    static void DrawConsole(DrawingContext ctx, double w, double h, double pad, double cx, double travel, double pos, double unity, Rect cap, double capY)
    {
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Alpha(Palette.FaintColor, SlotEdge)), new Rect(cx - 2.5, pad - 4, 5, travel + 8), 2.5, 2.5);
        ctx.DrawRectangle(Palette.Accent, null, new Rect(cx - 1.5, h - pad - travel * pos, 3, travel * pos), 1.5, 1.5);
        Unity(ctx, w, cx, h, pad, travel, unity, 5);
        ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 2)), 4, 4);
        ctx.DrawRectangle(ConsoleCap, BlackEdge, cap.Deflate(0.5), 4, 4);
        for (double gy = cap.Top + 5; gy < cap.Bottom - 5; gy += Groove)
        {
            if (Math.Abs(gy - capY) < 5) continue;
            ctx.DrawLine(GrooveDark, new Point(cap.Left + 4, gy + 0.5), new Point(cap.Right - 4, gy + 0.5));
            ctx.DrawLine(GrooveLight, new Point(cap.Left + 4, gy + 1.5), new Point(cap.Right - 4, gy + 1.5));
        }
        ctx.FillRectangle(LevelBand, new Rect(cap.Left + 1, capY - 3, cap.Width - 2, 6));
        ctx.DrawLine(WhiteLine, new Point(cap.Left + 1, capY), new Point(cap.Right - 1, capY));
    }

    /// <summary>The same cap lying down (<paramref name="cw"/>×<paramref name="ch"/>), for the timeline's horizontal
    /// volume: its level line at <paramref name="x"/>.</summary>
    public static void DrawSide(string look, DrawingContext ctx, double x, double cy, double cw, double ch)
    {
        var cap = new Rect(x - cw / 2, cy - ch / 2, cw, ch);
        Point a = new(x, cap.Top + 1), b = new(x, cap.Bottom - 1);
        switch (look)
        {
            case Flat:
                ctx.DrawRectangle(FlatCap, FlatEdge, cap.Deflate(0.5), 2, 2);
                ctx.DrawLine(Palette.Pen(Palette.Accent, 2), a, b);
                break;
            case Metal:
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 1.5)), 2, 2);
                ctx.DrawRectangle(MetalCap, MetalEdge, cap.Deflate(0.5), 2, 2);
                ctx.FillRectangle(MetalBand, new Rect(x - 1.5, cap.Top + 1, 3, ch - 2));
                break;
            case Line:
                ctx.DrawRectangle(LineCap, LineEdge, cap.Deflate(new Thickness(1.5, 0.5)), 2, 2);
                ctx.DrawLine(Palette.Pen(Palette.Accent, 1.5), a, b);
                break;
            case Bakelite:
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 1.5)), 3, 3);
                ctx.DrawRectangle(BakeliteCap, GoldRim, cap.Deflate(0.6), 3, 3);
                ctx.DrawLine(GoldLine, a, b);
                break;
            case Glass:
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 1.5)), 3, 3);
                ctx.DrawRectangle(GlassCap, GlassEdge, cap.Deflate(0.5), 3, 3);
                ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.3), 5), a, b);
                ctx.DrawLine(Palette.Pen(Palette.Accent, 2), a, b);
                break;
            case Led:
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 1.5)), 3, 3);
                ctx.DrawRectangle(LedCap, LedCapEdge, cap.Deflate(0.5), 3, 3);
                ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.3), 5), a, b);
                ctx.DrawLine(Palette.Pen(Palette.Accent, 1.5), a, b);
                break;
            case Ceramic:
                ctx.DrawRectangle(SoftShadow, null, cap.Translate(new Vector(0.5, 2)), 3, 3);
                ctx.DrawRectangle(CeramicCap, CeramicEdge, cap.Deflate(0.5), 3, 3);
                ctx.DrawLine(CeramicLine, a, b);
                break;
            default:
                ctx.DrawRectangle(DropShadow, null, cap.Translate(new Vector(0, 1.5)), 3, 3);
                ctx.DrawRectangle(ConsoleCap, BlackEdge, cap.Deflate(0.5), 3, 3);
                ctx.FillRectangle(LevelBand, new Rect(x - 2.5, cap.Top + 1, 5, ch - 2));
                ctx.DrawLine(WhiteLine, a, b);
                break;
        }
    }

    // --- pieces ---------------------------------------------------------------------------------

    /// <summary>The empty rail, <paramref name="width"/> px wide, a little past both ends of the travel.</summary>
    static void Rail(DrawingContext ctx, IBrush fill, IPen? edge, double cx, double pad, double travel, double width, double radius) =>
        ctx.DrawRectangle(fill, edge, new Rect(cx - width / 2, pad - 4, width, travel + 8), radius, radius);

    /// <summary>The rail lit in the accent from the bottom to the level; <paramref name="glow"/> lays a wider, fainter
    /// strip under it.</summary>
    static void Lit(DrawingContext ctx, double cx, double bottom, double len, double width, bool glow)
    {
        if (len <= 0) return;
        if (glow) ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.22), null, new Rect(cx - width * 1.5, bottom - len, width * 3, len), width * 1.5, width * 1.5);
        ctx.DrawRectangle(Palette.Accent, null, new Rect(cx - width / 2, bottom - len, width, len), width / 2, width / 2);
    }

    /// <summary>The 0 dB mark on both sides of the rail, leaving <paramref name="gap"/> px around its middle.</summary>
    static void Unity(DrawingContext ctx, double w, double cx, double h, double pad, double travel, double unity, double gap)
    {
        double uy = Math.Round(h - pad - travel * unity) - 0.5;
        ctx.DrawLine(Palette.Pen(Palette.Dim), new Point(0, uy), new Point(cx - gap, uy));
        ctx.DrawLine(Palette.Pen(Palette.Dim), new Point(cx + gap, uy), new Point(w, uy));
    }
}
