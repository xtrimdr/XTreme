using Avalonia;
using Avalonia.Media;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Looping a take's clip by its top right corner (asked by the user, like GarageBand): the corner's pointer is
/// <see cref="ToolCursors.Loop"/>, and dragging it shows the repeats it would make, each with the loop icon, until the
/// release asks the window for them (<see cref="ClipLoopRequested"/>, <see cref="ClipLoops"/>). Grabbed on a repeat it
/// works on the loop's source. MIDI clips and automation blocks loop from the same corner, on their own.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>A take's clip looped: the source's id and where the loop ends now.</summary>
    public event Action<string, double>? ClipLoopRequested;

    /// <summary>Px from a clip's right end, inside it, that grab its loop.</summary>
    const double LoopGrab = 10;

    /// <summary>How far down from a clip's top its loop corner reaches, for a clip <paramref name="h"/> px high.</summary>
    static double LoopBand(double h) => Math.Clamp(h / 4, 6, 12);

    // the loop gesture: its source, where one pass starts and how long it is, where the loop ended when grabbed and now
    string? loopSource;
    double loopStart, loopPass, loopFrom, loopEnd;

    /// <summary>Whether <paramref name="p"/> is on the loop corner of <paramref name="clip"/> (its lane's clip rect).</summary>
    bool OnLoopCorner(LiveClip clip, Point p, double right)
    {
        int lane = tracks.ToList().FindIndex(t => t.Clips.Contains(clip));
        if (lane < 0) return false;
        double top = LaneY(lane) + 5;
        return p.X <= right && right - p.X <= LoopGrab && p.Y >= top && p.Y < top + LoopBand(RowH(tracks[lane].Track) - 10);
    }

    /// <summary>Notes the loop a press on <paramref name="clip"/>'s corner works on (its source's, for a repeat).</summary>
    void StartLoop(int i, LiveClip clip)
    {
        var source = clip.Clip.LoopOf is { } id ? tracks[i].Clips.FirstOrDefault(c => c.Clip.Id == id) ?? clip : clip;
        var w = Window(source);
        loopSource = source.Clip.Id;
        loopStart = w.Origin + w.TrimStart;
        loopPass = w.End - loopStart;
        loopFrom = w.End;
        foreach (var c in tracks[i].Clips)
            if (c.Clip.LoopOf == loopSource) loopFrom = Math.Max(loopFrom, Window(c).End);
        loopEnd = loopFrom;
    }

    void DragLoop(double dx)
    {
        bool free = (modifiers & Avalonia.Input.KeyModifiers.Alt) != 0;
        loopEnd = Math.Max(loopStart + ClipEnvelope.MinClipSec, ClipEditing.Snap(loopFrom + dx, Grid, free));
    }

    /// <summary>The repeats a loop drag would make, over lane <paramref name="i"/>: outlined, each with the loop icon.</summary>
    void DrawLoopGhost(DrawingContext ctx, int i, double y, double W)
    {
        if (loopSource == null || !moved || loopPass < ClipEnvelope.MinClipSec || !tracks[i].Clips.Any(c => c.Clip.Id == loopSource)) return;
        var color = TrackIcons.ColorOf(tracks[i].Track) ?? Palette.AccentColor;
        double lh = RowH(tracks[i].Track);
        var pen = Palette.Pen(Palette.Alpha(color, 0.9));
        for (int k = 1; k <= ClipLoops.MaxRepeats; k++)
        {
            double from = loopStart + k * loopPass;
            if (loopEnd - from < ClipEnvelope.MinClipSec) break;
            double x0 = X(from), x1 = X(Math.Min(from + loopPass, loopEnd));
            if (x0 > W) break;
            if (x1 < HeaderW) continue;
            var r = new Rect(x0, y + 5, Math.Max(2, x1 - x0), lh - 10);
            ctx.DrawRectangle(Palette.Alpha(color, 0.12), pen, r, 5, 5);
            if (r.Width >= 16) ctx.DrawText(ToolCursors.LoopGlyph, 10, Palette.Alpha(color, 1), new Point(r.X + 4, r.Y + 3), Icons);
        }
    }

    /// <summary>The loop corner on a hovered or picked clip: the loop icon at its top right, lit while it would be grabbed.</summary>
    void DrawLoopCorner(DrawingContext ctx, Rect clip, bool lit)
    {
        if (clip.Width < 3 * LoopGrab || clip.Height < 20) return;
        var color = lit ? Palette.Accent : Palette.Alpha(Palette.FgColor, 0.7);
        ctx.DrawText(ToolCursors.LoopGlyph, 9, color, new Point(clip.Right - LoopGrab - 1, clip.Top + 1), Icons);
    }

    /// <summary>What the loop gesture ends in, taken before the gesture is cleared: null when it wasn't one.</summary>
    (string Source, double End, bool Changed)? TakeLoop()
    {
        if (loopSource is not { } source) return null;
        loopSource = null;
        return (source, loopEnd, moved && Math.Abs(loopEnd - loopFrom) > 1e-6);
    }
}
