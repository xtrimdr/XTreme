using System.Diagnostics;
using Avalonia;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Who else is where in a shared session (#291, asked by the user): a tag in the person's color with their name on
/// the track they selected, edit, play or record on (after the track's name, from the name's room, like the channel
/// badge) and on the clip they selected or edit (from the clip's label strip, <see cref="ClipBand"/>), and their
/// playhead as a thin line in their color, on the moving layer, while their transport runs. The window hands the
/// tags over whole when they change (<see cref="SetPresence"/>); a frame only looks them up, so nothing is built while
/// drawing. A cursor goes on from where it was last told at the speed of time, so a few updates a second draw smooth.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>A person's tag: the text it shows (their name, "+n" when others are on it too), their color and whether they are recording there.</summary>
    public sealed record PresenceTag(string Text, Color Color, bool Recording);

    /// <summary>Someone's playhead: where it was told (<see cref="Sec"/>, at <see cref="Stopwatch"/> time <see cref="Stamp"/>) and their tag.</summary>
    public readonly record struct PresenceCursor(double Sec, bool Rolling, long Stamp, PresenceTag Tag);

    IReadOnlyDictionary<string, PresenceTag> presenceRows = new Dictionary<string, PresenceTag>(), presenceClips = new Dictionary<string, PresenceTag>();
    PresenceCursor[] presenceCursors = [];
    /// <summary>The tag's text size and its padding on each side (px).</summary>
    const double PresenceFont = 10, PresencePad = 5, PresenceDot = 7;

    /// <summary>Who is on which track (by id) and clip (by id), and their playheads. Empty maps = nobody.</summary>
    public void SetPresence(IReadOnlyDictionary<string, PresenceTag> rows, IReadOnlyDictionary<string, PresenceTag> clips, PresenceCursor[] cursors)
    {
        presenceRows = rows;
        presenceClips = clips;
        presenceCursors = cursors;
        InvalidateVisual();
    }

    /// <summary>Redraws only the moving layer: someone's playhead moved on.</summary>
    public void InvalidateCursors() => layer.InvalidateVisual();

    /// <summary>True while someone's playhead runs, so the window keeps redrawing it.</summary>
    public bool CursorsRolling
    {
        get
        {
            foreach (var c in presenceCursors) if (c.Rolling) return true;
            return false;
        }
    }

    static double PresenceTagW(PresenceTag t) =>
        Palette.Text(t.Text, PresenceFont, Palette.Fg, Palette.UiBold).Width + 2 * PresencePad + (t.Recording ? PresenceDot : 0);

    /// <summary>The tag: tinted with the person's color, its border and name in it (a dot of the record red first while they record).</summary>
    static void DrawPresenceTag(DrawingContext ctx, Rect r, PresenceTag t)
    {
        ctx.DrawRectangle(Palette.Alpha(t.Color, 0.22), Palette.Pen(Palette.Alpha(t.Color, 0.9)), r.Deflate(0.5), 5, 5);
        double x = r.X + PresencePad;
        if (t.Recording)
        {
            ctx.DrawEllipse(Palette.Rec, null, new Point(x + 2.5, r.Center.Y), 2.5, 2.5);
            x += PresenceDot;
        }
        var text = Palette.Text(t.Text, PresenceFont, Palette.Alpha(t.Color, 1), Palette.UiBold);
        ctx.DrawText(text, new Point(x, r.Center.Y - text.Height / 2));
    }

    /// <summary>A track's tag, if someone is on it.</summary>
    PresenceTag? RowPresence(string id) => presenceRows.Count > 0 && presenceRows.TryGetValue(id, out var t) ? t : null;

    /// <summary>A clip's tag in its label strip, right after its media badge.</summary>
    void DrawClipPresence(DrawingContext ctx, ref ClipBand band, string clipId)
    {
        if (presenceClips.Count == 0 || !presenceClips.TryGetValue(clipId, out var t)) return;
        if (band.TakeRight(PresenceTagW(t), out var r)) DrawPresenceTag(ctx, r, t);
    }

    /// <summary>
    /// Where this copy's mouse is over the rows, for the others' pointers (#319): the row, how far down it, and the
    /// time over its lane or how far across the headers. False when it isn't over a row. Asked on every presence tick:
    /// it doesn't allocate.
    /// </summary>
    public bool PointerSpot(out string row, out double rowY, out double? sec, out double? head)
    {
        row = "";
        rowY = 0;
        sec = head = null;
        var p = lastPointer;
        if (!IsPointerOver && gesture == null) return false;
        if (p.X < 0 || p.Y < RulerH || p.X > Bounds.Width || p.Y > Bounds.Height) return false;
        foreach (var r in rows)
        {
            if (r.H <= 0) continue;
            string id = RowIdOf(r);
            double top = RulerH + Top(id) - scrollY;
            if (p.Y < top || p.Y >= top + r.H) continue;
            row = id;
            rowY = (p.Y - top) / r.H;
            if (p.X < HeaderW) head = p.X / Math.Max(1, HeaderW);
            else sec = Sec(p.X);
            return true;
        }
        return false;
    }

    /// <summary>Where someone's <see cref="PointerSpot"/> is on this copy's screen (its own zoom, scroll and heights); null when out of sight.</summary>
    public Point? SpotPoint(string row, double rowY, double? sec, double? head)
    {
        foreach (var r in rows)
        {
            if (r.H <= 0 || RowIdOf(r) != row) continue;
            double y = RulerH + Top(row) - scrollY + Math.Clamp(rowY, 0, 1) * r.H;
            double x = sec is { } s ? X(s) : Math.Clamp(head ?? 0.5, 0, 1) * HeaderW;
            if (y < RulerH || y > Bounds.Height || x > Bounds.Width || sec != null && x < HeaderW) return null;
            return new Point(x, y);
        }
        return null;
    }

    /// <summary>A row's header, on this copy's screen, for a pointer on a control this copy doesn't show (#319): its middle; null when out of sight.</summary>
    public Point? HeaderPoint(string row) => SpotPoint(row, 0.5, null, 0.5);

    /// <summary>The others' running playheads, under this copy's own, each with its name on the ruler.</summary>
    void DrawPresenceCursors(DrawingContext ctx, double H)
    {
        foreach (var c in presenceCursors)
        {
            if (!c.Rolling) continue;
            double sec = c.Sec + Stopwatch.GetElapsedTime(c.Stamp).TotalSeconds;
            double x = Math.Round(X(sec)) + 0.5;
            if (x < HeaderW || x > Bounds.Width) continue;
            var color = Palette.Alpha(c.Tag.Color, 0.85);
            ctx.DrawLine(Palette.Pen(color), new Point(x, StripsH), new Point(x, H));
            var text = Palette.Text(c.Tag.Text, PresenceFont, Palette.App, Palette.UiBold);
            var r = new Rect(x, StripsH - text.Height - 4, text.Width + 2 * PresencePad, text.Height + 2);
            ctx.DrawRectangle(color, null, r, 3, 3);
            ctx.DrawText(text, new Point(r.X + PresencePad, r.Y + 1));
        }
    }
}
