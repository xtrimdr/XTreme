using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Lápiz and Pincel (asked by a collaborator, like FL Studio's): a press on a clip makes it the brush; a press on an
/// empty stretch of a lane puts a copy of the brush there. With the Lápiz it is one copy, and dragging moves it before
/// it lands; with the Pincel dragging to the right keeps putting copies one after the other (asked by the user: the
/// pencil goes one by one, the brush covers a stretch). The grid applies, Alt frees it. The window owns the brush (it can also come from the Patrones list or the
/// Biblioteca) and makes the copies (<see cref="ClipStamp"/>): the timeline only knows how long the brush is and which
/// tracks take it, to draw where the copies will go.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>The brush's length: seconds for a take, beats for MIDI (so it follows the tempo); 0 = no brush yet.</summary>
    double pencilSize;
    bool pencilMidi;

    /// <summary>The brush: its length (seconds for a take, beats for a MIDI clip or a pattern) and whether it goes on MIDI tracks.</summary>
    public void SetPencil(double length, bool midi)
    {
        pencilSize = Math.Max(0, length);
        pencilMidi = midi;
        pencilHover = null;
        InvalidateVisual();
    }

    /// <summary>Whether there is a brush to put.</summary>
    double PencilSec => pencilSize;

    /// <summary>Where a copy put at <paramref name="at"/> ends: a MIDI brush's beats go through the tempo map.</summary>
    double PencilEnd(double at) => pencilMidi ? SecPlus(at, pencilSize) : at + pencilSize;

    /// <summary>A press on a clip with the pencil: its track and its id (a take or a MIDI clip), to make it the brush.</summary>
    public event Action<LiveTrack, string>? PencilPicked;
    /// <summary>The copies of one press (and its drag), on one track: where each starts. One step to undo.</summary>
    public event Action<LiveTrack, IReadOnlyList<double>>? PencilPlaced;
    /// <summary>A press on an empty lane that can't take a copy: no brush yet, or a track of the other kind.</summary>
    public event Action<LiveTrack>? PencilRefused;

    /// <summary>The lane being painted and the starts of the copies so far.</summary>
    int pencilLane = -1;
    readonly List<double> pencilAt = [];
    /// <summary>Where a press would put a copy (lane and start), drawn as a ghost; null off a lane that takes it.</summary>
    (int Lane, double Sec)? pencilHover;

    /// <summary>Lápiz or Pincel: the tools that put the brush.</summary>
    bool Drawing => tool is TimelineTool.Draw or TimelineTool.Paint;

    bool PencilTakes(int i) => i >= 0 && i < tracks.Count && PencilSec > 0 && tracks[i].Track.IsMidi == pencilMidi && !tracks[i].Track.EditLocked;

    void PencilPressed(int i, Point p, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount != 1) return;
        if (ClipAt(i, p.X) is { } take)
        {
            SelectClip(i, take.Clip.Id);
            PencilPicked?.Invoke(tracks[i], take.Clip.Id);
            return;
        }
        if (MidiClipAt(i, p.X) is { } notes)
        {
            SelectMidiClip(i, notes.Id);
            PencilPicked?.Invoke(tracks[i], notes.Id);
            return;
        }
        if (!PencilTakes(i))
        {
            PencilRefused?.Invoke(tracks[i]);
            return;
        }
        pencilLane = i;
        pencilAt.Clear();
        pencilAt.Add(SnappedSec(p.X));
        pencilHover = null;
        StartGesture("pencil", e);
    }

    /// <summary>The Lápiz's copy follows the pointer; with the Pincel, dragging to the right adds the next copy once the pointer passes the end of the last one.</summary>
    void DragPencil()
    {
        if (pencilAt.Count == 0 || PencilSec <= 0) return;
        if (tool == TimelineTool.Draw)
        {
            double to = SnappedSec(lastPointer.X);
            if (to != pencilAt[0]) { pencilAt[0] = to; moved = true; }
            return;
        }
        double at = Sec(lastPointer.X);
        while (at >= PencilEnd(pencilAt[^1]) && pencilAt.Count < MaxPencilCopies)
        {
            pencilAt.Add(PencilEnd(pencilAt[^1]));
            moved = true;
        }
    }

    /// <summary>The most copies one drag makes (a drag past the end of the view with auto-scroll stays reasonable).</summary>
    const int MaxPencilCopies = 512;

    /// <summary>The ghost of the next copy under the pointer, on a lane that takes it and off its clips.</summary>
    void UpdatePencilHover()
    {
        var before = pencilHover;
        pencilHover = null;
        int i = HitTrack(lastPointer.Y);
        if (Drawing && lastPointer.X >= HeaderW && PencilTakes(i) && AnyClipAt(i, lastPointer.X) == null)
            pencilHover = (i, SnappedSec(lastPointer.X));
        if (pencilHover != before) InvalidateVisual();
    }

    /// <summary>The copies being painted (or the one a press would put) as outlined ghosts on their lane.</summary>
    void DrawPencil(DrawingContext ctx)
    {
        int lane = gesture == "pencil" ? pencilLane : pencilHover?.Lane ?? -1;
        if (lane < 0 || PencilSec <= 0) return;
        foreach (var r in rows)
        {
            if (r.Folder != null || r.Lane != null || r.Track != lane) continue;
            double top = r.Top + RulerH - scrollY + 3, h = Math.Max(0, r.H - 6);
            var fill = Palette.Alpha(Palette.AccentColor, 0.12);
            var pen = Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.7));
            if (gesture == "pencil")
                foreach (double at in pencilAt) Ghost(at);
            else if (pencilHover is { } hover) Ghost(hover.Sec);
            void Ghost(double at)
            {
                double x0 = Math.Max(HeaderW, X(at)), x1 = X(PencilEnd(at));
                if (x1 > x0) ctx.DrawRectangle(fill, pen, new RoundedRect(new Rect(x0 + 0.5, top + 0.5, x1 - x0 - 1, h - 1), 5));
            }
            break;
        }
    }

    /// <summary>The starts painted in this drag, taken when it ends.</summary>
    List<double> TakePencil()
    {
        List<double> taken = [.. pencilAt];
        pencilAt.Clear();
        pencilLane = -1;
        return taken;
    }
}
