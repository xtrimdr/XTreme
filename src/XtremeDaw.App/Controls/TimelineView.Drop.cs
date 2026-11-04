using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Dropping a file on the timeline (a sample from the Biblioteca, a file from the Explorer): where it would land, drawn
/// on the moving layer while it is carried. The grid applies, and Alt frees it, like moving a clip. Several files land
/// one under the other (asked by the user), each drawn as a ghost: on the track that takes it, or on a new row under
/// the last track for one that will make its own (<see cref="AudioImport.Stack"/> decides, in the window).
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>A file being carried: the track it lands on (null = a new one) and its length (0 = not known yet).</summary>
    public readonly record struct DropGhost(string? TrackId, double LengthSec, string Name);

    double dropSec = -1;
    int dropTrack = -1;
    IReadOnlyList<DropGhost> dropGhosts = [];
    /// <summary>Each ghost's name, made when the ghosts change rather than on every frame.</summary>
    TextLayout[] dropLabels = [];

    /// <summary>Where a drop at <paramref name="p"/> lands, null off the lanes. With no track under it, it makes a new one.</summary>
    public (double Sec, string? TrackId)? DropTarget(Point p, KeyModifiers modifiers)
    {
        if (!IsEffectivelyVisible || p.X < HeaderW || p.Y < RulerH || p.X > Bounds.Width || p.Y > Bounds.Height) return null;
        int i = HitTrack(p.Y);
        double sec = Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0));
        return (sec, i >= 0 ? tracks[i].Track.Id : null);
    }

    /// <summary>The track <paramref name="id"/> and the ones under it, as they are shown (folded ones left out): where
    /// files dropped together stack. Empty for null (the space under the tracks).</summary>
    public List<string> TracksFrom(string? id)
    {
        var list = new List<string>();
        if (id == null) return list;
        foreach (var r in rows)
        {
            if (r.Folder != null || r.Lane != null) continue;
            string t = tracks[r.Track].Track.Id;
            if (t == id || list.Count > 0) list.Add(t);
        }
        return list;
    }

    /// <summary>A pattern carried over a MIDI track: the track under the pointer and a line where it starts.</summary>
    public void ShowDrop(Point p, KeyModifiers modifiers)
    {
        (double sec, int track) = DropTarget(p, modifiers) is { } t ? (t.Sec, HitTrack(p.Y)) : (-1d, -1);
        if (sec == dropSec && track == dropTrack && dropGhosts.Count == 0) return;
        (dropSec, dropTrack) = (sec, track);
        (dropGhosts, dropLabels) = ([], []);
        layer.InvalidateVisual();
    }

    /// <summary>Files carried over the lanes: a ghost per file at <paramref name="sec"/>, on its track or a new row.</summary>
    public void ShowDrop(double sec, IReadOnlyList<DropGhost> ghosts)
    {
        if (sec == dropSec && dropTrack == -1 && ghosts.SequenceEqual(dropGhosts)) return;
        (dropSec, dropTrack, dropGhosts) = (sec, -1, ghosts);
        dropLabels = [.. ghosts.Select(g => Palette.Text(g.Name, 11, Palette.Accent, Palette.UiBold))];
        layer.InvalidateVisual();
    }

    public void HideDrop()
    {
        if (dropSec < 0) return;
        (dropSec, dropTrack, dropGhosts, dropLabels) = (-1, -1, [], []);
        layer.InvalidateVisual();
    }

    void DrawDrop(DrawingContext ctx)
    {
        if (dropSec < 0) return;
        if (dropGhosts.Count > 0)
        {
            int added = 0;
            for (int k = 0; k < dropGhosts.Count; k++)
            {
                var g = dropGhosts[k];
                int i = g.TrackId != null ? TrackIndex(g.TrackId) : -1;
                if (i < 0) i = tracks.Count + added++;
                DrawDropGhost(ctx, LaneY(i), LaneH(i), g.LengthSec, dropLabels[k]);
            }
            return;
        }
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

    /// <summary>One carried file: its lane tinted and a dashed clip of its length, or just its start while the length
    /// isn't known (a file from the Explorer), like <see cref="DrawGhost"/> draws a carried clip.</summary>
    void DrawDropGhost(DrawingContext ctx, double y, double h, double lengthSec, TextLayout label)
    {
        if (y > Bounds.Height || y + h < RulerH) return;
        double W = Bounds.Width, x0 = X(dropSec);
        ctx.FillRectangle(Palette.AccentSoft, new Rect(HeaderW, y, Math.Max(0, W - HeaderW), h));
        var box = new Rect(x0, y + 5, Math.Max(2, lengthSec > 0 ? X(dropSec + lengthSec) - x0 : 0), Math.Max(0, h - 10));
        if (lengthSec > 0) ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.18), Palette.Dashed(Palette.Accent, 1.5), box, 5, 5);
        else ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(x0, y), new Point(x0, y + h));
        double lx = Math.Clamp(box.X + 8, HeaderW + 8, Math.Max(HeaderW + 8, W - label.Width - 8));
        ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, new Rect(lx - 4, box.Y + 3, label.Width + 8, label.Height + 2), 3, 3);
        ctx.DrawText(label, new Point(lx, box.Y + 4));
    }
}
