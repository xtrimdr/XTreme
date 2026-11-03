using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>What a click on the grid does: writes a note, paints one per step, or cuts.</summary>
public enum PianoTool { Draw, Paint, Slice }

/// <summary>Where an audio clip's wave goes behind the notes: its place in seconds and its peaks.</summary>
public readonly record struct BackWave(double AtSec, double FromSec, double LengthSec, float[] Min, float[] Max, double PeaksPerSec);

/// <summary>
/// The piano roll's tools (<see cref="MidiTools"/>) and what they need on the roll. A tool runs on a copy of the notes as
/// they were when it opened (<see cref="BeginTool"/>), again each time a setting moves, so the result is on screen and in
/// the speakers (<see cref="NotesPreviewed"/>) without touching the undo history until it is accepted. Also the select
/// commands, the note flags, the lane under the grid, paint and slice, the lasso, the notes of other tracks in grey, key
/// names and the wave behind the notes.
/// </summary>
public sealed partial class PianoRollView
{
    /// <summary>How visible a muted note stays.</summary>
    const double MutedAlpha = 0.3;
    /// <summary>The note colors: one hue each, like track colors they are the user's data, not tokens.</summary>
    static readonly Color[] GroupColors = [.. Enumerable.Range(0, MidiNote.Colors).Select(i => HsvColor.ToRgb(i * 360.0 / MidiNote.Colors, 0.62, 0.92))];

    public static Color GroupColor(int color) => GroupColors[Math.Clamp(color - 1, 0, MidiNote.Colors - 1)];

    /// <summary>A tool changed the notes for now: the window lets them be heard. Not an edit.</summary>
    public event Action<string, List<MidiNote>>? NotesPreviewed;
    /// <summary>Something else needs the notes: whatever the tools panel holds is settled.</summary>
    public event Action? ToolInterrupted;
    /// <summary>A tool's key, or tool-accept / tool-cancel while one is open.</summary>
    public event Action<string>? ToolKeyPressed;
    /// <summary>A double click on a note: its card.</summary>
    public event Action<MidiNote>? NoteDetailsRequested;
    /// <summary>The right button on a key: its name.</summary>
    public event Action<int>? KeyMenuRequested;

    public PianoTool WriteTool { get; set; }

    NoteLane lane;
    /// <summary>What the lane under the grid shows and edits.</summary>
    public NoteLane Lane { get => lane; set { lane = value; CancelGesture(); InvalidateVisual(); } }
    string LaneTitle => Loc.T(lane == NoteLane.Velocity ? "piano.velocity" : $"tool.c.lane.{lane.ToString().ToLowerInvariant()}");
    int LaneOf(MidiNote n) => NoteLanes.Get(n, lane);

    Dictionary<int, string>? keyNames;
    /// <summary>The track's names for its keys.</summary>
    public Dictionary<int, string>? KeyNames { get => keyNames; set { keyNames = value; InvalidateVisual(); } }

    void DrawKeyName(DrawingContext ctx, int pitch, double y)
    {
        var text = Palette.Text(keyNames![pitch], 9.5, MidiNote.IsBlack(pitch) ? Palette.Fg : Palette.App, Palette.Ui);
        ctx.DrawText(text, new Point(Math.Max(2, KeyW - 6 - text.Width), y + (rowH - text.Height) / 2));
    }

    /// <summary>A slide's wedge and a portamento's dot, so what a note does shows on it.</summary>
    void DrawNoteMarks(DrawingContext ctx, MidiNote n, Rect r)
    {
        if (r.Width < 8) return;
        if (n.Slide) ctx.FillRectangle(Palette.Fg, new Rect(r.X + 1, r.Bottom - 3, Math.Min(10, r.Width - 2), 2));
        if (n.Porta) ctx.FillRectangle(Palette.Fg, new Rect(r.X + 2, r.Y + 2, 3, 3), 1.5f);
    }

    // --- other tracks, in grey ---------------------------------------------------------------------

    (MidiClip Clip, double Bpm)[] shadows = [];
    /// <summary>Clips of other MIDI tracks, shown as grey notes to write against.</summary>
    public void SetShadows(IEnumerable<MidiClip> clips)
    {
        shadows = [.. clips.Select(c => (c, bpm))];
        InvalidateVisual();
    }

    void DrawShadows(DrawingContext ctx, double gb, double W)
    {
        if (patternId != null) return;
        var fill = Palette.Alpha(Palette.FgColor, 0.12);
        foreach (var (c, _) in shadows) DrawFaintNotes(ctx, c.Notes, StartBeat(c, bpm), c.CellBeats, c.LengthBeats, W, gb, fill);
    }

