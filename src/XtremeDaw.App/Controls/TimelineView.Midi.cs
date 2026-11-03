using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The clips of MIDI tracks (native only, stage 1 of the MIDI editor): a rounded box in the track's color with its
/// notes drawn small inside, between the clip's lowest and highest pitch. A click selects one, its body moves it (to
/// another time and another MIDI track, with the grid's magnet; Alt, free), its right edge changes its length by grid
/// steps, a double click opens it in the piano roll and a double click on an empty stretch of the lane makes a new one
/// there. Ctrl+click and the selection box pick several, takes and MIDI clips together (asked by the user): grabbing one
/// of them by its body carries them all by the same time, each on its own track (<see cref="ClipsMoved"/>). A pattern's
/// clip shows its name and every pass, a seam between them, and its right edge adds passes. The clips of a pattern's
/// other instruments that start with it go along (<see cref="UseOf"/>): moved by its body as a carry, resized by its edge
/// unless Shift is held. The window does the edit (<see cref="MidiClipChanged"/>): the control never touches the project.
/// <para>
/// What belongs together shows (asked by the user): a pattern's clip carries a link icon, the instruments of one use are
/// joined by a line between their clips, and pointing at a clip or selecting it outlines every clip of its pattern.
/// </para>
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>The MIDI clip selected (its track is <see cref="Selected"/>), or null.</summary>
    public string? SelectedMidiId { get; private set; }
    /// <summary>A MIDI clip moved, resized or carried to another track: its id, start, length in beats and new track (null = the same).</summary>
    public event Action<string, double, double, string?>? MidiClipChanged;
    /// <summary>A MIDI clip's start trimmed on its left edge: its id and the new start (its end stays where it was).</summary>
    public event Action<string, double>? MidiClipTrimmed;
    /// <summary>A MIDI clip of its own looped from its top right corner: its id and its new length in beats (<see cref="MidiEditing.Loop"/>).</summary>
    public event Action<string, double>? MidiClipLooped;
    /// <summary>A double click on a MIDI clip: open it in the piano roll.</summary>
    public event Action<LiveTrack, string>? MidiClipOpenRequested;
    /// <summary>A double click on an empty stretch of a MIDI lane: a new clip there (seconds, already snapped).</summary>
    public event Action<LiveTrack, double>? MidiClipCreateRequested;
    /// <summary>A right click on a MIDI clip: its menu.</summary>
    public event Action<LiveTrack, string, Point>? MidiClipContextRequested;
    /// <summary>The instrument chip's arrow (or the chip of a track without a working plugin): the instrument list, under it.</summary>
    public event Action<LiveTrack, Point>? InstrumentMenuRequested;
    /// <summary>The instrument chip of a track whose plugin is up: its window.</summary>
    public event Action<LiveTrack>? InstrumentWindowRequested;
    /// <summary>How each MIDI track's instrument is doing, by track id (the window knows its processes).</summary>
    public Func<string, InstrumentState>? InstrumentStateOf { get; set; }

    public enum InstrumentState { TestTone, Loading, Ready, Failed }

    /// <summary>The width of the chip's arrow, which always opens the list.</summary>
    const double ChipArrowW = 20;
    /// <summary>The chip's limits: below <see cref="ChipTextW"/> a name wouldn't read, so it folds to just its arrow,
    /// <see cref="ChipFoldedW"/> wide.</summary>
    const double ChipMaxW = 170, ChipTextW = 56, ChipFoldedW = 24;
    /// <summary>What the name keeps beside a chip that shows its text.</summary>
    const double ChipNameW = 70;

    /// <summary>
    /// A MIDI track's instrument chip, in the header's first row (asked by the user: the instrument shows on the channel
    /// and changes from there): from the name to just before the ● (an armed MIDI track records the keyboard; armed,
    /// before its input chip). It never overlaps them: with no room it folds to its arrow, and with no room for that
    /// (an armed track in a folder at the narrowest full column) it goes, like in a compact header.
    /// </summary>
    Rect InstrumentRect(int i)
    {
        if (Compact) return NoRect;
        double right = armed.Contains(i) && !Recording && InputLabel != null ? InputRect(i).X - 4 : ArmRect(i).X - 6;
        double w = Math.Min(ChipMaxW, right - (58 + Indent(i) + NameShift(i) + ChipNameW));
        if (w < ChipTextW) w = ChipFoldedW;
        return right - w < BadgeRect(i).Right ? NoRect : new(right - w, LaneY(i) + 8, w, 24);
    }

    InstrumentState StateOf(LiveTrack t) =>
        InstrumentStateOf?.Invoke(t.Track.Id) ?? (t.Track.Instrument == null && t.Track.Sampler == null ? InstrumentState.TestTone : InstrumentState.Loading);

    /// <summary>The chip: the plugin's name (accent once it plays, dim while it loads, amber when it couldn't open) or "Tono de prueba", and its arrow.</summary>
    void DrawInstrumentChip(DrawingContext ctx, int i)
    {
        var t = tracks[i];
        var r = InstrumentRect(i);
        var state = StateOf(t);
        bool ready = state == InstrumentState.Ready, hot = Hot(r);
        var fill = ready ? Palette.AccentSoft : hot ? Palette.Hover : Palette.Raised;
        var pen = Palette.Pen(ready ? Palette.Alpha(Palette.AccentColor, hot ? 0.8 : 0.4) : Palette.Line);
        ctx.DrawRectangle(fill, pen, r, 6, 6);
        if (r.Width <= ChipFoldedW)
        {
            DrawChevron(ctx, new Point(r.Center.X, r.Center.Y + 1), ready ? Palette.Accent : Palette.Dim);
            return;
        }
        var brush = state switch
        {
            InstrumentState.Ready => Palette.Accent, InstrumentState.Loading => Palette.Dim,
            InstrumentState.Failed => Palette.Mute, _ => Palette.Faint,
        };
        string name = t.Track.Sampler != null ? Loc.T("sampler.name") : t.Track.Instrument?.Name ?? Loc.T("vst.testTone");
        ctx.DrawText(name, 11, brush, new Point(r.X + 8, r.Y + 5), ready ? Palette.UiBold : Palette.Ui, Math.Max(0, r.Width - ChipArrowW - 10));
        double ax = r.Right - ChipArrowW;
        ctx.DrawLine(Palette.Pen(ready ? Palette.Alpha(Palette.AccentColor, 0.3) : Palette.Line), new Point(ax + 0.5, r.Y + 5), new Point(ax + 0.5, r.Bottom - 5));
        DrawChevron(ctx, new Point(ax + ChipArrowW / 2, r.Center.Y + 1), ready ? Palette.Accent : Palette.Dim);
    }

    /// <summary>The arrow of a header's select (the instrument and input chips): its tip at <paramref name="c"/>.</summary>
    static void DrawChevron(DrawingContext ctx, Point c, IBrush brush)
    {
        var arrow = Palette.Pen(brush, 1.5, PenLineCap.Round);
        ctx.DrawLine(arrow, new Point(c.X - 3.5, c.Y - 2), c);
        ctx.DrawLine(arrow, c, new Point(c.X + 3.5, c.Y - 2));
    }

    /// <summary>A press on the chip: the arrow (all there is of a folded chip, or a track without a working plugin) asks
    /// for the list, the rest opens the plugin's window.</summary>
    void InstrumentChipPressed(int i, Point p, bool right)
    {
        var t = tracks[i];
        var r = InstrumentRect(i);
        if (!right && r.Width > ChipFoldedW && p.X < r.Right - ChipArrowW && StateOf(t) == InstrumentState.Ready) InstrumentWindowRequested?.Invoke(t);
        else InstrumentMenuRequested?.Invoke(t, r.BottomLeft + new Point(0, 4));
    }

    // the "midi" gesture: the clip held, its start and length when it was grabbed and now, and whether it is the edge
    MidiClip? midiHeld;
    double midiFromSec, midiFromBeats, midiAtSec, midiBeats;
    bool midiEdge;
    /// <summary>The held clip is grabbed by its left edge: its start moves and its end stays (not on a pattern's clip).</summary>
    bool midiTrimStart;
    /// <summary>The held clip is grabbed by its loop corner: it resizes like the edge, and what it held repeats.</summary>
    bool midiLoop;
    /// <summary>The MIDI track the held clip is over, or -1.</summary>
    int midiOver = -1;
    /// <summary>The held MIDI clip is one of several boxed ones: it carries them all.</summary>
    bool midiCarrying;
    /// <summary>The boxed MIDI clips a carry moves (whichever clip was grabbed), with where each one started.</summary>
    readonly Dictionary<MidiClip, double> midiCarried = [];
    /// <summary>How far a carry moves everything (seconds), already snapped and kept from crossing 0.</summary>
    double carryShift;
    /// <summary>What was selected when a Ctrl+click on a take began: on release without moving, the take is added or taken out.</summary>
    HashSet<string>? toggleBase;

    /// <summary>The other instruments of the pattern's use held by its edge, as long as it: they take its length. Shift leaves them.</summary>
    readonly HashSet<MidiClip> midiAlong = [];
    /// <summary>The clips <see cref="midiAlong"/> ended up resizing, read by the handler of <see cref="MidiClipChanged"/>.</summary>
    List<string> resizedAlong = [];

    /// <summary>Px from a MIDI clip's right edge that grab it to change its length.</summary>
    const double MidiEdgeGrab = 7;
    /// <summary>The link icon of a pattern's clip.</summary>
    const string PatternGlyph = "";
    /// <summary>How far inside its clips a pattern's joining line runs, in px.</summary>
    const double LinkInset = 4;

    /// <summary>How far a pattern's link bows out to hop over the lanes between two of its clips, and its curve's steps.</summary>
    const double HopBow = 7;
    const int HopSteps = 12;

    /// <summary>The pattern of the clip under the pointer.</summary>
    string? hoverPattern;
    /// <summary>The pattern outlined on this frame: the one pointed at, or the selected clip's.</summary>
    string? litPattern;

    /// <summary>The pattern pointed at, while nothing is being dragged.</summary>
    void UpdatePatternHover() => hoverPattern = lastPointer.X >= HeaderW && lastPointer.Y >= RulerH ? MidiClipAt(HitTrack(lastPointer.Y), lastPointer.X)?.PatternId : null;

    string? SelectedPattern()
    {
        if (SelectedMidiId == null) return null;
        // indexed: it runs on every frame, and a foreach over the track list boxes its enumerator
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Track.MidiClips is { } clips)
                foreach (var c in clips)
                    if (c.Id == SelectedMidiId) return c.PatternId;
        return null;
    }

    static bool SameUse(MidiClip a, MidiClip b) =>
        a.PatternId == b.PatternId && a.PatternPart != b.PatternPart && Math.Abs(a.AtSec - b.AtSec) < MidiPatterns.SameStart;

    /// <summary>The lane's clip that plays with <paramref name="c"/>, or null. No LINQ: it runs on every frame.</summary>
    MidiClip? UseIn(int lane, MidiClip c)
    {
        if (tracks[lane].Track.MidiClips is { } clips && LaneY(lane) > -1e8)
            foreach (var o in clips)
                if (SameUse(c, o)) return o;
        return null;
    }

    /// <summary>
    /// A line down the left of each use of a pattern with several instruments, from the top clip to the last one, with a
    /// dot on each: the clips that go together. Drawn from the top clip of the use only, so each is drawn once.
    /// </summary>
    void DrawPatternLinks(DrawingContext ctx, double W)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Track.MidiClips is not { } clips || LaneY(i) < -1e8) continue;
            foreach (var c in clips)
            {
                if (c.PatternId == null) continue;
                double x = Math.Round(X(MidiSpan(c).Start) + LinkInset) + 0.5;
                if (x < HeaderW || x > W) continue;
                bool top = true;
                for (int k = 0; k < i && top; k++) top = UseIn(k, c) == null;
                if (!top) continue;
                int last = -1;
                for (int k = i + 1; k < tracks.Count; k++)
                    if (UseIn(k, c) != null) last = k;
                if (last < 0) continue;
                bool lit = c.PatternId == litPattern;
                var brush = lit ? Palette.Accent : Palette.Dim;
                var pen = Palette.Pen(brush, lit ? 1.5 : 1);
                var halo = Palette.Pen(Palette.App, lit ? 4 : 3.5);
                double from = LaneY(i) + LaneH(i) - 8, prevY = from;
                int prev = i;
                for (int k = i + 1; k <= last; k++)
                {
                    if (UseIn(k, c) == null) continue;
                    double to = LaneY(k) + 8;
                    DrawHop(ctx, halo, x, prevY, to, k - prev > 1);
                    DrawHop(ctx, pen, x, prevY, to, k - prev > 1);
                    prevY = to;
                    prev = k;
                }
                DrawLinkDot(ctx, brush, x, from);
                for (int k = i + 1; k <= last; k++)
                    if (UseIn(k, c) != null) DrawLinkDot(ctx, brush, x, LaneY(k) + 8);
            }
        }
    }

    /// <summary>
    /// A line from <paramref name="y0"/> down to <paramref name="y1"/>. When lanes sit between the two ends (a hop), it
    /// bows out to the left so it reads as a jump over them instead of vanishing behind their clips.
    /// </summary>
    static void DrawHop(DrawingContext ctx, IPen pen, double x, double y0, double y1, bool hop)
    {
        double bow = hop ? HopBow : 0;
        var prev = new Point(x, y0);
        for (int s = 1; s <= HopSteps; s++)
        {
            double t = s / (double)HopSteps;
            var p = new Point(x - bow * Math.Sin(Math.PI * t), y0 + (y1 - y0) * t);
            ctx.DrawLine(pen, prev, p);
            prev = p;
        }
    }

    /// <summary>A link's dot, ringed in the background so it stays visible over the clips.</summary>
    static void DrawLinkDot(DrawingContext ctx, IBrush brush, double x, double y)
    {
        ctx.DrawEllipse(Palette.App, null, new Point(x, y), 3.5, 3.5);
        ctx.DrawEllipse(brush, null, new Point(x, y), 2.5, 2.5);
    }

    /// <summary>A pattern's link icon and its name, at the clip's top left.</summary>
    static void DrawPatternName(DrawingContext ctx, string name, double x, double y)
    {
        ctx.DrawText(PatternGlyph, 10, Palette.Dim, new Point(x, y + 2), Icons);
        ctx.DrawText(name, 11, Palette.Dim, new Point(x + 14, y), Palette.Ui);
    }

    double BeatSec => 60 / Math.Max(1, Bpm);

    /// <summary>The other instruments of a pattern's use that a gesture on <paramref name="c"/> takes along.</summary>
    List<MidiClip> UseOf(MidiClip c) => MidiPatterns.Together(tracks.Where(t => !t.Track.Locked).Select(t => t.Track), c);

    /// <summary>The clips resized along with the last one, once.</summary>
    public List<string> TakeResizedAlong()
    {
        var taken = resizedAlong;
        resizedAlong = [];
        return taken;
    }

    MidiClip? MidiClipAt(int i, double x)
    {
        if (i < 0 || i >= tracks.Count || tracks[i].Track.MidiClips is not { } clips) return null;
        for (int k = clips.Count - 1; k >= 0; k--)
        {
            var (start, end) = MidiSpan(clips[k]);
            if (x >= X(start) && x <= X(end)) return clips[k];
        }
        return null;
    }

    /// <summary>A clip's start and end as drawn: a carried or held one where the gesture has it.</summary>
    (double Start, double End) MidiSpan(MidiClip c)
    {
        if (moved && midiCarried.TryGetValue(c, out double from)) return (from + carryShift, from + carryShift + c.LengthBeats * BeatSec);
        if (moved && midiAlong.Contains(c)) return (c.AtSec, c.AtSec + midiBeats * BeatSec);
        return c == midiHeld && moved ? (midiAtSec, midiAtSec + midiBeats * BeatSec) : (c.AtSec, c.EndSec(Bpm));
    }

    bool OnMidiEdge(MidiClip c, double x) => Math.Abs(x - X(MidiSpan(c).End)) <= MidiEdgeGrab;

    /// <summary>The loop corner of a clip on lane <paramref name="i"/>: the top of its right end, inside it (like GarageBand).</summary>
    bool OnMidiLoop(int i, MidiClip c, Point p)
    {
        double right = X(MidiSpan(c).End), top = LaneY(i) + 5;
        return !tracks[i].Track.Locked && p.X <= right && right - p.X <= LoopGrab && p.Y >= top && p.Y < top + LoopBand(LaneH(i) - 10);
    }

    /// <summary>The left edge of a clip that can be trimmed from it (a pattern's can't: its notes count from its start).</summary>
    bool OnMidiStartEdge(MidiClip c, double x) => c.PatternId == null && Math.Abs(x - X(MidiSpan(c).Start)) <= MidiEdgeGrab;

    /// <summary>Px from a clip's edge where its handle is drawn: a short tab on each side, centered vertically.</summary>
    const double MidiHandleW = 3, MidiHandleH = 12;

    void DrawMidiClips(DrawingContext ctx, int i, double y, double W)
    {
        var t = tracks[i];
        if (t.Track.MidiClips is not { } clips) return;
        foreach (var c in clips)
            if (c != midiHeld || midiOver == i || midiOver < 0) DrawMidiClip(ctx, t, c, y, RowH(t.Track), W, c == midiHeld && moved);
    }

    /// <summary>The held clip over another MIDI track, drawn there.</summary>
    void DrawMidiCarried(DrawingContext ctx, double W)
    {
        if (midiHeld == null || !moved || midiOver < 0 || midiOver >= tracks.Count || midiOver == fromIndex) return;
        DrawMidiClip(ctx, tracks[midiOver], midiHeld, LaneY(midiOver), LaneH(midiOver), W, true);
    }

    void DrawMidiClip(DrawingContext ctx, LiveTrack t, MidiClip c, double y, double lh, double W, bool held)
    {
        var (start, end) = MidiSpan(c);
        double x0 = X(start), x1 = X(end);
        if (x1 < HeaderW || x0 > W) return;
        bool sel = c.Id == SelectedMidiId || boxed.Contains(c.Id);
        var color = TrackIcons.ColorOf(t.Track) ?? Palette.AccentColor;
        bool silent = t.Silenced || anySoloed && !t.Soloed;
        var r = new Rect(x0, y + 5, Math.Max(2, x1 - x0), lh - 10);
        using var opacity = ctx.PushOpacity((silent ? 0.5 : held ? 0.85 : 1) * (erasing.Contains(c.Id) ? 0.25 : 1));
        bool lit = c.PatternId != null && c.PatternId == litPattern;
        var outline = lit ? Palette.Pen(Palette.Accent, 1.5) : Palette.Pen(Palette.Alpha(color, sel ? 0.95 : 0.45), sel ? 1.5 : 1);
        ctx.DrawRectangle(Palette.Alpha(color, sel ? 0.24 : 0.14), outline, r, 5, 5);
        // the edge handles of a selected clip: the right one changes its length, the left one (not on a pattern) trims it
        if (sel && r.Width >= 3 * MidiHandleW + 2)
        {
            var grip = Palette.Alpha(color, 0.95);
            double cy = r.Center.Y - MidiHandleH / 2;
            ctx.FillRectangle(grip, new Rect(r.Right - MidiHandleW - 2, cy, MidiHandleW, MidiHandleH));
            if (c.PatternId == null) ctx.FillRectangle(grip, new Rect(r.Left + 2, cy, MidiHandleW, MidiHandleH));
        }
        using (ctx.PushClip(new RoundedRect(r, 5)))
        {
            var notes = c.Notes;
            if (notes.Count == 0)
            {
                if (r.Height >= 26 && r.Width > 60)
                {
                    if (c.PatternName is { } empty) DrawPatternName(ctx, empty, Math.Max(x0, HeaderW) + LinkInset + 6, y + 9);
                    else ctx.DrawText(Loc.T("midi.clip.empty"), 11, Palette.Faint, new Point(Math.Max(x0, HeaderW) + 8, y + 9), Palette.Ui);
                }
                return;
            }
            int lo = 127, hi = 0;
            foreach (var n in notes) { lo = Math.Min(lo, n.Pitch); hi = Math.Max(hi, n.Pitch); }
            // a range of at least an octave, so a single note doesn't fill the clip from top to bottom
            int span = Math.Max(12, hi - lo + 1);
            lo -= (span - (hi - lo + 1)) / 2;
            double top = r.Top + 4, h = Math.Max(4, r.Height - 8);
            double rowH = Math.Max(1, Math.Min(4, h / span));
            var fill = Palette.Alpha(color, sel ? 1 : 0.85);
            double beatPx = BeatSec * pps;
            // as the gesture has it: a pattern's or a looped clip dragged longer shows its repeats while it moves
            double length = (end - start) / BeatSec, cell = c.Repeats ? c.CellBeats : held && midiLoop ? midiFromBeats : length;
            var seam = Palette.Pen(Palette.Alpha(color, 0.45));
            for (int pass = Math.Max(0, (int)Math.Floor((HeaderW - x0) / (cell * beatPx))); pass * cell < length - 1e-9; pass++)
            {
                double px = x0 + pass * cell * beatPx;
                if (px > W) break;
                if (pass > 0)
                {
                    ctx.DrawLine(seam, new Point(Math.Round(px) + 0.5, r.Top), new Point(Math.Round(px) + 0.5, r.Bottom));
                    // every repeat says it is one
                    if (cell * beatPx >= 16) ctx.DrawText(ToolCursors.LoopGlyph, 9, Palette.Alpha(color, 0.95), new Point(Math.Round(px) + 3, r.Top + 2), Icons);
                }
                foreach (var n in notes)
                {
                    if (!MidiClip.Slice(n, pass, cell, length, out double from, out double to)) continue;
                    double nx = x0 + from * beatPx;
                    if (nx > W) break;
                    double nw = Math.Max(1.5, (to - from) * beatPx - 1);
                    if (nx + nw < HeaderW) continue;
                    double ny = top + (span - 1 - (n.Pitch - lo)) / (double)span * h;
                    ctx.FillRectangle(fill, new Rect(nx, ny, nw, rowH));
                }
            }
            if (c.PatternName is { } name && r.Height >= 26 && r.Width > 60)
                DrawPatternName(ctx, name, Math.Max(x0, HeaderW) + LinkInset + 6, y + 7);
        }
    }

    /// <summary>A left press on a MIDI lane (past the headers). Always handles it.</summary>
    void MidiPressed(int i, Point p, PointerPressedEventArgs e)
    {
        var c = MidiClipAt(i, p.X);
        if (e.ClickCount == 2)
        {
            if (c != null) MidiClipOpenRequested?.Invoke(tracks[i], c.Id);
            else MidiClipCreateRequested?.Invoke(tracks[i], Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
            e.Handled = true;
            return;
        }
        if (c == null)
        {
            if ((modifiers & KeyModifiers.Control) == 0)
            {
                SelectedMidiId = null;
                Select(i);
            }
            StartBox(e);
            return;
        }
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            ToggleBoxed(c.Id, SelectionSeed());
            e.Handled = true;
            return;
        }
        // the nearer edge wins when both are in reach (a short clip)
        midiLoop = OnMidiLoop(i, c, p);
        midiTrimStart = !midiLoop && OnMidiStartEdge(c, p.X) && !(OnMidiEdge(c, p.X) && X(MidiSpan(c).End) - p.X < p.X - X(MidiSpan(c).Start));
        midiEdge = midiLoop || !midiTrimStart && OnMidiEdge(c, p.X);
        bool edge = midiEdge || midiTrimStart;
        cloning = (modifiers & KeyModifiers.Shift) != 0 && !edge;
        if (midiEdge && (modifiers & KeyModifiers.Shift) == 0)
            midiAlong.UnionWith(UseOf(c).Where(o => Math.Abs(o.LengthBeats - c.LengthBeats) < 1e-9));
        // a boxed clip grabbed by its body carries the other boxed ones; its edge resizes or trims it alone
        midiCarrying = boxed.Count > 1 && boxed.Contains(c.Id) && !edge;
        SelectedMidiId = c.Id;
        if (midiCarrying)
        {
            SelectOne(i);
            StartCarry(null);
        }
        else Select(i);
        midiHeld = c;
        midiFromSec = midiAtSec = c.AtSec;
        midiFromBeats = midiBeats = c.LengthBeats;
        fromIndex = midiOver = i;
        StartGesture("midi", e);
    }

    /// <summary>A right press on a MIDI clip asks for its menu; false when there is none under the pointer.</summary>
    bool MidiContext(int i, Point p)
    {
        if (MidiClipAt(i, p.X) is not { } c) return false;
        SelectedMidiId = c.Id;
        // on a boxed clip the others stay boxed, so the menu acts on them all
        if (boxed.Count > 1 && boxed.Contains(c.Id)) SelectOne(i);
        else Select(i);
        MidiClipContextRequested?.Invoke(tracks[i], c.Id, p);
        return true;
    }

    void UpdateMidiDrag()
    {
        if (midiHeld == null) return;
        double dxPx = lastPointer.X - down.X + scrollX - downScrollX;
        if (!moved && Math.Abs(dxPx) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
        // a pattern's clip dragged by its body brings its other instruments: only now, so a click still picks it alone
        if (!moved && !midiEdge && !midiCarrying && UseOf(midiHeld) is { Count: > 0 } use)
        {
            boxed.Clear();
            boxed.Add(midiHeld.Id);
            boxed.UnionWith(use.Select(c => c.Id));
            midiCarrying = true;
            StartCarry(null);
        }
        moved = true;
        bool free = (modifiers & KeyModifiers.Alt) != 0;
        var grid = Grid;
        if (midiCarrying)
        {
            // everything boxed moves by the same time, the grabbed clip landing on a line, each on its own track, none before 0
            double dt = ClipEditing.Snap(midiFromSec + dxPx / pps, grid, free) - midiFromSec;
            foreach (double from in midiCarried.Values) dt = Math.Max(dt, -from);
            foreach (var (lc, o) in carriedAlong) dt = Math.Max(dt, ClipEnvelope.MinClipSec - Window(lc, o).End);
            carryShift = dt;
            foreach (var (lc, o) in carriedAlong)
            {
                var v = ClipEditing.Copy(o);
                v.AtSec = ClipEditing.ToSample(o.AtSec + dt);
                carriedPreview[lc] = v;
            }
        }
        else if (midiTrimStart)
        {
            // the start goes to a line and the end stays: the length is what is left, never under a sixteenth
            double end = midiFromSec + midiFromBeats * BeatSec;
            midiAtSec = Math.Clamp(ClipEditing.Snap(midiFromSec + dxPx / pps, grid, free), 0, end - MidiClip.MinBeats * BeatSec);
            midiBeats = midiFromBeats - (midiAtSec - midiFromSec) / BeatSec;
        }
        else if (midiEdge)
        {
            // the length goes by grid steps (a sixteenth when the magnet is off), never under a sixteenth
            double step = free || grid.Step <= 0 ? MidiClip.MinBeats : Math.Max(MidiClip.MinBeats, grid.Step / BeatSec);
            double beats = midiFromBeats + dxPx / pps / BeatSec;
            midiBeats = Math.Clamp(Math.Round(beats / step) * step, MidiClip.MinBeats, MidiClip.MaxBeats);
        }
        else
        {
            // lands on a line, like a take
            midiAtSec = Math.Max(0, ClipEditing.Snap(midiFromSec + dxPx / pps, grid, free));
            int over = HitTrack(lastPointer.Y);
            if (over >= 0 && over < tracks.Count && tracks[over].Track.IsMidi) midiOver = over;
        }
    }

    /// <summary>The cursor over a MIDI lane: the edge resizes, a clip moves.</summary>
    StandardCursorType? MidiCursor(int i, Point p)
    {
        if (i < 0 || i >= tracks.Count || !tracks[i].Track.IsMidi || p.X < HeaderW) return null;
        if (MidiClipAt(i, p.X) is not { } c) return StandardCursorType.Arrow;
        if (OnMidiStartEdge(c, p.X) && !OnMidiEdge(c, p.X)) return StandardCursorType.LeftSide;
        return OnMidiEdge(c, p.X) ? StandardCursorType.RightSide : StandardCursorType.Hand;
    }

    /// <summary>Selects a MIDI clip by id (after the window made or moved it).</summary>
    public void SelectMidiClip(int index, string? clipId)
    {
        SelectedMidiId = clipId;
        Select(index);
    }

    /// <summary>What the "midi" gesture ends in (taken before the gesture is cleared), or null when the clip didn't change.</summary>
    (string Id, double At, double Beats, string? To, bool Trim, bool Loop)? MidiResult()
    {
        var c = midiHeld;
        if (c == null || !moved) return null;
        string? to = midiOver >= 0 && midiOver < tracks.Count && midiOver != fromIndex ? tracks[midiOver].Track.Id : null;
        resizedAlong = [.. midiAlong.Select(o => o.Id)];
        return midiAtSec != c.AtSec || midiBeats != c.LengthBeats || to != null ? (c.Id, midiAtSec, midiBeats, to, midiTrimStart, midiLoop && c.PatternId == null) : null;
    }

    void ReleaseMidi((string Id, double At, double Beats, string? To, bool Trim, bool Loop)? result)
    {
        if (result is not { } r) return;
        if (r.Trim) MidiClipTrimmed?.Invoke(r.Id, r.At);
        else if (r.Loop) MidiClipLooped?.Invoke(r.Id, r.Beats);
        else MidiClipChanged?.Invoke(r.Id, r.At, r.Beats, r.To);
    }

    void ClearMidiGesture()
    {
        midiHeld = null;
        midiOver = -1;
        midiEdge = false;
        midiTrimStart = false;
        midiLoop = false;
        midiCarrying = false;
        midiCarried.Clear();
        midiAlong.Clear();
        carryShift = 0;
    }

    /// <summary>Notes every boxed MIDI clip a carry moves, with where it starts.</summary>
    void StartMidiCarry()
    {
        midiCarried.Clear();
        carryShift = 0;
        foreach (var t in tracks)
            if (t.Track.MidiClips is { } clips)
                foreach (var c in clips)
                    if (boxed.Contains(c.Id)) midiCarried[c] = c.AtSec;
    }

    /// <summary>Where each carried MIDI clip lands (taken before the gesture is cleared).</summary>
    List<(string Id, double AtSec)> MidiMoves() => [.. midiCarried.Select(kv => (kv.Key.Id, ClipEditing.ToSample(kv.Value + carryShift)))];

    /// <summary>Boxes the MIDI clips of lane <paramref name="i"/> that the selection box touches.</summary>
    void BoxMidi(int i, Rect box)
    {
        if (tracks[i].Track.MidiClips is not { } clips) return;
        foreach (var c in clips)
        {
            var (start, end) = MidiSpan(c);
            if (new Rect(X(start), LaneY(i) + 5, Math.Max(2, X(end) - X(start)), LaneH(i) - 10).Intersects(box)) boxed.Add(c.Id);
        }
    }

    /// <summary>The earliest boxed MIDI clip of a lane, or null.</summary>
    MidiClip? FirstBoxedMidi(int i) => tracks[i].Track.MidiClips?.Where(c => boxed.Contains(c.Id)).MinBy(c => c.AtSec);

    /// <summary>What looks selected now: the boxed clips, else the selected MIDI clip, else the selected track's take.</summary>
    HashSet<string> SelectionSeed()
    {
        var s = new HashSet<string>();
        if (boxed.Count > 1) s.UnionWith(boxed);
        else if (SelectedMidiId != null) s.Add(SelectedMidiId);
        else if (Selected >= 0 && Selected < tracks.Count && SelectedClipId != null && tracks[Selected].Clips.Any(c => c.Clip.Id == SelectedClipId))
            s.Add(SelectedClipId);
        return s;
    }

    /// <summary>The lane of a take or a MIDI clip, or -1.</summary>
    int LaneOf(string id)
    {
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Clips.Any(c => c.Clip.Id == id) || tracks[i].Track.MidiClips?.Any(c => c.Id == id) == true) return i;
        return -1;
    }

    bool IsMidiClip(string id) => tracks.Any(t => t.Track.MidiClips?.Any(c => c.Id == id) == true);

    /// <summary>
    /// Ctrl+click on a clip (take or MIDI): adds it to <paramref name="set"/> (what was selected) or takes it out. One
    /// left is a plain selection; more are boxed together, with the one clicked (or another one) as the selected one.
    /// </summary>
    void ToggleBoxed(string id, HashSet<string> set)
    {
        if (!set.Remove(id)) set.Add(id);
        if (set.Count <= 1)
        {
            string only = set.Count == 1 ? set.First() : id;
            int lane = LaneOf(only);
            if (set.Count == 0) { SelectedMidiId = null; Select(lane); }
            else if (IsMidiClip(only)) SelectMidiClip(lane, only);
            else SelectClip(lane, only);
            return;
        }
        string primary = set.Contains(id) ? id : set.First();
        boxed.Clear();
        boxed.UnionWith(set);
        if (IsMidiClip(primary)) SelectedMidiId = primary;
        else
        {
            SelectedMidiId = null;
            SelectedClipId = primary;
        }
        SelectOne(LaneOf(primary));
    }
}
