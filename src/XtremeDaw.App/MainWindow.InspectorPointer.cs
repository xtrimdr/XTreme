using Avalonia;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App;

/// <summary>
/// The line from the selected track's header to the inspector (#180, <see cref="InspectorPointer"/>). Placed on every
/// tick from the panels' bounds, so it follows the scroll, a reorder and the window's layout without hooking each of
/// them; the positions are added up along the parents by hand (TranslatePoint can allocate, and a tick must not).
/// Only while the inspector and the timeline are both docked in this window, the inspector open and showing the
/// selected track or folder, and the inspector beside or above the timeline's headers.
/// </summary>
public partial class MainWindow
{
    /// <summary>How far into the track's header the line starts: past the card's 1 px border.</summary>
    const double PointerInset = 1;

    void TickInspectorPointer()
    {
        if (!TrackColorLooks.Pointer || inspectorFolded || selBusId != null || !inspectorTopRule.IsEffectivelyVisible
            || !Timeline.IsEffectivelyVisible || !Timeline.SelectedHeader(out double hx, out double top, out double bottom)
            || !InRoot(Timeline, out var tl) || !InRoot(InspectorBar, out var ib) || !InRoot(inspectorTopRule, out var head))
        {
            InspPointer.Clear();
            return;
        }
        double hy = tl.Y + (top + bottom) / 2;
        var insp = new Rect(ib, InspectorBar.Bounds.Size);
        // the row with the track's or folder's name (#180: "que llegue hasta" there), or the header if it is scrolled away
        double headBottom = head.Y + inspectorTopRule.Bounds.Height;
        Rect row = new(head, inspectorTopRule.Bounds.Size);
        if (NameRow(out var name) && name.Y >= headBottom && name.Bottom <= insp.Bottom) row = name;
        double iy = row.Center.Y;
        hx += tl.X;
        double gx, tip;
        if (insp.Right <= hx) { gx = (insp.Right + hx) / 2; tip = row.Right + PointerGap; } // beside: along the gap between them
        else if (insp.Bottom <= tl.Y) { gx = Math.Max(1, Math.Min(insp.X, hx) - 2); tip = row.X - PointerGap; } // above: along the window's left edge
        else { InspPointer.Clear(); return; }
        InspPointer.Set(hx + PointerInset, hy, gx, tip, iy, inspectorTint ?? Palette.AccentColor);
    }

    /// <summary>The selected track's or folder's name row in the inspector, in <see cref="Root"/>'s pixels.</summary>
    bool NameRow(out Rect row)
    {
        Visual v = takeHeader.IsEffectivelyVisible ? takeHeader : folderHead;
        row = default;
        if (!v.IsEffectivelyVisible || !InRoot(v, out var at)) return false;
        row = new Rect(at, v.Bounds.Size);
        return true;
    }

    /// <summary>The cards' margin (<c>Border.card</c>), and what the inspector adds on its right while the line is on, so
    /// the line has room between the panels (#180: "que la separación … sea un poquito más grande").</summary>
    const double CardMargin = 4, PointerRoom = 6;

    /// <summary>Widens the gap between the inspector and the timeline while the line is on; on start and when it is switched.</summary>
    void SpaceForPointer() =>
        InspectorBar.Margin = new Thickness(CardMargin, CardMargin, CardMargin + (TrackColorLooks.Pointer ? PointerRoom : 0), CardMargin);

    /// <summary>The room between the arrowhead's tip and the name row's last button.</summary>
    const double PointerGap = 3;

    /// <summary>Where a control sits in <see cref="Root"/>, the pointer's parent; false if it is not in this window's tree.</summary>
    bool InRoot(Visual v, out Point at)
    {
        double x = 0, y = 0;
        for (Visual? p = v; p != null; p = p.GetVisualParent())
        {
            if (p == Root) { at = new Point(x, y); return true; }
            if (!p.IsVisible) break;
            x += p.Bounds.X; y += p.Bounds.Y;
        }
        at = default;
        return false;
    }
}
