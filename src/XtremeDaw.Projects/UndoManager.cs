using System.Text.Json;

namespace XtremeDaw.Projects;

/// <summary>
/// Port of the editable snapshots in the web app's App.tsx: lanes, layout, aspect and bus/video settings,
/// plus the native project mode (video / solo audio), track folders, the extra returns, markers, the mix's A/B, the
/// tempo map (#214) and the whole song's speed (#262: the transport's tempo is a step too; the A–B loop range is
/// transport state and stays out, but it follows the speed a step brings back).
/// Mutable native objects are serialized for isolation; media, exports, log and project identity
/// stay outside undo, like the web. Of a take, only where its files are comes back (<see cref="Place"/>, native only:
/// <see cref="LinkedMedia"/> changes that); a take never leaves or returns by undo. Same 200-step limit and 800 ms
/// coalescing per nonempty key.
/// Native only: every step keeps when it was made, Historial → Pasos lists them with a name (<see cref="Steps"/>, named
/// by <see cref="EditSteps"/> from the states on both sides) and a click goes straight to any of them (<see cref="JumpTo"/>).
/// </summary>
public sealed class UndoManager(TimeProvider? timeProvider = null)
{
    // These limits match checkpoint() in App.tsx.
    const int Capacity = 200;
    static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(800);
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    /// <summary>
    /// The states before each step (undo) and after each undone one (redo, the next on top), with the step's time and
    /// its label when the edit brought one. Moving a state between the lists carries its step along.
    /// </summary>
    readonly List<Entry> undo = [], redo = [];
    string lastKey = "";
    long lastAt;
    /// <summary>Labels already worked out, by the state before the step (the same string while it stays in the lists).</summary>
    Dictionary<string, (string After, EditSteps.Label Label)> labels = new(ReferenceEqualityComparer.Instance);

    sealed record Entry(string Json, DateTime At, EditSteps.Label? Label);

    /// <summary>Only the editable state; serialized before every edit, never the immutable recordings.</summary>
    sealed record Place(string Id, string? Audio, string? Video, long AudioBytes, long VideoBytes);

    sealed record Editable(List<Track> Tracks, string Layout, string Aspect, ColorGrade Color, VideoFx VideoFx,
        string? Mode = null, List<Folder>? Folders = null, List<Marker>? Markers = null, List<Bus>? Buses = null,
        MixCompareState? Compare = null, List<ChordMarker>? ChordMarkers = null, double TimeScale = 1, double TempoScale = 1, TempoMap? TempoMap = null, List<Place>? Places = null);

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    /// <summary>How many steps are done: the current state is <see cref="Steps"/>' position.</summary>
    public int Position => undo.Count;
    /// <summary>Steps older than the first one kept were dropped at the 200-step limit.</summary>
    public bool Trimmed { get; private set; }
    /// <summary>Changes on every checkpoint, undo, redo, jump and clear (not on a merged one): the list needs redrawing.</summary>
    public int Version { get; private set; }

    /// <param name="label">What the step says, when the diff can't tell (a recording); null = named from the change.</param>
    public void Checkpoint(Project project, string key = "", EditSteps.Label? label = null)
    {
        long now = clock.GetTimestamp();
        bool merge = key.Length > 0 && key == lastKey && clock.GetElapsedTime(lastAt, now) < MergeWindow;
        lastKey = key;
        lastAt = now;
        if (merge) return;
        undo.Add(new(Capture(project), clock.GetUtcNow().UtcDateTime, label));
        if (undo.Count > Capacity)
        {
            undo.RemoveAt(0);
            Trimmed = true;
        }
        redo.Clear();
        Version++;
    }

    public bool Undo(Project project) => Move(project, undo, redo);
    public bool Redo(Project project) => Move(project, redo, undo);

    bool Move(Project project, List<Entry> from, List<Entry> to)
    {
        if (from.Count == 0) return false;
        var e = from[^1];
        to.Add(e with { Json = Capture(project) });
        from.RemoveAt(from.Count - 1);
        Apply(project, e.Json);
        return true;
    }

    /// <summary>
    /// Goes to the state after step <paramref name="position"/> (0 = before the first one kept), undoing or redoing
    /// every step in between at once. False when it is already there or out of range.
    /// </summary>
    public bool JumpTo(Project project, int position)
    {
        int n = undo.Count, m = n + redo.Count;
        if (position < 0 || position > m || position == n) return false;
        var (states, steps) = Timeline(project);
        undo.Clear();
        redo.Clear();
        for (int i = 0; i < position; i++) undo.Add(new(states[i], steps[i].At, steps[i].Label));
        for (int i = m; i > position; i--) redo.Add(new(states[i], steps[i - 1].At, steps[i - 1].Label));
        Apply(project, states[position]);
        return true;
    }

