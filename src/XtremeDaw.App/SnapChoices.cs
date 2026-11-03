using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The magnet's and the grid's note values, the same list and the same names in the timeline's magnet, the ruler's menu and
/// the piano roll (asked by the user: the industry's names, largest first). "line" follows the grid drawn at the current
/// zoom; the values are <see cref="Subdivisions.All"/>. What config.json saved under the old names goes through
/// <see cref="Subdivisions.Migrate"/>.
/// </summary>
public static class SnapChoices
{
    public static readonly string[] Ids = ["line", "off", .. Subdivisions.All];
    /// <summary>The piano roll's list: first, the timeline's magnet.</summary>
    public static readonly string[] PianoIds = ["main", .. Ids];
    /// <summary>Where <see cref="Ids"/> changes kind (a separator in the menu): straight values, triplets, dotted.</summary>
    public static readonly int[] Breaks = [2, 2 + Subdivisions.Straight.Length, 2 + Subdivisions.Straight.Length + Subdivisions.Triplets.Length];

    /// <summary>The note values, for the grid's menus: the same separators as the magnet's.</summary>
    public static bool BreakBefore(string id) => id == Subdivisions.Triplets[0] || id == Subdivisions.Dotted[0];

    /// <summary>A step that doesn't depend on the zoom: the only kind that can quantize.</summary>
    public static bool IsFixed(string id) => ClipEditing.GridSeconds(id, 60) > 0;

    /// <summary>Its place in <see cref="Ids"/>; what isn't there (a config.json from another version) counts as off.</summary>
    public static int Index(string? id)
    {
        id = id == null ? null : Subdivisions.Migrate(id);
        return Array.IndexOf(Ids, Ids.Contains(id) ? id : "off");
    }

    public static string Label(string id) => id switch
    {
        "off" => Loc.T("snap.label.0"),
        "bar" => Loc.T("snap.label.1"),
        "line" => Loc.T("grid.auto"),
        "main" => Loc.T("snap.label.main"),
        _ => id,
    };

    /// <summary>What it snaps to, in musician's words: each option's tooltip ("Cada corchea de tresillo").</summary>
    public static string Hint(string id)
    {
        switch (id)
        {
            case "off": return Loc.T("snap.hint.0");
            case "bar": return Loc.T("snap.hint.1");
            case "line" or "main": return Loc.T("snap.hint." + id);
        }
        if (!Subdivisions.All.Contains(id)) return id;
        string note = Loc.T("snap.value." + id[2..].TrimEnd('T', 'D'));
        return Loc.T(id[^1] switch { 'T' => "snap.hint.triplet", 'D' => "snap.hint.dotted", _ => "snap.hint.straight" }, note);
    }
}