    BackWave[] waves = [];
    /// <summary>Audio clips to draw behind the notes.</summary>
    public void SetBackWave(BackWave[] list)
    {
        waves = list;
        InvalidateVisual();
    }

    void DrawBackWave(DrawingContext ctx, double gb, double W)
    {
        if (patternId != null) return;
        double mid = (RulerH + gb) / 2, h = (gb - RulerH) / 2 * 0.9;
        var pen = Palette.Pen(Palette.Alpha(Palette.FgColor, 0.1));
        foreach (var w in waves)
        {
            double perBeat = 60 / bpm;
            double from = Math.Max(KeyW, XA(w.AtSec / perBeat)), to = Math.Min(W, XA((w.AtSec + w.LengthSec) / perBeat));
            for (double x = from; x < to; x++)
            {
                double sec = BeatA(x) * perBeat - w.AtSec + w.FromSec;
                int i = (int)(sec * w.PeaksPerSec);
                if (i < 0 || i >= w.Min.Length) continue;
                ctx.DrawLine(pen, new Point(x + 0.5, mid - w.Max[i] * h), new Point(x + 0.5, mid - w.Min[i] * h));
            }
        }
    }

    // --- paint, slice, lasso -----------------------------------------------------------------------

    double paintFrom;
    readonly List<Point> lasso = [];

    /// <summary>Writes a note in the step under the pointer unless the row has one there: dragging leaves a line of them. Shift stays on the row it began.</summary>
    void PaintAt(Point p)
    {
        double step = SnapBeats > 0 ? SnapBeats : 0.25, at = Math.Floor(Math.Max(0, Beat(p.X)) / step) * step;
        if (at >= clipBeats) return;
        int pitch = (modifiers & KeyModifiers.Shift) != 0 ? PitchAt(down.Y) : WritePitch(p.Y);
        if (notes.Any(n => n.Pitch == pitch && Math.Abs(n.Beat - at) < 1e-6)) return;
        var note = new MidiNote { Pitch = pitch, Beat = at, Beats = step, Velocity = lastVelocity };
        notes.Add(note);
        selected.Add(note);
        changed = true;
        if (Math.Abs(at - paintFrom) > 1e-6) NoteAuditioned?.Invoke(pitch, lastVelocity);
        paintFrom = at;
    }

    /// <summary>Cuts every note the line from the press to the pointer crosses, on the magnet (Alt free). A click cuts the note under it.</summary>
    void Slice()
    {
        var a = down;
        var b = lastPointer;
        double top = Math.Min(a.Y, b.Y) - 1, bottom = Math.Max(a.Y, b.Y) + 1;
        foreach (var n in notes.ToList())
        {
            var r = NoteRect(n);
            if (r.Bottom < top || r.Y > bottom) continue;
            double y = r.Center.Y, t = Math.Abs(b.Y - a.Y) < 1 ? 0 : (y - a.Y) / (b.Y - a.Y);
            double cut = Snap(Beat(a.X + (b.X - a.X) * Math.Clamp(t, 0, 1)));
            if (cut <= n.Beat + MidiNote.MinBeats / 2 || cut >= n.EndBeat - MidiNote.MinBeats / 2) continue;
            var rest = n.Copy();
            rest.Beat = cut;
            rest.Beats = n.EndBeat - cut;
            n.Beats = cut - n.Beat;
            notes.Add(rest);
            changed = true;
        }
    }

    void StartLasso() => lasso.Add(down);

    void EndLasso()
    {
        if ((modifiers & KeyModifiers.Shift) == 0) selected.Clear();
        foreach (var n in notes)
            if (Inside(NoteRect(n).Center)) selected.Add(n);
        WithGroups();
    }

    /// <summary>Even-odd rule against the shape drawn.</summary>
    bool Inside(Point p)
    {
        bool inside = false;
        for (int i = 0, j = lasso.Count - 1; i < lasso.Count; j = i++)
        {
            var (a, b) = (lasso[i], lasso[j]);
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    void DrawToolGesture(DrawingContext ctx)
    {
        var pen = Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.9));
        if (gesture == "slice")
            ctx.DrawLine(pen, moved ? down : new Point(down.X, RulerH), moved ? lastPointer : new Point(down.X, GridBottom));
        if (gesture != "lasso") return;
        for (int i = 1; i < lasso.Count; i++) ctx.DrawLine(pen, lasso[i - 1], lasso[i]);
    }

    /// <summary>A note picked brings the rest of its group.</summary>
    void WithGroups()
    {
        var groups = selected.Where(n => n.Group != 0).Select(n => n.Group).ToHashSet();
        if (groups.Count > 0) selected.UnionWith(notes.Where(n => groups.Contains(n.Group)));
    }

