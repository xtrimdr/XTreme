using Avalonia;
using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// A row of an effect's full view (asked by the user, #312: no room left unused): its inner panels side by side, each as
/// wide as its weight, all as tall as the tallest. Each panel is measured again at that height, so the short ones can fill
/// it (<see cref="FxFill"/>, <see cref="KnobGrid.FillMax"/>) instead of leaving a hole under what they hold.
/// </summary>
public sealed class FxRow(double[] weights, double gap) : Panel
{
    double Share(int i, double width)
    {
        double total = 0;
        foreach (double w in weights) total += w;
        double room = Math.Max(0, width - gap * (Children.Count - 1));
        return total <= 0 ? 0 : room * weights[Math.Min(i, weights.Length - 1)] / total;
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 0 : available.Width;
        double tall = 0, wide = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            c.Measure(new Size(width > 0 ? Share(i, width) : double.PositiveInfinity, double.PositiveInfinity));
            tall = Math.Max(tall, c.DesiredSize.Height);
            wide += c.DesiredSize.Width;
        }
        // again at the row's height: a panel that can use it grows into it, never past it
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            c.Measure(new Size(width > 0 ? Share(i, width) : double.PositiveInfinity, tall));
        }
        return new Size(width > 0 ? width : wide + gap * Math.Max(0, Children.Count - 1), tall);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            double w = Share(i, final.Width);
            Children[i].Arrange(new Rect(x, 0, w, final.Height));
            x += w + gap;
        }
        return final;
    }
}
