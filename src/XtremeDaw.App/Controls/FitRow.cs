using Avalonia;
using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// A row of whole children that wraps: each one goes beside the previous while it fits and drops to the next line when
/// it doesn't, never cut in half or run under what is beside the row (the Biblioteca's badges, asked by the user: they
/// overlapped the "Usar" button, and hiding them wasn't enough, they have to move down as the column narrows).
/// <see cref="Rest"/>, if set, goes after them on the last line and takes what is left there (a text that trims); with
/// less than <see cref="MinRest"/> left it goes on a line of its own.
/// </summary>
public sealed class FitRow : Panel
{
    public double Spacing { get; set; } = 4;

    /// <summary>Space between lines.</summary>
    public double LineSpacing { get; set; } = 3;

    /// <summary>The child that fills the leftover width (and trims) instead of being shown whole.</summary>
    public Control? Rest { get; set; }

    /// <summary>The least width the <see cref="Rest"/> takes beside the badges before it moves to its own line.</summary>
    public double MinRest { get; set; } = 64;

    /// <summary>The <see cref="Rest"/>'s untrimmed width, from the last measure.</summary>
    double restFull;

    public FitRow() => ClipToBounds = true;

    protected override Size MeasureOverride(Size available) => Layout(available.Width, false);

    protected override Size ArrangeOverride(Size final)
    {
        Layout(final.Width, true);
        return final;
    }

    /// <summary>Places the children in lines of <paramref name="width"/>; measures them, or arranges them when
    /// <paramref name="arrange"/>. Returns the size used.</summary>
    Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, lineH = 0, used = 0;
        bool any = false;
        var line = new List<Control>();
        void Close()
        {
            if (arrange)
            {
                double cx = 0;
                foreach (var c in line)
                {
                    c.Arrange(new Rect(cx, y + (lineH - c.DesiredSize.Height) / 2, c.DesiredSize.Width, c.DesiredSize.Height));
                    cx += c.DesiredSize.Width + Spacing;
                }
            }
            line.Clear();
        }
        foreach (var c in Children)
        {
            if (c == Rest || !c.IsVisible) continue;
            if (!arrange) c.Measure(Size.Infinity);
            double w = c.DesiredSize.Width;
            if (line.Count > 0 && x + Spacing + w > width)
            {
                Close();
                y += lineH + LineSpacing;
                x = lineH = 0;
            }
            if (line.Count > 0) x += Spacing;
            line.Add(c);
            x += w;
            lineH = Math.Max(lineH, c.DesiredSize.Height);
            used = Math.Max(used, x);
            any = true;
        }
        if (Rest is { IsVisible: true } rest)
        {
            // the whole width comes from measuring unbounded; at arrange the desired size is already the trimmed one
            if (!arrange) { rest.Measure(Size.Infinity); restFull = rest.DesiredSize.Width; }
            double full = restFull, gap = any ? Spacing * 2 : 0;
            double left = width - x - gap;
            if (any && left < Math.Min(full, MinRest))
            {
                // its own line
                Close();
                y += lineH + LineSpacing;
                x = lineH = gap = 0;
                left = width;
            }
            left = Math.Max(0, left);
            if (!arrange) rest.Measure(new Size(left, double.PositiveInfinity));
            double rh = rest.DesiredSize.Height;
            if (arrange)
            {
                double top = y + (Math.Max(lineH, rh) - rh) / 2;
                lineH = Math.Max(lineH, rh);
                Close();
                rest.Arrange(new Rect(x + gap, top, left, rh));
            }
            else lineH = Math.Max(lineH, rh);
            used = Math.Max(used, x + gap + Math.Min(full, left));
        }
        else Close();
        return new Size(Math.Min(used, width), y + lineH);
    }
}