    // --- tools -------------------------------------------------------------------------------------

    List<MidiNote>? toolBase;
    List<int> toolTargets = [];

    public bool ToolOpen => toolBase != null;
    public IReadOnlyCollection<MidiNote> SelectedNotes => selected;

    /// <summary>Remembers the notes a tool starts from; false with no clip. The selected ones are its targets, or all of them.</summary>
    public bool BeginTool()
    {
        if (clipId == null) return false;
        CancelGesture();
        toolBase = [.. notes.Select(n => n.Copy())];
        toolTargets = selected.Count > 0
            ? [.. Enumerable.Range(0, notes.Count).Where(i => selected.Contains(notes[i]))]
            : [.. Enumerable.Range(0, notes.Count)];
        return true;
    }

    /// <summary>Runs the tool from those notes with these settings and shows the result. Says how many notes it worked on.</summary>
    public int PreviewTool(MidiTool tool, Dictionary<string, int> values, Dictionary<string, string> words)
    {
        if (toolBase == null || clipId == null) return 0;
        notes.Clear();
        notes.AddRange(toolBase.Select(n => n.Copy()));
        var run = new ToolRun
        {
            Notes = notes, Targets = [.. toolTargets.Select(i => notes[i])], Values = new(values), Words = words,
            ClipBeats = clipBeats, OriginBeats = Origin, SnapBeats = SnapBeats, Bpm = bpm, Scale = scale, Copied = clipboard,
            PlayBeat = Math.Max(0, PlayBeat(playheadSec) - Origin),
        };
        var result = MidiTools.Apply(tool, run);
        selected.Clear();
        selected.UnionWith(result);
        chordRead = -1;
        NotesPreviewed?.Invoke(clipId, [.. notes.Select(n => n.Copy())]);
        InvalidateVisual();
        return run.Targets.Count;
    }

    /// <summary>The result stays, as one undo step.</summary>
    public void AcceptTool()
    {
        if (toolBase == null) return;
        toolBase = null;
        Commit();
    }

    /// <summary>Back to the notes the tool started from.</summary>
    public void CancelTool()
    {
        if (toolBase is not { } was || clipId == null) return;
        toolBase = null;
        var picked = toolTargets.Count == was.Count ? [] : toolTargets;
        notes.Clear();
        notes.AddRange(was);
        selected.Clear();
        selected.UnionWith(picked.Select(i => notes[i]));
        chordRead = -1;
        NotesPreviewed?.Invoke(clipId, [.. notes.Select(n => n.Copy())]);
        InvalidateVisual();
    }

    /// <summary>A tool with no settings, at once.</summary>
    public bool RunTool(MidiTool tool)
    {
        if (!BeginTool()) return false;
        PreviewTool(tool, MidiTools.Defaults(tool), []);
        AcceptTool();
        return true;
    }

    static readonly Random selectDice = new();

    /// <summary>One of <see cref="MidiSelect.Commands"/>.</summary>
    public void Select(string command)
    {
        if (clipId == null) return;
        var picked = MidiSelect.Run(command, notes, selected, Origin, selectDice, SnapBeats);
        selected.Clear();
        selected.UnionWith(picked);
        InvalidateVisual();
    }

    /// <summary>Changes the selected notes (all with none selected), as one edit.</summary>
    public void EditNotes(Action<MidiNote> edit)
    {
        if (clipId == null) return;
        CancelGesture();
        foreach (var n in selected.Count > 0 ? selected : (IEnumerable<MidiNote>)notes) edit(n);
        Commit();
        InvalidateVisual();
    }

    /// <summary>The selected notes all muted, or back if they already were.</summary>
    public void ToggleMute()
    {
        bool mute = (selected.Count > 0 ? selected : (IEnumerable<MidiNote>)notes).Any(n => !n.Muted);
        EditNotes(n => n.Muted = mute);
    }

    /// <summary>The selected notes become a group of their own.</summary>
    public void GroupNotes()
    {
        if (selected.Count < 2) return;
        int id = notes.Max(n => n.Group) + 1;
        EditNotes(n => n.Group = id);
    }

    public void UngroupNotes() => EditNotes(n => n.Group = 0);

    /// <summary>The notes selected now, or all: what a file or a sheet gets.</summary>
    public List<MidiNote> NotesForExport() => [.. (selected.Count > 1 ? selected : (IEnumerable<MidiNote>)notes).OrderBy(n => n.Beat)];
    public double ClipBeats => clipBeats;
    /// <summary>The name of the chord selected, or empty.</summary>
    public string SelectedChord
    {
        get
        {
            SyncChordBar();
            return chordVoicings.Length > 0 ? chordName : "";
        }
    }
}
