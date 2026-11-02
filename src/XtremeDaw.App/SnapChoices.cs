using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The magnet's choices, the same in the timeline and the piano roll (asked by the user, FL Studio's list in its order,
/// plus the 2 beats the timeline already had). A step is a sixteenth, so "Paso" and "1/4 tiempo" are the same length, as
/// in FL. The ids that existed before keep their name ("1/16" is "Paso"), so what config.json has saved stays valid.
/// "line" and "cell" follow the grid drawn at the current zoom; the others are fixed lengths
/// (<see cref="ClipEditing.GridSeconds"/>).
/// </summary>
public static class SnapChoices
{
    public static readonly string[] Ids =
        ["line", "cell", "off", "1/6step", "1/64", "1/3step", "1/32", "1/16", "1/6beat", "1/4beat", "1/8T", "1/8", "1/4", "1/2", "bar"];
    /// <summary>The piano roll's list: first, the timeline's magnet (FL's Main).</summary>
    public static readonly string[] PianoIds = ["main", .. Ids];
    /// <summary>Where <see cref="Ids"/> changes kind (a separator in the menu): zoom, off, lengths.</summary>
    public static readonly int[] Breaks = [2, 3];

    /// <summary>A step that doesn't depend on the zoom: the only kind that can quantize.</summary>
    public static bool IsFixed(string id) => ClipEditing.GridSeconds(id, 60) > 0;

    /// <summary>Its place in <see cref="Ids"/>; what isn't there (a config.json from another version) counts as off.</summary>
    public static int Index(string? id) => Array.IndexOf(Ids, Ids.Contains(id) ? id : "off");

    public static string Label(string id) => id switch
    {
        "off" => Loc.T("snap.label.0"),
        "bar" => Loc.T("snap.label.1"),
        "main" or "line" or "cell" => Loc.T($"snap.label.{id}"),
        "1/2" => Loc.T("snap.label.twoBeats"),
        "1/4" => Loc.T("snap.label.beat"),
        "1/16" => Loc.T("snap.label.step"),
        "1/8" => Loc.T("snap.label.beats", "1/2"),
        "1/8T" => Loc.T("snap.label.beats", "1/3"),
        "1/4beat" => Loc.T("snap.label.beats", "1/4"),
        "1/6beat" => Loc.T("snap.label.beats", "1/6"),
        "1/32" => Loc.T("snap.label.steps", "1/2"),
        "1/3step" => Loc.T("snap.label.steps", "1/3"),
        "1/64" => Loc.T("snap.label.steps", "1/4"),
        "1/6step" => Loc.T("snap.label.steps", "1/6"),
        _ => id,
    };

    /// <summary>What it snaps to, in musician's words: each option's tooltip.</summary>
    public static string Hint(string id) => Loc.T("snap.hint." + id switch
    {
        "off" => "0", "bar" => "1", "1/2" => "2", "1/4" => "3", "1/8" => "4", "1/8T" => "5", "1/16" or "1/4beat" => "6", "1/32" => "7", "1/64" => "8",
        "1/6beat" => "beat6", "1/3step" => "step3", "1/6step" => "step6",
        _ => id,
    });
}
