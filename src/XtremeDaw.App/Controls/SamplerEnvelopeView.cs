using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// An envelope you can see and grab: the attack, the decay down to the sustain and the release, in the same scale as
/// their knobs (a time's root, so a few milliseconds still have room next to several seconds). Its three points are
/// dragged: the first sets the attack, the second the decay and the sustain, the third the release. It only raises
/// <see cref="Moved"/>.
/// </summary>
public sealed class SamplerEnvelopeView : Control
{
    /// <summary>Each of the three times takes up to this share of the width; the rest is the held part.</summary>
    const double Share = 0.3;
    const double Pad = 6, Dot = 3.5, Grab = 12;
    const double Radius = 6;

    readonly double maxSec;
    double attack, decay, sustain, release;
    int dragging = -1, hot = -1;
    Geometry? shape, line;

    public SamplerEnvelopeView(double maxSec)
    {
        this.maxSec = maxSec;
        Height = 72;
        ClipToBounds = true;
    }

    public event Action<double, double, double, double>? Moved;

    public void Set(double a, double d, double s, double r)
    {
        if (a == attack && d == decay && s == sustain && r == release) return;
        (attack, decay, sustain, release) = (a, d, s, r);
        shape = line = null;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        shape = line = null;
    }

    /// <summary>A time as the knobs show it, 0..1.</summary>
    double Pos(double sec) => Math.Pow(sec / maxSec, 0.25);
    double Sec(double pos) => Math.Pow(Math.Clamp(pos, 0, 1), 4) * maxSec;

    double Across => Math.Max(1, Bounds.Width - 2 * Pad);
    double Up => Math.Max(1, Bounds.Height - 2 * Pad);

    Point P1 => new(Pad + Pos(attack) * Share * Across, Pad);
    Point P2 => new(P1.X + Pos(decay) * Share * Across, Pad + (1 - sustain) * Up);
    Point P3 => new(Bounds.Width - Pad - Pos(release) * Share * Across, Pad + (1 - sustain) * Up);
    Point Foot => new(Bounds.Width - Pad, Pad + Up);
    Point Toe => new(Pad, Pad + Up);

    void Build()
    {
        var a = new StreamGeometry();
        using (var c = a.Open())
        {
            c.BeginFigure(Toe, false);
            Curve(c);
            c.EndFigure(false);
        }
        var f = new StreamGeometry();
        using (var c = f.Open())
        {
            c.BeginFigure(Toe, true);
            Curve(c);
            c.EndFigure(true);
        }
        line = a;
        shape = f;
    }

    /// <summary>The decay and the release bow as they sound: fast first, slow at the end.</summary>
    void Curve(StreamGeometryContext c)
    {
        var (p1, p2, p3, foot) = (P1, P2, P3, Foot);
        c.LineTo(p1);
        c.QuadraticBezierTo(new Point(p1.X, p2.Y), p2);
        c.LineTo(p3);
        c.QuadraticBezierTo(new Point(p3.X, foot.Y), foot);
    }

    public override void Render(DrawingContext ctx)
    {
        var box = new Rect(Bounds.Size);
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), box.Deflate(0.5), Radius, Radius);
        if (shape == null || line == null) Build();
        bool on = IsEffectivelyEnabled;
        ctx.DrawGeometry(on ? Palette.AccentSoft : Palette.Raised, null, shape!);
        ctx.DrawGeometry(null, Palette.Pen(on ? Palette.Accent : Palette.Faint, 1.5, PenLineCap.Round, PenLineJoin.Round), line!);
        if (!on) return;
        DrawDot(ctx, P1, 0);
        DrawDot(ctx, P2, 1);
        DrawDot(ctx, P3, 2);
    }

    void DrawDot(DrawingContext ctx, Point p, int i) =>
        ctx.DrawEllipse(i == hot || i == dragging ? Palette.Accent : Palette.Fg, null, p, Dot, Dot);

    int NearestPoint(Point p)
    {
        Span<Point> all = [P1, P2, P3];
        int best = -1;
        double dist = Grab;
        for (int i = 0; i < 3; i++)
        {
            double d = Math.Abs(all[i].X - p.X) + Math.Abs(all[i].Y - p.Y) / 2;
            if (d < dist) (dist, best) = (d, i);
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        dragging = NearestPoint(e.GetPosition(this));
        if (dragging < 0) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (dragging < 0)
        {
            int near = NearestPoint(at);
            if (near == hot) return;
            hot = near;
            Cursor = new Cursor(near < 0 ? StandardCursorType.Arrow : StandardCursorType.Hand);
            InvalidateVisual();
            return;
        }
        double room = Share * Across;
        var (a, d, s, r) = (attack, decay, sustain, release);
        switch (dragging)
        {
            case 0: a = Sec((at.X - Pad) / room); break;
            case 1:
                d = Sec((at.X - P1.X) / room);
                s = Math.Clamp(1 - (at.Y - Pad) / Up, 0, 1);
                break;
            default: r = Sec((Bounds.Width - Pad - at.X) / room); break;
        }
        Moved?.Invoke(a, d, s, r);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging < 0) return;
        dragging = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = -1;
    }
}
