using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The piano roll (native only, stage 1 of the MIDI editor, asked by the user "in the purest FL Studio style"): the notes
/// of one MIDI clip on a grid of pitches (rows, a keyboard on the left) and beats (columns, the timeline's bars on the ruler),
/// with each note's velocity as a bar in the lane at the bottom. Drawn by hand like the timeline.
/// <para>
/// Mouse, as in FL: a click on an empty cell writes a note there with the length of the last one touched (and drags
/// it), a note's body moves it (with the others selected; Alt, free in time; Shift, a copy of them, dropped where it is
/// let go), its right edge resizes it, the right button erases what it touches (on an empty spot it also drops the
/// selection), Ctrl+drag on empty space selects with a box, a key of the keyboard plays its note and the ruler moves the
/// playhead (held, it scrubs like the timeline's). The keys (Supr, Ctrl+A, arrows, copy / cut / paste / duplicate)
/// come from the window (<see cref="HandleKey"/>).
/// </para>
/// <para>
/// Copies, pastes and duplicates stay inside the clip being edited (asked by the user): a copy that would start past
/// its end is left out. Paste goes to the playhead when it is inside the clip (on the magnet), else back where the
/// notes were copied from.
/// </para>
/// <para>
/// The velocity lane grows or shrinks from the bar above it (double click: back to its height), and dragging a bar of
/// a selected note sets every selected note that starts there, a chord at once (both asked by the user). It can be
/// hidden (<see cref="VelocityShown"/>), has a scale of values on the left, and its values go to steps of
/// <see cref="VelocityStep"/> (Alt: free). Its tools (<see cref="VelocityTool"/>, asked by the user): the pencil paints
/// every bar the drag sweeps, the line sets the bars under it as a ramp (both only on the selected notes when there are
/// some), and the right button paints back <see cref="MidiNote.DefaultVelocity"/>.
/// </para>
/// <para>
/// The grid's lines follow the magnet or go their own way (<see cref="GridMode"/>, asked by the user, like the
/// timeline's): the magnet's steps, Auto (finer as it zooms in) or a fixed subdivision. The right button on the ruler
/// asks the window for that menu. The magnet itself (<see cref="SnapMode"/>, FL's list) can follow those lines: Línea
/// goes to the nearest, Celda to the start of the cell.
/// </para>
/// <para>
/// It shows the whole track on the timeline's own bars (asked by the user): the clip being edited is lit, the track's
/// other clips are marked on the ruler and in the grid with their notes faint, and a click inside one of them makes it
/// the one being edited (<see cref="ClipPicked"/>) and goes on as a click there. The grid and the magnet count from the
/// timeline's start, so they line up with the timeline's.
/// </para>
/// <para>
/// With a <see cref="Scale"/> the rows of its notes stand out and the root's are lit, notes written or moved land on it
/// and moving goes along it (a chord stays in key), the ↑ ↓ keys too; Alt is free. A note outside it is drawn hollow.
/// With <see cref="ChordTool"/> a click writes a whole chord, previewed under the pointer. Any stack of selected notes
/// that makes a chord gets a small bar over it with its name and its inversions (<see cref="DrawChordBar"/>), keys 1 to
/// 4 too, so a chord written long ago can be turned just the same, and the next one drawn comes in that inversion.
/// </para>
/// <para>
/// A pattern's clip opens the pattern itself (asked by the user: editing a pattern is that pattern and nothing else): one
/// pass from bar 1, with no other clip or repeat around it and the grid counted from its start. The timeline is out of
/// the picture, so the playhead shows where any clip of the pattern is sounding (<see cref="PlayBeat"/>), wrapping with
/// its repeats, and the ruler seeks inside the clip that was opened.
/// </para>
/// It edits a working copy of the clip's notes and hands the whole list to the window when a gesture ends
/// (<see cref="NotesCommitted"/>), which makes it one undo step; the control never touches the project or the engine.
/// </summary>
public sealed partial class PianoRollView : Control
{
    /// <summary>
    /// How <see cref="SetClip"/> frames the view: as it was, the clip filling the width, the clip scrolled into sight
    /// (the zoom kept), or the whole track with the playhead in sight.
    /// </summary>
    public enum Frame { Keep, Clip, Reveal, Track }

    const double KeyW = 56, RulerH = 24, VelGap = 8;
    /// <summary>Marks of the velocity lane's scale (the default, 100, among them); a label is skipped when it would touch the last.</summary>
    static readonly int[] VelMarks = [127, 100, 64, 32];
    /// <summary>Px between the scale's labels.</summary>
    const double VelLabelGap = 11;
    static readonly string[] VelNames = [.. Enumerable.Range(0, 128).Select(v => v.ToString())];
    /// <summary>The velocity lane's height range and default; the grid always keeps <see cref="MinGridH"/>.</summary>
    public const double MinVelH = 40, DefaultVelH = 64;
    const double MinGridH = 60;
    /// <summary>Rows' height range and default (px per semitone).</summary>
    const double MinRowH = 7, MaxRowH = 28, DefaultRowH = 14;
    /// <summary>Zoom range in px per beat.</summary>
    const double MinBeatW = 6, MaxBeatW = 480;
    /// <summary>Px from the ruler's ends where a scrub scrolls the view.</summary>
    const double EdgeScroll = 16;
    /// <summary>Px the pointer moves on the ruler before a click becomes a scrub.</summary>
    const double ScrubSlop = 3;
    /// <summary>Px from a note's right end that grab it to resize it.</summary>
    const double EdgeGrab = 6;
    /// <summary>Lines closer than this are not drawn; Auto picks the finest step at least <see cref="AutoGridPx"/> apart.</summary>
    const double MinGridPx = 6, AutoGridPx = 14;
    /// <summary>The whole track is fitted only down to this zoom (a bar = 48 px); a longer one is shown around the playhead.</summary>
    const double MinFitBeatW = 12;
    /// <summary>Room left before the first clip (or the playhead's margin) when the view is framed.</summary>
    const double FrameMargin = 12;
    const int Pitches = 128;
    /// <summary>The chord bar: its height, each inversion's box and the room for the chord's name; the most notes a chord can have to get one.</summary>
    const double ChordBarH = 28, ChordChipW = 34, ChordBarPad = 3;
    const int MaxChordNotes = 6;
    /// <summary>The text keys of the inversions' boxes, made once: a key built on every render was garbage per frame.</summary>
    static readonly string[] InversionKeys = [.. Enumerable.Range(0, MaxChordNotes).Select(i => $"piano.chord.inv.{i}")];

    string? clipId;
    string? title;
    /// <summary>What can be edited: the clip, or one pass of its pattern.</summary>
    double clipBeats = MidiClip.DefaultBeats;
    /// <summary>The loop length when a pattern's end was grabbed, for the drag and for Esc.</summary>
    double cellFrom;
    /// <summary>Where the view puts the clip's start: its place on the timeline, or 0 in a pattern.</summary>
    double clipAtSec;
    /// <summary>The pattern on screen (null on a clip of its own), and the timeline beat the opened clip starts on.</summary>
    string? patternId;
    double patternStart;
    /// <summary>Where the pattern's clips are on the timeline, in beats.</summary>
    readonly List<(double Start, double Beats)> patternUses = [];
    Color color;
    readonly List<MidiNote> notes = [];
    readonly HashSet<MidiNote> selected = [];
    /// <summary>The track's other clips (the project's, only read to draw them and to pick one).</summary>
    readonly List<MidiClip> others = [];
    /// <summary>The ghost notes: what the pattern's other instruments play, each in its track's color. Only read to draw them.</summary>
    readonly List<(List<MidiNote> Notes, Color Tint)> ghostParts = [];

    double rowH = DefaultRowH, beatW = 40, scrollX, scrollY;
    double velH = DefaultVelH;
    /// <summary>The framing <see cref="SetClip"/> asked for, done on the next render (when the size is known).</summary>
    Frame pending = Frame.Clip;
    double bpm = 120;
    double playheadSec = -1;

    string snapMode = "1/16", mainSnap = "1/4";
    string gridMode = "snap";

    /// <summary>The magnet: one of <see cref="SnapChoices.PianoIds"/>, set by the window's menu.</summary>
    public string SnapMode { get => snapMode; set { snapMode = value; InvalidateVisual(); } }
    /// <summary>The timeline's magnet, what "main" follows.</summary>
    public string MainSnap { get => mainSnap; set { mainSnap = value; InvalidateVisual(); } }
    /// <summary>The magnet in effect, with "main" resolved.</summary>
    public string ActiveSnap => snapMode == "main" ? mainSnap : snapMode;
    /// <summary>The magnet's fixed length in beats, or 0 when it is off or follows the zoom: there is nothing to quantize to.</summary>
    public double QuantizeBeats => ClipEditing.GridSeconds(ActiveSnap, 60);
    /// <summary>The magnet's lines in beats right now (0 = free).</summary>
    double SnapBeats => ActiveSnap == "line" ? DrawnUnit() : QuantizeBeats;

    /// <summary>
    /// How the grid's lines are spaced: "snap" (the magnet's steps; Auto while it has no fixed length), "auto" (by zoom) or one of
    /// the timeline's subdivisions ("bar", "1/4", "1/8T"…).
    /// </summary>
    public string GridMode { get => gridMode; set { gridMode = value; InvalidateVisual(); } }
    /// <summary>The length and velocity the next written note gets: the last note touched.</summary>
    double lastBeats = 1;
    int lastVelocity = MidiNote.DefaultVelocity;

    MusicScale? scale;
    string chordKind = Chords.InScale;
    bool chordTool;
    /// <summary>The inversion the next chord comes in: the last one picked.</summary>
    int chordInversion;
    /// <summary>The preview under the pointer: its root (-1 = make it again), notes and name.</summary>
    int ghostRoot = -1;
    int[] ghost = [];
    string ghostName = "";
    /// <summary>The chord bar over the selection: its notes (lowest first), each inversion's pitches, the one it is in, and what it was read from.</summary>
    readonly List<MidiNote> chordNotes = [];
    int[][] chordVoicings = [];
    int chordAt;
    string chordName = "";
    long chordRead;

    /// <summary>The song's scale (null = none).</summary>
    public MusicScale? Scale { get => scale; set { scale = value; ghostRoot = -1; InvalidateVisual(); } }
    /// <summary>A click on the grid writes a chord instead of a note.</summary>
    public bool ChordTool { get => chordTool; set { chordTool = value; InvalidateVisual(); } }
    /// <summary>What the chord tool writes: an id of <see cref="Chords.Kinds"/>.</summary>
    public string ChordKind { get => chordKind; set { chordKind = value; ghostRoot = -1; InvalidateVisual(); } }

