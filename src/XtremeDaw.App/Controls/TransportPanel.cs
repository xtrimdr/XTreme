using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The transport bar's three groups (input, the transport box, the views), always in one row (#36: two rows looked
/// broken on smaller screens): the box stays centered on the window while the sides fit beside it, and with less room
/// it slides away from the wider side. When the three don't fit, the controls marked with <see cref="FoldProperty"/>
/// hide one by one, the lowest number first, until they do; the ones the user turned off (<see cref="OffProperty"/>,
/// the bar's right-click menu) stay hidden at any width. A folded control keeps the width it had when it was last seen
/// (plus its stack's spacing), so the panel knows exactly when it fits again and the choice never flips between passes.
/// Fold only controls in horizontal stacks that nothing else shows or hides.
/// </summary>
public sealed class TransportPanel : Panel
{
    /// <summary>Order in which a control hides when the bar is short of room: 1 goes first; 0 never hides.</summary>
    public static readonly AttachedProperty<int> FoldProperty = AvaloniaProperty.RegisterAttached<TransportPanel, Control, int>("Fold");
    /// <summary>Hidden by the user, whatever the width.</summary>
    public static readonly AttachedProperty<bool> OffProperty = AvaloniaProperty.RegisterAttached<TransportPanel, Control, bool>("Off");

    public static int GetFold(Control c) => c.GetValue(FoldProperty);
    public static void SetFold(Control c, int v) => c.SetValue(FoldProperty, v);
    public static bool GetOff(Control c) => c.GetValue(OffProperty);
    public static void SetOff(Control c, bool v) => c.SetValue(OffProperty, v);

    /// <summary>Least space between the box and a side group.</summary>
    public double Spacing { get; set; } = 16;

    /// <summary>The foldable controls in fold order and the width each takes when shown (gathered on the first measure:
    /// the bar's controls are fixed in the XAML).</summary>
    Control[]? folds;
    double[] widths = [];

    protected override Size MeasureOverride(Size available)
    {
        if (folds == null)
        {
            folds = [.. this.GetLogicalDescendants().OfType<Control>().Where(c => GetFold(c) > 0).OrderBy(GetFold)];
            widths = new double[folds.Length];
        }
        foreach (var c in Children) c.Measure(Size.Infinity);
        if (Children.Count != 3) return default;
        double total = Children[0].DesiredSize.Width + Children[1].DesiredSize.Width + Children[2].DesiredSize.Width + 2 * Spacing;
        if (!double.IsInfinity(available.Width) && Fold(total, available.Width))
            foreach (var c in Children) c.Measure(Size.Infinity);
        double w = double.IsInfinity(available.Width) ? total : available.Width;
        double h = Math.Max(Children[0].DesiredSize.Height, Math.Max(Children[1].DesiredSize.Height, Children[2].DesiredSize.Height));
        return new Size(w, h);
    }

    /// <summary>Shows what fits at this width and hides the rest; true when something changed.</summary>
    bool Fold(double total, double width)
    {
        var f = folds!;
        // the width of everything not turned off, as if nothing were folded
        double full = total;
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i].IsEffectivelyVisible) widths[i] = f[i].DesiredSize.Width + (f[i].Parent is StackPanel sp ? sp.Spacing : 0);
            else if (!GetOff(f[i])) full += widths[i];
        }
        bool changed = false;
        for (int i = 0; i < f.Length; i++)
        {
            bool show = !GetOff(f[i]) && full <= width;
            if (!show && !GetOff(f[i])) full -= widths[i];
            if (f[i].IsVisible != show) { f[i].IsVisible = show; changed = true; }
        }
        return changed;
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Children.Count != 3) return final;
        var (left, mid, right) = (Children[0], Children[1], Children[2]);
        var (l, m, r) = (left.DesiredSize, mid.DesiredSize, right.DesiredSize);
        double w = final.Width, row = final.Height;
        // centered, unless a side doesn't fit beside it: then as close to the center as the sides allow (with no room
        // at all, below the window's least width, the box keeps clear of the input and the views run off the edge)
        double x = Math.Max(l.Width + Spacing, Math.Min((w - m.Width) / 2, w - r.Width - Spacing - m.Width));
        left.Arrange(new Rect(x - Spacing - l.Width, (row - l.Height) / 2, l.Width, l.Height));
        mid.Arrange(new Rect(x, (row - m.Height) / 2, m.Width, m.Height));
        right.Arrange(new Rect(Math.Max(w - r.Width, x + m.Width + Spacing), (row - r.Height) / 2, r.Width, r.Height));
        return final;
    }
}
