using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>What a press on the timeline's lanes does (native only, the tool strip in the project's bar).</summary>
public enum TimelineTool { Select, Draw, Paint, Range, Split, Erase, Listen, Zoom, Hand }

/// <summary>
/// The timeline's tools (native only, asked by the user): besides Seleccionar (everything the lanes did before) and
/// Lápiz and Pincel (TimelineView.Pencil.cs), Rango
/// marks a stretch over one or more tracks, Dividir cuts a clip where you click, Borrar takes away every clip it
/// touches, Escuchar plays the track alone from the click while the button is held, Zoom zooms on a click (Alt, out) or
/// to the stretch dragged, and Mano drags the view. A tool only changes what a press on the lanes does: the headers,
/// the ruler, the markers and the menus work the same with any of them. Each tool shows its own pointer
/// (<see cref="ToolCursors"/>), so the timeline still needs no hover help. Takes and MIDI clips answer to them alike.
/// </summary>
public sealed partial class TimelineView
{
    TimelineTool tool;

    public TimelineTool Tool
    {
        get => tool;
        set
        {
            if (tool == value) return;
            CancelGesture();
            tool = value;
            splitHover = null;
            pencilHover = null;
            UpdateCursor();
            InvalidateVisual();
        }
    }

    /// <summary>Dividir: a click on a clip, at the (snapped) time under the pointer.</summary>
    public event Action<LiveTrack, LiveClip, double>? SplitAtRequested;
    /// <summary>Dividir on a MIDI clip: its track, its id and the (snapped) time.</summary>
    public event Action<LiveTrack, string, double>? MidiSplitRequested;
    /// <summary>Borrar: the clips one drag touched, removed together (one step to undo).</summary>
    public event Action<IReadOnlyList<string>>? ClipsErased;
    /// <summary>Escuchar: the track to hear alone and from where, while the button is held; <see cref="ListenEnded"/> on release.</summary>
    public event Action<LiveTrack, double>? ListenStarted;
    public event Action? ListenEnded;

    /// <summary>The tracks a range covers, by id (Rango or Shift+drag, over one track or down several).</summary>
    readonly HashSet<string> rangeTracks = [];
    /// <summary>Where a range started, in px from the top of the content: dragging up or down from there adds tracks.</summary>
    double rangeTop;
    /// <summary>The clips the eraser went over in this drag, drawn faded until it is released.</summary>
    readonly HashSet<string> erasing = [];
    /// <summary>A right press on the lanes: the menu it opens if it is released without dragging (a drag erases instead).</summary>
    Action? rightMenu;
    /// <summary>How far a right press moves before it erases instead of opening the menu.</summary>
    const double RightEraseSlop = 4;
    /// <summary>Dividir: the lane and time the cut would go at, drawn as a line on the clip under the pointer.</summary>
    (int Lane, double Sec)? splitHover;
    /// <summary>Where a Zoom drag started, in seconds.</summary>
    double zoomFrom;
    /// <summary>A Zoom drag narrower than this is a click: it zooms in (or out with Alt) around the pointer.</summary>
    const double ZoomDragPx = 6;
    /// <summary>How much one Zoom click zooms.</summary>
    const double ZoomStep = 2;

    bool FreeTime => (modifiers & KeyModifiers.Alt) != 0;
    double SnappedSec(double x) => Math.Max(0, ClipEditing.Snap(Sec(x), Grid, FreeTime));

