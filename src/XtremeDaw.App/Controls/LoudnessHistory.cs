using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The Mastering meter's short-term loudness over the last <see cref="Span"/> seconds, against the Destino's line
/// (asked by the user: how the loudness moves, not just where it is now). The window pushes a reading every
/// <see cref="StepSec"/> while it plays; the points live in a ring made once, and each frame only draws lines with
/// <see cref="Palette"/>'s cached pens, so it allocates nothing while it moves.
/// </summary>
public sealed class LoudnessHistory : Control
{
    /// <summary>Seconds the graph spans and how often a reading goes in.</summary>
    public const double Span = 30, StepSec = 0.1;
    /// <summary>What the graph shows from the top down (LUFS).</summary>
    const double Top = 0, Bottom = -36;
    /// <summary>The grid lines (LUFS).</summary>
    static readonly double[] Grid = [-6, -12, -18, -24, -30];

    readonly double[] ring = new double[(int)(Span / StepSec)];
    int head, count;
    double target = double.NaN;

    public LoudnessHistory()
    {
        ClipToBounds = true;
        MinHeight = 60;
    }

    /// <summary>The Destino's loudness (LUFS), drawn as a dashed line; NaN = none.</summary>
    public double Target
    {
        get => target;
        set
        {
            if (target.Equals(value)) return;
            target = value;
            InvalidateVisual();
        }
    }

    /// <summary>One more reading (LUFS; not finite = silence, a gap in the curve).</summary>
    public void Push(double lufs)
    {
        ring[head] = lufs;
        head = (head + 1) % ring.Length;
        if (count < ring.Length) count++;
        InvalidateVisual();
    }

    /// <summary>Starts over (a new pass).</summary>
    public void Clear()
    {
        if (count == 0) return;
        head = count = 0;
        InvalidateVisual();
    }

    double Y(double lufs) => (Top - Math.Clamp(lufs, Bottom, Top)) / (Top - Bottom) * Bounds.Height;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(Palette.App, new Rect(0, 0, w, h));
        var grid = Palette.Pen(Palette.Line);
        foreach (double g in Grid)
        {
            double y = Math.Round(Y(g)) + 0.5;
            ctx.DrawLine(grid, new Point(0, y), new Point(w, y));
        }
        // two labels are enough to read the scale (constant strings: nothing is built per frame)
        ctx.DrawText(Palette.Text("-12", 8, Palette.Faint, Palette.Mono), new Point(3, Math.Round(Y(-12)) - 11));
        ctx.DrawText(Palette.Text("-24", 8, Palette.Faint, Palette.Mono), new Point(3, Math.Round(Y(-24)) - 11));
        if (double.IsFinite(target))
        {
            double y = Math.Round(Y(target)) + 0.5;
            ctx.DrawLine(Palette.Dashed(Palette.Dim, 1), new Point(0, y), new Point(w, y));
        }
        // newest at the right edge, one step per StepSec
        double dx = w / (ring.Length - 1);
        var pen = Palette.Pen(Palette.Accent, 1.5, PenLineCap.Round, PenLineJoin.Round);
        bool prev = false;
        Point last = default;
        for (int k = 0; k < count; k++)
        {
            double v = ring[(head - count + k + ring.Length) % ring.Length];
            if (!double.IsFinite(v) || v < Bottom)
            {
                prev = false;
                continue;
            }
            var p = new Point(w - (count - 1 - k) * dx, Y(v));
            if (prev) ctx.DrawLine(pen, last, p);
            last = p;
            prev = true;
        }
    }
}
