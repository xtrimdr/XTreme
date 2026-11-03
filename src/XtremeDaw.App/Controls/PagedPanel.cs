using Avalonia;
using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Shows one child at a time (the Mastering view's editor: the panel of the module picked above it) and takes its size,
/// so a small panel doesn't sit in a big empty box; it also measures the others and keeps the tallest
/// (<see cref="Tallest"/>), so the view can keep the height of the tallest and never jump when another is picked. A
/// child with a <see cref="Layoutable.MaxWidth"/> gets that width, or what there is when there is less: its knobs lay
/// themselves out in it (<c>FxGrid</c>) instead of being cut. The others are laid out out of sight and clipped, so they
/// neither draw nor take clicks, yet stay built: their knobs keep refreshing and switching is instant.
/// </summary>
public sealed class PagedPanel : Panel
{
    int selected;
    /// <summary>How far past the right edge the pages not shown are laid out.</summary>
    const double Away = 100000;

    /// <summary>The child shown (an index into <see cref="Panel.Children"/>).</summary>
    public int Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The tallest child's height, from the last measure.</summary>
    public double Tallest { get; private set; }

    /// <summary>The width a child is given: its MaxWidth, or less when there is less; without one, what it asks.</summary>
    static double WidthOf(Control c, double available) =>
        double.IsInfinity(c.MaxWidth) ? Math.Min(c.DesiredSize.Width, available) : Math.Min(c.MaxWidth, available);

    protected override Size MeasureOverride(Size available)
    {
        double h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.IsInfinity(c.MaxWidth) ? available.Width : Math.Min(c.MaxWidth, available.Width), available.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        Tallest = h;
        if (selected >= Children.Count) return default;
        var shown = Children[selected];
        return new Size(WidthOf(shown, available.Width), shown.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        // the others keep their own size (a panel squeezed to nothing would lay its knobs out in one column and be
        // measured that tall), out of sight past the right edge, which the panel clips
        ClipToBounds = true;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            c.Arrange(i == selected ? new Rect(final) : new Rect(final.Width + Away, 0, WidthOf(c, final.Width), c.DesiredSize.Height));
        }
        return final;
    }
}
