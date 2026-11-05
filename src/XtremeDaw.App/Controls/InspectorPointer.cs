using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The line from the selected track's header to the inspector, in the track's color (#180: "este track se ve acá"): it
/// leaves a dot on the header's left edge, runs along the gap between the panels and ends in a filled arrowhead inside
/// the inspector, beside the track's name row, with a dark halo so it reads over any color. A layer over the whole
/// window that takes no input; the window passes the points on its tick (<see cref="Set"/>), and it only redraws when
/// they change. Shown while both panels are docked (<see cref="TrackColorLooks.Pointer"/>).
/// </summary>
public sealed class InspectorPointer : Control
{
    /// <summary>The line's width (1.5 px, #180: asked thinner twice, from 3 px; the halo keeps it from reading as a
    /// panel's border), and the dark halo around it so it shows over any color.</summary>
    const double LineW = 1.5, HaloW = LineW + 2;
    /// <summary>The filled arrowhead: half height and length.</summary>
    const double HeadHalf = 4.5, HeadLen = 8;
    /// <summary>The dot's radius where the line leaves the track's header.</summary>
    const double DotR = 3;
    /// <summary>The halo's opacity, black.</summary>
    const double HaloA = 0.6;

    bool shown;
    double fromX, fromY, gapX, tipX, toY;
    Color color;
    /// <summary>The arrowhead, rebuilt only when the points change (Render must not allocate).</summary>
    StreamGeometry? head;

    public InspectorPointer() => IsHitTestVisible = false;

    /// <summary>Hides it.</summary>
    public void Clear()
    {
        if (!shown) return;
        shown = false;
        InvalidateVisual();
    }

    /// <summary>
    /// From (<paramref name="hx"/>, <paramref name="hy"/>) on the header's edge, across to <paramref name="gx"/>, along
    /// it to <paramref name="iy"/> and on to the arrowhead, whose tip is at <paramref name="tx"/>. All in this control's pixels.
    /// </summary>
    public void Set(double hx, double hy, double gx, double tx, double iy, Color c)
    {
        hx = Math.Round(hx); hy = Math.Round(hy); gx = Math.Round(gx); tx = Math.Round(tx); iy = Math.Round(iy);
        if (shown && hx == fromX && hy == fromY && gx == gapX && tx == tipX && iy == toY && c == color) return;
        bool moved = !shown || gx != gapX || tx != tipX || iy != toY;
        (shown, fromX, fromY, gapX, tipX, toY, color) = (true, hx, hy, gx, tx, iy, c);
        if (moved || head == null)
        {
            double dir = tx < gx ? -1 : 1, back = tx - dir * HeadLen;
            head = new StreamGeometry();
            using var g = head.Open();
            g.BeginFigure(new Point(tx, iy), true);
            g.LineTo(new Point(back, iy - HeadHalf));
            g.LineTo(new Point(back, iy + HeadHalf));
            g.EndFigure(true);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (!shown || head == null) return;
        // the line goes 1 px into the arrowhead's base, so no gap shows between them
        double dir = tipX < gapX ? -1 : 1, back = tipX - dir * (HeadLen - 1);
        var halo = Palette.Pen(Palette.Alpha(Colors.Black, HaloA), HaloW, PenLineCap.Round, PenLineJoin.Round);
        var fill = Palette.Alpha(color, 1);
        var pen = Palette.Pen(fill, LineW, PenLineCap.Round, PenLineJoin.Round);
        Point a = new(fromX, fromY), b = new(gapX, fromY), c = new(gapX, toY), d = new(back, toY);
        foreach (var p in (ReadOnlySpan<IPen>)[halo, pen])
        {
            ctx.DrawLine(p, a, b);
            ctx.DrawLine(p, b, c);
            ctx.DrawLine(p, c, d);
        }
        ctx.DrawGeometry(fill, halo, head);
        ctx.DrawGeometry(fill, null, head);
        ctx.DrawEllipse(fill, halo, a, DotR, DotR);
    }
}