    /// <summary>The clip's id and its notes after an edit (one undo step).</summary>
    public event Action<string, List<MidiNote>>? NotesCommitted;
    /// <summary>A note to hear on its own: pitch, velocity.</summary>
    public event Action<int, int>? NoteAuditioned;
    /// <summary>A click on the ruler: the timeline time to move the playhead to.</summary>
    public event Action<double>? SeekRequested;
    /// <summary>A drag on the ruler started (true) or ended (false): the window holds playback meanwhile, as the timeline's.</summary>
    public event Action<bool>? ScrubChanged;
    /// <summary>The right button on the ruler: the grid's menu.</summary>
    public event Action? GridMenuRequested;
    /// <summary>The velocity lane was resized (its new height), for the window to remember.</summary>
    public event Action<double>? VelocityHeightChanged;
    /// <summary>A click inside another clip of the track: the window shows that one (<see cref="SetClip"/>) before the click goes on.</summary>
    public event Action<string>? ClipPicked;
    /// <summary>A pattern's loop length was dragged by its end in the ruler: the new length in beats, for the window to set.</summary>
    public event Action<double>? CellLengthChanged;

    /// <summary>How a drag in the velocity lane edits: painting bar by bar, or a straight line (a ramp).</summary>
    public enum VelTool { Pencil, Line }

    bool velShown = true;
    /// <summary>The velocity lane is shown (the window's toggle; hidden, the grid takes its place).</summary>
    public bool VelocityShown
    {
        get => velShown;
        set { if (velShown == value) return; velShown = value; CancelGesture(); ClampScroll(); InvalidateVisual(); }
    }

    int velStep;
    /// <summary>The velocity lane's steps (0 or 1 = free): values go to multiples of it, or 127.</summary>
    public int VelocityStep { get => velStep; set { velStep = Math.Clamp(value, 0, 127); InvalidateVisual(); } }

    public VelTool VelocityTool { get; set; }

    /// <summary>The velocity lane's height (the window restores it from config.json).</summary>
    public double VelocityHeight
    {
        get => velH;
        set { velH = Math.Max(MinVelH, value); ClampScroll(); InvalidateVisual(); }
    }

    /// <summary>The lane as drawn: never so tall that the grid gets under <see cref="MinGridH"/>.</summary>
    double VelH => velShown ? Math.Max(MinVelH, Math.Min(velH, Bounds.Height - RulerH - VelGap - MinGridH)) : 0;
    double Gap => velShown ? VelGap : 0;

    public string? ClipId => clipId;

