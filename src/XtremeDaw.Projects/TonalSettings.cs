using System.Text.Json;
using System.Text.Json.Serialization;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>
/// A tonal balance's own target (#210), made from reference files the user picked and kept in the slot itself, so the
/// project doesn't depend on those files: per point of <see cref="TonalScale"/> the expected level and its range (dB,
/// levelled like a reading, rounded to a hundredth: a few hundred numbers) and the low-end punch range. <see cref="Name"/>
/// and <see cref="Files"/> only say where it came from.
/// </summary>
public sealed class TonalCurveSettings
{
    public string Name { get; set; } = "";
    /// <summary>The references' file names (without their folders), for the menu's hint.</summary>
    public List<string> Files { get; set; } = [];
    public double[] Mean { get; set; } = [];
    public double[] Min { get; set; } = [];
    public double[] Max { get; set; } = [];
    public double CrestMin { get; set; }
    public double CrestMax { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The analysis as the slot keeps it.</summary>
    public static TonalCurveSettings From(TonalTarget t, string name, IEnumerable<string> files)
    {
        static double[] R(double[] v) => [.. v.Select(x => Math.Round(x, 2))];
        return new()
        {
            Name = name,
            Files = [.. files.Select(Path.GetFileName).OfType<string>()],
            Mean = R(t.Mean),
            Min = R(t.Min),
            Max = R(t.Max),
            CrestMin = Math.Round(t.CrestMin, 2),
            CrestMax = Math.Round(t.CrestMax, 2),
        };
    }

    public TonalTarget ToTarget() => new("custom", Mean, Min, Max, CrestMin, CrestMax);

    /// <summary>Repairs what can be repaired (ranges in order, the punch in a sane range); false = unusable (the slot drops it).</summary>
    internal bool Normalize()
    {
        Name ??= "";
        Files ??= [];
        Files.RemoveAll(string.IsNullOrWhiteSpace);
        int n = TonalScale.Points;
        if (Mean?.Length != n || Min?.Length != n || Max?.Length != n) return false;
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(Mean[i]) || !double.IsFinite(Min[i]) || !double.IsFinite(Max[i])) return false;
            Mean[i] = Math.Clamp(Mean[i], -60, 60);
            Min[i] = Math.Min(Min[i], Mean[i]);
            Max[i] = Math.Max(Max[i], Mean[i]);
        }
        if (!double.IsFinite(CrestMin) || !double.IsFinite(CrestMax)) (CrestMin, CrestMax) = (0, 0);
        CrestMin = Math.Clamp(CrestMin, 0, 40);
        CrestMax = Math.Clamp(Math.Max(CrestMax, CrestMin), 0, 40);
        return true;
    }
}
