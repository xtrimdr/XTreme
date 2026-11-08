using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The tools on the automation lanes (asked by the user, #281: a block answers to every tool as a clip does). Dividir cuts
/// the block where you click (with its line under the pointer), Borrar takes every block it goes over, Escuchar plays the
/// lane's track alone, Rango marks a stretch of the lane's track (its lanes lose it with the clips), Zoom and Mano work
/// as anywhere. Lápiz and Pincel have a brush of their own here: a click on a block takes it, a click on the empty lane puts
/// a copy (with the Pincel, one after the other while dragging right); with no block taken yet the click makes a new block.
/// Like the rest of the control it only raises events: the window edits the project.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>Dividir on a block: its lane's owner, the lane's id, the block's id and the (snapped) time.</summary>
    public event Action<IAutomated, string, string, double>? AutomationSplitRequested;
    /// <summary>Lápiz or Pincel on a lane: the brush and where each copy starts. One step to undo.</summary>
    public event Action<IAutomated, string, AutomationBlock, IReadOnlyList<double>>? AutomationPainted;

    /// <summary>The block the pencil took last, a copy of it (it pastes on any lane); null = none yet.</summary>
    AutomationBlock? autoBrush;
    /// <summary>The lane being painted, its owner and the starts of the copies so far.</summary>
    AutomationLane? autoPencilLane;
    IAutomated? autoPencilOwner;
    readonly List<double> autoPencilAt = [];
    /// <summary>Dividir over a block: the lane and the time the cut would go at.</summary>
    (AutomationLane Lane, double Sec)? autoSplitHover;

    /// <summary>Selects one block alone (after a split, the right piece, as with a clip).</summary>
    public void SelectBlock(string? id)
    {
        boxed.Clear();
        selectedBlock = id;
        InvalidateVisual();
    }

    /// <summary>A left press on a lane with a tool other than Seleccionar. True when the tool took it.</summary>
    bool AutoToolPressed(Row r, Point p, PointerPressedEventArgs e)
    {
        var lane = r.Lane!;
        var owner = OwnerOf(r);
        var block = AutoBlockAt(lane, Sec(p.X));
        int track = r.Folder == null && r.Bus == null && r.Track >= 0 && r.Track < tracks.Count ? r.Track : -1;
        switch (tool)
        {
            case TimelineTool.Select:
                return false;
            case TimelineTool.Hand:
                StartGesture("pan", e);
                return true;
            case TimelineTool.Zoom:
                zoomFrom = Sec(p.X);
                StartGesture("zoom", e);
                return true;
            case TimelineTool.Split:
                if (e.ClickCount == 1 && block != null)
                {
                    SelectBlock(block.Id);
                    AutomationSplitRequested?.Invoke(owner, lane.Id, block.Id, SnappedSec(p.X));
                }
                break;
            case TimelineTool.Erase:
                erasing.Clear();
                autoErasing.Clear();
                erasePrev = p;
                StartGesture("erase", e);
                if (block != null)
                {
                    autoErasing.Add(block.Id);
                    moved = true;
                }
                return true;
            case TimelineTool.Listen:
                if (track >= 0 && block != null)
                {
                    Select(track);
                    StartGesture("listen", e);
                    ListenStarted?.Invoke(tracks[track], Math.Max(0, Sec(p.X)));
                }
                break;
            case TimelineTool.Range:
                // a lane marks its track's stretch: the range takes its clips and its lanes
                if (track >= 0)
                {
                    StartRange(track, p, e);
                    return true;
                }
                break;
            case TimelineTool.Draw or TimelineTool.Paint:
                if (e.ClickCount != 1) break;
                if (block != null)
                {
                    SelectBlock(block.Id);
                    autoBrush = Automate(block);
                    break;
                }
                SelectBlock(null);
                if (autoBrush == null)
                {
                    AutomationBlockCreated?.Invoke(owner, lane.Id, SnapAt(p.X));
                    break;
                }
                autoPencilLane = lane;
                autoPencilOwner = owner;
                autoPencilAt.Clear();
                autoPencilAt.Add(SnappedSec(p.X));
                StartGesture("auto-pencil", e);
                return true;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>The Lápiz's copy follows the pointer; with the Pincel, dragging right adds the next copy past the end of the last.</summary>
    void DragAutoPencil()
    {
        if (autoBrush is not { } brush || autoPencilAt.Count == 0) return;
        if (tool == TimelineTool.Draw)
        {
            double to = SnappedSec(lastPointer.X);
            if (to != autoPencilAt[0]) { autoPencilAt[0] = to; moved = true; }
            return;
        }
        double at = Sec(lastPointer.X);
        while (at >= autoPencilAt[^1] + brush.LengthSec && autoPencilAt.Count < MaxPencilCopies)
        {
            autoPencilAt.Add(autoPencilAt[^1] + brush.LengthSec);
            moved = true;
        }
    }

    /// <summary>The copies of this press, taken before the gesture is cleared: the lane's owner, its id, the brush and the starts.</summary>
    (IAutomated Owner, string Lane, AutomationBlock Brush, List<double> Starts)? TakeAutoPencil()
    {
        (IAutomated, string, AutomationBlock, List<double>)? taken = null;
        if (autoPencilLane is { } l && autoPencilOwner is { } o && autoBrush is { } b && autoPencilAt.Count > 0)
            taken = (o, l.Id, b, new List<double>(autoPencilAt));
        ClearAutoPencil();
        return taken;
    }

    void ClearAutoPencil()
    {
        autoPencilAt.Clear();
        autoPencilLane = null;
        autoPencilOwner = null;
    }

    /// <summary>Dividir: where a click on a lane would cut, if the pointer is on a block far enough from its edges.</summary>
    void UpdateAutoSplitHover()
    {
        var before = autoSplitHover;
        autoSplitHover = null;
        if (tool == TimelineTool.Split && lastPointer.X >= HeaderW && HitRow(lastPointer.Y) is { Lane: { } lane }
            && AutoBlockAt(lane, Sec(lastPointer.X)) is { } b && SnappedSec(lastPointer.X) is var at && Automations.CanSplit(b, at))
            autoSplitHover = (lane, at);
        if (autoSplitHover != before) InvalidateVisual();
    }

    /// <summary>On a lane: the cut line of Dividir and the copies the pencil is putting, as outlined ghosts.</summary>
    void DrawAutoTools(DrawingContext ctx, AutomationLane lane, double y, double h)
    {
        if (autoSplitHover is { } s && s.Lane == lane && gesture == null && X(s.Sec) is var sx && sx >= HeaderW)
        {
            double x = Math.Round(sx) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.Fg, 1), new Point(x, y + 3), new Point(x, y + h - 3));
        }
        if (gesture != "auto-pencil" || autoPencilLane != lane || autoBrush is not { } brush) return;
        var fill = Palette.Alpha(Palette.AccentColor, 0.12);
        var pen = Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.7));
        foreach (double at in autoPencilAt)
        {
            double x0 = Math.Max(HeaderW, X(at)), x1 = X(at + brush.LengthSec);
            if (x1 > x0) ctx.DrawRectangle(fill, pen, new RoundedRect(new Rect(x0 + 0.5, y + 1.5, x1 - x0 - 1, h - 3), 5));
        }
    }
}
