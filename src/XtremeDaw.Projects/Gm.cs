using System.Text.Json;
using System.Text.Json.Serialization;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>
/// The native General MIDI instrument of a MIDI track (<see cref="Track.Gm"/>, asked by the user, #200): which of the
/// 128 GM programs it plays, or a drum kit (what channel 10 of a MIDI file is). The sound is the app's bank
/// (<see cref="GmBank"/>), so the project keeps those two numbers and the tone over it (asked by the user, #200: it had
/// no controls), the fields of <see cref="GmTone"/> with the same meaning; every one at its default plays the bank as it is.
/// The tone stays when the program changes. A kit doesn't transpose (it would swap drums, not tune them).
/// <see cref="Bank"/> is which bank it plays (asked by the user, #212): the shipped one, a downloaded one or a .sf2 of the
/// user; one that isn't on this PC plays the shipped bank meanwhile and is kept, so the project sounds right again where it is.
/// </summary>
public sealed class GmSettings
{
    /// <summary>The GM program, 0..127 (0 = Piano acústico); with <see cref="Drums"/>, the kit (0 = Estándar).</summary>
    public int Program { get; set; }
    /// <summary>The bank (<see cref="GmBank.Key"/>): null = GeneralUser GS, an id of <see cref="GmBank.Downloads"/> or a .sf2's whole path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Bank { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Drums { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Attack { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Decay { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Sustain { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Release { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Bright { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Resonance { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double VelBright { get; set; }
    public double Velocity { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Transpose { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Fine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Mono { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double GlideSec { get; set; }
    /// <summary>The instrument's volume in dB (asked by the user, #212), <see cref="GmTone.MinVolumeDb"/> = silence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double VolumeDb { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Every value back in its range, what <see cref="Track"/> does on opening and the window after each edit.</summary>
    public void Normalize()
    {
        Program = Math.Clamp(Program, 0, 127);
        Bank = GmBank.IsDefault(Bank) ? null : GmBank.IsFile(Bank) ? Bank!.Trim() : GmBank.Key(Bank);
        Attack = Range(Attack, -1, 1);
        Decay = Range(Decay, -1, 1);
        Sustain = Range(Sustain, -1, 1);
        Release = Range(Release, -1, 1);
        Bright = Range(Bright, -1, 1);
        Resonance = Range(Resonance, 0, 1);
        VelBright = Range(VelBright, 0, 1);
        Velocity = double.IsFinite(Velocity) ? Math.Clamp(Velocity, 0, 1) : 1;
        Transpose = Math.Clamp(Transpose, -GmTone.MaxTranspose, GmTone.MaxTranspose);
        Fine = Range(Fine, -100, 100);
        GlideSec = Range(GlideSec, 0, GmTone.MaxGlideSec);
        VolumeDb = Range(VolumeDb, GmTone.MinVolumeDb, GmTone.MaxVolumeDb);
    }

    static double Range(double v, double min, double max) => double.IsFinite(v) ? Math.Clamp(v, min, max) : 0;

    /// <summary>The tone back to the bank's, keeping the program and the volume.</summary>
    public void ResetTone()
    {
        Attack = Decay = Sustain = Release = Bright = Resonance = VelBright = Fine = GlideSec = 0;
        Velocity = 1;
        Transpose = 0;
        Mono = false;
    }

    /// <summary>What the engine plays over the preset.</summary>
    public GmTone Tone() => new()
    {
        Attack = Attack, Decay = Decay, Sustain = Sustain, Release = Release, Bright = Bright, Resonance = Resonance,
        VelBright = VelBright, Velocity = Velocity, Transpose = Drums ? 0 : Transpose, Fine = Fine, Mono = Mono, GlideSec = GlideSec, VolumeDb = VolumeDb,
    };

    /// <summary>What the export plays it with (the shipped bank while its own isn't there); null when no bank is.</summary>
    public GmRig? Rig() => GmBank.LoadOrDefault(Bank) is { } font && font.Find(Program, Drums) is { } preset ? new GmRig(font, preset, Tone()) : null;

    /// <summary>Its name as the menus show it ("Piano acústico", "Batería").</summary>
    public string Name => Drums ? GmNames.Kit(Program) : GmNames.Program(Program);
}

/// <summary>
/// The names of the GM programs, their 16 families of 8, and the GS drum kits, translated through <see cref="Texts"/>
/// (keys <c>gm.program.&lt;n&gt;</c>, <c>gm.family.&lt;n&gt;</c>, <c>gm.kit.&lt;n&gt;</c>). The Spanish here is the reference.
/// </summary>
public static class GmNames
{
    static readonly string[] Programs =
    [
        "Piano acústico", "Piano brillante", "Piano eléctrico de cola", "Piano honky-tonk", "Piano eléctrico 1", "Piano eléctrico 2", "Clave", "Clavinet",
        "Celesta", "Glockenspiel", "Caja de música", "Vibráfono", "Marimba", "Xilófono", "Campanas tubulares", "Dulcémele",
        "Órgano de barras", "Órgano percusivo", "Órgano de rock", "Órgano de iglesia", "Armonio", "Acordeón", "Armónica", "Bandoneón",
        "Guitarra de nylon", "Guitarra de acero", "Guitarra eléctrica jazz", "Guitarra eléctrica limpia", "Guitarra eléctrica muteada", "Guitarra saturada", "Guitarra distorsionada", "Armónicos de guitarra",
        "Bajo acústico", "Bajo eléctrico (dedos)", "Bajo eléctrico (púa)", "Bajo sin trastes", "Bajo slap 1", "Bajo slap 2", "Bajo sintetizado 1", "Bajo sintetizado 2",
        "Violín", "Viola", "Violonchelo", "Contrabajo", "Cuerdas en trémolo", "Cuerdas en pizzicato", "Arpa", "Timbales sinfónicos",
        "Cuerdas 1", "Cuerdas 2", "Cuerdas sintetizadas 1", "Cuerdas sintetizadas 2", "Coro aahs", "Voz oohs", "Voz sintetizada", "Golpe de orquesta",
        "Trompeta", "Trombón", "Tuba", "Trompeta con sordina", "Corno francés", "Sección de metales", "Metales sintetizados 1", "Metales sintetizados 2",
        "Saxo soprano", "Saxo alto", "Saxo tenor", "Saxo barítono", "Oboe", "Corno inglés", "Fagot", "Clarinete",
        "Flautín", "Flauta", "Flauta dulce", "Flauta de pan", "Botella soplada", "Shakuhachi", "Silbido", "Ocarina",
        "Lead cuadrada", "Lead sierra", "Lead calíope", "Lead chiff", "Lead charang", "Lead voz", "Lead quintas", "Lead bajo y lead",
        "Pad new age", "Pad cálido", "Pad polisintetizador", "Pad coro", "Pad arco", "Pad metálico", "Pad halo", "Pad barrido",
        "FX lluvia", "FX banda sonora", "FX cristal", "FX atmósfera", "FX brillo", "FX duendes", "FX ecos", "FX ciencia ficción",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Gaita", "Violín folk", "Shanai",
        "Campanilla", "Agogó", "Steel drums", "Bloque de madera", "Taiko", "Tom melódico", "Tambor sintetizado", "Platillo invertido",
        "Ruido de trastes", "Respiración", "Mar", "Pájaros", "Teléfono", "Helicóptero", "Aplausos", "Disparo",
    ];

    static readonly string[] Families =
    [
        "Pianos", "Percusión cromática", "Órganos", "Guitarras", "Bajos", "Cuerdas solistas", "Ensambles", "Metales",
        "Lengüetas", "Vientos", "Leads", "Pads", "Efectos de sintetizador", "Étnicos", "Percusión", "Efectos de sonido",
    ];

    /// <summary>The GS drum kits a GM bank usually has, by program.</summary>
    public static readonly (int Program, string Name)[] Kits =
    [
        (0, "Estándar"), (8, "Sala"), (16, "Potente"), (24, "Electrónica"), (25, "TR-808"), (32, "Jazz"), (40, "Escobillas"), (48, "Orquesta"),
    ];

    static readonly string?[] programNames = new string?[128];

    /// <summary>The program's name, kept once made: the timeline draws it every frame.</summary>
    public static string Program(int program)
    {
        program = Math.Clamp(program, 0, 127);
        return programNames[program] ??= Texts.T("gm.program." + program, Programs[program]);
    }

    public static string Family(int family)
    {
        family = Math.Clamp(family, 0, 15);
        return Texts.T("gm.family." + family, Families[family]);
    }

    static readonly string?[] kitNames = new string?[128];

    /// <summary>"Batería" for the standard kit, "Batería · Jazz" for another. Kept once made: the timeline draws it every frame.</summary>
    public static string Kit(int program)
    {
        program = Math.Clamp(program, 0, 127);
        if (kitNames[program] is { } made) return made;
        string drums = Texts.T("gm.drums", "Batería");
        return kitNames[program] = program == 0 ? drums : $"{drums} · {KitOnly(program)}";
    }

    /// <summary>The GM percussion map, keys 35 (B1) to 81.</summary>
    const int FirstDrumKey = 35;
    static readonly string[] DrumKeys =
    [
        "Bombo acústico", "Bombo", "Aro (side stick)", "Caja", "Palmas", "Caja eléctrica", "Tom de piso grave", "Hi-hat cerrado",
        "Tom de piso agudo", "Hi-hat con pedal", "Tom grave", "Hi-hat abierto", "Tom medio grave", "Tom medio agudo", "Crash 1", "Tom agudo",
        "Ride 1", "Platillo chino", "Campana del ride", "Pandereta", "Splash", "Cencerro", "Crash 2", "Vibraslap",
        "Ride 2", "Bongó agudo", "Bongó grave", "Conga aguda muteada", "Conga aguda abierta", "Conga grave", "Timbal agudo", "Timbal grave",
        "Agogó agudo", "Agogó grave", "Cabasa", "Maracas", "Silbato corto", "Silbato largo", "Güiro corto", "Güiro largo",
        "Claves", "Bloque agudo", "Bloque grave", "Cuíca muteada", "Cuíca abierta", "Triángulo muteado", "Triángulo abierto",
    ];

    /// <summary>The name of a key of a GM kit ("Caja", "Hi-hat cerrado"); null off the map.</summary>
    public static string? DrumKey(int pitch)
    {
        int i = pitch - FirstDrumKey;
        return i >= 0 && i < DrumKeys.Length ? Texts.T("gm.key." + pitch, DrumKeys[i]) : null;
    }

    public static string KitOnly(int program)
    {
        foreach (var (p, name) in Kits)
            if (p == program) return Texts.T("gm.kit." + p, name);
        return (program + 1).ToString();
    }
}
