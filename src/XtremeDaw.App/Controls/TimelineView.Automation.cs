using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// A track's automation lanes (<see cref="AutomationLane"/>), its children in the timeline (asked by the user): each is a
/// row right under its track, with its name in the header column and its blocks over a shadow of the track's clips. A
/// block goes like a MIDI clip: double click on the empty lane makes one, its top strip moves it (with Shift or Ctrl, a copy, that can
/// land on any lane: #171) and
/// its edges trim it. Inside it, a click adds a point, a drag moves it, a double click takes it out and Shift+drag draws;
/// the handle in the middle of a stretch sets its tension (a curve's bend, the steps or cycles of the other shapes) and a
/// double click on it takes it back to 0. A right click on a point or on a stretch (its handle or its line) asks for that
/// stretch's shape (<see cref="AutoShape"/>, as in FL Studio: a point's is the stretch that comes into it). The right end of its
/// top strip loops it (like GarageBand): its points repeat until where the drag stops, each repeat with the loop icon, and
/// a point edited in any repeat changes them all. They fold with the
/// track's chevron, go with their track when it folds into a folder, and a dragged track carries them folded. A folder's
/// lanes (#136) are the same rows under the folder's row, above its tracks: they hide when the folder folds (the user's
/// call) and fold from its menu or its own chevron, which (like a track's) carries the automation glyph over it (#159). The owner of a lane is an <see cref="IAutomated"/>, the track or the folder. Like the
/// rest of the control it only raises events: the window edits the project.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>The window's words: a lane's name, and a position (0..1) as its parameter reads.</summary>
    public Func<IAutomated, AutomationLane, string>? AutomationName { get; set; }
    public Func<IAutomated, AutomationLane, double, string>? AutomationValue { get; set; }
    /// <summary>A block after a gesture (its id may be new: a copy).</summary>
    public event Action<IAutomated, string, AutomationBlock>? AutomationBlockEdited;
    /// <summary>A double click on the empty lane: a new block there.</summary>
    public event Action<IAutomated, string, double>? AutomationBlockCreated;
    /// <summary>Its switch: it plays or the parameter stays where the channel has it.</summary>
    public event Action<IAutomated, string>? AutomationToggled;
    /// <summary>A track's chevron folds or shows its lanes.</summary>
    public event Action<IAutomated>? AutomationFoldRequested;
    /// <summary>Right click on a lane: the block and point under the pointer (null, -1 = none) and the snapped time there.</summary>
    public event Action<IAutomated, AutomationLane, AutomationBlock?, int, double, Point>? AutomationContextRequested;
    /// <summary>
    /// Right click on a point or a stretch: the block, the stretch whose shape it asks for (by the point it starts at) and
    /// whether it was on the point (true) or on the stretch.
    /// </summary>
    public event Action<IAutomated, AutomationLane, AutomationBlock, int, bool, Point>? AutomationShapeRequested;
    /// <summary>Right click on a header control (a track's or a folder's): its target.</summary>
    public event Action<IAutomated, string, Point>? AutomationAsked;
    /// <summary>A lane's trash can: the window asks there (the point) before removing it.</summary>
    public event Action<IAutomated, AutomationLane, Point>? AutomationRemoveRequested;

    /// <summary>The block picked last, for the shortcuts: its lane's owner (a track or a folder) and lane; null = none.</summary>
    public (IAutomated Owner, AutomationLane Lane, AutomationBlock Block)? SelectedBlock
    {
        get
        {
            if (selectedBlock == null) return null;
            foreach (var t in tracks)
                foreach (var l in t.Track.Automation ?? [])
                    if (Automations.BlockById(l, selectedBlock) is { } b) return (t.Track, l, b);
            foreach (var f in folders)
                foreach (var l in f.Automation ?? [])
                    if (Automations.BlockById(l, selectedBlock) is { } b) return (f, l, b);
            return null;
        }
    }

    /// <summary>Px around a point that grab it.</summary>
    const double AutoGrab = 7;
    /// <summary>Above and below the curve inside its row: under the block's top strip.</summary>
    const double AutoPadTop = 12, AutoPadBottom = 5;
    /// <summary>A block's top strip, which moves it, and how near an edge trims it (px): as wide as the edge's tab, inside the block.</summary>
    const double AutoStrip = 9, AutoEdge = 7;
    /// <summary>How far apart two points drawn by hand are (s).</summary>
    const double AutoDrawGap = 0.03;
    const double AutoChipW = 22;
    /// <summary>A stretch narrower than this (px) shows no handle: it would sit on its points.</summary>
    const double AutoHandleMinW = 28;
    /// <summary>A stretch this flat (of the lane's height) has nothing to bend.</summary>
    const double AutoFlat = 0.01;
    /// <summary>How close a shaped stretch is drawn to its curve (px between its segments), and the most a cycle of a wave gets.</summary>
    const double AutoBendStep = 4, AutoCycleSteps = 12;
    /// <summary>Px of a vertical drag that take a stretch's steps or cycles from the fewest to the most.</summary>
    const double AutoTensionDrag = 160;

    enum AutoDrag { None, Point, Bend, Draw, Move, Left, Right, Loop, Pass }

    string? selectedBlock;
    // the "auto" gesture: the lane and block, the block as it goes (a copy, never the project's), what the drag does
    AutomationLane? autoLane;
    AutomationBlock? autoBlock, autoDraft;
    AutoDrag autoDrag;
    bool autoCopy;
    IAutomated? autoOwner;
    // where a copy lands: the lane under the pointer (any track's or folder's, #171), its own until it leaves it
    AutomationLane? autoDestLane;
    IAutomated? autoDestOwner;
    int autoPoint = -1;
    double autoPen, autoFrom, autoTension;
    // where the pass being edited starts in the block (relative): a looped block's points are its first pass's
    double autoPassAt;

    /// <summary>A track's or a folder's lanes on screen: none folded, none while it is carried, none in a folded folder.</summary>
    IEnumerable<AutomationLane> LanesOf(IAutomated t) => t.Automation is not { } list || t.AutomationFolded || IsDragged(t.Id)
        || t is Folder { Collapsed: true } or Folder { Separator: true } ? [] : list.Where(l => !l.Hidden);

    /// <summary>Whose a lane row is: its track or its folder.</summary>
    IAutomated OwnerOf(Row r) => r.Folder ?? (IAutomated)tracks[r.Track].Track;

    /// <summary>The color a lane is drawn in: its track's or its folder's.</summary>
    Color LaneTint(Row r) => r.Folder is { } f ? FolderColor(f) : TrackIcons.ColorOf(tracks[r.Track].Track) ?? Palette.AccentColor;

    /// <summary>A lane's usual height at the current vertical zoom: it scales with the tracks (#174: a fixed one stayed
    /// tall over zoomed-out tracks), its own <see cref="AutomationLane.HeightScale"/> a multiple of this.</summary>
    public double AutoBaseH => AutomationLane.DefaultHeight * laneH / Track.DefaultHeight;

    double AutoH(AutomationLane l) => heightDrag is { } d && d.Id == l.Id ? d.H
        : Math.Clamp(Math.Round(AutoBaseH * (l.HeightScale ?? 1) / 2) * 2, AutomationLane.MinHeight, AutomationLane.MaxHeight);

    /// <summary>A track's row with its lanes under it.</summary>
    double BlockH(Track t) => RowH(t) + LanesOf(t).Sum(AutoH);

    /// <summary>A folder's row with its lanes under it (its tracks come after them).</summary>
    double FolderBlockH(Folder f) => FolderRowH(f) + LanesOf(f).Sum(AutoH);

    double AutoY(Row r) => RulerH + Top(r.Lane!.Id) - scrollY;
    double AutoY(double y, double h, double position) => y + AutoPadTop + (1 - position) * Math.Max(1, h - AutoPadTop - AutoPadBottom);
    double AutoPosition(double y, double h, double py) => Math.Clamp(1 - (py - y - AutoPadTop) / Math.Max(1, h - AutoPadTop - AutoPadBottom), 0, 1);

    bool AutoStepped(Row r) => Automations.Stepped(OwnerOf(r), r.Lane!.Target);

    /// <summary>The lane's blocks as they are drawn: during a gesture, with the one going.</summary>
    IEnumerable<AutomationBlock> BlocksOf(AutomationLane l)
    {
        if (autoDraft == null || l != DraftLane) return l.Blocks;
        return autoCopy ? l.Blocks.Append(autoDraft) : l.Blocks.Select(b => b == autoBlock ? autoDraft : b);
    }

    /// <summary>The lane the block being dragged is drawn in: a copy's goes where it would land.</summary>
    AutomationLane? DraftLane => autoCopy ? autoDestLane : autoLane;

    /// <summary>The block on top at a time: of two that overlap, the later one (it takes over at its start).</summary>
    AutomationBlock? AutoBlockAt(AutomationLane l, double sec)
    {
        AutomationBlock? found = null;
        foreach (var b in l.Blocks)
            if (sec >= b.AtSec && sec < b.EndSec && (found == null || b.AtSec >= found.AtSec)) found = b;
        return found;
    }

    /// <summary>The point of a block under the pointer, or -1.</summary>
    int AutoPointAt(Row r, AutomationBlock b, Point p)
    {
        var pts = b.Points;
        double y = AutoY(r);
        for (int k = pts.Count - 1; k >= 0; k--)
            if (pts[k].Sec <= b.PassSec && Math.Abs(p.X - X(b.AtSec + pts[k].Sec)) <= AutoGrab && Math.Abs(p.Y - AutoY(y, r.H, pts[k].Value)) <= AutoGrab) return k;
        return -1;
    }

    /// <summary>
    /// The handle of the stretch after point <paramref name="k"/>: a curve's on it halfway through, the others' halfway
    /// between its points (a double curve's middle never moves, so its handle stays there too: asked by the user).
    /// </summary>
    Point AutoHandle(AutomationBlock block, int k, double y, double h)
    {
        var a = block.Points[k];
        var b = block.Points[k + 1];
        double along = a.Shape == AutoShape.Curve ? AutoShapes.Along(a.Shape, 0.5, a.Tension) : 0.5;
        return new(X(block.AtSec + a.Sec + (b.Sec - a.Sec) * 0.5), AutoY(y, h, a.Value + (b.Value - a.Value) * along));
    }

    /// <summary>Whether the stretch after point <paramref name="k"/> can bend: inside the block, wide enough and going somewhere.</summary>
    bool AutoBendable(AutomationBlock block, int k)
    {
        var a = block.Points[k];
        var b = block.Points[k + 1];
        return b.Sec <= block.PassSec && Math.Abs(b.Value - a.Value) >= AutoFlat && (b.Sec - a.Sec) * pps >= AutoHandleMinW;
    }

    /// <summary>The stretch whose handle is under the pointer, or -1.</summary>
    int AutoHandleAt(Row r, AutomationBlock b, Point p)
    {
        if (AutoStepped(r)) return -1;
        double y = AutoY(r);
        for (int k = 0; k + 1 < b.Points.Count; k++)
        {
            if (!AutoBendable(b, k)) continue;
            var c = AutoHandle(b, k, y, r.H);
            if (Math.Abs(p.X - c.X) <= AutoGrab && Math.Abs(p.Y - c.Y) <= AutoGrab) return k;
        }
        return -1;
    }

    /// <summary>The stretch whose line is under the pointer (by the point it starts at), or -1: only one that can take a shape.</summary>
    int AutoStretchAt(Row r, AutomationBlock b, Point p)
    {
        if (AutoStepped(r)) return -1;
        double at = Sec(p.X) - b.AtSec;
        if (at < 0 || at > b.PassSec) return -1;
        int k = b.Points.FindLastIndex(x => x.Sec <= at);
        if (k < 0 || k + 1 >= b.Points.Count || !AutoBendable(b, k)) return -1;
        return Math.Abs(p.Y - AutoY(AutoY(r), r.H, b.PositionAt(at, false))) <= AutoGrab ? k : -1;
    }

    /// <summary>What a press on a block does where the pointer is, past its points and handles.</summary>
    AutoDrag AutoEdgeAt(Row r, AutomationBlock b, Point p)
    {
        if (Math.Abs(p.X - X(b.AtSec)) <= AutoEdge) return AutoDrag.Left;
        bool strip = p.Y < AutoY(r) + AutoStrip + 1;
        // the right end of the top strip loops it, the rest of that edge trims it
        if (strip && p.X <= X(b.EndSec) && X(b.EndSec) - p.X <= AutoLoopGrab) return AutoDrag.Loop;
        if (Math.Abs(p.X - X(b.EndSec)) <= AutoEdge) return AutoDrag.Right;
        // a looped block's first seam is its original's end: it sets the pass, and every repeat follows (#171)
        if (b.LoopSec != null && Math.Abs(p.X - X(b.AtSec + b.PassSec)) <= AutoEdge) return AutoDrag.Pass;
        return strip ? AutoDrag.Move : AutoDrag.None;
    }

    /// <summary>Px from a block's right end, on its top strip, that grab its loop.</summary>
    const double AutoLoopGrab = 10;

    /// <summary>Whether the pointer is on a block's loop corner (its pointer is <see cref="ToolCursors.Loop"/>).</summary>
    bool OnAutoLoop(Row r)
    {
        var p = lastPointer;
        return p.X >= HeaderW && AutoBlockAt(r.Lane!, Sec(p.X)) is { } b && AutoPointAt(r, b, p) < 0 && AutoEdgeAt(r, b, p) == AutoDrag.Loop;
    }

    /// <summary>What the pointer says over a lane.</summary>
    StandardCursorType AutoCursor(Row r)
    {
        var p = lastPointer;
        if (p.X < HeaderW || AutoBlockAt(r.Lane!, Sec(p.X)) is not { } b) return StandardCursorType.Arrow;
        if (AutoPointAt(r, b, p) >= 0) return StandardCursorType.SizeAll;
        if (AutoHandleAt(r, b, p) >= 0) return StandardCursorType.SizeNorthSouth;
        return AutoEdgeAt(r, b, p) switch
        {
            AutoDrag.Left or AutoDrag.Right or AutoDrag.Pass => StandardCursorType.SizeWestEast,
            AutoDrag.Move => StandardCursorType.SizeAll,
            _ => StandardCursorType.Hand,
        };
    }

    /// <summary>A lane's switch, just left of its trash can.</summary>
    Rect AutoChip(Row r) => new(AutoTrashRect(r).X - AutoChipW - 6, AutoY(r) + 5, AutoChipW, 18);

    /// <summary>A lane's trash can, last in its header and lined up with the tracks' and folders' (asked by the user, #152).</summary>
    Rect AutoTrashRect(Row r) => new(HeaderRight - 38, AutoY(r) + 2, 24, 24);

    /// <summary>The automation glyph (Segoe Fluent), the same as the "Automatización" menu's: over the chevron it says what
    /// the chevron folds (asked by the user, #159).</summary>
    const string AutoGlyph = "";
    /// <summary>The fold column: the chevron at the foot of the handle and the glyph over it; where its grip ends (px from
    /// the row's top), which the glyph leaves clear or is not drawn.</summary>
    const double AutoFoldW = 16, AutoCaretH = 15, AutoGlyphH = 14, AutoGripEnd = 27;

    /// <summary>A track's chevron over its lanes, at the foot of its handle; nothing without lanes.</summary>
    Rect AutoFoldRect(int i) => tracks[i].Track.Automation is { Count: > 0 } ? AutoFoldColumn(4 + Indent(i), LaneY(i), LaneH(i)) : NoRect;

    /// <summary>A folder's, the same at the foot of its handle: only open (folded, its lanes hide) and with room under its grip.</summary>
    Rect FolderAutoFoldRect(Folder f)
    {
        double h = FolderRowH(f);
        return f.Separator || f.Collapsed || f.Automation is not { Count: > 0 } || h < AutoGripEnd + AutoCaretH + 2 ? NoRect
            : AutoFoldColumn(4, FolderY(f), h);
    }

    /// <summary>The chevron at the foot of a row's handle, with the glyph over it when the row leaves room under its grip.</summary>
    static Rect AutoFoldColumn(double x, double y, double h)
    {
        bool glyph = h - 2 - AutoCaretH - AutoGlyphH >= AutoGripEnd;
        double top = y + h - 2 - AutoCaretH - (glyph ? AutoGlyphH : 0);
        return new(x, top, AutoFoldW, AutoCaretH + (glyph ? AutoGlyphH : 0));
    }

    void DrawAutoLanes(DrawingContext ctx, double W)
    {
        foreach (var r in rows)
            if (r.Lane != null) DrawAutoLane(ctx, r, W);
    }

    void DrawAutoLane(DrawingContext ctx, Row r, double W)
    {
        double y = AutoY(r), h = r.H;
        if (y + h < RulerH || y > Bounds.Height) return;
        var owner = OwnerOf(r);
        var lane = r.Lane!;
        var tint = LaneTint(r);
        bool live = lane.On && Automations.Exists(owner, lane.Target);
        bool stepped = AutoStepped(r);
        ctx.FillRectangle(Palette.Alpha(Palette.RaisedColor, 0.3), new Rect(HeaderW, y, W - HeaderW, h));
        var pen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.4));
        foreach (var (gx, _) in grid)
            if (gx >= HeaderW) ctx.DrawLine(pen, new Point(gx, y), new Point(gx, y + h));
        // the track's clips under it (a folder's: its tracks'), so the blocks are placed against the music
        for (int i = 0; i < tracks.Count; i++)
        {
            if (r.Folder is { } f ? tracks[i].Track.FolderId != f.Id : i != r.Track) continue;
            foreach (var c in tracks[i].Clips)
            {
                double x0 = X(c.ClipStartSec), x1 = X(c.ClipEndSec);
                if (x1 >= HeaderW && x0 <= W) ctx.FillRectangle(Palette.Alpha(tint, 0.05), new Rect(x0, y + 2, Math.Max(2, x1 - x0), h - 4));
            }
        }
        var blocks = BlocksOf(lane).ToList();
        foreach (var b in blocks) DrawAutoBlock(ctx, b, y, h, W, tint);
        var flat = lane == DraftLane && autoDraft != null ?AutomationLane.Flatten(blocks, stepped) : lane.Flat(stepped);
        if (flat.Count > 0)
        {
            var line = new StreamGeometry();
            using (var g = line.Open()) TraceAuto(g, flat, y, h, W, stepped);
            ctx.DrawGeometry(null, Palette.Pen(Palette.Alpha(live ? tint : Palette.FaintColor, 1), 1.5), line);
        }
        if (autoDrag != AutoDrag.Draw || lane != autoLane)
            foreach (var b in blocks) DrawAutoPoints(ctx, r, b, y, h, W, tint, live, stepped);
        foreach (var b in blocks) DrawAutoHandles(ctx, r, b, y, h, W);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW, y + h - 0.5), new Point(W, y + h - 0.5));
    }

    void DrawAutoBlock(DrawingContext ctx, AutomationBlock b, double y, double h, double W, Color tint)
    {
        double x0 = X(b.AtSec), x1 = X(b.EndSec);
        if (x1 < HeaderW || x0 > W) return;
        var box = new Rect(x0, y + 1, Math.Max(3, x1 - x0), h - 3);
        bool picked = b.Id == selectedBlock || b == autoDraft;
        ctx.DrawRectangle(Palette.Alpha(tint, picked ? 0.16 : 0.09), Palette.Pen(Palette.Alpha(tint, picked ? 0.95 : 0.45), picked ? 1.5 : 1), box, 4, 4);
        ctx.FillRectangle(Palette.Alpha(tint, picked ? 0.4 : 0.25), new Rect(box.X + 1, box.Y + 1, Math.Max(1, box.Width - 2), AutoStrip - 1));
        if (b.LoopSec == null) return;
        // a looped block: a seam where each repeat starts, with the loop icon on its strip
        var seam = Palette.Pen(Palette.Alpha(tint, 0.45));
        double passPx = b.PassSec * pps;
        for (int k = Math.Max(1, (int)Math.Floor((HeaderW - x0) / passPx)); k < b.Passes; k++)
        {
            double px = Math.Round(x0 + k * passPx) + 0.5;
            if (px > W) break;
            ctx.DrawLine(seam, new Point(px, box.Top), new Point(px, box.Bottom));
            if (passPx >= 14) ctx.DrawText(ToolCursors.LoopGlyph, 8, Palette.Alpha(tint, 0.95), new Point(px + 3, box.Y), Icons);
        }
    }

    /// <summary>
    /// A hovered or picked block's handles, like a clip's (asked by the user, #171): a tab on each edge, centered under
    /// its strip, and the loop icon at the strip's right end. The one a press would grab, or the one being dragged,
    /// lights up. Drawn over the curve, so a point never hides them.
    /// </summary>
    void DrawAutoHandles(DrawingContext ctx, Row r, AutomationBlock b, double y, double h, double W)
    {
        double x0 = X(b.AtSec), x1 = X(b.EndSec);
        if (x1 < HeaderW || x0 > W) return;
        var p = lastPointer;
        bool dragging = gesture == "auto" && b == autoDraft;
        bool hover = gesture == null && p.X >= HeaderW && p.Y >= y && p.Y < y + h && p.X >= x0 - AutoEdge && p.X <= x1 + AutoEdge;
        if (!dragging && !hover && b.Id != selectedBlock) return;
        var lit = dragging ? autoDrag
            : hover && AutoPointAt(r, b, p) < 0 && AutoHandleAt(r, b, p) < 0 ? AutoEdgeAt(r, b, p) : AutoDrag.None;
        var box = new Rect(x0, y + 1, Math.Max(3, x1 - x0), h - 3);
        if (box.Width >= 3 * AutoLoopGrab)
        {
            var color = lit == AutoDrag.Loop ? Palette.Accent : Palette.Alpha(Palette.FgColor, 0.7);
            ctx.DrawText(ToolCursors.LoopGlyph, 8, color, new Point(box.Right - AutoLoopGrab, box.Y), Icons);
        }
        if (box.Width < 4 * AutoEdge) return;
        double cy = (box.Top + AutoStrip + box.Bottom) / 2, th = Math.Round(Math.Min(16, (box.Height - AutoStrip) / 2));
        DrawGripTab(ctx, box.Left + 3.5, cy, th, lit == AutoDrag.Left, Palette.FgColor);
        DrawGripTab(ctx, box.Right - 3.5, cy, th, lit == AutoDrag.Right, Palette.FgColor);
        // a looped block's original keeps its own end tab, at its first seam
        if (b.LoopSec != null && b.PassSec * pps >= 4 * AutoEdge)
            DrawGripTab(ctx, Math.Round(x0 + b.PassSec * pps) - 3, cy, th, lit == AutoDrag.Pass, Palette.FgColor);
    }

    /// <summary>The curve as lines, from the left of the view to its right, holding its ends.</summary>
    void TraceAuto(StreamGeometryContext g, List<AutomationPoint> pts, double y, double h, double W, bool stepped)
    {
        double first = AutoY(y, h, pts[0].Value);
        g.BeginFigure(new Point(Math.Max(HeaderW, X(0)), first), false);
        double prev = first;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            double px = X(p.Sec), py = AutoY(y, h, p.Value);
            if (stepped) g.LineTo(new Point(px, prev));
            else if (i > 0 && (pts[i - 1].Shape != AutoShape.Curve || pts[i - 1].Tension != 0)) BendTo(g, pts[i - 1], p, y, h, W);
            g.LineTo(new Point(px, py));
            prev = py;
        }
        g.LineTo(new Point(W, prev));
        g.EndFigure(false);
    }

    /// <summary>
    /// A shaped stretch, only over what the view shows: a stepped one (hold, stairs, pulses) by its flat pieces with
    /// straight jumps, the rest as short lines, closer for a wave's cycles.
    /// </summary>
    void BendTo(StreamGeometryContext g, AutomationPoint a, AutomationPoint b, double y, double h, double W)
    {
        double x0 = X(a.Sec), x1 = X(b.Sec), width = x1 - x0, lo = HeaderW - AutoBendStep, hi = W + AutoBendStep;
        if (width <= 0) return;
        double Y(double along) => AutoY(y, h, a.Value + (b.Value - a.Value) * along);
        int pieces = AutoShapes.Pieces(a.Shape, a.Tension);
        if (pieces > 0)
        {
            for (int j = 0; j < pieces; j++)
            {
                double px0 = x0 + width * j / pieces, px1 = x0 + width * (j + 1) / pieces;
                if (px1 < lo || px0 > hi) continue;
                double py = Y(AutoShapes.Along(a.Shape, (j + 0.5) / pieces, a.Tension));
                g.LineTo(new Point(Math.Max(px0, lo), py));
                g.LineTo(new Point(Math.Min(px1, hi), py));
            }
            return;
        }
        double cycles = a.Shape switch
        {
            AutoShape.Wave => AutoShapes.Count(a.Tension),
            AutoShape.SmoothStairs => AutoShapes.Count(a.Tension) / 2.0,
            _ => 0,
        };
        double step = cycles > 0 ? Math.Clamp(width / (cycles * AutoCycleSteps), 1, AutoBendStep) : AutoBendStep;
        for (double x = Math.Max(x0, lo); x < Math.Min(x1, hi); x += step)
            g.LineTo(new Point(x, Y(AutoShapes.Along(a.Shape, (x - x0) / width, a.Tension))));
    }

    void DrawAutoPoints(DrawingContext ctx, Row r, AutomationBlock b, double y, double h, double W, Color tint, bool live, bool stepped)
    {
        var pts = b.Points;
        bool over = lastPointer.Y >= y && lastPointer.Y < y + h || b == autoDraft;
        if (!stepped && live)
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                if (!AutoBendable(b, k) || !over && pts[k].Shape == AutoShape.Curve && pts[k].Tension == 0) continue;
                var c = AutoHandle(b, k, y, h);
                if (c.X < HeaderW || c.X > W) continue;
                bool bending = b == autoDraft && autoDrag == AutoDrag.Bend && k == autoPoint;
                ctx.DrawEllipse(bending ? Palette.Fg : Palette.App, Palette.Pen(Palette.Alpha(tint, 1), 1.5), c, 3.5, 3.5);
            }
        for (int k = 0; k < pts.Count; k++)
        {
            if (pts[k].Sec > b.PassSec) continue;
            double px = X(b.AtSec + pts[k].Sec);
            if (px < HeaderW - 4 || px > W + 4) continue;
            double py = AutoY(y, h, pts[k].Value);
            bool held = b == autoDraft && autoDrag == AutoDrag.Point && k == autoPoint;
            ctx.DrawRectangle(live ? held ? Palette.Fg : Palette.Accent : Palette.Faint, Palette.Pen(Palette.App), new Rect(px - 4, py - 4, 8, 8), 2, 2);
            if (held && AutomationValue != null)
            {
                var label = Palette.Text(AutomationValue(OwnerOf(r), r.Lane!, pts[k].Value), 11, Palette.Fg, Palette.Mono);
                double lx = Math.Min(px + 9, W - label.Width - 8), ly = Math.Clamp(py - label.Height / 2, y + 1, y + h - label.Height - 1);
                ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.8), null, new Rect(lx - 4, ly - 1, label.Width + 8, label.Height + 2), 3, 3);
                ctx.DrawText(label, new Point(lx, ly));
            }
        }
    }

    void DrawAutoHeaders(DrawingContext ctx)
    {
        foreach (var r in rows)
            if (r.Lane != null) DrawAutoHeader(ctx, r);
        for (int i = 0; i < tracks.Count; i++) DrawAutoFold(ctx, AutoFoldRect(i), tracks[i].Track.AutomationFolded);
        foreach (var f in folders) DrawAutoFold(ctx, FolderAutoFoldRect(f), f.AutomationFolded);
    }

    void DrawAutoHeader(DrawingContext ctx, Row r)
    {
        double y = AutoY(r), h = r.H;
        if (y + h < RulerH || y > Bounds.Height) return;
        var owner = OwnerOf(r);
        var lane = r.Lane!;
        var tint = LaneTint(r);
        ctx.FillRectangle(Palette.Panel, new Rect(0, y, HeaderW, h));
        // a folder's lanes sit where its tracks do, one step in
        double ind = r.Folder != null ? Compact ? CompactIndent : FolderIndent : Indent(r.Track), x = 12 + ind;
        // the branch from the track: it says whose child this is
        var branch = Palette.Pen(Palette.Alpha(tint, 0.6), 1.5);
        ctx.DrawLine(branch, new Point(x, y), new Point(x, y + 14));
        ctx.DrawLine(branch, new Point(x, y + 14), new Point(x + 8, y + 14));
        bool live = lane.On && Automations.Exists(owner, lane.Target);
        var chip = AutoChip(r);
        var trash = AutoTrashRect(r);
        string name = AutomationName?.Invoke(owner, lane) ?? lane.Target;
        ctx.DrawText(name, 11.5, live ? Palette.Fg : Palette.Faint, new Point(x + 12, y + 7), Palette.Ui, Math.Max(0, chip.X - x - 16));
        DrawTrash(ctx, trash, Hot(trash));
        bool hot = Hot(chip);
        ctx.DrawRectangle(lane.On ? Palette.AccentSoft : hot ? Palette.Hover : null, Palette.Pen(lane.On ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line), chip, 4, 4);
        var text = Palette.Text("ON", 9.5, lane.On ? Palette.Accent : Palette.Faint, Palette.Mono);
        ctx.DrawText(text, new Point(chip.Center.X - text.Width / 2, chip.Center.Y - text.Height / 2));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + h - 0.5), new Point(HeaderW, y + h - 0.5));
    }

    /// <summary>A fold column (<see cref="AutoFoldColumn"/>): the glyph, if it has room, over the chevron, both one button.</summary>
    void DrawAutoFold(DrawingContext ctx, Rect r, bool folded)
    {
        if (r.Width <= 0 || r.Bottom < RulerH || r.Y > Bounds.Height) return;
        var brush = Hot(r) ? Palette.Fg : Palette.Accent;
        if (r.Height > AutoCaretH) ctx.DrawText(AutoGlyph, 11, brush, new Point(r.X + 2, r.Y + 1), Icons);
        ctx.DrawText(folded ? "" : "", 9, brush, new Point(r.X + 3, r.Bottom - AutoCaretH + 3), Icons);
    }

    /// <summary>Right click: a lane's menu, or a header control asking for its lane. True when it took the click.</summary>
    bool AutoRightPressed(Row? row, int i, Point p)
    {
        if (row is { Lane: { } lane } r)
        {
            var block = p.X >= HeaderW ? AutoBlockAt(lane, Sec(p.X)) : null;
            selectedBlock = block?.Id;
            // on the curve, its shape and nothing else (asked by the user): a point's is the stretch coming into it
            if (block != null && !AutoStepped(r))
            {
                int point = AutoPointAt(r, block, p);
                int stretch = point >= 0 ? -1 : AutoHandleAt(r, block, p) is var k and >= 0 ? k : AutoStretchAt(r, block, p);
                if (point >= 0 && block.Points.Count > 1) stretch = Math.Max(0, point - 1);
                if (stretch >= 0)
                {
                    AutomationShapeRequested?.Invoke(OwnerOf(r), lane, block, stretch, point >= 0, p);
                    return true;
                }
            }
            AutomationContextRequested?.Invoke(OwnerOf(r), lane, block, block == null ? -1 : AutoPointAt(r, block, p), SnapAt(Math.Max(p.X, HeaderW)), p);
            return true;
        }
        if (row is { Folder: { Separator: false } folder } && p.X < HeaderW)
        {
            string? own = FolderMuteRect(folder).Contains(p) ? AutoTarget.Mute
                : FolderShowsControls(folder) && FolderVolumeRect(folder).Contains(p) ? AutoTarget.Volume
                : FolderShowsControls(folder) && FolderPanRect(folder).Contains(p) ? AutoTarget.Pan
                : FolderFxHit(folder, p) is { Dial: true } fh && SendsOf(folder) is var fs && fh.Knob < fs.Length ? AutoTarget.Send(fs[fh.Knob].BusId) : null;
            if (own == null) return false;
            SelectFolder(folder.Id);
            AutomationAsked?.Invoke(folder, own, p);
            return true;
        }
        if (i < 0 || i >= tracks.Count || p.X >= HeaderW) return false;
        string? target = ShowsControls(i) && VolumeRect(i).Contains(p) ? AutoTarget.Volume
            : ShowsControls(i) && PanRect(i).Contains(p) ? AutoTarget.Pan
            : MuteRect(i).Contains(p) ? AutoTarget.Mute
            : FxHit(i, p) is { Dial: true } hit && SendsOf(tracks[i]) is var sends && hit.Knob < sends.Length ? AutoTarget.Send(sends[hit.Knob].BusId) : null;
        if (target == null) return false;
        Select(i);
        AutomationAsked?.Invoke(tracks[i].Track, target, p);
        return true;
    }

    /// <summary>Left press on a lane: its switch, a block's points, handles, strip and edges, or a new block.</summary>
    void AutoPressed(Row r, Point p, PointerPressedEventArgs e)
    {
        var lane = r.Lane!;
        var t = OwnerOf(r);
        if (r.Folder is { } f) SelectFolder(f.Id);
        else if (Selected != r.Track) Select(r.Track);
        if (p.X < HeaderW)
        {
            if (AutoChip(r).Contains(p)) AutomationToggled?.Invoke(t, lane.Id);
            else if (AutoTrashRect(r).Contains(p)) AutomationRemoveRequested?.Invoke(t, lane, AutoTrashRect(r).BottomLeft + new Point(0, 4));
            e.Handled = true;
            return;
        }
        if (AutoBlockAt(lane, Sec(p.X)) is not { } block)
        {
            selectedBlock = null;
            if (e.ClickCount == 2) AutomationBlockCreated?.Invoke(t, lane.Id, SnapAt(p.X));
            e.Handled = true;
            return;
        }
        selectedBlock = block.Id;
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        int point = AutoPointAt(r, block, p);
        int bend = point < 0 && !shift ? AutoHandleAt(r, block, p) : -1;
        if (e.ClickCount == 2 && (point >= 0 && block.Points.Count > 1 || bend >= 0))
        {
            var edited = Automate(block);
            if (bend >= 0) edited.Points[bend].Tension = 0;
            else edited.Points.RemoveAt(point);
            AutomationBlockEdited?.Invoke(t, lane.Id, edited);
            e.Handled = true;
            return;
        }
        autoLane = lane;
        autoBlock = block;
        autoDraft = Automate(block);
        autoOwner = t;
        autoFrom = block.AtSec;
        autoCopy = false;
        autoPassAt = 0;
        autoDestLane = lane;
        autoDestOwner = t;
        // on the top strip Shift copies, as with a clip (#171); inside the block it draws
        var edge = AutoEdgeAt(r, block, p);
        autoDrag = point >= 0 ? AutoDrag.Point : bend >= 0 ? AutoDrag.Bend : shift && edge != AutoDrag.Move ? AutoDrag.Draw : edge;
        bool added = autoDrag is AutoDrag.None or AutoDrag.Draw;
        if (added)
        {
            if (autoDrag == AutoDrag.None) autoDrag = AutoDrag.Point;
            // in a repeat the point goes into the pass, so every repeat gets it
            double at = SnapAt(p.X) - block.AtSec;
            autoPassAt = at - block.InPass(at);
            point = PutAutoPoint(at - autoPassAt, AutoValueAt(r, p));
        }
        else if (autoDrag == AutoDrag.Move && (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0)
        {
            autoCopy = true;
            autoDraft = block.Copy(block.AtSec);
        }
        autoPoint = bend >= 0 ? bend : point;
        autoPen = point >= 0 ? autoDraft.Points[point].Sec : 0;
        autoTension = bend >= 0 ? autoDraft.Points[bend].Tension : 0;
        StartGesture("auto", e);
        moved = added; // a new point is an edit even if it does not move
    }

    /// <summary>A block to work on: the same id and points, none of them the project's.</summary>
    static AutomationBlock Automate(AutomationBlock b)
    {
        var c = b.Copy(b.AtSec);
        c.AtSec = b.AtSec;
        c.Id = b.Id;
        c.Extra = b.Extra;
        return c;
    }

    double AutoValueAt(Row r, Point p)
    {
        double k = AutoPosition(AutoY(r), r.H, p.Y);
        return Math.Round(AutoStepped(r) ? Math.Round(k) : k, 4);
    }

    /// <summary>A point in the block being edited, in its place. Returns its index.</summary>
    int PutAutoPoint(double sec, double value)
    {
        var list = autoDraft!.Points;
        sec = Math.Clamp(sec, 0, autoDraft.PassSec);
        int at = list.FindIndex(x => x.Sec >= sec);
        if (at < 0) at = list.Count;
        if (at < list.Count && Math.Abs(list[at].Sec - sec) < Automations.MinGapSec) list[at].Value = value;
        // a new point takes the shape of the stretch it splits, as both halves go on with it
        else list.Insert(at, at > 0 ? list[at - 1].At(Math.Round(sec, 3), value) : new AutomationPoint { Sec = Math.Round(sec, 3), Value = value });
        return at;
    }

    void DragAuto()
    {
        if (autoDraft is not { } d || autoLane == null || autoBlock == null) return;
        var r = rows.Find(x => x.Lane == autoLane);
        if (r.Lane == null) return;
        var list = d.Points;
        bool alt = (modifiers & KeyModifiers.Alt) != 0;
        double dx = Sec(lastPointer.X) - Sec(down.X);
        switch (autoDrag)
        {
            case AutoDrag.Move:
                d.AtSec = Math.Max(0, ClipEditing.Snap(autoFrom + dx, Grid, alt));
                // a copy goes to the lane under the pointer; past the lanes it stays in the last one
                if (autoCopy)
                    foreach (var o in rows)
                        if (o.Lane != null && lastPointer.Y >= AutoY(o) && lastPointer.Y < AutoY(o) + o.H)
                        {
                            (autoDestLane, autoDestOwner) = (o.Lane, OwnerOf(o));
                            break;
                        }
                break;
            case AutoDrag.Right:
            case AutoDrag.Loop:
                // the loop's pass is what the block held; ending inside the first pass takes the loop away
                if (autoDrag == AutoDrag.Loop) d.LoopSec = autoBlock.LoopSec ?? autoBlock.LengthSec;
                d.LengthSec = Math.Max(AutomationBlock.MinSec, ClipEditing.Snap(autoBlock.EndSec + dx, Grid, alt) - autoBlock.AtSec);
                if (d.LoopSec is { } pass && d.LengthSec <= pass + 1e-9) d.LoopSec = null;
                break;
            case AutoDrag.Pass when autoBlock.LoopSec is { } held:
                // the original's end: the pass changes, the block keeps how many passes it had
                (d.LoopSec, d.LengthSec) = (held, autoBlock.LengthSec);
                d.SetPass(ClipEditing.Snap(autoBlock.AtSec + held + dx, Grid, alt) - autoBlock.AtSec);
                break;
            case AutoDrag.Left:
            {
                double at = Math.Clamp(ClipEditing.Snap(autoFrom + dx, Grid, alt), 0, autoBlock.EndSec - AutomationBlock.MinSec);
                var trimmed = autoBlock.Cut(at - autoBlock.AtSec, autoBlock.LengthSec, AutoStepped(r));
                (d.AtSec, d.LengthSec, d.Points) = (trimmed.AtSec, trimmed.LengthSec, trimmed.Points);
                break;
            }
            case AutoDrag.Bend:
            {
                var a = list[autoPoint];
                double rise = list[autoPoint + 1].Value - a.Value;
                double along = (AutoPosition(AutoY(r), r.H, lastPointer.Y) - a.Value) / rise;
                // a curve goes through the pointer; the others (the double curve too) count up as it goes up
                a.Tension = Math.Round(a.Shape switch
                {
                    AutoShape.Curve => AutoShapes.TensionThrough(along),
                    AutoShape.Hold => 0,
                    _ => Math.Clamp(autoTension + (down.Y - lastPointer.Y) / (AutoTensionDrag / 2), -1, 1),
                }, 3);
                break;
            }
            case AutoDrag.Draw:
            {
                // the pen replaces what it passes over, one point every AutoDrawGap
                double at = Math.Clamp(Sec(lastPointer.X) - d.AtSec - autoPassAt, 0, d.PassSec);
                if (Math.Abs(at - autoPen) < AutoDrawGap) return;
                double lo = Math.Min(at, autoPen), hi = Math.Max(at, autoPen);
                list.RemoveAll(x => x.Sec > lo && x.Sec < hi);
                autoPoint = PutAutoPoint(at, AutoValueAt(r, lastPointer));
                autoPen = at;
                break;
            }
            case AutoDrag.Point when autoPoint >= 0 && autoPoint < list.Count:
            {
                double min = autoPoint > 0 ? list[autoPoint - 1].Sec + Automations.MinGapSec : 0;
                double max = Math.Min(d.PassSec, autoPoint < list.Count - 1 ? list[autoPoint + 1].Sec - Automations.MinGapSec : double.MaxValue);
                var p = list[autoPoint];
                p.Sec = Math.Round(max > min ? Math.Clamp(SnapAt(lastPointer.X) - d.AtSec - autoPassAt, min, max) : min, 3);
                p.Value = AutoValueAt(r, lastPointer);
                break;
            }
        }
    }

    /// <summary>The gesture ends: the block goes to the window, and the view goes back to the project's.</summary>
    void EndAuto(bool changed)
    {
        var lane = autoCopy ? autoDestLane : autoLane;
        var block = autoDraft;
        var owner = autoCopy ? autoDestOwner : autoOwner;
        // a copy into an on/off or stepped parameter takes whole values, as a point drawn there would
        if (autoCopy && block != null && owner != null && lane != null && lane != autoLane && Automations.Stepped(owner, lane.Target))
            foreach (var pt in block.Points) pt.Value = Math.Round(pt.Value);
        autoDestLane = null;
        autoDestOwner = null;
        autoLane = null;
        autoBlock = autoDraft = null;
        autoDrag = AutoDrag.None;
        autoPoint = -1;
        autoOwner = null;
        autoCopy = false;
        if (!changed || lane == null || block == null || owner == null) return;
        selectedBlock = block.Id;
        AutomationBlockEdited?.Invoke(owner, lane.Id, block);
    }
}
