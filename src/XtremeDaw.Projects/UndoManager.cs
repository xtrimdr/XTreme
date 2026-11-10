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
/// In a shared session (#291) the others' changes are listed too, by their author (<see cref="Remote"/>), among this
/// user's steps by time; they are only told, never undone here (each one undoes their own).
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
        FlushRemote();
        undo.Add(new(Capture(project), clock.GetUtcNow().UtcDateTime, label));
        if (undo.Count > Capacity)
        {
            undo.RemoveAt(0);
            Trimmed = true;
        }
        redo.Clear();
        Version++;
    }

    /// <summary>
    /// Takes back the last <see cref="Checkpoint"/> as if it never was: the project goes back to it and the step is
    /// dropped, with nothing left to redo. For an edit that failed halfway.
    /// </summary>
    public bool Revert(Project project)
    {
        if (undo.Count == 0) return false;
        var e = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Apply(project, e.Json);
        lastKey = "";
        Version++;
        return true;
    }

    public bool Undo(Project project) => Move(project, undo, redo);
    public bool Redo(Project project) => Move(project, redo, undo);

    bool Move(Project project, List<Entry> from, List<Entry> to)
    {
        if (from.Count == 0) return false;
        FlushRemote();
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
        FlushRemote();
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

    /// <summary>
    /// One step of Historial → Pasos: <see cref="Position"/> after it is <see cref="Index"/>; undone ones are not
    /// <see cref="Done"/>. Someone else's change in a shared session has its <see cref="Author"/> and no place to go
    /// to (<see cref="Index"/> &lt; 0); this user's own have none.
    /// </summary>
    public sealed record Step(int Index, DateTime At, EditSteps.Label Label, bool Done, string? Author = null);

    /// <summary>The others' changes kept for the list (#291), at most <see cref="Capacity"/>; a burst by one person is one step.</summary>
    readonly List<RemoteStep> others = [];

    /// <summary>
    /// Someone else's change, named once its burst is over: the open one (the last) keeps its states on both sides until
    /// then, the closed ones only their name.
    /// </summary>
    sealed class RemoteStep(string author, DateTime at, string before, string after, Func<string, string?> mediaName)
    {
        public readonly string Author = author;
        public DateTime At = at;
        public string? Before = before, After = after;
        public Func<string, string?>? MediaName = mediaName;
        public EditSteps.Label? Label;
        /// <summary>Still taking the next change of its burst.</summary>
        public bool Open = true;

        public EditSteps.Label Name()
        {
            if (Before != null) Label = EditSteps.Describe(Before, After!, MediaName!);
            if (!Open) (Before, After, MediaName) = (null, null, null);
            return Label!;
        }
    }

    /// <summary>
    /// A change by <paramref name="author"/> in a shared session (#291), from <paramref name="before"/> to
    /// <paramref name="after"/> (<see cref="Capture"/> states): listed by <see cref="Steps"/> under their name. Their
    /// changes that come one after the other within <see cref="RemoteMergeWindow"/>, with no step of this user's in
    /// between (a fader drag goes as a few a second), make one step.
    /// </summary>
    public void Remote(string author, string before, string after, Func<string, string?> mediaName)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var last = others.Count > 0 ? others[^1] : null;
        if (last is { Open: true } && last.Author == author && now - last.At < RemoteMergeWindow && (undo.Count == 0 || undo[^1].At <= last.At))
        {
            last.After = after;
            last.At = now;
        }
        else
        {
            if (last is { Open: true })
            {
                last.Open = false;
                last.Name();
            }
            others.Add(new(author, now, before, after, mediaName));
            if (others.Count > Capacity) others.RemoveAt(0);
        }
        Version++;
    }

    /// <summary>How close two changes by the same person go to be one step in the list.</summary>
    static readonly TimeSpan RemoteMergeWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Every step kept, oldest first, each with its name (worked out once and remembered while its states stay).
    /// </summary>
    /// <param name="mediaName">A take's name by its media id, for the clips' lines.</param>
    public List<Step> Steps(Project project, Func<string, string?> mediaName)
    {
        FlushRemote();
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
        if (others.Count == 0) return list;
        // the others' changes among these by time
        var merged = new List<Step>(list.Count + others.Count);
        int at = 0;
        foreach (var o in others)
        {
            while (at < list.Count && list[at].At <= o.At) merged.Add(list[at++]);
            merged.Add(new(-1, o.At, o.Name(), true, o.Author));
        }
        while (at < list.Count) merged.Add(list[at++]);
        return merged;
    }

    void Apply(Project project, string json)
    {
        FlushRemote();
        ApplyState(project, json);
        lastKey = "";
        Version++;
    }

    /// <summary>Puts an undo state (<see cref="Capture"/>) back into the project: what an undo, a redo or a shared session's change does.</summary>
    internal static void ApplyState(Project project, string json)
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
    }

    internal static string Capture(Project p) => JsonSerializer.Serialize(new Editable(p.Tracks, p.Settings.Layout,
        p.Settings.Aspect, p.Settings.Color, p.Settings.VideoFx, p.Settings.Mode, p.Folders, p.Markers, p.Settings.Buses, p.Settings.Compare, p.ChordMarkers, p.Settings.TimeScale, p.Settings.TempoScale, p.Settings.TempoMap,
        [.. p.Media.Select(m => new Place(m.Id, m.Audio, m.Video, m.AudioBytes, m.VideoBytes))]), ProjectStore.Json);

    /// <summary>Someone else's changes in a shared session, not yet carried into the kept states (<see cref="Rebase"/>).</summary>
    readonly List<System.Text.Json.Nodes.JsonObject> remote = [];

    /// <summary>
    /// A change that came from someone else in a shared session (#291): it goes into every kept state too, so an undo
    /// takes back only this user's own steps, not what the others did since. Applied when the states are next read.
    /// </summary>
    public void Rebase(System.Text.Json.Nodes.JsonObject patch)
    {
        if (undo.Count + redo.Count == 0) return;
        remote.Add(patch);
    }

    void FlushRemote()
    {
        if (remote.Count == 0) return;
        for (int l = 0; l < 2; l++)
        {
            var list = l == 0 ? undo : redo;
            for (int i = 0; i < list.Count; i++)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(list[i].Json)!.AsObject();
                foreach (var p in remote) LiveDiff.Apply(node, p);
                list[i] = list[i] with { Json = node.ToJsonString(ProjectStore.Json) };
            }
        }
        remote.Clear();
    }

    public void Clear()
    {
        remote.Clear();
        others.Clear();
        undo.Clear();
        redo.Clear();
        labels.Clear();
        lastKey = "";
        lastAt = 0;
        Trimmed = false;
        Version++;
    }
}
