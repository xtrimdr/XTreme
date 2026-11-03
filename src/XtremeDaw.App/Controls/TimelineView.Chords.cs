using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The strip of chords, under the markers' (asked by the user): each chord is a block from where it starts to where it
/// ends, so they never overlap the user's markers or each other. It takes room while it is shown, with chords or
/// without, so they can be written by hand: a double click on the empty strip opens a name for a new one (up to the
/// next chord, a bar at most), a double click on a chord renames it. A chord drags in time by its body (with the others
/// marked, if it is one of them) and stretches by its edges, snapping like the clips (Alt = free) and never over its
/// neighbours; the drag is a local preview and the window gets it on release, one undo step. A click jumps and marks
/// one, Ctrl+click adds or takes out, a drag on the empty strip marks all it touches; what is marked goes with Supr or the menu. Pressing anywhere else clears the
/// marks, so Supr can't take chords by surprise. The range's, markers' and chords' strips hide from the buttons by the
/// tool strip, and the ruler moves up with them (<see cref="RulerH"/>).
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>Height of the chords' strip, and of the ruler's numbers under the strips.</summary>
    public const double ChordH = 18, RulerNumbersH = 32;
    /// <summary>Px kept around a chord's name: under this a block goes without it.</summary>
    const double ChordTextPad = 8;
    /// <summary>Px from a chord's edge that grab it; a block narrower than three of these only moves.</summary>
    const double ChordEdgeGrab = 5;

    static readonly string ChordsLabel = Loc.T("chords.strip");

    IReadOnlyList<ChordMarker> chords = [];
    readonly HashSet<string> chordMarks = [];
    /// <summary>The marks a Ctrl+drag adds to.</summary>
    HashSet<string> chordMarkBase = [];
    /// <summary>The chords' gesture: "" marks with a box, "move", "start" or "end" edit the grabbed chord.</summary>
    string chordMode = "";
    ChordMarker? chordGrab;
    /// <summary>Where the dragged chords are drawn until the release (id → start, end).</summary>
    readonly Dictionary<string, (double Sec, double End)> chordDrag = [];
    bool rangeStripShown = true, markerStripShown = true, chordStripShown = true;

    /// <summary>The project's chords, in time order (the window sets them after every edit).</summary>
    public IReadOnlyList<ChordMarker> Chords
    {
        get => chords;
        set
        {
            chords = value;
            chordMarks.RemoveWhere(id => !value.Any(c => c.Id == id));
            StripsChanged();
        }
    }

    public bool RangeStripShown { get => rangeStripShown; set { rangeStripShown = value; StripsChanged(); } }
    public bool MarkerStripShown { get => markerStripShown; set { markerStripShown = value; StripsChanged(); } }
    public bool ChordStripShown { get => chordStripShown; set { chordStripShown = value; StripsChanged(); } }

    /// <summary>The chords marked now, to remove them.</summary>
    public IReadOnlyCollection<string> ChordMarks => chordMarks;

    /// <summary>A right click on the strip: the chord under the pointer (or null), the time there and where.</summary>
    public event Action<ChordMarker?, double, Point>? ChordContextRequested;
    /// <summary>A double click on the empty strip: a chord to write there (start, end, where its block goes).</summary>
    public event Action<double, double, Rect>? ChordAddRequested;
    /// <summary>A double click on a chord: the window edits its name in place over that rect.</summary>
    public event Action<string, Rect>? ChordRenameRequested;
    /// <summary>Chords moved or stretched on the strip: their new start and end, by id.</summary>
    public event Action<IReadOnlyDictionary<string, (double Sec, double End)>>? ChordTimesRequested;

    double RangeStripH => rangeStripShown ? RangeH : 0;
    double MarkerStripH => markerStripShown ? MarkerH : 0;
    double MarkerTop => RangeStripH;
    double ChordTop => RangeStripH + MarkerStripH;
    bool ChordStripOn => chordStripShown;
    double StripsH => ChordTop + (ChordStripOn ? ChordH : 0);
    /// <summary>The strips that are shown and the ruler's numbers under them.</summary>
    public double RulerH => StripsH + RulerNumbersH;

    void StripsChanged()
    {
        ClampScroll();
        InvalidateVisual();
        // the playhead's head sits on the ruler, and it lives on the layer
        layer.InvalidateVisual();
    }

    bool InChordStrip(Point p) => ChordStripOn && p.X > HeaderW && p.Y >= ChordTop && p.Y < StripsH;

    Rect ChordRect(ChordMarker c) => chordDrag.TryGetValue(c.Id, out var d) ? ChordBox(d.Sec, d.End) : ChordBox(c.Sec, c.EndSec);

    /// <summary>Which part of the chord under the point is grabbed: "start", "end" or "move".</summary>
    string ChordPartAt(ChordMarker c, Point p)
    {
        var r = ChordRect(c);
        if (r.Width < 3 * ChordEdgeGrab) return "move";
        if (p.X - r.X <= ChordEdgeGrab) return "start";
        if (r.Right - p.X <= ChordEdgeGrab) return "end";
        return "move";
    }

    StandardCursorType ChordCursor(Point p) =>
        ChordAt(p) is not { } c ? StandardCursorType.Arrow
        : ChordPartAt(c, p) switch { "start" => StandardCursorType.LeftSide, "end" => StandardCursorType.RightSide, _ => StandardCursorType.Hand };

    Rect ChordBox(double sec, double end)
    {
        double x = X(sec);
        return new Rect(x, ChordTop + 1, Math.Max(1, X(end) - x - 1), ChordH - 2);
    }

    /// <summary>Where a chord's block is drawn, to rename it in place; null if it is out of sight.</summary>
    public Rect? ChordRectOf(string id) =>
        chords.FirstOrDefault(c => c.Id == id) is { } c && ChordRect(c) is var r && r.Right > HeaderW && r.X < Bounds.Width ? r : null;

    /// <summary>
    /// The room for a new chord at <paramref name="sec"/>: up to the next chord, a bar at most; null when a chord is
    /// already there or the gap is too short.
    /// </summary>
    public (double End, Rect Box)? ChordSlotAt(double sec)
    {
        if (!ChordStripOn || chords.Any(c => c.Sec <= sec && c.EndSec > sec)) return null;
        double end = sec + 4 * 60 / Math.Max(1, Bpm);
        foreach (var c in chords)
            if (c.Sec > sec) { end = Math.Min(end, c.Sec); break; }
        return end - sec >= ProjectMarkers.MinChordSec ? (end, ChordBox(sec, end)) : null;
    }

    ChordMarker? ChordAt(Point p)
    {
        if (!InChordStrip(p)) return null;
        foreach (var c in chords)
            if (ChordRect(c).Contains(p)) return c;
        return null;
    }

    public void ClearChordMarks()
    {
        if (chordMarks.Count == 0) return;
        chordMarks.Clear();
        InvalidateVisual();
    }

    /// <summary>Drops a chord's drag that never got its release (Esc, the pointer taken away).</summary>
    void CancelChordDrag()
    {
        chordGrab = null;
        chordMode = "";
        if (chordDrag.Count == 0) return;
        chordDrag.Clear();
        InvalidateVisual();
    }

    /// <summary>A press on the chords' strip; true when it took it. Any other press clears the marks.</summary>
    bool ChordStripPressed(Point p, PointerPressedEventArgs e, bool right)
    {
        CancelChordDrag();
        if (!InChordStrip(p))
        {
            ClearChordMarks();
            return false;
        }
        var c = ChordAt(p);
        if (right)
        {
            if (c != null && !chordMarks.Contains(c.Id))
            {
                chordMarks.Clear();
                chordMarks.Add(c.Id);
                InvalidateVisual();
            }
            ChordContextRequested?.Invoke(c, SnapAt(p.X), p);
            e.Handled = true;
            return true;
        }
        if (e.ClickCount == 2)
        {
            if (c != null) ChordRenameRequested?.Invoke(c.Id, ChordRect(c));
            else if (ChordSlotAt(SnapAt(p.X)) is { } slot) ChordAddRequested?.Invoke(SnapAt(p.X), slot.End, slot.Box);
            e.Handled = true;
            return true;
        }
        bool add = (modifiers & KeyModifiers.Control) != 0;
        if (add && c != null)
        {
            if (!chordMarks.Remove(c.Id)) chordMarks.Add(c.Id);
            InvalidateVisual();
            e.Handled = true;
            return true;
        }
        chordMarkBase = add ? [.. chordMarks] : [];
        chordGrab = c;
        chordMode = c != null ? ChordPartAt(c, p) : "";
        StartGesture("chords", e);
        return true;
    }

    void ChordsMoved()
    {
        if (!moved && Math.Abs(lastPointer.X - down.X) < 3) return;
        moved = true;
        if (chordGrab is { } g && chordMode.Length > 0)
        {
            DragChords(g);
            return;
        }
        double a = Sec(Math.Min(down.X, lastPointer.X)), b = Sec(Math.Max(down.X, lastPointer.X));
        chordMarks.Clear();
        chordMarks.UnionWith(chordMarkBase);
        foreach (var c in chords)
            if (c.Sec < b && c.EndSec > a) chordMarks.Add(c.Id);
    }

    /// <summary>
    /// The preview of a chord's drag: the body moves it (and the other marked ones, if it is marked) as far as the
    /// chords around let; an edge moves only that edge, between its neighbour and a minimum length.
    /// </summary>
    void DragChords(ChordMarker g)
    {
        bool free = (modifiers & KeyModifiers.Alt) != 0;
        chordDrag.Clear();
        if (chordMode == "move")
        {
            bool group = chordMarks.Contains(g.Id) && chordMarks.Count > 1;
            double d = ClipEditing.Snap(g.Sec + Sec(lastPointer.X) - Sec(down.X), Grid, free) - g.Sec;
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            foreach (var c in chords)
            {
                if (c.Id != g.Id && !(group && chordMarks.Contains(c.Id))) continue;
                lo = Math.Max(lo, -c.Sec);
                foreach (var o in chords)
                {
                    if (o.Id == g.Id || group && chordMarks.Contains(o.Id)) continue;
                    if (o.EndSec <= c.Sec) lo = Math.Max(lo, o.EndSec - c.Sec);
                    else if (o.Sec >= c.EndSec) hi = Math.Min(hi, o.Sec - c.EndSec);
                }
            }
            d = hi < lo ? 0 : Math.Clamp(d, lo, hi);
            foreach (var c in chords)
                if (c.Id == g.Id || group && chordMarks.Contains(c.Id)) chordDrag[c.Id] = (c.Sec + d, c.EndSec + d);
        }
        else
        {
            double at = SnapAt(lastPointer.X), prevEnd = 0, nextSec = double.PositiveInfinity;
            foreach (var o in chords)
            {
                if (o.Id == g.Id) continue;
                if (o.EndSec <= g.Sec) prevEnd = Math.Max(prevEnd, o.EndSec);
                else if (o.Sec >= g.EndSec) nextSec = Math.Min(nextSec, o.Sec);
            }
            chordDrag[g.Id] = chordMode == "start"
                ? (Math.Clamp(at, prevEnd, g.EndSec - ProjectMarkers.MinChordSec), g.EndSec)
                : (g.Sec, Math.Clamp(at, g.Sec + ProjectMarkers.MinChordSec, nextSec));
        }
        InvalidateVisual();
    }

    /// <summary>The gesture ended: a chord's drag goes to the window; without a drag, a chord is marked and the playhead jumps to it.</summary>
    void ChordsReleased(bool changed)
    {
        var grab = chordGrab;
        chordGrab = null;
        chordMode = "";
        if (chordDrag.Count > 0)
        {
            var times = new Dictionary<string, (double Sec, double End)>(chordDrag);
            chordDrag.Clear();
            bool same = times.All(t => chords.FirstOrDefault(c => c.Id == t.Key) is { } c && c.Sec == t.Value.Sec && c.EndSec == t.Value.End);
            if (changed && !same) ChordTimesRequested?.Invoke(times);
            InvalidateVisual();
            if (changed) return;
        }
        if (changed) return;
        chordMarks.Clear();
        if ((grab ?? ChordAt(down)) is { } c)
        {
            chordMarks.Add(c.Id);
            SeekRequested?.Invoke(c.Sec);
        }
        InvalidateVisual();
    }

    void DrawChordStrip(DrawingContext ctx, double W)
    {
        if (!ChordStripOn) return;
        double top = ChordTop;
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, top, W - HeaderW, ChordH));
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7)), new Point(HeaderW, top + ChordH - 0.5), new Point(W, top + ChordH - 0.5));
        using var clip = ctx.PushClip(new Rect(HeaderW, top, Math.Max(0, W - HeaderW), ChordH));
        for (int i = 0; i < chords.Count; i++)
        {
            var c = chords[i];
            var box = ChordRect(c);
            if (box.Right < HeaderW || box.X > W || c.Id == editingNameId) continue;
            bool marked = chordMarks.Contains(c.Id);
            ctx.DrawRectangle(marked ? Palette.AccentSoft : Palette.Raised, marked ? Palette.Pen(Palette.Accent) : null, box, 3, 3);
            var label = Palette.Text(c.Name, 11, marked ? Palette.Fg : Palette.Dim, Palette.Ui);
            if (label.Width + ChordTextPad <= box.Width)
                ctx.DrawText(label, new Point(Math.Max(box.X + 4, Math.Min(HeaderW + 4, box.Right - label.Width - 4)), top + (ChordH - label.Height) / 2));
        }
    }

    static readonly string RangeLabel = Loc.T("range.strip"), MarkersLabel = Loc.T("markers.strip");

    /// <summary>The strips' names in the header column; goes after that column's background.</summary>
    void DrawStripLabels(DrawingContext ctx)
    {
        if (rangeStripShown) Label(RangeLabel, 0, RangeH);
        if (markerStripShown) Label(MarkersLabel, MarkerTop, MarkerH);
        if (ChordStripOn) Label(ChordsLabel, ChordTop, ChordH);

        void Label(string text, double top, double h)
        {
            var label = Palette.Text(text, 10.5, Palette.Faint, Palette.Ui);
            ctx.DrawText(label, new Point(HeaderW - label.Width - 8, top + (h - label.Height) / 2));
        }
    }
}