    /// <summary>A left press on the lanes (or under the last one) with a tool other than Seleccionar. True when the tool took it.</summary>
    bool ToolPressed(int i, Point p, PointerPressedEventArgs e)
    {
        bool lane = i >= 0 && i < tracks.Count;
        switch (tool)
        {
            case TimelineTool.Hand:
                StartGesture("pan", e);
                return true;
            case TimelineTool.Zoom:
                zoomFrom = Sec(p.X);
                StartGesture("zoom", e);
                return true;
            case TimelineTool.Draw or TimelineTool.Paint when lane:
                PencilPressed(i, p, e);
                return true;
            case TimelineTool.Range when lane:
                StartRange(i, p, e);
                return true;
            case TimelineTool.Split when lane:
                if (e.ClickCount == 1 && ClipAt(i, p.X) is { } cut)
                {
                    SelectClip(i, cut.Clip.Id);
                    SplitAtRequested?.Invoke(tracks[i], cut, SnappedSec(p.X));
                }
                else if (e.ClickCount == 1 && MidiClipAt(i, p.X) is { } notes)
                {
                    SelectMidiClip(i, notes.Id);
                    MidiSplitRequested?.Invoke(tracks[i], notes.Id, SnappedSec(p.X));
                }
                e.Handled = true;
                return true;
            case TimelineTool.Erase:
                erasing.Clear();
                if (lane && AnyClipAt(i, p.X) is { } gone) erasing.Add(gone);
                StartGesture("erase", e);
                return true;
            case TimelineTool.Listen when lane:
                if (AnyClipAt(i, p.X) != null)
                {
                    Select(i);
                    StartGesture("listen", e);
                    ListenStarted?.Invoke(tracks[i], Math.Max(0, Sec(p.X)));
                }
                e.Handled = true;
                return true;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>The id of the take or MIDI clip under the pointer in lane <paramref name="i"/>, or null.</summary>
    string? AnyClipAt(int i, double x) => ClipAt(i, x)?.Clip.Id ?? MidiClipAt(i, x)?.Id;

    /// <summary>Starts a range on track <paramref name="i"/> (Rango, or Shift+drag with Seleccionar).</summary>
    void StartRange(int i, Point p, PointerPressedEventArgs e)
    {
        Select(i);
        rangeTracks.Clear();
        rangeTracks.Add(tracks[i].Track.Id);
        rangeTop = p.Y - RulerH + scrollY;
        rangeAnchor = rangeStart = rangeEnd = SnappedSec(p.X);
        StartGesture("range", e);
    }

    /// <summary>The range follows the pointer in time and takes in every visible track between where it started and the pointer.</summary>
    void DragRange()
    {
        double at = SnappedSec(lastPointer.X);
        (rangeStart, rangeEnd) = (Math.Min(rangeAnchor, at), Math.Max(rangeAnchor, at));
        double cy = lastPointer.Y - RulerH + scrollY;
        double lo = Math.Min(rangeTop, cy), hi = Math.Max(rangeTop, cy);
        rangeTracks.Clear();
        foreach (var r in rows)
        {
            if (r.Folder != null || r.Lane != null || r.Track >= tracks.Count || r.Top + r.H <= lo || r.Top > hi) continue;
            string id = tracks[r.Track].Track.Id;
            if (!hidden.Contains(id)) rangeTracks.Add(id);
        }
    }

    /// <summary>A right press on the lanes: a drag erases what it goes over (as in FL Studio); a click opens <paramref name="menu"/> on release.</summary>
    void StartRightErase(PointerPressedEventArgs e, Action menu)
    {
        erasing.Clear();
        rightMenu = menu;
        StartGesture("erase", e);
    }

    /// <summary>The eraser takes every clip it goes over.</summary>
    void DragErase()
    {
        if (rightMenu != null && !moved)
        {
            // a right press only erases once it is a drag; the clip it started on goes too
            if (Math.Abs(lastPointer.X - down.X) < RightEraseSlop && Math.Abs(lastPointer.Y - down.Y) < RightEraseSlop) return;
            moved = true;
            if (down.X >= HeaderW && AnyClipAt(HitTrack(down.Y), down.X) is { } first) erasing.Add(first);
        }
        int i = HitTrack(lastPointer.Y);
        if (lastPointer.X >= HeaderW && AnyClipAt(i, lastPointer.X) is { } c && erasing.Add(c)) moved = true;
    }

    /// <summary>A released Zoom: a drag shows that stretch across the view, a click zooms around the pointer.</summary>
    void EndZoom(Point p, bool zoomOut)
    {
        double a = Math.Max(0, Math.Min(zoomFrom, Sec(p.X))), b = Math.Max(zoomFrom, Sec(p.X));
        if (Math.Abs(X(zoomFrom) - p.X) >= ZoomDragPx && b > a)
            ZoomAround((Bounds.Width - HeaderW - 24) / (b - a), a, 12);
        else ZoomAround(zoomOut ? pps / ZoomStep : pps * ZoomStep, Sec(p.X), p.X - HeaderW);
    }

    /// <summary>Zooms keeping second <paramref name="sec"/> at <paramref name="px"/> px from the headers.</summary>
    void ZoomAround(double next, double sec, double px)
    {
        pps = Math.Clamp(next, MinPps, MaxPps);
        double fit = (Bounds.Width - HeaderW - 24) / Math.Max(8, LengthSec);
        SetFit(Math.Abs(pps / fit - 1) < 0.02);
        scrollX = Math.Max(0, sec * pps - px);
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Dividir: where a click would cut, if the pointer is on a clip far enough from its edges.</summary>
    void UpdateSplitHover()
    {
        var before = splitHover;
        splitHover = null;
        int i = HitTrack(lastPointer.Y);
        if (tool == TimelineTool.Split && lastPointer.X >= HeaderW && ClipAt(i, lastPointer.X) is { } c)
        {
            double at = SnappedSec(lastPointer.X);
            var w = Window(c);
            if (at > w.Start + ClipEnvelope.MinClipSec && at < w.End - ClipEnvelope.MinClipSec) splitHover = (i, at);
        }
        else if (tool == TimelineTool.Split && lastPointer.X >= HeaderW && MidiClipAt(i, lastPointer.X) is { } m)
        {
            double at = SnappedSec(lastPointer.X);
            if (MidiEditing.CanSplit(m, at, Bpm)) splitHover = (i, at);
        }
        if (splitHover != before) InvalidateVisual();
    }

    /// <summary>The cut line of Dividir over the clip in lane <paramref name="i"/>.</summary>
    void DrawSplitHover(DrawingContext ctx, int i, double y, double lh)
    {
        if (splitHover is not { } s || s.Lane != i || gesture != null) return;
        double x = Math.Round(X(s.Sec)) + 0.5;
        if (x < HeaderW) return;
        ctx.DrawLine(Palette.Pen(Palette.Fg, 1), new Point(x, y + 5), new Point(x, y + lh - 5));
    }

    /// <summary>The stretch a Zoom drag will show: a band over every lane.</summary>
    void DrawZoomBand(DrawingContext ctx, double H)
    {
        if (gesture != "zoom" || Math.Abs(X(zoomFrom) - lastPointer.X) < ZoomDragPx) return;
        double x0 = Math.Max(HeaderW, Math.Min(X(zoomFrom), lastPointer.X)), x1 = Math.Max(X(zoomFrom), lastPointer.X);
        if (x1 <= x0) return;
        var band = new Rect(x0, RulerH, x1 - x0, Math.Max(0, H - RulerH));
        ctx.FillRectangle(Palette.Alpha(Palette.FgColor, 0.06), band);
        var pen = Palette.Pen(Palette.Alpha(Palette.FgColor, 0.5));
        ctx.DrawLine(pen, new Point(Math.Round(x0) + 0.5, RulerH), new Point(Math.Round(x0) + 0.5, H));
        ctx.DrawLine(pen, new Point(Math.Round(x1) - 0.5, RulerH), new Point(Math.Round(x1) - 0.5, H));
    }

    /// <summary>The tool's pointer over the lanes; null where the tool doesn't act (headers, ruler, folders, a row's edge).</summary>
    Cursor? ToolCursor()
    {
        if (tool == TimelineTool.Select || lastPointer.X < HeaderW || lastPointer.Y < RulerH || RowEdgeAt(lastPointer) != null) return null;
        if (HitRow(lastPointer.Y) is { Folder: not null }) return null;
        // over a clip the pencil picks it up as the brush
        if (Drawing && AnyClipAt(HitTrack(lastPointer.Y), lastPointer.X) != null) return CursorOf(StandardCursorType.Hand);
        return tool == TimelineTool.Range ? CursorOf(StandardCursorType.Ibeam) : ToolCursors.Of(tool, FreeTime);
    }
}
