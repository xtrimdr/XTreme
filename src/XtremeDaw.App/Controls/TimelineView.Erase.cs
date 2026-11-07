using Avalonia;
using Avalonia.Input;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The right drag erases anything it goes over, wherever it starts (#268: one rule for all, as in FL Studio): clips
/// (TimelineView.Tools.cs), automation blocks (TimelineView.Automation.cs) and, here, what the strips above the lanes
/// hold: the loop range, markers, chords, meter changes and tempo points. A right click that does not move opens that
/// place's menu on release. What a drag takes is hidden until release, when it goes in one undo step.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>Markers (by id) a right drag went over. A loop range it went over goes by <see cref="LoopClearRequested"/>.</summary>
    public event Action<IReadOnlyList<string>>? MarkersErased;
    /// <summary>Chords (by id) a right drag went over.</summary>
    public event Action<IReadOnlyList<string>>? ChordsErased;
    /// <summary>Tempo points (by beat) and meter changes (by bar) a right drag went over; the first of each stays.</summary>
    public event Action<IReadOnlyList<double>, IReadOnlyList<int>>? TempoErased;

    enum EraseStrip { None, Range, Marker, Chord, Meter, Tempo }

    // the strip a right drag started on and what it went over
    EraseStrip eraseStrip;
    readonly HashSet<string> stripErasing = [];
    readonly HashSet<double> tempoErasing = [];
    readonly HashSet<int> meterErasing = [];
    bool loopErasing;
    // where the pointer was at the last erase step: a fast drag erases along the line, skipping nothing
    Point erasePrev;

    /// <summary>A right press on a strip: a drag erases there, a click opens <paramref name="menu"/> on release.</summary>
    void StartStripErase(PointerPressedEventArgs e, EraseStrip strip, Point p, Action menu)
    {
        eraseStrip = strip;
        erasePrev = p;
        StartRightErase(e, menu);
        e.Handled = true;
    }

    /// <summary>One step of the right drag on a strip: what lies between the last pointer and this one, in the strip's band.</summary>
    void DragStripErase()
    {
        Point a = erasePrev, b = lastPointer;
        erasePrev = b;
        double lo = Math.Min(a.X, b.X), hi = Math.Max(a.X, b.X);
        bool any = false;
        switch (eraseStrip)
        {
            case EraseStrip.Range when b.Y < RangeH && loopRange is { } r && !loopErasing:
                if (X(r.End) >= lo && X(r.Start) <= hi) any = loopErasing = true;
                break;
            case EraseStrip.Marker when b.Y >= MarkerTop && b.Y < MarkerTop + MarkerH:
                foreach (var m in markers)
                    if (MarkerRect(m) is var box && box.Right >= lo && box.X <= hi && stripErasing.Add(m.Id)) any = true;
                break;
            case EraseStrip.Chord when b.Y >= ChordTop && b.Y < ChordTop + ChordH:
                foreach (var c in chords)
                    if (ChordRect(c) is var box && box.Right >= lo && box.X <= hi && stripErasing.Add(c.Id)) any = true;
                break;
            case EraseStrip.Meter when b.Y >= TempoTop && b.Y < CurveTop:
            {
                var meters = tempo.Meters;
                for (int i = 1; i < meters.Count; i++)
                {
                    double x = TempoX(tempo.BarStartBeat(meters[i].Bar));
                    if (x >= lo - TempoGrab && x <= hi + TempoGrab && meterErasing.Add(meters[i].Bar)) any = true;
                }
                break;
            }
            case EraseStrip.Tempo:
            {
                var pts = tempo.Points;
                var s = TempoScale();
                for (int i = 1; i < pts.Count; i++)
                {
                    double x = TempoX(pts[i].Beat);
                    if (x < lo - TempoGrab || x > hi + TempoGrab) continue;
                    double k = hi == lo ? 0 : Math.Clamp((x - a.X) / (b.X - a.X), 0, 1);
                    double y = BpmY(pts[i].Bpm, s), ly = a.Y + (b.Y - a.Y) * k;
                    // a steep stretch of the drag reaches the point anywhere between its ends' heights
                    bool near = Math.Abs(y - ly) <= TempoGrab
                        || hi - lo <= TempoGrab * 2 && y >= Math.Min(a.Y, b.Y) - TempoGrab && y <= Math.Max(a.Y, b.Y) + TempoGrab;
                    if (near && tempoErasing.Add(pts[i].Beat)) any = true;
                }
                break;
            }
        }
        if (any) InvalidateVisual();
    }

    /// <summary>The right drag ends: what it went over goes to the window, in one step per kind.</summary>
    void EndRightErase(bool changed)
    {
        EndAutoErase(changed);
        var strip = eraseStrip;
        List<string> ids = [.. stripErasing];
        List<double> beats = [.. tempoErasing];
        List<int> bars = [.. meterErasing];
        bool loop = loopErasing;
        ClearStripErase();
        if (!changed) return;
        switch (strip)
        {
            case EraseStrip.Range when loop: LoopClearRequested?.Invoke(); break;
            case EraseStrip.Marker when ids.Count > 0: MarkersErased?.Invoke(ids); break;
            case EraseStrip.Chord when ids.Count > 0: ChordsErased?.Invoke(ids); break;
            case EraseStrip.Meter or EraseStrip.Tempo when beats.Count > 0 || bars.Count > 0: TempoErased?.Invoke(beats, bars); break;
        }
    }

    /// <summary>Forgets a right drag: the automation's and the strips'.</summary>
    void ClearRightErase()
    {
        if (autoErasing.Count > 0) autoErasing.Clear();
        autoEraseLane = null;
        autoEraseOwner = null;
        ClearStripErase();
    }

    void ClearStripErase()
    {
        eraseStrip = EraseStrip.None;
        if (stripErasing.Count > 0) stripErasing.Clear();
        if (tempoErasing.Count > 0) tempoErasing.Clear();
        if (meterErasing.Count > 0) meterErasing.Clear();
        loopErasing = false;
    }
}
