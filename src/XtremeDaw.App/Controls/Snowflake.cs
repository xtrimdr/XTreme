using Avalonia;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>The frozen track's mark (#240): a six-armed snowflake drawn with lines. Segoe's "Frigid" glyph reads as a
/// thermometer (the user said so), and no icon font has a plain snowflake, so it is drawn by hand: straight onto a
/// <see cref="DrawingContext"/> (no allocation, fit for every frame) or as a geometry for a <c>Path</c> in the mixer.</summary>
public static class Snowflake
{
    /// <summary>Where along an arm its two branches start, and how long they are, as fractions of the arm.</summary>
    const double BranchAt = 0.55, BranchLen = 0.38;

    /// <summary>Stroke width for a snowflake of a given size: thin enough that the branches stay apart.</summary>
    public static double Stroke(double size) => Math.Max(1, size / 10);

    /// <summary>Draws a snowflake of <paramref name="size"/> (its full width) centered on <paramref name="c"/>.</summary>
    public static void Draw(DrawingContext ctx, Point c, double size, IBrush brush)
    {
        var pen = Palette.Pen(brush, Stroke(size), PenLineCap.Round, PenLineJoin.Round);
        double r = size / 2;
        for (int k = 0; k < 6; k++)
        {
            var (a, tip, b1, b2) = Arm(c, r, k);
            ctx.DrawLine(pen, c, tip);
            ctx.DrawLine(pen, a, b1);
            ctx.DrawLine(pen, a, b2);
        }
    }

    /// <summary>The same snowflake as a geometry (built once per control), for a <c>Path</c> stroked by the caller.</summary>
    public static Geometry Geometry(double size)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        double r = size / 2;
        var c = new Point(r, r);
        for (int k = 0; k < 6; k++)
        {
            var (a, tip, b1, b2) = Arm(c, r, k);
            s.BeginFigure(c, false); s.LineTo(tip); s.EndFigure(false);
            s.BeginFigure(b1, false); s.LineTo(a); s.LineTo(b2); s.EndFigure(false);
        }
        return g;
    }

    /// <summary>One arm k (0..5, the first one up): where its branches leave it, its tip and the branches' ends.</summary>
    static (Point at, Point tip, Point b1, Point b2) Arm(Point c, double r, int k)
    {
        double ang = -Math.PI / 2 + k * Math.PI / 3;
        double dx = Math.Cos(ang), dy = Math.Sin(ang);
        var at = new Point(c.X + dx * r * BranchAt, c.Y + dy * r * BranchAt);
        var tip = new Point(c.X + dx * r, c.Y + dy * r);
        // the branches point outward, 45° to each side of the arm
        double len = r * BranchLen, s = Math.Sqrt(0.5);
        var b1 = new Point(at.X + (dx * s - dy * s) * len, at.Y + (dy * s + dx * s) * len);
        var b2 = new Point(at.X + (dx * s + dy * s) * len, at.Y + (dy * s - dx * s) * len);
        return (at, tip, b1, b2);
    }
}
