using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Model;
using XtremeDaw.App.Video;
using XtremeDaw.Projects;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The timeline: bar ruler, one lane per track with its clips and waveforms, sticky track headers
/// Port of the web app's Timeline.tsx gestures, handles, snap and lane controls.
/// Native differences: an empty lane is just empty (the user asked to drop the web's dashed box and its "Apretá R" hint);
/// drag previews are local and commit on release (the window applies them in place, still playing);
/// multiple clips per track swap only with the clip under the pointer. Thumbnails are not ported yet.
/// The selected clip shows its volume line (clip gain + <see cref="Clip.GainCurve"/>, native only): a click on it
/// adds a point, a point drags in time and level, a double click removes it.
/// Track folders (native only): a folder has its own shorter row with a summary of its children's clips, its children
/// are indented inside a thin frame of its color and fold away with it. Rows are laid out in pixels (<see cref="Relayout"/>), so a dragged
/// track can drop into a folder, out of it or between folders; the rows make room where it will land and the folder it
/// goes into lights up.
/// </summary>
public sealed partial class TimelineView : Control
{
    /// <summary>The width the header rows are laid out for; a wider column gives the rest to the volume slider.</summary>
    const double LayoutHeaderW = 264;
    public const double MinHeaderW = 94, DefaultHeaderW = 324, MaxHeaderW = 640;
    /// <summary>Below this width the headers go compact (asked by the user): the track's icon, M, S, ● and its meters.</summary>
    public const double CompactAt = 276;
    /// <summary>Minimum px between minor ruler ticks before they get their own small number.</summary>
    const double MinorLabelPx = 30;
    /// <summary>Deepest horizontal zoom: two pixels per sample at 48 kHz, so single samples can be seen (native only).</summary>
    public const double MaxPps = 96000;
    /// <summary>Track height limits; once the header's effect knobs reach their biggest size a taller lane only gives the wave more room.</summary>
    public const double MinTrackH = Track.MinHeight, MaxTrackH = Track.MaxHeight;
    /// <summary>The zoom bar under the track headers (MainWindow.axaml) covers the column's bottom: the rows scroll above it.</summary>
    public const double FooterH = 40;
    const double BaseHeaderH = 64; // the header's fixed part (name, volume, pan); taller lanes add the effect knobs under it
    /// <summary>The effect knobs' row: smallest and biggest diameter, the space above them and their label line under them.</summary>
    const double FxKnobMin = 20, FxKnobMax = 64, FxPadTop = 4, FxLabelH = 16, FxPadBottom = 6;
    /// <summary>Room kept between the knobs' row and the header meters at the right edge.</summary>
    const double FxMeterGap = 16;
    /// <summary>Knob drag: px of travel for the whole range (Shift = fine, ×0.2), as the mixer's big knobs.</summary>
    const double FxDragPx = 160;
    /// <summary>Width of the track-header column; dragged from its grip at the right edge (native only).</summary>
    public double HeaderW { get; private set; } = DefaultHeaderW;
    bool Compact => HeaderW < CompactAt;
    /// <summary>The rect of a control a compact header doesn't have: off the view, so no press or hover ever lands on it.</summary>
    static readonly Rect NoRect = new(-100, -100, 0, 0);
    /// <summary>The column's resize grip: a strip inside the header, so it never shares pixels with a clip at 0 s.</summary>
    const double GripW = 8;
    /// <summary>Where the header's right-aligned controls end, left of the grip.</summary>
    double HeaderRight => HeaderW - GripW;
    bool OnGrip(Point p) => p.Y >= RulerH && p.X >= HeaderRight && p.X < HeaderW;
    public event Action<double>? HeaderWidthChanged;
    double originalHeaderW;
    int fxRow;
    /// <summary>The dragged knob's value and the pointer's last height: the drag is relative, so Shift mid-drag doesn't jump.</summary>
    double fxDragValue, fxDragY;
    /// <summary>The key <see cref="SetKnobs"/> takes for the sends' knobs, and the prefix of a send knob's <see cref="EffectRequested"/> key (then the bus id).</summary>
    public const string SendsKey = "sends", SendPrefix = "send:";
    /// <summary>Whether the headers show the sends' knobs (the user's pick, Configuración → Vista).</summary>
    bool sendsShown = true;
    /// <summary>
    /// The header's knobs (asked by the user: all of them dynamic, no fixed ones): one per send in the track's list, in
    /// the buses' order, with the bus's name (the label) and icon. Set by the window, which keeps the arrays until
    /// something is edited: it is asked while drawing and must not allocate.
    /// </summary>
    public Func<LiveTrack, SendKnob[]>? SendKnobs { get; set; }
    /// <summary>The level and ON of a track's send to a bus (off and 0 without one). Set by the window; it must not allocate.</summary>
    public Func<LiveTrack, string, (bool On, double Level)>? SendState { get; set; }
    /// <summary>How far a track's transpose is being made (0 to 1), null when it plays what it should; set by the window.</summary>
    public Func<LiveTrack, float?>? TransposeProgress { get; set; }
    /// <summary>How far the sound one clip is waiting for is, shown on that clip alone; null when it has it.</summary>
    public Func<LiveTrack, LiveClip, float?>? ClipRenderProgress { get; set; }
    /// <summary>How far "Detectar tempo" is on a clip (0 to 1), or null when it isn't listening to it.</summary>
    public Func<LiveClip, float?>? TempoProgress { get; set; }
    /// <summary>How far "Detectar escala y acordes" is on a clip (0 to 1), or null when it isn't listening to it.</summary>
    public Func<LiveClip, float?>? ScaleProgress { get; set; }
    /// <summary>How far "Extraer MIDI" is on a clip (0..1), null when it isn't taking notes out of it.</summary>
    public Func<LiveClip, float?>? ToMidiProgress { get; set; }
    /// <summary>How far "Separar en stems" is on a clip (0 to 1); null when it isn't separating that clip.</summary>
    public Func<LiveClip, float?>? StemsProgress { get; set; }
    /// <summary>"-24" to "+24", made once so a header with a transposed track allocates nothing per frame.</summary>
    static readonly string[] TransposeTags = [.. Enumerable.Range(-24, 49).Select(v => $"{v:+0;-0;0}")];
    /// <summary>"0" to "100", the knobs' readouts, made once so drawing allocates nothing.</summary>
    static readonly string[] Hundreds = [.. Enumerable.Range(0, 101).Select(v => $"{v}")];
    /// <summary>A send knob's edit from a header: track, <see cref="SendPrefix"/> + bus, toggle on/off, new level (a level above 0 also turns it on).</summary>
    public event Action<LiveTrack, string, bool, double?>? EffectRequested;

    /// <summary>Shows the sends' knobs in the headers when <paramref name="keys"/> has <see cref="SendsKey"/>; none hides the row.</summary>
    public void SetKnobs(IReadOnlyCollection<string> keys)
    {
        sendsShown = keys.Contains(SendsKey);
        if (gesture == "fx") gesture = null;
        InvalidateVisual();
    }

    public void SetHeaderWidth(double w)
    {
        w = Math.Clamp(Math.Round(w), MinHeaderW, MaxHeaderW);
        if (w == HeaderW) return;
        HeaderW = w;
        ClampScroll();
        waveCache.Clear(); // the waves are cached in window pixels, which shift with the column
        HeaderWidthChanged?.Invoke(w);
        InvalidateVisual();
    }

    /// <summary>Left edge of the effect knobs' row (past the handle column) and how far it stays from the meters.</summary>
    double FxLeft(int i) => KnobsLeft + Indent(i);
    double FxCellW(int i) => KnobCellW(FxLeft(i), KnobCount(i), FxFit(i));
    /// <summary>The knobs' diameter the lane's height allows under the header's fixed part, up to <see cref="FxKnobMax"/>.</summary>
    double FxFit(int i) => Math.Min(FxKnobMax, LaneH(i) - BaseHeaderH - FxPadTop - FxLabelH - FxPadBottom);

    /// <summary>
    /// Where the send knobs start, under the track's badge (a folder's under its fold arrow): they line up from the left
    /// (asked by the user), each in a cell as wide as its label needs (<see cref="KnobCellMin"/>) or its knob, instead
    /// of spreading over the header.
    /// </summary>
    const double KnobsLeft = 25, KnobCellMin = 72;
    double KnobCellW(double left, int n, double fit) => Math.Min((HeaderRight - FxMeterGap - left) / Math.Max(1, n), Math.Max(KnobCellMin, fit + 16));

    SendKnob[] SendsOf(LiveTrack t) => sendsShown ? SendKnobs?.Invoke(t) ?? [] : [];

    /// <summary>A folder's send knobs, like a track's (asked by the user): one per send in its list. Set by the window,
    /// which keeps the arrays until something is edited: it is asked while drawing and must not allocate.</summary>
    public Func<Folder, SendKnob[]>? FolderSendKnobs { get; set; }
    /// <summary>The level and ON of a folder's send to a bus (off and 0 without one). Set by the window; it must not allocate.</summary>
    public Func<Folder, string, (bool On, double Level)>? FolderSendState { get; set; }
    /// <summary>A folder's send knob edit: folder, bus, toggle on/off, new level (a level above 0 also turns it on).</summary>
    public event Action<Folder, string, bool, double?>? FolderSendRequested;
    /// <summary>The folder whose knob the "fx" gesture turns; null while it turns a track's.</summary>
    Folder? fxFolder;

    SendKnob[] SendsOf(Folder f) => sendsShown && !f.Separator ? FolderSendKnobs?.Invoke(f) ?? [] : [];

    /// <summary>A folder's title line when it has knobs under it (its buttons end at 29 px).</summary>
    const double FolderTitleH = 30;
    /// <summary>The knobs' diameter as the row's height allows (it can be resized), up to <see cref="FxKnobMax"/>.</summary>
    double FolderKnobFit(Folder f) => Math.Min(FxKnobMax, FolderRowH(f) - FolderKnobsTop(f) - FxPadTop - FxLabelH - FxPadBottom);
    /// <summary>The line under a folder's title with its volume and pan, as a track's header has them (#136: its fader is
    /// automated from there too).</summary>
    const double FolderControlsH = 30;
    /// <summary>A folder's row shows its volume and pan when it is tall enough for them under its title.</summary>
    bool FolderShowsControls(Folder f) => !Compact && !f.Separator && FolderRowH(f) >= FolderTitleH + FolderControlsH;
    /// <summary>Where a folder's send knobs start: under its title, and under its volume and pan when it shows them.</summary>
    double FolderKnobsTop(Folder f) => FolderTitleH + (FolderShowsControls(f) ? FolderControlsH : 0);
    Rect FolderVolumeRect(Folder f) => new(52, FolderY(f) + FolderTitleH + 3, 92 + Extra, 20);
    Rect FolderPanRect(Folder f) => new(186 + Extra, FolderY(f) + FolderTitleH + 1, 24, 24);
    /// <summary>A folder's volume and pan as its lanes have them now (the window's; without it, its own). It must not allocate.</summary>
    public Func<Folder, (double Volume, double Pan)>? FolderShown { get; set; }
    /// <summary>A folder's volume or pan from its header (the other one null).</summary>
    public event Action<Folder, double?, double?>? FolderChannelRequested;
    /// <summary>The folder whose volume or pan the "volume" and "pan-knob" gestures move; null while they move a track's.</summary>
    Folder? channelFolder;
    double FolderCellW(Folder f) => KnobCellW(KnobsLeft, SendsOf(f).Length, FolderKnobFit(f));
    /// <summary>The folder's knobs' diameter: what its row's height and its cells allow, 0 when even
    /// <see cref="FxKnobMin"/> doesn't fit (a row made short shows only its title).</summary>
    double FolderKnobDiameter(Folder f)
    {
        if (Compact || SendsOf(f).Length == 0) return 0;
        double d = Math.Floor(Math.Min(FolderKnobFit(f), FolderCellW(f) - 10));
        return d >= FxKnobMin ? d : 0;
    }
    Rect FolderKnobRect(Folder f, int k, double d) =>
        new(KnobsLeft + FolderCellW(f) * k, FolderY(f) + FolderKnobsTop(f) + FxPadTop, d, d);
    Rect FolderLabelRect(Folder f, int k, double d) =>
        new(KnobsLeft + FolderCellW(f) * k + d / 2 - FolderCellW(f) / 2, FolderY(f) + FolderKnobsTop(f) + FxPadTop + d + 2, FolderCellW(f), FxLabelH - 2);

    /// <summary>The folder knob under the point: its place in the folder's sends, and whether it is the dial or its label.</summary>
    (int Knob, bool Dial)? FolderFxHit(Folder f, Point p)
    {
        double d = FolderKnobDiameter(f);
        if (d == 0 || p.X >= HeaderW) return null;
        int n = SendsOf(f).Length;
        for (int k = 0; k < n; k++)
        {
            if (FolderKnobRect(f, k, d).Inflate(4).Contains(p)) return (k, true);
            if (FolderLabelRect(f, k, d).Contains(p)) return (k, false);
        }
        return null;
    }

    (bool On, double Amount) FolderFxState(Folder f, int k)
    {
        var sends = SendsOf(f);
        return k < sends.Length && FolderSendState?.Invoke(f, sends[k].BusId) is var (on, level) ? (on && level > 0, level) : (false, 0);
    }

    string FolderKnobBus(Folder f, int k) => SendsOf(f) is var s && k < s.Length ? s[k].BusId : "";

    /// <summary>How many knobs track <paramref name="i"/>'s header shows: one per send in its list.</summary>
    int KnobCount(int i) => SendsOf(tracks[i]).Length;

    /// <summary>The key a knob's edits go out with: <see cref="SendPrefix"/> and the bus (only on a gesture: it allocates).</summary>
    string KnobKey(LiveTrack t, int k) => SendsOf(t) is var s && k < s.Length ? SendPrefix + s[k].BusId : "";

    /// <summary>The knobs' diameter: as big as the lane height and the column width allow (asked by the user), 0 when
    /// even <see cref="FxKnobMin"/> doesn't fit and the row is hidden.</summary>
    double FxKnobD(int i)
    {
        if (Compact || KnobCount(i) == 0) return 0;
        double d = Math.Floor(Math.Min(FxFit(i), FxCellW(i) - 10));
        return d >= FxKnobMin ? d : 0;
    }
    Rect FxKnobRect(int i, int k, double d) => new(FxLeft(i) + FxCellW(i) * k, LaneY(i) + BaseHeaderH + FxPadTop, d, d);
    /// <summary>A knob's label: a cell's width centered under its knob.</summary>
    Rect FxLabelRect(int i, int k, double d) => new(FxLeft(i) + FxCellW(i) * k + d / 2 - FxCellW(i) / 2, LaneY(i) + BaseHeaderH + FxPadTop + d + 2, FxCellW(i), FxLabelH - 2);
    /// <summary>The knob under the point: its place in the track's sends, and whether it is the dial or its label.</summary>
    (int Knob, bool Dial)? FxHit(int i, Point p)
    {
        double d = FxKnobD(i);
        if (d == 0) return null;
        int n = KnobCount(i);
        for (int k = 0; k < n; k++)
        {
            if (FxKnobRect(i, k, d).Inflate(4).Contains(p)) return (k, true);
            if (FxLabelRect(i, k, d).Contains(p)) return (k, false);
        }
        return null;
    }

    /// <summary>A send knob's state: lit while it is on with some level.</summary>
    (bool On, double Amount) FxState(LiveTrack t, int k)
    {
        var sends = SendsOf(t);
        return k < sends.Length && SendState?.Invoke(t, sends[k].BusId) is var (on, level) ? (on && level > 0, level) : (false, 0);
    }

    IReadOnlyList<LiveTrack> tracks = [];
    double pps = 10; // pixels per second
    double scrollX;
    double laneH = Track.DefaultHeight;
    const double ShowThumbsAt = 52; // SHOW_THUMBS_AT: lower lanes have no room for the filmstrip
    double scrollY;
    string snapMode = "1/4";
    bool bars = true;
    Point lastPointer;
    string? gesture;
    IPointer? capturedPointer;
    Point down;
    double downScrollX, downScrollY;
    int fromIndex = -1, overIndex = -1;
    /// <summary>
    /// The dragged clip (a take's or a MIDI one) is over the empty space under the last row: dropped there it goes to a
    /// new track of its kind (asked by the user, #309). Shown as a "new track" lane right under the rows.
    /// </summary>
    bool toNewTrack;
    /// <summary>The target a drop on <see cref="toNewTrack"/> sends with <see cref="ClipEditCommitted"/> and <see cref="MidiClipChanged"/>.</summary>
    public const string NewTrackTarget = "+new";
    LiveClip? dragged;
    Clip? original, preview;
    ClipEditMode dragMode;
    /// <summary>
    /// The speed shown while a clip is stretched by hand, against the recording (asked by the user: half as long reads
    /// ×2, the way it sounds, not <see cref="Clip.Stretch"/>); null in any other gesture.
    /// </summary>
    string? stretchText;
    bool moved;
    string? swapId;
    /// <summary>The clip is over another track with Ctrl held: it swaps instead of landing at a new time.</summary>
    bool swapping;
    /// <summary>
    /// The clips caught by the selection box (a drag over the empty part of the lanes; with Ctrl it adds to them), by id
    /// (asked by the user, to move several at once). Moving one of them moves them all by the same time, each on its own
    /// track, and Delete removes them all. Any plain selection clears it.
    /// </summary>
    readonly HashSet<string> boxed = [];
    /// <summary>What was boxed before a Ctrl+box started: the box adds to it.</summary>
    readonly HashSet<string> boxBase = [];
    /// <summary>Where the box started: seconds, and px from the top of the content (it stays put while the view scrolls).</summary>
    double boxSec, boxTop;
    /// <summary>The other boxed clips carried along with the grabbed one: each one as it was, and where it would land.</summary>
    readonly List<(LiveClip Clip, Clip Original)> carriedAlong = [];
    readonly Dictionary<LiveClip, Clip> carriedPreview = [];
    /// <summary>Shift+drag on a clip's body clones it (like FL Studio): the gesture is a move, and on release a copy stays where it was.</summary>
    bool cloning;
    /// <summary>The clips the gesture just cloned; the handler of the move that ends it reads them with <see cref="TakeClones"/>.</summary>
    List<string> clones = [];
    /// <summary>The other clip's edge the dragged one is stuck to (snap to other clips), drawn as a guide; null = none.</summary>
    double? stuckTo;
    /// <summary>
    /// The range selected with Rango or Shift+drag over one or more tracks (native only, <see cref="rangeTracks"/>), to
    /// delete what is inside it. It stays after the mouse is released until another click, Esc, an edit or the delete itself.
    /// </summary>
    double rangeStart, rangeEnd, rangeAnchor;
    KeyModifiers modifiers;
    double originalPan;
    readonly DispatcherTimer dragScroll;
    /// <summary>
    /// Where each row (track or folder, by id) is drawn, in px from the top of the content, while it glides to its place
    /// during and after a reorder or a fold (native only: the web jumps). Absent = drawn at its place.
    /// </summary>
    readonly Dictionary<string, double> shownTop = [];
    readonly DispatcherTimer slide;
    /// <summary>Share of the remaining distance a gliding track covers per 16 ms frame (about 150 ms to settle).</summary>
    const double SlideEase = 0.3;
    /// <summary>The track dragged by its handle, or still gliding into place after the drop: drawn over the others.</summary>
    string? liftedId;
    /// <summary>The other picked tracks carried with it: drawn over the others too.</summary>
    readonly HashSet<string> liftedAlso = [];
    bool fitMode = true;
    /// <summary>Keyed by clip and side: <see cref="MixSide"/> for one wave of both, 0 and 1 for a split track's left and right.
    /// Line = it is the line through the samples (the deepest zoom), which <see cref="WaveLooks.Paint"/> strokes in every look.</summary>
    readonly Dictionary<(LiveClip Clip, int Side), (StreamGeometry Geo, WaveKey Key, ClipGainCurve.Point[] Curve, double GainDb, bool Line)> waveCache = [];
    /// <summary>A wave's columns while its geometry is built (see <see cref="WaveLooks.Emit"/>), reused: they only grow.</summary>
    double[] waveHi = [], waveLo = [];

    /// <summary>A new wave look: every cached wave is built again once, on the next frame.</summary>
    void OnWaveLook()
    {
        waveCache.Clear();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WaveLooks.Changed += OnWaveLook;
        TrackColorLooks.Changed += InvalidateVisual;
        SeparatorLooks.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        WaveLooks.Changed -= OnWaveLook;
        TrackColorLooks.Changed -= InvalidateVisual;
        SeparatorLooks.Changed -= InvalidateVisual;
    }
    /// <summary>
    /// What a cached wave was drawn for, each value on its own. It was one sum (pps × 1e6 + … + AtSec × 1.7e-3): at the
    /// deepest zoom the first term is ~1e11, where a double cannot see a small move, so a nudged clip kept its old wave.
    /// </summary>
    readonly record struct WaveKey(double Pps, double ScrollX, double W, double Top, double Bottom, double At, double TrimStart, double TrimEnd, double Stretch, bool Reversed);

    TempoMap tempo = TempoMap.Constant(120).Prepare();
    /// <summary>The song's tempo map (#214): the bars, the grid and the magnet follow it. The window sets it after every change.</summary>
    public TempoMap Tempo
    {
        get => tempo;
        set
        {
            tempo = value.Prepare();
            TempoChanged();
            InvalidateVisual();
            layer.InvalidateVisual();
        }
    }
    /// <summary>The tempo at the song's start.</summary>
    public double Bpm => tempo.Bpm;
    public double LengthSec { get; set; }
    public double Playhead { get; private set; }
    public int Selected { get; private set; } = -1;
    /// <summary>The folder selected by a click on its row (then no track is selected), or null.</summary>
    public string? SelectedFolderId { get; private set; }
    /// <summary>The track or folder whose name is being edited in place (its drawn name is left out), or null.</summary>
    public string? EditingNameId { get => editingNameId; set { editingNameId = value; InvalidateVisual(); } }
    string? editingNameId;
    public string? SelectedClipId { get; private set; }
    public string SnapMode { get => snapMode; set { snapMode = value; InvalidateVisual(); } }
    /// <summary>
    /// How the bars-mode grid is drawn (asked by the user): "auto" splits beats finer as you zoom in, "snap" draws the
    /// snap's steps and a subdivision ("1/16"…) keeps that spacing at any zoom. Lines closer than
    /// <see cref="MinGridPx"/> are not drawn; with the snap off, "snap" works as "auto".
    /// </summary>
    public string GridMode { get => gridMode; set { gridMode = value; InvalidateVisual(); } }
    string gridMode = "auto";
    /// <summary>A right click on the ruler's numbers: the window offers the grid's spacing there.</summary>
    public event Action<Point>? RulerMenuRequested;
    /// <summary>Which edges of a moved clip stick to the edges of the others (the selector next to the snap; Alt still moves free).</summary>
    public SnapEdges SnapToClips { get; set; }
    public bool RulerInBars { get => bars; set { bars = value; InvalidateVisual(); } }
    public double TrackHeight => laneH;
    bool showVideo = true;
    /// <summary>False in a solo audio project: clips show only their waveform, without the filmstrip.</summary>
    public bool ShowVideo { get => showVideo; set { if (showVideo == value) return; showVideo = value; waveCache.Clear(); InvalidateVisual(); } }
    IReadOnlySet<int>? recordingLanes;
    /// <summary>
    /// The tracks takes are going into while recording (tracks.Count = an extra row for a new track at the
    /// end); null when not recording. Like the web, the timeline only scrolls and zooms meanwhile.
    /// </summary>
    public IReadOnlySet<int>? RecordingLanes
    {
        get => recordingLanes;
        set { recordingLanes = value; CancelGesture(); ClampScroll(); InvalidateVisual(); }
    }
    bool Recording => recordingLanes != null;
    /// <summary>Where the take being recorded starts (the playhead when recording started): its red bar grows from there.</summary>
    public double RecordingStartSec { get; set; }
    bool RecordingInto(int lane) => recordingLanes?.Contains(lane) == true;
    int ExtraRows => RecordingInto(tracks.Count) ? 1 : 0;
    IReadOnlySet<int> armed = new HashSet<int>();
    /// <summary>The tracks the next take goes to (armed by hand or picked by the window), tinted red; empty = a new track.</summary>
    public IReadOnlySet<int> ArmedIndices
    {
        get => armed;
        set { if (armed.SetEquals(value)) return; armed = value; InvalidateVisual(); }
    }
    /// <summary>The armed track's input as its header chip shows it ("1-2", "3"); set by the window.</summary>
    public Func<LiveTrack, string>? InputLabel { get; set; }
    /// <summary>The ● of a track header was clicked (native only: the web has no record arming).</summary>
    public event Action<LiveTrack>? ArmRequested;
    /// <summary>A track's freeze toggle was clicked (#240): the window freezes it, or unfreezes it.</summary>
    public event Action<LiveTrack>? FreezeRequested;
    /// <summary>The input chip of an armed track was clicked: the window shows the input menu (native only).</summary>
    public event Action<LiveTrack, Point>? InputMenuRequested;
    /// <summary>The track's badge was clicked: the window shows the color and icon picker there (native only).</summary>
    public event Action<LiveTrack, Point>? LookRequested;
    public event Action<Clip, string?, string?>? ClipEditCommitted;
    /// <summary>Tracks dropped by a handle (the grabbed one, or all the picked ones): their ids in order, their place in
    /// the folders-and-tracks sequence without them (<see cref="TrackFolders.Move"/>) and the folder they land in (null = the root).</summary>
    public event Action<IReadOnlyList<string>, int, string?>? TrackMoveRequested;
    /// <summary>A folder dropped by its handle: its id and its place in the sequence without it and its children.</summary>
    public event Action<string, int>? FolderMoveRequested;
    public event Action<Folder>? FolderFoldRequested;
    /// <summary>The folder's × was clicked: the window asks there whether its tracks go too.</summary>
    public event Action<Folder, Point>? FolderRemoveRequested;
    /// <summary>A folder's M (false) or S (true) was clicked.</summary>
    public event Action<Folder, bool>? FolderToggled;
    public event Action<Folder, Point>? FolderContextRequested;
    /// <summary>The folder's icon was clicked: the window opens its color picker below that point.</summary>
    public event Action<Folder, Point>? FolderLookRequested;
    /// <summary>A double click on a track's or a folder's name: the window edits it in place over that rect.</summary>
    public event Action<string, Rect>? NameEditRequested;
    /// <summary>A track's trash can was clicked: the window asks there before removing it.</summary>
    public event Action<string, Point>? RemoveTrackRequested;
    public event Action<LiveTrack, LiveClip?, Point>? ClipContextRequested;
    /// <summary>A take's clip dropped on a MIDI track: the window takes its notes out there (the clip stays put).</summary>
    public event Action<LiveClip, LiveTrack>? ExtractMidiRequested;
    /// <summary>A locked clip was grabbed to move or trim it (<see cref="ClipEditing.IsLocked"/>): the window says why nothing happens.</summary>
    public event Action<LiveTrack, LiveClip>? LockedTouched;
    /// <summary>The chip on a join between two clips was clicked: the track, the clip coming in and where (native only).</summary>
    public event Action<LiveTrack, LiveClip, Point>? TransitionRequested;
    public event Action<Point>? NewTrackRequested;
    public event Action<LiveTrack, double?, double?>? ChannelRequested;
    public event Action<double>? TrackHeightChanged;

    /// <summary>The selected range, if any: its tracks (a live set: copy it before clearing) and its times. The window
    /// deletes what is inside it on Supr or from the menu.</summary>
    public (IReadOnlyCollection<string> TrackIds, double Start, double End)? Range =>
        rangeTracks.Count > 0 && rangeEnd - rangeStart >= MinRangeSec ? (rangeTracks, rangeStart, rangeEnd) : null;
    /// <summary>A range drag shorter than this is a click, not a range.</summary>
    const double MinRangeSec = 0.02;

    public void ClearRange()
    {
        if (rangeTracks.Count == 0) return;
        rangeTracks.Clear();
        rangeStart = rangeEnd = 0;
        InvalidateVisual();
    }

