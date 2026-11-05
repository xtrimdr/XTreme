using Avalonia;
using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Buttons that share the width evenly, side by side, when each one's content fits in its share, and one under the
/// other, full width, when not (asked by the user for the Biblioteca's two clean-up buttons: side by side when the
/// column is wide enough). Never cuts a button's text.
/// </summary>
public sealed class EvenRow : Panel
{
    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        var shown = Children.Where(c => c.IsVisible).ToList();
        foreach (var c in shown) c.Measure(Size.Infinity);
        if (shown.Count == 0) return default;
        double w = double.IsInfinity(available.Width) ? shown.Sum(c => c.DesiredSize.Width) + Spacing * (shown.Count - 1) : available.Width;
        if (Fits(shown, w)) return new Size(w, shown.Max(c => c.DesiredSize.Height));
        return new Size(w, shown.Sum(c => c.DesiredSize.Height) + Spacing * (shown.Count - 1));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var shown = Children.Where(c => c.IsVisible).ToList();
        if (shown.Count == 0) return final;
        if (Fits(shown, final.Width))
        {
            double cw = (final.Width - Spacing * (shown.Count - 1)) / shown.Count, x = 0;
            foreach (var c in shown)
            {
                c.Arrange(new Rect(x, 0, cw, final.Height));
                x += cw + Spacing;
            }
            return final;
        }
        double y = 0;
        foreach (var c in shown)
        {
            c.Arrange(new Rect(0, y, final.Width, c.DesiredSize.Height));
            y += c.DesiredSize.Height + Spacing;
        }
        return final;
    }

    /// <summary>Each child fits in an even share of <paramref name="width"/>.</summary>
    bool Fits(List<Control> shown, double width)
    {
        double share = (width - Spacing * (shown.Count - 1)) / shown.Count;
        return shown.All(c => c.DesiredSize.Width <= share);
    }
}
