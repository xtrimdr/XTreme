using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Dropping a file on the timeline (a sample from the Biblioteca, a file from the Explorer): where it would land, drawn
/// on the moving layer while it is carried. The grid applies, and Alt frees it, like moving a clip.
/// </summary>
public sealed partial class TimelineView
{
    double dropSec = -1;
    int dropTrack = -1;

    /// <summary>Where a drop at <paramref name="p"/> lands, null off the lanes. With no track under it, it makes a new one.</summary>
    public (double Sec, string? TrackId)? DropTarget(Point p, KeyModifiers modifiers)
    {
        if (!IsEffectivelyVisible || p.X < HeaderW || p.Y < RulerH || p.X > Bounds.Width || p.Y > Bounds.Height) return null;
        int i = HitTrack(p.Y);
        double sec = Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0));
        return (sec, i >= 0 ? tracks[i].Track.Id : null);
    }

    public void ShowDrop(Point p, KeyModifiers modifiers)
    {
        (double sec, int track) = DropTarget(p, modifiers) is { } t ? (t.Sec, HitTrack(p.Y)) : (-1d, -1);
        if (sec == dropSec && track == dropTrack) return;
        (dropSec, dropTrack) = (sec, track);
        layer.InvalidateVisual();
    }

    public void HideDrop()
    {
        if (dropSec < 0) return;
        (dropSec, dropTrack) = (-1, -1);
        layer.InvalidateVisual();
    }

    void DrawDrop(DrawingContext ctx)
    {
        if (dropSec < 0) return;
        double px = X(dropSec), H = Bounds.Height;
        double top = RulerH, bottom = H;
        foreach (var r in rows)
        {
            if (r.Folder != null || r.Lane != null || r.Track != dropTrack) continue;
            top = r.Top + RulerH - scrollY;
            bottom = top + r.H;
            ctx.FillRectangle(Palette.AccentSoft, new Rect(HeaderW, top, Math.Max(0, Bounds.Width - HeaderW), r.H));
            break;
        }
        ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(px, top), new Point(px, bottom));
    }
}
