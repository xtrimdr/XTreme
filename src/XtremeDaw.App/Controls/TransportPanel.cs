using Avalonia;
using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The transport bar's three groups (input, the transport box, the views): the box stays centered on the window while
/// the sides fit beside it; with less room it slides away from the wider side, and when the three don't fit in one row
/// the side groups go down to a second row (input left, views right), so nothing is ever drawn over the box (the
/// "*,Auto,*" grid it replaces did, between 1000 and 1600 px).
/// </summary>
public sealed class TransportPanel : Panel
{
    /// <summary>Least space between the box and a side group, and between the rows.</summary>
    public double Spacing { get; set; } = 16;
    public double RowSpacing { get; set; } = 6;

    bool TwoRows(double width) =>
        Children.Count == 3 && Children[0].DesiredSize.Width + Children[1].DesiredSize.Width + Children[2].DesiredSize.Width + 2 * Spacing > width;

    protected override Size MeasureOverride(Size available)
    {
        foreach (var c in Children) c.Measure(Size.Infinity);
        if (Children.Count != 3) return default;
        var (l, m, r) = (Children[0].DesiredSize, Children[1].DesiredSize, Children[2].DesiredSize);
        double w = double.IsInfinity(available.Width) ? l.Width + m.Width + r.Width + 2 * Spacing : available.Width;
        double h = TwoRows(w) ? m.Height + RowSpacing + Math.Max(l.Height, r.Height) : Math.Max(m.Height, Math.Max(l.Height, r.Height));
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Children.Count != 3) return final;
        var (left, mid, right) = (Children[0], Children[1], Children[2]);
        var (l, m, r) = (left.DesiredSize, mid.DesiredSize, right.DesiredSize);
        double w = final.Width;
        if (TwoRows(w))
        {
            double y = m.Height + RowSpacing, h = Math.Max(l.Height, r.Height);
            mid.Arrange(new Rect(Math.Max(0, (w - m.Width) / 2), 0, m.Width, m.Height));
            left.Arrange(new Rect(0, y + (h - l.Height) / 2, Math.Min(l.Width, w), l.Height));
            right.Arrange(new Rect(Math.Max(0, w - r.Width), y + (h - r.Height) / 2, r.Width, r.Height));
            return final;
        }
        double row = final.Height;
        // centered, unless a side doesn't fit beside it: then as close to the center as the sides allow
        double x = Math.Clamp((w - m.Width) / 2, l.Width + Spacing, w - r.Width - Spacing - m.Width);
        left.Arrange(new Rect(x - Spacing - l.Width, (row - l.Height) / 2, l.Width, l.Height));
        mid.Arrange(new Rect(x, (row - m.Height) / 2, m.Width, m.Height));
        right.Arrange(new Rect(w - r.Width, (row - r.Height) / 2, r.Width, r.Height));
        return final;
    }
}
