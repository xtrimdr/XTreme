using System.Text.Json;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The effect presets (#141), rows of comprobar-dsp: every native effect starts at its Default, a preset puts its
/// settings on a slot and leaves what is the slot's (id, ON, sidechain, window), a saved preset reads back the same, and
/// nothing new starts at a guitar's compressor. Without touching the user's saved presets.
/// </summary>
static class FxPresetChecks
{
    /// <summary>Every native kind a preset can be of.</summary>
    static readonly string[] Kinds =
    [
        InsertSlot.SatKind, InsertSlot.CompKind, InsertSlot.EqKind, InsertSlot.ReverbKind, InsertSlot.DelayKind, InsertSlot.GateKind,
        TrackInserts.Clean, TrackInserts.Bands, TrackInserts.Color, TrackInserts.Tape, TrackInserts.Width, TrackInserts.Clip, TrackInserts.Limit,
        TrackInserts.Chorus, TrackInserts.Flanger, TrackInserts.Phaser, TrackInserts.Tremolo,
        TrackInserts.Fet, TrackInserts.Opto, TrackInserts.BusComp, TrackInserts.Vca, TrackInserts.VariMu, TrackInserts.Pump,
    ];

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        int fresh = Kinds.Count(k => FxPresets.Same(InsertSlot.New(k), InsertSlot.New(k)));
        Row("Presets: Default igual a uno nuevo", $"{fresh}/{Kinds.Length}", "todos", fresh == Kinds.Length);

        int applied = 0, kept = 0, told = 0, back = 0;
        foreach (string kind in Kinds)
        {
            // a preset: the first knob moved (or the mix, on a kind without knobs)
            var preset = InsertSlot.New(kind);
            if (FxDefs.Params(kind) is [var p, ..])
            {
                double v = p.Get(preset);
                p.Set(preset, p.Snap(Math.Abs(v - p.Max) > 1e-9 ? p.Max : p.Min));
            }
            else preset.Wet = 0.4;
            preset.OutputDb = -3;
            var slot = InsertSlot.New(kind);
            string id = slot.Id;
            slot.On = false;
            slot.Full = true;
            if (TrackInserts.TakesKey(kind)) slot.Sidechain = "trk_key";
            if (!FxPresets.Same(slot, preset)) told++;
            FxPresets.Apply(slot, preset);
            if (FxPresets.Same(slot, preset)) applied++;
            if (slot.Id == id && !slot.On && slot.Full && (!TrackInserts.TakesKey(kind) || slot.Sidechain == "trk_key")) kept++;
            var saved = new FxPreset { Name = "x", Kind = kind, Slot = preset };
            var read = JsonSerializer.Deserialize<FxPreset>(JsonSerializer.Serialize(saved, ProjectStore.Json), ProjectStore.Json)!;
            if (FxPresets.Same(slot, read.Slot)) back++;
        }
        Row("Presets: distinguen un cambio", $"{told}/{Kinds.Length}", "todos", told == Kinds.Length);
        Row("Presets: se aplican", $"{applied}/{Kinds.Length}", "todos", applied == Kinds.Length);
        Row("Presets: el slot sigue siendo el mismo", $"{kept}/{Kinds.Length}", "todos", kept == Kinds.Length);
        Row("Presets: guardado y leído, igual", $"{back}/{Kinds.Length}", "todos", back == Kinds.Length);

        // nothing starts at a guitar's compressor anymore: a new one, a new channel and a recorded lane
        bool neutral = new CompSettings().Preset == CompPresets.DefaultId && new Channel().Comp.Preset == CompPresets.DefaultId
            && TakeRecorder.DefaultCompPreset(1) == CompPresets.DefaultId && CompPresets.All.Any(p => p.Id == CompPresets.DefaultId);
        Row("Presets: el compresor arranca en Default", neutral ? "Default" : "otro", "Default", neutral);
        return allOk;
    }
}
