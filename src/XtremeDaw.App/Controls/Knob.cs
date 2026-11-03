using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Port of Knob in the web app's ui.tsx (the mixer's knobs): a 270° arc lit from the reset value to the
/// current one, drag up/down (relative, Shift = fine), wheel (50 steps over the range, Shift = fine) and double-click
/// to reset.
/// The cap is a console's, like the <see cref="Fader"/>'s (asked by the user): a ridged skirt, a shaded top, a shadow
/// under it and a white pointer, around a thin scale. Its greys don't follow the theme, the same as the fader's.
/// Avalonia only hit-tests what is painted (the web's svg takes its whole box), so the knob paints a transparent
/// square <see cref="HitPad"/> px bigger than itself to catch the pointer anywhere around it, and its track lights up
/// while hovered or dragged.
/// </summary>
public sealed class Knob : Control
{
    /// <summary>Extra px around the knob that still grab it: small knobs are hard to hit with the guitar on.</summary>
    const double HitPad = 6;
    /// <summary>From this diameter (px) the skirt shows its ridges: on a smaller knob they would only blur.</summary>
    const double RidgedFrom = 28;
    /// <summary>The ridges around a skirt, and the top's share of the cap's radius.</summary>
    const int Ridges = 28;
    const double TopShare = 0.78;

    static LinearGradientBrush Shade(string light, string dark) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(light), 0), new GradientStop(Color.Parse(dark), 1) },
    };
    static readonly IBrush SkirtFill = Shade("#6e6e77", "#1d1d21").ToImmutable(), TopFill = Shade("#55555d", "#2e2e34").ToImmutable();
    /// <summary>The unlit scale: opaque, since its segments overlap and a transparent color would show them as beads.</summary>
    static readonly IBrush ScaleOff = new ImmutableSolidColorBrush(Color.Parse("#3f3f46"));
    static readonly IBrush CapShadow = new ImmutableSolidColorBrush(Color.FromArgb(130, 0, 0, 0));
    static readonly IBrush LevelBand = new ImmutableSolidColorBrush(Color.FromArgb(150, 8, 8, 10));
    static readonly IPen CapEdge = new ImmutablePen(Color.Parse("#08080a").ToUInt32(), 1);
    static readonly IPen TopEdge = new ImmutablePen(Color.FromArgb(70, 255, 255, 255).ToUInt32(), 1);
    static readonly IPen RidgePen = new ImmutablePen(Color.FromArgb(150, 8, 8, 10).ToUInt32(), 1);

    double value, dragValue, dragY;
    IPointer? pointer;

    public double Min { get; init; } = -1;
    public double Max { get; init; } = 1;
    public double ResetTo { get; init; }
    public double Size { get; private set; }
    public event Action<double>? Changed;

    public Knob(double size = 22, string? tip = null)
    {
        Size = size;
        Width = Height = size;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        Tip = tip;
    }

    /// <summary>Follows the room it has (the effect panels scale with their width).</summary>
    public void Resize(double size)
    {
        if (size == Size) return;
        Size = size;
        Width = Height = size;
        InvalidateVisual();
    }

    public string? Tip
    {
        set { ToolTip.SetTip(this, value); AutomationProperties.SetName(this, value); }
    }

    public double Value
    {
        get => value;
        set { if (this.value == value) return; this.value = value; InvalidateVisual(); }
    }

    bool Big => Size >= 32;
    double Round(double v) => Math.Round(Math.Clamp(v, Min, Max) * 1000) / 1000;

    public override void Render(DrawingContext ctx)
    {
        double angle = -135 + (value - Min) / (Max - Min) * 270;
        double origin = -135 + (ResetTo - Min) / (Max - Min) * 270;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size).Inflate(HitPad));
        Draw(ctx, new Point(Size / 2, Size / 2), Size, angle, origin, true, IsPointerOver || pointer != null);
    }

    /// <summary>
    /// Draws a knob of diameter <paramref name="d"/> (every knob in the window, this control's and the timeline's):
    /// the scale lit from <paramref name="origin"/> to <paramref name="angle"/> (degrees, 0 = up), the cap with its
    /// shadow, and a pointer. <paramref name="label"/> keeps the top free for a value drawn over it. Allocates
    /// nothing: the timeline redraws it on every pointer move.
    /// </summary>
    public static void Draw(DrawingContext ctx, Point c, double d, double angle, double origin, bool on, bool hot, bool label = false)
    {
        double scale = Math.Max(2, d / 18), rim = d / 2 - scale / 2, r = rim - scale / 2 - Math.Max(1.5, d / 24);
        bool ridged = d >= RidgedFrom;
        double top = r * (ridged ? TopShare : 0.88);

        Arc(ctx, c, rim, -135, 135, Palette.Pen(hot ? Palette.Faint : ScaleOff, scale, PenLineCap.Round));
        if (Math.Abs(angle - origin) > 0.5)
            Arc(ctx, c, rim, Math.Min(origin, angle), Math.Max(origin, angle), Palette.Pen(on ? Palette.Accent : Palette.Dim, scale, PenLineCap.Round));

        ctx.DrawEllipse(CapShadow, null, new Point(c.X, c.Y + Math.Max(1, d / 28)), r + 0.5, r + 0.5);
        ctx.DrawEllipse(SkirtFill, CapEdge, c, r, r);
        if (ridged)
            for (int k = 0; k < Ridges; k++)
            {
                double a = (angle + k * 360.0 / Ridges - 90) * Math.PI / 180, x = Math.Cos(a), y = Math.Sin(a);
                ctx.DrawLine(RidgePen, new Point(c.X + x * (top + 1), c.Y + y * (top + 1)), new Point(c.X + x * (r - 0.5), c.Y + y * (r - 0.5)));
            }
        ctx.DrawEllipse(TopFill, TopEdge, c, top, top);

        // like the fader's level line, white on a dark band; it turns with the cap, so it runs across the skirt
        double sin = Math.Sin((angle - 90) * Math.PI / 180), cos = Math.Cos((angle - 90) * Math.PI / 180), from = label ? top * 0.82 : top * 0.42;
        var a0 = new Point(c.X + cos * from, c.Y + sin * from);
        var a1 = new Point(c.X + cos * (r - 0.5), c.Y + sin * (r - 0.5));
        double w = Math.Max(1.5, d / 22);
        ctx.DrawLine(Palette.Pen(LevelBand, w + 2), a0, a1);
        ctx.DrawLine(Palette.Pen(on ? Brushes.White : Palette.Dim, w, PenLineCap.Round), a0, a1);
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { Changed?.Invoke(ResetTo); return; }
        Focus();
        pointer = e.Pointer;
        pointer.Capture(this);
        dragY = e.GetPosition(this).Y;
        dragValue = value;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (pointer == null) return;
        double y = e.GetPosition(this).Y;
        // relative steps, so pressing Shift mid-drag doesn't jump
        double next = dragValue + (dragY - y) / (Big ? 160 : 120) * (Max - Min) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1);
        dragY = y;
        dragValue = Math.Clamp(next, Min, Max);
        Changed?.Invoke(Round(next));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; e.Handled = true; InvalidateVisual(); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { pointer = null; InvalidateVisual(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPointerOverProperty) InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        double step = (Max - Min) / 50 * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1) * (e.Delta.Y > 0 ? 1 : -1);
        Changed?.Invoke(Round(value + step));
        e.Handled = true;
    }
}
