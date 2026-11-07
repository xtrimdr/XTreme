using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The tempo track (#214, asked by the user, like Fender Studio Pro's): a strip under the ruler's numbers that shows and
/// edits the song's <see cref="TempoMap"/>, folded away from the tool strip's button. On top, a row with the meter
/// changes ("7/8" at their bar); under it, the tempo curve with its points, each a step (it holds and jumps) or a ramp to
/// the next, on a BPM scale that fits the map's tempos (the numbers in the header column). A double click (or a click
/// with the Lápiz) on the curve adds a point at the snapped beat, at the tempo under the pointer; a point drags in time
/// (snapped like the clips, Alt = free, Shift = tempo only) and in tempo, never past its neighbours, and the first one,
/// at the song's start, only in tempo. A click picks a point (Supr removes it); a right click opens its menu, and a
/// right click or double click on the meter row edits the meter of that bar. The drag is a local preview of the strip
/// alone, its points kept where the project's map puts them (only the tempos change, so nothing slides off the grid);
/// the window gets it on release, one undo step, and puts the new map in the whole timeline. The BPM scale stays put
/// while the tempos fit in it, so an edit doesn't squash the curve. The strip's bottom edge drags the curve taller
/// (the window remembers the height). Everything drawn
/// here comes from cached texts and pens: the strip is drawn on every frame the timeline is.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>Height of the meter row; the tempo curve under it is as tall as the user drags its bottom edge.</summary>
    public const double TempoMeterH = 16;
    /// <summary>The curve's height: by default, and the range its bottom edge drags in (#214).</summary>
    public const double TempoCurveDefault = 48, TempoCurveMin = 32, TempoCurveMax = 320;
    /// <summary>Px over the strip's bottom edge that grab it to resize.</summary>
    const double TempoEdgeGrab = 4;
    double tempoCurveH = TempoCurveDefault;
    double TempoCurveH => tempoCurveH;
    double TempoStripH => TempoMeterH + tempoCurveH;
    /// <summary>Px around a point that grab it.</summary>
    const double TempoGrab = 7;
    /// <summary>Px kept free above and under the curve inside its row.</summary>
    const double TempoPad = 5;
    /// <summary>The narrowest BPM span the scale shows, and the room it leaves around the map's tempos.</summary>
    const double TempoSpanMin = 40, TempoMargin = 10;
    /// <summary>Px between the samples of a ramp's curve.</summary>
    const double RampStepPx = 3;
    /// <summary>In beats: the closest a dragged point gets to its neighbours.</summary>
    const double TempoMinGap = 1.0 / 64;

    static readonly string TempoLabel = Loc.T("tempo.strip"), MeterLabel = Loc.T("tempo.meterRow");

    bool tempoStripShown;
    /// <summary>The beat of the point picked with a click (Supr removes it), or null.</summary>
    double? tempoPicked;
    /// <summary>The strip's drag: the point's beat before, where it goes and its tempo, and the map drawn meanwhile.</summary>
    double tempoFromBeat, tempoDragBeat, tempoDragBpm;
    TempoMap? tempoPreview;
    /// <summary>The scale kept during a drag, so the curve doesn't slide under the pointer.</summary>
    (double Lo, double Hi)? tempoHeldScale;

    public bool TempoStripShown
    {
        get => tempoStripShown;
        set
        {
            tempoStripShown = value;
            if (!value) tempoPicked = null;
            tempoKeptScale = null;
            StripsChanged();
        }
    }

    /// <summary>The tempo curve's height (the window restores the one the user left).</summary>
    public double TempoCurveHeight
    {
        get => tempoCurveH;
        set
        {
            double h = Math.Clamp(Math.Round(double.IsFinite(value) ? value : TempoCurveDefault), TempoCurveMin, TempoCurveMax);
            if (h == tempoCurveH) return;
            tempoCurveH = h;
            StripsChanged();
        }
    }

    /// <summary>The curve's bottom edge dragged to a new height (on release), to remember it.</summary>
    public event Action<double>? TempoStripResized;

    /// <summary>The picked tempo point's beat, or null.</summary>
    public double? PickedTempoBeat => tempoPicked;

    /// <summary>A double click (or the Lápiz) on the curve: a new point at that beat and tempo.</summary>
    public event Action<double, double>? TempoPointAdded;
    /// <summary>A point dragged: its beat before, its beat now and its tempo.</summary>
    public event Action<double, double, double>? TempoPointMoved;
    /// <summary>A right click on a point: its beat and where.</summary>
    public event Action<double, Point>? TempoPointMenuRequested;
    /// <summary>A right click on the curve off the points: the snapped beat there, the tempo under the pointer and where.</summary>
    public event Action<double, double, Point>? TempoStripMenuRequested;
    /// <summary>A right click on the meter row: the bar there and where.</summary>
    public event Action<int, Point>? MeterMenuRequested;
    /// <summary>A double click on the meter row: the bar there and where, to change its meter.</summary>
    public event Action<int, Point>? MeterEditRequested;

    double TempoStripHeight => tempoStripShown ? TempoStripH : 0;
    double TempoTop => StripsH + RulerNumbersH;
    double CurveTop => TempoTop + TempoMeterH;
    bool InTempoStrip(Point p) => tempoStripShown && p.X > HeaderW && p.Y >= TempoTop && p.Y < TempoTop + TempoStripH;
    /// <summary>On the strip's bottom edge (header column included), where a drag makes the curve taller or shorter.</summary>
    bool OnTempoEdge(Point p) => tempoStripShown && p.Y >= TempoTop + TempoStripH - TempoEdgeGrab && p.Y < TempoTop + TempoStripH;
    /// <summary>The curve's height when its edge was grabbed.</summary>
    double tempoSizeFrom;

    /// <summary>
    /// Where a beat is drawn: always under the project's map, also while a point is dragged. The preview changes only
    /// the tempos, so the points after the dragged one don't slide away from the grid under the pointer; the timeline
    /// moves once, on release.
    /// </summary>
    double TempoX(double beat) => X(tempo.SecAt(beat));

    /// <summary>The bar (from 0) that holds the time at <paramref name="x"/>.</summary>
    public int BarAtX(double x) => Math.Max(0, tempo.BarAt(tempo.BeatAt(Math.Max(0, Sec(x)))));

    /// <summary>What the strip draws: the dragged preview, else the project's map.</summary>
    TempoMap ShownTempo => tempoPreview ?? tempo;

    /// <summary>The scale last shown: it stays while the map's tempos fit in it, so an edit doesn't rescale the curve.</summary>
    (double Lo, double Hi)? tempoKeptScale;

    /// <summary>
    /// The BPM scale: the one already shown while the map's tempos fit in it with room and it isn't more than twice what
    /// they need; else the map's tempos with some room, in tens, at least <see cref="TempoSpanMin"/> wide.
    /// </summary>
    (double Lo, double Hi) TempoScale()
    {
        if (tempoHeldScale is { } held) return held;
        var need = TempoScaleNeeded();
        if (tempoKeptScale is { } kept && kept.Lo <= need.Lo && kept.Hi >= need.Hi && kept.Hi - kept.Lo <= 2 * (need.Hi - need.Lo))
            return kept;
        tempoKeptScale = need;
        return need;
    }

    (double Lo, double Hi) TempoScaleNeeded()
    {
        var pts = tempo.Points;
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < pts.Count; i++) { min = Math.Min(min, pts[i].Bpm); max = Math.Max(max, pts[i].Bpm); }
        if (pts.Count == 0) min = max = tempo.Bpm;
        double lo = Math.Floor((min - TempoMargin) / 10) * 10, hi = Math.Ceiling((max + TempoMargin) / 10) * 10;
        if (hi - lo < TempoSpanMin)
        {
            double grow = (TempoSpanMin - (hi - lo)) / 2;
            lo -= Math.Ceiling(grow / 10) * 10;
            hi += Math.Ceiling(grow / 10) * 10;
        }
        lo = Math.Max(TempoMap.MinBpm, lo);
        hi = Math.Min(TempoMap.MaxBpm, Math.Max(hi, lo + TempoSpanMin));
        return (lo, hi);
    }

    double BpmY(double bpm, (double Lo, double Hi) s)
    {
        double k = Math.Clamp((s.Hi - bpm) / (s.Hi - s.Lo), 0, 1);
        return CurveTop + TempoPad + k * (TempoCurveH - 2 * TempoPad);
    }

    double YBpm(double y, (double Lo, double Hi) s) =>
        Math.Clamp(s.Hi - (y - CurveTop - TempoPad) / (TempoCurveH - 2 * TempoPad) * (s.Hi - s.Lo), TempoMap.MinBpm, TempoMap.MaxBpm);

    /// <summary>A tempo from the mouse: whole BPM (a typed one can have decimals).</summary>
    static double MouseBpm(double bpm) => Math.Clamp(Math.Round(bpm), TempoMap.MinBpm, TempoMap.MaxBpm);

    /// <summary>The point of the shown map under <paramref name="p"/> (the nearest in reach), or -1.</summary>
    int TempoPointAt(Point p)
    {
        if (!InTempoStrip(p) || p.Y < CurveTop) return -1;
        var map = ShownTempo;
        var s = TempoScale();
        int best = -1;
        double bestD = TempoGrab;
        for (int i = 0; i < map.Points.Count; i++)
        {
            var pt = map.Points[i];
            double dx = Math.Abs(TempoX(pt.Beat) - p.X), dy = Math.Abs(BpmY(pt.Bpm, s) - p.Y);
            if (dx > TempoGrab || dy > TempoGrab) continue;
            double d = Math.Max(dx, dy);
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>The beat a press at <paramref name="x"/> lands on: the magnet's line (Alt = free).</summary>
    double TempoBeatAt(double x) => tempo.BeatAt(SnapAt(x));

    void ClearTempoPick()
    {
        if (tempoPicked == null) return;
        tempoPicked = null;
        InvalidateVisual();
    }

    /// <summary>A new map from the window: the picked point stays picked only if it is still there, and the preview goes.</summary>
    void TempoChanged()
    {
        tempoPreview = null;
        tempoHeldScale = null;
        if (tempoPicked is not { } b) return;
        bool there = false;
        foreach (var pt in tempo.Points) if (Math.Abs(pt.Beat - b) < 1e-9) { there = true; break; }
        if (!there) tempoPicked = null;
    }

    /// <summary>A press on the tempo strip; true when it took it. Any other press clears the picked point.</summary>
    bool TempoStripPressed(Point p, PointerPressedEventArgs e, bool right)
    {
        if (!right && OnTempoEdge(p))
        {
            e.Handled = true;
            tempoSizeFrom = tempoCurveH;
            StartGesture("tempo-size", e);
            return true;
        }
        if (!InTempoStrip(p))
        {
            ClearTempoPick();
            return false;
        }
        e.Handled = true;
        tempoPreview = null;
        tempoHeldScale = null;
        if (p.Y < CurveTop)
        {
            int bar = BarAtX(p.X);
            if (right) StartStripErase(e, EraseStrip.Meter, p, () => MeterMenuRequested?.Invoke(bar, p));
            else if (e.ClickCount == 2) MeterEditRequested?.Invoke(bar, p);
            return true;
        }
        int hit = TempoPointAt(p);
        var s = TempoScale();
        if (right)
        {
            double beat = TempoBeatAt(p.X), bpm = MouseBpm(YBpm(p.Y, s));
            StartStripErase(e, EraseStrip.Tempo, p, () =>
            {
                if (hit >= 0 && hit < tempo.Points.Count)
                {
                    tempoPicked = tempo.Points[hit].Beat;
                    InvalidateVisual();
                    TempoPointMenuRequested?.Invoke(tempoPicked.Value, p);
                }
                else TempoStripMenuRequested?.Invoke(beat, bpm, p);
            });
            return true;
        }
        if (hit >= 0)
        {
            var pt = tempo.Points[hit];
            tempoPicked = tempoFromBeat = tempoDragBeat = pt.Beat;
            tempoDragBpm = pt.Bpm;
            tempoHeldScale = s;
            StartGesture("tempo", e);
            InvalidateVisual();
            return true;
        }
        if (e.ClickCount == 2 || tool == TimelineTool.Draw)
        {
            TempoPointAdded?.Invoke(TempoBeatAt(p.X), MouseBpm(YBpm(p.Y, s)));
            return true;
        }
        ClearTempoPick();
        return true;
    }

    /// <summary>The drag of a point: time on the magnet between its neighbours (the first one stays at 0), tempo by height.</summary>
    void TempoMoved()
    {
        if (!moved && Math.Abs(lastPointer.X - down.X) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
        moved = true;
        var s = TempoScale();
        double bpm = MouseBpm(YBpm(lastPointer.Y, s)), beat = tempoFromBeat;
        bool ramp = false;
        double prev = 0, next = double.PositiveInfinity;
        var pts = tempo.Points;
        for (int i = 0; i < pts.Count; i++)
        {
            if (Math.Abs(pts[i].Beat - tempoFromBeat) < 1e-9) ramp = pts[i].Ramp;
            else if (pts[i].Beat < tempoFromBeat) prev = pts[i].Beat;
            else if (double.IsPositiveInfinity(next)) next = pts[i].Beat;
        }
        if (tempoFromBeat > 0 && (modifiers & KeyModifiers.Shift) == 0)
        {
            double sec = Math.Max(0, ClipEditing.Snap(Sec(lastPointer.X), Grid, (modifiers & KeyModifiers.Alt) != 0));
            beat = Math.Clamp(tempo.BeatAt(sec), prev + TempoMinGap, next - TempoMinGap);
        }
        if (beat == tempoDragBeat && bpm == tempoDragBpm && tempoPreview != null) return;
        tempoDragBeat = beat;
        tempoDragBpm = bpm;
        tempoPreview = tempo.WithoutPoint(tempoFromBeat).WithPoint(beat, bpm, ramp).Prepare();
    }

    /// <summary>The drag's end: the window gets the point's new place and tempo (the preview stays until it sends the new map).</summary>
    void TempoReleased(bool changed)
    {
        bool same = Math.Abs(tempoDragBeat - tempoFromBeat) < 1e-9 && tempo.BpmAt(tempoFromBeat) == tempoDragBpm;
        tempoHeldScale = null;
        if (!changed || same)
        {
            tempoPreview = null;
            return;
        }
        tempoPicked = tempoDragBeat;
        TempoPointMoved?.Invoke(tempoFromBeat, tempoDragBeat, tempoDragBpm);
    }

    /// <summary>The edge's drag: the curve as tall as the pointer says, live.</summary>
    void TempoSizeMoved()
    {
        moved = true;
        TempoCurveHeight = tempoSizeFrom + lastPointer.Y - down.Y;
    }

    void TempoSizeReleased(bool changed)
    {
        if (changed && tempoCurveH != tempoSizeFrom) TempoStripResized?.Invoke(tempoCurveH);
    }

    StandardCursorType TempoCursor(Point p) => p.Y >= CurveTop && TempoPointAt(p) >= 0 ? StandardCursorType.Hand : StandardCursorType.Arrow;

    /// <summary>Tempos as text by tenth of a BPM ("120", "92,5"): the strip is drawn every frame.</summary>
    static readonly Dictionary<long, string> bpmTexts = [];
    static string BpmText(double bpm)
    {
        long key = (long)Math.Round(bpm * 10);
        if (bpmTexts.TryGetValue(key, out var t)) return t;
        if (bpmTexts.Count >= 4096) bpmTexts.Clear();
        return bpmTexts[key] = (key / 10.0).ToString("0.#", CultureInfo.CurrentCulture);
    }

    /// <summary>Meters as text ("7/8"), made once each.</summary>
    static readonly Dictionary<int, string> meterTexts = [];
    static string MeterText(int num, int den)
    {
        int key = num * 1000 + den;
        if (meterTexts.TryGetValue(key, out var t)) return t;
        return meterTexts[key] = $"{num}/{den}";
    }

    void DrawTempoStrip(DrawingContext ctx, double W)
    {
        if (!tempoStripShown) return;
        var map = ShownTempo;
        double top = TempoTop, bottom = top + TempoStripH;
        var line = Palette.Pen(Palette.Line);
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, top, W - HeaderW, TempoStripH));
        ctx.DrawLine(line, new Point(0, top - 0.5), new Point(W, top - 0.5));
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7)), new Point(HeaderW, CurveTop - 0.5), new Point(W, CurveTop - 0.5));
        // the bottom edge, lit while it is grabbed or would be
        if (gesture == "tempo-size" || (gesture == null && OnTempoEdge(lastPointer)))
            ctx.DrawLine(Palette.Pen(Palette.Accent), new Point(0, bottom - 0.5), new Point(W, bottom - 0.5));
        using var clip = ctx.PushClip(new Rect(HeaderW, top, Math.Max(0, W - HeaderW), TempoStripH));

        // the meter changes, the first one kept in sight while its bars are on screen
        var meters = map.Meters;
        var tickPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 1));
        for (int i = 0; i < meters.Count; i++)
        {
            double x = TempoX(map.BarStartBeat(meters[i].Bar));
            if (x > W) break;
            double next = i + 1 < meters.Count ? TempoX(map.BarStartBeat(meters[i + 1].Bar)) : double.PositiveInfinity;
            if (next < HeaderW || meterErasing.Contains(meters[i].Bar)) continue;
            var label = Palette.Text(MeterText(meters[i].Num, meters[i].Den), 11, i == 0 ? Palette.Dim : Palette.Fg, Palette.Mono);
            if (x >= HeaderW) ctx.DrawLine(tickPen, new Point(Math.Round(x) + 0.5, top + 2), new Point(Math.Round(x) + 0.5, CurveTop));
            double lx = Math.Min(Math.Max(x, HeaderW) + 4, next - label.Width - 6);
            if (lx >= x + 3 || x >= HeaderW) ctx.DrawText(label, new Point(Math.Max(lx, x + 4), top + (TempoMeterH - label.Height) / 2));
        }

        // the curve: a step holds and jumps, a ramp goes straight in beats (so it bends a little in time)
        var s = TempoScale();
        var accent = Palette.AccentColor;
        var fill = Palette.Alpha(accent, 0.10);
        var curve = Palette.Pen(Palette.Alpha(accent, 0.9), 1.5);
        var pts = map.Points;
        double floor = bottom;
        for (int i = 0; i < pts.Count; i++)
        {
            double x0 = TempoX(pts[i].Beat);
            if (x0 > W) break;
            bool last = i + 1 >= pts.Count;
            double x1 = last ? W + 1 : TempoX(pts[i + 1].Beat);
            if (x1 < HeaderW) continue;
            double y0 = BpmY(pts[i].Bpm, s);
            if (pts[i].Ramp && !last)
            {
                double xa = Math.Max(x0, HeaderW - RampStepPx), xb = Math.Min(x1, W + RampStepPx);
                double px = xa, py = BpmY(map.BpmAt(tempo.BeatAt(Sec(xa))), s);
                while (px < xb)
                {
                    double nx = Math.Min(xb, px + RampStepPx), ny = BpmY(map.BpmAt(tempo.BeatAt(Sec(nx))), s);
                    ctx.FillRectangle(fill, new Rect(px, Math.Min(py, ny), nx - px, Math.Max(0, floor - Math.Min(py, ny))));
                    ctx.DrawLine(curve, new Point(px, py), new Point(nx, ny));
                    px = nx;
                    py = ny;
                }
            }
            else
            {
                double xa = Math.Max(x0, HeaderW - 1), xb = Math.Min(x1, W + 1);
                ctx.FillRectangle(fill, new Rect(xa, y0, Math.Max(0, xb - xa), Math.Max(0, floor - y0)));
                ctx.DrawLine(curve, new Point(xa, y0), new Point(xb, y0));
                if (!last) ctx.DrawLine(curve, new Point(x1, y0), new Point(x1, BpmY(pts[i + 1].Bpm, s)));
            }
        }

        // the points, with their tempo beside them when there is room
        bool dragging = gesture == "tempo" && moved;
        int hover = gesture == null ? TempoPointAt(lastPointer) : -1;
        for (int i = 0; i < pts.Count; i++)
        {
            double x = TempoX(pts[i].Beat);
            if (x > W + TempoGrab) break;
            double nextX = i + 1 < pts.Count ? TempoX(pts[i + 1].Beat) : double.PositiveInfinity;
            if (x < HeaderW - TempoGrab && nextX < HeaderW || tempoErasing.Contains(pts[i].Beat)) continue;
            double y = BpmY(pts[i].Bpm, s);
            bool picked = dragging ? Math.Abs(pts[i].Beat - tempoDragBeat) < 1e-9 : tempoPicked is { } pb && Math.Abs(pb - pts[i].Beat) < 1e-9;
            double r = picked || i == hover ? 4.5 : 3.5;
            ctx.DrawEllipse(picked ? Palette.Accent : Palette.Panel, Palette.Pen(Palette.Accent, 1.5), new Point(x, y), r, r);
            var text = Palette.Text(BpmText(pts[i].Bpm), 10, picked ? Palette.Fg : Palette.Dim, Palette.Mono);
            if (nextX - x < text.Width + 14 && !picked) continue;
            double ty = y - text.Height - 3 < CurveTop + 1 ? y + 4 : y - text.Height - 3;
            ctx.DrawText(text, new Point(Math.Max(x, HeaderW) + 6, ty));
        }
    }

    /// <summary>The tempo strip's names and its scale in the header column; goes after that column's background.</summary>
    void DrawTempoLabels(DrawingContext ctx)
    {
        if (!tempoStripShown) return;
        double right = HeaderW - 8;
        var meter = Palette.Text(MeterLabel, 10.5, Palette.Faint, Palette.Ui);
        ctx.DrawText(meter, new Point(right - meter.Width, TempoTop + (TempoMeterH - meter.Height) / 2));
        var s = TempoScale();
        var hi = Palette.Text(BpmText(s.Hi), 10, Palette.Faint, Palette.Mono);
        var lo = Palette.Text(BpmText(s.Lo), 10, Palette.Faint, Palette.Mono);
        ctx.DrawText(hi, new Point(right - hi.Width, CurveTop + 1));
        ctx.DrawText(lo, new Point(right - lo.Width, CurveTop + TempoCurveH - lo.Height - 1));
        var name = Palette.Text(TempoLabel, 10.5, Palette.Faint, Palette.Ui);
        double nx = right - Math.Max(hi.Width, lo.Width) - 8 - name.Width;
        if (nx > 8) ctx.DrawText(name, new Point(nx, CurveTop + (TempoCurveH - name.Height) / 2));
    }
}
