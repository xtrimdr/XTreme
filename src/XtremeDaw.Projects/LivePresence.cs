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
}
