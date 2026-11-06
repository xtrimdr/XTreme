using System.Text;
using NAudio.Midi;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;
using NoteEvent = XtremeDaw.Audio.Dsp.NoteEvent;

namespace XtremeDaw.Lab;

/// <summary>
/// The native General MIDI instrument (#200), rows of comprobar-dsp and the command <c>gm</c>: a SoundFont written here
/// reads back with its zones added up, a note plays at the pitch of its key and loops while held, the release ends it,
/// the block size changes nothing, an exclusive class cuts the note before, and a MIDI file brings its programs, its
/// drums on channel 10 and its names into new tracks. With the app's bank there, a piano and a kit sound.
/// </summary>
static class GmChecks
{
    const int Sr = 48000;

    /// <summary><c>gm [presets] [--programa 0] [--bateria] [--nota 60] [--segundos 2] [--salida x.wav]</c>: lists the bank, or plays a note of it to a WAV.</summary>
    public static int Run(Args a)
    {
        if (a.Positional.FirstOrDefault() == "comprobar") return Check() ? 0 : 1;
        var font = GmBank.Load() ?? throw new ArgumentException($"No está el banco GM: {GmBank.Path} (corré tools\\get-gm.ps1 y compilá).");
        if (a.Positional.FirstOrDefault() == "presets")
        {
            Console.WriteLine($"{font.Name} · {font.Data.Length / 1_000_000.0:0.0} M muestras");
            foreach (var p in font.Presets) Console.WriteLine($"{p.Bank,4} {p.Program,4}  {p.Name,-22} {p.Regions.Length,4} zonas");
            return 0;
        }
        int program = (int)a.Double("programa", 0), note = (int)a.Double("nota", 60);
        bool drums = a.Flag("bateria");
        double sec = a.Double("segundos", 2);
        var preset = font.Find(program, drums) ?? throw new ArgumentException("El banco no tiene ese programa.");
        var (l, r) = Render(font, preset, [On(0, (long)(sec * Sr), note, 100)], (int)((sec + 2) * Sr));
        Console.WriteLine($"{preset.Name} · pico {l.Concat(r).Max(Math.Abs):0.000}");
        if (a.Get("salida") is { } path) Wav.Write(path, l, r, Sr);
        return 0;
    }

    public static bool Check()
    {
        Console.WriteLine();
        Console.WriteLine("General MIDI (instrumento nativo)");
        var font = SoundFont.Read(new MemoryStream(TestFont()));
        bool ok = CheckRead(font);
        ok &= CheckPitch(font);
        ok &= CheckLoopAndRelease(font);
        ok &= CheckBlocks(font);
        ok &= CheckExclusive(font);
        ok &= CheckTone(font);
        ok &= CheckFile();
        ok &= CheckBank();
        return ok;
    }

    static bool Row(string what, string value, string want, bool good)
    {
        Console.WriteLine($"{what,-44} {value,14} {want,12}  {(good ? "igual" : "DISTINTO")}");
        return good;
    }

    static NoteEvent On(long start, long length, int pitch, int velocity = 127) => new(start, length, NoteSynth.Hz(pitch), 1, pitch, velocity);

    static (float[] L, float[] R) Render(SoundFont font, SfPreset preset, NoteEvent[] notes, int frames, int block = 256, GmTone? tone = null)
    {
        var s = new GmSynth(Sr) { Notes = new NoteSequence(notes, Sr) };
        s.Set(font, preset, tone);
        var l = new float[frames];
        var r = new float[frames];
        for (int at = 0; at < frames; at += block)
        {
            int n = Math.Min(block, frames - at);
            s.Play(at, n, l.AsSpan(at, n), r.AsSpan(at, n));
        }
        return (l, r);
    }

