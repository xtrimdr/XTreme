using Avalonia;
using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// What an inner panel of an effect's full view holds, top to bottom (asked by the user, #312: no room left unused). Given
/// more height than it needs (a short panel beside a tall one in an <see cref="FxRow"/>), its pictures take it all
/// (<see cref="Grow"/>); without a picture, each of the rest is measured again with its share, so a knob grid grows its
/// knobs into it, and what still remains goes evenly between them.
/// </summary>
public sealed class FxFill : Panel
{
    readonly List<Control> grows = [];

    public double Spacing { get; init; } = 8;

    /// <summary>Adds a child that takes the panel's spare height (a graph, drawn to its bounds).</summary>
    public void Grow(Control c)
    {
        grows.Add(c);
        Children.Add(c);
    }

    int Shown()
    {
        int n = 0;
        foreach (var c in Children) if (c.IsVisible) n++;
        return n;
    }

    protected override Size MeasureOverride(Size available)
    {
        var free = new Size(available.Width, double.PositiveInfinity);
        double sum = 0, wide = 0;
        int n = Shown();
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(free);
            sum += c.DesiredSize.Height;
            wide = Math.Max(wide, c.DesiredSize.Width);
        }
        sum += Spacing * Math.Max(0, n - 1);
        if (double.IsInfinity(available.Height) || available.Height <= sum) return new Size(wide, sum);
        if (!grows.Exists(g => g.IsVisible) && n > 0)
        {
            double share = (available.Height - sum) / n;
            foreach (var c in Children)
            {
                if (!c.IsVisible) continue;
                c.Measure(new Size(available.Width, c.DesiredSize.Height + share));
                wide = Math.Max(wide, c.DesiredSize.Width);
            }
        }
        return new Size(wide, available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int n = Shown(), growing = 0;
        double sum = Spacing * Math.Max(0, n - 1);
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            sum += c.DesiredSize.Height;
            if (grows.Contains(c)) growing++;
        }
        double spare = Math.Max(0, final.Height - sum);
        double more = growing > 0 ? spare / growing : 0;
        double gap = growing > 0 || n == 0 ? 0 : spare / (n + 1);
        double y = gap;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            double h = c.DesiredSize.Height + (grows.Contains(c) ? more : 0);
            c.Arrange(new Rect(0, y, final.Width, h));
            y += h + Spacing + gap;
        }
        return final;
    }
}
