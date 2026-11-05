using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The looks a knob can take (asked by the user: ten to pick from in Configuración → Apariencia), the same for every
/// knob in the window, this control's and the timeline's. Kept in config.json ("knobLook"); an unknown id falls back
/// to the first. Each look only paints: geometry, dragging and the value stay <see cref="Knob"/>'s. Their greys don't
/// follow the theme, the same as the fader's; the lit arc and the glows take the accent. Nothing here allocates on a
/// redraw: the brushes are built once, and what follows the accent comes from <see cref="Palette"/>'s caches.
/// </summary>
public static class KnobLooks
{
    public const string Soft = "soft", Console = "console", Line = "line", Metal = "metal", Glass = "glass",
        Ring = "ring", Dots = "dots", Bakelite = "bakelite", Halo = "halo", Ceramic = "ceramic";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] All = [Soft, Console, Line, Metal, Glass, Ring, Dots, Bakelite, Halo, Ceramic];

    const string ConfigKey = "knobLook";

    /// <summary>The look the app comes with (Apariencia's "Restablecer").</summary>
    public const string Default = Soft;

    public static string Current { get; private set; } = Default;

    /// <summary>The cap is light (Aluminio, Cerámica): a value written over it goes dark.</summary>
    public static bool LightCap => IsLight(Current);
    public static bool IsLight(string look) => look is Metal or Ceramic;

    /// <summary>
    /// The color the next draw lights its arc, dots and pointer with instead of the accent (an effect's family color,
    /// #187); <see cref="Knob"/> sets it around its own draw. UI thread only, like every draw.
    /// </summary>
    public static Color? Tint { get; set; }

    static Color LitColor => Tint ?? Palette.AccentColor;
    static IBrush Lit => Tint is { } t ? Palette.Alpha(t, 1) : Palette.Accent;

    /// <summary>The look changed: whoever paints knobs redraws.</summary>
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

    // --- brushes --------------------------------------------------------------------------------

    static Color C(string hex) => Color.Parse(hex);
    static Color A(byte a, Color c) => Color.FromArgb(a, c.R, c.G, c.B);
    static readonly Color White = Colors.White, Black = Colors.Black;

    static IImmutableBrush Solid(string hex) => new ImmutableSolidColorBrush(C(hex));

    static IImmutableBrush Linear(double x0, double y0, double x1, double y1, params (Color C, double At)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new RelativePoint(x0, y0, RelativeUnit.Relative), EndPoint = new RelativePoint(x1, y1, RelativeUnit.Relative) };
        foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop(c, at));
        return b.ToImmutable();
    }

    /// <summary>Lit from the top left (the neumorphic and ceramic surfaces).</summary>
    static IImmutableBrush Diagonal(Color from, Color to) => Linear(0.15, 0.15, 0.85, 0.85, (from, 0), (to, 1));
    static IImmutableBrush Vertical(params (Color C, double At)[] stops) => Linear(0, 0, 0, 1, stops);

    static IImmutableBrush Radial(double ox, double oy, params (Color C, double At)[] stops)
    {
        var b = new RadialGradientBrush { GradientOrigin = new RelativePoint(ox, oy, RelativeUnit.Relative), Center = new RelativePoint(ox, oy, RelativeUnit.Relative), RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative) };
        foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop(c, at));
        return b.ToImmutable();
    }

    /// <summary>How far a soft shadow or halo reaches past the cap (its radius × this).</summary>
    const double HaloReach = 1.3;

    /// <summary>A disc <see cref="HaloReach"/> times the cap: solid under the cap, fading outside it.</summary>
    static IImmutableBrush HaloOf(Color c) => new RadialGradientBrush
    {
        GradientStops = { new GradientStop(c, 0), new GradientStop(c, 1 / HaloReach), new GradientStop(A(0, c), 1) },
    }.ToImmutable();

    static IPen P(IImmutableBrush b, double w = 1) => new ImmutablePen(b, w);

    /// <summary>The sunk scale: opaque, since its segments overlap and a transparent color would show them as beads.</summary>
    static readonly IBrush Groove = Solid("#08080a"), GrooveHot = Solid("#2a2a30");
    static readonly IBrush LightHalo = HaloOf(A(20, White)), DarkHalo = HaloOf(A(170, Black)), DropHalo = HaloOf(A(120, Black));

    // Neumórfico
    static readonly IBrush SoftCap = Diagonal(C("#36363d"), C("#17171b")), SoftDish = Diagonal(C("#19191d"), C("#2d2d34"));
    static readonly IPen SoftCapEdge = P(Diagonal(A(46, White), A(150, Black))), SoftDishEdge = P(Diagonal(A(120, Black), A(26, White)));

    // Consola
    const int Ridges = 28;
    static readonly IBrush ConsoleSkirt = Vertical((C("#6e6e77"), 0), (C("#1d1d21"), 1)), ConsoleTop = Vertical((C("#55555d"), 0), (C("#2e2e34"), 1));
    static readonly IBrush ConsoleScale = Solid("#3f3f46"), CapShadow = new ImmutableSolidColorBrush(A(130, Black)), LevelBand = new ImmutableSolidColorBrush(A(150, C("#08080a")));
    static readonly IPen ConsoleEdge = P(Solid("#08080a")), ConsoleTopEdge = P(new ImmutableSolidColorBrush(A(70, White))), RidgePen = P(new ImmutableSolidColorBrush(A(150, C("#08080a"))));

    // Línea
    static readonly IBrush HairTrack = Solid("#2e2e34"), HairTrackHot = Solid("#45454d");
    static readonly IPen LineRing = P(Solid("#3a3a41")), LineRingHot = P(Solid("#55555d"));

    // Aluminio
    static readonly IBrush MetalBevel = Vertical((C("#f4f4f6"), 0), (C("#8d8d95"), 0.55), (C("#4a4a50"), 1));
    static readonly IBrush MetalFace = Vertical((C("#c2c2c8"), 0), (C("#e4e4e8"), 0.45), (C("#a6a6ad"), 1));
    static readonly IPen MetalEdge = P(Solid("#0c0c0e")), MetalFaceEdge = P(Vertical((A(90, Black), 0), (A(150, White), 1)));
    static readonly IBrush MetalMark = Solid("#1c1c20"), MetalMarkOff = Solid("#77777f");

    // Cristal
    static readonly IBrush GlassCap = Radial(0.5, 0.25, (C("#3c414c"), 0), (C("#151820"), 0.7), (C("#0b0c10"), 1));
    static readonly IBrush GlassShine = Vertical((A(70, White), 0), (A(0, White), 1));
    static readonly IPen GlassEdge = P(Vertical((A(60, White), 0), (A(18, White), 0.5), (A(160, Black), 1)));

    // Anillo
    static readonly IBrush RingTrack = Solid("#202025"), RingTrackHot = Solid("#2c2c32"), RingCap = Solid("#1a1a1e");
    static readonly IPen RingEdge = P(Solid("#2c2c32"));

    // Puntos
    static readonly IBrush DotOff = Solid("#3a3a41"), DotOffHot = Solid("#55555d");
    static readonly IBrush MatteCap = Vertical((C("#2a2a30"), 0), (C("#16161a"), 1));
    static readonly IPen MatteEdge = P(Vertical((A(40, White), 0), (A(200, Black), 1)));

    // Bakelita
    static readonly IBrush BakeliteCap = Radial(0.38, 0.28, (C("#45454b"), 0), (C("#121214"), 0.55), (C("#040405"), 1));
    static readonly IBrush Gold = Linear(0.2, 0, 0.8, 1, (C("#f7e2a1"), 0), (C("#c9a24a"), 0.5), (C("#7b5c22"), 1));
    /// <summary>The rim's pens, thin and from <see cref="WideRimFrom"/> px thick: a gradient pen isn't cached by <see cref="Palette"/>.</summary>
    static readonly IPen GoldRim = P((IImmutableBrush)Gold, 1), GoldRimWide = P((IImmutableBrush)Gold, 1.6);
    const double WideRimFrom = 40;
    static readonly IBrush GoldMark = Solid("#e7c66b"), GoldMarkOff = Solid("#6f6145");

    // Halo
    static readonly IBrush HaloTrack = Solid("#222227"), HaloTrackHot = Solid("#34343a"), HaloCap = Solid("#131316");
    static readonly IPen HaloEdge = P(Solid("#26262b"));

    // Cerámica
    static readonly IBrush CeramicCap = Diagonal(C("#f6f6f8"), C("#b4b4bb"));
    static readonly IPen CeramicEdge = P(Diagonal(A(200, White), A(90, Black)));
    static readonly IBrush CeramicDish = Diagonal(C("#c9c9cf"), C("#f0f0f3"));
    static readonly IPen CeramicDishEdge = P(Diagonal(A(60, Black), A(160, White)));
    static readonly IBrush CeramicMark = Solid("#26262b"), CeramicMarkOff = Solid("#8a8a92");

    // --- drawing --------------------------------------------------------------------------------

    /// <summary>
    /// Draws a knob of diameter <paramref name="d"/> in <paramref name="look"/>: the scale lit from
    /// <paramref name="origin"/> to <paramref name="angle"/> (degrees, 0 = up), the cap and its pointer.
    /// <paramref name="label"/> keeps the middle free for a value drawn over it.
    /// </summary>
    public static void Draw(string look, DrawingContext ctx, Point c, double d, double angle, double origin, bool on, bool hot, bool label)
    {
        double sw = Math.Max(2, d / 18), rim = d / 2 - sw / 2;
        switch (look)
        {
            case Console: DrawConsole(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
            case Line: DrawLine(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
            case Metal: DrawMetal(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
            case Glass: DrawGlass(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
            case Ring: DrawRing(ctx, c, d, angle, origin, on, hot, label); break;
            case Dots: DrawDots(ctx, c, d, sw, angle, origin, on, hot, label); break;
            case Bakelite: DrawBakelite(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
            case Halo: DrawHalo(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
            case Ceramic: DrawCeramic(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
            default: DrawSoft(ctx, c, d, sw, rim, angle, origin, on, hot, label); break;
        }
    }

    /// <summary>A disc lit from the top left, a light halo on that side and a dark one on the other, a concave dish
    /// and a glowing dot.</summary>
    static void DrawSoft(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(2, d / 16), dish = r * 0.78, o = Math.Max(0.75, d / 26);
        SunkScale(ctx, c, sw, rim, angle, origin, on, hot);
        double halo = r * HaloReach;
        ctx.DrawEllipse(LightHalo, null, new Point(c.X - o, c.Y - o), halo, halo);
        ctx.DrawEllipse(DarkHalo, null, new Point(c.X + o, c.Y + o), halo, halo);
        ctx.DrawEllipse(SoftCap, SoftCapEdge, c, r - 0.5, r - 0.5);
        ctx.DrawEllipse(SoftDish, SoftDishEdge, c, dish, dish);
        GlowDot(ctx, Polar(c, angle, label ? (dish + r) / 2 : dish * 0.66), Math.Max(1.25, d / 22), on);
    }

    /// <summary>A console's cap, like the fader's: a ridged skirt, a shaded top, a shadow under it and a white line
    /// on a dark band.</summary>
    static void DrawConsole(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(1.5, d / 24);
        bool ridged = d >= 28;
        double top = r * (ridged ? 0.78 : 0.88);
        Arc(ctx, c, rim, -135, 135, Palette.Pen(hot ? Palette.Faint : ConsoleScale, sw, PenLineCap.Round));
        LitArc(ctx, c, rim, angle, origin, on, sw, false);
        ctx.DrawEllipse(CapShadow, null, new Point(c.X, c.Y + Math.Max(1, d / 28)), r + 0.5, r + 0.5);
        ctx.DrawEllipse(ConsoleSkirt, ConsoleEdge, c, r, r);
        if (ridged)
            for (int k = 0; k < Ridges; k++)
            {
                double a = angle + k * 360.0 / Ridges;
                ctx.DrawLine(RidgePen, Polar(c, a, top + 1), Polar(c, a, r - 0.5));
            }
        ctx.DrawEllipse(ConsoleTop, ConsoleTopEdge, c, top, top);
        double w = Math.Max(1.5, d / 22);
        var a0 = Polar(c, angle, label ? top * 0.82 : top * 0.42);
        var a1 = Polar(c, angle, r - 0.5);
        ctx.DrawLine(Palette.Pen(LevelBand, w + 2), a0, a1);
        ctx.DrawLine(Palette.Pen(on ? Brushes.White : Palette.Dim, w, PenLineCap.Round), a0, a1);
    }

    /// <summary>Only lines: a hairline scale with the lit arc over it, an outlined cap and a pointer from the middle.</summary>
    static void DrawLine(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(2, d / 14);
        Arc(ctx, c, rim, -135, 135, Palette.Pen(hot ? HairTrackHot : HairTrack, Math.Max(1, sw / 2), PenLineCap.Round));
        LitArc(ctx, c, rim, angle, origin, on, sw, false);
        ctx.DrawEllipse(null, hot ? LineRingHot : LineRing, c, r, r);
        ctx.DrawLine(Palette.Pen(on ? Palette.Fg : Palette.Dim, Math.Max(1.25, d / 26), PenLineCap.Round), Polar(c, angle, label ? r * 0.66 : r * 0.2), Polar(c, angle, r - 1));
    }

    /// <summary>Machined aluminium: a bevelled rim, a satin face and a dark engraved line.</summary>
    static void DrawMetal(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(2, d / 16), face = r - Math.Max(1.5, d / 20);
        SunkScale(ctx, c, sw, rim, angle, origin, on, hot);
        double drop = r * HaloReach;
        ctx.DrawEllipse(DropHalo, null, new Point(c.X, c.Y + Math.Max(1, d / 22)), drop, drop);
        ctx.DrawEllipse(MetalBevel, MetalEdge, c, r, r);
        ctx.DrawEllipse(MetalFace, MetalFaceEdge, c, face, face);
        ctx.DrawLine(Palette.Pen(on ? MetalMark : MetalMarkOff, Math.Max(1.5, d / 22), PenLineCap.Round), Polar(c, angle, label ? face * 0.7 : face * 0.4), Polar(c, angle, face - 1));
    }

    /// <summary>Smoked glass: a dark dome lit from above, a soft reflection on top and a glowing accent line.</summary>
    static void DrawGlass(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(2, d / 16);
        SunkScale(ctx, c, sw, rim, angle, origin, on, hot);
        double drop = r * HaloReach;
        ctx.DrawEllipse(DropHalo, null, new Point(c.X, c.Y + Math.Max(1, d / 26)), drop, drop);
        ctx.DrawEllipse(GlassCap, GlassEdge, c, r, r);
        ctx.DrawEllipse(GlassShine, null, new Point(c.X, c.Y - r * 0.42), r * 0.66, r * 0.42);
        double w = Math.Max(1.25, d / 26);
        var a0 = Polar(c, angle, label ? r * 0.66 : r * 0.3);
        var a1 = Polar(c, angle, r * 0.84);
        if (on) ctx.DrawLine(Palette.Pen(Palette.Alpha(LitColor, 0.3), w * 3, PenLineCap.Round), a0, a1);
        ctx.DrawLine(Palette.Pen(on ? Lit : Palette.Dim, w, PenLineCap.Round), a0, a1);
    }

    /// <summary>A wide ring that fills with the value around a small flat cap, like a synth's.</summary>
    static void DrawRing(DrawingContext ctx, Point c, double d, double angle, double origin, bool on, bool hot, bool label)
    {
        double w = Math.Max(3, d / 7), rim = d / 2 - w / 2, r = rim - w / 2 - Math.Max(1.5, d / 16);
        Arc(ctx, c, rim, -135, 135, Palette.Pen(hot ? RingTrackHot : RingTrack, w, PenLineCap.Round));
        LitArc(ctx, c, rim, angle, origin, on, w, false);
        ctx.DrawEllipse(RingCap, RingEdge, c, r, r);
        if (!label)
            ctx.DrawLine(Palette.Pen(on ? Palette.Fg : Palette.Dim, Math.Max(1.25, d / 26), PenLineCap.Round), Polar(c, angle, r * 0.35), Polar(c, angle, r - 1.5));
    }

    /// <summary>A scale of dots that light up to the value around a matte cap with a fine notch.</summary>
    static void DrawDots(DrawingContext ctx, Point c, double d, double sw, double angle, double origin, bool on, bool hot, bool label)
    {
        const int n = 11;
        double dot = Math.Max(1, sw * 0.55), rim = d / 2 - dot, r = rim - dot - Math.Max(2, d / 12);
        double lo = Math.Min(origin, angle) - 0.5, hi = Math.Max(origin, angle) + 0.5;
        bool any = Math.Abs(angle - origin) > 0.5;
        for (int k = 0; k < n; k++)
        {
            double a = -135 + k * 270.0 / (n - 1);
            bool lit = any && a >= lo && a <= hi;
            ctx.DrawEllipse(lit ? (on ? Lit : Palette.Dim) : hot ? DotOffHot : DotOff, null, Polar(c, a, rim), dot, dot);
        }
        double drop = r * HaloReach;
        ctx.DrawEllipse(DropHalo, null, new Point(c.X, c.Y + Math.Max(1, d / 26)), drop, drop);
        ctx.DrawEllipse(MatteCap, MatteEdge, c, r, r);
        ctx.DrawLine(Palette.Pen(on ? Lit : Palette.Dim, Math.Max(1.25, d / 24), PenLineCap.Round), Polar(c, angle, label ? r * 0.7 : r * 0.45), Polar(c, angle, r - 1.5));
    }

    /// <summary>Vintage hi-fi: glossy black bakelite with a gold rim and a gold line.</summary>
    static void DrawBakelite(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(2, d / 16);
        SunkScale(ctx, c, sw, rim, angle, origin, on, hot);
        double drop = r * HaloReach;
        ctx.DrawEllipse(DropHalo, null, new Point(c.X, c.Y + Math.Max(1, d / 24)), drop, drop);
        ctx.DrawEllipse(BakeliteCap, d >= WideRimFrom ? GoldRimWide : GoldRim, c, r, r);
        ctx.DrawLine(Palette.Pen(on ? GoldMark : GoldMarkOff, Math.Max(1.25, d / 26), PenLineCap.Round), Polar(c, angle, label ? r * 0.68 : r * 0.3), Polar(c, angle, r * 0.86));
    }

    /// <summary>A plain cap inside a ring of light: the lit arc glows in three layers.</summary>
    static void DrawHalo(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(2.5, d / 12);
        Arc(ctx, c, rim, -135, 135, Palette.Pen(hot ? HaloTrackHot : HaloTrack, Math.Max(1, sw / 2), PenLineCap.Round));
        LitArc(ctx, c, rim, angle, origin, on, sw, true);
        ctx.DrawEllipse(HaloCap, HaloEdge, c, r, r);
        GlowDot(ctx, Polar(c, angle, label ? r * 0.8 : r * 0.62), Math.Max(1.25, d / 24), on);
    }

    /// <summary>White matte ceramic on a soft shadow, with a shallow dish and a dark line.</summary>
    static void DrawCeramic(DrawingContext ctx, Point c, double d, double sw, double rim, double angle, double origin, bool on, bool hot, bool label)
    {
        double r = rim - sw / 2 - Math.Max(2, d / 16), dish = r * 0.74, o = Math.Max(0.75, d / 24);
        SunkScale(ctx, c, sw, rim, angle, origin, on, hot);
        double halo = r * HaloReach;
        ctx.DrawEllipse(DarkHalo, null, new Point(c.X + o * 0.5, c.Y + o), halo, halo);
        ctx.DrawEllipse(CeramicCap, CeramicEdge, c, r - 0.5, r - 0.5);
        ctx.DrawEllipse(CeramicDish, CeramicDishEdge, c, dish, dish);
        ctx.DrawLine(Palette.Pen(on ? CeramicMark : CeramicMarkOff, Math.Max(1.5, d / 24), PenLineCap.Round), Polar(c, angle, label ? dish * 0.9 : dish * 0.35), Polar(c, angle, r - 1.5));
    }

    // --- pieces ---------------------------------------------------------------------------------

    /// <summary>The scale sunk into the panel, with the lit arc glowing in it.</summary>
    static void SunkScale(DrawingContext ctx, Point c, double sw, double rim, double angle, double origin, bool on, bool hot)
    {
        Arc(ctx, c, rim, -135, 135, Palette.Pen(hot ? GrooveHot : Groove, sw + 1, PenLineCap.Round));
        if (Math.Abs(angle - origin) <= 0.5) return;
        double a = Math.Min(origin, angle), b = Math.Max(origin, angle);
        if (on) Arc(ctx, c, rim, a, b, Palette.Pen(Palette.Alpha(LitColor, 0.18), sw * 2.6, PenLineCap.Round));
        Arc(ctx, c, rim, a, b, Palette.Pen(on ? Lit : Palette.Dim, sw, PenLineCap.Round));
    }

    /// <summary>The arc from the origin to the value, in the accent (grey when off); <paramref name="glow"/> lays two
    /// wider, fainter strokes under it.</summary>
    static void LitArc(DrawingContext ctx, Point c, double rim, double angle, double origin, bool on, double w, bool glow)
    {
        if (Math.Abs(angle - origin) <= 0.5) return;
        double a = Math.Min(origin, angle), b = Math.Max(origin, angle);
        if (glow && on)
        {
            Arc(ctx, c, rim, a, b, Palette.Pen(Palette.Alpha(LitColor, 0.1), w * 3.6, PenLineCap.Round));
            Arc(ctx, c, rim, a, b, Palette.Pen(Palette.Alpha(LitColor, 0.22), w * 2.2, PenLineCap.Round));
        }
        Arc(ctx, c, rim, a, b, Palette.Pen(on ? Lit : Palette.Dim, w, PenLineCap.Round));
    }

    static void GlowDot(DrawingContext ctx, Point p, double r, bool on)
    {
        if (on) ctx.DrawEllipse(Palette.Alpha(LitColor, 0.3), null, p, r * 2, r * 2);
        ctx.DrawEllipse(on ? Lit : Palette.Dim, null, p, r, r);
    }

    /// <summary>An arc of short round-capped lines: a geometry would be garbage on every redraw.</summary>
    static void Arc(DrawingContext ctx, Point c, double rad, double from, double to, IPen pen)
    {
        int n = Math.Max(1, (int)Math.Ceiling((to - from) / 6));
        for (int s = 0; s < n; s++) ctx.DrawLine(pen, Polar(c, from + (to - from) * s / n, rad), Polar(c, from + (to - from) * (s + 1) / n, rad));
    }

    static Point Polar(Point c, double deg, double rad)
    {
        double a = (deg - 90) * Math.PI / 180;
        return new Point(c.X + rad * Math.Cos(a), c.Y + rad * Math.Sin(a));
    }
}