    static double Rms(float[] x, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>Frequency by rising zero crossings over a stretch.</summary>
    static double Hz(float[] x, int from, int to)
    {
        int first = -1, last = -1, count = 0;
        for (int i = from + 1; i < to; i++)
            if (x[i - 1] <= 0 && x[i] > 0)
            {
                if (first < 0) first = i;
                last = i;
                count++;
            }
        return count < 2 ? 0 : (count - 1) * (double)Sr / (last - first);
    }

    // ---- the test bank: preset 0 (a looped 441 Hz sine on key 69, its preset zone adds 60 cB of attenuation) and kit 128:0,
    // two hi-hats of exclusive class 1 playing a one-shot burst
    /// <summary>441 Hz at 44.1 kHz is 100 samples a cycle: the loop holds 25 whole ones.</summary>
    const int SineRate = 44100, SineLen = 44100, LoopFrom = 1000, LoopTo = LoopFrom + 25 * 100;

    static byte[] TestFont()
    {
        var samples = new List<short>();
        int sineStart = samples.Count;
        for (int i = 0; i < SineLen; i++) samples.Add((short)(16000 * Math.Sin(2 * Math.PI * 441 * i / SineRate)));
        samples.AddRange(new short[46]);
        int hatStart = samples.Count;
        var rnd = new Random(7);
        for (int i = 0; i < 22050; i++) samples.Add((short)(rnd.Next(-12000, 12000) * (1 - i / 22050.0)));
        samples.AddRange(new short[46]);
        int hatEnd = hatStart + 22050;

        var shdr = new MemoryStream();
        void Sample(string name, int start, int end, int ls, int le, int pitch)
        {
            var w = new BinaryWriter(shdr);
            w.Write(Name20(name));
            w.Write((uint)start); w.Write((uint)end); w.Write((uint)ls); w.Write((uint)le);
            w.Write((uint)SineRate); w.Write((byte)pitch); w.Write((sbyte)0); w.Write((ushort)0); w.Write((ushort)1);
        }
        Sample("sine", sineStart, sineStart + SineLen, sineStart + LoopFrom, sineStart + LoopTo, 69);
        Sample("hat", hatStart, hatEnd, hatStart, hatEnd, 60);
        Sample("EOS", 0, 0, 0, 0, 0);

        // instruments: 0 = sine (looped, release 0.1 s), 1 = hats (closed key 42 and open key 46, class 1, one shot)
        var igen = new List<(ushort, short)>();
        var ibag = new List<ushort>();
        var inst = new List<(string, ushort)>();
        void Zone(params (ushort, short)[] gens) { ibag.Add((ushort)igen.Count); igen.AddRange(gens); }
        inst.Add(("Sine", 0));
        Zone((54, 1), (38, (short)(1200 * Math.Log2(0.1))), (53, 0));
        inst.Add(("Hats", (ushort)ibag.Count));
        Zone((43, (short)(42 | 42 << 8)), (57, 1), (53, 1));
        Zone((43, (short)(46 | 46 << 8)), (57, 1), (53, 1));
        inst.Add(("EOI", (ushort)ibag.Count));
        ibag.Add((ushort)igen.Count);

        var pgen = new List<(ushort, short)>();
        var pbag = new List<ushort>();
        var phdr = new List<(string, ushort, ushort, ushort)>();
        phdr.Add(("Sine", 0, 0, 0));
        pbag.Add((ushort)pgen.Count); pgen.Add((48, 60)); pgen.Add((41, 0));
        phdr.Add(("Kit", 0, 128, (ushort)pbag.Count));
        pbag.Add((ushort)pgen.Count); pgen.Add((41, 1));
        phdr.Add(("EOP", 0, 0, (ushort)pbag.Count));
        pbag.Add((ushort)pgen.Count);

        byte[] Gens(List<(ushort Op, short V)> g) { var m = new MemoryStream(); var w = new BinaryWriter(m); foreach (var (op, v) in g) { w.Write(op); w.Write(v); } w.Write((ushort)0); w.Write((short)0); return m.ToArray(); }
        byte[] Bags(List<ushort> b) { var m = new MemoryStream(); var w = new BinaryWriter(m); foreach (var x in b) { w.Write(x); w.Write((ushort)0); } return m.ToArray(); }
        byte[] Phdr() { var m = new MemoryStream(); var w = new BinaryWriter(m); foreach (var (n, p, b, bag) in phdr) { w.Write(Name20(n)); w.Write(p); w.Write(b); w.Write(bag); w.Write(0u); w.Write(0u); w.Write(0u); } return m.ToArray(); }
        byte[] Inst() { var m = new MemoryStream(); var w = new BinaryWriter(m); foreach (var (n, bag) in inst) { w.Write(Name20(n)); w.Write(bag); } return m.ToArray(); }
        byte[] Mods(int count) => new byte[10 * count];

        var smpl = new byte[samples.Count * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, smpl, 0, smpl.Length);
        var info = List("INFO", Chunk("ifil", [2, 0, 1, 0]), Chunk("INAM", Encoding.ASCII.GetBytes("Prueba\0\0")));
        var sdta = List("sdta", Chunk("smpl", smpl));
        var pdta = List("pdta", Chunk("phdr", Phdr()), Chunk("pbag", Bags(pbag)), Chunk("pmod", Mods(1)), Chunk("pgen", Gens(pgen)),
            Chunk("inst", Inst()), Chunk("ibag", Bags(ibag)), Chunk("imod", Mods(1)), Chunk("igen", Gens(igen)), Chunk("shdr", shdr.ToArray()));
        var body = Concat(Encoding.ASCII.GetBytes("sfbk"), info, sdta, pdta);
        return Concat(Encoding.ASCII.GetBytes("RIFF"), BitConverter.GetBytes(body.Length), body);
    }

    static byte[] Name20(string s)
    {
        var b = new byte[20];
        Encoding.ASCII.GetBytes(s, 0, Math.Min(19, s.Length), b, 0);
        return b;
    }

    static byte[] Chunk(string id, byte[] data) => Concat(Encoding.ASCII.GetBytes(id), BitConverter.GetBytes(data.Length), data, data.Length % 2 == 1 ? [0] : []);
    static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", Concat([Encoding.ASCII.GetBytes(type), .. chunks]));
    static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    static bool CheckRead(SoundFont font)
    {
        var sine = font.Find(0, false);
        var kit = font.Find(0, true);
        var missing = font.Find(40, false);
        bool ok = sine is { Name: "Sine", Regions: [{ Loop: SfLoop.Always, Root: 69 } r] } && Math.Abs(r.Gain - Math.Pow(10, -60 * 0.4 / 200)) < 1e-4
            && Math.Abs(r.Release - 0.1) < 0.002 && kit is { Bank: 128, Regions.Length: 2 } && kit.Regions.All(z => z.Exclusive == 1 && z.KeyLo == z.KeyHi)
            && missing == sine;
        return Row("GM: leer un SF2 (zonas, generadores, faltantes)", $"{font.Presets.Count()} presets", "2", ok);
    }

    static bool CheckPitch(SoundFont font)
    {
        var sine = font.Find(0, false)!;
        var (l, _) = Render(font, sine, [On(0, Sr, 69), On(Sr, Sr, 81)], 2 * Sr);
        // the sample is 441 Hz at its own rate: key 69 plays it as it is, key 81 an octave up
        double a = Hz(l, Sr / 4, Sr / 2), b = Hz(l, Sr + Sr / 4, Sr + Sr / 2);
        bool ok = Math.Abs(a - 441) < 1 && Math.Abs(b - 882) < 2;
        return Row("GM: la tecla da el tono (69 y 81)", $"{a:0.0} / {b:0.0} Hz", "441 / 882", ok);
    }

    static bool CheckLoopAndRelease(SoundFont font)
    {
        var sine = font.Find(0, false)!;
        // held 3 s, past the sample's 1 s: the loop keeps it sounding; 0.1 s of release ends it
        var (l, _) = Render(font, sine, [On(0, 3 * Sr, 69, 127)], 4 * Sr);
        double held = Rms(l, (int)(2.5 * Sr), 3 * Sr), after = Rms(l, (int)(3.3 * Sr), 4 * Sr);
        double want = 16000 / 32768.0 * Math.Pow(10, -60 * 0.4 / 200) * GmSynth.Headroom / Math.Sqrt(2);
        bool ok = Math.Abs(held - want) < want * 0.02 && after < 1e-5;
        return Row("GM: loop sostenido y release", $"{held:0.0000}", $"{want:0.0000}", ok);
    }

    /// <summary>The tone over the preset (#200): transpose, velocity, release, filter, Mono and its glide, in any block.</summary>
    static bool CheckTone(SoundFont font)
    {
        var sine = font.Find(0, false)!;
        var (up, _) = Render(font, sine, [On(0, Sr, 69)], Sr, tone: new GmTone { Transpose = 12 });
        double hz = Hz(up, Sr / 4, Sr / 2);
        bool ok = Row("GM tono: transponer +12", $"{hz:0.0} Hz", "882", Math.Abs(hz - 882) < 2);

        var flat = new GmTone { Velocity = 0 };
        var (soft, _) = Render(font, sine, [On(0, Sr, 69, 32)], Sr, tone: flat);
        var (hard, _) = Render(font, sine, [On(0, Sr, 69, 127)], Sr, tone: flat);
        double s = Rms(soft, Sr / 2, Sr), h = Rms(hard, Sr / 2, Sr);
        ok &= Row("GM tono: sensibilidad 0, suave = fuerte", $"{s / h:0.000}", "1.000", Math.Abs(s / h - 1) < 1e-3);

        // +0.5 adds 2.5 s to the bank's 0.1 s release: 0.3 s after the note it still sounds
        var (bank, _) = Render(font, sine, [On(0, Sr, 69)], 2 * Sr);
        var (longer, _) = Render(font, sine, [On(0, Sr, 69)], 2 * Sr, tone: new GmTone { Release = 0.5 });
        double was = Rms(bank, (int)(1.3 * Sr), (int)(1.5 * Sr)), now = Rms(longer, (int)(1.3 * Sr), (int)(1.5 * Sr));
        ok &= Row("GM tono: liberación más larga", $"{now:0.0000}", "> 0.01", was < 1e-5 && now > 0.01);

        // all the way down, and softest: the 441 Hz sine under a low pass near 80 Hz
        var (dark, _) = Render(font, sine, [On(0, Sr, 69, 1)], Sr, tone: new GmTone { Bright = -1, VelBright = 1, Velocity = 0 });
        var (open, _) = Render(font, sine, [On(0, Sr, 69, 1)], Sr, tone: flat);
        double ratio = Rms(dark, Sr / 2, Sr) / Rms(open, Sr / 2, Sr);
        ok &= Row("GM tono: brillo cierra el filtro", $"{ratio:0.000}", "< 0.1", ratio < 0.1);

        // Mono: the second note cuts the first, so only 882 Hz is left; with glide, halfway it is half an octave up
        NoteEvent[] tied = [On(0, Sr, 69), On(Sr / 2, Sr, 81)];
        var (mono, _) = Render(font, sine, tied, 2 * Sr, tone: new GmTone { Mono = true });
        var (alone, _) = Render(font, sine, [On(Sr / 2, Sr, 81)], 2 * Sr);
        double rest = 0;
        for (int i = Sr / 2 + 300; i < Sr; i++) rest = Math.Max(rest, Math.Abs(mono[i] - alone[i]));
        ok &= Row("GM tono: mono corta la nota anterior", $"{rest:0.0000}", "< 1e-6", rest < 1e-6);

        var glide = new GmTone { Mono = true, GlideSec = 0.5 };
        var (slid, _) = Render(font, sine, tied, 2 * Sr, tone: glide);
        double mid = Hz(slid, Sr / 2 + Sr / 5, Sr / 2 + 3 * Sr / 10);
        ok &= Row("GM tono: glide a mitad de camino", $"{mid:0.0} Hz", "~624", Math.Abs(mid - 623.7) < 15);
        var (a, _) = Render(font, sine, tied, 2 * Sr, 64, glide);
        var (b, _) = Render(font, sine, tied, 2 * Sr, 509, glide);
        bool same = a.AsSpan().SequenceEqual(b);
        ok &= Row("GM tono: glide en cualquier bloque", same ? "igual" : "distinto", "igual", same);
        return ok;
    }

    static bool CheckBlocks(SoundFont font)
    {
        var sine = font.Find(0, false)!;
        NoteEvent[] notes = [On(0, Sr / 2, 60, 90), On(Sr / 4, Sr, 67, 50), On(Sr / 3, Sr / 5, 74)];
        var (a, _) = Render(font, sine, notes, 2 * Sr, 64);
        var (b, _) = Render(font, sine, notes, 2 * Sr, 509);
        bool same = a.AsSpan().SequenceEqual(b);
        return Row("GM: cualquier bloque da lo mismo", same ? "igual" : "distinto", "igual", same);
    }

    static bool CheckExclusive(SoundFont font)
    {
        var kit = font.Find(0, true)!;
        // the open hi-hat rings; a closed one 0.1 s later cuts it in 5 ms
        var (alone, _) = Render(font, kit, [On(0, Sr, 46)], Sr);
        var (cut, _) = Render(font, kit, [On(0, Sr, 46), On(Sr / 10, Sr, 42)], Sr);
        var (closed, _) = Render(font, kit, [On(Sr / 10, Sr, 42)], Sr);
        double rest = 0;
        for (int i = Sr / 10 + 300; i < Sr / 2; i++) rest = Math.Max(rest, Math.Abs(cut[i] - closed[i]));
        bool ok = Rms(alone, Sr / 5, Sr / 2) > 0.01 && rest < 1e-6;
        return Row("GM: clase exclusiva (hi-hat cerrado corta)", $"{rest:0.000000}", "0", ok);
    }

    static bool CheckFile()
    {
        string dir = Directory.CreateTempSubdirectory("xtreme-gm").FullName;
        try
        {
            var events = new MidiEventCollection(1, 480);
            events.AddTrack();
            events.AddEvent(new TempoEvent(500000, 0), 0);
            events.AddTrack();
            events.AddEvent(new TextEvent("Guitarra", MetaEventType.SequenceTrackName, 0), 1);
            events.AddEvent(new PatchChangeEvent(0, 1, 25), 1);
            events.AddEvent(new NAudio.Midi.NoteEvent(0, 1, MidiCommandCode.NoteOn, 64, 90), 1);
            events.AddEvent(new NAudio.Midi.NoteEvent(480, 1, MidiCommandCode.NoteOff, 64, 0), 1);
            events.AddTrack();
            events.AddEvent(new NAudio.Midi.NoteEvent(0, 10, MidiCommandCode.NoteOn, 36, 100), 2);
            events.AddEvent(new NAudio.Midi.NoteEvent(240, 10, MidiCommandCode.NoteOff, 36, 0), 2);
            events.AddEvent(new NAudio.Midi.NoteEvent(480, 10, MidiCommandCode.NoteOn, 38, 100), 2);
            events.AddEvent(new NAudio.Midi.NoteEvent(720, 10, MidiCommandCode.NoteOff, 38, 0), 2);
            events.AddTrack();
            events.AddEvent(new PatchChangeEvent(0, 3, 33), 0);
            events.AddEvent(new NAudio.Midi.NoteEvent(0, 3, MidiCommandCode.NoteOn, 40, 80), 3);
            events.AddEvent(new NAudio.Midi.NoteEvent(960, 3, MidiCommandCode.NoteOff, 40, 0), 3);
            for (int t = 0; t < 4; t++) events.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, 1920), t);
            string path = Path.Combine(dir, "cancion.mid");
            MidiFile.Export(path, events);
            var data = MidiFiles.Read(path);
            bool read = data.Tracks is [{ Name: "Guitarra", Program: 25, Drums: false }, { Name: "Batería", Drums: true }, { Name: "Bajo eléctrico (dedos)", Program: 33 }];
            var p = new Project();
            var clips = MidiFiles.Import(p, data, null, 0, [1, 2]);
            bool picked = clips.Count == 2 && p.Tracks.Count == 2 && p.Tracks[0] is { Name: "Batería", Gm: { Drums: true, Program: 0 } } t0
                && t0.KeyNames is { } keys && keys[36] == "Bombo" && keys[38] == "Caja" && p.Tracks[1] is { Gm: { Program: 33, Drums: false } };
            return Row("GM: .mid con programas, batería y nombres", $"{data.Tracks.Count} pistas, {clips.Count} elegidas", "3, 2", read && picked);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>With the app's bank there: a piano and a kit sound, quietly enough to leave room.</summary>
    static bool CheckBank()
    {
        if (GmBank.Load() is not { } font)
        {
            Console.WriteLine($"{"GM: banco de la app",-44} {"no está",14} {"",12}  (tools\\get-gm.ps1)");
            return true;
        }
        var piano = font.Find(0, false);
        var kit = font.Find(0, true);
        if (piano == null || kit == null) return Row("GM: banco de la app (piano y batería)", "faltan", "están", false);
        var (pl, _) = Render(font, piano, [On(0, Sr, 60, 100)], 2 * Sr);
        // a dense chord on the strings: what a block costs, and nothing allocated on the audio thread
        var strings = font.Find(48, false)!;
        var chord = Enumerable.Range(0, 24).Select(k => On(k * 20, 4 * Sr, 36 + 2 * k, 90)).ToArray();
        var synth = new GmSynth(Sr) { Notes = new NoteSequence(chord, Sr) };
        synth.Set(font, strings);
        var bl = new float[256];
        var br = new float[256];
        synth.Play(0, 256, bl, br);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int blocks = 4 * Sr / 256;
        for (int b = 1; b < blocks; b++) synth.Play(b * 256L, 256, bl, br);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double load = clock.Elapsed.TotalSeconds / (blocks * 256.0 / Sr) * 100;
        bool quiet = Row("GM: 24 notas de cuerdas (carga, memoria)", $"{load:0.0}% · {allocated} B", "0 B", allocated == 0);
        var (kl, _) = Render(font, kit, [On(0, Sr / 4, 38, 100)], Sr);
        double p = pl.Max(Math.Abs), k = kl.Max(Math.Abs);
        bool ok = p is > 0.02 and < 1 && k is > 0.02 and < 1;
        return Row("GM: banco de la app (piano y batería)", $"{p:0.00} / {k:0.00}", "sonando", ok) & quiet;
    }
}
