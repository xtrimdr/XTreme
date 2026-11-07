namespace XtremeDaw.Projects;

/// <summary>
/// The note values the magnet and the grid offer, by the industry's names (asked by the user: the same list and the same
/// names in the magnet, the ruler's menu and the piano roll): the bar, straight values, triplets ("T") and dotted ("D").
/// A dotted value doesn't fit a whole number of times in a bar, so its lines start over at each bar
/// (<see cref="SnapGrid.Bar"/>, asked by the user); the others fit, and for them it makes no difference.
/// </summary>
public static class Subdivisions
{
    /// <summary>A bar of 4/4, what "bar" means without a tempo map; a bar of the song is <see cref="TempoMap.BarBeats"/> (#214).</summary>
    public const double BarBeats = 4;

    public static readonly string[] Straight = ["bar", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64"];
    public static readonly string[] Triplets = ["1/2T", "1/4T", "1/8T", "1/16T", "1/32T"];
    public static readonly string[] Dotted = ["1/2D", "1/4D", "1/8D", "1/16D"];
    public static readonly string[] All = [.. Straight, .. Triplets, .. Dotted];

    /// <summary>A value's length in beats ("1/8T" = a third), or 0 when it isn't one of <see cref="All"/>.</summary>
    public static double Beats(string? id)
    {
        if (id == "bar") return BarBeats;
        if (id == null || !All.Contains(id)) return 0;
        double beats = 4.0 / int.Parse(id[2..].TrimEnd('T', 'D'));
        return id[^1] switch { 'T' => beats * 2 / 3, 'D' => beats * 1.5, _ => beats };
    }

    /// <summary>
    /// The id a saved choice has now: the magnet's names before this list ("1/4beat", "1/3step"…) go to the same length,
    /// the ones that are gone to the nearest, and "cell" to "line". Lowercase "t"/"d" (typed in xtlab) are accepted.
    /// </summary>
    public static string Migrate(string id) => id switch
    {
        "1/4beat" => "1/16", "1/6beat" => "1/16T", "1/3step" => "1/32T", "1/6step" => "1/32T", "cell" => "line",
        _ when id.Length > 2 && id[^1] is 't' or 'd' => id[..^1] + char.ToUpperInvariant(id[^1]),
        _ => id,
    };
}