    /// <summary>
    /// The states in order (the first kept, …, the current one, …, the last undone) and each step between two of them
    /// (step i goes from state i to i + 1).
    /// </summary>
    (List<string> States, List<(DateTime At, EditSteps.Label? Label)> Steps) Timeline(Project project)
    {
        var states = new List<string>(undo.Count + redo.Count + 1);
        var steps = new List<(DateTime, EditSteps.Label?)>(undo.Count + redo.Count);
        foreach (var e in undo) { states.Add(e.Json); steps.Add((e.At, e.Label)); }
        states.Add(Capture(project));
        for (int i = redo.Count - 1; i >= 0; i--) { states.Add(redo[i].Json); steps.Add((redo[i].At, redo[i].Label)); }
        return (states, steps);
    }

    /// <summary>One step of Historial → Pasos: <see cref="Position"/> after it is <see cref="Index"/>; undone ones are not <see cref="Done"/>.</summary>
    public sealed record Step(int Index, DateTime At, EditSteps.Label Label, bool Done);

    /// <summary>
    /// Every step kept, oldest first, each with its name (worked out once and remembered while its states stay).
    /// </summary>
    /// <param name="mediaName">A take's name by its media id, for the clips' lines.</param>
    public List<Step> Steps(Project project, Func<string, string?> mediaName)
    {
        var (states, steps) = Timeline(project);
        var known = labels;
        labels = new(ReferenceEqualityComparer.Instance);
        var list = new List<Step>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            string before = states[i], after = states[i + 1];
            if (steps[i].Label is not { } label)
            {
                label = known.TryGetValue(before, out var k) && (ReferenceEquals(k.After, after) || k.After == after)
                    ? k.Label
                    : EditSteps.Describe(before, after, mediaName);
                labels[before] = (after, label);
            }
            list.Add(new(i + 1, steps[i].At, label, i < undo.Count));
        }
        return list;
    }

    void Apply(Project project, string json)
    {
        var state = JsonSerializer.Deserialize<Editable>(json, ProjectStore.Json)!;
        project.Tracks = state.Tracks;
        project.Folders = state.Folders ?? [];
        project.Markers = state.Markers ?? [];
        project.ChordMarkers = state.ChordMarkers ?? [];
        var s = project.Settings;
        s.Layout = state.Layout;
        s.Aspect = state.Aspect;
        s.Color = state.Color;
        s.VideoFx = state.VideoFx;
        s.Mode = state.Mode ?? s.Mode;
        s.Buses = state.Buses ?? [];
        s.Compare = state.Compare;
        // the song's speed comes back too (#262: a change of tempo is a step); the loop, outside undo, follows it
        double speed = state.TimeScale / s.TimeScale;
        if (double.IsFinite(speed) && speed > 0 && Math.Abs(speed - 1) > 1e-12)
        {
            if (s.LoopStartSec is { } a) s.LoopStartSec = a * speed;
            if (s.LoopEndSec is { } b) s.LoopEndSec = b * speed;
        }
        s.TimeScale = state.TimeScale;
        s.TempoScale = state.TempoScale;
        if (state.TempoMap != null) s.TempoMap = state.TempoMap;
        // a take recorded after that state has no line here and stays as it is
        if (state.Places != null)
        {
            var media = project.Media.ToDictionary(m => m.Id);
            foreach (var at in state.Places)
                if (media.TryGetValue(at.Id, out var m)) (m.Audio, m.Video, m.AudioBytes, m.VideoBytes) = (at.Audio, at.Video, at.AudioBytes, at.VideoBytes);
        }
        project.Normalize();
        lastKey = "";
        Version++;
    }

    static string Capture(Project p) => JsonSerializer.Serialize(new Editable(p.Tracks, p.Settings.Layout,
        p.Settings.Aspect, p.Settings.Color, p.Settings.VideoFx, p.Settings.Mode, p.Folders, p.Markers, p.Settings.Buses, p.Settings.Compare, p.ChordMarkers, p.Settings.TimeScale, p.Settings.TempoScale, p.Settings.TempoMap,
        [.. p.Media.Select(m => new Place(m.Id, m.Audio, m.Video, m.AudioBytes, m.VideoBytes))]), ProjectStore.Json);

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        labels.Clear();
        lastKey = "";
        lastAt = 0;
        Trimmed = false;
        Version++;
    }
}
