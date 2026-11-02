using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The strip of detected chords, under the markers' (asked by the user): each chord is a block from where it starts to
/// where it ends, so they never overlap the user's markers or each other. It only takes room while it is shown and the
/// project has chords. A click jumps and marks one, Ctrl+click adds or takes out, a drag marks all it touches; what is
/// marked goes with Supr or the menu. Pressing anywhere else clears the marks, so Supr can't take chords by surprise.
/// Both strips hide from the buttons by the tool strip, and the ruler moves up with them (<see cref="RulerH"/>).
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>Height of the chords' strip, and of the ruler's numbers under the strips.</summary>
    public const double ChordH = 18, RulerNumbersH = 32;
    /// <summary>Px kept around a chord's name: under this a block goes without it.</summary>
    const double ChordTextPad = 8;

    static readonly string ChordsLabel = Loc.T("chords.strip");

    IReadOnlyList<ChordMarker> chords = [];
    readonly HashSet<string> chordMarks = [];
    /// <summary>The marks a Ctrl+drag adds to.</summary>
    HashSet<string> chordMarkBase = [];
    bool markerStripShown = true, chordStripShown = true;

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

    public bool MarkerStripShown { get => markerStripShown; set { markerStripShown = value; StripsChanged(); } }
    public bool ChordStripShown { get => chordStripShown; set { chordStripShown = value; StripsChanged(); } }

    /// <summary>The chords marked now, to remove them.</summary>
    public IReadOnlyCollection<string> ChordMarks => chordMarks;

    /// <summary>A right click on the strip: the chord under the pointer (or null) and where.</summary>
    public event Action<ChordMarker?, Point>? ChordContextRequested;

    double MarkerStripH => markerStripShown ? MarkerH : 0;
    bool ChordStripOn => chordStripShown && chords.Count > 0;
    double StripsH => MarkerStripH + (ChordStripOn ? ChordH : 0);
    /// <summary>The strips that are shown and the ruler's numbers under them.</summary>
    public double RulerH => StripsH + RulerNumbersH;

    void StripsChanged()
    {
        ClampScroll();
        InvalidateVisual();
        // the playhead's head sits on the ruler, and it lives on the layer
        layer.InvalidateVisual();
    }

    bool InChordStrip(Point p) => ChordStripOn && p.X > HeaderW && p.Y >= MarkerStripH && p.Y < StripsH;

    Rect ChordRect(ChordMarker c)
    {
        double x = X(c.Sec);
        return new Rect(x, MarkerStripH + 1, Math.Max(1, X(c.EndSec) - x - 1), ChordH - 2);
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

    /// <summary>A press on the chords' strip; true when it took it. Any other press clears the marks.</summary>
    bool ChordStripPressed(Point p, PointerPressedEventArgs e, bool right)
    {
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
            ChordContextRequested?.Invoke(c, p);
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
        StartGesture("chords", e);
        return true;
    }

    void ChordsMoved()
    {
        if (!moved && Math.Abs(lastPointer.X - down.X) < 3) return;
        moved = true;
        double a = Sec(Math.Min(down.X, lastPointer.X)), b = Sec(Math.Max(down.X, lastPointer.X));
        chordMarks.Clear();
        chordMarks.UnionWith(chordMarkBase);
        foreach (var c in chords)
            if (c.Sec < b && c.EndSec > a) chordMarks.Add(c.Id);
    }

    /// <summary>The gesture ended: without a drag, a chord is marked and the playhead jumps to it.</summary>
    void ChordsReleased(bool changed)
    {
        if (changed) return;
        chordMarks.Clear();
        if (ChordAt(down) is { } c)
        {
            chordMarks.Add(c.Id);
            SeekRequested?.Invoke(c.Sec);
        }
        InvalidateVisual();
    }

    void DrawChordStrip(DrawingContext ctx, double W)
    {
        if (!ChordStripOn) return;
        double top = MarkerStripH;
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, top, W - HeaderW, ChordH));
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7)), new Point(HeaderW, top + ChordH - 0.5), new Point(W, top + ChordH - 0.5));
        using var clip = ctx.PushClip(new Rect(HeaderW, top, Math.Max(0, W - HeaderW), ChordH));
        for (int i = 0; i < chords.Count; i++)
        {
            var c = chords[i];
            var box = ChordRect(c);
            if (box.Right < HeaderW || box.X > W) continue;
            bool marked = chordMarks.Contains(c.Id);
            ctx.DrawRectangle(marked ? Palette.AccentSoft : Palette.Raised, marked ? Palette.Pen(Palette.Accent) : null, box, 3, 3);
            var label = Palette.Text(c.Name, 11, marked ? Palette.Fg : Palette.Dim, Palette.Ui);
            if (label.Width + ChordTextPad <= box.Width)
                ctx.DrawText(label, new Point(Math.Max(box.X + 4, Math.Min(HeaderW + 4, box.Right - label.Width - 4)), top + (ChordH - label.Height) / 2));
        }
    }

    /// <summary>The strip's name in the header column; goes after that column's background.</summary>
    void DrawChordStripLabel(DrawingContext ctx)
    {
        if (!ChordStripOn) return;
        var label = Palette.Text(ChordsLabel, 10.5, Palette.Faint, Palette.Ui);
        ctx.DrawText(label, new Point(HeaderW - label.Width - 8, MarkerStripH + (ChordH - label.Height) / 2));
    }
}
