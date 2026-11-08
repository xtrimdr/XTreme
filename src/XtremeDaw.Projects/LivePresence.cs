namespace XtremeDaw.Projects;

/// <summary>
/// Where someone is in a shared session (#291), sent on its own, apart from the project's changes: what they have
/// selected, the track they play or record on and their playhead. Ephemeral (decided with the user): never saved,
/// never an undo step, dropped when they leave. What they are editing is not sent: each copy reads it from the
/// changes themselves (<see cref="LiveSession.EditingBy"/>). Whoever sends it doesn't say who they are; the host
/// stamps <see cref="Who"/>, so nobody passes for someone else.
/// </summary>
public sealed record LivePresence
{
    /// <summary>Whose it is: set by the host.</summary>
    public string Who { get; init; } = "";
    /// <summary>The selected track and clip (their ids); null = none.</summary>
    public string? Track { get; init; }
    public string? Clip { get; init; }
    /// <summary>The track they play with a MIDI keyboard or record on; null = none.</summary>
    public string? Playing { get; init; }
    /// <summary>On <see cref="Playing"/>, a take is being recorded.</summary>
    public bool Recording { get; init; }
    /// <summary>Their transport runs: <see cref="Cursor"/> moves on its own from when it arrived.</summary>
    public bool Rolling { get; init; }
    /// <summary>Their playhead, in timeline seconds.</summary>
    public double Cursor { get; init; }
    /// <summary>Their mouse and what they hold with it (#319); null = nothing to show. It changes many times a second:
    /// <see cref="Still"/> says whether anything else did.</summary>
    public LivePointer? Pointer { get; init; }

    /// <summary>The same presence without the pointer: two that only differ in it are the same for the tags.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LivePresence Still => Pointer == null ? this : this with { Pointer = null };
}

/// <summary>
/// Someone's mouse in a shared session (#319, asked by the user: "cursores ... que muestren específicamente lo que
/// están tocando"). Each copy has its window at its own size, zoom and scroll, so it never goes in pixels: over the
/// timeline it is a row and a time (or a place over the headers), and while they hold a control (a fader, a knob, a
/// button), the strip or panel it belongs to and which of its controls it is. Each copy draws it where that is on its
/// own screen.
/// </summary>
public sealed record LivePointer
{
    /// <summary>Over the timeline: the row's id (track, folder, bus or automation lane) and how far down it (0..1).</summary>
    public string? Row { get; init; }
    public double RowY { get; init; }
    /// <summary>Over the row's lane, the time (s); over its header, how far across the headers (0..1).</summary>
    public double? Sec { get; init; }
    public double? Head { get; init; }
    /// <summary>The control they hold: whose it is (a track's, folder's or bus's id) and which one (<c>area:Type:n</c>,
    /// the n-th control of that type in that area: "mix" a mixer strip, "insp" the inspector).</summary>
    public string? Owner { get; init; }
    public string? Control { get; init; }
}