    public PianoRollView()
    {
        Focusable = true;
        ClipToBounds = true;
        // a scrub held against the ruler's ends keeps the view (and the playhead) going that way, as the timeline's
        edgeScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (gesture != "scrub") return;
            double old = scrollX;
            if (lastPointer.X > Bounds.Width - EdgeScroll) scrollX += Math.Max(2, (lastPointer.X - Bounds.Width + EdgeScroll) / 2);
            else if (lastPointer.X < KeyW + EdgeScroll) scrollX -= Math.Max(2, (KeyW + EdgeScroll - lastPointer.X) / 2);
            ClampScroll();
            if (old == scrollX) return;
            if (scrubHeld) Scrub();
            InvalidateVisual();
        });
        edgeScroll.Stop();
        DetachedFromVisualTree += (_, _) => edgeScroll.Stop();
        layer = new Layer(this);
        VisualChildren.Add(layer);
    }

    /// <summary>
    /// The playhead, drawn over the roll (as <see cref="TimelineView"/>'s layer): while playing, each step repaints only
    /// this, not every row, line, wave and note. Not hit-testable: the roll under it takes the pointer.
    /// </summary>
    sealed class Layer : Control
    {
        readonly PianoRollView owner;
        public Layer(PianoRollView owner) { this.owner = owner; IsHitTestVisible = false; }
        public override void Render(DrawingContext ctx) => owner.RenderLayer(ctx);
    }

    readonly Layer layer;

    /// <summary>Repaints the roll and its playhead layer (the playhead's place follows scroll, zoom and size). Hides
    /// Visual.InvalidateVisual so every caller, inside or outside, keeps both in step.</summary>
    public new void InvalidateVisual()
    {
        base.InvalidateVisual();
        layer?.InvalidateVisual();
    }

    /// <summary>
    /// Shows a clip (null = none) with what goes with it (<paramref name="family"/>, the clip among them): the track's
    /// clips around it or, on a pattern's clip, the pattern's clips, which aren't drawn. The same clip again (after an
    /// edit or an undo) keeps the selection where it can; <paramref name="frame"/> says how the view moves (a view never
    /// framed yet fits the clip). A pattern fits when it comes on screen, and its clips all show the same, so moving
    /// between them keeps the view. <paramref name="ghosts"/> = the notes of the pattern's other instruments, to see and
    /// not to touch.
    /// </summary>
    public void SetClip(MidiClip? clip, IEnumerable<MidiClip>? family, string? trackName, Color? trackColor, double tempo, Frame frame = Frame.Keep,
        IEnumerable<(List<MidiNote> Notes, Color? Color)>? ghosts = null)
    {
        ghostParts.Clear();
        foreach (var (list, tint) in ghosts ?? []) ghostParts.Add((list, tint ?? Palette.AccentColor));
        if (clip?.PatternId is { } id) frame = id != patternId || frame == Frame.Clip ? Frame.Clip : Frame.Keep;
        else if (clip != null && patternId != null && frame == Frame.Keep) frame = Frame.Clip;
        bool same = clip != null && clip.Id == clipId;
        if (toolBase != null && !same) ToolInterrupted?.Invoke();
        var keep = same ? selected.Select(n => (n.Pitch, n.Beat, n.Beats)).ToHashSet() : [];
        CancelGesture();
        clipId = clip?.Id;
        // the notes are copied again: the chord bar's would be the old ones
        chordRead = -1;
        bpm = tempo;
        color = trackColor ?? Palette.AccentColor;
        notes.Clear();
        selected.Clear();
        others.Clear();
        patternUses.Clear();
        patternId = clip?.PatternId;
        if (clip != null)
        {
            clipBeats = clip.CellBeats;
            clipAtSec = patternId != null ? 0 : clip.AtSec;
            patternStart = patternId != null ? clip.AtSec * tempo / 60 : 0;
            title = trackName;
            foreach (var n in clip.Notes)
            {
                var copy = n.Copy();
                notes.Add(copy);
                if (keep.Contains((n.Pitch, n.Beat, n.Beats))) selected.Add(copy);
            }
            if (patternId != null) patternUses.AddRange((family ?? [clip]).Select(c => (c.AtSec * tempo / 60, c.LengthBeats)));
            else if (family != null) others.AddRange(family.Where(c => c.Id != clip.Id));
        }
        if (frame != Frame.Keep) pending = frame;
        InvalidateVisual();
    }

    /// <summary>The playhead in timeline seconds; when it moved inside (or across) the view, redraws only the
    /// <see cref="Layer"/>.</summary>
    public void SetPlayhead(double sec)
    {
        // kept while hidden too: framing the whole track looks for it
        if (clipId == null || !IsVisible) { playheadSec = sec; return; }
        double W = Bounds.Width;
        double x0 = XA(PlayBeat(playheadSec)), x1 = XA(PlayBeat(sec));
        bool was = playheadSec >= 0 && x0 >= KeyW - 2 && x0 <= W + 2, now = x1 >= KeyW - 2 && x1 <= W + 2;
        if (sec == playheadSec || !was && !now) { playheadSec = sec; return; }
        playheadSec = sec;
        layer.InvalidateVisual();
    }

    /// <summary>
    /// The view's beat of a timeline time. In a pattern, its place in the pass of the clip sounding there, or -1 when none
    /// is (left of the view, so it isn't drawn). Allocates nothing: it runs on every tick.
    /// </summary>
    double PlayBeat(double sec)
    {
        double beat = sec * bpm / 60;
        if (patternId == null) return beat;
        foreach (var (start, length) in patternUses)
            if (beat >= start && beat < start + length) return (beat - start) % clipBeats;
        return -1;
    }

    // --- geometry ---------------------------------------------------------------------------------

    double GridBottom => Math.Max(RulerH, Bounds.Height - VelH - Gap);
    /// <summary>On the bar between the grid and the velocity lane, which resizes the lane.</summary>
    bool OnVelSplit(Point p) => velShown && p.Y >= GridBottom - 2 && p.Y <= GridBottom + VelGap + 2;
    /// <summary>The velocity lane's bars: the height of a full one, and the y of a value.</summary>
    double VelLaneH => Bounds.Height - GridBottom - VelGap - 6;
    double VelY(int v) => Bounds.Height - 3 - VelLaneH * v / 127.0;
    /// <summary>Where the clip starts, in beats from the timeline's start (the view's own coordinate).</summary>
    double Origin => clipAtSec * bpm / 60;
    static double StartBeat(MidiClip c, double bpm) => c.AtSec * bpm / 60;
    /// <summary>X of a beat from the timeline's start (<see cref="XA"/>) or from the clip's (<see cref="X"/>, its notes).</summary>
    double XA(double beat) => KeyW + beat * beatW - scrollX;
    double X(double beat) => XA(Origin + beat);
    double BeatA(double x) => (x - KeyW + scrollX) / beatW;
    double Beat(double x) => BeatA(x) - Origin;
    double Y(int pitch) => RulerH + (Pitches - 1 - pitch) * rowH - scrollY;
    int PitchAt(double y) => Math.Clamp(Pitches - 1 - (int)Math.Floor((y + scrollY - RulerH) / rowH), 0, Pitches - 1);
    Rect NoteRect(MidiNote n) => new(X(n.Beat), Y(n.Pitch) + 1, Math.Max(3, n.Beats * beatW - 1), rowH - 2);
    double ContentH => Pitches * rowH;
    /// <summary>Up to the last clip's end, with two bars to spare.</summary>
    double ContentW
    {
        get
        {
            double end = Origin + clipBeats;
            foreach (var c in others) end = Math.Max(end, StartBeat(c, bpm) + c.LengthBeats);
            return Math.Max(end + 8, 16) * beatW;
        }
    }

    void ClampScroll()
    {
        scrollY = Math.Clamp(scrollY, 0, Math.Max(0, ContentH - (GridBottom - RulerH)));
        scrollX = Math.Clamp(scrollX, 0, Math.Max(0, ContentW - (Bounds.Width - KeyW)));
    }

    /// <summary>
    /// Does the framing <see cref="SetClip"/> asked for, once the size is known. Clip: it fills the width. Reveal: the
    /// zoom stays and the clip is scrolled into sight. Track: every clip fits the width, or, if that would be too small
    /// to work in, the playhead is in the middle. The notes shown (or C4) sit in the middle of the height.
    /// </summary>
    void Fit()
    {
        if (pending == Frame.Keep || Bounds.Width <= KeyW || clipId == null) return;
        var frame = pending;
        pending = Frame.Keep;
        double w = Bounds.Width - KeyW, origin = Origin;
        var pitches = notes.Select(n => n.Pitch);
        switch (frame)
        {
            case Frame.Clip:
                beatW = Math.Clamp((w - 2 * FrameMargin) / Math.Max(1, clipBeats), MinBeatW, MaxBeatW);
                scrollX = origin * beatW - FrameMargin;
                break;
            case Frame.Reveal:
                // only what is out of sight moves: its start, its end if it fits, its notes if none shows
                if (XA(origin) < KeyW || XA(origin) > Bounds.Width - FrameMargin || X(clipBeats) > Bounds.Width && clipBeats * beatW <= w)
                    scrollX = origin * beatW - FrameMargin;
                if (notes.Any(n => Y(n.Pitch) >= RulerH && Y(n.Pitch) + rowH <= GridBottom)) pitches = [];
                break;
            case Frame.Track:
            {
                double start = origin, end = origin + clipBeats;
                foreach (var c in others)
                {
                    start = Math.Min(start, StartBeat(c, bpm));
                    end = Math.Max(end, StartBeat(c, bpm) + c.LengthBeats);
                }
                double fit = (w - 2 * FrameMargin) / Math.Max(1, end - start);
                beatW = Math.Clamp(fit, MinFitBeatW, MaxBeatW);
                double play = playheadSec * bpm / 60;
                scrollX = fit >= MinFitBeatW ? start * beatW - FrameMargin
                    : (play >= start && play <= end ? play : origin) * beatW - w / 2;
                pitches = pitches.Concat(others.SelectMany(c => c.Notes.Select(n => n.Pitch)));
                break;
            }
        }
        if (pitches.Any() || frame != Frame.Reveal)
        {
            double mid = pitches.Any() ? (pitches.Min() + pitches.Max()) / 2.0 : 60;
            scrollY = (Pitches - 1 - mid) * rowH - (GridBottom - RulerH) / 2;
        }
        ClampScroll();
    }

    /// <summary>A beat from the clip's start to the magnet, whose steps count from the timeline's start.</summary>
    double Snap(double beat, bool floor = false) => SnapA(Origin + beat, floor) - Origin;

    double SnapA(double beat, bool floor = false)
    {
        double step = SnapBeats;
        if (step <= 0 || (modifiers & KeyModifiers.Alt) != 0) return Math.Max(0, beat);
        return Math.Max(0, ClipEditing.Snap(beat, new SnapGrid(step, floor, Subdivisions.BarBeats), false));
    }

    /// <summary>The track's other clip under a timeline beat (the last one drawn wins), or null.</summary>
    MidiClip? OtherAt(double beatA)
    {
        for (int i = others.Count - 1; i >= 0; i--)
        {
            double s = StartBeat(others[i], bpm);
            if (beatA >= s && beatA < s + others[i].LengthBeats) return others[i];
        }
        return null;
    }

    MidiNote? NoteAt(Point p)
    {
        if (p.Y < RulerH || p.Y >= GridBottom || p.X < KeyW) return null;
        for (int i = notes.Count - 1; i >= 0; i--)
            if (NoteRect(notes[i]).Inflate(new Thickness(0, 0, 2, 0)).Contains(p)) return notes[i];
        return null;
    }

    bool OnEdge(MidiNote n, Point p) => p.X >= NoteRect(n).Right - EdgeGrab;

    /// <summary>The note's left end (the right one wins on a short note): dragging it moves where the note starts.</summary>
    bool OnStartEdge(MidiNote n, Point p) => !OnEdge(n, p) && p.X <= NoteRect(n).Left + EdgeGrab;

    /// <summary>The loop end of a pattern in the ruler: its handle sits on the end line there.</summary>
    bool OnCellEdge(Point p) => patternId != null && p.Y < RulerH && Math.Abs(p.X - X(clipBeats)) <= EdgeGrab + 2;

    // --- drawing ----------------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Palette.App, new Rect(0, 0, W, H));
        if (clipId == null)
        {
            ctx.DrawText(Loc.T("piano.empty.title"), 12.5, Palette.Dim, new Point(24, 28), Palette.UiBold);
            ctx.DrawText(Loc.T("piano.empty.hint"), 12, Palette.Faint, new Point(24, 52));
            return;
        }
        Fit();
        double gb = GridBottom;
        var line = Palette.Pen(Palette.Line);

        using (ctx.PushClip(new Rect(KeyW, RulerH, Math.Max(0, W - KeyW), Math.Max(0, gb - RulerH))))
        {
            // rows: the black keys' rows darker, a line under every B (between octaves)
            for (int p = 0; p < Pitches; p++)
            {
                double y = Y(p);
                if (y + rowH < RulerH || y > gb) continue;
                if (scale != null)
                {
                    // the scale's rows lifted and the root's lit, the rest left on the background
                    if (scale.IsRoot(p)) ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.14), new Rect(KeyW, y, W - KeyW, rowH));
                    else if (scale.Contains(p)) ctx.FillRectangle(Palette.Raised, new Rect(KeyW, y, W - KeyW, rowH));
                }
                else if (MidiNote.IsBlack(p)) ctx.FillRectangle(Palette.Panel, new Rect(KeyW, y, W - KeyW, rowH));
                if (p % 12 == 0) ctx.DrawLine(line, new Point(KeyW, y + rowH - 0.5), new Point(W, y + rowH - 0.5));
            }
            DrawColumns(ctx, RulerH, gb, W);
            DrawBackWave(ctx, gb, W);
            DrawRegions(ctx, gb, W);
            DrawShadows(ctx, gb, W);
            foreach (var c in others) DrawFaintNotes(ctx, c.Notes, StartBeat(c, bpm), c.CellBeats, c.LengthBeats, W, gb);
            foreach (var (list, tint) in ghostParts) DrawFaintNotes(ctx, list, 0, clipBeats, clipBeats, W, gb, Palette.Alpha(tint, 0.28));
            foreach (var n in notes) DrawNote(ctx, n, W, gb);
            if (gesture == "box" && moved)
                ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.08), Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.7)), BoxRect());
            DrawGhost(ctx, W);
            DrawToolGesture(ctx);
            // the playhead is on the Layer
            SyncChordBar();
            if (gesture == null) DrawChordBar(ctx);
        }

        if (velShown) DrawVelocity(ctx, gb + VelGap, W, H);
        DrawKeys(ctx, gb);
        DrawRuler(ctx, W);
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, KeyW, RulerH));
        ctx.DrawLine(line, new Point(KeyW - 0.5, 0), new Point(KeyW - 0.5, H));
        ctx.DrawLine(line, new Point(0, gb + 0.5), new Point(W, gb + 0.5));
    }

    /// <summary>Bars strong, beats softer, the magnet's steps faint (only when they are at least 6 px apart). The steps
    /// start over at each bar, so a dotted grid lines up with the bars.</summary>
    void DrawColumns(DrawingContext ctx, double top, double bottom, double W)
    {
        var bar = Palette.Pen(Palette.Alpha(Palette.LineColor, 1));
        var beat = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.55));
        var step = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.25));
        double unit = DrawnUnit(), barBeats = Subdivisions.BarBeats;
        int perBar = unit >= barBeats ? 1 : (int)Math.Ceiling(barBeats / unit - 1e-6);
        double every = Math.Max(unit, barBeats);
        for (double b0 = Math.Max(0, Math.Floor(BeatA(KeyW) / every) * every); XA(b0) <= W; b0 += every)
            for (int k = 0; k < perBar; k++)
            {
                double b = b0 + k * unit, x = Math.Round(XA(b)) + 0.5;
                if (x > W) break;
                if (x < KeyW) continue;
                bool isBar = Math.Abs(b / 4 - Math.Round(b / 4)) < 1e-9, isBeat = Math.Abs(b - Math.Round(b)) < 1e-9;
                ctx.DrawLine(isBar ? bar : isBeat ? beat : step, new Point(x, top), new Point(x, bottom));
            }
    }

    /// <summary>The step of the lines on screen: the grid's, or beats or bars when those are too close together to draw.</summary>
    double DrawnUnit()
    {
        double unit = GridUnit();
        return unit * beatW >= MinGridPx ? unit : beatW >= MinGridPx ? 1 : 4;
    }

    /// <summary>The grid's step in beats (<see cref="GridMode"/>).</summary>
    double GridUnit()
    {
        double unit = gridMode switch { "auto" => 0, "snap" => QuantizeBeats, _ => ClipEditing.GridSeconds(gridMode, 60) };
        if (unit > 0) return unit;
        // Auto: from a bar down to a 1/64, the finest step that still leaves room between the lines
        unit = 4;
        while (unit > 1.0 / 16 && unit / 2 * beatW >= AutoGridPx) unit /= 2;
        return unit;
    }

    /// <summary>
    /// Where the track's clips are: outside the one being edited the grid is darker (nothing of it is heard there),
    /// a bit less inside the other clips, and every clip's start and end is a line in the track's color.
    /// </summary>
    void DrawRegions(DrawingContext ctx, double gb, double W)
    {
        double h = gb - RulerH;
        double x0 = Math.Max(KeyW, X(0)), x1 = Math.Min(W, X(clipBeats));
        var dark = Palette.Alpha(Palette.AppColor, 0.6);
        if (x0 > KeyW) ctx.FillRectangle(dark, new Rect(KeyW, RulerH, x0 - KeyW, h));
        if (x1 < W) ctx.FillRectangle(dark, new Rect(Math.Max(KeyW, x1), RulerH, W - Math.Max(KeyW, x1), h));
        var tint = Palette.Alpha(color, 0.06);
        var otherEdge = Palette.Pen(Palette.Alpha(color, 0.4));
        foreach (var c in others)
        {
            double s = XA(StartBeat(c, bpm)), e = XA(StartBeat(c, bpm) + c.LengthBeats);
            if (e < KeyW || s > W) continue;
            double l = Math.Max(KeyW, s), r = Math.Min(W, e);
            if (r > l) ctx.FillRectangle(tint, new Rect(l, RulerH, r - l, h));
            DrawEdge(ctx, otherEdge, s, gb, W);
            DrawEdge(ctx, otherEdge, e, gb, W);
        }
        var edge = Palette.Pen(Palette.Alpha(color, 0.85));
        DrawEdge(ctx, edge, X(0), gb, W);
        DrawEdge(ctx, edge, X(clipBeats), gb, W);
        // a pattern's loop end has its handle in the ruler: it is dragged to change the loop
        if (patternId != null && X(clipBeats) is var cx && cx >= KeyW && cx <= W)
            ctx.FillRectangle(Palette.Alpha(color, 0.95), new Rect(Math.Round(cx) - 4, RulerH - 8, 8, 6));
    }

    static void DrawEdge(DrawingContext ctx, IPen pen, double x, double gb, double W)
    {
        if (x < KeyW || x > W) return;
        x = Math.Round(x) + 0.5;
        ctx.DrawLine(pen, new Point(x, RulerH), new Point(x, gb));
    }

    /// <summary>Corners of the notes (asked by the user: a bit rounded), never more than half the note.</summary>
    static double NoteRadius(Rect r) => Math.Min(4, Math.Min(r.Width, r.Height) / 2);

    void DrawNote(DrawingContext ctx, MidiNote n, double W, double gb)
    {
        var r = NoteRect(n);
        if (r.Right < KeyW || r.X > W || r.Bottom < RulerH || r.Y > gb) return;
        bool sel = selected.Contains(n);
        bool past = n.Beat >= clipBeats;
        double a = (0.55 + 0.45 * n.Velocity / 127.0) * (past ? 0.35 : 1) * (n.Muted ? MutedAlpha : 1);
        double rad = NoteRadius(r);
        // out of the scale: hollow, so it reads as a question and not as an error
        bool outside = scale != null && !scale.Contains(n.Pitch);
        var tint = n.Color > 0 ? GroupColors[n.Color - 1] : color;
        ctx.DrawRectangle(Palette.Alpha(tint, outside ? a * 0.25 : a), sel ? Palette.Pen(Palette.Fg, 1.5) : outside ? Palette.Pen(Palette.Alpha(tint, 0.9)) : Palette.Pen(Palette.Alpha(Palette.AppColor, 0.6)), r, rad, rad);
        // the velocity at a glance (asked by the user): a gauge along the bottom, full at 127
        if (r.Width >= 10 && r.Height >= 8)
        {
            double gw = r.Width - 2 * rad, gy = r.Bottom - 3;
            ctx.FillRectangle(Palette.Alpha(Palette.AppColor, 0.18), new Rect(r.X + rad, gy, gw, 2));
            ctx.FillRectangle(Palette.Alpha(Palette.AppColor, 0.6), new Rect(r.X + rad, gy, gw * n.Velocity / 127.0, 2));
        }
        DrawNoteMarks(ctx, n, r);
        // the name only where it fits whole: "Sol#3" is wider than "G#3"
        if (r.Width >= 20 && rowH >= 11 && Palette.Text(NoteNames.Of(n.Pitch), 9.5, Palette.AccentFg, Palette.UiBold) is var name && name.Width + 5 <= r.Width)
            ctx.DrawText(name, new Point(r.X + 3, r.Y + (r.Height - 15) / 2));
    }

    // --- chords -----------------------------------------------------------------------------------

    /// <summary>"Sol m7": a chord's name.</summary>
    internal static string ChordLabel(int root, ChordKind kind) => Loc.T("piano.chord.name", NoteNames.Class(root), kind.Suffix).TrimEnd();

    /// <summary>The chord tool's notes on this root, in the last inversion (or the highest it has); none that leave MIDI's range.</summary>
    int[] ChordOn(int root)
    {
        if (MidiStamps.Find(chordKind) is { } stamp) return [.. MidiStamps.Notes(stamp, root, 0, scale, lastVelocity).Select(n => n.Pitch).Distinct()];
        int[] rootPosition = Chords.Build(chordKind, root, scale);
        int inversion = Math.Min(chordInversion, rootPosition.Length - 1);
        return [.. Chords.Invert(rootPosition, inversion).Where(p => p is >= MidiNote.MinPitch and <= MidiNote.MaxPitch)];
    }

    /// <summary>The note a click writes on this row: on the scale, unless Alt is down.</summary>
    int WritePitch(double y)
    {
        int pitch = PitchAt(y);
        return scale == null || (modifiers & KeyModifiers.Alt) != 0 ? pitch : Math.Clamp(scale.Nearest(pitch), MidiNote.MinPitch, MidiNote.MaxPitch);
    }

    /// <summary>The chord a click would write, faint under the pointer, with its name.</summary>
    void DrawGhost(DrawingContext ctx, double W)
    {
        if (!chordTool || gesture != null || !IsPointerOver) return;
        var p = lastPointer;
        double at = Beat(p.X);
        if (p.X < KeyW || p.Y < RulerH || p.Y >= GridBottom || at < 0 || at >= clipBeats || NoteAt(p) != null || ChordChipAt(p) >= 0) return;
        int root = WritePitch(p.Y);
        if (root != ghostRoot)
        {
            ghostRoot = root;
            ghost = ChordOn(root);
            ghostName = Chords.Read(ghost.Order().ToList()) is { } read ? ChordLabel(read.Root[0], read.Kind) : "";
        }
        double x = X(Math.Max(0, Snap(at, floor: true))), width = Math.Max(3, lastBeats * beatW - 1);
        var fill = Palette.Alpha(color, 0.4);
        var outline = Palette.Pen(Palette.Alpha(color, 0.9));
        foreach (int pitch in ghost)
        {
            var r = new Rect(x, Y(pitch) + 1, width, rowH - 2);
            double rad = NoteRadius(r);
            ctx.DrawRectangle(fill, outline, r, rad, rad);
        }
        if (ghost.Length > 0 && ghostName.Length > 0)
            ctx.DrawText(Palette.Text(ghostName, 11, Palette.Fg, Palette.UiBold), new Point(x + width + 6, Y(ghost[^1]) - 2));
    }

    /// <summary>
    /// Keeps the chord bar on what is selected: notes starting together that make a known chord. Read again only when the
    /// selection changed, so a render normally costs a sum.
    /// </summary>
    void SyncChordBar()
    {
        long read = 0;
        double beat = -1;
        bool together = selected.Count is >= 2 and <= MaxChordNotes;
        if (together)
            foreach (var n in selected)
            {
                if (beat < 0) beat = n.Beat;
                else if (Math.Abs(n.Beat - beat) > 1e-9) { together = false; break; }
                read += (long)n.Pitch * n.Pitch * n.Pitch + 1;
            }
        if (!together) read = 0;
        else read = read * 31 + (long)(beat * 960);
        if (read == chordRead) return;
        chordRead = read;
        chordNotes.Clear();
        chordVoicings = [];
        if (!together) return;
        var stack = selected.OrderBy(n => n.Pitch).ToList();
        if (Chords.Read(stack.ConvertAll(n => n.Pitch)) is not { } chord) return;
        chordNotes.AddRange(stack);
        chordVoicings = [.. Enumerable.Range(0, stack.Count).Select(i => Chords.Invert(chord.Root, i))];
        chordAt = chord.Inversion;
        chordName = ChordLabel(chord.Root[0], chord.Kind);
    }

    /// <summary>The bar's box: over the chord, always inside the grid. Null when there is no bar.</summary>
    Rect? ChordBarRect()
    {
        if (chordVoicings.Length == 0 || chordNotes.Count == 0) return null;
        double name = Palette.Text(chordName, 11.5, Palette.Fg, Palette.UiBold).Width + 12;
        double width = name + chordVoicings.Length * ChordChipW + ChordBarPad;
        double x = Math.Clamp(X(chordNotes[0].Beat), KeyW + 2, Math.Max(KeyW + 2, Bounds.Width - width - 2));
        double y = Math.Clamp(Y(chordNotes[^1].Pitch) - ChordBarH - 4, RulerH + 2, Math.Max(RulerH + 2, GridBottom - ChordBarH - 2));
        return new Rect(x, y, width, ChordBarH);
    }

    Rect ChordChip(Rect bar, int i) => new(bar.Right - ChordBarPad - (chordVoicings.Length - i) * ChordChipW, bar.Y + ChordBarPad, ChordChipW - 2, ChordBarH - 2 * ChordBarPad);

    /// <summary>The inversion's box under the pointer, or -1.</summary>
    int ChordChipAt(Point p)
    {
        if (gesture != null || ChordBarRect() is not { } bar || !bar.Contains(p)) return -1;
        for (int i = 0; i < chordVoicings.Length; i++)
            if (ChordChip(bar, i).Contains(p)) return i;
        return -1;
    }

    /// <summary>The chord's name and its inversions, each box with its number and a little picture of where its notes go.</summary>
    void DrawChordBar(DrawingContext ctx)
    {
        if (ChordBarRect() is not { } bar) return;
        ctx.DrawRectangle(Palette.Raised, Palette.Pen(Palette.Line), bar, 6, 6);
        var name = Palette.Text(chordName, 11.5, Palette.Fg, Palette.UiBold);
        ctx.DrawText(name, new Point(bar.X + 7, bar.Y + (ChordBarH - name.Height) / 2));
        int low = chordVoicings[0][0], span = Math.Max(1, chordVoicings[^1][^1] - low);
        for (int i = 0; i < chordVoicings.Length; i++)
        {
            var chip = ChordChip(bar, i);
            bool on = i == chordAt, hot = chip.Contains(lastPointer);
            ctx.DrawRectangle(on ? Palette.AccentSoft : hot ? Palette.Hover : Palette.Panel, on ? Palette.Pen(Palette.Accent) : null, chip, 4, 4);
            var mark = Palette.Text(Loc.T(InversionKeys[i]), 10, on ? Palette.Accent : Palette.Dim, Palette.UiBold);
            ctx.DrawText(mark, new Point(chip.X + 4, chip.Y + (chip.Height - mark.Height) / 2));
            foreach (int pitch in chordVoicings[i])
            {
                double y = chip.Bottom - 4 - (chip.Height - 8) * (pitch - low) / span;
                ctx.FillRectangle(on ? Palette.Accent : Palette.Dim, new Rect(chip.Right - 14, Math.Round(y) - 1, 10, 2));
            }
        }
    }

    /// <summary>Puts the chord in the bar in this inversion, which the next chord drawn takes too.</summary>
    void SetInversion(int inversion)
    {
        if (inversion < 0 || inversion >= chordVoicings.Length || chordNotes.Count != chordVoicings[inversion].Length) return;
        chordInversion = inversion;
        ghostRoot = -1;
        if (inversion == chordAt || !Chords.InRange(chordVoicings[inversion])) return;
        for (int i = 0; i < chordNotes.Count; i++)
        {
            chordNotes[i].Pitch = chordVoicings[inversion][i];
            NoteAuditioned?.Invoke(chordNotes[i].Pitch, chordNotes[i].Velocity);
        }
        Commit();
        InvalidateVisual();
    }

    /// <summary>
    /// Notes only there to see, faint and without their names: another clip of the track, with its repeats.
    /// <paramref name="start"/> is where the clip starts, in timeline beats.
    /// </summary>
    void DrawFaintNotes(DrawingContext ctx, List<MidiNote> list, double start, double cell, double length, double W, double gb, IBrush? tint = null)
    {
        if (XA(start + length) < KeyW || XA(start) > W) return;
        var fill = tint ?? Palette.Alpha(color, 0.28);
        for (int pass = 0; pass * cell < length - 1e-9 && XA(start + pass * cell) <= W; pass++)
        {
            if (XA(start + (pass + 1) * cell) < KeyW) continue;
            foreach (var n in list)
            {
                if (!MidiClip.Slice(n, pass, cell, length, out double from, out double to)) continue;
                var r = new Rect(XA(start + from), Y(n.Pitch) + 1, Math.Max(3, (to - from) * beatW - 1), rowH - 2);
                if (r.Right < KeyW || r.X > W || r.Bottom < RulerH || r.Y > gb) continue;
                double rad = NoteRadius(r);
                ctx.DrawRectangle(fill, null, r, rad, rad);
            }
        }
    }

    /// <summary>
    /// Draws the <see cref="Layer"/>: the playhead in the ruler and down the grid, each clipped as the roll clips them.
    /// The chord bar sat over the playhead, so when the line crosses it the bar is drawn again on top. Allocates
    /// nothing: it runs on every playhead step.
    /// </summary>
    void RenderLayer(DrawingContext ctx)
    {
        if (clipId == null || playheadSec < 0) return;
        // the roll may not have framed yet in this pass; both do it once, whichever draws first
        Fit();
        double W = Bounds.Width, gb = GridBottom;
        double xa = XA(PlayBeat(playheadSec));
        if (xa >= KeyW && xa <= W)
            using (ctx.PushClip(new Rect(KeyW, 0, Math.Max(0, W - KeyW), RulerH)))
                ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(xa, 0), new Point(xa, RulerH));
        double x = Math.Round(xa) + 0.5;
        if (x < KeyW || x > W) return;
        using (ctx.PushClip(new Rect(KeyW, RulerH, Math.Max(0, W - KeyW), Math.Max(0, gb - RulerH))))
        {
            ctx.DrawLine(Palette.Pen(Palette.Accent), new Point(x, RulerH), new Point(x, gb));
            SyncChordBar();
            if (gesture == null && ChordBarRect() is { } bar && x >= bar.X - 1 && x <= bar.Right + 1) DrawChordBar(ctx);
        }
    }

    void DrawKeys(DrawingContext ctx, double gb)
    {
        using var clip = ctx.PushClip(new Rect(0, RulerH, KeyW, Math.Max(0, gb - RulerH)));
        ctx.FillRectangle(Palette.Fg, new Rect(0, RulerH, KeyW, gb - RulerH));
        var line = Palette.Pen(Palette.Alpha(Palette.AppColor, 0.35));
        for (int p = 0; p < Pitches; p++)
        {
            double y = Y(p);
            if (y + rowH < RulerH || y > gb) continue;
            bool lit = p == heardPitch;
            if (MidiNote.IsBlack(p)) ctx.FillRectangle(lit ? Palette.Accent : Palette.App, new Rect(0, y, KeyW * 0.62, rowH));
            else
            {
                if (lit) ctx.FillRectangle(Palette.Accent, new Rect(0, y, KeyW, rowH));
                // the line between two white keys (E–F and B–C have no black key between them)
                if (!MidiNote.IsBlack(p + 1)) ctx.DrawLine(line, new Point(0, y + 0.5), new Point(KeyW, y + 0.5));
            }
            // with a scale: what isn't in it goes dark, its root gets a mark and every note its name
            bool inScale = scale == null || scale.Contains(p);
            if (!inScale && !lit) ctx.FillRectangle(Palette.Alpha(Palette.AppColor, 0.6), new Rect(0, y, KeyW, rowH));
            if (scale != null && scale.IsRoot(p)) ctx.FillRectangle(Palette.Accent, new Rect(KeyW - 4, y, 4, rowH));
            if (keyNames?.ContainsKey(p) == true && rowH >= 9)
            {
                DrawKeyName(ctx, p, y);
                continue;
            }
            if (rowH >= 9 && (scale == null ? p % 12 == 0 : inScale && rowH >= 11 || scale.IsRoot(p)))
            {
                // right-aligned like the user's key names, so a longer name ("Sol#3") grows to the left
                var name = Palette.Text(NoteNames.Of(p), 9.5, MidiNote.IsBlack(p) && scale != null ? Palette.Fg : Palette.App, Palette.Ui);
                ctx.DrawText(name, new Point(Math.Max(2, KeyW - 6 - name.Width), y + (rowH - 12) / 2));
            }
        }
    }

    void DrawRuler(DrawingContext ctx, double W)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(KeyW, 0, W - KeyW, RulerH));
        using var clip = ctx.PushClip(new Rect(KeyW, 0, Math.Max(0, W - KeyW), RulerH));
        // the track's clips as bands, like the timeline's boxes: the one being edited lit
        foreach (var c in others) DrawBand(ctx, StartBeat(c, bpm), c.LengthBeats, 0.08, 0.4, W);
        DrawBand(ctx, Origin, clipBeats, 0.16, 0.9, W);
        var tick = Palette.Pen(Palette.Alpha(Palette.LineColor, 1));
        // the timeline's bars: a number every bar, or every few when they get close
        int every = beatW * 4 >= 40 ? 1 : beatW * 4 >= 16 ? 4 : 16;
        int firstBar = Math.Max(0, (int)Math.Floor(BeatA(KeyW) / 4));
        double origin = Origin;
        for (int b = firstBar; XA(b * 4) <= W; b++)
        {
            double x = Math.Round(XA(b * 4)) + 0.5;
            ctx.DrawLine(tick, new Point(x, RulerH - 8), new Point(x, RulerH - 4));
            bool inside = b * 4 >= origin - 1e-9 && b * 4 < origin + clipBeats;
            if (b % every == 0) ctx.DrawText(BarLabel(b + 1), 10, inside ? Palette.Dim : Palette.Faint, new Point(x + 3, 4), Palette.Mono);
        }
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(KeyW, RulerH - 0.5), new Point(W, RulerH - 0.5));
    }

    /// <summary>A clip on the ruler: a tint over its stretch and a strip in the track's color along the bottom.</summary>
    void DrawBand(DrawingContext ctx, double start, double beats, double tint, double strip, double W)
    {
        double l = Math.Max(KeyW, XA(start)), r = Math.Min(W, XA(start + beats));
        if (r <= l) return;
        ctx.FillRectangle(Palette.Alpha(color, tint), new Rect(l, 0, r - l, RulerH));
        ctx.FillRectangle(Palette.Alpha(color, strip), new Rect(l, RulerH - 4, r - l, 3));
    }

    static readonly List<string> barLabels = [];
    static string BarLabel(int n)
    {
        while (barLabels.Count < n) barLabels.Add((barLabels.Count + 1).ToString());
        return barLabels[n - 1];
    }

    void DrawVelocity(DrawingContext ctx, double top, double W, double H)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(0, top - VelGap, W, H - top + VelGap));
        // the bar's handle, lit while it is grabbed or under the pointer
        bool hot = gesture == "velsize" || gesture == null && OnVelSplit(lastPointer);
        ctx.DrawRectangle(hot ? Palette.Accent : Palette.Faint, null, new Rect(W / 2 - 16, top - VelGap / 2 - 1.5, 32, 3), 1.5, 1.5);
        ctx.DrawText(LaneTitle, 10, Palette.Faint, new Point(6, top + 4), Palette.UiBold);
        double h = H - top - 6;
        // the scale: a label for each mark that has room, right against the lane
        double lastLabel = double.NegativeInfinity;
        foreach (int v in VelMarks)
        {
            double y = VelY(v);
            if (y - lastLabel < VelLabelGap) continue;
            var t = Palette.Text(VelNames[v], 9.5, v == MidiNote.DefaultVelocity ? Palette.Dim : Palette.Faint, Palette.Mono);
            ctx.DrawText(t, new Point(KeyW - 5 - t.Width, Math.Min(H - t.Height, y - t.Height / 2)));
            lastLabel = y;
        }
        using var clip = ctx.PushClip(new Rect(KeyW, top, Math.Max(0, W - KeyW), Math.Max(0, H - top)));
        if (velStep > 1 && h * velStep / 127.0 >= MinGridPx)
        {
            var step = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.35));
            for (int v = velStep; v < 127; v += velStep)
            {
                double y = Math.Round(VelY(v)) + 0.5;
                ctx.DrawLine(step, new Point(KeyW, y), new Point(W, y));
            }
        }
        foreach (int v in VelMarks)
        {
            double y = Math.Round(VelY(v)) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.LineColor, v == MidiNote.DefaultVelocity ? 1 : 0.7)), new Point(KeyW, y), new Point(W, y));
        }
        // the other clips' velocities, faint (they are edited once their clip is picked)
        var faint = Palette.Alpha(color, 0.25);
        foreach (var c in others)
        {
            double start = StartBeat(c, bpm);
            if (XA(start + c.LengthBeats) < KeyW || XA(start) > W) continue;
            for (int pass = 0; pass < c.Passes; pass++)
                foreach (var n in c.Notes)
                {
                    if (!c.Heard(n, pass, out double from, out _)) continue;
                    double x = XA(start + from);
                    if (x < KeyW - 4 || x > W) continue;
                    double bh = Math.Max(2, h * LaneOf(n) / 127.0);
                    ctx.FillRectangle(faint, new Rect(Math.Round(x), H - 3 - bh, 3, bh));
                }
        }
        foreach (var n in notes)
        {
            double x = X(n.Beat);
            if (x < KeyW - 4 || x > W) continue;
            double bh = Math.Max(2, h * LaneOf(n) / 127.0);
            var brush = selected.Contains(n) || velGroup.Contains(n) ? Palette.Accent : Palette.Alpha(color, 0.8);
            ctx.FillRectangle(brush, new Rect(Math.Round(x), H - 3 - bh, 3, bh));
            ctx.FillRectangle(brush, new Rect(Math.Round(x) - 2, H - 3 - bh, 7, 2));
        }
        if (gesture == "velline" && moved)
            ctx.DrawLine(Palette.Pen(Palette.Fg, 1.5), new Point(down.X, VelY(VelAt(down.Y))), new Point(lastPointer.X, VelY(VelAt(lastPointer.Y))));
        // the value being set, by the pointer
        if (gesture is "velocity" or "velline" or "velreset" && shownVel >= 0)
        {
            var t = Palette.Text(VelNames[shownVel], 11, Palette.Fg, Palette.Mono);
            double tx = Math.Min(W - t.Width - 4, lastPointer.X + 10), ty = Math.Clamp(VelY(shownVel) - t.Height - 2, top, Math.Max(top, H - t.Height));
            ctx.FillRectangle(Palette.Alpha(Palette.AppColor, 0.75), new Rect(tx - 3, ty, t.Width + 6, t.Height));
            ctx.DrawText(t, new Point(tx, ty));
        }
    }

    // --- gestures ---------------------------------------------------------------------------------

    string? gesture;
    Point down, lastPointer;
    bool moved, changed;
    KeyModifiers modifiers;
    /// <summary>The note grabbed and, for every selected note, where it was when the gesture began.</summary>
    MidiNote? held;
    readonly Dictionary<MidiNote, (int Pitch, double Beat, double Beats)> from = [];
    MidiNote? velNote;
    /// <summary>The notes a velocity drag sets: the bar grabbed, or every selected note starting where it does.</summary>
    readonly List<MidiNote> velGroup = [];
    /// <summary>Every note's velocity when a line began: the line is redrawn from them on each move.</summary>
    readonly Dictionary<MidiNote, int> velBase = [];
    /// <summary>Where the last paint stroke reached (its sweep runs from there), and the value shown by the pointer (-1 none).</summary>
    double velPaintX;
    int shownVel = -1;
    double velFrom;
    int heardPitch = -1;
    double downScrollX, downScrollY;
    readonly DispatcherTimer edgeScroll;
    bool scrubHeld;
    /// <summary>The notes copied (Ctrl+C / Ctrl+X), shared by every clip so they paste into another one too.</summary>
    static readonly List<MidiNote> clipboard = [];

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (clipId == null) return;
        var p = e.GetPosition(this);
        lastPointer = down = p;
        modifiers = e.KeyModifiers;
        var props = e.GetCurrentPoint(this).Properties;
        moved = changed = false;
        // a click on the notes settles what the tools panel is showing, and does only that
        if (toolBase != null && p.Y >= RulerH)
        {
            ToolInterrupted?.Invoke();
            e.Handled = true;
            return;
        }
        downScrollX = scrollX;
        downScrollY = scrollY;
        if (props.IsMiddleButtonPressed) { Start("pan", e); return; }
        if (props.IsLeftButtonPressed && ChordChipAt(p) is >= 0 and var inversion)
        {
            SetInversion(inversion);
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed && OnCellEdge(p))
        {
            cellFrom = clipBeats;
            Start("cell", e);
            return;
        }
        // inside another clip of the track (not on a note of this one): that clip is the one edited from here on
        if (p.Y >= RulerH && p.Y < GridBottom && p.X >= KeyW && NoteAt(p) == null)
        {
            double at = Beat(p.X);
            string? was = clipId;
            if ((at < 0 || at >= clipBeats) && OtherAt(BeatA(p.X)) is { } other) ClipPicked?.Invoke(other.Id);
            // a pattern opens on its own view: the point clicked means nothing there
            if (clipId == null || patternId != null && clipId != was) return;
        }
        if (props.IsRightButtonPressed)
        {
            if (velShown && p.Y > GridBottom + VelGap && p.X >= KeyW)
            {
                StartVelocity(p);
                Start("velreset", e);
                VelPaint(MidiNote.DefaultVelocity);
                return;
            }
            if (p.Y < RulerH && p.X >= KeyW)
            {
                GridMenuRequested?.Invoke();
                e.Handled = true;
                return;
            }
            if (p.X < KeyW && p.Y >= RulerH && p.Y < GridBottom)
            {
                KeyMenuRequested?.Invoke(PitchAt(p.Y));
                e.Handled = true;
                return;
            }
            if (p.Y >= RulerH && p.Y < GridBottom && p.X >= KeyW)
            {
                if (NoteAt(p) == null && selected.Count > 0)
                {
                    selected.Clear();
                    InvalidateVisual();
                }
                Start("erase", e);
                EraseAt(p);
            }
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        if (p.Y < RulerH)
        {
            if (p.X >= KeyW)
            {
                // the click is one jump, on press; a real drag holds playback and follows the pointer (ScrubChanged)
                Scrub();
                Start("scrub", e);
                edgeScroll.Start();
            }
            e.Handled = true;
            return;
        }
        if (OnVelSplit(p))
        {
            if (e.ClickCount == 2)
            {
                VelocityHeight = DefaultVelH;
                VelocityHeightChanged?.Invoke(velH);
            }
            else
            {
                velFrom = VelH;
                Start("velsize", e);
            }
            e.Handled = true;
            return;
        }
        if (p.Y > GridBottom)
        {
            StartVelocity(p);
            if (VelocityTool == VelTool.Line)
            {
                velBase.Clear();
                foreach (var n in notes) velBase[n] = LaneOf(n);
                velGroup.Clear();
                Start("velline", e);
                shownVel = VelAt(p.Y);
                InvalidateVisual();
                return;
            }
            Start("velocity", e);
            VelPaint(VelAt(p.Y));
            return;
        }
        if (p.X < KeyW)
        {
            heardPitch = PitchAt(p.Y);
            NoteAuditioned?.Invoke(heardPitch, lastVelocity);
            Start("keys", e);
            InvalidateVisual();
            return;
        }
        var hit = NoteAt(p);
        bool ctrl = (modifiers & KeyModifiers.Control) != 0;
        if (hit != null && !ctrl && e.ClickCount == 2)
        {
            NoteDetailsRequested?.Invoke(hit);
            e.Handled = true;
            return;
        }
        if (hit == null && ctrl)
        {
            bool shape = (modifiers & KeyModifiers.Alt) != 0;
            if (shape) StartLasso();
            Start(shape ? "lasso" : "box", e);
            return;
        }
        if (!ctrl && WriteTool == PianoTool.Slice)
        {
            Start("slice", e);
            return;
        }
        if (!ctrl && hit == null && WriteTool == PianoTool.Paint)
        {
            double painted = Beat(p.X);
            if (painted < 0 || painted >= clipBeats) return;
            selected.Clear();
            paintFrom = -1;
            Start("paint", e);
            PaintAt(p);
            return;
        }
        if (hit == null)
        {
            // outside the clip nothing is written (it would not be heard): a click there only drops the selection
            double at = Beat(p.X);
            if (at < 0 || at >= clipBeats)
            {
                selected.Clear();
                InvalidateVisual();
                return;
            }
            // FL's pencil: a note in the cell under the pointer, with the last length, and it follows the drag
            double beat = Math.Max(0, Snap(at, floor: true));
            selected.Clear();
            changed = true;
            if (chordTool && MidiStamps.Find(chordKind) is { } stamp)
            {
                var added = MidiStamps.Notes(stamp, WritePitch(p.Y), beat, scale, lastVelocity).Where(n => n.Beat < clipBeats - 1e-9).ToList();
                notes.AddRange(added);
                selected.UnionWith(added);
                if (added.Count == 0) { changed = false; return; }
                hit = added[0];
            }
            else if (chordTool && ChordOn(WritePitch(p.Y)) is { Length: > 0 } chord)
            {
                // the chord is selected, so it moves as one and its bar comes up as soon as it lands
                var added = chord.Order().Select(pitch => new MidiNote { Pitch = pitch, Beat = beat, Beats = lastBeats, Velocity = lastVelocity }).ToList();
                notes.AddRange(added);
                selected.UnionWith(added);
                hit = added[0];
                foreach (var n in added.Skip(1)) NoteAuditioned?.Invoke(n.Pitch, n.Velocity);
            }
            else
            {
                hit = new MidiNote { Pitch = WritePitch(p.Y), Beat = beat, Beats = lastBeats, Velocity = lastVelocity };
                notes.Add(hit);
            }
        }
        else if (ctrl)
        {
            if (!selected.Remove(hit)) selected.Add(hit);
            InvalidateVisual();
            return;
        }
        if (!selected.Contains(hit))
        {
            selected.Clear();
            selected.Add(hit);
        }
        WithGroups();
        held = hit;
        lastBeats = hit.Beats;
        lastVelocity = hit.Velocity;
        from.Clear();
        foreach (var n in selected) from[n] = (n.Pitch, n.Beat, n.Beats);
        heardPitch = hit.Pitch;
        NoteAuditioned?.Invoke(hit.Pitch, hit.Velocity);
        bool copy = (modifiers & KeyModifiers.Shift) != 0 && !changed;
        Start(copy ? "copy" : OnEdge(hit, p) && !changed ? "resize" : OnStartEdge(hit, p) && !changed ? "resizeStart" : "move", e);
        InvalidateVisual();
    }

    void Scrub() => SeekRequested?.Invoke((patternStart + SnapA(BeatA(lastPointer.X))) * 60 / bpm);

    /// <summary>
    /// The Shift drag's first move: copies of the selected notes take their place in the drag (and the selection), the
    /// originals stay where they were.
    /// </summary>
    void StartCopy()
    {
        var originals = from.ToList();
        from.Clear();
        selected.Clear();
        foreach (var (n, f) in originals)
        {
            var c = n.Copy();
            notes.Add(c);
            selected.Add(c);
            from[c] = f;
            if (n == held) held = c;
        }
        changed = true;
    }

    /// <summary>Takes out of the clip the given notes that start past its end (a copy only lands inside it).</summary>
    void DropOutside(IEnumerable<MidiNote> added)
    {
        foreach (var n in added.Where(n => n.Beat >= clipBeats - 1e-9).ToList())
        {
            notes.Remove(n);
            selected.Remove(n);
        }
    }

    /// <summary>
    /// Adds copies of <paramref name="source"/> moved <paramref name="offset"/> beats, inside the clip, as the new
    /// selection, and commits; false when none of them fits.
    /// </summary>
    bool AddCopies(IEnumerable<MidiNote> source, double offset)
    {
        var added = source.Select(n => { var c = n.Copy(); c.Beat = n.Beat + offset; return c; })
            .Where(c => c.Beat >= 0 && c.Beat < clipBeats - 1e-9).ToList();
        if (added.Count == 0) return false;
        selected.Clear();
        notes.AddRange(added);
        selected.UnionWith(added);
        Commit();
        return true;
    }

    void Start(string kind, PointerPressedEventArgs e)
    {
        gesture = kind;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        lastPointer = e.GetPosition(this);
        modifiers = e.KeyModifiers;
        if (gesture == null) { UpdateCursor(); return; }
        if (!moved && Math.Abs(lastPointer.X - down.X) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
        moved = true;
        switch (gesture)
        {
            case "pan":
                scrollX = downScrollX - (lastPointer.X - down.X);
                scrollY = downScrollY - (lastPointer.Y - down.Y);
                ClampScroll();
                break;
            case "move": DragMove(); break;
            case "copy":
                if (!changed) StartCopy();
                DragMove();
                break;
            case "scrub":
                if (!scrubHeld && Math.Abs(lastPointer.X - down.X) >= ScrubSlop)
                {
                    scrubHeld = true;
                    ScrubChanged?.Invoke(true);
                }
                if (scrubHeld) Scrub();
                break;
            case "resize": DragResize(); break;
            case "resizeStart": DragResizeStart(); break;
            case "cell": DragCell(); break;
            case "erase": EraseAt(lastPointer); break;
            case "paint": PaintAt(lastPointer); break;
            case "lasso": lasso.Add(lastPointer); break;
            case "velocity": VelPaint(VelAt(lastPointer.Y)); break;
            case "velreset": VelPaint(MidiNote.DefaultVelocity); break;
            case "velline": VelLine(); break;
            case "velsize":
                velH = Math.Clamp(velFrom - (lastPointer.Y - down.Y), MinVelH, Math.Max(MinVelH, Bounds.Height - RulerH - VelGap - MinGridH));
                ClampScroll();
                break;
            case "keys":
                int k = PitchAt(lastPointer.Y);
                if (k != heardPitch) { heardPitch = k; NoteAuditioned?.Invoke(k, lastVelocity); }
                break;
        }
        InvalidateVisual();
    }

    void DragMove()
    {
        if (held == null) return;
        var (p0, b0, _) = from[held];
        int rowFrom = PitchAt(down.Y), rowTo = PitchAt(lastPointer.Y);
        double db = Snap(b0 + (lastPointer.X - down.X) / beatW) - b0;
        // never before the clip's start
        foreach (var (_, f) in from) db = Math.Max(db, -f.Beat);
        int before = held.Pitch;
        if (scale is { } key && (modifiers & KeyModifiers.Alt) == 0)
        {
            // along the scale, each note by the same degrees: a chord stays in key. Only sideways, nothing changes pitch
            int degrees = key.Degree(rowTo) - key.Degree(rowFrom);
            bool fits = from.All(x => degrees == 0 || key.Move(x.Value.Pitch, degrees) is >= MidiNote.MinPitch and <= MidiNote.MaxPitch);
            foreach (var (n, f) in from)
            {
                if (fits) n.Pitch = degrees == 0 ? f.Pitch : key.Move(f.Pitch, degrees);
                n.Beat = f.Beat + db;
            }
        }
        else
        {
            // the group stays inside the keyboard
            int dp = rowTo - rowFrom;
            foreach (var (_, f) in from) dp = Math.Clamp(dp, MidiNote.MinPitch - f.Pitch, MidiNote.MaxPitch - f.Pitch);
            foreach (var (n, f) in from)
            {
                n.Pitch = f.Pitch + dp;
                n.Beat = f.Beat + db;
            }
        }
        changed |= held.Pitch != p0 || db != 0;
        if (held.Pitch != before) { heardPitch = held.Pitch; NoteAuditioned?.Invoke(held.Pitch, held.Velocity); }
    }

    void DragResize()
    {
        if (held == null) return;
        var (_, b0, l0) = from[held];
        double end = Snap(b0 + l0 + (lastPointer.X - down.X) / beatW);
        double dl = Math.Max(end - b0, SnapBeats > 0 && (modifiers & KeyModifiers.Alt) == 0 ? SnapBeats : MidiNote.MinBeats) - l0;
        foreach (var (n, f) in from) n.Beats = Math.Max(MidiNote.MinBeats, f.Beats + dl);
        lastBeats = held.Beats;
        changed |= dl != 0;
    }

    /// <summary>The left end dragged: the notes selected start where the pointer lands, their ends where they were.</summary>
    void DragResizeStart()
    {
        if (held == null) return;
        var (_, b0, l0) = from[held];
        double start = Math.Max(0, Snap(b0 + (lastPointer.X - down.X) / beatW));
        double dBeat = Math.Min(start, b0 + l0 - (SnapBeats > 0 && (modifiers & KeyModifiers.Alt) == 0 ? SnapBeats : MidiNote.MinBeats)) - b0;
        // no note of the selection starts before 0 or ends under its minimum
        foreach (var (_, f) in from)
        {
            dBeat = Math.Max(dBeat, -f.Beat);
            dBeat = Math.Min(dBeat, f.Beats - MidiNote.MinBeats);
        }
        foreach (var (n, f) in from)
        {
            n.Beat = f.Beat + dBeat;
            n.Beats = f.Beats - dBeat;
        }
        lastBeats = held.Beats;
        changed |= dBeat != 0;
    }

    /// <summary>The pattern's loop end dragged by grid steps (a sixteenth with the grid off, never under one).</summary>
    void DragCell()
    {
        double beats = Snap(cellFrom + (lastPointer.X - down.X) / beatW);
        clipBeats = Math.Max(MidiClip.MinBeats, beats);
        InvalidateVisual();
    }

    void EraseAt(Point p)
    {
        if (NoteAt(p) is not { } n) return;
        notes.Remove(n);
        selected.Remove(n);
        changed = true;
        InvalidateVisual();
    }

    MidiNote? VelocityNoteAt(double x)
    {
        MidiNote? best = null;
        double bestD = 6;
        foreach (var n in notes)
        {
            double d = Math.Abs(X(n.Beat) + 1.5 - x);
            // a selected note wins a tie: the one being worked on
            if (d < bestD || d == bestD && best != null && selected.Contains(n)) { best = n; bestD = d; }
        }
        return best;
    }

    /// <summary>A press in the velocity lane: the bar under it and the notes it sets (<see cref="velGroup"/>).</summary>
    void StartVelocity(Point p)
    {
        velNote = VelocityNoteAt(p.X);
        velGroup.Clear();
        velPaintX = p.X;
        if (velNote == null) return;
        if (selected.Contains(velNote))
        {
            foreach (var n in selected)
                if (Math.Abs(n.Beat - velNote.Beat) < 1e-9) velGroup.Add(n);
        }
        else velGroup.Add(velNote);
    }

    /// <summary>The value at a y of the lane, to <see cref="VelocityStep"/> (or 127) unless Alt is held.</summary>
    int VelAt(double y) => SnapVel(Math.Clamp((int)Math.Round((Bounds.Height - 3 - y) / VelLaneH * 127), 1, 127));

    int SnapVel(int v)
    {
        if (velStep <= 1 || (modifiers & KeyModifiers.Alt) != 0) return v;
        int q = (int)Math.Round(v / (double)velStep) * velStep;
        if (127 - v < Math.Abs(v - q)) q = 127;
        return Math.Clamp(q, 1, 127);
    }

    /// <summary>The notes a sweep of the lane edits: the selected ones if there are some, else all.</summary>
    IEnumerable<MidiNote> VelTargets => selected.Count > 0 ? selected : notes;

    /// <summary>
    /// The pencil (and the right button's reset): the bar pressed (its chord, if selected) and every bar swept since the
    /// last move take the value. The other notes on the pressed bar's beat stay as they were, as before.
    /// </summary>
    void VelPaint(int v)
    {
        shownVel = v;
        double a = Math.Min(velPaintX, lastPointer.X) - 2, b = Math.Max(velPaintX, lastPointer.X) + 2;
        velPaintX = lastPointer.X;
        bool any = false;
        foreach (var n in velGroup) any |= SetVel(n, v);
        foreach (var n in VelTargets)
        {
            double x = X(n.Beat) + 1.5;
            if (x < a || x > b || velNote != null && !velGroup.Contains(n) && Math.Abs(n.Beat - velNote.Beat) < 1e-9) continue;
            any |= SetVel(n, v);
        }
        if (any && gesture != "velreset" && lane == NoteLane.Velocity) lastVelocity = v;
        InvalidateVisual();
    }

    bool SetVel(MidiNote n, int v)
    {
        if (gesture == "velreset") NoteLanes.Reset(n, lane);
        else if (LaneOf(n) == v) return false;
        else NoteLanes.Set(n, lane, v);
        changed = true;
        return true;
    }

    /// <summary>The line: from where it was pressed to the pointer, the bars between them on the ramp, the rest as they were.</summary>
    void VelLine()
    {
        int v0 = VelAt(down.Y), v1 = VelAt(lastPointer.Y);
        shownVel = v1;
        foreach (var (n, v) in velBase) NoteLanes.Set(n, lane, v);
        double x0 = down.X, x1 = lastPointer.X, lo = Math.Min(x0, x1) - 2, hi = Math.Max(x0, x1) + 2;
        foreach (var n in VelTargets)
        {
            double x = X(n.Beat) + 1.5;
            if (x < lo || x > hi) continue;
            double t = Math.Abs(x1 - x0) < 1 ? 1 : Math.Clamp((x - x0) / (x1 - x0), 0, 1);
            NoteLanes.Set(n, lane, SnapVel(Math.Clamp((int)Math.Round(v0 + (v1 - v0) * t), 1, 127)));
        }
        changed = velBase.Any(kv => LaneOf(kv.Key) != kv.Value);
    }

    Rect BoxRect() => new(new Point(Math.Min(down.X, lastPointer.X), Math.Min(down.Y, lastPointer.Y)),
        new Point(Math.Max(down.X, lastPointer.X), Math.Max(down.Y, lastPointer.Y)));

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gesture == null) return;
        if (gesture == "box")
        {
            if ((modifiers & KeyModifiers.Shift) == 0 && (modifiers & KeyModifiers.Control) == 0) selected.Clear();
            var box = BoxRect();
            foreach (var n in notes) if (box.Intersects(NoteRect(n))) selected.Add(n);
            WithGroups();
        }
        if (gesture == "lasso") EndLasso();
        if (gesture == "slice") Slice();
        bool resized = gesture == "velsize" && moved;
        EndGesture(e.Pointer);
        if (changed) Commit();
        if (resized) VelocityHeightChanged?.Invoke(velH);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (gesture == null) return;
        EndGesture(null);
        if (changed) Commit();
    }

    void EndGesture(IPointer? pointer)
    {
        if (gesture == "cell" && moved && clipBeats != cellFrom) CellLengthChanged?.Invoke(clipBeats);
        if (gesture == "copy" && changed)
        {
            DropOutside([.. selected]);
            // every copy fell past the clip's end: nothing was added
            if (selected.Count == 0) changed = false;
        }
        EndScrub();
        gesture = null;
        held = null;
        velNote = null;
        velGroup.Clear();
        velBase.Clear();
        shownVel = -1;
        heardPitch = -1;
        from.Clear();
        lasso.Clear();
        pointer?.Capture(null);
        InvalidateVisual();
    }

    void EndScrub()
    {
        edgeScroll.Stop();
        if (!scrubHeld) return;
        scrubHeld = false;
        ScrubChanged?.Invoke(false);
    }

    /// <summary>Drops a gesture in flight without committing it (another clip or an undo replaces the notes anyway).</summary>
    public void CancelGesture()
    {
        if (gesture == null) return;
        if (gesture == "cell") clipBeats = cellFrom;
        EndScrub();
        gesture = null;
        held = null;
        velNote = null;
        velGroup.Clear();
        velBase.Clear();
        shownVel = -1;
        heardPitch = -1;
        from.Clear();
        changed = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Moves the selected notes (all of them with none selected) to the magnet's steps on the song's grid, as one edit
    /// (<see cref="MidiEditing.Quantize(IList{MidiNote}, double, double, double, IReadOnlyCollection{MidiNote}?)"/>).
    /// Returns how many moved; -1 with no clip or a magnet with no fixed length.
    /// </summary>
    public int Quantize()
    {
        if (clipId == null || QuantizeBeats <= 0) return -1;
        CancelGesture();
        int moved = MidiEditing.Quantize(notes, clipAtSec, bpm, QuantizeBeats, selected.Count > 0 ? selected : null);
        if (moved > 0) Commit();
        return moved;
    }

    /// <summary>
    /// Scales the selected notes' velocities (all of them with none selected) around their average, as one edit
    /// (<see cref="MidiEditing.ScaleVelocity"/>). Returns how many changed; -1 with no clip.
    /// </summary>
    public int ScaleVelocity(double percent)
    {
        if (clipId == null) return -1;
        CancelGesture();
        int n = MidiEditing.ScaleVelocity(notes, percent, selected.Count > 0 ? selected : null);
        if (n > 0) Commit();
        InvalidateVisual();
        return n;
    }

    static readonly Random humanize = new();

    /// <summary>Humanizes the selected notes' velocities (all with none selected), as one edit; -1 with no clip.</summary>
    public int HumanizeVelocity(int amount)
    {
        if (clipId == null) return -1;
        CancelGesture();
        int n = MidiEditing.HumanizeVelocity(notes, amount, humanize, selected.Count > 0 ? selected : null);
        if (n > 0) Commit();
        InvalidateVisual();
        return n;
    }

    /// <summary>The notes the velocity tools change: the selected ones, or every note of the clip.</summary>
    public int ToolTargets => selected.Count > 0 ? selected.Count : notes.Count;

    /// <summary>
    /// Takes the selected notes (all of them with none selected) to the nearest note of the scale, as one edit
    /// (<see cref="MidiEditing.FitScale"/>). Returns how many moved; -1 with no clip or no scale.
    /// </summary>
    public int FitToScale()
    {
        if (clipId == null || scale == null) return -1;
        CancelGesture();
        int moved = MidiEditing.FitScale(notes, scale, selected.Count > 0 ? selected : null);
        if (moved > 0) Commit();
        InvalidateVisual();
        return moved;
    }

    void Commit()
    {
        if (clipId == null) return;
        notes.Sort(MidiNote.Order);
        NotesCommitted?.Invoke(clipId, [.. notes.Select(n => n.Copy())]);
    }

    void UpdateCursor()
    {
        var p = lastPointer;
        var c = StandardCursorType.Arrow;
        if (clipId != null && ChordChipAt(p) >= 0) c = StandardCursorType.Hand;
        else if (clipId != null && OnCellEdge(p)) c = StandardCursorType.SizeWestEast;
        else if (clipId != null && p.X >= KeyW && p.Y >= RulerH && p.Y < GridBottom)
            c = NoteAt(p) is { } n ? (OnEdge(n, p) ? StandardCursorType.RightSide : OnStartEdge(n, p) ? StandardCursorType.LeftSide : StandardCursorType.SizeAll)
                : Beat(p.X) >= 0 && Beat(p.X) < clipBeats || OtherAt(BeatA(p.X)) != null ? StandardCursorType.Cross : StandardCursorType.Arrow;
        else if (p.X < KeyW && p.Y >= RulerH && p.Y < GridBottom) c = StandardCursorType.Hand;
        if (clipId != null && OnVelSplit(p)) c = StandardCursorType.SizeNorthSouth;
        InvalidateVisual(); // the bar's handle lights under the pointer
        Cursor = cursors.TryGetValue(c, out var cur) ? cur : cursors[c] = new Cursor(c);
    }

    static readonly Dictionary<StandardCursorType, Cursor> cursors = [];

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (clipId == null) return;
        var p = e.GetPosition(this);
        double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        // the same modifiers as the timeline (Configuración → Atajos): zoom, sideways, height
        switch (Shortcuts.FindWheel(e.KeyModifiers))
        {
            case "wheelZoom":
            {
                double at = BeatA(p.X);
                beatW = Math.Clamp(beatW * Math.Pow(1.2, d * Shortcuts.WheelSign("wheelZoom")), MinBeatW, MaxBeatW);
                scrollX = at * beatW - (p.X - KeyW);
                break;
            }
            case "wheelSideways":
                scrollX -= d * 60 * Shortcuts.WheelSign("wheelSideways");
                break;
            case "wheelHeight":
            {
                int at = PitchAt(p.Y);
                double offset = p.Y - Y(at);
                rowH = Math.Clamp(rowH + d * Shortcuts.WheelSign("wheelHeight"), MinRowH, MaxRowH);
                scrollY = (Pitches - 1 - at) * rowH + RulerH - p.Y + offset;
                break;
            }
            default:
                scrollY -= d * rowH * 3 * Shortcuts.WheelSign(Shortcuts.WheelScroll);
                break;
        }
        ClampScroll();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampScroll();
    }

    // the chord's preview leaves with the pointer
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        InvalidateVisual();
    }

    /// <summary>
    /// The piano roll's keys while it has the focus: Supr / Retroceso erase the selected notes, Ctrl+A selects them all,
    /// ↑ ↓ move them a semitone (Shift: an octave), Esc drops the selection, and the timeline's copy, cut, paste and
    /// duplicate (<see cref="Shortcuts"/>) do it with the notes. True when it used the key.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (clipId == null || !IsKeyboardFocusWithin) return false;
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (toolBase != null && e.Key is Key.Enter or Key.Escape)
        {
            ToolKeyPressed?.Invoke(e.Key == Key.Enter ? "tool-accept" : "tool-cancel");
            return true;
        }
        // used even with nothing to do: let through, they would copy or duplicate the timeline's selection
        switch (gesture == null ? Shortcuts.Find(e) : null)
        {
            case { } id when id.StartsWith(Shortcuts.PianoPrefix):
                ToolKeyPressed?.Invoke(id[Shortcuts.PianoPrefix.Length..]);
                return true;
            case "copy" or "cut":
                if (selected.Count == 0) return true;
                clipboard.Clear();
                clipboard.AddRange(selected.OrderBy(n => n.Beat).Select(n => n.Copy()));
                if (Shortcuts.Find(e) == "cut")
                {
                    notes.RemoveAll(selected.Contains);
                    selected.Clear();
                    Commit();
                }
                return true;
            case "paste":
            {
                if (clipboard.Count == 0) return true;
                double first = clipboard.Min(n => n.Beat), play = PlayBeat(playheadSec) - Origin;
                double at = playheadSec >= 0 && play >= 0 && play < clipBeats ? Snap(play, floor: true) : first;
                AddCopies(clipboard, at - first);
                return true;
            }
            case "duplicate":
            {
                if (selected.Count == 0) return true;
                // right after the selection, a whole number of magnet steps on, as the timeline's duplicate
                double start = selected.Min(n => n.Beat), span = selected.Max(n => n.Beat + n.Beats) - start;
                double magnet = SnapBeats;
                double step = magnet > 0 ? Math.Max(magnet, Math.Ceiling(span / magnet - 1e-9) * magnet) : span;
                AddCopies([.. selected], step);
                return true;
            }
        }
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                // used even with nothing selected: let through, it would remove the whole clip in the timeline
                if (selected.Count == 0) return true;
                notes.RemoveAll(selected.Contains);
                selected.Clear();
                Commit();
                return true;
            case Key.A when ctrl:
                selected.Clear();
                selected.UnionWith(notes);
                InvalidateVisual();
                return true;
            case Key.Left or Key.Right when (e.KeyModifiers & KeyModifiers.Shift) != 0 && !ctrl:
            {
                if (selected.Count == 0) return true;
                // a magnet step (a 1/16 when free), never out of the clip
                double db = (e.Key == Key.Right ? 1 : -1) * (SnapBeats > 0 ? SnapBeats : 0.25);
                if (selected.Any(n => n.Beat + db < -1e-9 || n.Beat + db >= clipBeats - 1e-9)) return true;
                foreach (var n in selected) n.Beat = Math.Max(0, n.Beat + db);
                Commit();
                return true;
            }
            case >= Key.D1 and <= Key.D4 when e.KeyModifiers == KeyModifiers.None && chordVoicings.Length > 0:
                SetInversion(e.Key - Key.D1);
                return true;
            case Key.Up or Key.Down when selected.Count > 0:
                int sign = e.Key == Key.Up ? 1 : -1, step = sign * (ctrl ? 12 : 1);
                // a semitone is a note of the scale when there is one; the octave is the octave
                var key = ctrl ? null : scale;
                int Target(int pitch) => key?.Move(pitch, sign) ?? pitch + step;
                if (selected.Any(n => Target(n.Pitch) is < MidiNote.MinPitch or > MidiNote.MaxPitch)) return true;
                foreach (var n in selected) n.Pitch = Target(n.Pitch);
                if (selected.Count == 1) NoteAuditioned?.Invoke(selected.First().Pitch, selected.First().Velocity);
                Commit();
                return true;
            case Key.Escape when gesture != null:
            {
                // what the gesture did stays, as if it had been released
                EndGesture(null);
                if (changed) Commit();
                return true;
            }
            case Key.Escape when selected.Count > 0:
                selected.Clear();
                InvalidateVisual();
                return true;
        }
        return false;
    }
}
