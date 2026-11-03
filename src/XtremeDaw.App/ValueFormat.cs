using XtremeDaw.App.Controls;

namespace XtremeDaw.App;

/// <summary>
/// The one format of a channel's volume, pan and send level (asked by the user: the same text in the inspector, the
/// mixer, the timeline and the lanes), after the guide's "Formato de valores": a gain as "+3,5 dB" in the system's
/// culture, silence as "-∞ dB", a pan as "Centro" / "30 L" / "30 R" or, compact, "C" / "L30" / "R30". The compact
/// forms come from tables made once, so a header drawn every frame allocates nothing.
/// </summary>
public static class ValueFormat
{
    /// <summary>A linear volume at or under this (about -66 dB) reads as silence, like the fader's bottom.</summary>
    public const double Floor = 0.0005;
    /// <summary>What silence reads, with and without the unit.</summary>
    public const string Silence = "-∞ dB", SilenceShort = "-∞";
    /// <summary>The compact table's range in tenths of a dB: from the floor to a little above <see cref="Fader.VolMax"/> (+3,5 dB).</summary>
    const int MinTenths = -661, MaxTenths = 60;

    static readonly string[] DbShorts = [.. Enumerable.Range(MinTenths, MaxTenths - MinTenths + 1).Select(t => $"{t / 10.0:+0.0;-0.0;0.0}")];
    static readonly string[] PanLefts = [.. Enumerable.Range(0, 101).Select(v => $"L{v}")],
        PanRights = [.. Enumerable.Range(0, 101).Select(v => $"R{v}")];

    /// <summary>A gain in dB: "+3,5 dB", "0,0 dB", "-12,0 dB".</summary>
    public static string Gain(double db) => $"{db:+0.0;-0.0;0.0} dB";

    /// <summary>A linear volume or send level (1 = 0 dB) as the gain it applies: "+3,5 dB", "-∞ dB".</summary>
    public static string Db(double v) => v <= Floor ? Silence : Gain(20 * Math.Log10(v));

    /// <summary>
    /// The same without the unit, for tight spots: "+3,5", "-6,0", "-∞". From a table within the fader's range, so it
    /// allocates nothing there; only a value beyond it is formatted.
    /// </summary>
    public static string DbShort(double v)
    {
        if (v <= Floor) return SilenceShort;
        double db = 20 * Math.Log10(v);
        double tenths = Math.Round(db * 10, MidpointRounding.AwayFromZero);
        if (tenths < MinTenths) return SilenceShort;
        return tenths <= MaxTenths ? DbShorts[(int)tenths - MinTenths] : $"{db:+0.0;-0.0;0.0}";
    }

    /// <summary>Whole percent of a pan (-1..1) that still reads as the centre.</summary>
    static bool Centered(double pan) => Math.Round(Math.Abs(pan) * 100, MidpointRounding.AwayFromZero) == 0;

    /// <summary>A pan (-1..1) in full: "Centro", "30 L", "30 R".</summary>
    public static string Pan(double pan) =>
        Centered(pan) ? Loc.T("value.pan.center") : $"{Math.Round(Math.Abs(pan) * 100, MidpointRounding.AwayFromZero):0} {(pan < 0 ? "L" : "R")}";

    /// <summary>A pan (-1..1) compact: "C", "L30", "R30". From tables, so it allocates nothing.</summary>
    public static string PanShort(double pan)
    {
        if (Centered(pan)) return "C";
        double v = Math.Round(Math.Abs(pan) * 100, MidpointRounding.AwayFromZero);
        if (v > 100) return pan < 0 ? $"L{v:0}" : $"R{v:0}";
        return pan < 0 ? PanLefts[(int)v] : PanRights[(int)v];
    }

    /// <summary>A send level (0..1) as a percent: "35%".</summary>
    public static string Percent(double level) => $"{Math.Round(level * 100, MidpointRounding.AwayFromZero):0}%";
}
