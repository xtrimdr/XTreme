using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XtremeDaw.Audio;

namespace XtremeDaw.Projects;

/// <summary>
/// A native effect's settings saved by the user under a name (asked by the user, #141): one kind each, the whole slot
/// but what belongs to where it sits (its id, ON, sidechain and how its window shows).
/// </summary>
public sealed class FxPreset
{
    public string Id { get; set; } = Ids.New("fxp");
    public string Name { get; set; } = "";
    /// <summary>The effect it is for (<see cref="InsertSlot.Type"/>).</summary>
    public string Kind { get; set; } = "";
    public InsertSlot Slot { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// The user's effect presets, program-wide in %APPDATA%\XTreme\efectos.json (every project sees them), written
/// atomically like <see cref="TrackPresets"/>. Also what an effect's Default and a saved preset share: putting settings on
/// a slot (<see cref="Apply"/>) and telling whether a slot sounds as some settings (<see cref="Same"/>).
/// </summary>
public static class FxPresets
{
    static string FilePath => Path.Combine(AppConfig.Dir, "efectos.json");

    /// <summary>Every saved preset, in the order they were saved; a broken or missing file is an empty list.</summary>
    public static List<FxPreset> All()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<FxPreset>>(File.ReadAllText(FilePath, Encoding.UTF8), ProjectStore.Json) ?? [];
            list.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name) || !Audio.Dsp.TrackInserts.IsKind(p.Kind) || p.Kind == InsertSlot.VstKind);
            foreach (var p in list)
            {
                if (string.IsNullOrEmpty(p.Id)) p.Id = Ids.New("fxp");
                p.Slot = Bare(p.Slot ?? InsertSlot.New(p.Kind), p.Kind);
            }
            return list;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The saved presets of one kind of effect, by name.</summary>
    public static List<FxPreset> Of(string kind) => [.. All().Where(p => p.Kind == kind).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Saves a slot's settings as a preset; one of the same kind and name (any case) is replaced. Returns it.
    /// </summary>
    public static FxPreset Save(InsertSlot slot, string name)
    {
        name = ProjectLibrary.CleanName(name);
        if (name.Length == 0) throw new ArgumentException(Texts.T("err.preset.name", "Falta el nombre."));
        var preset = new FxPreset { Name = name, Kind = slot.Type, Slot = Bare(slot, slot.Type) };
        var list = All();
        var existing = list.Find(x => x.Kind == slot.Type && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            preset.Id = existing.Id;
            list[list.IndexOf(existing)] = preset;
        }
        else list.Add(preset);
        Write(list);
        return preset;
    }

    public static void Delete(string id)
    {
        var list = All();
        if (list.RemoveAll(p => p.Id == id) > 0) Write(list);
    }

    /// <summary>Whether a preset of this kind already has the name (any case).</summary>
    public static bool Exists(string kind, string name) =>
        All().Exists(p => p.Kind == kind && string.Equals(p.Name, ProjectLibrary.CleanName(name), StringComparison.OrdinalIgnoreCase));

    static void Write(List<FxPreset> list) => AppConfig.WriteAtomic(FilePath, JsonSerializer.Serialize(list, ProjectStore.Json));

    /// <summary>A copy of the slot's settings alone: what a preset keeps and what <see cref="Same"/> compares.</summary>
    static InsertSlot Bare(InsertSlot slot, string kind)
    {
        var s = ProjectLibrary.Clone(slot);
        s.Type = kind;
        s.Id = "";
        s.Sidechain = null;
        s.Collapsed = s.Full = false;
        s.Normalize();
        // On runs Normalize, which gives an empty id a new one: the id goes last
        s.On = true;
        s.Id = "";
        return s;
    }

    /// <summary>
    /// Puts the settings of <paramref name="from"/> (a preset's or a Default's) on <paramref name="target"/>, of the same
    /// kind: what it is where it sits (id, ON, sidechain, its window) stays.
    /// </summary>
    public static void Apply(InsertSlot target, InsertSlot from)
    {
        if (from.Type != target.Type) return;
        var src = ProjectLibrary.Clone(from);
        bool on = target.On;
        target.Sat = src.Sat;
        target.Comp = src.Comp;
        target.Eq = src.Eq;
        target.Reverb = src.Reverb;
        target.Delay = src.Delay;
        target.Gate = src.Gate;
        target.Stage = src.Stage;
        target.Tonal = src.Tonal;
        target.Wet = src.Wet;
        target.Dry = src.Dry;
        target.OutputDb = src.OutputDb;
        target.Normalize();
        target.On = on;
    }

    /// <summary>Whether a slot has exactly these settings (a preset's, or a Default's), whatever its ON, id or place.</summary>
    public static bool Same(InsertSlot slot, InsertSlot settings) =>
        slot.Type == settings.Type
        && JsonSerializer.Serialize(Bare(slot, slot.Type), ProjectStore.Json) == JsonSerializer.Serialize(Bare(settings, settings.Type), ProjectStore.Json);
}