    public event Action<double>? SeekRequested;
    /// <summary>A drag on the ruler started (true) or ended (false): the window holds playback meanwhile.</summary>
    public event Action<bool>? ScrubChanged;
    bool scrubHeld, scrubSought;
    /// <summary>Pixels the pointer moves on the ruler before a click becomes a drag.</summary>
    const double ScrubSlop = 3;
    public event Action<int>? SelectionChanged;
    /// <summary>Requests a channel edit from the window; true selects solo, false selects mute.</summary>
    public event Action<LiveTrack, bool>? TrackToggleRequested;

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
        VideoThumbnails.Ready += InvalidateVisual;
        dragScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (gesture is not ("clip" or "reorder" or "scrub" or "range" or "box")) return;
            double oldX = scrollX, oldY = scrollY;
            if (lastPointer.Y < RulerH + 36) scrollY -= Math.Ceiling((RulerH + 36 - lastPointer.Y) / 4);
            else if (lastPointer.Y > Bounds.Height - 36) scrollY += Math.Ceiling((lastPointer.Y - Bounds.Height + 36) / 4);
            if (gesture is "clip" or "scrub" or "range" or "box")
            {
                if (lastPointer.X > Bounds.Width - 16) scrollX += Math.Max(2, (lastPointer.X - Bounds.Width + 16) / 2);
                else if (lastPointer.X < HeaderW + 16) scrollX = Math.Max(0, scrollX - Math.Max(2, (HeaderW + 16 - lastPointer.X) / 2));
            }
            ClampScroll();
            if (oldX != scrollX || oldY != scrollY) UpdateGesture();
        });
        slide = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Slide());
        DetachedFromVisualTree += (_, _) => { dragScroll.Stop(); slide.Stop(); };
        dragScroll.Stop();
        slide.Stop();
        layer = new Layer(this);
        VisualChildren.Add(layer);
    }

    /// <summary>
    /// What moves on every tick while audio runs, the playhead and the header meters, drawn over the timeline. Each
    /// tick repaints only this; repainting the whole timeline per frame (every clip, wave and label) made enough
    /// garbage for GCs that stalled the audio callback. Not hit-testable: the timeline under it takes the pointer.
    /// </summary>
    sealed class Layer : Control
    {
        readonly TimelineView owner;
        public Layer(TimelineView owner) { this.owner = owner; IsHitTestVisible = false; }
        public override void Render(DrawingContext ctx) => owner.RenderLayer(ctx);
    }

    readonly Layer layer;

    static readonly Dictionary<StandardCursorType, Cursor> cursors = [];

    /// <summary>A cursor per shape, made once: a new Cursor on every pointer move was a native handle each time.</summary>
    static Cursor CursorOf(StandardCursorType type) =>
        cursors.TryGetValue(type, out var c) ? c : cursors[type] = new Cursor(type);

    /// <summary>Repaints the timeline and its moving layer (the layer's positions follow scroll, zoom and lane
    /// height). Hides Visual.InvalidateVisual so every caller, inside or outside, keeps both in step.</summary>
    public new void InvalidateVisual()
    {
        base.InvalidateVisual();
        layer?.InvalidateVisual();
    }

    /// <summary>The meters move to the background while a header is lifted or sliding, so the lifted card covers its
    /// neighbours' meters as it covers the rest of their header.</summary>
    bool MetersOnLayer => liftedId == null && !slide.IsEnabled;

    public void SetTracks(IReadOnlyList<LiveTrack> list, IReadOnlyList<Folder> folderList, bool preserveView = false)
    {
        tracks = list;
        SetFolders(folderList);
        waveCache.Clear();
        curveCache.Clear();
        shownTop.Clear();
        liftedId = null;
        liftedAlso.Clear();
        picked.Clear();
        boxed.Clear();
        pickAnchor = null;
        Relayout();
        if (!preserveView) SetFit(true);
        Selected = list.Count > 0 ? 0 : -1;
        if (!list.Any(t => t.Clips.Any(c => c.Clip.Id == SelectedClipId))) SelectedClipId = null;
        ClampScroll();
        InvalidateVisual();
    }

    /// <summary>After an edit applied in place: same view and selection, and only the waveforms of clips that left are dropped.</summary>
    public void UpdateTracks(IReadOnlyList<LiveTrack> list, IReadOnlyList<Folder> folderList)
    {
        // rows that move (a reorder dropped, a folder folded) glide from where they are drawn now
        foreach (var r in rows) { string id = RowIdOf(r); shownTop.TryAdd(id, Top(id)); }
        tracks = list;
        SetFolders(folderList);
        picked.RemoveWhere(id => !list.Any(t => t.Track.Id == id));
        boxed.RemoveWhere(id => !list.Any(t => t.Clips.Any(c => c.Clip.Id == id) || t.Track.MidiClips?.Any(c => c.Id == id) == true) && !IsBlockId(id));
        Relayout();
        if (shownTop.Count > 0) slide.Start();
        var live = list.SelectMany(t => t.Clips).ToHashSet();
        foreach (var gone in waveCache.Keys.Where(k => !live.Contains(k.Clip)).ToList()) waveCache.Remove(gone);
        foreach (var gone in curveCache.Keys.Where(c => !live.Contains(c)).ToList()) curveCache.Remove(gone);
        ClampScroll();
        InvalidateVisual();
    }

    /// <summary>
    /// The tracks picked together with Ctrl (one by one) or Shift (a range) on their headers, by id, the selected one
    /// included (asked by the user, to move several into or out of a folder at once): dragging one of them by its
    /// handle carries them all. Empty or one = a plain selection; any plain selection clears it.
    /// </summary>
    readonly HashSet<string> picked = [];
    /// <summary>Where a Shift range starts: the last track clicked without Shift.</summary>
    string? pickAnchor;
    /// <summary>The picked tracks in the order they are drawn, or just the selected one.</summary>
    public IReadOnlyList<string> PickedTrackIds => picked.Count > 1
        ? Seq().OfType<Track>().Where(t => picked.Contains(t.Id)).Select(t => t.Id).ToList()
        : Selected >= 0 && Selected < tracks.Count ? [tracks[Selected].Track.Id] : [];
    bool Picked(int i) => i == Selected || picked.Contains(tracks[i].Track.Id);

    /// <summary>Picks these tracks together again (after the window moved them), keeping the selected one.</summary>
    public void PickTracks(IReadOnlyCollection<string> ids)
    {
        picked.Clear();
        if (ids.Count > 1) foreach (var id in ids) picked.Add(id);
        TracksPicked?.Invoke();
        InvalidateVisual();
    }

    /// <summary>The window picked tracks again without changing the selected one.</summary>
    public event Action? TracksPicked;

    /// <summary>The clips caught by the selection box, by id (empty or one = a plain selection).</summary>
    public IReadOnlyCollection<string> BoxedClipIds => boxed.Count > 1 ? [.. boxed.Where(id => !IsBlockId(id))] : [];
    /// <summary>Everything boxed, clips and automation blocks (#281), to box it again after an edit.</summary>
    public IReadOnlyCollection<string> BoxedIds => boxed.Count > 1 ? [.. boxed] : [];
    /// <summary>
    /// The boxed clips were dragged: each take as it lands (the grabbed one first, if a take was grabbed), each MIDI
    /// clip's new start and each automation block's (#281).
    /// </summary>
    public event Action<IReadOnlyList<Clip>, IReadOnlyList<(string Id, double AtSec)>, IReadOnlyList<(string Id, double AtSec)>>? ClipsMoved;

    /// <summary>Boxes these clips together again (after the window moved them), keeping the selected one.</summary>
    public void BoxClips(IReadOnlyCollection<string> ids)
    {
        boxed.Clear();
        // an id the edit took away (a clip gone, a loop's repeat made again) doesn't stay selected: the next edit would fail on it
        if (ids.Count > 1) boxed.UnionWith(ids.Where(id => IsBlockId(id) || tracks.Any(t => t.Clips.Any(c => c.Clip.Id == id) || t.Track.MidiClips?.Any(c => c.Id == id) == true)));
        if (boxed.Count < 2) boxed.Clear();
        InvalidateVisual();
    }

    /// <summary>A press on the empty part of the lanes: a drag from here draws the selection box.</summary>
    void StartBox(PointerPressedEventArgs e)
    {
        boxBase.Clear();
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            boxBase.UnionWith(boxed);
            if (SelectedClipId != null) boxBase.Add(SelectedClipId);
            if (SelectedMidiId != null) boxBase.Add(SelectedMidiId);
            if (pressBlock != null) boxBase.Add(pressBlock);
        }
        boxSec = Sec(Math.Max(HeaderW, lastPointer.X));
        boxTop = lastPointer.Y - RulerH + scrollY;
        StartGesture("box", e);
    }

    /// <summary>The selection box in view px, from where it started to the pointer.</summary>
    Rect BoxRect()
    {
        double x0 = X(boxSec), y0 = boxTop + RulerH - scrollY, x1 = Math.Max(HeaderW, lastPointer.X), y1 = lastPointer.Y;
        return new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }

    /// <summary>Boxes what was boxed before (with Ctrl) plus every clip the box touches on a visible track.</summary>
    void UpdateBox()
    {
        var b = BoxRect();
        boxed.Clear();
        boxed.UnionWith(boxBase);
        for (int i = 0; i < tracks.Count; i++)
        {
            if (hidden.Contains(tracks[i].Track.Id)) continue;
            foreach (var c in tracks[i].Clips)
                if (ClipRect(i, Window(c)).Intersects(b)) boxed.Add(c.Clip.Id);
            BoxMidi(i, b);
        }
        BoxAuto(b);
    }

    /// <summary>The box was let go: the first boxed clip (top track, earliest) becomes the selected one, for the inspector.</summary>
    void EndBox()
    {
        // a boxed block is the one the shortcuts act on when it is alone
        selectedBlock = FirstBoxedBlock();
        if (boxed.Count == 1 && selectedBlock != null)
        {
            InvalidateVisual();
            return;
        }
        foreach (var r in rows)
        {
            if (r.Folder != null || r.Lane != null || r.Bus != null) continue;
            if (FirstBoxedMidi(r.Track) is { } firstMidi)
            {
                if (boxed.Count == 1) { SelectMidiClip(r.Track, firstMidi.Id); return; }
                SelectedMidiId = firstMidi.Id;
                SelectOne(r.Track);
                return;
            }
            var first = tracks[r.Track].Clips.Where(c => boxed.Contains(c.Clip.Id)).MinBy(c => Window(c).Start);
            if (first == null) continue;
            if (boxed.Count == 1) { SelectClip(r.Track, first.Clip.Id); return; }
            SelectedClipId = first.Clip.Id;
            SelectOne(r.Track);
            return;
        }
        InvalidateVisual();
    }

    /// <summary>Grabbing a boxed clip by its body carries the other boxed ones with it, takes and MIDI clips alike
    /// (<paramref name="grabbed"/> = null when the one grabbed is a MIDI clip).</summary>
    void StartCarry(LiveClip? grabbed)
    {
        carriedAlong.Clear();
        carriedPreview.Clear();
        foreach (var t in tracks)
            foreach (var c in t.Clips)
                if (c != grabbed && boxed.Contains(c.Clip.Id) && !ClipEditing.IsLocked(t.Track, c.Clip)) carriedAlong.Add((c, ClipEditing.Copy(c.Clip)));
        StartMidiCarry();
        StartAutoCarry();
    }

    /// <summary>True on a part of a clip that a Shift+drag clones: its body, not its handles, and not a locked one.</summary>
    bool OnClipBody(int i, Point p)
    {
        if (tracks[i].Track.IsMidi) return !tracks[i].Track.EditLocked && MidiClipAt(i, p.X) is { } m && !OnMidiEdge(m, p.X);
        return ClipAt(i, p.X) is { } c && !ClipEditing.IsLocked(tracks[i].Track, c.Clip) && HitHandle(c, p) == ClipEditMode.Move;
    }

    /// <summary>The ids of what a Shift+drag carried; a copy of each stays where it was.</summary>
    List<string> CloneIds(string kind)
    {
        List<string> ids = [];
        if (kind == "clip" && dragged != null) ids.Add(dragged.Clip.Id);
        if (kind == "midi" && midiHeld != null && !midiCarrying) ids.Add(midiHeld.Id);
        ids.AddRange(carriedAlong.Select(c => c.Item1.Clip.Id));
        ids.AddRange(midiCarried.Keys.Select(c => c.Id));
        ids.AddRange(autoCarried.Keys.Select(b => b.Id));
        return ids;
    }

    /// <summary>The clips the gesture that just ended cloned, once.</summary>
    public List<string> TakeClones()
    {
        var taken = clones;
        clones = [];
        return taken;
    }

    /// <summary>The grabbed clip moved (and snapped): the carried ones move by the same time, none ending before 0.</summary>
    void DragCarried()
    {
        if (dragged == null || original == null || preview == null) return;
        double shift = preview.AtSec - original.AtSec;
        foreach (var (c, o) in carriedAlong) shift = Math.Max(shift, ClipEnvelope.MinClipSec - Window(c, o).End);
        foreach (double from in midiCarried.Values) shift = Math.Max(shift, -from);
        foreach (double from in autoCarried.Values) shift = Math.Max(shift, -from);
        carryShift = shift;
        preview.AtSec = ClipEditing.ToSample(original.AtSec + shift);
        foreach (var (c, o) in carriedAlong)
        {
            var v = ClipEditing.Copy(o);
            v.AtSec = ClipEditing.ToSample(o.AtSec + shift);
            carriedPreview[c] = v;
        }
    }

    /// <summary>A Ctrl or Shift click on a track's header: adds it or takes it out, or picks the range from the anchor.</summary>
    void Pick(int i, bool range)
    {
        string id = tracks[i].Track.Id;
        if (picked.Count == 0 && Selected >= 0 && Selected < tracks.Count) picked.Add(tracks[Selected].Track.Id);
        pickAnchor ??= Selected >= 0 && Selected < tracks.Count ? tracks[Selected].Track.Id : id;
        int primary = i;
        if (range)
        {
            // every track on screen between the anchor and this one, in the order they are drawn
            var order = rows.Where(r => r.Folder == null && r.Lane == null && r.Bus == null).Select(r => tracks[r.Track].Track.Id).ToList();
            int a = order.IndexOf(pickAnchor), b = order.IndexOf(id);
            picked.Clear();
            if (a < 0) picked.Add(id);
            else for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) picked.Add(order[k]);
        }
        else
        {
            pickAnchor = id;
            if (!picked.Add(id))
            {
                picked.Remove(id);
                primary = i != Selected ? Selected : tracks.ToList().FindIndex(t => picked.Contains(t.Track.Id));
            }
        }
        SelectOne(primary);
    }

    public void Select(int i)
    {
        picked.Clear();
        boxed.Clear();
        pickAnchor = i >= 0 && i < tracks.Count ? tracks[i].Track.Id : null;
        SelectOne(i);
    }

    /// <summary>Makes a track the selected one (the inspector's) without touching the picked ones.</summary>
    void SelectOne(int i)
    {
        Selected = i;
        SelectedFolderId = null;
        if (i < 0 || i >= tracks.Count) SelectedClipId = null;
        else if (!tracks[i].Clips.Any(c => c.Clip.Id == SelectedClipId)) SelectedClipId = tracks[i].Clips.FirstOrDefault()?.Clip.Id;
        if (i < 0 || i >= tracks.Count || tracks[i].Track.MidiClips?.Any(c => c.Id == SelectedMidiId) != true) SelectedMidiId = null;
        SelectionChanged?.Invoke(i);
        InvalidateVisual();
    }

    /// <summary>Moves the playhead, scrolling a page when it runs off the right edge while playing.</summary>
    /// <summary>Called every tick: repaints only when the playhead or a header meter moved, and then only the moving
    /// <see cref="Layer"/>, unless the view scrolled, a take is recording (its bar grows with the playhead) or the
    /// meters are in the background.</summary>
    public void SetPlayhead(double sec, bool follow)
    {
        bool meters = MetersMoved();
        if (sec == Playhead && !meters) return;
        Playhead = sec;
        double x = X(sec), oldScroll = scrollX;
        if (follow && (x > Bounds.Width - 40 || x < HeaderW)) scrollX = Math.Max(0, sec * pps - 40);
        if (scrollX != oldScroll || Recording || (meters && !MetersOnLayer)) InvalidateVisual();
        else layer.InvalidateVisual();
    }

    /// <summary>Where the playhead goes back on stop, drawn as a dashed line (#271), or NaN for none.</summary>
    double returnMark = double.NaN;

    /// <summary>Shows (or hides, with NaN) the return-on-stop point; repaints the <see cref="Layer"/> only when it moved.</summary>
    public void SetReturnMark(double sec)
    {
        if (sec == returnMark || (double.IsNaN(sec) && double.IsNaN(returnMark))) return;
        returnMark = sec;
        layer.InvalidateVisual();
    }

    /// <summary>The header meters as last drawn, in steps finer than a pixel (see <see cref="DrawMeterBar"/>).</summary>
    int[] meterSteps = [];
    bool MetersMoved()
    {
        if (meterSteps.Length != tracks.Count * 2) meterSteps = new int[tracks.Count * 2];
        bool moved = false;
        for (int i = 0; i < tracks.Count; i++)
            for (int c = 0; c < 2; c++)
            {
                int step = MeterStep(c == 0 ? tracks[i].ShowL : tracks[i].ShowR);
                if (meterSteps[i * 2 + c] == step) continue;
                meterSteps[i * 2 + c] = step;
                moved = true;
            }
        return moved;
    }

    static int MeterStep(float peak)
    {
        double db = Palette.ToDb(peak);
        int step = (int)Math.Round(Math.Clamp((db + 48) / 48, 0, 1) * 200);
        return db > -1 ? -step - 1 : step;
    }

    /// <summary>True while the zoom follows the width ("Ver todo"); the toolbar disables its button then, like the web.</summary>
    public bool FitMode => fitMode;
    public event Action<bool>? FitModeChanged;
    void SetFit(bool on)
    {
        if (fitMode == on) return;
        fitMode = on;
        FitModeChanged?.Invoke(on);
    }

    /// <summary>Beats across the view for the zoom presets (native only, asked by the user; no key by default since 1–7
    /// pick the tools): from the closest, a beat, to a bar, 4 bars, 16 bars and the whole song (0). Counted in beats so a
    /// preset shows the same music at any tempo.</summary>
    public static readonly double[] ZoomPresetBeats = [1, 4, 16, 64, 0];

    /// <summary>Zoom preset <paramref name="n"/> (0..4) of <see cref="ZoomPresetBeats"/>, keeping the playhead in place.</summary>
    public void ZoomPreset(int n)
    {
        n = Math.Clamp(n, 0, ZoomPresetBeats.Length - 1);
        if (ZoomPresetBeats[n] == 0) { FitAll(); return; }
        double at = tempo.BeatAt(Math.Max(0, Sec(HeaderW)));
        ZoomTo((Bounds.Width - HeaderW - 24) / Math.Max(1e-3, tempo.SecAt(at + ZoomPresetBeats[n]) - tempo.SecAt(at)));
    }

    public void FitAll()
    {
        double w = Bounds.Width - HeaderW - 24;
        if (w <= 0 || LengthSec <= 0) return;
        SetFit(true);
        pps = w / Math.Max(8, LengthSec);
        scrollX = 0;
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Horizontal zoom as 0..1 on a log scale from the whole song to <see cref="MaxPps"/>, for the slider under the headers.</summary>
    public double Zoom => Math.Clamp(Math.Log(pps / MinPps) / Math.Log(MaxPps / MinPps), 0, 1);
    /// <summary>The horizontal zoom changed (wheel, slider, "ver todo" or a resize while fitting).</summary>
    public event Action? ZoomChanged;

    public void SetZoom(double z) => ZoomTo(MinPps * Math.Pow(MaxPps / MinPps, Math.Clamp(z, 0, 1)));

    /// <summary>Zooms keeping the playhead where it is on screen, or the view's middle when it is out of sight.</summary>
    void ZoomTo(double next)
    {
        next = Math.Clamp(next, MinPps, MaxPps);
        if (Math.Abs(next - pps) < pps * 1e-6) return;
        double px = Playhead * pps - scrollX;
        double at = Playhead;
        if (px < 0 || px > Bounds.Width - HeaderW)
        {
            px = (Bounds.Width - HeaderW) / 2;
            at = (scrollX + px) / pps;
        }
        ZoomAround(next, at, px);
    }

    double X(double sec) => HeaderW + sec * pps - scrollX;

    double Sec(double x) => (x - HeaderW + scrollX) / pps;
    /// <summary>Where track <paramref name="i"/>'s row is drawn; past the last track, the extra rows under them
    /// (a take going to a new track, "Nueva pista"). A track folded away is far above the view, so nothing draws it.</summary>
    double LaneY(int i) => i >= tracks.Count ? RulerH + contentH + (i - tracks.Count) * laneH - scrollY
        : hidden.Contains(tracks[i].Track.Id) && !IsDragged(tracks[i].Track.Id) ? -1e9 : RulerH + Top(tracks[i].Track.Id) - scrollY;
    double FolderY(Folder f) => RulerH + Top(f.Id) - scrollY;
    /// <summary>A track's lane height: the one being dragged, or the shared one times its <see cref="Track.HeightScale"/>.</summary>
    double RowH(Track t) => heightDrag is { } d && d.Id == t.Id ? d.H
        : t.HeightScale is { } k ? Math.Clamp(Math.Round(laneH * k / 2) * 2, Track.MinHeight, Track.MaxHeight) : laneH;
    /// <summary>Lane height of track <paramref name="i"/>; the extra rows under the tracks use the shared one.</summary>
    double LaneH(int i) => i >= 0 && i < tracks.Count ? RowH(tracks[i].Track) : laneH;
    /// <summary>A folder's row height, following the vertical zoom like the tracks. Its usual one, open or folded, is the
    /// tracks' shared height (#161: after "even out heights" every row is the same; what doesn't fit, its volume and pan
    /// or its send knobs, hides as on a short track), unless the user resized it: open, its own px
    /// (<see cref="Folder.Height"/>); folded, a multiple of the tracks' shared height (<see cref="Folder.HeightScale"/>).</summary>
    double FolderRowH(Folder f) => f.Separator ? (f.HiddenInTimeline ? 0 : SeparatorH)
        : heightDrag is { } d && d.Id == f.Id ? d.H
        : f.Collapsed ? f.HeightScale is { } k ? Math.Clamp(Math.Round(laneH * k / 2) * 2, Folder.MinHeight, Track.MaxHeight) : FolderBaseH
        : f.Height is { } own ? FolderZoomed(own, Folder.MinHeight, Folder.MaxHeight)
        : FolderBaseH;

    /// <summary>A folder height in px at <see cref="Track.DefaultHeight"/>, at the current vertical zoom.</summary>
    double FolderZoomed(double h, double min, double max) => Math.Clamp(Math.Round(h * laneH / Track.DefaultHeight / 2) * 2, min, max);
    /// <summary>A folder height on screen back to px at <see cref="Track.DefaultHeight"/> (how <see cref="Folder.Height"/> keeps it).</summary>
    public double FolderUnzoomed(double h) => h * Track.DefaultHeight / laneH;

    /// <summary>A folder's usual row height: the tracks' shared one.</summary>
    double FolderBaseH => Math.Max(laneH, Folder.MinHeight);
    /// <summary>The row whose bottom edge is being dragged and its height so far (applied on release).</summary>
    (string Id, double H)? heightDrag;
    /// <summary>Px above and below a row's bottom edge (in the header) that grab it to change that row's height.</summary>
    const double RowEdgeGrab = 3;
    double heightFrom;
    /// <summary>A row's own height changed by dragging its bottom edge (null = back to the shared one, a double click on the edge).</summary>
    public event Action<string, double?>? RowHeightRequested;

    /// <summary>The track or folder whose bottom edge is under the pointer, in the header column; null elsewhere.</summary>
    string? RowEdgeAt(Point p)
    {
        // the zoom bar covers the rows' edges there
        if (p.Y < RulerH || p.X >= HeaderRight || p.Y >= Bounds.Height - FooterH) return null;
        foreach (var r in rows)
        {
            // a separator keeps its height
            if (r.Folder is { Separator: true } || r is { Bus: not null, Lane: null }) continue;
            double bottom = RulerH + r.Top - scrollY + r.H;
            if (Math.Abs(p.Y - bottom) <= RowEdgeGrab) return RowIdOf(r);
        }
        return null;
    }

    // --- rows and folders (native only) ----------------------------------------------------------

    /// <summary>Height of a folder's own row when open: one line, a title over its tracks (asked by the user: at a track's
    /// height it was an empty block that weighed more than its children). The smallest a folder row can be.</summary>
    public const double FolderH = Folder.MinHeight;
    /// <summary>Height of a separator's row (a hidden one takes none: it keeps its place in the order, out of sight).</summary>
    public const double SeparatorH = 22;
    IReadOnlyList<Folder> folders = [];
    readonly Dictionary<string, Folder> folderById = [];
    /// <summary>A row on screen: a track (its index), a folder or a bus (#267, at the end, Track -1), with its place in px from the top of the content.</summary>
    readonly record struct Row(int Track, Folder? Folder, double Top, double H, AutomationLane? Lane = null, Bus? Bus = null);
    /// <summary>The rows in order as they belong now (during a reorder: with room made where the dragged one lands).</summary>
    readonly List<Row> rows = [];
    readonly Dictionary<string, double> targetTop = [];
    /// <summary>Tracks folded away in their folder.</summary>
    readonly HashSet<string> hidden = [];
    double contentH;

    string RowIdOf(Row r) => r.Lane?.Id ?? r.Folder?.Id ?? r.Bus?.Id ?? tracks[r.Track].Track.Id;

    void SetFolders(IReadOnlyList<Folder> list)
    {
        folders = list;
        folderById.Clear();
        foreach (var f in list) folderById[f.Id] = f;
        if (SelectedFolderId != null && !folderById.ContainsKey(SelectedFolderId)) SelectedFolderId = null;
    }

    public Folder? FolderOf(LiveTrack t) => t.Track.FolderId is { } id ? folderById.GetValueOrDefault(id) : null;
    /// <summary>Its color, or a neutral gray without one (asked by the user: in the accent its frame looked selected).</summary>
    public static Color FolderColor(Folder f) => f.Color != null && Color.TryParse(f.Color, out var c) ? c : Palette.DimColor;

    /// <summary>Folders and tracks in order, as <see cref="TrackFolders.Sequence"/> keeps them.</summary>
    List<object> Seq() => TrackFolders.Sequence(tracks.Select(t => t.Track).ToList(), folders);

    /// <summary>Stacks a sequence: each visible item's top in <paramref name="tops"/>, folded tracks in <paramref name="folded"/>; returns the height.</summary>
    double Stack(List<object> seq, Dictionary<string, double> tops, HashSet<string> folded, bool landing)
    {
        double y = 0;
        foreach (var x in seq)
        {
            if (x is Folder f)
            {
                tops[f.Id] = y;
                y += FolderRowH(f);
                foreach (var lane in LanesOf(f))
                {
                    tops[lane.Id] = y;
                    y += AutoH(lane);
                }
                continue;
            }
            var t = (Track)x;
            string? fid = landing && dragIds.Contains(t.Id) && drop is { } d ? d.Folder : t.FolderId;
            // a folder being dragged travels folded
            if (fid != null && folderById.TryGetValue(fid, out var parent) && (parent.Collapsed || parent == dragFolder && moved))
            {
                folded.Add(t.Id);
                continue;
            }
            tops[t.Id] = y;
            y += RowH(t);
            foreach (var lane in LanesOf(t))
            {
                tops[lane.Id] = y;
                y += AutoH(lane);
            }
        }
        return y;
    }

    /// <summary>Lays the rows out again: as they are, or, while a reorder has a place to drop, as they will be.</summary>
    void Relayout()
    {
        targetTop.Clear();
        hidden.Clear();
        rows.Clear();
        var seq = Seq();
        bool landing = gesture == "reorder" && moved && drop != null;
        if (landing)
        {
            var block = Dragged(seq);
            seq.RemoveAll(block.Contains);
            seq.InsertRange(Math.Clamp(drop!.Value.Pos, 0, seq.Count), block);
        }
        contentH = StackBuses(Stack(seq, targetTop, hidden, landing));
        var index = new Dictionary<string, int>();
        for (int i = 0; i < tracks.Count; i++) index[tracks[i].Track.Id] = i;
        foreach (var x in seq)
        {
            if (x is Folder f)
            {
                rows.Add(new Row(-1, f, targetTop[f.Id], FolderRowH(f)));
                foreach (var lane in LanesOf(f)) rows.Add(new Row(-1, f, targetTop[lane.Id], AutoH(lane), lane));
            }
            else if (x is Track t && targetTop.TryGetValue(t.Id, out double top) && index.TryGetValue(t.Id, out int i))
            {
                rows.Add(new Row(i, null, top, RowH(t)));
                foreach (var lane in LanesOf(t)) rows.Add(new Row(i, null, targetTop[lane.Id], AutoH(lane), lane));
            }
        }
        AddBusRows();
        overSeparator.Clear();
        for (int k = 0; k + 1 < rows.Count; k++)
            if (rows[k + 1].Folder is { Separator: true, HiddenInTimeline: false }) overSeparator.Add(RowIdOf(rows[k]));
        seamUnder.Clear();
        for (int k = 0; k < rows.Count; k++)
            if (k + 1 == rows.Count || rows[k + 1].Lane == null) seamUnder.Add(RowIdOf(rows[k]));
        ClampScroll();
    }

    /// <summary>Rows right above a shown separator: they leave out their bottom line, which the separator already draws.</summary>
    readonly HashSet<string> overSeparator = [];

    /// <summary>
    /// Whether a row leaves out its bottom line for the separator under it (asked by the user: both lines together were
    /// redundant). Not while a reorder moves rows around, when they pass over each other.
    /// </summary>
    bool OverSeparator(string id) => !(gesture == "reorder" && moved) && overSeparator.Contains(id);

    /// <summary>Rows that close a channel (a track or a folder, under its automation lanes when shown): they end in
    /// <see cref="DrawSeam"/>'s groove instead of a line.</summary>
    readonly HashSet<string> seamUnder = [];

    /// <summary>
    /// A row's bottom edge from <paramref name="x0"/> to <paramref name="x1"/>: nothing over a separator, the groove the
    /// joined mixer strips have between them at the end of a channel (asked by the user, #250: "la sombra del separador …
    /// apliquémoslo a los canales en el timeline"), a thin line between a channel and its own lanes.
    /// </summary>
    void DrawRowBottom(DrawingContext ctx, string id, double x0, double x1, double bottom)
    {
        if (OverSeparator(id) || x1 <= x0) return;
        if (seamUnder.Contains(id)) DrawSeam(ctx, x0, x1, bottom);
        else ctx.DrawLine(Palette.Pen(Palette.Line), new Point(x0, bottom - 0.5), new Point(x1, bottom - 0.5));
    }

    /// <summary>The joined mixer strips' groove, lying down: a dark line (the window's background) at the row's bottom
    /// with a lit one over it, like the seam between a console's panels.</summary>
    static void DrawSeam(DrawingContext ctx, double x0, double x1, double bottom)
    {
        ctx.FillRectangle(Palette.App, new Rect(x0, bottom - 1, x1 - x0, 1));
        ctx.FillRectangle(Palette.Alpha(Palette.FgColor, MixerLooks.GrooveLitA), new Rect(x0, bottom - 2, x1 - x0, 1));
    }

    /// <summary>The dragged item: the carried tracks, or a folder with its children.</summary>
    List<object> Dragged(List<object> seq) => dragFolder is { } df
        ? seq.Where(x => x == df || x is Track t && t.FolderId == df.Id).ToList()
        : seq.Where(x => x is Track t && dragIds.Contains(t.Id)).ToList();

    bool IsDragged(string id) => gesture == "reorder" && moved && (dragFolder != null ? id == dragFolder.Id : dragIds.Contains(id));

    /// <summary>Where the dragged block's top is, following the pointer.</summary>
    double BlockTop() => Math.Max(0, dragTop + lastPointer.Y - down.Y + scrollY - downScrollY);

    /// <summary>Where a dragged row goes in the block: the carried tracks stack one under the other.</summary>
    double BlockOffset(string id) => dragOffset.GetValueOrDefault(id);

    /// <summary>Where a row is drawn now, in px from the top of the content: the dragged one follows the pointer, and
    /// the other carried tracks glide to their place under it.</summary>
    double Top(string id)
    {
        if (IsDragged(id) && (id == liftedId || !shownTop.ContainsKey(id))) return BlockTop() + BlockOffset(id);
        return shownTop.TryGetValue(id, out double s) ? s : targetTop.GetValueOrDefault(id);
    }

    /// <summary>
    /// The selected track's (or folder's) header in the view: its top and bottom cut to the rows' area, and the header
    /// column's left edge. False when nothing is selected or it is scrolled or folded away. The window points from it
    /// to the inspector (#180); it is asked on every tick, so it does not allocate.
    /// </summary>
    public bool SelectedHeader(out double left, out double top, out double bottom)
    {
        left = top = bottom = 0;
        string? id = SelectedFolderId ?? (Selected >= 0 && Selected < tracks.Count ? tracks[Selected].Track.Id : SelectedBusId?.Invoke());
        if (id == null) return false;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Lane != null || RowIdOf(r) != id) continue;
            double y = RulerH + Top(id) - scrollY, h = r.H;
            top = Math.Max(y, RulerH);
            bottom = Math.Min(y + h, Bounds.Height - FooterH);
            return h > 0 && bottom - top >= 1;
        }
        return false;
    }

    /// <summary>The row under a y in the view, or null (the ruler, the space under the rows).</summary>
    Row? HitRow(double y)
    {
        if (y < RulerH) return null;
        double cy = y - RulerH + scrollY;
        foreach (var r in rows)
            if (cy >= r.Top && cy < r.Top + r.H) return r;
        return null;
    }

    int HitTrack(double y) => HitRow(y) is { Folder: null, Lane: null } r ? r.Track : -1;

    /// <summary>Whether <paramref name="y"/> is in the empty space under every row (and the take being recorded on a new track).</summary>
    bool InEmptySpace(double y) => y >= RulerH && y - RulerH + scrollY >= contentH + ExtraRows * laneH;

    /// <summary>The "new track" lane a clip dropped in the empty space lands on (#309): right under the rows, the shared height.</summary>
    Rect NewTrackLane(double W) => new(HeaderW, LaneY(tracks.Count + ExtraRows), Math.Max(0, W - HeaderW), laneH);

    // the reorder gesture: a track or a folder, where it was and where it will land
    string? dragTrackId;
    /// <summary>The tracks carried (the grabbed one, or every picked one when it is picked), in the order they are drawn.</summary>
    readonly List<string> dragIds = [];
    readonly Dictionary<string, double> dragOffset = [];
    /// <summary>The carried tracks were apart or in different folders: dropping them even where they were gathers them.</summary>
    bool dragGathers;
    Folder? dragFolder;
    double dragTop;
    /// <summary>Where the dragged item lands: its place in the sequence without it, and the folder (tracks only).</summary>
    (int Pos, string? Folder)? drop;
    (int Pos, string? Folder) dropFrom;

    /// <summary>Px the dragged row has to pass the line between its current place and another before it takes that
    /// one: without it, a row resting on an edge (the last track of a folder, say) flickered in and out.</summary>
    const double DropStick = 8;
    /// <summary>Px that separate the two places at a folder's bottom edge (its last one inside, the first one after it),
    /// so the dragged row's top passing that edge is what takes it in or out.</summary>
    const double EdgeSplit = 1;

    /// <summary>
    /// Where the dragged row lands: the place whose gap is nearest to the row's top, measured on the rows without it,
    /// where a gap's y is exactly where the row's top would sit once dropped (so making room never moves the target,
    /// and a row resting in its gap stays there). A track can land between any two rows; at a folder's bottom edge
    /// there are two places, the last inside (just above the edge) and the first after it (just below), and over a
    /// folded folder's row it goes in last. A folder lands only between items at the root.
    /// </summary>
    (int Pos, string? Folder) FindDrop()
    {
        var seq = Seq();
        seq.RemoveAll(Dragged(seq).Contains);
        var tops = new Dictionary<string, double>();
        double end = Stack(seq, tops, [], false);
        double at = BlockTop();
        bool tracksOnly = dragFolder == null;
        var slots = new List<(double Y, int Pos, string? Folder)>();
        for (int k = 0; k < seq.Count; k++)
        {
            if (seq[k] is Track { FolderId: { } fid } child)
            {
                // after a track in a folder, still in it (a folded one's tracks have no row)
                if (tracksOnly && tops.TryGetValue(child.Id, out double ct)) slots.Add((ct + BlockH(child) - EdgeSplit, k + 1, fid));
                continue;
            }
            // a row at the root: before it, out of any folder above
            string id = seq[k] is Folder rf ? rf.Id : ((Track)seq[k]).Id;
            double top = tops[id];
            slots.Add((top + EdgeSplit, k, null));
            if (tracksOnly && seq[k] is Folder { Separator: false } f)
            {
                if (f.Collapsed)
                {
                    int children = seq.Count(x => x is Track t && t.FolderId == f.Id);
                    slots.Add((top + FolderRowH(f) / 2, k + 1 + children, f.Id));
                }
                else slots.Add((top + FolderBlockH(f) - EdgeSplit, k + 1, f.Id));
            }
        }
        slots.Add((end + EdgeSplit, seq.Count, null));
        // measured DropStick px back towards the current place, so leaving it takes passing the line by that much
        var now = drop ?? dropFrom;
        foreach (var s in slots)
            if (s.Pos == now.Pos && s.Folder == now.Folder)
            {
                at = Math.Abs(at - s.Y) <= DropStick ? s.Y : at - Math.Sign(at - s.Y) * DropStick;
                break;
            }
        (int Pos, string? Folder) best = (seq.Count, null);
        double bestD = double.MaxValue;
        foreach (var s in slots)
        {
            double d = Math.Abs(at - s.Y);
            if (d < bestD) { bestD = d; best = (s.Pos, s.Folder); }
        }
        return best;
    }

    void Slide()
    {
        string? dragId = gesture == "reorder" ? dragFolder?.Id ?? dragTrackId : null;
        foreach (var id in shownTop.Keys.ToList())
        {
            // a carried track glides to its place in the block, the others to their place in the rows
            double target = 0;
            bool carried = id != dragId && IsDragged(id);
            if (carried) target = BlockTop() + BlockOffset(id);
            if (id == dragId || !carried && !targetTop.TryGetValue(id, out target)) { shownTop.Remove(id); continue; }
            double s = shownTop[id];
            s += (target - s) * SlideEase;
            if (Math.Abs(target - s) < 0.5) shownTop.Remove(id);
            else shownTop[id] = s;
        }
        if (shownTop.Count == 0)
        {
            slide.Stop();
            if (gesture != "reorder")
            {
                liftedId = null;
                liftedAlso.Clear();
            }
        }
        InvalidateVisual();
    }

    /// <summary>The track indices in drawing order, filled at the start of each Render (<see cref="FillDrawOrder"/>): an
    /// iterator would be a new object on every frame.</summary>
    readonly List<int> drawOrder = [];

    /// <summary>Fills <see cref="drawOrder"/>: the lifted track last, so it passes over the others.</summary>
    void FillDrawOrder()
    {
        drawOrder.Clear();
        int lifted = -1;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Track.Id == liftedId) { lifted = i; continue; }
            if (!liftedAlso.Contains(tracks[i].Track.Id)) drawOrder.Add(i);
        }
        if (liftedAlso.Count > 0)
            for (int i = 0; i < tracks.Count; i++)
                if (liftedAlso.Contains(tracks[i].Track.Id)) drawOrder.Add(i);
        if (lifted >= 0) drawOrder.Add(lifted);
    }

    /// <summary>The index of the track with this id, or -1 (a plain loop: it runs while drawing a drag).</summary>
    int TrackIndex(string id)
    {
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Track.Id == id) return i;
        return -1;
    }

    /// <summary>The folders in drawing order: the lifted one last.</summary>
    IEnumerable<Folder> FolderOrder() => folders.Where(f => f.Id != liftedId).Concat(folders.Where(f => f.Id == liftedId));
    /// <summary>The magnet's lines on the tempo map: every <see cref="SnapBeats"/> from each bar's start (the bars for "bar"; Auto, the lines drawn now).</summary>
    SnapGrid Grid
    {
        get
        {
            double beats = snapMode == "line" ? LineBeats : snapMode == "bar" ? 0 : FixedSnapBeats;
            if (snapMode != "line" && snapMode != "bar" && beats <= 0) return new(0);
            double beatSec = tempo.BeatSecAt(tempo.BeatAt(Math.Max(0, Playhead)));
            return new((beats > 0 ? beats : tempo.BarBeats(0)) * beatSec, Map: tempo, Beats: beats);
        }
    }
    /// <summary>The magnet's lines, whatever kind it is (a step of 0 = off).</summary>
    public SnapGrid SnapLines => Grid;
    /// <summary>The magnet's step in seconds (0 = off), at the tempo under the playhead.</summary>
    public double SnapSec => Grid.Step;
    /// <summary>The magnet's length in beats when it has a fixed one ("bar" too, as 4/4's), or 0.</summary>
    double FixedSnapBeats => NoteBeats(snapMode);

    /// <summary>A value's beats (<see cref="Subdivisions.Beats"/>) remembered by id: the grid reads it on every frame and parsing it made garbage.</summary>
    static readonly Dictionary<string, double> noteBeats = [];
    static double NoteBeats(string mode)
    {
        if (noteBeats.TryGetValue(mode, out double b)) return b;
        return noteBeats[mode] = Subdivisions.Beats(mode);
    }
    double ViewSec => Math.Max(8, Math.Max(LengthSec, Playhead + 2));
    double MinPps => Math.Max(0.01, (Bounds.Width - HeaderW) / Math.Max(600, ViewSec * 8));
    void ClampScroll() => scrollY = Math.Clamp(scrollY, 0, Math.Max(0, contentH + ExtraRows * laneH + TailH + FooterH - (Bounds.Height - RulerH)));
    public void SetTrackHeight(double height)
    {
        double next = Math.Clamp(Math.Round(height / 2) * 2, MinTrackH, MaxTrackH); // the web's slider steps by 2 px
        if (Math.Abs(next - laneH) < 0.001) return;
        laneH = next;
        Relayout();
        waveCache.Clear();
        TrackHeightChanged?.Invoke(laneH);
        InvalidateVisual();
    }
    Clip DisplayClip(LiveClip clip) => dragged == clip && preview != null && overIndex == fromIndex && !toNewTrack ? preview
        : carriedPreview.TryGetValue(clip, out var along) ? along : clip.Clip;
    ClipEnvelope.ClipWindow Window(LiveClip clip, Clip? value = null)
    {
        var c = value ?? DisplayClip(clip);
        // the shown clip's own stretch: a stretch being dragged is not on the clip yet
        return ClipEditing.Window(c, clip.Media, clip.MediaLengthSec * c.Stretch);
    }
    LiveClip? ClipAt(int i, double x) => i >= 0 && i < tracks.Count
        ? tracks[i].Clips.LastOrDefault(c => { var w = Window(c); return x >= X(w.Start) && x <= X(w.End); }) : null;

    // --- clip volume curve (native only) -----------------------------------------------------------

    /// <summary>The point being dragged in a "curve" gesture.</summary>
    int curvePoint = -1;
    /// <summary>Pixels around a curve point or the line that still grab it.</summary>
    const double CurveGrab = 5;

    Rect ClipRect(int lane, ClipEnvelope.ClipWindow w) => new(X(w.Start), LaneY(lane) + 5, Math.Max(2, X(w.End) - X(w.Start)), LaneH(lane) - 10);
    static double CurveY(Rect r, double db) => r.Top + 3 + (ClipGainCurve.MaxDb - Math.Clamp(db, ClipGainCurve.MinDb, ClipGainCurve.MaxDb))
        / (ClipGainCurve.MaxDb - ClipGainCurve.MinDb) * (r.Height - 6);
    static double CurveDb(Rect r, double y) => Math.Clamp(ClipGainCurve.MaxDb - (y - r.Top - 3) / Math.Max(1, r.Height - 6)
        * (ClipGainCurve.MaxDb - ClipGainCurve.MinDb), ClipGainCurve.MinDb, ClipGainCurve.MaxDb);
    /// <summary>The live clip's curve, or the preview's while it is dragged.</summary>
    static ClipGainCurve.Point[] CurveOf(LiveClip c, Clip model) => ReferenceEquals(model, c.Clip) ? c.Curve : model.CurvePoints();
    /// <summary>What the line shows at a timeline time: the clip gain plus the curve.</summary>
    static double LevelAt(Clip model, ClipGainCurve.Point[] pts, ClipEnvelope.ClipWindow w, double sec) => model.GainDb + ClipGainCurve.DbAt(pts, sec - w.Origin);
    bool CurveEditable(int lane, LiveClip c) => lane == Selected && c.Clip.Id == SelectedClipId && !c.Offline && !Recording;

    /// <summary>Each clip's volume line as last drawn, like <see cref="waveCache"/>: a new geometry per clip and frame was garbage.</summary>
    readonly Dictionary<LiveClip, (StreamGeometry Geo, CurveKey Key, ClipGainCurve.Point[] Curve)> curveCache = [];
    /// <summary>What a cached volume line was drawn for: everything its points depend on but the curve itself (compared by reference).</summary>
    readonly record struct CurveKey(Rect R, double Origin, double Start, double End, double GainDb, double Pps, double ScrollX, double HeaderW);

    void DrawCurve(DrawingContext ctx, LiveClip c, Clip model, ClipEnvelope.ClipWindow w, Rect r, bool editable)
    {
        var pts = CurveOf(c, model);
        var key = new CurveKey(r, w.Origin, w.Start, w.End, model.GainDb, pps, scrollX, HeaderW);
        StreamGeometry geo;
        if (curveCache.TryGetValue(c, out var hit) && hit.Key == key && hit.Curve == pts) geo = hit.Geo;
        else
        {
            geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(r.Left, CurveY(r, LevelAt(model, pts, w, w.Start))), false);
                foreach (var pt in pts)
                    if (w.Origin + pt.Sec > w.Start && w.Origin + pt.Sec < w.End)
                        g.LineTo(new Point(X(w.Origin + pt.Sec), CurveY(r, model.GainDb + pt.Db)));
                g.LineTo(new Point(r.Right, CurveY(r, LevelAt(model, pts, w, w.End))));
                g.EndFigure(false);
            }
            curveCache[c] = (geo, key, pts);
        }
        // over an accent wave the selected line goes in the text color, its points in the accent
        ctx.DrawGeometry(null, editable ? Palette.Pen(Palette.Fg, 1.5) : Palette.Pen(Palette.Alpha(Palette.DimColor, 0.7), 1), geo);
        if (!editable) return;
        foreach (var pt in pts)
        {
            double x = X(w.Origin + pt.Sec);
            if (x < r.Left - 4 || x > r.Right + 4) continue;
            ctx.DrawRectangle(Palette.Accent, Palette.Pen(Palette.App), new Rect(x - 4, CurveY(r, model.GainDb + pt.Db) - 4, 8, 8), 2, 2);
        }
    }

    /// <summary>The curve point under the pointer on an editable clip, or -1.</summary>
    int CurvePointAt(int lane, LiveClip c, Point p)
    {
        if (!CurveEditable(lane, c)) return -1;
        var w = Window(c);
        var r = ClipRect(lane, w);
        var pts = c.Curve;
        for (int k = pts.Length - 1; k >= 0; k--)
            if (Math.Abs(p.X - X(w.Origin + pts[k].Sec)) <= CurveGrab && Math.Abs(p.Y - CurveY(r, c.Clip.GainDb + pts[k].Db)) <= CurveGrab) return k;
        return -1;
    }

    /// <summary>True when the pointer is on the volume line of an editable clip (away from its trim edges).</summary>
    bool OnCurve(int lane, LiveClip c, Point p)
    {
        if (!CurveEditable(lane, c)) return false;
        var w = Window(c);
        var r = ClipRect(lane, w);
        return p.X > r.Left + 8 && p.X < r.Right - 8 && Math.Abs(p.Y - CurveY(r, LevelAt(c.Clip, c.Curve, w, Sec(p.X)))) <= CurveGrab;
    }

    /// <summary>Starts dragging a point of the selected clip's curve; a new one is added where the line was clicked.</summary>
    void StartCurve(int lane, LiveClip c, int point, PointerPressedEventArgs e)
    {
        dragged = c;
        original = ClipEditing.Copy(c.Clip);
        preview = ClipEditing.Copy(c.Clip);
        bool added = point < 0;
        if (added)
        {
            double sec = Sec(lastPointer.X) - Window(c).Origin;
            var point0 = new GainPoint { Sec = Math.Round(sec, 3), Db = Math.Round(ClipGainCurve.DbAt(c.Curve, sec), 1) };
            preview.GainCurve = [.. preview.GainCurve.Append(point0).OrderBy(g => g.Sec)];
            point = preview.GainCurve.IndexOf(point0);
        }
        curvePoint = point;
        StartGesture("curve", e);
        fromIndex = overIndex = lane;
        moved = added; // a new point is an edit even if it does not move
    }

    void DragCurve()
    {
        if (dragged == null || original == null || preview == null || curvePoint < 0 || curvePoint >= preview.GainCurve.Count) return;
        var w = Window(dragged, original);
        var r = ClipRect(fromIndex, w);
        var list = preview.GainCurve.Select(g => new GainPoint { Sec = g.Sec, Db = g.Db, Extra = g.Extra }).ToList();
        double lo = (curvePoint > 0 ? list[curvePoint - 1].Sec : w.Start - w.Origin) + 0.001;
        double hi = (curvePoint < list.Count - 1 ? list[curvePoint + 1].Sec : w.End - w.Origin) - 0.001;
        double sec = Sec(lastPointer.X) - w.Origin;
        list[curvePoint].Sec = Math.Round(hi > lo ? Math.Clamp(sec, lo, hi) : lo, 3);
        // the point holds the curve's part: the line shows it on top of the clip gain
        list[curvePoint].Db = Math.Round(Math.Clamp(CurveDb(r, lastPointer.Y) - preview.GainDb, ClipGainCurve.MinDb, ClipGainCurve.MaxDb), 1);
        preview.GainCurve = list;
    }

    /// <summary>Double click on a point: the curve without it, committed like any clip edit.</summary>
    void RemoveCurvePoint(LiveClip c, int point)
    {
        var value = ClipEditing.Copy(c.Clip);
        value.GainCurve.RemoveAt(point);
        ClipEditCommitted?.Invoke(value, null, null);
    }

    public override void Render(DrawingContext ctx)
    {
        // only with room past the 24 px margin: a timeline barely wider than its headers (mid-layout, at startup) would
        // give a zero or negative zoom, and the ruler's loops would never reach the right edge
        if (fitMode && Bounds.Width - HeaderW - 24 > 0)
        {
            double fit = (Bounds.Width - HeaderW - 24) / Math.Max(8, LengthSec);
            // the slider follows after this frame: setting it from inside Render would re-enter layout
            if (fit != pps) Dispatcher.UIThread.Post(() => ZoomChanged?.Invoke());
            pps = fit;
        }
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Palette.App, new Rect(0, 0, W, H));

        BuildGrid(W);
        FillDrawOrder();
        anySoloed = false;
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Soloed) { anySoloed = true; break; }
        litPattern = hoverPattern ?? SelectedPattern();
        using (ctx.PushClip(new Rect(HeaderW, RulerH, W - HeaderW, Math.Max(0, H - RulerH))))
        {
            // indexed loops: a foreach over the interfaces boxes an enumerator on every frame
            for (int k = 0; k < folders.Count; k++) if (folders[k].Id != liftedId) DrawFolderLane(ctx, folders[k], W);
            DrawDropHole(ctx, W);
            foreach (int i in drawOrder) DrawLane(ctx, i, W);
            DrawBusLanes(ctx, W);
            DrawAutoLanes(ctx, W);
            DrawPatternLinks(ctx, W);
            if (liftedId != null && folderById.TryGetValue(liftedId, out var liftedFolder)) DrawFolderLane(ctx, liftedFolder, W);
            if (recordingLanes is HashSet<int> recSet)
                foreach (int r in recSet) DrawRecordingBar(ctx, r, W);
            else if (recordingLanes != null)
                foreach (int r in recordingLanes) DrawRecordingBar(ctx, r, W);
            DrawMidiCarried(ctx, W);
            DrawStripGuides(ctx, W, H);
            DrawSocialMarks(ctx, W);
            // the snap guide: the edge the dragged clip is stuck to, across every lane (asked by the user)
            if (gesture == "clip" && stuckTo is { } guide && X(guide) >= HeaderW)
            {
                double gx = Math.Round(X(guide)) + 0.5;
                ctx.DrawLine(Palette.Pen(Palette.Accent), new Point(gx, RulerH), new Point(gx, H));
            }
            DrawStretchAmount(ctx);
            if (gesture == "box" && moved)
                ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.08), Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.7)), BoxRect());
            DrawZoomBand(ctx, H);
            DrawPencil(ctx);
        }
        DrawRuler(ctx, W);
        DrawRangeStrip(ctx, W);
        DrawStrip(ctx, W);
        DrawChordStrip(ctx, W);
        DrawTempoStrip(ctx, W);

        // sticky headers
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, H));
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, RulerH));
        DrawStripLabels(ctx);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, RulerH - 0.5), new Point(W, RulerH - 0.5));
        using (ctx.PushClip(new Rect(0, RulerH, HeaderW, Math.Max(0, H - RulerH))))
        {
            for (int k = 0; k < folders.Count; k++) if (folders[k].Id != liftedId) DrawFolderHeader(ctx, folders[k]);
            foreach (int i in drawOrder) DrawHeader(ctx, i);
            DrawBusHeaders(ctx);
            DrawAutoHeaders(ctx);
            if (liftedId != null && folderById.TryGetValue(liftedId, out var liftedFolder)) DrawFolderHeader(ctx, liftedFolder);
        }
        using (ctx.PushClip(new Rect(0, RulerH, W, Math.Max(0, H - RulerH))))
        {
            DrawFolderFrames(ctx, W);
            DrawDropTarget(ctx, W);
        }
        if (ExtraRows > 0) DrawRecordingHeader(ctx, LaneY(tracks.Count), W);
        if (tracks.Count == 0 && folders.Count == 0 && busRows.Count == 0 && !Recording)
        {
            ctx.DrawText(Loc.T("timeline.empty.title"), 12.5, Palette.Dim, new Point(HeaderW + 24, RulerH + 66), Palette.UiBold);
            ctx.DrawText(Loc.T("timeline.empty.a"), 12,
                Palette.Faint, new Point(HeaderW + 24, RulerH + 90));
        }
        if (cloning && moved) DrawLeftBehind(ctx, W, H);
        if (toNewTrack && moved) DrawNewTrackLane(ctx, W, H);
        else if (dragged != null && preview != null && overIndex >= 0 && overIndex != fromIndex && !swapping)
            DrawGhost(ctx, W, H);
        else if (dragged != null && overIndex >= 0 && overIndex != fromIndex)
        {
            var r = new Rect(HeaderW + 1, LaneY(overIndex) + 1, Math.Max(1, W - HeaderW - 2), LaneH(overIndex) - 2);
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.1), Palette.Pen(Palette.Accent, 2), r);
            string text = LocText(swapId == null ? "timeline.moveTo" : "timeline.swapWith", tracks[overIndex].Track.Name);
            ctx.DrawText(text, 11, Palette.Accent, new Point(Math.Max(HeaderW + 10, W - 280), r.Y + 8), Palette.UiBold);
        }
        DrawRowEdge(ctx);
        DrawColumnGrip(ctx, H);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW - 0.5, 0), new Point(HeaderW - 0.5, H));
    }

    /// <summary>The bottom edge of the row whose height is being changed (or that the pointer would grab), lit across the header.</summary>
    void DrawRowEdge(DrawingContext ctx)
    {
        string? id = gesture == "row-height" ? heightDrag?.Id : gesture == null ? RowEdgeAt(lastPointer) : null;
        if (id == null) return;
        foreach (var r in rows)
        {
            if (RowIdOf(r) != id) continue;
            double y = RulerH + r.Top - scrollY + r.H;
            ctx.FillRectangle(Palette.Accent, new Rect(0, y - 1, HeaderRight, 2));
            return;
        }
    }

    /// <summary>Empty space the scroll leaves under the last row, so a full view still has somewhere to right click for a new track.</summary>
    const double TailH = 64;

    /// <summary>
    /// The column's resize grip: one strip for every track, the whole height under the ruler, with two short lines
    /// in the middle of the view. It lights up while the pointer is on it or dragging it.
    /// </summary>
    void DrawColumnGrip(DrawingContext ctx, double H)
    {
        bool lit = gesture == "header" || gesture == null && OnGrip(lastPointer);
        var strip = new Rect(HeaderRight, RulerH, GripW, Math.Max(0, H - RulerH));
        ctx.FillRectangle(lit ? Palette.Hover : Palette.Panel, strip);
        var grip = lit ? Palette.Accent : Palette.Faint;
        double gx = HeaderW - GripW / 2, gy = Math.Round(strip.Center.Y);
        ctx.FillRectangle(grip, new Rect(gx - 2, gy - 10, 1, 20));
        ctx.FillRectangle(grip, new Rect(gx + 1, gy - 10, 1, 20));
    }

    /// <summary>
    /// The "ghost" of a clip carried to another track (native only): a dashed outline where it will land, on the
    /// lane under the pointer and at the snapped time, with the track and the time it will start at. Drawn over the
    /// lanes and under the sticky headers, so it never covers the header column.
    /// </summary>
    void DrawGhost(DrawingContext ctx, double W, double H)
    {
        // over a MIDI track it shows where the notes will land: under the take, where it is now
        bool toMidi = tracks[overIndex].Track.IsMidi;
        var w = Window(dragged!, toMidi ? original : preview);
        double y = LaneY(overIndex);
        ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.06), new Rect(HeaderW, y, Math.Max(0, W - HeaderW), LaneH(overIndex)));
        double x0 = X(w.Start), x1 = X(w.End);
        var box = new Rect(x0, y + 5, Math.Max(2, x1 - x0), LaneH(overIndex) - 10);
        using (ctx.PushClip(new Rect(HeaderW, RulerH, Math.Max(0, W - HeaderW), Math.Max(0, H - RulerH))))
        {
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.18), Palette.Dashed(Palette.Accent, 1.5), box, 5, 5);
            var label = Palette.Text(toMidi ? LocText("toMidi.drop", tracks[overIndex].Track.Name) : GhostText(tracks[overIndex].Track.Name, w.Start),
                11, Palette.Accent, Palette.UiBold);
            double lx = Math.Clamp(box.X + 8, HeaderW + 8, Math.Max(HeaderW + 8, W - label.Width - 8));
            ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, new Rect(lx - 4, box.Y + 3, label.Width + 8, label.Height + 2), 3, 3);
            ctx.DrawText(label, new Point(lx, box.Y + 4));
        }
    }

    /// <summary>
    /// The "new track" lane under the rows while a clip is held over the empty space (#309): a take shows where it will
    /// land as a dashed box, like over another track; a MIDI clip is drawn there whole.
    /// </summary>
    void DrawNewTrackLane(DrawingContext ctx, double W, double H)
    {
        var lane = NewTrackLane(W);
        using (ctx.PushClip(new Rect(HeaderW, RulerH, Math.Max(0, W - HeaderW), Math.Max(0, H - RulerH))))
        {
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.06), Palette.Dashed(Palette.Accent, 1.5), lane.Deflate(1), 5, 5);
            if (dragged != null && preview != null)
            {
                var w = Window(dragged, preview);
                double x0 = X(w.Start), x1 = X(w.End);
                ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.18), Palette.Dashed(Palette.Accent, 1.5), new Rect(x0, lane.Y + 5, Math.Max(2, x1 - x0), lane.Height - 10), 5, 5);
            }
            else if (midiHeld != null && fromIndex >= 0 && fromIndex < tracks.Count)
                DrawMidiClip(ctx, tracks[fromIndex], midiHeld, lane.Y, lane.Height, W, true);
            var label = Palette.Text(Loc.T("timeline.toNewTrack"), 11, Palette.Accent, Palette.UiBold);
            double lx = Math.Max(HeaderW + 10, W - label.Width - 16);
            ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, new Rect(lx - 4, lane.Y + 7, label.Width + 8, label.Height + 2), 3, 3);
            ctx.DrawText(label, new Point(lx, lane.Y + 8));
        }
    }

    /// <summary>Past this many entries a cache of drawn texts below starts over (a long drag, a zoom through many values).</summary>
    const int DrawnTextMax = 1024;
    /// <summary>A localized text with one argument (a track or folder name), by key and argument: drawn on every frame of a drag.</summary>
    readonly Dictionary<(string Key, string Arg), string> argTexts = [];
    /// <summary>"1.25 s" by hundredth of a second (a range's length, a clip's anchor cut).</summary>
    readonly Dictionary<long, string> secTexts = [];
    /// <summary>The folder headers' track counts, by count.</summary>
    readonly Dictionary<int, string> folderCounts = [];
    /// <summary>The ghost's "Track · 1.25 s" as last drawn: made again only when the track or the shown hundredth changes.</summary>
    string? ghostName;
    long ghostKey = long.MinValue;
    string ghostText = "";
    /// <summary>Whether any track is soloed, set at the start of each Render (a lookup per clip was a closure per clip).</summary>
    bool anySoloed;

    /// <summary>A <see cref="Loc"/> text with one argument, made once per key and argument.</summary>
    string LocText(string key, string arg)
    {
        if (argTexts.TryGetValue((key, arg), out var s)) return s;
        if (argTexts.Count >= DrawnTextMax) argTexts.Clear();
        return argTexts[(key, arg)] = Loc.T(key, arg);
    }

    /// <summary>A time in seconds as "0.00 s", made once per hundredth.</summary>
    string SecText(double sec)
    {
        long key = (long)Math.Round(sec * 100, MidpointRounding.AwayFromZero);
        if (secTexts.TryGetValue(key, out var s)) return s;
        if (secTexts.Count >= DrawnTextMax) secTexts.Clear();
        return secTexts[key] = $"{key / 100.0:0.00} s";
    }

    /// <summary>The ghost's label, the track it lands on and its start.</summary>
    string GhostText(string name, double start)
    {
        long key = (long)Math.Round(start * 100, MidpointRounding.AwayFromZero);
        if (key != ghostKey || name != ghostName)
        {
            ghostKey = key;
            ghostName = name;
            ghostText = $"{name} · {key / 100.0:0.00} s";
        }
        return ghostText;
    }

    /// <summary>Who stays behind in a Shift+drag: a dashed outline where each clip was, that turns into the copy on release.</summary>
    void DrawLeftBehind(DrawingContext ctx, double W, double H)
    {
        var dashed = Palette.Dashed(Palette.Accent, 1.5);
        using (ctx.PushClip(new Rect(HeaderW, RulerH, Math.Max(0, W - HeaderW), Math.Max(0, H - RulerH))))
        {
            void Outline(int lane, double start, double end) =>
                ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.18), dashed, new Rect(X(start), LaneY(lane) + 5, Math.Max(2, X(end) - X(start)), LaneH(lane) - 10), 5, 5);
            if (dragged != null && original != null && Window(dragged, original) is var w) Outline(fromIndex, w.Start, w.End);
            for (int i = 0; i < tracks.Count; i++)
            {
                foreach (var (c, o) in carriedAlong)
                    if (tracks[i].Clips.Contains(c) && Window(c, o) is var along) Outline(i, along.Start, along.End);
                if (tracks[i].Track.MidiClips is not { } midi) continue;
                foreach (var (c, from) in midiCarried)
                    if (midi.Contains(c)) Outline(i, from, SecPlus(from, c.LengthBeats));
            }
            if (midiHeld != null && !midiCarrying) Outline(fromIndex, midiFromSec, SecPlus(midiFromSec, midiFromBeats));
            // the boxed automation blocks, in their lanes (#281)
            if (autoCarried.Count == 0) return;
            foreach (var r in rows)
            {
                if (r.Lane is not { } lane) continue;
                foreach (var b in lane.Blocks)
                    if (autoCarried.TryGetValue(b, out double from))
                        ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.18), dashed, new Rect(X(from), AutoY(r) + 1, Math.Max(3, X(from + b.LengthSec) - X(from)), r.H - 3), 4, 4);
            }
        }
    }

    /// <summary>The selected range over its lane: a band of the accent with the length on top.</summary>
    void DrawRange(DrawingContext ctx, double y, double lh, double W, double start, double end)
    {
        double x0 = Math.Max(HeaderW, X(start)), x1 = Math.Min(W, X(end));
        if (x1 <= x0) return;
        var band = new Rect(x0, y + 1, x1 - x0, lh - 2);
        ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.22), band);
        var pen = Palette.Pen(Palette.Accent, 1.5);
        if (X(start) >= HeaderW) ctx.DrawLine(pen, new Point(X(start), y + 1), new Point(X(start), y + lh - 1));
        if (X(end) <= W) ctx.DrawLine(pen, new Point(X(end), y + 1), new Point(X(end), y + lh - 1));
        var label = Palette.Text(SecText(end - start), 10.5, Palette.Accent, Palette.Mono);
        if (band.Width > label.Width + 12)
        {
            ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, new Rect(band.X + 4, band.Y + 3, label.Width + 8, label.Height + 2), 3, 3);
            ctx.DrawText(label, new Point(band.X + 8, band.Y + 4));
        }
    }

    /// <summary>The take being recorded: a red bar from 0 to the playhead (Timeline.tsx recBar).</summary>
    void DrawRecordingBar(DrawingContext ctx, int lane, double W)
    {
        double y = LaneY(lane);
        if (lane == tracks.Count) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.14 * 0.4), new Rect(HeaderW, y, W - HeaderW, laneH));
        double x0 = X(RecordingStartSec), x1 = X(Playhead);
        if (x1 > x0) ctx.DrawRectangle(Palette.Alpha(Palette.RecColor, 0.2), Palette.Pen(Palette.Alpha(Palette.RecColor, 0.5)), new Rect(x0, y + 6, x1 - x0, LaneH(lane) - 12), 4, 4);
        if (lane == tracks.Count) ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW, y + laneH - 0.5), new Point(W, y + laneH - 0.5));
    }

    /// <summary>The extra row of a take going to a new track (Timeline.tsx extraRecRow).</summary>
    void DrawRecordingHeader(DrawingContext ctx, double y, double W)
    {
        ctx.FillRectangle(Palette.Brush("#1d1214"), new Rect(0, y, HeaderW, laneH));
        var badge = new Rect(12, y + (laneH - 20) / 2, 20, 20);
        ctx.DrawRectangle(Palette.RecSoft, null, badge, 4, 4);
        ctx.DrawEllipse(Palette.Rec, null, badge.Center, 4, 4);
        var text = Palette.Text(Loc.T("timeline.recording"), 13, Palette.Rec);
        if (42 + text.Width <= HeaderRight) ctx.DrawText(text, new Point(42, y + (laneH - text.Height) / 2));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + laneH - 0.5), new Point(W, y + laneH - 0.5));
    }

    static readonly Typeface Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    /// <summary>The gray of a clip whose file is missing (the "gris offline" token).</summary>
    static readonly Color OfflineColor = Color.Parse("#71717a");
    /// <summary>The dark gradient under the wave of a clip with frames (bottom 80 % black to clear at the top): one
    /// immutable brush for every clip, instead of a new one per clip and frame.</summary>
    static readonly IBrush ClipShade = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(204, 0, 0, 0), 0),
            new GradientStop(Color.FromArgb(128, 0, 0, 0), 0.5),
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
        },
    }.ToImmutable();
    /// <summary>A fade's shade as a unit right triangle, mapped onto each fade by a transform (<see cref="FadeShade"/>)
    /// instead of a new geometry per fade and frame.</summary>
    static readonly Geometry FadeTriangle = Geometry.Parse("M 0,0 L 1,0 L 0,1 Z");

    /// <summary>Px of a folded folder's mini map strip below which its clips show as plain bars, without their wave.</summary>
    const double MiniWaveAt = 6;

    /// <summary>
    /// A folder's lane, tinted with its color. Open, its children show below it, so the lane only sums up where they have
    /// clips (<see cref="DrawFolderSummary"/>; a block per clip there only repeated them). Folded, it is a mini map: one
    /// strip per child track, in order, with its clips in the track's color and their wave once the strip is tall enough;
    /// the row follows the vertical zoom.
    /// </summary>
    void DrawFolderLane(DrawingContext ctx, Folder f, double W)
    {
        double y = FolderY(f), fh = FolderRowH(f);
        if (fh <= 0 || y + fh < RulerH || y > Bounds.Height) return;
        if (f.Separator)
        {
            // a separator: its line across the lanes, at the middle of its row, in the look picked in Apariencia (#293)
            if (f.Id == liftedId) ctx.FillRectangle(Palette.App, new Rect(HeaderW, y, Math.Max(0, W - HeaderW), fh));
            SeparatorLooks.DrawRow(ctx, SeparatorLooks.Current, SeparatorColor(f), HeaderW, HeaderW, W, y, fh, null, 0);
            return;
        }
        var c = FolderColor(f);
        var row = new Rect(HeaderW, y, Math.Max(0, W - HeaderW), fh);
        if (f.Id == liftedId) ctx.FillRectangle(Palette.App, row);
        ctx.FillRectangle(Palette.Alpha(c, 0.07), row);
        // a lifted folder travels folded, so it shows its mini map while dragged too
        if (f.Collapsed || f.Id == liftedId) DrawMiniMap(ctx, f, y, fh, W);
        else DrawFolderSummary(ctx, f, y, fh, W, c);
        DrawRowBottom(ctx, f.Id, HeaderW, W, y + fh);
    }

    /// <summary>Accent over a selected track's header and lane, so it reads as chosen and not as a hover (asked by the user).</summary>
    const double SelectedTint = 0.06;
    /// <summary>Opacity of a selected header's outline.</summary>
    const double SelectedEdge = 0.8;

    /// <summary>
    /// The selected row's mark: an accent outline around its header, the way a selected mixer strip shows it (asked by
    /// the user: no bar on the side). Inside the card's edge and the folder frame, which would cover it.
    /// </summary>
    void DrawSelectedFrame(DrawingContext ctx, double y, double h) => DrawSelectedFrame(ctx, y, h, Palette.AccentColor);

    /// <summary>The same mark in <paramref name="c"/>: a track's own color when Apariencia asks for it (#162).</summary>
    void DrawSelectedFrame(DrawingContext ctx, double y, double h, Color c) =>
        ctx.DrawRectangle(null, Palette.Pen(Palette.Alpha(c, SelectedEdge)), SelectedBox(y, h), SelectedRadius, SelectedRadius);

    /// <summary>The selected frame's line (half-pixel), also the rounded box the header's color stripe follows.</summary>
    Rect SelectedBox(double y, double h) => new(3.5, y + 1.5, Math.Max(0, HeaderW - 7), Math.Max(0, h - 3));
    const double SelectedRadius = 6;

    /// <summary>Lane columns covered by a child's clip, reused by <see cref="DrawFolderSummary"/> so it allocates nothing per frame.</summary>
    bool[] covered = [];

    /// <summary>
    /// The open folder's lane: one bar in its color per stretch where any of its tracks has a clip, merged across tracks
    /// (asked by the user), so the material shows at a glance even when the tracks are scrolled away.
    /// </summary>
    void DrawFolderSummary(DrawingContext ctx, Folder f, double y, double fh, double W, Color c)
    {
        int cols = (int)Math.Ceiling(W - HeaderW);
        if (cols <= 0) return;
        if (covered.Length < cols) covered = new bool[cols];
        Array.Clear(covered, 0, cols);
        for (int ti = 0; ti < tracks.Count; ti++)
        {
            var t = tracks[ti];
            if (t.Track.FolderId != f.Id) continue;
            for (int ci = 0; ci < t.Clips.Count; ci++)
            {
                var w = Window(t.Clips[ci]);
                Cover(X(w.Start), X(w.End), cols);
            }
            if (t.Track.MidiClips is { } midi)
                foreach (var m in midi)
                {
                    var (start, end) = MidiSpan(m);
                    Cover(X(start), X(end), cols);
                }
        }
        var fill = Palette.Alpha(c, 0.16);
        var pen = Palette.Pen(Palette.Alpha(c, 0.4));
        // a band as tall as a folder without knobs needs, centered in a taller row
        double h = Math.Max(2, Math.Min(FolderH, fh) - 14), top = y + (fh - h) / 2;
        for (int i = 0; i < cols; i++)
        {
            if (!covered[i]) continue;
            int from = i;
            while (i < cols && covered[i]) i++;
            ctx.DrawRectangle(fill, pen, new Rect(HeaderW + from + 0.5, top + 0.5, Math.Max(1, i - from - 1), h - 1), 3, 3);
        }
    }

    void Cover(double x0, double x1, int cols)
    {
        int a = Math.Max(0, (int)Math.Floor(x0 - HeaderW)), b = Math.Min(cols, (int)Math.Ceiling(x1 - HeaderW));
        // at least a column, so a clip shorter than a pixel still shows
        if (b <= a && a < cols && x1 >= HeaderW) b = a + 1;
        for (int i = a; i < b; i++) covered[i] = true;
    }

    /// <summary>The folded folder's mini map (<see cref="DrawFolderLane"/>): a strip per child track over the row's height.</summary>
    void DrawMiniMap(DrawingContext ctx, Folder f, double y, double fh, double W)
    {
        int n = 0;
        for (int ti = 0; ti < tracks.Count; ti++)
            if (tracks[ti].Track.FolderId == f.Id) n++;
        if (n == 0) return;
        double top = y + 4, strip = (fh - 8) / n;
        int k = 0;
        for (int ti = 0; ti < tracks.Count; ti++)
        {
            var t = tracks[ti];
            if (t.Track.FolderId != f.Id) continue;
            double sy = top + k++ * strip;
            bool silent = t.Silenced || anySoloed && !t.Soloed;
            var tint = TrackIcons.ColorOf(t.Track) ?? Palette.AccentColor;
            for (int ci = 0; ci < t.Clips.Count; ci++)
            {
                var clip = t.Clips[ci];
                var w = Window(clip);
                double x0 = X(w.Start), x1 = X(w.End);
                if (x1 < HeaderW || x0 > W) continue;
                var color = clip.Offline ? OfflineColor : tint;
                var bar = new Rect(x0, sy + 0.5, Math.Max(2, x1 - x0), Math.Max(1, strip - 1));
                ctx.DrawRectangle(Palette.Alpha(color, silent ? 0.08 : 0.2), strip >= MiniWaveAt ? Palette.Pen(Palette.Alpha(color, silent ? 0.2 : 0.45)) : null,
                    bar, Math.Min(3, bar.Height / 2), Math.Min(3, bar.Height / 2));
                if (strip < MiniWaveAt || clip.Offline || clip.Audio == null) continue;
                var (geo, line) = Waveform(clip, clip.Clip, w, bar.Top + 1, bar.Bottom - 1, W);
                using (ctx.PushClip(bar))
                    WaveLooks.Paint(WaveLooks.Current, ctx, geo, silent ? Palette.Faint : Palette.Alpha(color, 0.8), line);
            }
            if (t.Track.MidiClips is not { } midi) continue;
            foreach (var m in midi)
            {
                var (start, end) = MidiSpan(m);
                double x0 = X(start), x1 = X(end);
                if (x1 < HeaderW || x0 > W) continue;
                var bar = new Rect(x0, sy + 0.5, Math.Max(2, x1 - x0), Math.Max(1, strip - 1));
                ctx.DrawRectangle(Palette.Alpha(tint, silent ? 0.08 : 0.2), strip >= MiniWaveAt ? Palette.Pen(Palette.Alpha(tint, silent ? 0.2 : 0.45)) : null,
                    bar, Math.Min(3, bar.Height / 2), Math.Min(3, bar.Height / 2));
                if (strip < MiniWaveAt || m.Notes.Count == 0) continue;
                using (ctx.PushClip(bar))
                    DrawMiniNotes(ctx, m, start, end, bar.Top + 1, bar.Height - 2, W, silent ? Palette.Faint : Palette.Alpha(tint, 0.8));
            }
        }
    }

    /// <summary>A MIDI clip's notes squeezed into a mini map strip, repeats of a pattern included, like the lane draws them.</summary>
    void DrawMiniNotes(DrawingContext ctx, MidiClip c, double start, double end, double top, double h, double W, IBrush fill)
    {
        int lo = 127, hi = 0;
        foreach (var n in c.Notes) { lo = Math.Min(lo, n.Pitch); hi = Math.Max(hi, n.Pitch); }
        int span = Math.Max(12, hi - lo + 1);
        lo -= (span - (hi - lo + 1)) / 2;
        double rowH = Math.Max(1, Math.Min(2, h / span)), sb = tempo.BeatAt(start);
        double length = BeatsBetween(start, end), cell = c.Repeats ? c.CellBeats : length;
        for (int pass = Math.Max(0, (int)Math.Floor((tempo.BeatAt(Sec(HeaderW)) - sb) / cell)); pass * cell < length - 1e-9; pass++)
        {
            if (BeatX(sb + pass * cell) > W) break;
            foreach (var n in c.Notes)
            {
                if (!MidiClip.Slice(n, pass, cell, length, out double from, out double to)) continue;
                double nx = BeatX(sb + from);
                if (nx > W) break;
                double nw = Math.Max(1, BeatX(sb + to) - nx - 1);
                if (nx + nw < HeaderW) continue;
                ctx.FillRectangle(fill, new Rect(nx, top + (span - 1 - (n.Pitch - lo)) / (double)span * (h - rowH), nw, rowH));
            }
        }
    }

    Rect FoldRect(Folder f) => new(20, FolderY(f) + 5, 17, 24);
    Rect FolderIconRect(Folder f) => new(37, FolderY(f) + 5, 22, 24);
    /// <summary>Where a folder's name sits (and its in-place editor goes).</summary>
    public Rect FolderNameRect(Folder f) => new(60, FolderY(f) + 5, Math.Max(Compact ? CompactEditorW : 40, FolderMuteRect(f).X - 66), 24);
    /// <summary>The room for a folder's drawn name: a compact one has no track count, M S or remove button beside it.</summary>
    double FolderNameW(Folder f) => Compact ? HeaderRight - 68 : Math.Max(20, FolderMuteRect(f).X - 8 - 62 - 64);
    /// <summary>A folder's M and S, where a track's sit (#134: a folder is a channel).</summary>
    Rect FolderMuteRect(Folder f) => Compact ? NoRect : new(HeaderRight - 94, FolderY(f) + 5, 24, 24);
    Rect FolderSoloRect(Folder f) => Compact ? NoRect : new(HeaderRight - 66, FolderY(f) + 5, 24, 24);
    /// <summary>Just the drawn name of a folder (a double click there renames it; elsewhere on the row it folds).</summary>
    Rect FolderTitleRect(Folder f)
    {
        if (Compact && FolderNameW(f) < CompactNameMin) return NoRect;
        double w = Math.Min(Palette.Text(f.Name, 12.5, Palette.Fg, Palette.UiBold).Width, FolderNameW(f));
        return new(58, FolderY(f) + 5, Math.Max(20, w) + 8, 24);
    }
    Rect FolderRemoveRect(Folder f) => Compact ? NoRect : new(HeaderRight - 38, FolderY(f) + 5, 24, 24);
    /// <summary>Px of row from which a track's header shows its volume and pan.</summary>
    const double ControlsAt = 62;
    bool ShowsControls(int i) => !Compact && LaneH(i) >= ControlsAt;

    /// <summary>A folder's header: its tint, handle, fold arrow and icon, name and track count, M S and ×, and under them its volume and pan.</summary>
    void DrawFolderHeader(DrawingContext ctx, Folder f)
    {
        double y = FolderY(f), fh = FolderRowH(f);
        if (fh <= 0 || y + fh < RulerH || y > Bounds.Height) return;
        if (f.Separator) { DrawSeparatorHeader(ctx, f, y, fh); return; }
        var c = FolderColor(f);
        var row = new Rect(0, y, HeaderW, fh);
        bool sel = f.Id == SelectedFolderId;
        ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, row);
        ctx.FillRectangle(Palette.Alpha(c, sel ? 0.18 : 0.12), row);
        if (sel) DrawSelectedFrame(ctx, y, fh);
        for (int k = 0; k < 3; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(8, y + 12 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(13, y + 12 + k * 5, 2, 2));
        }
        bool overFold = gesture == null && FoldRect(f).Contains(lastPointer);
        if (overFold) ctx.DrawRectangle(Palette.Hover, null, FoldRect(f), 4, 4);
        if (gesture == null && FolderIconRect(f).Contains(lastPointer)) ctx.DrawRectangle(Palette.Hover, null, FolderIconRect(f), 4, 4);
        ctx.DrawText(f.Collapsed ? "" : "", 10, Palette.Dim, new Point(24, y + 12), Icons);
        ctx.DrawText(f.Collapsed ? "" : "", 15, Palette.Alpha(c, 1), new Point(38, y + 9), Icons);
        int n = 0;
        for (int k = 0; k < tracks.Count; k++) if (tracks[k].Track.FolderId == f.Id) n++;
        double maxW = FolderNameW(f);
        var name = Palette.Text(f.Name, 12.5, Palette.Fg, Palette.UiBold);
        if (editingNameId != f.Id && (!Compact || maxW >= CompactNameMin))
            ctx.DrawText(f.Name, 12.5, Palette.Fg, new Point(62, y + 9), Palette.UiBold, maxW);
        if (editingNameId != f.Id && !Compact)
        {
            string count = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : FolderCountText(n);
            ctx.DrawText(count, 11, Palette.Faint, new Point(62 + Math.Min(name.Width, maxW) + 8, y + 11), Palette.Ui);
        }
        if (!Compact && lastPointer.Y >= y && lastPointer.Y < y + fh && lastPointer.X < HeaderW)
            DrawTrash(ctx, FolderRemoveRect(f), Hot(FolderRemoveRect(f)));
        if (!Compact)
        {
            DrawToggle(ctx, FolderMuteRect(f), "M", f.Muted, Palette.Mute, Hot(FolderMuteRect(f)));
            DrawToggle(ctx, FolderSoloRect(f), "S", f.Solo, Palette.Accent, Hot(FolderSoloRect(f)));
        }
        if (FolderShowsControls(f))
        {
            // like a track's: a lane moves them while it plays
            var (volume, pan) = FolderShown?.Invoke(f) ?? (f.Volume, f.Pan);
            var vr = FolderVolumeRect(f);
            DrawVolume(ctx, vr, volume, Palette.Accent);
            ctx.DrawText(VolumeText(volume), 11, Palette.Fg, new Point(148 + Extra, vr.Y + 2), Palette.Mono);
            var pr = FolderPanRect(f);
            Knob.Draw(ctx, pr.Center, pr.Width, pan * 135, 0, true, Hot(pr));
            ctx.DrawText(PanText(pan), 10.5, Palette.Faint, new Point(216 + Extra, pr.Y + 3), Palette.Mono);
        }
        DrawFolderKnobs(ctx, f);
        DrawRowBottom(ctx, f.Id, 0, HeaderW, y + fh);
    }

    /// <summary>"5 pistas" for a folder header, made once per count.</summary>
    string FolderCountText(int n)
    {
        if (folderCounts.TryGetValue(n, out var s)) return s;
        if (folderCounts.Count >= DrawnTextMax) folderCounts.Clear();
        return folderCounts[n] = Loc.T("folder.count", n);
    }

    /// <summary>
    /// A separator's color: its own, or the accent (asked by the user: the line glows from the chosen accent). Folders
    /// stay gray without one (<see cref="FolderColor"/>), since in the accent their frame looked selected.
    /// </summary>
    public static Color SeparatorColor(Folder f) => f.Color != null && Color.TryParse(f.Color, out var c) ? c : Palette.AccentColor;

    /// <summary>Px from a separator header's left edge to where its line starts, past the handle.</summary>
    const double SeparatorLineX = 22;

    /// <summary>Upper-cased separator names, kept so a frame doesn't make a new string per separator.</summary>
    readonly Dictionary<string, string> upperNames = [];

    string UpperName(string name)
    {
        if (!upperNames.TryGetValue(name, out var up)) upperNames[name] = up = name.ToUpperInvariant();
        return up;
    }

    /// <summary>A separator's header: its handle, then its row in the look picked in Apariencia (<see cref="SeparatorLooks"/>,
    /// #293; the console's double line by default, #289): a short lead, its name small and upper-cased in its color and
    /// the line on to the right edge, over the window's background, so it reads as a gap between the groups and not as
    /// one more row.</summary>
    void DrawSeparatorHeader(DrawingContext ctx, Folder f, double y, double fh)
    {
        var c = SeparatorColor(f);
        ctx.FillRectangle(Palette.App, new Rect(0, y, HeaderW, fh));
        string? up = editingNameId != f.Id && f.Name.Length > 0 ? UpperName(f.Name) : null;
        double nameX = SeparatorLineX + SeparatorLooks.Lead + SeparatorLooks.NameGap;
        SeparatorLooks.DrawRow(ctx, SeparatorLooks.Current, c, 0, SeparatorLineX, HeaderW, y, fh, up, HeaderRight - nameX - 16);
        for (int k = 0; k < 2; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(8, y + fh / 2 - 3 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(13, y + fh / 2 - 3 + k * 5, 2, 2));
        }
    }

    /// <summary>The folder the dragged track would land in, or the folder a track is in.</summary>
    Folder? RailFolder(int i)
    {
        var t = tracks[i];
        if (IsDragged(t.Track.Id) && drop is { } d) return d.Folder is { } id ? folderById.GetValueOrDefault(id) : null;
        return FolderOf(t);
    }

    /// <summary>Where the dragged row will land: a tinted place with a dashed outline, under everything else.</summary>
    void DrawDropHole(DrawingContext ctx, double W)
    {
        if (gesture != "reorder" || !moved || drop == null) return;
        string id = dragFolder?.Id ?? dragIds[0];
        if (!targetTop.TryGetValue(id, out double top)) return;
        double h = 0;
        if (dragFolder != null) h = FolderRowH(dragFolder);
        else
            for (int k = 0; k < tracks.Count; k++)
                if (dragIds.Contains(tracks[k].Track.Id)) h += RowH(tracks[k].Track);
        var r = new Rect(1, RulerH + top - scrollY + 1, Math.Max(0, W - 2), h - 2);
        var color = dragFolder != null ? FolderColor(dragFolder) : RailFolder(TrackIndex(id)) is { } f ? FolderColor(f) : Palette.AccentColor;
        ctx.DrawRectangle(Palette.Alpha(color, 0.08), Palette.Dashed(Palette.Alpha(color, 0.7), 1.5), r, 5, 5);
    }

    /// <summary>
    /// A thin frame in the folder's color around its row and its tracks, header and lanes (asked by the user: a rail and a
    /// tint on every child looked like they were all selected). Left out while a reorder drags rows around: the drop
    /// target draws its own frame then.
    /// </summary>
    void DrawFolderFrames(DrawingContext ctx, double W)
    {
        if (gesture == "reorder" && moved) return;
        for (int k = 0; k < folders.Count; k++)
        {
            var f = folders[k];
            if (f.Separator) continue;
            double top = FolderY(f), bottom = top + FolderBlockH(f);
            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Track.FolderId != f.Id) continue;
                double y = LaneY(i);
                if (y > -1e8) bottom = Math.Max(bottom, y + BlockH(tracks[i].Track));
            }
            if (bottom < RulerH || top > Bounds.Height) continue;
            var pen = Palette.Pen(Palette.Alpha(FolderColor(f), 0.45));
            // one px in: the card's edge ("card-edge") is drawn over the timeline's outer pixel and hid the sides
            ctx.DrawRectangle(null, pen, new Rect(1.5, top + 0.5, Math.Max(0, W - 3), Math.Max(0, bottom - top - 1)), 5, 5);
        }
    }

    /// <summary>
    /// What a dragged track does to folders, drawn over the rows: the folder it goes into gets an outline in its color
    /// around its row and children, and a label next to the track says "Entra en …" or "Sale de …".
    /// </summary>
    void DrawDropTarget(DrawingContext ctx, double W)
    {
        if (gesture != "reorder" || !moved || drop is not { } d || dragFolder != null || dragTrackId == null) return;
        int i = TrackIndex(dragTrackId);
        if (i < 0) return;
        var from = FolderOf(tracks[i]);
        var into = d.Folder is { } id ? folderById.GetValueOrDefault(id) : null;
        if (into != null)
        {
            double top = FolderY(into), bottom = top + FolderRowH(into);
            foreach (var r in rows)
                if (r.Folder == null && r.Lane == null && r.Bus == null && (tracks[r.Track].Track.FolderId == into.Id || IsDragged(tracks[r.Track].Track.Id)) && targetTop.ContainsKey(tracks[r.Track].Track.Id))
                {
                    double ry = RulerH + r.Top - scrollY;
                    if (ry >= top) bottom = Math.Max(bottom, ry + r.H);
                }
            var c = FolderColor(into);
            ctx.DrawRectangle(null, Palette.Pen(Palette.Alpha(c, 1), 2), new Rect(2, top + 1, Math.Max(0, W - 4), bottom - top - 2), 6, 6);
        }
        string? text = into != null && into != from ? LocText("tl.drop.into", into.Name) : into == null && from != null ? LocText("tl.drop.out", from.Name) : null;
        if (text == null) return;
        var color = into != null ? FolderColor(into) : Palette.FgColor;
        var label = Palette.Text(text, 11, Palette.Alpha(color, 1), Palette.UiBold);
        double y = LaneY(i) + 8, x = HeaderW + 12;
        ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.85), Palette.Pen(Palette.Alpha(color, 0.6)), new Rect(x - 6, y - 3, label.Width + 12, label.Height + 6), 4, 4);
        ctx.DrawText(label, new Point(x, y));
    }

    // seconds between labels in the time ruler (down to 1 ms for the deepest zoom) and minor ticks inside each
    internal static readonly double[] TimeSteps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
    internal static readonly int[] TimeDivs = [5, 4, 5, 5, 4, 5, 5, 5, 5, 5, 4, 5, 5, 3, 6, 6, 4, 5, 5, 6];
    /// <summary>Px between the finest bars-mode ticks: zoomed in, beats split in halves until they get this close.</summary>
    const double FineTickPx = 12;

    /// <summary>Px under which a fixed grid's lines are too close together to draw.</summary>
    const double MinGridPx = 6;
    /// <summary>The grid's fixed spacing in beats (a value, or the magnet's when linked), 0 when it follows the zoom, -1 for the bars alone.</summary>
    double FixedGridBeats
    {
        get
        {
            string mode = gridMode == "snap" ? snapMode : gridMode;
            return mode switch { "auto" or "line" or "off" => 0, "bar" => -1, _ => NoteBeats(mode) };
        }
    }

    /// <summary>
    /// The spacing of the lines drawn at this zoom in beats, what Auto snaps to (0 = the bars): a fixed grid's own or
    /// the finest tick, doubling up to whole bars while they are too close together to draw (a step that doesn't fit the
    /// bar goes straight to the bar). Measured at the first bar in view: with a tempo map the bars change length.
    /// </summary>
    double LineBeats
    {
        get
        {
            int b = Math.Max(0, tempo.BarAt(tempo.BeatAt(Math.Max(0, Sec(HeaderW)))));
            var (num, den) = tempo.MeterAt(b);
            double unit = 4.0 / den, len = num * unit, beatPx = pps * tempo.BeatSecAt(tempo.BarStartBeat(b));
            double g = FixedGridBeats;
            if (g < 0 || !(beatPx > 0)) return 0;
            double step = g >= unit ? g : FineBeats(unit, beatPx);
            while (step < len - 1e-9 && step * beatPx < MinGridPx) step = ClipEditing.FitsBar(step, len) ? step * 2 : len;
            return step >= len - 1e-9 ? 0 : step;
        }
    }

    /// <summary>Bars mode, one label per bar: the finest minor tick in beats (the meter's unit, or a half of it, a half of that…).</summary>
    double FineBeats(double unit, double beatPx)
    {
        double g = FixedGridBeats;
        if (g > 0) return g < unit && g * beatPx >= MinGridPx ? g : unit;
        double tick = unit;
        while (tick / 2 * beatPx >= FineTickPx * 2 && tick > 1e-4) tick /= 2;
        return tick;
    }

    /// <summary>The time ruler's step at this zoom: the first of <see cref="TimeSteps"/> at least 64 px apart, else the
    /// last. A plain loop: Array.FindIndex with a lambda on pps was a new closure on every frame.</summary>
    int TimeStepIndex()
    {
        for (int i = 0; i < TimeSteps.Length; i++)
            if (TimeSteps[i] * pps >= 64) return i;
        return TimeSteps.Length - 1;
    }

    /// <summary>The time ruler's label styles (<see cref="RulerText"/>): a minor tick's, a major one under a second apart, and a coarser major one.</summary>
    const int MinorLabel = 0, MajorFineLabel = 1, MajorLabel = 2;
    /// <summary>The time ruler's labels by tenth of a millisecond and style (also what absorbs the drift of adding the step up).</summary>
    readonly Dictionary<(long Tenths, int Style), string> rulerTexts = [];

    /// <summary>A time ruler label, made once per time and style: the ruler is drawn every frame while audio runs.</summary>
    string RulerText(double sec, int style)
    {
        long key = (long)Math.Round(sec * 1e4);
        if (rulerTexts.TryGetValue((key, style), out var t)) return t;
        if (rulerTexts.Count >= DrawnTextMax) rulerTexts.Clear();
        double s = key / 1e4;
        t = style switch
        {
            MinorLabel => s >= 60 ? $"{(int)(s / 60)}:{s % 60:00.####}" : $"{s:0.####}",
            MajorFineLabel => s >= 60 ? $"{(int)(s / 60)}:{s % 60:00.###}" : $"{s:0.###}s",
            _ => s >= 60 ? $"{(int)(s / 60)}:{s % 60:00}" : $"{s:0.###}s",
        };
        return rulerTexts[(key, style)] = t;
    }

    /// <summary>Whole numbers as text from 0 up (bar and beat numbers in the ruler), each made the first time it shows.</summary>
    static readonly List<string> numberTexts = [];

    static string NumberText(int n)
    {
        if (n < 0) return n.ToString(); // never: bars and beats count from 1
        while (numberTexts.Count <= n) numberTexts.Add(numberTexts.Count.ToString());
        return numberTexts[n];
    }

    void DrawRuler(DrawingContext ctx, double W)
    {
        // the ruler's numbers sit under the marker strip
        const double rulerH = RulerNumbersH;
        using var _ = ctx.PushTransform(Matrix.CreateTranslation(0, StripsH));
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, 0, W - HeaderW, rulerH));
        if (!bars)
        {
            int si = TimeStepIndex();
            double seconds = TimeSteps[si];
            int div = TimeDivs[si];
            var minorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7));
            for (double s = Math.Max(0, Math.Floor(Sec(HeaderW) / seconds) * seconds); X(s) <= W; s += seconds)
            {
                double x = X(s);
                if (seconds * pps / div >= 6)
                    for (int k = 1; k < div; k++)
                    {
                        double mx = X(s + k * seconds / div);
                        ctx.DrawLine(minorPen, new Point(mx, rulerH - 6), new Point(mx, rulerH));
                        if (seconds * pps / div >= MinorLabelPx)
                        {
                            ctx.DrawText(RulerText(s + k * seconds / div, MinorLabel), 10, Palette.Faint, new Point(mx + 3, 17), Palette.Mono);
                        }
                    }
                ctx.DrawLine(Palette.Pen(Palette.Line), new Point(x, 3), new Point(x, rulerH));
                ctx.DrawText(RulerText(s, seconds < 1 ? MajorFineLabel : MajorLabel), 15, Palette.Fg, new Point(x + 4, 3), Palette.MonoBold);
            }
            return;
        }
        var tick = Palette.Pen(Palette.Line);
        var beatPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7));
        for (int k = 0; k < rulerTicks.Count; k++)
        {
            var t = rulerTicks[k];
            double x = t.X;
            switch (t.Kind)
            {
                case TickBar:
                    ctx.DrawLine(tick, new Point(x, 3), new Point(x, rulerH));
                    ctx.DrawText(NumberText(t.Bar + 1), 15, Palette.Fg, new Point(x + 4, 3), Palette.MonoBold);
                    break;
                case TickMinorBar or TickSub:
                    ctx.DrawLine(beatPen, new Point(x, rulerH - 6), new Point(x, rulerH));
                    if (t.Label) ctx.DrawText(NumberText(t.Kind == TickSub ? t.Unit + 1 : t.Bar + 1), 10, Palette.Faint, new Point(x + 3, 17), Palette.Mono);
                    break;
                case TickFine:
                    ctx.DrawLine(beatPen, new Point(x, rulerH - 5), new Point(x, rulerH));
                    break;
                default:
                    ctx.DrawLine(beatPen, new Point(x, rulerH - 9), new Point(x, rulerH));
                    ctx.DrawText(NumberText(t.Unit + 1), 10, Palette.Faint, new Point(x + 3, 17), Palette.Mono);
                    break;
            }
        }
    }

    /// <summary>The kinds of <see cref="RulerTick"/>: a labelled bar, a bar between labels, a unit or grid step, a fine tick, a fine tick on a unit (labelled with the unit's number only, #255).</summary>
    const byte TickBar = 0, TickMinorBar = 1, TickSub = 2, TickFine = 3, TickFineUnit = 4;

    /// <summary>A tick of the bars ruler: where, what kind, its bar and its unit in the bar (from 0), and whether it carries its number.</summary>
    readonly record struct RulerTick(double X, byte Kind, int Bar, int Unit, bool Label);

    /// <summary>The bars ruler's ticks in view, built with <see cref="grid"/> (one pass over the visible bars of the tempo map) and drawn by <see cref="DrawRuler"/>.</summary>
    readonly List<RulerTick> rulerTicks = [];

    // x of every ruler tick in view (major = labelled step, minor = beat / subdivision), rebuilt each Render so the
    // lanes can draw the same ticks as faint vertical lines behind their clips without allocating per lane
    readonly List<(double X, bool Major)> grid = [];

    /// <summary>Collects the ruler's tick positions (same steps and thresholds as <see cref="DrawRuler"/>) into <see cref="grid"/>.</summary>
    void BuildGrid(double W)
    {
        grid.Clear();
        rulerTicks.Clear();
        // a zoom that isn't a positive number would make the loops below run forever
        if (!(pps > 0) || !double.IsFinite(pps)) return;
        if (!bars)
        {
            int si = TimeStepIndex();
            double seconds = TimeSteps[si];
            int div = TimeDivs[si];
            for (double s = Math.Max(0, Math.Floor(Sec(HeaderW) / seconds) * seconds); X(s) <= W; s += seconds)
            {
                grid.Add((X(s), true));
                if (seconds * pps / div >= 6)
                    for (int k = 1; k < div; k++) grid.Add((X(s + k * seconds / div), false));
            }
            return;
        }
        BuildBars(W);
    }

    /// <summary>
    /// Bars mode: the ticks of every bar in view, from the tempo map (each bar its own length and meter). Bars get a
    /// number, or every few when they get close; inside a labelled bar, the meter's units (eighths in 6/8) or the
    /// grid's steps, and zoomed in past the units their halves, quarters…, the units labelled with their number only. Only the
    /// visible bars are visited.
    /// </summary>
    void BuildBars(double W)
    {
        double sec0 = Math.Max(0, Sec(HeaderW));
        int first = Math.Max(0, tempo.BarAt(tempo.BeatAt(sec0)));
        double firstPx = (tempo.BarStartSec(first + 1) - tempo.BarStartSec(first)) * pps;
        if (!(firstPx > 0)) return;
        int step = 1;
        while (step * firstPx < 56 && step < 1 << 24) step *= 2;
        // bars between the labels get a tick while they are apart enough; else only the labelled ones are visited
        bool every = firstPx >= 6;
        double g = FixedGridBeats;
        for (int b = every ? first : first - first % step; ; b += every ? 1 : step)
        {
            double bs = tempo.BarStartBeat(b), x = X(tempo.SecAt(bs));
            if (x > W) break;
            bool labelled = b % step == 0;
            rulerTicks.Add(new(x, labelled ? TickBar : TickMinorBar, b, 0, !labelled && firstPx >= MinorLabelPx));
            grid.Add((x, labelled));
            if (step != 1 || g < 0) continue;
            var (num, den) = tempo.MeterAt(b);
            double unit = 4.0 / den, len = num * unit, beatPx = pps * tempo.BeatSecAt(bs);
            double fine = FineBeats(unit, beatPx);
            if (fine < unit - 1e-9)
            {
                int n = (int)Math.Ceiling(len / fine - 1e-6);
                for (int k = 1; k < n; k++)
                {
                    double at = k * fine, tx = X(tempo.SecAt(bs + at));
                    if (tx > W) break;
                    double units = at / unit;
                    bool onUnit = Math.Abs(units - Math.Round(units)) < 1e-6;
                    rulerTicks.Add(new(tx, onUnit ? TickFineUnit : TickFine, b, (int)Math.Round(units), onUnit));
                    grid.Add((tx, false));
                }
                continue;
            }
            double sub = g >= unit ? g : unit;
            if (sub * beatPx < 6) continue;
            int subN = (int)Math.Ceiling(len / sub - 1e-6);
            for (int k = 1; k < subN; k++)
            {
                double at = k * sub, tx = X(tempo.SecAt(bs + at));
                if (tx > W) break;
                double units = at / unit;
                bool onUnit = Math.Abs(units - Math.Round(units)) < 1e-6;
                rulerTicks.Add(new(tx, TickSub, b, (int)Math.Round(units), onUnit && sub * beatPx >= MinorLabelPx));
                grid.Add((tx, false));
            }
        }
    }

    void DrawLane(DrawingContext ctx, int i, double W)
    {
        var t = tracks[i];
        double y = LaneY(i), lh = LaneH(i);
        if (y + lh < RulerH || y > Bounds.Height) return;
        bool sel = i == Selected;
        // the lifted lane is opaque so the lanes it passes over don't show through
        if (t.Track.Id == liftedId || liftedAlso.Contains(t.Track.Id)) ctx.FillRectangle(Palette.App, new Rect(HeaderW, y, W - HeaderW, lh));
        var tint = TrackIcons.ColorOf(t.Track);
        TrackColorLooks.DrawLane(ctx, tint, new Rect(HeaderW, y, W - HeaderW, lh));
        if (Picked(i))
        {
            ctx.FillRectangle(Palette.Alpha(Palette.RaisedColor, 0.6), new Rect(HeaderW, y, W - HeaderW, lh));
            ctx.FillRectangle(Palette.Alpha(TrackColorLooks.SelectionColor(tint), SelectedTint), new Rect(HeaderW, y, W - HeaderW, lh));
        }
        if (armed.Contains(i) && !Recording) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.06), new Rect(HeaderW, y, W - HeaderW, lh));
        var majorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.55));
        var minorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.22));
        foreach (var (gx, major) in grid)
            if (gx >= HeaderW) ctx.DrawLine(major ? majorPen : minorPen, new Point(gx, y), new Point(gx, y + lh));
        // a frozen track (#240): its clips dimmed, under the wave of the render it plays, and their labels over it all
        var layer = t.Freeze != null ? ClipLayer.Body : ClipLayer.All;
        using (ctx.PushOpacity(t.Freeze != null ? FrozenDim : 1))
        {
            for (int k = 0; k < t.Clips.Count; k++) DrawClip(ctx, t, t.Clips[k], y, sel, W, layer);
            DrawLoopGhost(ctx, i, y, W);
            if (t.Track.IsMidi) DrawMidiClips(ctx, i, y, W, layer);
        }
        if (t.Freeze is { } frozen)
        {
            DrawFrozen(ctx, t, frozen, y, W);
            for (int k = 0; k < t.Clips.Count; k++) DrawClip(ctx, t, t.Clips[k], y, sel, W, ClipLayer.Labels);
            if (t.Track.IsMidi) DrawMidiClips(ctx, i, y, W, ClipLayer.Labels);
        }
        DrawJoins(ctx, i, W);
        if (Range is { } r && rangeTracks.Contains(t.Track.Id)) DrawRange(ctx, y, lh, W, r.Start, r.End);
        DrawSplitHover(ctx, i, y, lh);
        DrawRowBottom(ctx, t.Track.Id, HeaderW, W, y + lh);
    }

    /// <summary>How much a frozen track's clips show under its render's wave.</summary>
    const double FrozenDim = 0.35;

    /// <summary>A frozen track's render: its wave across the lane, in the track's color or the accent, with no box (it isn't a clip one can touch).</summary>
    void DrawFrozen(DrawingContext ctx, LiveTrack t, FrozenRender f, double y, double W)
    {
        var c = f.Lane;
        var w = Window(c, c.Clip);
        if (X(w.End) < HeaderW || X(w.Start) > W) return;
        double lh = RowH(t.Track);
        var color = TrackIcons.ColorOf(t.Track) ?? Palette.AccentColor;
        bool silent = t.Silenced || anySoloed && !t.Soloed;
        var (geo, line) = Waveform(c, c.Clip, w, y + 8, y + lh - 8, W);
        WaveLooks.Paint(WaveLooks.Current, ctx, geo, silent ? Palette.Faint : Palette.Alpha(color, 0.9), line);
        // each clip then draws its labels over it, the snowflake first (DrawFrozenChip)
    }

    void DrawClip(DrawingContext ctx, LiveTrack t, LiveClip c, double y, bool sel, double W, ClipLayer layer = ClipLayer.All)
    {
        bool body = (layer & ClipLayer.Body) != 0, labels = (layer & ClipLayer.Labels) != 0;
        var model = DisplayClip(c);
        double lh = RowH(t.Track);
        var w = Window(c, model);
        double x0 = X(w.Start), x1 = X(w.End);
        sel = sel && (SelectedClipId == null || c.Clip.Id == SelectedClipId) || boxed.Contains(c.Clip.Id);
        if (x1 < HeaderW || x0 > W) return;
        // while its loop is dragged the repeats are the ghost's (DrawLoopGhost)
        if (LoopDragged(c.Clip.LoopOf)) return;
        var clip = new Rect(x0, y + 5, Math.Max(2, x1 - x0), lh - 10);
        // a track with a color tints its clips with it (native only); without one they stay as in the web
        var tint = TrackIcons.ColorOf(t.Track);
        var color = c.Offline ? OfflineColor : tint ?? Palette.AccentColor;
        bool silent = t.Silenced || anySoloed && !t.Soloed;
        // the clip being carried to another track stays dimmed where it was until it is dropped
        bool carried = dragged == c && (overIndex >= 0 && overIndex != fromIndex && !swapping || toNewTrack);
        using var opacity = ctx.PushOpacity((!c.Offline && silent ? 0.5 : 1) * (carried ? 0.35 : 1) * (erasing.Contains(c.Clip.Id) ? ErasingOpacity : 1));
        // the web shows the filmstrip from SHOW_THUMBS_AT; audio-only media (native only) keeps the plain clip
        bool thumbs = showVideo && t.Track.Video && !c.Offline && lh >= ShowThumbsAt && c.VideoPath != null;
        // a colored track paints its clips in the look chosen in Apariencia (TrackColorLooks, #162)
        bool looked = tint != null && !c.Offline && !thumbs;
        var look = looked ? TrackColorLooks.Clip(color, sel) : default;
        var fill = c.Offline ? Palette.Alpha(color, silent ? 0.05 : sel ? 0.16 : 0.10) : thumbs ? null
            : looked ? look.Fill : sel ? Palette.Alpha(color, 0.16) : Palette.Alpha(Palette.HoverColor, 0.4);
        var border = looked ? look.Border : Palette.Pen(c.Offline ? Palette.Alpha(color, sel ? 0.9 : 0.45)
            : sel ? Palette.Alpha(color, tint != null ? 0.95 : 0.6) : tint != null ? Palette.Alpha(color, 0.45) : Palette.Line,
            (c.Offline || tint != null) && sel ? 1.5 : 1);
        if (body) ctx.DrawRectangle(fill, border, clip, 5, 5);
        // what is being worked on: the dark shade and its bar go with the clip, its pill with the labels
        float? working = null;
        string[]? workTexts = null;
        string? workGlyph = null;
        if (c.Offline) { }
        else if (ClipRenderProgress?.Invoke(t, c) is { } progress) (working, workTexts) = (progress, model.Stretch != 1 ? FittingTexts : TransposingTexts);
        else if (TempoProgress?.Invoke(c) is { } listened) (working, workTexts, workGlyph) = (listened, TempoTexts, TempoGlyph);
        else if (ScaleProgress?.Invoke(c) is { } scaled) (working, workTexts, workGlyph) = (scaled, ScaleTexts, ToMidiGlyph);
        else if (ToMidiProgress?.Invoke(c) is { } heard) (working, workTexts, workGlyph) = (heard, ToMidiTexts, ToMidiGlyph);
        else if (StemsProgress?.Invoke(c) is { } split) (working, workTexts) = (split, StemsTexts);
        var f = ClipEnvelope.FitFades(model.FadeInSec, model.FadeOutSec, w.End - w.Start);
        double fadeInX = x0 + f.FadeIn * pps, fadeOutX = x1 - f.FadeOut * pps;
        using (ctx.PushClip(new RoundedRect(clip, 5)))
        {
            if (body)
            {
                if (looked) TrackColorLooks.DrawBar(ctx, clip, color, sel);
                if (!c.Offline)
                {
                    if (thumbs)
                    {
                        DrawFilmstrip(ctx, c, model, clip, W);
                        // the wave sits on a dark gradient over the bottom 55 % of the clip
                        var shade = new Rect(clip.X, clip.Bottom - clip.Height * 0.55, clip.Width, clip.Height * 0.55);
                        ctx.DrawRectangle(ClipShade, null, shade);
                        ctx.DrawRectangle(null, border, clip.Deflate(0.5), 4.5, 4.5); // over the frames, inside the clip
                    }
                    double waveTop = thumbs ? clip.Bottom - clip.Height * 0.55 : y + 6 + (looked ? TrackColorLooks.Top(clip.Height) : 0), waveBottom = y + lh - 6;
                    // the wave is the track's color or the accent, at full strength over the frames (the shade dims it); untinted goes white there
                    var wave = silent ? Palette.Faint : looked ? look.Content : thumbs && tint == null && !sel ? Palette.Alpha(Colors.White, 0.85)
                        : Palette.Alpha(color, sel || thumbs ? 1 : 0.8);
                    // a split track shows a stereo clip's left over its right (asked by the user), each in its half
                    if (t.Track.SplitWave && c.Audio is { Mono: false } && (waveBottom - waveTop) / 2 >= SplitWaveMinH)
                    {
                        double split = Math.Round((waveTop + waveBottom) / 2) + 0.5;
                        var (left, leftLine) = Waveform(c, model, w, waveTop, split - 1.5, W, 0);
                        WaveLooks.Paint(WaveLooks.Current, ctx, left, wave, leftLine);
                        var (right, rightLine) = Waveform(c, model, w, split + 1.5, waveBottom, W, 1);
                        WaveLooks.Paint(WaveLooks.Current, ctx, right, wave, rightLine);
                        ctx.DrawLine(Palette.Pen(Palette.Alpha(color, 0.25)), new Point(Math.Max(x0, HeaderW), split), new Point(Math.Min(x1, W), split));
                    }
                    else
                    {
                        var (geo, line) = Waveform(c, model, w, waveTop, waveBottom, W);
                        WaveLooks.Paint(WaveLooks.Current, ctx, geo, wave, line);
                    }
                    if (working is { } done) DrawWorkingShade(ctx, clip, done);
                }
                void Fade(double a, double b, double edge)
                {
                    FadeShade(ctx, edge, a, b, clip.Top, clip.Bottom);
                    ctx.DrawLine(Palette.Pen(Palette.Accent, 1.5), new Point(a, clip.Top), new Point(b, clip.Bottom));
                }
                if (f.FadeIn > 0) Fade(fadeInX, x0, x0);
                if (f.FadeOut > 0) Fade(fadeOutX, x1, x1);
                bool editable = sel && Selected >= 0 && Selected < tracks.Count && tracks[Selected] == t && CurveEditable(Selected, c);
                if (!c.Offline && (editable || model.GainDb != 0 || model.GainCurve.Count > 0)) DrawCurve(ctx, c, model, w, clip, editable);
            }
            if (labels)
            {
                bool locked = ClipEditing.IsLocked(t.Track, c.Clip);
                if (sel || clip.Contains(lastPointer)) DrawEdgeHandles(ctx, c, clip, fadeInX, fadeOutX, locked);
                // every label asks the strip for its room, the most telling first; the name takes what is left (#240)
                var band = BandOf(clip, x0, x1, fadeInX, fadeOutX, W);
                if (t.Freeze != null && band.TakeLeft(FrozenChipW, out var frozen)) DrawFrozenChip(ctx, frozen);
                if (!c.Offline && band.TakeRight(MediaBadgeW, out var badge)) DrawMediaBadge(ctx, badge, c.VideoPath != null);
                DrawClipPresence(ctx, ref band, c.Clip.Id);
                // a repeat of a loop says so with the loop icon at its start, as while it was dragged (#253)
                if (model.LoopOf != null && band.TakeLeft(LoopGlyphW, out var loop)) DrawLoopGlyph(ctx, 10, Palette.Alpha(color, 1), new Point(loop.X, loop.Y - 1));
                if (locked && band.TakeRight(ClipTabW, out var lockTab)) DrawClipTab(ctx, lockTab, "");
                if (model.Reversed && band.TakeRight(ClipTabW, out var reversed)) DrawClipTab(ctx, reversed, "");
                if (model.Pitch != 0 && band.TakeRight(PitchTabW, out var pitch)) DrawPitchTab(ctx, pitch, model.Pitch);
                if (working is { } done && workTexts != null) DrawWorkingPill(ctx, ref band, done, workTexts, workGlyph);
                if (c.Clip.Id != editingNameId && (c.Offline ? c.Name : model.Name) is { } own) DrawClipName(ctx, clip, own, band.Left, band.Right, sel);
                if (c.Offline && clip.Height >= OfflineTextMinH)
                    ctx.DrawText(Loc.T("clip.offline"), 11, Palette.Faint, new Point(Math.Max(x0, HeaderW) + ClipLabelInset, clip.Top + ClipBand.Below + ClipBand.H + 2), Palette.Ui);
                if (w.AnchorCut > 0.0005)
                {
                    for (double sy = clip.Top - 6; sy < clip.Bottom; sy += 6)
                        ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(x0, sy + 6), new Point(x0 + 6, sy));
                    // along the bottom, where the strip isn't
                    if (clip.Width > 60 && clip.Height >= ClipBand.MinClipH + 20)
                    {
                        var label = Palette.Text(SecText(w.AnchorCut), 10.5, Palette.Accent, Palette.Mono);
                        ctx.DrawRectangle(Palette.Alpha(Colors.Black, 0.7), null,
                            new Rect(x0 + 10, clip.Bottom - 19, label.Width + 22, 16), 2, 2);
                        ctx.DrawText("", 10, Palette.Accent, new Point(x0 + 14, clip.Bottom - 17), Icons);
                        ctx.DrawText(label, new Point(x0 + 28, clip.Bottom - 18));
                    }
                }
            }
        }
    }

    /// <summary>
    /// A fade's dark triangle: its corner on the clip's <paramref name="edge"/> at the top, along the top to
    /// <paramref name="a"/> and down to <paramref name="b"/> at the bottom. <see cref="FadeTriangle"/> mapped there by an
    /// affine transform ((0,0) → edge, (1,0) → a, (0,1) → b), so nothing is built per frame.
    /// </summary>
    static void FadeShade(DrawingContext ctx, double edge, double a, double b, double top, double bottom)
    {
        var map = new Matrix(a - edge, 0, b - edge, bottom - top, edge, top);
        if (!map.HasInverse) return; // a fade or a clip of no size: nothing to shade
        using (ctx.PushTransform(map))
            ctx.DrawGeometry(Palette.Alpha(Colors.Black, 0.55), null, FadeTriangle);
    }

    /// <summary>
    /// A clip whose transpose is being made (asked by the user: you couldn't tell whether it worked), or whose tempo is
    /// being detected: the wave dims, a bar fills along its bottom and a label (<paramref name="texts"/>, by percent)
    /// says how far it is, after <paramref name="glyph"/> (a Fluent icon; null = the transpose's). It keeps sounding meanwhile.
    /// </summary>
    void DrawWorkingShade(DrawingContext ctx, Rect clip, float progress)
    {
        ctx.FillRectangle(Palette.Alpha(Colors.Black, 0.45), clip);
        var track = new Rect(clip.X, clip.Bottom - 4, clip.Width, 4);
        ctx.FillRectangle(Palette.Alpha(Colors.White, 0.08), track);
        ctx.FillRectangle(Palette.Accent, new Rect(track.X, track.Y, track.Width * Math.Clamp(progress, 0, 1), track.Height));
    }

    /// <summary>The label of <see cref="DrawWorkingShade"/>, from the strip: a short clip keeps only the bar.</summary>
    static void DrawWorkingPill(DrawingContext ctx, ref ClipBand band, float progress, string[] texts, string? glyph)
    {
        int pct = Math.Clamp((int)(progress * 100), 0, 100);
        var label = Palette.Text(texts[pct], 11, Palette.Fg, Palette.Ui);
        if (!band.TakeLeft(label.Width + 30, out var room)) return;
        var pill = room.Inflate(new Thickness(0, 2));
        ctx.DrawRectangle(Palette.Alpha(Colors.Black, 0.7), Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.5)), pill, 4, 4);
        if (glyph == null) FxIcon.Draw(ctx, FxIcon.Transpose, new Rect(pill.X + 7, pill.Center.Y - 6, 12, 12), Palette.Accent);
        else
        {
            var icon = Palette.Text(glyph, 12, Palette.Accent, Icons);
            ctx.DrawText(icon, new Point(pill.X + 7, pill.Center.Y - icon.Height / 2));
        }
        ctx.DrawText(label, new Point(pill.X + 24, pill.Center.Y - label.Height / 2));
    }

    // --- transitions (native only) ------------------------------------------------------------------

    /// <summary>More clips than this on one lane and its joins aren't looked for (their windows go on the stack).</summary>
    const int MaxJoinClips = 256;
    /// <summary>The join chip's side: a square big enough to see over the frames, like CapCut's.</summary>
    const double JoinChipSize = 20;

    /// <summary>Fills <paramref name="w"/> with the lane's clip windows as drawn (a drag shows where the clip goes).</summary>
    void JoinWindows(int lane, Span<ClipEnvelope.ClipWindow> w)
    {
        var t = tracks[lane];
        for (int k = 0; k < w.Length; k++) w[k] = Window(t.Clips[k]);
    }

    /// <summary>The out clip's index when clip <paramref name="k"/> starts where another ends, both have their media and the join offers a transition, or -1.</summary>
    int JoinOut(LiveTrack t, ReadOnlySpan<ClipEnvelope.ClipWindow> w, int k)
    {
        int o = ClipTransitions.FindOut(w, k);
        return o >= 0 && !t.Clips[k].Offline && !t.Clips[o].Offline && t.OffersTransition(t.Clips[o], t.Clips[k], showVideo) ? o : -1;
    }

    /// <summary>The chip of a join at <paramref name="x"/>: centered on the join, halfway down the clips.</summary>
    Rect JoinChip(int lane, double x) =>
        new(Math.Round(x - JoinChipSize / 2), Math.Round(LaneY(lane) + LaneH(lane) / 2 - JoinChipSize / 2), JoinChipSize, JoinChipSize);

    /// <summary>
    /// The joins between video clips of a track (native only, asked by the user): a square chip on each, centered on it
    /// halfway down the clips, so it shows over the frames. White with a bar (|) while it is a cut; of the accent
    /// with an X (crossfade: a fade out crossing a fade in, like the clips' fade lines) or a V (dip) when it has a transition,
    /// with arrows out of it to both ends of the stretch it covers (<see cref="DrawJoinSpan"/>). It sits over the edge handles there and wins their clicks. A click opens its menu.
    /// </summary>
    void DrawJoins(DrawingContext ctx, int lane, double W)
    {
        var t = tracks[lane];
        int n = t.Clips.Count;
        if (n < 2 || n > MaxJoinClips || LaneH(lane) < JoinChipSize + 12) return;
        Span<ClipEnvelope.ClipWindow> w = stackalloc ClipEnvelope.ClipWindow[n];
        JoinWindows(lane, w);
        for (int k = 0; k < n; k++)
        {
            if (JoinOut(t, w, k) < 0) continue;
            var model = DisplayClip(t.Clips[k]);
            var join = ClipTransitions.Find(w, k, ClipTransitions.Parse(model.Transition), model.TransitionSec);
            double x = X(w[k].Start);
            if (x < HeaderW || x > W) continue;
            var chip = JoinChip(lane, x);
            bool over = gesture == null && chip.Inflate(2).Contains(lastPointer);
            if (join is { } j) DrawJoinSpan(ctx, chip, Math.Max(HeaderW, X(j.Start)), X(j.End));
            var fill = join != null ? Palette.AccentColor : Palette.FgColor;
            ctx.DrawRectangle(Palette.Alpha(fill, over || join != null ? 1 : 0.9), Palette.Pen(Palette.Alpha(Colors.Black, 0.6)), chip.Deflate(0.5), 5, 5);
            var pen = Palette.Pen(Palette.Alpha(Palette.AppColor, 1), 1.5);
            double gx = chip.X + 5.5, gy = chip.Y + 6, gs = 8;
            switch (join?.Kind)
            {
                case TransitionKind.Crossfade:
                    ctx.DrawLine(pen, new Point(gx, gy), new Point(gx + gs, gy + gs));
                    ctx.DrawLine(pen, new Point(gx, gy + gs), new Point(gx + gs, gy));
                    break;
                case TransitionKind.Dip:
                    ctx.DrawLine(pen, new Point(gx, gy), new Point(gx + gs / 2, gy + gs));
                    ctx.DrawLine(pen, new Point(gx + gs / 2, gy + gs), new Point(gx + gs, gy));
                    break;
                default:
                    ctx.DrawLine(pen, new Point(gx + gs / 2, gy - 1), new Point(gx + gs / 2, gy + gs + 1));
                    break;
            }
        }
    }

    /// <summary>
    /// How long a transition lasts (asked by the user: a line along the clips' top barely showed): an accent line out
    /// of each side of the chip, at its height, to an arrowhead at that end, over a dark outline so it reads over the frames.
    /// </summary>
    static void DrawJoinSpan(DrawingContext ctx, Rect chip, double left, double right)
    {
        double y = chip.Center.Y, a = 4;
        var shade = Palette.Pen(Palette.Alpha(Colors.Black, 0.7), 3.5, PenLineCap.Round, PenLineJoin.Round);
        var pen = Palette.Pen(Palette.Accent, 1.5, PenLineCap.Round, PenLineJoin.Round);
        Arrows(shade);
        Arrows(pen);
        void Arrows(IPen p)
        {
            if (chip.Left - left > a + 2)
            {
                ctx.DrawLine(p, new Point(left, y), new Point(chip.Left, y));
                ctx.DrawLine(p, new Point(left, y), new Point(left + a, y - a));
                ctx.DrawLine(p, new Point(left, y), new Point(left + a, y + a));
            }
            if (right - chip.Right > a + 2)
            {
                ctx.DrawLine(p, new Point(chip.Right, y), new Point(right, y));
                ctx.DrawLine(p, new Point(right, y), new Point(right - a, y - a));
                ctx.DrawLine(p, new Point(right, y), new Point(right - a, y + a));
            }
        }
    }

    /// <summary>The clip coming in at the join chip under <paramref name="p"/>, or null.</summary>
    LiveClip? JoinChipAt(int lane, Point p)
    {
        if (lane < 0 || lane >= tracks.Count || p.X < HeaderW) return null;
        var t = tracks[lane];
        int n = t.Clips.Count;
        if (n < 2 || n > MaxJoinClips || LaneH(lane) < JoinChipSize + 12) return null;
        Span<ClipEnvelope.ClipWindow> w = stackalloc ClipEnvelope.ClipWindow[n];
        JoinWindows(lane, w);
        for (int k = 0; k < n; k++)
            if (JoinOut(t, w, k) >= 0 && JoinChip(lane, X(w[k].Start)).Inflate(2).Contains(p)) return t.Clips[k];
        return null;
    }

    /// <summary>
    /// The badge in a clip's top right corner (native only): a camera when its media has video, a note when it is
    /// audio only, so you can tell them apart without opening anything. It sits left of the edge handles and only
    /// says something, with no tooltip: over a clip it would cover what you are about to grab.
    /// </summary>
    const double MediaBadgeW = 18;

    void DrawMediaBadge(DrawingContext ctx, Rect badge, bool video)
    {
        bool over = badge.Contains(lastPointer);
        ctx.DrawRectangle(Palette.Alpha(Colors.Black, over ? 0.75 : 0.55), null, badge, 4, 4);
        ctx.DrawText(video ? "" : "", 10, over ? Palette.Fg : Palette.Dim, new Point(badge.X + 4, badge.Y + 3), Icons);
    }

    /// <summary>"Transponiendo… 0%" to "100%", and the knob's bare "0%" to "100%", made once (nothing is built per frame).</summary>
    static readonly string[] TransposingTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.transposing", p))],
        FittingTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.fitting", p))],
        PercentTexts = [.. Enumerable.Range(0, 101).Select(p => $"{p}%")],
        TempoTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.tempo", p))],
        ScaleTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.scale", p))],
        ToMidiTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.toMidi", p))];
    /// <summary>"Separando…" per percent, built once: the timeline draws it every frame while it works.</summary>
    static readonly string[] StemsTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.stems", p))];
    /// <summary>The music note (Fluent's MusicNote) "Extraer MIDI" uses in the clip menu and while it listens.</summary>
    public const string ToMidiGlyph = "";
    /// <summary>The metronome-ish note "Detectar tempo" uses in the clip menu.</summary>
    public const string TempoGlyph = "";

    /// <summary>
    /// The clip's edge handles (native only): on each side a small tab split at the clip's middle. The top one sits
    /// where the fade ends and drags the fade; the bottom one sits on the edge and trims. The one under the pointer
    /// (or being dragged) lights up, so the two gestures never look alike.
    /// </summary>
    void DrawEdgeHandles(DrawingContext ctx, LiveClip c, Rect clip, double fadeInX, double fadeOutX, bool locked)
    {
        double mid = clip.Center.Y, quarter = clip.Height / 4;
        var lit = dragged == c ? dragMode : gesture == null && clip.Contains(lastPointer) ? HitHandle(c, lastPointer) : ClipEditMode.Move;
        // a stretch grabs the same tab as a trim
        lit = lit switch { ClipEditMode.StretchStart => ClipEditMode.TrimStart, ClipEditMode.StretchEnd => ClipEditMode.TrimEnd, _ => lit };
        // with Ctrl the trim tabs stretch: they say so before the press
        bool stretching = dragged == c ? dragMode is ClipEditMode.StretchStart or ClipEditMode.StretchEnd : tool == TimelineTool.Select && (modifiers & KeyModifiers.Control) != 0;
        // centered in its half; the one you would grab lights up
        void Tick(double x, double cy, ClipEditMode mode, Color color) => DrawGripTab(ctx, x, cy, Math.Round(Math.Min(16, quarter * 2 - 4)), lit == mode, color);
        Tick(Math.Max(clip.Left + 5, fadeInX), mid - quarter, ClipEditMode.FadeIn, Palette.AccentColor);
        Tick(Math.Min(clip.Right - 5, fadeOutX), mid - quarter, ClipEditMode.FadeOut, Palette.AccentColor);
        // a locked clip keeps its length: no trim tabs and no loop
        if (locked) return;
        DrawLoopCorner(ctx, clip, lit == ClipEditMode.Loop);
        if (stretching)
        {
            StretchTick(ctx, clip.Left + 8, mid + quarter, Math.Round(Math.Min(16, quarter * 2 - 4)), lit == ClipEditMode.TrimStart);
            StretchTick(ctx, clip.Right - 8, mid + quarter, Math.Round(Math.Min(16, quarter * 2 - 4)), lit == ClipEditMode.TrimEnd);
            return;
        }
        Tick(clip.Left + 5, mid + quarter, ClipEditMode.TrimStart, Palette.FgColor);
        Tick(clip.Right - 5, mid + quarter, ClipEditMode.TrimEnd, Palette.FgColor);
    }

    /// <summary>
    /// An edge handle: a small dark tab with two short lines, like the column's grip, <paramref name="h"/> px high and
    /// centered on (<paramref name="x"/>, <paramref name="cy"/>); lit while it would be grabbed. Audio clips, MIDI clips
    /// and automation blocks share it (asked by the user, #171), so a trim looks the same everywhere.
    /// </summary>
    static void DrawGripTab(DrawingContext ctx, double x, double cy, double h, bool on, Color color)
    {
        if (h < 8) return;
        var tab = new Rect(Math.Round(x) - 3.5, Math.Round(cy - h / 2) + 0.5, 7, h);
        ctx.DrawRectangle(Palette.Alpha(on ? Palette.HoverColor : Palette.AppColor, 0.85), Palette.Pen(on ? Palette.Alpha(color, 0.7) : Palette.Line), tab, 2, 2);
        var line = Palette.Alpha(color, on ? 1 : 0.6);
        ctx.FillRectangle(line, new Rect(tab.X + 1.5, tab.Y + 2.5, 1, h - 5));
        ctx.FillRectangle(line, new Rect(tab.X + 3.5, tab.Y + 2.5, 1, h - 5));
    }

    /// <summary>A trim tab while Ctrl is down: wider, in the accent, with two arrows pointing out, so a stretch never looks like a trim.</summary>
    static void StretchTick(DrawingContext ctx, double x, double cy, double h, bool on)
    {
        if (h < 8) return;
        var tab = new Rect(Math.Round(x) - 6.5, Math.Round(cy - h / 2) + 0.5, 13, h);
        ctx.DrawRectangle(Palette.Alpha(on ? Palette.HoverColor : Palette.AppColor, 0.85), Palette.Pen(Palette.Alpha(Palette.AccentColor, on ? 1 : 0.7)), tab, 2, 2);
        var pen = Palette.Pen(Palette.Alpha(Palette.AccentColor, on ? 1 : 0.8));
        double mid = tab.Center.Y;
        ctx.DrawLine(pen, new Point(tab.X + 5, mid - 2.5), new Point(tab.X + 2.5, mid));
        ctx.DrawLine(pen, new Point(tab.X + 2.5, mid), new Point(tab.X + 5, mid + 2.5));
        ctx.DrawLine(pen, new Point(tab.Right - 5, mid - 2.5), new Point(tab.Right - 2.5, mid));
        ctx.DrawLine(pen, new Point(tab.Right - 2.5, mid), new Point(tab.Right - 5, mid + 2.5));
    }

    /// <summary>How fast against its recording the clip being stretched plays, on a dark tab beside the pointer.</summary>
    void DrawStretchAmount(DrawingContext ctx)
    {
        if (gesture != "clip" || !moved || stretchText == null) return;
        var t = Palette.Text(stretchText, 11, Palette.Fg, Palette.Mono);
        var tab = new Rect(lastPointer.X + 12, lastPointer.Y - t.Height - 10, t.Width + 12, t.Height + 6);
        ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.85), Palette.Pen(Palette.Line), tab, 4, 4);
        t.Draw(ctx, new Point(tab.X + 6, tab.Y + 3));
    }

    /// <summary>Lowest clip that also shows "Sin conexión" under its name.</summary>
    const double OfflineTextMinH = 40;
    /// <summary>Narrowest stretch of a clip that still shows its name.</summary>
    const double ClipNameMinW = 24;
    /// <summary>Narrowest box for a clip's name, and how tall it is.</summary>
    const double ClipEditorW = 160, ClipEditorH = 22;

    /// <summary>
    /// A clip's own name (asked by the user) at its top left, audio or MIDI alike (#253), with a faint dark halo so it
    /// reads over the wave or the notes, cut at <paramref name="right"/> (where the tabs or the next repeat begin).
    /// </summary>
    static void DrawClipName(DrawingContext ctx, Rect clip, string name, double left, double right, bool sel)
    {
        if (clip.Height < 24 || right - left < ClipNameMinW) return;
        // no box: the faint dark halo of the loop icon keeps it legible over the clip (#253)
        var text = Palette.Text(name, 11, sel ? Palette.Fg : Palette.Dim, Palette.UiBold);
        DrawHaloText(ctx, name, 11, sel ? Palette.Fg : Palette.Dim, new Point(Math.Round(left) + 1, Math.Round(clip.Top + 12 - text.Height / 2)),
            Palette.UiBold, right - left - 1);
    }

    /// <summary>Where a clip's in-place name box goes: along its top, from where it shows; null when it is not on screen.</summary>
    Rect? ClipNameRectOf(string id)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            double start, end;
            if (tracks[i].Clips.FirstOrDefault(c => c.Clip.Id == id) is { } clip)
            {
                var w = Window(clip);
                (start, end) = (w.Start, w.End);
            }
            else if (tracks[i].Track.MidiClips?.Find(c => c.Id == id) is { } midi) (start, end) = MidiSpan(midi);
            else continue;
            if (!rows.Any(r => r.Folder == null && r.Lane == null && r.Track == i)) return null;
            double x0 = Math.Max(X(start), HeaderW), x1 = Math.Min(X(end), Bounds.Width);
            if (x1 - x0 < 8) return null;
            double x = Math.Min(x0, Bounds.Width - ClipEditorW);
            return new Rect(x, LaneY(i) + 7, Math.Max(ClipEditorW, x1 - x - 4), ClipEditorH);
        }
        return null;
    }

    /// <summary>Widths of a clip's icon tab (lock, reversed) and of its pitch tab, in its label strip.</summary>
    const double ClipTabW = 17, PitchTabW = 27;

    /// <summary>An icon tab of a clip (a padlock when locked, an arrow when reversed), on a dark tab so it reads over the frames.</summary>
    static void DrawClipTab(DrawingContext ctx, Rect tab, string glyph)
    {
        ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, tab, 4, 4);
        ctx.DrawText(glyph, 10, Palette.Dim, new Point(tab.X + 3.5, tab.Y + 3), Icons);
    }

    /// <summary>A clip's own pitch ("+2"), beside its other tabs: the track's Transponer has its tag in the header.</summary>
    static void DrawPitchTab(DrawingContext ctx, Rect tab, double pitch)
    {
        ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, tab, 4, 4);
        var t = Palette.Text(TransposeTags[Math.Clamp((int)Math.Round(pitch), -24, 24) + 24], 10, Palette.Accent, Palette.Mono);
        t.Draw(ctx, new Point(tab.Center.X - t.Width / 2, tab.Center.Y - t.Height / 2));
    }

    /// <summary>
    /// The web's Filmstrip: the whole source (trims only hide its ends) split into as many 16:9 cells as fit at
    /// the lane's height, each showing the cached frame nearest its middle, cover-fitted and mirrored like the clip.
    /// </summary>
    void DrawFilmstrip(DrawingContext ctx, LiveClip c, Clip model, Rect clip, double W)
    {
        if (VideoThumbnails.Get(c.VideoPath!, c.MediaLengthSec) is not { Length: > 0 } frames) return;
        double x0 = X(model.AtSec), srcW = c.MediaLengthSec * model.Stretch * pps;
        double h = clip.Height - 2;
        int slots = Math.Max(1, (int)Math.Round(srcW / (h * 16 / 9)));
        double cell = srcW / slots;
        double left = Math.Max(clip.Left, HeaderW), right = Math.Min(clip.Right, W);
        int first = Math.Max(0, (int)Math.Floor((left - x0) / cell)), last = Math.Min(slots - 1, (int)Math.Floor((right - x0) / cell));
        var divider = Palette.Pen(Palette.Alpha(Colors.Black, 0.4)); // border-black/40
        for (int i = first; i <= last; i++)
        {
            var img = frames[Math.Min(frames.Length - 1, (int)Math.Floor((i + 0.5) / slots * frames.Length))];
            var dest = new Rect(x0 + i * cell, clip.Top, cell, clip.Height);
            double iw = img.Size.Width, ih = img.Size.Height;
            double scale = Math.Max(dest.Width / iw, dest.Height / ih); // object-cover
            double sw = dest.Width / scale, sh = dest.Height / scale;
            var src = new Rect((iw - sw) / 2, (ih - sh) / 2, sw, sh);
            if (model.Framing.Mirror)
            {
                using (ctx.PushTransform(Matrix.CreateTranslation(-dest.Center.X, 0) * Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(dest.Center.X, 0)))
                    ctx.DrawImage(img, src, dest);
            }
            else ctx.DrawImage(img, src, dest);
            if (i < slots - 1) ctx.DrawLine(divider, new Point(dest.Right - 0.5, dest.Top), new Point(dest.Right - 0.5, dest.Bottom));
        }
    }

    /// <summary>
    /// Min/max per pixel column with the clip gain and its curve applied, so the wave shows what is heard; cached
    /// until the zoom, scroll, size or gain changes (every curve edit assigns a new array, see <see cref="LiveClip.Curve"/>).
    /// </summary>
    (StreamGeometry Geo, bool Line) Waveform(LiveClip c, Clip model, ClipEnvelope.ClipWindow w, double top, double bottom, double W, int side = MixSide)
    {
        var a = c.Audio!;
        var pts = CurveOf(c, model);
        var key = new WaveKey(pps, scrollX, W, top, bottom, model.AtSec, model.TrimStartSec, model.TrimEndSec, model.Stretch, model.Reversed);
        if (waveCache.TryGetValue((c, side), out var hit) && hit.Key == key && hit.Curve == pts && hit.GainDb == model.GainDb) return (hit.Geo, hit.Line);
        // a mono clip has no sides of its own: both halves of a split track would be the same wave
        var (peakMin, peakMax) = side == 0 && a.PeakMinL != null ? (a.PeakMinL, a.PeakMaxL!) : side == 1 && a.PeakMinR != null ? (a.PeakMinR, a.PeakMaxR!) : (a.PeakMin, a.PeakMax);
        double clipGain = Math.Pow(10, model.GainDb / 20);
        var geo = new StreamGeometry();
        string look = WaveLooks.Current;
        double binsPerSec = a.SampleRate / (double)MediaAudio.PeakBin;
        int xa = (int)Math.Max(HeaderW, X(w.Start));
        int xb = (int)Math.Min(W, X(w.End));
        int n = Math.Max(0, xb - xa + 1);
        if (waveHi.Length < n) { waveHi = new double[n + 256]; waveLo = new double[n + 256]; }
        double lo = model.MediaSec(model.TrimStartSec) * binsPerSec, hi = (a.LengthSec - model.MediaSec(model.TrimEndSec)) * binsPerSec;
        double perPx = model.MediaSec(a.SampleRate / pps);
        using (var g = geo.Open())
        {
            // zoomed in past the peaks (native only): the samples themselves, as min/max per column or, closer
            // than a sample per pixel, a line through them
            if (perPx < MediaAudio.PeakBin)
            {
                bool line = perPx < 1.5;
                SampleWave(g, look, a, model, w, pts, clipGain, xa, xb, top, bottom, line, a.Mono ? MixSide : side);
                waveCache[(c, side)] = (geo, key, pts, model.GainDb, line);
                return (geo, line);
            }
            for (int px = xa; px < xb; px++)
            {
                double s0 = model.MediaSec(Sec(px) - model.AtSec) * binsPerSec;
                double s1 = model.MediaSec(Sec(px + 1) - model.AtSec) * binsPerSec;
                int b0 = (int)Math.Max(lo, s0), b1 = (int)Math.Min(hi, Math.Max(s0 + 1, s1));
                // the peaks are the recording's: a reversed clip reads them from the other end
                if (model.Reversed) (b0, b1) = (peakMax.Length - b1, peakMax.Length - b0);
                float mn = 0, mx = 0;
                for (int b = Math.Max(0, b0); b < b1 && b < peakMax.Length; b++)
                {
                    if (peakMin[b] < mn) mn = peakMin[b];
                    if (peakMax[b] > mx) mx = peakMax[b];
                }
                double gain = clipGain * ClipGainCurve.GainAt(pts, Sec(px + 0.5) - w.Origin);
                waveHi[px - xa] = Math.Min(1, mx * gain);
                waveLo[px - xa] = Math.Max(-1, mn * gain);
            }
            WaveLooks.Emit(look, g, xa, Math.Max(0, xb - xa), waveHi, waveLo, top, bottom, X(w.Start));
        }
        waveCache[(c, side)] = (geo, key, pts, model.GainDb, false);
        return (geo, false);
    }

    /// <summary>The side <see cref="Waveform"/> draws when it shows both channels in one wave.</summary>
    const int MixSide = -1;
    /// <summary>A split track's clip draws two waves only if each gets at least this many px; shorter, one wave of both.</summary>
    const double SplitWaveMinH = 14;

    /// <summary>The wave from the samples (both channels mixed, like the peaks, or one side of a split track) for the deepest zoom levels.</summary>
    void SampleWave(StreamGeometryContext g, string look, MediaAudio a, Clip model, ClipEnvelope.ClipWindow w, ClipGainCurve.Point[] pts,
        double clipGain, int xa, int xb, double top, double bottom, bool line, int side)
    {
        float At(long i) => side == 0 ? a.L[i] : side == 1 ? a.R[i] : 0.5f * (a.L[i] + a.R[i]);
        double sr = a.SampleRate;
        long lo = (long)(model.MediaSec(model.TrimStartSec) * sr), hi = Math.Min(a.L.Length, (long)((a.LengthSec - model.MediaSec(model.TrimEndSec)) * sr));
        double Y(double v, double gain) => WaveLooks.LineY(look, Math.Clamp(v * gain, -1, 1), top, bottom);
        // what the clip plays at a sample of its own: a reversed one reads the recording from the other end
        long Src(long i) => model.Reversed ? a.L.Length - 1 - i : i;
        if (line)
        {
            bool open = false;
            for (int px = xa; px <= xb; px++)
            {
                double s = model.MediaSec(Sec(px + 0.5) - model.AtSec) * sr;
                long i0 = (long)Math.Floor(s);
                if (i0 < lo || i0 + 1 >= hi) continue;
                double f = s - i0;
                long at = Src(i0), next = Src(i0 + 1);
                double v = At(at) * (1 - f) + At(next) * f;
                var pt = new Point(px + 0.5, Y(v, clipGain * ClipGainCurve.GainAt(pts, Sec(px + 0.5) - w.Origin)));
                if (!open) { g.BeginFigure(pt, false); open = true; }
                else g.LineTo(pt);
            }
            if (open) g.EndFigure(false);
            return;
        }
        for (int px = xa; px < xb; px++)
        {
            long s0 = Math.Max(lo, (long)(model.MediaSec(Sec(px) - model.AtSec) * sr));
            long s1 = Math.Min(hi, Math.Max(s0 + 1, (long)Math.Ceiling(model.MediaSec(Sec(px + 1) - model.AtSec) * sr)));
            if (s0 >= s1) { waveHi[px - xa] = waveLo[px - xa] = double.NaN; continue; }
            float mn = float.MaxValue, mx = float.MinValue;
            for (long i = s0; i < s1; i++)
            {
                long at = Src(i);
                float v = At(at);
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            double gain = clipGain * ClipGainCurve.GainAt(pts, Sec(px + 0.5) - w.Origin);
            waveHi[px - xa] = Math.Clamp(mx * gain, -1, 1);
            waveLo[px - xa] = Math.Clamp(mn * gain, -1, 1);
        }
        WaveLooks.Emit(look, g, xa, Math.Max(0, xb - xa), waveHi, waveLo, top, bottom, X(w.Start));
    }

    /// <summary>How far a track in a folder moves its handle, badge, name and sliders right, so what is inside shows at a glance.</summary>
    const double FolderIndent = 24;
    double Indent(int i) => RailFolder(i) != null ? Compact ? CompactIndent : FolderIndent : 0;
    /// <summary>
    /// The compact header has one layout per room, so its pieces never float at odd sizes. From <see cref="CompactRowAt"/>:
    /// a row, handle, badge, the name if there is room for <see cref="CompactNameMin"/>, ● M S against the meters, on the
    /// badge's center. Narrower, on a lane of <see cref="CompactUnderAt"/> or more, a strip: the badge and ● M S under it,
    /// centered. Narrower and shorter: M over S beside the badge, exactly as tall as it, the name between them if it fits,
    /// and no ●. <see cref="MinHeaderW"/> is what that last one needs for a track in a folder.
    /// </summary>
    const double CompactRowAt = 140, CompactUnderAt = 60;
    const double CompactBadgeX = 16, CompactIndent = 6, CompactMeterW = 12, CompactMeterGap = 3;
    /// <summary>The square buttons of a row, and M and S stacked beside the badge (twice this plus the gap is <see cref="MinBadge"/>).</summary>
    const double CompactBtnSize = 20, CompactBtnGap = 2, CompactStackW = 18, CompactStackH = 13, CompactStripBtnMax = 24;
    const double CompactNameMin = 30, CompactEditorW = 140;
    bool CompactRow => HeaderW >= CompactRowAt;
    bool CompactStrip(int i) => !CompactRow && LaneH(i) >= CompactUnderAt;
    double CompactRight => HeaderRight - CompactMeterW - CompactMeterGap;
    double CompactLeft(int i) => CompactBadgeX + Indent(i);
    /// <summary>A compact button by its place in ● M S; <see cref="NoRect"/> for the ● where it doesn't fit (the lane's
    /// red tint still says it is armed).</summary>
    Rect CompactBtn(int i, int k)
    {
        double y = LaneY(i);
        if (CompactRow)
        {
            double b = Math.Min(CompactBtnSize, LaneH(i) - 2 * SlimPad);
            return new(CompactRight - (3 - k) * (CompactBtnSize + CompactBtnGap) + CompactBtnGap + (CompactBtnSize - b) / 2, HeadY(i) + SlimAt / 2 - b / 2, b, b);
        }
        if (CompactStrip(i))
        {
            double left = CompactLeft(i), room = CompactRight - left, w = Math.Min(CompactStripBtnMax, Math.Floor((room - 2 * CompactBtnGap) / 3));
            double x = Math.Floor(left + (room - 3 * w - 2 * CompactBtnGap) / 2);
            return new(x + k * (w + CompactBtnGap), y + 38, w, CompactBtnSize);
        }
        // M over S as tall as the badge, which a slim lane shrinks
        double sh = Math.Min(CompactStackH, (BadgeSize(i) - CompactBtnGap) / 2);
        return k == 0 ? NoRect : new(CompactRight - CompactStackW, BadgeRect(i).Y + (k - 1) * (sh + CompactBtnGap), CompactStackW, sh);
    }
    double CompactNameW(int i) => CompactStrip(i) ? 0 : (CompactRow ? CompactBtn(i, 0).X : CompactRight - CompactStackW) - 6 - (BadgeRect(i).Right + 6);
    /// <summary>The badge at the narrowest full column; a wider one grows it (asked by the user) by <see cref="BadgeGrowth"/>
    /// px per px of column, up to <see cref="MaxBadge"/> and to what the lane's height leaves. The cap keeps it beside the
    /// name and volume rows: grown into the effect rows it pushed them around and looked off.</summary>
    const double MinBadge = 28, MaxBadge = BaseHeaderH - 12, BadgeGrowth = 0.25;
    double BadgeSize(int i) => LaneH(i) < SlimAt ? Math.Min(MinBadge, LaneH(i) - 2 * SlimPad)
        : Math.Max(MinBadge, Math.Min(Math.Min(MaxBadge, MinBadge + (HeaderW - DefaultHeaderW) * BadgeGrowth), LaneH(i) - 12));
    /// <summary>
    /// A lane under this height (px) takes the slim header (asked by the user, #168: lanes lower than the usual 40): the
    /// header's top row, laid out for 40 px, is centered on the lane (<see cref="HeadY"/>) and its badge and buttons
    /// shrink to the lane less <see cref="SlimPad"/> above and below (<see cref="HeadBtn"/>).
    /// </summary>
    const double SlimAt = 40, SlimPad = 4, HeadBtnSize = 24;
    /// <summary>Where a header's top row starts: the lane's top, or above it on a slim lane so the row stays centered.</summary>
    double HeadY(int i) => LaneY(i) + Math.Min(0, (LaneH(i) - SlimAt) / 2);
    /// <summary>A button of a header's top row at <paramref name="x"/>: <paramref name="w"/> by 24 px, or smaller and
    /// centered on a slim lane (a square one stays square).</summary>
    Rect HeadBtn(int i, double x, double w = HeadBtnSize)
    {
        double b = Math.Min(HeadBtnSize, LaneH(i) - 2 * SlimPad), cy = HeadY(i) + SlimAt / 2;
        return w == HeadBtnSize ? new(x + (w - b) / 2, cy - b / 2, b, b) : new(x, cy - b / 2, w, b);
    }
    /// <summary>How far a grown badge pushes the name and the volume right. A slim lane's smaller badge keeps the name
    /// where it is (#168: pulled left with the badge, it ran over it).</summary>
    double BadgeShift(int i) => Math.Max(0, BadgeSize(i) - MinBadge);
    /// <summary>The name and the volume also open a gap from a grown badge, which reaches down beside them: at the
    /// narrowest column the volume sits under the badge, so the gap grows with the first px of badge growth instead
    /// of jumping in.</summary>
    const double NameGap = 4, VolumeGap = 14;
    double NameShift(int i) => BadgeShift(i) + Math.Min(BadgeShift(i), NameGap);
    double VolumeShift(int i) => BadgeShift(i) + Math.Min(BadgeShift(i), VolumeGap);
    Rect BadgeRect(int i)
    {
        double s = BadgeSize(i);
        // a slim lane's smaller badge sits in the middle of the usual one's place, in line with the other tracks
        double x = !Compact ? 25 + Indent(i) + Math.Max(0, (MinBadge - s) / 2) : CompactStrip(i) ? Math.Floor((CompactLeft(i) + CompactRight - s) / 2) : CompactLeft(i);
        return new(x, HeadY(i) + SlimAt / 2 - Math.Min(s, MinBadge) / 2, s, s);
    }
    Rect ArmRect(int i) => Compact ? CompactBtn(i, 0) : HeadBtn(i, HeaderRight - 122);
    /// <summary>The input chip: wide enough for its longest label ("11-12") and the arrow side by side.</summary>
    Rect InputRect(int i) => Compact ? NoRect : HeadBtn(i, HeaderRight - 204, 50);
    /// <summary>The freeze toggle (#240, asked by the user): left of the ●, lit in the accent while the track is frozen.
    /// Not in a compact header (the track menu still has it).</summary>
    Rect FreezeRect(int i) => Compact ? NoRect : HeadBtn(i, HeaderRight - 150);
    Rect MuteRect(int i) => Compact ? CompactBtn(i, 1) : HeadBtn(i, HeaderRight - 94);
    Rect SoloRect(int i) => Compact ? CompactBtn(i, 2) : HeadBtn(i, HeaderRight - 66);
    Rect RemoveRect(int i) => Compact ? NoRect : HeadBtn(i, HeaderRight - 38);

    /// <summary>Where the "¿Quitar?" of a track opens when it wasn't its trash can that asked (Supr, a menu): under the can.</summary>
    public Point RemoveAskPoint(string trackId)
    {
        int i = -1;
        for (int k = 0; k < tracks.Count && i < 0; k++) if (tracks[k].Track.Id == trackId) i = k;
        if (i < 0) return new Point(8, RulerH + 8);
        return Compact ? new Point(8, LaneY(i) + 24) : RemoveRect(i).BottomLeft + new Point(0, 4);
    }
    /// <summary>Room for the name: an armed track shows its input left of the ●, so the name gets shorter.</summary>
    double NameW(int i) => Compact ? CompactNameW(i) : Math.Max(0, (i >= 0 && i < tracks.Count && tracks[i].Track.IsMidi && InstrumentRect(i).Width > 0 ? InstrumentRect(i).X - 62
        : armed.Contains(i) && !Recording && InputLabel != null ? InputRect(i).X - 62 : HeaderRight - 214) - Indent(i) - NameShift(i));
    double NameX(int i) => Compact ? BadgeRect(i).Right + 2 : 54 + Indent(i) + NameShift(i);
    /// <summary>Where a track's name sits (and its in-place editor goes, which a compact header lets run over the lane).</summary>
    Rect NameRect(int i) => new(NameX(i), HeadY(i) + 7, Compact ? Math.Max(CompactEditorW, NameW(i) + 8) : NameW(i) + 8, 26);
    /// <summary>Just the drawn name of a track (a double click there renames it), not the empty space after it.</summary>
    Rect TitleRect(int i)
    {
        if (Compact && NameW(i) < CompactNameMin) return NoRect;
        double w = Math.Min(Palette.Text(tracks[i].Track.Name, 12.5, Palette.Fg, Palette.UiBold).Width, NameW(i));
        return new(NameX(i), HeadY(i) + 7, Math.Max(20, w) + 8, 26);
    }
    /// <summary>The handle that drags a track, left of its badge.</summary>
    bool OnHandle(int i, Point p) => p.X < (Compact ? CompactBadgeX - 1 : 24) + Indent(i);
    /// <summary>The name rect of a track or a folder by id, or null when it is not on screen.</summary>
    public Rect? NameRectOf(string id)
    {
        if (folderById.TryGetValue(id, out var f))
            return !rows.Any(r => r.Folder == f) || f.Separator && f.HiddenInTimeline ? null
                : f.Separator ? new Rect(22, FolderY(f), Math.Max(60, HeaderRight - 30), FolderRowH(f)) : FolderNameRect(f);
        int i = tracks.ToList().FindIndex(t => t.Track.Id == id);
        if (i < 0) return ClipNameRectOf(id);
        return rows.Any(r => r.Folder == null && r.Lane == null && r.Track == i) ? NameRect(i) : null;
    }
    // a wider column gives its extra width to the volume slider and moves the pan knob with it
    double Extra => HeaderW - LayoutHeaderW;
    Rect VolumeRect(int i) => new(52 + Indent(i) + VolumeShift(i), LaneY(i) + 40, 92 + Extra - Indent(i) - VolumeShift(i), 20);
    Rect PanRect(int i) => new(186 + Extra, LaneY(i) + 40, 24, 24);

    /// <summary>A header's volume fader, the mixer's <see cref="Fader"/> lying down: a dark slot lit up to the level, a
    /// mark at 100 and the same cap (tracks and folders).</summary>
    static void DrawVolume(DrawingContext ctx, Rect v, double volume, IBrush fill)
    {
        double cy = v.Center.Y, px = v.X + Math.Clamp(volume / 1.5, 0, 1) * v.Width, unity = Math.Round(v.X + v.Width / 1.5) + 0.5;
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Alpha(Palette.FaintColor, 0.6)), new Rect(v.X, cy - 2.5, v.Width, 5), 2.5, 2.5);
        ctx.DrawRectangle(fill, null, new Rect(v.X + 1, cy - 1.5, Math.Max(0, px - v.X - 1), 3), 1.5, 1.5);
        ctx.DrawLine(Palette.Pen(Palette.Dim), new Point(unity, cy - 8), new Point(unity, cy - 4));
        ctx.DrawLine(Palette.Pen(Palette.Dim), new Point(unity, cy + 4), new Point(unity, cy + 8));
        Fader.DrawSideCap(ctx, px, cy);
    }

    /// <summary>A header's volume readout in dB without the unit ("+3,5", "-6,0", "-∞"), from <see cref="ValueFormat"/>'s table: it is drawn every frame.</summary>
    static string VolumeText(double volume) => ValueFormat.DbShort(volume);

    /// <summary>A header's pan readout ("C", "L30", "R30"), from <see cref="ValueFormat"/>'s tables.</summary>
    static string PanText(double pan) => ValueFormat.PanShort(pan);

    void DrawHeader(DrawingContext ctx, int i)
    {
        var t = tracks[i];
        double y = LaneY(i), lh = LaneH(i);
        if (y + lh < RulerH || y > Bounds.Height) return;
        bool sel = Picked(i);
        ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, new Rect(0, y, HeaderW, lh));
        var tint = TrackIcons.ColorOf(t.Track);
        var mark = TrackColorLooks.SelectionColor(tint);
        if (sel) ctx.FillRectangle(Palette.Alpha(mark, SelectedTint), new Rect(0, y, HeaderW, lh));
        bool isArmed = armed.Contains(i);
        if (isArmed || RecordingInto(i)) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.10), new Rect(0, y, HeaderW, lh));
        // a track in a folder is indented; the folder's color goes on the frame around the group (DrawFolderFrames)
        double ind = Indent(i), hy = HeadY(i);
        TrackColorLooks.DrawHeader(ctx, tint, 0, y, HeaderW, lh, SelectedBox(y, lh).Inflate(0.5),
            ind + (Compact ? TrackColorLooks.CompactStripeX : TrackColorLooks.StripeX),
            Compact ? TrackColorLooks.CompactStripeW : TrackColorLooks.HeaderStripeW);
        if (sel) DrawSelectedFrame(ctx, y, lh, mark);
        if (Compact)
        {
            DrawCompactHeader(ctx, i);
            return;
        }

        double gx = ind + (TrackColorLooks.HasStripe(tint) ? TrackColorLooks.StripeGripShift : 0);
        for (int k = 0; k < 3; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(8 + gx, hy + 13 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(13 + gx, hy + 13 + k * 5, 2, 2));
        }
        var badge = BadgeRect(i);
        TrackIcons.DrawBadge(ctx, badge, t.Track, i + 1, false, Math.Round(badge.Height * 0.18), showVideo);
        double nameX = 58 + ind + NameShift(i);
        bool chip = isArmed && !Recording && InputLabel != null;
        double nameW = NameW(i);
        // a locked track says so with a padlock after its name and its tags, which gives it the room
        // (a frozen one says so with its lit freeze toggle, #240)
        bool padlock = t.Track.Locked && editingNameId != t.Track.Id && !RecordingInto(i);
        if (padlock) nameW = Math.Max(0, nameW - LockW);
        // someone else on this track in a shared session (#291): their tag after the rest, while the name keeps its least
        var someone = editingNameId != t.Track.Id && !RecordingInto(i) ? RowPresence(t.Track.Id) : null;
        double someoneW = someone != null ? PresenceTagW(someone) : 0;
        if (someone != null && nameW - someoneW - TagGap >= NameMinW) nameW -= someoneW + TagGap;
        else someone = null;
        if (RecordingInto(i))
        {
            ctx.DrawText(Loc.T("timeline.recording"), 13, Palette.Rec, new Point(nameX, hy + 11), Palette.Ui, nameW);
            ctx.DrawEllipse(Palette.Rec, null, new Point(RemoveRect(i).Center.X, hy + 20), 4, 4);
        }
        else if (editingNameId != t.Track.Id)
        {
            // after its name: mono / stereo / mixed (asked by the user), then "+2" if transposed, dim while the transpose is made
            var tags = HeaderTags(i, nameX, nameW);
            ctx.DrawText(t.Track.Name, 12.5, Palette.Fg, new Point(nameX, hy + 11), Palette.UiBold, tags.NameRoom);
            if (tags.Layout.Width > 0) DrawLayoutTag(ctx, tags.Layout, t.Layout, t.Track.SplitWave);
            if (tags.Transpose.Width > 0)
            {
                var progress = TransposeProgress?.Invoke(t);
                var r = tags.Transpose;
                ctx.DrawRectangle(progress == null ? Palette.AccentSoft : Palette.Hover, Palette.Pen(progress == null ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line), r, 4, 4);
                FxIcon.Draw(ctx, FxIcon.Transpose, new Rect(r.X + 5, r.Center.Y - 5, 10, 10), progress == null ? Palette.Accent : Palette.Faint);
                var tag = TransposeTag(t, progress == null ? Palette.Accent : Palette.Faint);
                ctx.DrawText(tag, new Point(r.X + 18, r.Center.Y - tag.Height / 2));
            }
            double after = tags.Transpose.Width > 0 ? tags.Transpose.Right : tags.Layout.Width > 0 ? tags.Layout.Right
                : nameX + Math.Min(Palette.Text(t.Track.Name, 12.5, Palette.Fg, Palette.UiBold).Width, tags.NameRoom);
            if (padlock)
            {
                ctx.DrawText("", 11, Palette.Faint, new Point(after + 5, hy + 13), Icons);
                after += LockW;
            }
            if (someone != null) DrawPresenceTag(ctx, new Rect(after + TagGap, hy + 10, someoneW, TagH), someone);
        }
        if (chip) DrawInputChip(ctx, InputRect(i), InputLabel!(t), Hot(InputRect(i)));

        if (!RecordingInto(i) && lastPointer.Y >= y && lastPointer.Y < y + lh)
            DrawTrash(ctx, RemoveRect(i), Hot(RemoveRect(i)));
        DrawRowBottom(ctx, t.Track.Id, 0, HeaderW, y + lh);
        DrawArm(ctx, ArmRect(i), isArmed || RecordingInto(i), Hot(ArmRect(i)));
        DrawFreeze(ctx, FreezeRect(i), t.Track.Frozen, Hot(FreezeRect(i)));
        if (tracks[i].Track.IsMidi && InstrumentRect(i).Width > 0) DrawInstrumentChip(ctx, i);

        var ch = t.Channel;
        // a lane moves them while it plays, like a fader that follows its automation
        double shownVolume = t.ShownVolume, shownPan = t.ShownPan;
        string pan = PanText(shownPan);
        if (lh >= ControlsAt)
        {
            // the volume stands out from the effect sliders (asked by the user): a thicker track with a mark at 100
            // and a fader cap instead of a dot
            DrawVolume(ctx, VolumeRect(i), shownVolume, Palette.Accent);
            ctx.DrawText(VolumeText(shownVolume), 11, Palette.Fg, new Point(148 + Extra, y + 42), Palette.Mono);
            var pr = PanRect(i);
            Knob.Draw(ctx, pr.Center, pr.Width, shownPan * 135, 0, true, Hot(pr));
            ctx.DrawText(pan, 10.5, Palette.Faint, new Point(216 + Extra, y + 43), Palette.Mono);
        }

        // the sends' knobs in one row, as big as the lane allows, empty tracks included (they are channel settings)
        double kd = FxKnobD(i);
        int knobs = kd > 0 ? KnobCount(i) : 0;
        var sendKnobs = knobs > 0 ? SendsOf(t) : [];
        for (int k = 0; k < knobs; k++)
        {
            var (on, amount) = FxState(t, k);
            var kr = FxKnobRect(i, k, kd);
            bool dragging = gesture == "fx" && fxFolder == null && fromIndex == i && fxRow == k;
            DrawSendKnob(ctx, kr, FxLabelRect(i, k, kd), sendKnobs[k], amount, on, dragging || (gesture == null && kr.Inflate(4).Contains(lastPointer)));
        }

        DrawToggle(ctx, MuteRect(i), "M", ch.Muted, Palette.Mute, Hot(MuteRect(i)));
        DrawToggle(ctx, SoloRect(i), "S", ch.Solo, Palette.Accent, Hot(SoloRect(i)));

        if (!MetersOnLayer) DrawMeters(ctx, i);
    }

    static Avalonia.Media.TextFormatting.TextLayout TransposeTag(LiveTrack t, IBrush brush) => Palette.Text(TransposeTags[Math.Clamp((int)Math.Round(t.Track.Transpose), -24, 24) + 24], 10.5, brush, Palette.Mono);

    public static readonly string[] LayoutKeys = ["", "layout.mono", "layout.stereo", "layout.mixed"];
    /// <summary>The name keeps at least this much before the channel badge gives up its place.</summary>
    const double NameMinW = 40;
    const double TagGap = 6, TagH = 18;
    /// <summary>What a locked track's padlock takes from its name.</summary>
    const double LockW = 16;
    static Avalonia.Media.TextFormatting.TextLayout LayoutText(ChannelLayout l, IBrush brush) => Palette.Text(Loc.T(LayoutKeys[(int)l]), 10, brush, Palette.UiBold);

    /// <summary>
    /// Where the tags after a track's name go: its channel badge (<see cref="LiveTrack.Layout"/>, none on a MIDI or
    /// empty track) and its Transponer, right after the shown name; the name gets what is left. The badge gives up its
    /// place first when the name would get less than <see cref="NameMinW"/>.
    /// </summary>
    (Rect Layout, Rect Transpose, double NameRoom) HeaderTags(int i, double nameX, double nameW)
    {
        var t = tracks[i];
        double y = HeadY(i);
        var layout = t.Track.IsMidi ? ChannelLayout.None : t.Layout;
        double lw = layout == ChannelLayout.None ? 0 : LayoutText(layout, Palette.Dim).Width + 24;
        double tw = t.Track.Transpose != 0 ? TransposeTag(t, Palette.Accent).Width + 22 : 0;
        double Room(double l) => Math.Max(0, nameW - (l > 0 ? l + TagGap : 0) - (tw > 0 ? tw + TagGap : 0));
        if (lw > 0 && Room(lw) < NameMinW) lw = 0;
        double room = Room(lw);
        if (lw == 0 && tw == 0) return (NoRect, NoRect, nameW);
        double x = nameX + Math.Min(Palette.Text(t.Track.Name, 12.5, Palette.Fg, Palette.UiBold).Width, room) + TagGap;
        var lr = lw > 0 ? new Rect(x, y + 10, lw, TagH) : NoRect;
        if (lw > 0) x += lw + TagGap;
        return (lr, tw > 0 ? new Rect(x, y + 10, tw, TagH) : NoRect, room);
    }

    /// <summary>The channel badge: one ring for mono, two for stereo, a ring and two small ones for mixed. Lit while the
    /// track shows its stereo clips split. Only informative (asked by the user): it takes no click; the split is set for
    /// all at once from the L R button under the tracks.</summary>
    static void DrawLayoutTag(DrawingContext ctx, Rect r, ChannelLayout layout, bool split)
    {
        bool on = split && layout is ChannelLayout.Stereo or ChannelLayout.Mixed;
        var fg = on ? Palette.Accent : Palette.Dim;
        ctx.DrawRectangle(on ? Palette.AccentSoft : Palette.Hover, Palette.Pen(on ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line), r, 5, 5);
        var pen = Palette.Pen(fg, 1.2);
        double cy = r.Center.Y;
        if (layout == ChannelLayout.Mono) ctx.DrawEllipse(null, pen, new Point(r.X + 10, cy), 3.5, 3.5);
        else if (layout == ChannelLayout.Stereo)
        {
            ctx.DrawEllipse(null, pen, new Point(r.X + 8, cy), 3.5, 3.5);
            ctx.DrawEllipse(null, pen, new Point(r.X + 12.5, cy), 3.5, 3.5);
        }
        else
        {
            ctx.DrawEllipse(null, pen, new Point(r.X + 7, cy), 2.5, 2.5);
            ctx.DrawEllipse(null, pen, new Point(r.X + 12, cy - 2.5), 2.2, 2.2);
            ctx.DrawEllipse(null, pen, new Point(r.X + 12, cy + 2.5), 2.2, 2.2);
        }
        var text = LayoutText(layout, fg);
        text.Draw(ctx, new Point(r.X + 19, cy - text.Height / 2));
    }

    /// <summary>A header in the compact column (asked by the user): handle, icon, the name if it fits, M, S, ● and the
    /// meters. Volume, pan, sends, input and instrument stay in the inspector, the mixer and the track's menu.</summary>
    void DrawCompactHeader(DrawingContext ctx, int i)
    {
        var t = tracks[i];
        double y = LaneY(i), lh = LaneH(i), ind = Indent(i), hy = HeadY(i);
        for (int k = 0; k < 3; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(CompactBadgeX - 8 + ind, hy + 14 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(CompactBadgeX - 4 + ind, hy + 14 + k * 5, 2, 2));
        }
        var badge = BadgeRect(i);
        TrackIcons.DrawBadge(ctx, badge, t.Track, i + 1, false, Math.Round(badge.Height * 0.18), showVideo);
        double nameW = NameW(i);
        if (nameW >= CompactNameMin && editingNameId != t.Track.Id)
            ctx.DrawText(t.Track.Name, 12.5, Palette.Fg, new Point(badge.Right + 6, hy + 11), Palette.UiBold, nameW);
        DrawRowBottom(ctx, t.Track.Id, 0, HeaderW, y + lh);
        var ch = t.Channel;
        DrawToggle(ctx, MuteRect(i), "M", ch.Muted, Palette.Mute, Hot(MuteRect(i)));
        DrawToggle(ctx, SoloRect(i), "S", ch.Solo, Palette.Accent, Hot(SoloRect(i)));
        var arm = ArmRect(i);
        if (arm.Width > 0) DrawArm(ctx, arm, armed.Contains(i) || RecordingInto(i), Hot(arm));
        if (!MetersOnLayer) DrawMeters(ctx, i);
    }

    /// <summary>A send knob of a track or a folder: the dial, the amount inside a big one, and under it the bus's icon
    /// and name (a small one shows the amount there while touched).</summary>
    void DrawSendKnob(DrawingContext ctx, Rect kr, Rect lr, SendKnob s, double amount, bool on, bool hot)
    {
        double kd = kr.Width;
        bool inside = kd >= 36;
        Knob.Draw(ctx, kr.Center, kd, -135 + Math.Clamp(amount, 0, 1) * 270, -135, on, hot, inside);
        string value = Hundreds[Math.Clamp((int)Math.Round(amount * 100), 0, 100)];
        if (inside)
        {
            var vt = Palette.Text(value, kd >= 50 ? 12 : 10.5, KnobLooks.LightCap ? Palette.App : on ? Palette.Fg : Palette.Dim, Palette.Mono);
            ctx.DrawText(vt, new Point(kr.Center.X - vt.Width / 2, kr.Center.Y - vt.Height / 2));
        }
        if (!inside && hot)
        {
            var vt = Palette.Text(value, 10.5, Palette.Fg, Palette.Mono);
            ctx.DrawText(vt, new Point(lr.Center.X - vt.Width / 2, lr.Center.Y - vt.Height / 2));
            return;
        }
        var brush = on ? Palette.Accent : Palette.Faint;
        var label = Palette.Text(s.Name, 10.5, on ? Palette.Fg : Palette.Faint, Palette.Ui);
        // a narrow cell keeps only the icon
        bool withText = 16 + label.Width <= lr.Width - 4;
        double lx = lr.Center.X - (withText ? 16 + label.Width : 12) / 2;
        FxIcon.Draw(ctx, FxIcon.Bus, new Rect(lx, lr.Center.Y - 6, 12, 12), brush);
        if (withText) ctx.DrawText(label, new Point(lx + 16, lr.Center.Y - label.Height / 2));
    }

    /// <summary>A folder's send knobs, in a row under its title, laid out like a track's.</summary>
    void DrawFolderKnobs(DrawingContext ctx, Folder f)
    {
        double d = FolderKnobDiameter(f);
        if (d == 0) return;
        var sends = SendsOf(f);
        for (int k = 0; k < sends.Length; k++)
        {
            var (on, amount) = FolderFxState(f, k);
            var kr = FolderKnobRect(f, k, d);
            bool dragging = gesture == "fx" && fxFolder == f && fxRow == k;
            DrawSendKnob(ctx, kr, FolderLabelRect(f, k, d), sends[k], amount, on, dragging || (gesture == null && kr.Inflate(4).Contains(lastPointer)));
        }
    }

    /// <summary>The header's meter: two thin bars at the right edge, wider and taller in the compact column.</summary>
    void DrawMeters(DrawingContext ctx, int i)
    {
        double y = LaneY(i);
        if (y + LaneH(i) < RulerH || y > Bounds.Height) return;
        if (Compact)
        {
            double cx = HeaderRight - CompactMeterW, ch = LaneH(i) - 12;
            DrawMeterBar(ctx, new Rect(cx, y + 6, 5, ch), tracks[i].ShowL);
            DrawMeterBar(ctx, new Rect(cx + 6, y + 6, 5, ch), tracks[i].ShowR);
            return;
        }
        // a slim lane keeps the bars SlimPad from its edges
        double pad = Math.Min(10, Math.Max(SlimPad, (LaneH(i) - 20) / 2)), mh = LaneH(i) - 2 * pad, mx = HeaderRight - 10;
        DrawMeterBar(ctx, new Rect(mx, y + pad, 4, mh), tracks[i].ShowL);
        DrawMeterBar(ctx, new Rect(mx + 6, y + pad, 4, mh), tracks[i].ShowR);
    }

    /// <summary>Draws the <see cref="Layer"/>: the header meters (unless they are in the background) and the playhead.</summary>
    void RenderLayer(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height;
        if (MetersOnLayer)
            using (ctx.PushClip(new Rect(0, RulerH, HeaderW, Math.Max(0, H - RulerH))))
                for (int i = 0; i < tracks.Count; i++) DrawMeters(ctx, i);

        using (ctx.PushClip(new Rect(HeaderW, 0, Math.Max(0, W - HeaderW), H)))
        {
            DrawDrop(ctx);
            if (!double.IsNaN(returnMark))
            {
                double rx = Math.Round(X(returnMark)) + 0.5;
                ctx.DrawLine(Palette.Dashed(Palette.Alpha(Palette.AccentColor, 0.7), 1), new Point(rx, StripsH), new Point(rx, H));
            }
            DrawPresenceCursors(ctx, H);
            double px = X(Playhead);
            var playColor = Recording ? Palette.Rec : Palette.Accent;
            var pen = Palette.Pen(playColor, 1.5);
            ctx.DrawLine(pen, new Point(px, 0), new Point(px, H));
            using (ctx.PushTransform(Matrix.CreateTranslation(px, StripsH)))
                ctx.DrawGeometry(playColor, null, PlayheadTriangle);
        }
    }

    /// <summary>The playhead's head in the ruler, at x = 0 (moved into place when drawn).</summary>
    static readonly Geometry PlayheadTriangle = Geometry.Parse("M -6,0 L 6,0 L 0,8 Z");

    /// <summary>True while the pointer rests on this header button with nothing dragged, so it lights up like the
    /// window's buttons do on hover.</summary>
    bool Hot(Rect r) => gesture == null && !Recording && r.Contains(lastPointer);

    /// <summary>The hover glow of a drawn button: two soft rings of its color around it, like HoverGlow on the
    /// window's buttons (an effect can't be drawn per button here, and these brushes are cached).</summary>
    static void DrawGlow(DrawingContext ctx, Rect r, Color c)
    {
        ctx.DrawRectangle(Palette.Alpha(c, 0.10), null, r.Inflate(3), 7, 7);
        ctx.DrawRectangle(Palette.Alpha(c, 0.20), null, r.Inflate(1.5), 5.5, 5.5);
    }

    /// <summary>The remove button of a header: a trash can that turns red, on a box with the red glow, on hover.</summary>
    static void DrawTrash(DrawingContext ctx, Rect r, bool hot)
    {
        if (hot)
        {
            DrawGlow(ctx, r, Palette.RecColor);
            ctx.DrawRectangle(Palette.Hover, Palette.Pen(Palette.Alpha(Palette.RecColor, 0.45)), r, 4, 4);
        }
        ctx.DrawText("", 12, hot ? Palette.Rec : Palette.Faint, r.Center - new Point(6, 6), Icons);
    }

    /// <summary>The record-arm toggle: a red dot, lit red while the track takes the next take.</summary>
    static void DrawArm(DrawingContext ctx, Rect r, bool on, bool hot)
    {
        if (hot) DrawGlow(ctx, r, Palette.RecColor);
        if (hot && !on)
        {
            ctx.DrawRectangle(Palette.Hover, Palette.Pen(Palette.Alpha(Palette.RecColor, 0.45)), r, 4, 4);
            ctx.DrawEllipse(Palette.Rec, null, r.Center, 4.5, 4.5);
            return;
        }
        ctx.DrawRectangle(on ? Palette.Rec : Palette.Hover, on ? null : Palette.Pen(Palette.Line), r, 4, 4);
        ctx.DrawEllipse(on ? Palette.Fg : Palette.Alpha(Palette.RecColor, 0.7), null, r.Center, 4.5, 4.5);
    }

    /// <summary>The armed track's input: a small outlined chip with the input and a caret, like a select.</summary>
    static void DrawInputChip(DrawingContext ctx, Rect r, string label, bool hot)
    {
        ctx.DrawRectangle(Palette.Hover, Palette.Pen(Palette.Alpha(Palette.RecColor, hot ? 0.8 : 0.45)), r, 6, 6);
        var t = Palette.Text(label, 10.5, Palette.Fg, Palette.Mono);
        ctx.DrawText(t, new Point(r.X + 6, r.Center.Y - t.Height / 2));
        DrawChevron(ctx, new Point(r.Right - 9, r.Center.Y + 1), Palette.Dim);
    }

    /// <summary>The freeze toggle (#240): a snowflake, on the accent while the track is frozen; like M / S on hover.</summary>
    static void DrawFreeze(DrawingContext ctx, Rect r, bool on, bool hot)
    {
        if (hot) DrawGlow(ctx, r, Palette.AccentColor);
        var border = on ? null : Palette.Pen(hot ? Palette.Alpha(Palette.AccentColor, 0.5) : Palette.Line);
        ctx.DrawRectangle(on ? Palette.Accent : Palette.Hover, border, r, 4, 4);
        Snowflake.Draw(ctx, r.Center, Math.Min(r.Width, r.Height) - 6, on ? Palette.App : hot ? Palette.Fg : Palette.Dim);
    }

    /// <summary>An M / S toggle; on hover it glows in the accent and, while off, its letter and border brighten.</summary>
    static void DrawToggle(DrawingContext ctx, Rect r, string label, bool on, IBrush onBrush, bool hot)
    {
        if (hot) DrawGlow(ctx, r, Palette.AccentColor);
        var border = on ? null : Palette.Pen(hot ? Palette.Alpha(Palette.AccentColor, 0.5) : Palette.Line);
        ctx.DrawRectangle(on ? onBrush : Palette.Hover, border, r, 4, 4);
        var t = Palette.Text(label, 10.5, on ? Palette.App : hot ? Palette.Fg : Palette.Dim, Palette.UiBold);
        ctx.DrawText(t, new Point(r.Center.X - t.Width / 2, r.Center.Y - t.Height / 2));
    }

    void DrawMeterBar(DrawingContext ctx, Rect r, float peak)
    {
        ctx.FillRectangle(Palette.App, r);
        double db = Palette.ToDb(peak);
        double k = Math.Clamp((db + 48) / 48, 0, 1);
        if (k <= 0) return;
        double h = r.Height * k;
        ctx.FillRectangle(db > -1 ? Palette.Rec : Palette.Accent, new Rect(r.X, r.Bottom - h, r.Width, h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        lastPointer = p;
        modifiers = e.KeyModifiers;
        if (Recording) { e.Handled = true; return; }
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsMiddleButtonPressed)
        {
            StartGesture("pan", e);
            return;
        }
        var row = HitRow(p.Y);
        int i = row is { Folder: null, Lane: null } h ? h.Track : -1;
        pressBlock = selectedBlock;
        if (row?.Lane == null) selectedBlock = null;
        if (buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed)
        {
            if (TempoStripPressed(p, e, buttons.IsRightButtonPressed)) return;
            if (ChordStripPressed(p, e, buttons.IsRightButtonPressed)) return;
            if (RangePressed(p, e, buttons.IsRightButtonPressed)) return;
            if (StripPressed(p, e, buttons.IsRightButtonPressed)) return;
        }
        if (buttons.IsRightButtonPressed)
        {
            if (p.Y >= RulerH && AutoRightPressed(row, i, p, e)) { e.Handled = true; return; }
            if (p.Y < RulerH && p.X > HeaderW) RulerMenuRequested?.Invoke(p);
            else if (row?.Folder is { } folder) FolderContextRequested?.Invoke(folder, p);
            else if (row?.Bus is { } rightBus) BusRowRightPressed(rightBus, p);
            // the empty space under the last row, header or lanes, offers a new track
            else if (row == null && p.Y >= RulerH)
            {
                if (p.X > HeaderW) { StartRightErase(e, () => NewTrackRequested?.Invoke(p)); return; }
                NewTrackRequested?.Invoke(p);
            }
            else if (i >= 0 && i < tracks.Count && p.Y >= RulerH)
            {
                if (p.X < HeaderW && tracks[i].Track.IsMidi && InstrumentRect(i).Contains(p)) { Select(i); InstrumentChipPressed(i, p, true); e.Handled = true; return; }
                if (p.X > HeaderW) { StartRightErase(e, () => TrackMenu(i, p)); return; }
                TrackMenu(i, p);
            }
            e.Handled = true;
            return;
        }
        if (!buttons.IsLeftButtonPressed) return;
        ClearRange();
        if (OnGrip(p))
        {
            originalHeaderW = HeaderW;
            StartGesture("header", e);
            return;
        }
        if (RowEdgeAt(p) is { } edge)
        {
            if (e.ClickCount == 2) { RowHeightRequested?.Invoke(edge, null); e.Handled = true; return; }
            var r = rows.First(x => RowIdOf(x) == edge);
            heightFrom = r.H;
            heightDrag = (edge, r.H);
            StartGesture("row-height", e);
            return;
        }
        if (p.Y < RulerH)
        {
            if (p.X > HeaderW) { StartGesture("scrub", e); UpdateGesture(); }
            return;
        }
        if (row is { Lane: not null } autoRow)
        {
            // the tools act on the blocks as on the clips (#281); the lane's header works the same with any tool
            if (tool != TimelineTool.Select && p.X >= HeaderW && AutoToolPressed(autoRow, p, e)) return;
            AutoPressed(autoRow, p, e);
            return;
        }
        if (row?.Folder is { } pressed)
        {
            FolderPressed(pressed, p, e);
            return;
        }
        if (row?.Bus is { } pressedBus)
        {
            BusRowPressed(pressedBus, p, e);
            return;
        }
        if (JoinChipAt(i, p) is { } incoming)
        {
            TransitionRequested?.Invoke(tracks[i], incoming, p);
            e.Handled = true;
            return;
        }
        if (tool != TimelineTool.Select && p.X >= HeaderW && ToolPressed(i, p, e)) return;
        if (i < 0 || i >= tracks.Count)
        {
            // under the last track a drag also draws the selection box
            if ((modifiers & KeyModifiers.Control) == 0 || p.X < HeaderW) Select(-1);
            if (p.X >= HeaderW && row == null) StartBox(e);
            return;
        }
        // Ctrl on a header picks or drops that track, Shift picks the range up to it (to move them together)
        if (p.X < HeaderW && (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0 && !RemoveRect(i).Contains(p))
        {
            Pick(i, (modifiers & KeyModifiers.Shift) != 0);
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && AutoFoldRect(i).Contains(p))
        {
            AutomationFoldRequested?.Invoke(tracks[i].Track);
            e.Handled = true;
            return;
        }
        fromIndex = overIndex = i;
        // any touch on a header (its handle or a control) selects that track first, except the remove button; the
        // handle of a picked track keeps the others picked, to carry them all
        bool carry = picked.Count > 1 && picked.Contains(tracks[i].Track.Id) && OnHandle(i, p);
        if (p.X < HeaderW && !carry && (Selected != i || picked.Count > 1) && !RemoveRect(i).Contains(p)) Select(i);
        if (OnHandle(i, p))
        {
            StartReorder(tracks[i].Track.Id, null, e);
            return;
        }
        if (p.X < HeaderW && RemoveRect(i).Contains(p))
        {
            RemoveTrackRequested?.Invoke(tracks[i].Track.Id, RemoveRect(i).BottomLeft + new Point(0, 4));
            return;
        }
        if (p.X < HeaderW && BadgeRect(i).Contains(p))
        {
            LookRequested?.Invoke(tracks[i], BadgeRect(i).BottomLeft + new Point(0, 4));
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && tracks[i].Track.IsMidi && InstrumentRect(i).Contains(p))
        {
            InstrumentChipPressed(i, p, false);
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && ArmRect(i).Contains(p))
        {
            ArmRequested?.Invoke(tracks[i]);
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && FreezeRect(i).Contains(p))
        {
            FreezeRequested?.Invoke(tracks[i]);
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && armed.Contains(i) && InputLabel != null && InputRect(i).Contains(p))
        {
            Select(i);
            InputMenuRequested?.Invoke(tracks[i], InputRect(i).BottomLeft + new Point(0, 4));
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && e.ClickCount == 2 && !RecordingInto(i) && TitleRect(i).Contains(p))
        {
            NameEditRequested?.Invoke(tracks[i].Track.Id, NameRect(i));
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && MuteRect(i).Contains(p))
        {
            TrackToggleRequested?.Invoke(tracks[i], false);
        }
        else if (p.X < HeaderW && SoloRect(i).Contains(p))
        {
            TrackToggleRequested?.Invoke(tracks[i], true);
        }
        else if (p.X < HeaderW && FxHit(i, p) is { } hit)
        {
            fxFolder = null;
            fxRow = hit.Knob;
            string fx = KnobKey(tracks[i], hit.Knob);
            if (!hit.Dial) EffectRequested?.Invoke(tracks[i], fx, true, null);
            else if (e.ClickCount == 2) EffectRequested?.Invoke(tracks[i], fx, false, 0);
            else
            {
                fxDragValue = FxState(tracks[i], hit.Knob).Amount;
                fxDragY = p.Y;
                StartGesture("fx", e);
            }
        }
        else if (ShowsControls(i) && VolumeRect(i).Contains(p))
        {
            if (e.ClickCount == 2) ChannelRequested?.Invoke(tracks[i], 1, null);
            else { channelFolder = null; StartGesture("volume", e); UpdateGesture(); }
        }
        else if (ShowsControls(i) && PanRect(i).Contains(p))
        {
            if (e.ClickCount == 2) ChannelRequested?.Invoke(tracks[i], null, 0);
            else { channelFolder = null; originalPan = tracks[i].Channel.Pan; StartGesture("pan-knob", e); }
        }
        else if (p.X >= HeaderW && (modifiers & KeyModifiers.Shift) != 0 && e.ClickCount == 1 && !OnClipBody(i, p))
        {
            // Shift+drag over a lane selects a range, like the Rango tool (native only); on a clip it clones the clip
            StartRange(i, p, e);
        }
        else if (p.X >= HeaderW && tracks[i].Track.IsMidi)
        {
            // MidiPressed decides the selection itself: falling through to the Select below dropped what Ctrl had
            // just boxed (#311)
            MidiPressed(i, p, e);
            return;
        }
        else if (p.X >= HeaderW)
        {
            var clip = ClipAt(i, p.X);
            // a double click on a curve point removes it; anywhere else it moves the playhead
            if (e.ClickCount == 2 && clip != null && HitHandle(clip, p) == ClipEditMode.Move && CurvePointAt(i, clip, p) is var gone and >= 0)
            {
                RemoveCurvePoint(clip, gone);
                e.Handled = true;
                return;
            }
            // with Ctrl or Shift a quick second click still picks a clip (#311)
            if (e.ClickCount == 2 && (clip == null || (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0))
            {
                SelectClip(i, clip?.Clip.Id);
                SeekRequested?.Invoke(Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
                return;
            }
            if (clip == null)
            {
                // a drag from here draws the selection box; with Ctrl it adds to what is selected
                if ((modifiers & KeyModifiers.Control) == 0) Select(tracks[i].Clips.Count == 0 ? i : -1);
                StartBox(e);
                return;
            }
            cloning = (modifiers & KeyModifiers.Shift) != 0 && OnClipBody(i, p);
            // a Shift+click without moving boxes from the selected clip to this one (#311)
            shiftAnchor = cloning ? RangeAnchor() : null;
            if (!cloning && HitHandle(clip, p) == ClipEditMode.Move && CurvePointAt(i, clip, p) is var point and >= 0)
            {
                StartCurve(i, clip, point, e);
                return;
            }
            if (!cloning && HitHandle(clip, p) == ClipEditMode.Move && OnCurve(i, clip, p))
            {
                StartCurve(i, clip, -1, e);
                return;
            }
            dragMode = cloning ? ClipEditMode.Move : GrabMode(clip, p);
            // a locked clip is selected but neither moved, trimmed nor stretched; its fades still drag
            if (ClipEditing.IsLocked(tracks[i].Track, clip.Clip) && dragMode is ClipEditMode.Move or ClipEditMode.TrimStart or ClipEditMode.TrimEnd or ClipEditMode.StretchStart or ClipEditMode.StretchEnd or ClipEditMode.Loop)
            {
                SelectClip(i, clip.Clip.Id);
                LockedTouched?.Invoke(tracks[i], clip);
                return;
            }
            // with Ctrl, a click (released without moving) adds the clip to the selection or takes it out
            toggleBase = (modifiers & KeyModifiers.Control) != 0 ? SelectionSeed() : null;
            // a boxed clip grabbed by its body carries the other boxed ones; by an edge, a fade or its loop, it edits them all
            bool group = GroupEdits(clip.Clip.Id);
            if (group)
            {
                SelectedClipId = clip.Clip.Id;
                SelectOne(i);
                if (dragMode == ClipEditMode.Move) StartCarry(clip);
            }
            // a Ctrl or Shift click decides on release: what is selected stays on screen until then (#311)
            else if (toggleBase != null || shiftAnchor != null)
            {
                string id = clip.Clip.Id;
                pendingSelect = () => SelectClip(i, id);
            }
            else SelectClip(i, clip.Clip.Id);
            dragged = clip;
            original = ClipEditing.Copy(clip.Clip);
            preview = ClipEditing.Copy(clip.Clip);
            if (dragMode == ClipEditMode.Loop) StartLoop(i, clip);
            if (group && dragMode != ClipEditMode.Move) StartGroup(dragMode, i, clip, null);
            StartGesture("clip", e);
            return;
        }
        Select(i);
        InvalidateVisual();
    }

    /// <summary>A press on a folder's row: its handle drags it, the arrow folds it, M S and × act on it, a double click on its name renames it and anywhere else folds it.</summary>
    void FolderPressed(Folder f, Point p, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (f.Separator)
        {
            // a separator moves by its handle and renames by a double click; it is never selected or folded
            if (p.X < 20) StartReorder(null, f, e);
            else if (e.ClickCount == 2 && p.X < HeaderW && NameRectOf(f.Id) is { } nameRect) NameEditRequested?.Invoke(f.Id, nameRect);
            return;
        }
        // like a track's header, any touch on the row selects the folder first, except the remove button
        if (!FolderRemoveRect(f).Contains(p) || p.X >= HeaderW) SelectFolder(f.Id);
        if (p.X < HeaderW && FolderAutoFoldRect(f).Contains(p)) { AutomationFoldRequested?.Invoke(f); return; }
        if (p.X < 20) { StartReorder(null, f, e); return; }
        if (p.X >= HeaderW)
        {
            if (e.ClickCount == 2) FolderFoldRequested?.Invoke(f);
            return;
        }
        // a send knob, like a track's: its label turns it on or off, a double click on the dial sets it to 0
        if (FolderFxHit(f, p) is { } hit)
        {
            fxFolder = f;
            fxRow = hit.Knob;
            string bus = FolderKnobBus(f, hit.Knob);
            if (!hit.Dial) FolderSendRequested?.Invoke(f, bus, true, null);
            else if (e.ClickCount == 2) FolderSendRequested?.Invoke(f, bus, false, 0);
            else
            {
                fxDragValue = FolderFxState(f, hit.Knob).Amount;
                fxDragY = p.Y;
                StartGesture("fx", e);
            }
            return;
        }
        // its volume and pan, like a track's: a double click sets them back to 0 dB and the center
        if (FolderShowsControls(f) && FolderVolumeRect(f).Contains(p))
        {
            if (e.ClickCount == 2) FolderChannelRequested?.Invoke(f, 1, null);
            else { channelFolder = f; StartGesture("volume", e); UpdateGesture(); }
            return;
        }
        if (FolderShowsControls(f) && FolderPanRect(f).Contains(p))
        {
            if (e.ClickCount == 2) FolderChannelRequested?.Invoke(f, null, 0);
            else { channelFolder = f; originalPan = f.Pan; StartGesture("pan-knob", e); }
            return;
        }
        if (FoldRect(f).Contains(p)) FolderFoldRequested?.Invoke(f);
        else if (FolderIconRect(f).Contains(p)) FolderLookRequested?.Invoke(f, FolderIconRect(f).BottomLeft + new Point(0, 4));
        else if (FolderRemoveRect(f).Contains(p)) FolderRemoveRequested?.Invoke(f, FolderRemoveRect(f).BottomLeft + new Point(0, 4));
        else if (FolderMuteRect(f).Contains(p)) FolderToggled?.Invoke(f, false);
        else if (FolderSoloRect(f).Contains(p)) FolderToggled?.Invoke(f, true);
        else if (e.ClickCount == 2 && FolderTitleRect(f).Contains(p)) NameEditRequested?.Invoke(f.Id, FolderNameRect(f));
        else if (e.ClickCount == 2) FolderFoldRequested?.Invoke(f);
    }

    /// <summary>Lifts a track or a folder by its handle: it follows the pointer and the others make room (<see cref="FindDrop"/>).</summary>
    void StartReorder(string? trackId, Folder? folder, PointerPressedEventArgs e)
    {
        dragTrackId = trackId;
        dragFolder = folder;
        string id = folder?.Id ?? trackId!;
        var seq = Seq();
        // a picked track carries every picked one on screen with it, stacked in their order
        dragIds.Clear();
        dragOffset.Clear();
        liftedAlso.Clear();
        if (trackId != null)
        {
            bool group = picked.Count > 1 && picked.Contains(trackId);
            double y = 0;
            foreach (var t in seq.OfType<Track>())
            {
                if (t.Id != trackId && (!group || !picked.Contains(t.Id) || !targetTop.ContainsKey(t.Id))) continue;
                dragIds.Add(t.Id);
                dragOffset[t.Id] = y;
                y += RowH(t);
                if (t.Id != trackId) liftedAlso.Add(t.Id);
            }
        }
        liftedId = id;
        dragTop = Top(id) - BlockOffset(id);
        shownTop.Remove(id);
        drop = null;
        StartGesture("reorder", e);
        if (folder != null)
        {
            dropFrom = (seq.IndexOf(folder), null);
            dragGathers = false;
            return;
        }
        var first = seq.OfType<Track>().First(t => t.Id == dragIds[0]);
        int at = seq.IndexOf(first);
        dropFrom = (at, first.FolderId);
        // apart or from different folders, dropping them where they were still moves them together
        dragGathers = false;
        for (int k = 1; k < dragIds.Count; k++)
            if (seq[at + k] is not Track t || t.Id != dragIds[k] || t.FolderId != first.FolderId) dragGathers = true;
    }

    /// <summary>Selects a folder (no track stays selected); raises <see cref="SelectionChanged"/> with -1.</summary>
    public void SelectFolder(string id)
    {
        if (SelectedFolderId == id && Selected < 0) return;
        picked.Clear();
        boxed.Clear();
        Selected = -1;
        SelectedClipId = null;
        SelectedFolderId = id;
        SelectionChanged?.Invoke(-1);
        InvalidateVisual();
    }

    public void SelectClip(int index, string? clipId)
    {
        SelectedClipId = clipId;
        Select(index);
    }

    /// <summary>Px from a clip's edge (or a fade tab, ±half of it) that grab its handle.</summary>
    const double EdgeGrab = 9;

    /// <summary>What a press at <paramref name="p"/> does: <see cref="HitHandle"/>, with Ctrl turning a trim into a stretch.</summary>
    ClipEditMode GrabMode(LiveClip clip, Point p)
    {
        var mode = HitHandle(clip, p);
        if ((modifiers & KeyModifiers.Control) == 0) return mode;
        return mode switch { ClipEditMode.TrimStart => ClipEditMode.StretchStart, ClipEditMode.TrimEnd => ClipEditMode.StretchEnd, _ => mode };
    }

    ClipEditMode HitHandle(LiveClip clip, Point p)
    {
        var w = Window(clip);
        double left = X(w.Start), right = X(w.End);
        if (OnLoopCorner(clip, p, right)) return ClipEditMode.Loop;
        var c = DisplayClip(clip);
        var f = ClipEnvelope.FitFades(c.FadeInSec, c.FadeOutSec, w.End - w.Start);
        // the top half of an edge (or of the tab where a fade ends) drags the fade; the bottom half trims
        int lane = tracks.ToList().FindIndex(t => t.Clips.Contains(clip));
        double mid = LaneY(lane) + LaneH(lane) / 2;
        if (p.Y < mid)
        {
            double fadeOut = Math.Min(right - 5, right - f.FadeOut * pps), fadeIn = Math.Max(left + 5, left + f.FadeIn * pps);
            if (right - p.X < EdgeGrab || Math.Abs(p.X - fadeOut) <= EdgeGrab / 2) return ClipEditMode.FadeOut;
            if (p.X - left < EdgeGrab || Math.Abs(p.X - fadeIn) <= EdgeGrab / 2) return ClipEditMode.FadeIn;
            return ClipEditMode.Move;
        }
        if (p.X - left < EdgeGrab) return ClipEditMode.TrimStart;
        if (right - p.X < EdgeGrab) return ClipEditMode.TrimEnd;
        return ClipEditMode.Move;
    }

    /// <summary>The menu of track <paramref name="i"/>'s header (no clip) or of the clip under the pointer in its lane.</summary>
    void TrackMenu(int i, Point p)
    {
        if (p.X > HeaderW && tracks[i].Track.IsMidi && MidiContext(i, p)) return;
        var clip = p.X > HeaderW ? ClipAt(i, p.X) : null;
        SelectedClipId = clip?.Clip.Id ?? tracks[i].Clips.FirstOrDefault(c => c.Clip.Id == SelectedClipId)?.Clip.Id;
        // on a picked track the others stay picked, so the menu's folder entries move them all; on a boxed
        // clip the others stay boxed, so the menu acts on them all
        if (picked.Count > 1 && picked.Contains(tracks[i].Track.Id) || clip != null && boxed.Count > 1 && boxed.Contains(clip.Clip.Id)) SelectOne(i);
        else Select(i);
        ClipContextRequested?.Invoke(tracks[i], clip, p);
    }

    void StartGesture(string kind, PointerPressedEventArgs e)
    {
        gesture = kind;
        down = e.GetPosition(this);
        downScrollX = scrollX;
        downScrollY = scrollY;
        moved = false;
        capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        if (kind is "clip" or "reorder" or "scrub" or "range" or "box" or "erase" or "zoom" or "pencil" or "auto-pencil") dragScroll.Start();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        lastPointer = e.GetPosition(this);
        modifiers = e.KeyModifiers;
        if (gesture != null) UpdateGesture();
        else
        {
            UpdateCursor();
            UpdatePatternHover();
            if (tool == TimelineTool.Split)
            {
                UpdateSplitHover();
                UpdateAutoSplitHover();
            }
            if (Drawing) UpdatePencilHover();
        }
        InvalidateVisual();
    }

    /// <summary>A key changed the modifiers with the pointer still (the window listens to the keys): the cursor and the handles say what a press does now.</summary>
    public void SetModifiers(KeyModifiers now)
    {
        if (now == modifiers) return;
        modifiers = now;
        if (gesture != null || !IsPointerOver) return;
        UpdateCursor();
        InvalidateVisual();
    }

    /// <summary>The pointer left: the header buttons stop glowing (no gesture holds it outside).</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (gesture != null) return;
        lastPointer = new Point(-1, -1);
        splitHover = null;
        pencilHover = null;
        hoverPattern = null;
        InvalidateVisual();
    }

    void UpdateGesture()
    {
        if (gesture == null) return;
        double dxPx = lastPointer.X - down.X + scrollX - downScrollX;
        if (gesture == "pan")
        {
            scrollX = Math.Max(0, downScrollX - (lastPointer.X - down.X));
            scrollY = downScrollY - (lastPointer.Y - down.Y);
            ClampScroll();
        }
        else if (gesture == "scrub")
        {
            // a plain click is one jump, on press: seeking again on release (or on a pixel of jitter) sent the
            // playhead back to where it already was and crossfaded again. A real drag holds playback (see ScrubChanged)
            bool first = !scrubSought;
            if (!scrubHeld && Math.Abs(lastPointer.X - down.X) >= ScrubSlop)
            {
                scrubHeld = true;
                ScrubChanged?.Invoke(true);
            }
            if (first || scrubHeld)
            {
                scrubSought = true;
                SeekRequested?.Invoke(Math.Max(0, ClipEditing.Snap(Sec(lastPointer.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
            }
        }
        else if (gesture is "marker" or "loop")
            StripMoved();
        else if (gesture == "chords")
            ChordsMoved();
        else if (gesture == "tempo")
            TempoMoved();
        else if (gesture == "tempo-size")
            TempoSizeMoved();
        else if (gesture == "volume")
        {
            var r = channelFolder is { } cf ? FolderVolumeRect(cf) : VolumeRect(fromIndex);
            double v = Math.Clamp((lastPointer.X - r.X) / r.Width * 1.5, 0, 1.5);
            if (channelFolder != null) FolderChannelRequested?.Invoke(channelFolder, v, null);
            else ChannelRequested?.Invoke(tracks[fromIndex], v, null);
        }
        else if (gesture == "header")
            SetHeaderWidth(originalHeaderW + lastPointer.X - down.X);
        else if (gesture == "row-height" && heightDrag is { } hd)
        {
            // an open folder's row holds its title and knobs; a folded one's mini map goes as tall as a track
            bool folder = folderById.TryGetValue(hd.Id, out var hf);
            bool lane = rows.Exists(r => r.Lane?.Id == hd.Id);
            double h = Math.Clamp(Math.Round((heightFrom + lastPointer.Y - down.Y) / 2) * 2,
                lane ? AutomationLane.MinHeight : folder ? Folder.MinHeight : Track.MinHeight,
                lane ? AutomationLane.MaxHeight : folder && !hf!.Collapsed ? Folder.MaxHeight : Track.MaxHeight);
            if (h != hd.H)
            {
                moved = true;
                heightDrag = (hd.Id, h);
                Relayout();
            }
        }
        else if (gesture == "fx")
        {
            double dy = fxDragY - lastPointer.Y;
            fxDragY = lastPointer.Y;
            if (dy != 0)
            {
                fxDragValue = Math.Clamp(fxDragValue + dy / FxDragPx * ((modifiers & KeyModifiers.Shift) != 0 ? 0.2 : 1), 0, 1);
                double value = Math.Round(fxDragValue * 1000) / 1000;
                if (fxFolder != null) FolderSendRequested?.Invoke(fxFolder, FolderKnobBus(fxFolder, fxRow), false, value);
                else EffectRequested?.Invoke(tracks[fromIndex], KnobKey(tracks[fromIndex], fxRow), false, value);
            }
        }
        else if (gesture == "pan-knob")
        {
            double pan = Math.Clamp(originalPan - (lastPointer.Y - down.Y) / 80, -1, 1);
            if (channelFolder != null) FolderChannelRequested?.Invoke(channelFolder, null, Math.Abs(pan) < 0.04 ? 0 : pan);
            else ChannelRequested?.Invoke(tracks[fromIndex], null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else if (gesture == "auto")
        {
            if (Math.Abs(dxPx) >= 2 || Math.Abs(lastPointer.Y - down.Y) >= 2) moved = true;
            if (moved) DragAuto();
        }
        else if (gesture == "curve")
        {
            if (Math.Abs(dxPx) >= 2 || Math.Abs(lastPointer.Y - down.Y) >= 2) moved = true;
            if (moved) DragCurve();
        }
        else if (gesture == "range")
            DragRange();
        else if (gesture == "erase")
            DragErase();
        else if (gesture == "pencil")
            DragPencil();
        else if (gesture == "auto-pencil")
            DragAutoPencil();
        else if (gesture == "midi") UpdateMidiDrag();
        else if (gesture is "zoom" or "listen")
        {
            // nothing follows the pointer: Zoom acts on release (its band is drawn from the pointer), Escuchar on press
        }
        else
        {
            if (!moved && Math.Abs(dxPx) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
            bool wasMoved = moved;
            moved = true;
            if (!wasMoved) RunPendingSelect();
            if (gesture == "box") UpdateBox();
            else if (gesture == "reorder")
            {
                var d = FindDrop();
                // a lifted folder folds its children away; then each new place makes room again
                if (!wasMoved || d != drop)
                {
                    // the rows that make room glide from where they are drawn now
                    // (and on lifting, the other carried tracks glide from there to their place under the grabbed one)
                    foreach (var r in rows)
                    {
                        string id = RowIdOf(r);
                        if (id == liftedId || IsDragged(id) && wasMoved) continue;
                        shownTop.TryAdd(id, shownTop.TryGetValue(id, out double s) ? s : r.Top);
                    }
                    drop = d;
                    Relayout();
                    slide.Start();
                }
            }
            else if (dragged != null && original != null && groupMode != null)
            {
                // the grabbed clip's edge, fade or loop follows the pointer (with the grid), the other boxed ones go with it
                overIndex = fromIndex;
                stuckTo = null;
                if (dragMode == ClipEditMode.Loop) DragLoop(dxPx / pps);
                else
                {
                    preview = ClipEditing.Drag(original, dragged.Media, dragMode, dxPx / pps, Grid, (modifiers & KeyModifiers.Alt) != 0, dragged.LengthSec,
                        SnapToClips != SnapEdges.None ? tracks.SelectMany(t => t.Clips).Where(c => !boxed.Contains(c.Clip.Id)).Select(c => Window(c)) : null, SnapToClips, pps);
                    stretchText = dragMode is ClipEditMode.StretchStart or ClipEditMode.StretchEnd ? $"×{1 / preview.Stretch:0.00}" : null;
                }
                DragGroup();
            }
            else if (dragged != null && original != null && (carriedAlong.Count > 0 || midiCarried.Count > 0 || autoCarried.Count > 0))
            {
                // the boxed clips move in time together, each on its own track (no swapping, no landing elsewhere)
                overIndex = fromIndex;
                preview = ClipEditing.Drag(original, dragged.Media, dragMode, dxPx / pps, Grid, (modifiers & KeyModifiers.Alt) != 0, out stuckTo, dragged.LengthSec,
                    SnapToClips != SnapEdges.None ? tracks.SelectMany(t => t.Clips).Where(c => c != dragged && !boxed.Contains(c.Clip.Id)).Select(c => Window(c)) : null, SnapToClips, pps);
                double at = preview.AtSec;
                DragCarried();
                // a carried clip that would cross zero holds them all back, off the edge they stuck to
                if (preview.AtSec != at) stuckTo = null;
            }
            else if (dragged != null && original != null && dragMode == ClipEditMode.Loop)
            {
                // the clip stays as it is: what changes is how far its repeats reach
                overIndex = fromIndex;
                stuckTo = null;
                DragLoop(dxPx / pps);
            }
            else if (dragged != null && original != null)
            {
                int lane = HitTrack(lastPointer.Y);
                toNewTrack = dragMode == ClipEditMode.Move && InEmptySpace(lastPointer.Y);
                overIndex = dragMode == ClipEditMode.Move && lane >= 0 && lane < tracks.Count ? lane : fromIndex;
                // Ctrl over another track swaps with the clip under the pointer, as in the web (the time stays); without
                // it the clip lands on that track at the time under the pointer, shown by a ghost (native only).
                // a take dropped on a MIDI track gives its notes there ("Extraer MIDI"): nothing to swap with
                swapping = !cloning && overIndex != fromIndex && (modifiers & KeyModifiers.Control) != 0 && !tracks[overIndex].Track.IsMidi;
                swapId = swapping ? ClipAt(overIndex, lastPointer.X)?.Clip.Id : null;
                stuckTo = null;
                if (swapping) preview = ClipEditing.Copy(original);
                else preview = ClipEditing.Drag(original, dragged.Media, dragMode, dxPx / pps, Grid, (modifiers & KeyModifiers.Alt) != 0, out stuckTo, dragged.LengthSec,
                    SnapToClips != SnapEdges.None ? tracks.SelectMany(t => t.Clips).Where(c => c != dragged).Select(c => Window(c)) : null, SnapToClips, pps);
                stretchText = dragMode is ClipEditMode.StretchStart or ClipEditMode.StretchEnd ? $"×{1 / preview.Stretch:0.00}" : null;
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gesture == null) return;
        lastPointer = e.GetPosition(this);
        modifiers = e.KeyModifiers;
        UpdateGesture();
        string kind = gesture;
        var value = preview;
        int from = fromIndex, to = overIndex;
        bool toNew = toNewTrack && moved && (kind == "clip" && dragged != null || kind == "midi" && midiHeld != null);
        string? target = kind == "clip" && toNew ? NewTrackTarget : kind == "clip" && to >= 0 && to != from ? tracks[to].Track.Id : null;
        var extractTo = target != null && !toNew && tracks[to].Track.IsMidi && dragged != null ? (Clip: dragged, Track: tracks[to]) : default;
        string? swap = swapId;
        bool changed = moved;
        var landed = drop;
        var from0 = dropFrom;
        var movedFolder = dragFolder;
        var movedTracks = dragIds.ToList();
        bool gathers = dragGathers;
        List<string> erased = [.. erasing];
        int paintedLane = pencilLane;
        var painted = kind == "pencil" ? TakePencil() : null;
        var group = TakeGroup();
        bool midiCarry = kind == "midi" && midiCarrying;
        List<Clip>? together = group == null && kind == "clip" && (carriedAlong.Count > 0 || midiCarried.Count > 0 || autoCarried.Count > 0) && value != null ? [value, .. carriedPreview.Values]
            : midiCarry ? [.. carriedPreview.Values] : null;
        var midiMoves = together != null ? MidiMoves() : [];
        var blockMoves = together != null ? AutoMoves() : [];
        string? grabbedId = kind == "midi" ? midiHeld?.Id : dragged?.Clip.Id;
        var midi = kind == "midi" && !midiCarry && group == null ? MidiResult() : null;
        var loop = kind == "clip" && group == null ? TakeLoop() : null;
        bool stayed = kind == "clip" ? value?.AtSec == original?.AtSec && target == null : midiCarry || autoCarrying ? carryShift == 0 : midi == null;
        clones = cloning && changed && !stayed && extractTo.Clip == null ? CloneIds(kind) : [];
        var toggle = toggleBase;
        var anchor = shiftAnchor;
        var pending = pendingSelect;
        var menu =kind == "erase" && !changed ? rightMenu : null;
        // the new height is applied while the dragged one still holds: applied after, the rows below went back to the
        // old height for a moment and glided from there, so everything jumped on release
        if (kind == "row-height" && changed && heightDrag is { } rh) RowHeightRequested?.Invoke(rh.Id, rh.H);
        if (kind == "auto") EndAuto(changed);
        List<string> erasedBlocks = kind == "erase" ? EndRightErase(changed) : [];
        var autoPainted = kind == "auto-pencil" ? TakeAutoPencil() : null;
        ClearGesture();
        e.Pointer.Capture(null);
        if (kind == "range" && Range == null) ClearRange();
        if (kind == "erase" && (erased.Count > 0 || erasedBlocks.Count > 0)) ClipsErased?.Invoke(erased, erasedBlocks);
        menu?.Invoke();
        if (painted is { Count: > 0 } && paintedLane >= 0 && paintedLane < tracks.Count) PencilPlaced?.Invoke(tracks[paintedLane], painted);
        if (autoPainted is { } ap) AutomationPainted?.Invoke(ap.Owner, ap.Lane, ap.Brush, ap.Starts);
        if (kind == "zoom") EndZoom(lastPointer, (modifiers & KeyModifiers.Alt) != 0);
        if (kind is "marker" or "loop") StripReleased(kind, changed);
        if (kind == "chords") ChordsReleased(changed);
        if (kind == "tempo") TempoReleased(changed);
        if (kind == "tempo-size") TempoSizeReleased(changed);
        if (kind == "box" && changed) EndBox();
        // Ctrl+click on a take adds it or takes it out (with Ctrl held, a drag still swaps it with another track's clip)
        if (toggle != null && !changed && grabbedId != null) ToggleBoxed(grabbedId, toggle);
        // Shift+click on a clip without moving it: from the selected clip to this one (#311)
        else if (anchor != null && !changed && grabbedId != null && anchor != grabbedId && BoxRange(anchor, grabbedId)) { }
        // a Shift+click with no range to box selects just that clip
        else if (pending != null && !changed) pending();
        else if (group is { } g)
        {
            // a click on an edge without moving it leaves just that clip selected, like one on its body
            if (!changed && kind == "midi") SelectMidiClip(from, grabbedId);
            else if (!changed && kind != "auto") SelectClip(from, grabbedId);
            else if (changed) RaiseClipsEdited(g.Takes, g.Loops, g.Midi, g.Blocks);
        }
        // a click on a boxed clip without moving it leaves just that one selected
        else if (together != null && !changed)
        {
            if (kind == "midi") SelectMidiClip(from, grabbedId);
            else SelectClip(from, grabbedId);
        }
        else if (together != null) ClipsMoved?.Invoke(together, midiMoves, blockMoves);
        else if (extractTo.Clip != null) ExtractMidiRequested?.Invoke(extractTo.Clip, extractTo.Track);
        else if (loop is { } lp)
        {
            if (lp.Changed) ClipLoopRequested?.Invoke(lp.Source, lp.End);
        }
        else if (kind == "clip" && changed && value != null) ClipEditCommitted?.Invoke(value, target, swap);
        if (kind == "curve" && changed && value != null) ClipEditCommitted?.Invoke(value, null, null);
        if (kind == "midi") ReleaseMidi(midi);
        if (kind == "reorder" && changed && landed is { } l && (l != from0 || gathers))
        {
            if (movedFolder != null) FolderMoveRequested?.Invoke(movedFolder.Id, l.Pos);
            else if (movedTracks.Count > 0) TrackMoveRequested?.Invoke(movedTracks, l.Pos, l.Folder);
        }
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (gesture == "tempo") tempoPreview = null;
        ClearGesture();
    }

    public void CancelGesture()
    {
        ClearRange();
        if (gesture == "tempo") tempoPreview = null;
        ClearGesture();
        CancelChordDrag();
    }
    void ClearGesture()
    {
        if (gesture == "listen") ListenEnded?.Invoke();
        tempoHeldScale = null;
        stretchText = null;
        loopRuns.Clear();
        ClearGroup();
        if (erasing.Count > 0) erasing.Clear();
        ClearRightErase();
        rightMenu = null;
        if (gesture == "pencil") TakePencil();
        bool reordered = gesture == "reorder" && moved;
        if (reordered)
        {
            // keep every row where it is drawn; once the window applies the new order they glide to their places
            foreach (var r in rows) { string id = RowIdOf(r); shownTop[id] = Top(id); }
            slide.Start();
        }
        else if (gesture == "reorder")
        {
            liftedId = null;
            liftedAlso.Clear();
        }
        gesture = null;
        dragFolder = null;
        dragTrackId = null;
        dragIds.Clear();
        drop = null;
        bool resized = heightDrag != null;
        heightDrag = null;
        if (reordered || resized) Relayout();
        scrubSought = false;
        if (scrubHeld)
        {
            scrubHeld = false;
            ScrubChanged?.Invoke(false);
        }
        var pointer = capturedPointer;
        capturedPointer = null;
        dragged = null;
        original = preview = null;
        cloning = false;
        shiftAnchor = null;
        toggleBase = null;
        pendingSelect = null;
        ClearMidiGesture();
        carriedAlong.Clear();
        carriedPreview.Clear();
        autoCarried.Clear();
        autoCarrying = false;
        autoToggleBase = null;
        ClearAutoPencil();
        stuckTo = null;
        curvePoint = -1;
        swapId = null;
        swapping = false;
        toNewTrack = false;
        fromIndex = overIndex = -1;
        dragScroll.Stop();
        pointer?.Capture(null);
        Cursor = ToolCursor() ?? CursorOf(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    /// <summary>The cursor says what a press does where the pointer is (the timeline has no hover help: it got in the way).</summary>
    void UpdateCursor()
    {
        var row = HitRow(lastPointer.Y);
        int i = row is { Folder: null, Lane: null } h ? h.Track : -1;
        if (ToolCursor() is { } toolCursor) { Cursor = toolCursor; return; }
        var cursor = StandardCursorType.Arrow;
        if (OnTempoEdge(lastPointer)) cursor = StandardCursorType.SizeNorthSouth;
        else if (InTempoStrip(lastPointer)) cursor = TempoCursor(lastPointer);
        else if (InRangeStrip(lastPointer)) cursor = RangeCursor(lastPointer);
        else if (InStrip(lastPointer)) cursor = StripCursor(lastPointer);
        else if (InChordStrip(lastPointer)) cursor = ChordCursor(lastPointer);
        else if (OnGrip(lastPointer)) cursor = StandardCursorType.SizeWestEast;
        else if (RowEdgeAt(lastPointer) != null) cursor = StandardCursorType.SizeNorthSouth;
        else if (row is { Lane: not null } autoRow)
        {
            if (OnAutoLoop(autoRow))
            {
                Cursor = ToolCursors.Loop;
                return;
            }
            cursor = AutoCursor(autoRow);
        }
        else if (row?.Folder is { } fo)
        {
            if (lastPointer.X < 20 || (lastPointer.X < HeaderW && (FoldRect(fo).Contains(lastPointer) || FolderIconRect(fo).Contains(lastPointer))))
                cursor = StandardCursorType.Hand;
            else if (FolderFxHit(fo, lastPointer) is { } fx)
                cursor = fx.Dial ? StandardCursorType.SizeNorthSouth : StandardCursorType.Hand;
        }
        else if (i >= 0 && i < tracks.Count && tracks[i].Track.IsMidi && lastPointer.X >= HeaderW && MidiClipAt(i, lastPointer.X) is { } loopClip && OnMidiLoop(i, loopClip, lastPointer))
        {
            Cursor = ToolCursors.Loop;
            return;
        }
        else if (MidiCursor(i, lastPointer) is { } midiCursor) cursor = midiCursor;
        else if (i >= 0 && i < tracks.Count)
        {
            if (OnHandle(i, lastPointer) || BadgeRect(i).Contains(lastPointer) || JoinChipAt(i, lastPointer) != null
                || tracks[i].Track.IsMidi && lastPointer.X < HeaderW && InstrumentRect(i).Contains(lastPointer)) cursor = StandardCursorType.Hand;
            else if (lastPointer.X < HeaderW && FxHit(i, lastPointer) is { } fx)
                cursor = fx.Dial ? StandardCursorType.SizeNorthSouth : StandardCursorType.Hand;
            else if (lastPointer.X >= HeaderW && ClipAt(i, lastPointer.X) is { } clip)
            {
                var mode = GrabMode(clip, lastPointer);
                if (mode is ClipEditMode.TrimStart or ClipEditMode.TrimEnd or ClipEditMode.StretchStart or ClipEditMode.StretchEnd or ClipEditMode.Loop && ClipEditing.IsLocked(tracks[i].Track, clip.Clip)) mode = ClipEditMode.Move;
                if (mode == ClipEditMode.Loop)
                {
                    Cursor = ToolCursors.Loop;
                    return;
                }
                if (mode is ClipEditMode.StretchStart or ClipEditMode.StretchEnd)
                {
                    Cursor = ToolCursors.Stretch;
                    return;
                }
                cursor = mode switch
                {
                    ClipEditMode.Move when CurvePointAt(i, clip, lastPointer) >= 0 => StandardCursorType.SizeAll,
                    ClipEditMode.Move when OnCurve(i, clip, lastPointer) => StandardCursorType.Cross,
                    // the diagonal follows the fade line: / for a fade in, \ for a fade out
                    ClipEditMode.FadeIn => StandardCursorType.TopRightCorner, ClipEditMode.FadeOut => StandardCursorType.TopLeftCorner,
                    ClipEditMode.TrimStart => StandardCursorType.LeftSide, ClipEditMode.TrimEnd => StandardCursorType.RightSide,
                    _ => StandardCursorType.Hand,
                };
            }
        }
        Cursor = CursorOf(cursor);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        int i = HitTrack(p.Y);
        if (p.Y >= RulerH && i >= 0 && i < tracks.Count && ShowsControls(i) && PanRect(i).Contains(p))
        {
            double pan = Math.Clamp(tracks[i].Channel.Pan + e.Delta.Y * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.01 : 0.05), -1, 1);
            ChannelRequested?.Invoke(tracks[i], null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else if (p.Y >= RulerH && i >= 0 && i < tracks.Count && p.X < HeaderW && FxHit(i, p) is { Dial: true } fx)
        {
            // like the mixer's knobs: 50 steps over the range, Shift = fine
            // Transponer: a semitone per notch
            double amount = FxState(tracks[i], fx.Knob).Amount + Math.Sign(e.Delta.Y) * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.004 : 0.02);
            EffectRequested?.Invoke(tracks[i], KnobKey(tracks[i], fx.Knob), false, Math.Round(Math.Clamp(amount, 0, 1) * 1000) / 1000);
        }
        else if (p.Y >= RulerH && HitRow(p.Y) is { Folder: { } fp, Lane: null } && FolderShowsControls(fp) && FolderPanRect(fp).Contains(p))
        {
            double pan = Math.Clamp(fp.Pan + e.Delta.Y * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.01 : 0.05), -1, 1);
            FolderChannelRequested?.Invoke(fp, null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else if (p.Y >= RulerH && HitRow(p.Y) is { Folder: { } fo, Lane: null } && FolderFxHit(fo, p) is { Dial: true } ffx)
        {
            double amount = FolderFxState(fo, ffx.Knob).Amount + Math.Sign(e.Delta.Y) * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.004 : 0.02);
            FolderSendRequested?.Invoke(fo, FolderKnobBus(fo, ffx.Knob), false, Math.Round(Math.Clamp(amount, 0, 1) * 1000) / 1000);
        }
        // plain wheel: up and down; the modifiers for zoom, sideways and track height are the user's (Shortcuts.Wheel:
        // Shift, Ctrl and Alt by default), and so is each one's direction (Shortcuts.WheelSign); a sideways wheel or
        // touchpad swipe always goes sideways
        else if (Shortcuts.FindWheel(e.KeyModifiers) is { } wheel)
        {
            // some mice and drivers turn a modified wheel into a sideways delta, so take whichever axis moved
            double d = (e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) * Shortcuts.WheelSign(wheel);
            if (wheel == "wheelZoom") ZoomTo(pps * Math.Pow(1.18, d));
            else if (wheel == "wheelHeight") SetTrackHeight(laneH + d * 6);
            // wheel up goes right, toward later in the song (reversed: down)
            else scrollX = Math.Max(0, scrollX + (e.Delta.Y + e.Delta.X) * Shortcuts.WheelSign(wheel) * 80);
        }
        else
        {
            scrollY -= e.Delta.Y * Shortcuts.WheelSign(Shortcuts.WheelScroll) * 60;
            ClampScroll();
            if (e.Delta.X != 0) scrollX = Math.Max(0, scrollX - e.Delta.X * 80);
        }
        e.Handled = true;
        InvalidateVisual();
    }
}

/// <summary>A send shown as a knob in a track's header: the bus, its name (the knob's label) and its <see cref="FxIcon"/> icon.</summary>
public readonly record struct SendKnob(string BusId, string Name);
