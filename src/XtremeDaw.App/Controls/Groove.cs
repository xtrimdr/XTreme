using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The line that divides a view into parts (the mixer's docks and its pinned master, Mastering's meter): a recessed
/// channel, the window's darkest fill between two hairlines in <c>Line</c>, instead of a plain light line (asked by the
/// user, #275). One that can be dragged carries a grip in its middle, lit in the accent while the pointer is on it or
/// it is being dragged. Drawn from the palette's brushes on every render, so a theme change repaints it; allocates nothing.
/// </summary>
public sealed class Groove : Control
{
    /// <summary>The channel's width (px), hairlines included.</summary>
    public const double W = 6;
    /// <summary>The grip's length along the line (px).</summary>
    const double GripH = 28;

    /// <summary>Whether it shows the grip (it can be dragged).</summary>
    public bool Grip { get; init; }

    bool hot;
    /// <summary>The pointer is on it, or it is being dragged: the grip and the hairlines take the accent.</summary>
    public bool Hot
    {
        get => hot;
        set { if (hot == value) return; hot = value; InvalidateVisual(); }
    }

    public Groove() { Width = W; IsHitTestVisible = false; }

    public override void Render(DrawingContext ctx) => Draw(ctx, new Rect(Bounds.Size), Grip, hot);

    /// <summary>Draws the channel filling <paramref name="r"/>'s height at its left edge.</summary>
    public static void Draw(DrawingContext ctx, Rect r, bool grip, bool hot)
    {
        double h = r.Height;
        if (h <= 0) return;
        ctx.FillRectangle(Palette.App, new Rect(r.X, r.Y, W, h));
        IBrush edge = hot ? Palette.Accent : Palette.Line;
        ctx.FillRectangle(edge, new Rect(r.X, r.Y, 1, h));
        ctx.FillRectangle(edge, new Rect(r.X + W - 1, r.Y, 1, h));
        if (!grip) return;
        double gh = Math.Min(GripH, h - 8);
        if (gh <= 0) return;
        ctx.DrawRectangle(hot ? Palette.Accent : Palette.Faint, null, new Rect(r.X + W / 2 - 1, r.Y + (h - gh) / 2, 2, gh), 1, 1);
    }
}

/// <summary>A pinned column (<see cref="Groove"/> at its left edge, then its content): the master beside the mixer's strips.</summary>
public sealed class GrooveBorder : Decorator
{
    public override void Render(DrawingContext ctx) => Groove.Draw(ctx, new Rect(Bounds.Size), false, false);
}
