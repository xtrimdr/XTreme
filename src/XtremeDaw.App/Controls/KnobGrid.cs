using Avalonia;
using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// An effect panel's knobs in equal cells: as many columns as fit the width it is measured with, each knob growing with
/// its cell, so the same panel works in a narrow window or a wide inspector. Columns and knob size are chosen inside the
/// measure, from the width given: deciding them after the layout (on SizeChanged) laid every resize out twice, a frame
/// with the old columns and another with the new, and fought a scroll bar that came and went with the height, so a
/// window being resized flickered and could settle on the wrong layout.
/// </summary>
public sealed class KnobGrid(IReadOnlyList<Knob> knobs, double cellW, double cellMinW, double bigCellW, double knobW, double bigKnobW) : Panel
{
    /// <summary>The columns without a width to go by (measured with all the room it wants).</summary>
    const int FreeColumns = 3;

    int columns = FreeColumns;

    int Visible()
    {
        int n = 0;
        foreach (var c in Children) if (c.IsVisible) n++;
        return n;
    }

    protected override Size MeasureOverride(Size available)
    {
        int visible = Visible();
        double w = available.Width;
        if (double.IsInfinity(w)) columns = Math.Max(1, Math.Min(FreeColumns, visible));
        else
        {
            int cols = Math.Clamp((int)Math.Ceiling(w / cellW), 1, Math.Max(1, (int)(w / cellMinW)));
            columns = Math.Max(1, Math.Min(cols, visible));
        }
        double size = double.IsInfinity(w) || w / columns >= bigCellW ? bigKnobW : knobW;
        foreach (var k in knobs) k.Resize(size);
        double cell = double.IsInfinity(w) ? double.PositiveInfinity : w / columns;
        double cw = 0, ch = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(new Size(cell, available.Height));
            cw = Math.Max(cw, c.DesiredSize.Width);
            ch = Math.Max(ch, c.DesiredSize.Height);
        }
        int rows = (visible + columns - 1) / columns;
        return new Size(cw * columns, ch * rows);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int visible = Visible();
        int rows = Math.Max(1, (visible + columns - 1) / columns);
        double cw = final.Width / columns, ch = final.Height / rows;
        int i = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Arrange(new Rect(i % columns * cw, i / columns * ch, cw, ch));
            i++;
        }
        return final;
    }
}
