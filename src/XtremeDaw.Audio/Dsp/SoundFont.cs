using System.Text;

namespace XtremeDaw.Audio.Dsp;

/// <summary>What a region does once its sample reaches the loop: 0 plays through, 1 loops always, 3 loops until the key is let go.</summary>
public enum SfLoop : byte { None = 0, Always = 1, UntilRelease = 3 }

/// <summary>
/// One sample of a preset as the synth plays it: the preset's and the instrument's generators already added (SF2 2.04,
/// section 9.4), times in seconds, levels as gains, so a voice only reads numbers. Indices are into <see cref="SoundFont.Data"/>.
/// </summary>
public sealed class SfRegion
{
    public int KeyLo, KeyHi = 127, VelLo, VelHi = 127;
    public int Start, End, LoopStart, LoopEnd;
    public SfLoop Loop;
    public int SampleRate = 44100;
    /// <summary>The key that plays the sample as it is.</summary>
    public int Root = 60;
    /// <summary>Coarse, fine and the sample's own correction, in cents.</summary>
    public double TuneCents;
    /// <summary>Cents per key, 100 = a semitone; 0 = every key the same pitch (some drums).</summary>
    public double ScaleTuning = 100;
    /// <summary>The key that plays it whatever key was struck; -1 = the key struck.</summary>
    public int FixedKey = -1;
    /// <summary>Its level before the velocity (initial attenuation as a gain).</summary>
    public float Gain = 1;
    /// <summary>-1 (left) .. 1 (right).</summary>
    public float Pan;
    /// <summary>Volume envelope: seconds, the sustain as a level in centibels down from the peak.</summary>
    public double Delay, Attack, Hold, Decay, SustainCb, Release;
    /// <summary>Timecents per key away from 60 on hold and decay (keynumToVolEnvHold/Decay).</summary>
    public double HoldPerKey, DecayPerKey;
    /// <summary>Modulation envelope: seconds, the sustain as 0..1.</summary>
    public double ModDelay, ModAttack, ModHold, ModDecay, ModSustain = 1, ModRelease;
    public double ModEnvToPitch, ModEnvToCutoff;
    /// <summary>Low pass cutoff in absolute cents; at or over <see cref="SoundFont.OpenCutoff"/> the filter is off.</summary>
    public double Cutoff = SoundFont.OpenCutoff;
    /// <summary>Resonance in centibels over flat.</summary>
    public double Resonance;
    /// <summary>Vibrato LFO (pitch only) and modulation LFO: delay in seconds, rate in Hz, depths in cents / centibels.</summary>
    public double VibDelay, VibHz = 8.176, VibToPitch;
    public double ModLfoDelay, ModLfoHz = 8.176, ModLfoToPitch, ModLfoToCutoff, ModLfoToVolume;
    /// <summary>A new note of the same class cuts this one (open and closed hi-hats); 0 = none.</summary>
    public int Exclusive;

    public bool Covers(int key, int velocity) => key >= KeyLo && key <= KeyHi && velocity >= VelLo && velocity <= VelHi;
}

/// <summary>A preset (an instrument of the GM set, or a drum kit on bank 128) and every region it plays.</summary>
public sealed class SfPreset(string name, int bank, int program, SfRegion[] regions)
{
    public string Name { get; } = name;
    public int Bank { get; } = bank;
    public int Program { get; } = program;
    public SfRegion[] Regions { get; } = regions;
}

/// <summary>
/// A SoundFont 2 bank read whole into memory: its samples as one float array and its presets flattened to regions, so
/// the audio thread never walks generators. Read once (it is tens of MB) and shared by every track that plays it, live
/// and in the export (<see cref="GmBank"/>). Immutable after <see cref="Read(string)"/>. Only what a GM player needs is
/// kept: modulators other than the defaults (velocity to level) are ignored, and so are ROM samples and 24-bit extras.
/// </summary>
public sealed class SoundFont
{
    /// <summary>Cents at which the low pass is fully open (about 20 kHz): the generator's default.</summary>
    public const double OpenCutoff = 13500;
    /// <summary>The bank drum kits live on.</summary>
    public const int DrumBank = 128;
    /// <summary>Each sample is followed by at least 46 silent ones (SF2 7.10): a read past its end stays inside the array.</summary>
    const int Pad = 4;

    // generator numbers (SF2 8.1.2)
    const int GStartOffset = 0, GEndOffset = 1, GLoopStartOffset = 2, GLoopEndOffset = 3, GStartCoarse = 4, GModLfoToPitch = 5,
        GVibLfoToPitch = 6, GModEnvToPitch = 7, GCutoff = 8, GResonance = 9, GModLfoToCutoff = 10, GModEnvToCutoff = 11,
        GEndCoarse = 12, GModLfoToVolume = 13, GPan = 17, GModLfoDelay = 21, GModLfoFreq = 22, GVibDelay = 23, GVibFreq = 24,
        GModDelay = 25, GModAttack = 26, GModHold = 27, GModDecay = 28, GModSustain = 29, GModRelease = 30,
        GDelay = 33, GAttack = 34, GHold = 35, GDecay = 36, GSustain = 37, GRelease = 38, GKeyToHold = 39, GKeyToDecay = 40,
        GInstrument = 41, GKeyRange = 43, GVelRange = 44, GLoopStartCoarse = 45, GKeynum = 46, GAttenuation = 48,
        GLoopEndCoarse = 50, GCoarseTune = 51, GFineTune = 52, GSampleId = 53, GSampleModes = 54, GScaleTuning = 56,
        GExclusive = 57, GRootKey = 58, GCount = 61;
    /// <summary>
    /// What EMU hardware, and FluidSynth after it, actually apply of the initial attenuation: banks like GeneralUser are
    /// voiced for it.
    /// </summary>
    const double AttenuationScale = 0.4;

    public float[] Data { get; }
    public string Name { get; }
    readonly Dictionary<int, SfPreset> presets;

    SoundFont(float[] data, string name, Dictionary<int, SfPreset> presets)
    {
        Data = data;
        Name = name;
        this.presets = presets;
    }

    public IEnumerable<SfPreset> Presets => presets.Values.OrderBy(p => p.Bank).ThenBy(p => p.Program);

    /// <summary>
    /// The preset for a GM program, or a drum kit; what the bank lacks falls back to the first of its kind (a GS kit
    /// number this bank doesn't have plays the standard kit). Null only for a bank with nothing usable.
    /// </summary>
    public SfPreset? Find(int program, bool drums)
    {
        int bank = drums ? DrumBank : 0;
        program = Math.Clamp(program, 0, 127);
        if (presets.TryGetValue(Key(bank, program), out var p)) return p;
        if (presets.TryGetValue(Key(bank, 0), out p)) return p;
        return presets.Values.Where(x => (x.Bank == DrumBank) == drums).OrderBy(x => x.Bank).ThenBy(x => x.Program).FirstOrDefault();
    }

    static int Key(int bank, int program) => bank * 128 + program;

    public static SoundFont Read(string path)
    {
        using var s = File.OpenRead(path);
        return Read(s);
    }

    /// <summary>Reads a bank; anything that isn't a SoundFont 2 throws <see cref="FormatException"/>.</summary>
    public static SoundFont Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Tag(r) != "RIFF") throw new FormatException("not RIFF");
        long end = r.ReadUInt32() + stream.Position;
        if (Tag(r) != "sfbk") throw new FormatException("not a SoundFont");
        string name = "";
        short[]? smpl = null;
        Dictionary<string, byte[]> pdta = [];
        while (stream.Position + 8 <= end)
        {
            string id = Tag(r);
            long size = r.ReadUInt32(), next = stream.Position + size + (size & 1);
            if (id == "LIST")
            {
                string list = Tag(r);
                while (stream.Position + 8 <= next)
                {
                    string sub = Tag(r);
                    int len = (int)r.ReadUInt32();
                    long after = stream.Position + len + (len & 1);
                    if (list == "INFO" && sub == "INAM") name = Encoding.ASCII.GetString(r.ReadBytes(len)).TrimEnd('\0', ' ');
                    else if (list == "sdta" && sub == "smpl")
                    {
                        var bytes = r.ReadBytes(len);
                        smpl = new short[len / 2];
                        Buffer.BlockCopy(bytes, 0, smpl, 0, smpl.Length * 2);
                    }
                    else if (list == "pdta") pdta[sub] = r.ReadBytes(len);
                    stream.Position = after;
                }
            }
            stream.Position = next;
        }
        if (smpl == null) throw new FormatException("no samples");
        var data = new float[smpl.Length + Pad];
        for (int i = 0; i < smpl.Length; i++) data[i] = smpl[i] / 32768f;
        return new SoundFont(data, name, ReadPresets(pdta, data.Length - Pad));
    }

    static string Tag(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(4));

    readonly record struct Sample(int Start, int End, int LoopStart, int LoopEnd, int Rate, int Pitch, int Correction, bool Rom);

    /// <summary>A zone's generators: the amount of each, and whether the zone set it.</summary>
    sealed class Gens
    {
        public readonly short[] V = new short[GCount];
        public readonly bool[] Set = new bool[GCount];

        public Gens Copy()
        {
            var g = new Gens();
            V.CopyTo(g.V, 0);
            Set.CopyTo(g.Set, 0);
            return g;
        }

        public void Put(int op, short v)
        {
            if (op >= GCount) return;
            V[op] = v;
            Set[op] = true;
        }

        public int Lo(int op, int fallback) => Set[op] ? V[op] & 0xFF : fallback;
        public int Hi(int op, int fallback) => Set[op] ? (V[op] >> 8) & 0xFF : fallback;
    }

    static Dictionary<int, SfPreset> ReadPresets(Dictionary<string, byte[]> pdta, int sampleCount)
    {
        byte[] Chunk(string id) => pdta.TryGetValue(id, out var b) ? b : throw new FormatException("missing " + id);
        var phdr = Chunk("phdr");
        var pbag = Chunk("pbag");
        var pgen = Chunk("pgen");
        var inst = Chunk("inst");
        var ibag = Chunk("ibag");
        var igen = Chunk("igen");
        var shdr = Chunk("shdr");

        var samples = new List<Sample>();
        for (int i = 0; i + 46 <= shdr.Length - 46; i += 46)
        {
            int U(int at) => (int)Math.Min(BitConverter.ToUInt32(shdr, i + at), (uint)sampleCount);
            int type = BitConverter.ToUInt16(shdr, i + 44);
            samples.Add(new Sample(U(20), U(24), U(28), U(32), (int)BitConverter.ToUInt32(shdr, i + 36), shdr[i + 40], (sbyte)shdr[i + 41], (type & 0x8000) != 0));
        }

        // every bag's generators; a bag of the last record only marks where the one before ends
        List<Gens> Bags(byte[] bags, byte[] gens)
        {
            var list = new List<Gens>();
            int count = bags.Length / 4;
            for (int b = 0; b + 1 < count; b++)
            {
                int from = BitConverter.ToUInt16(bags, b * 4), to = BitConverter.ToUInt16(bags, (b + 1) * 4);
                var g = new Gens();
                for (int k = from; k < to && k * 4 + 4 <= gens.Length; k++) g.Put(BitConverter.ToUInt16(gens, k * 4), BitConverter.ToInt16(gens, k * 4 + 2));
                list.Add(g);
            }
            return list;
        }
        var pBags = Bags(pbag, pgen);
        var iBags = Bags(ibag, igen);

        // each instrument's zones: its global zone (no sample) goes under the others
        var instruments = new List<List<Gens>>();
        int instCount = inst.Length / 22;
        for (int i = 0; i + 1 < instCount; i++)
        {
            int from = BitConverter.ToUInt16(inst, i * 22 + 20), to = BitConverter.ToUInt16(inst, (i + 1) * 22 + 20);
            var zones = new List<Gens>();
            Gens? global = null;
            for (int b = from; b < to && b < iBags.Count; b++)
            {
                var z = iBags[b];
                if (!z.Set[GSampleId])
                {
                    if (b == from) global = z;
                    continue;
                }
                if (global == null) zones.Add(z);
                else
                {
                    var merged = global.Copy();
                    for (int g = 0; g < GCount; g++) if (z.Set[g]) merged.Put(g, z.V[g]);
                    zones.Add(merged);
                }
            }
            instruments.Add(zones);
        }

        var result = new Dictionary<int, SfPreset>();
        int presetCount = phdr.Length / 38;
        for (int i = 0; i + 1 < presetCount; i++)
        {
            string name = Encoding.ASCII.GetString(phdr, i * 38, 20).Split('\0')[0].Trim();
            int program = BitConverter.ToUInt16(phdr, i * 38 + 20), bank = BitConverter.ToUInt16(phdr, i * 38 + 22);
            int from = BitConverter.ToUInt16(phdr, i * 38 + 24), to = BitConverter.ToUInt16(phdr, (i + 1) * 38 + 24);
            if (program > 127 || bank > DrumBank) continue;
            Gens? global = null;
            var regions = new List<SfRegion>();
            for (int b = from; b < to && b < pBags.Count; b++)
            {
                var z = pBags[b];
                if (!z.Set[GInstrument])
                {
                    if (b == from) global = z;
                    continue;
                }
                var pz = global?.Copy() ?? new Gens();
                for (int g = 0; g < GCount; g++) if (z.Set[g]) pz.Put(g, z.V[g]);
                int instrument = pz.V[GInstrument];
                if (instrument < 0 || instrument >= instruments.Count) continue;
                foreach (var iz in instruments[instrument])
                {
                    int sampleId = iz.V[GSampleId];
                    if (sampleId < 0 || sampleId >= samples.Count || samples[sampleId].Rom) continue;
                    if (Region(pz, iz, samples[sampleId]) is { } region) regions.Add(region);
                }
            }
            if (regions.Count > 0) result.TryAdd(Key(bank, program), new SfPreset(name, bank, program, [.. regions]));
        }
        return result;
    }

    static double Seconds(double timecents) => timecents <= -12000 ? 0 : Math.Pow(2, timecents / 1200);
    static double Hz(double cents) => 8.176 * Math.Pow(2, cents / 1200);

    /// <summary>A preset zone over an instrument zone: ranges intersect, everything else adds (SF2 9.4). Null when the ranges don't meet.</summary>
    static SfRegion? Region(Gens p, Gens i, Sample s)
    {
        int keyLo = Math.Max(i.Lo(GKeyRange, 0), p.Lo(GKeyRange, 0)), keyHi = Math.Min(i.Hi(GKeyRange, 127), p.Hi(GKeyRange, 127));
        int velLo = Math.Max(i.Lo(GVelRange, 0), p.Lo(GVelRange, 0)), velHi = Math.Min(i.Hi(GVelRange, 127), p.Hi(GVelRange, 127));
        if (keyLo > keyHi || velLo > velHi) return null;
        // instrument value (or its default) plus the preset's offset; offsets of addresses, keys and modes are instrument only
        double G(int op, double fallback) => (i.Set[op] ? i.V[op] : fallback) + (p.Set[op] ? p.V[op] : 0);
        int I(int op, int fallback) => i.Set[op] ? i.V[op] : fallback;
        int last = s.End;
        int start = Math.Clamp(s.Start + I(GStartOffset, 0) + 32768 * I(GStartCoarse, 0), 0, last);
        int end = Math.Clamp(s.End + I(GEndOffset, 0) + 32768 * I(GEndCoarse, 0), start, last);
        int loopStart = Math.Clamp(s.LoopStart + I(GLoopStartOffset, 0) + 32768 * I(GLoopStartCoarse, 0), start, end);
        int loopEnd = Math.Clamp(s.LoopEnd + I(GLoopEndOffset, 0) + 32768 * I(GLoopEndCoarse, 0), loopStart, end);
        if (end - start < 2) return null;
        var loop = (SfLoop)(I(GSampleModes, 0) & 3);
        if (loop == (SfLoop)2) loop = SfLoop.None;
        if (loopEnd - loopStart < 2) loop = SfLoop.None;
        int root = I(GRootKey, -1);
        if (root < 0 || root > 127) root = s.Pitch is >= 0 and <= 127 ? s.Pitch : 60;
        double attenuation = Math.Clamp(G(GAttenuation, 0), 0, 1440) * AttenuationScale;
        return new SfRegion
        {
            KeyLo = keyLo, KeyHi = keyHi, VelLo = velLo, VelHi = velHi,
            Start = start, End = end, LoopStart = loopStart, LoopEnd = loopEnd, Loop = loop,
            SampleRate = s.Rate is > 0 and < 400_000 ? s.Rate : 44100,
            Root = root,
            TuneCents = G(GCoarseTune, 0) * 100 + G(GFineTune, 0) + s.Correction,
            ScaleTuning = G(GScaleTuning, 100),
            FixedKey = I(GKeynum, -1) is >= 0 and <= 127 and var k ? k : -1,
            Gain = (float)Math.Pow(10, -attenuation / 200),
            Pan = (float)Math.Clamp(G(GPan, 0) / 500, -1, 1),
            Delay = Seconds(G(GDelay, -12000)),
            Attack = Seconds(G(GAttack, -12000)),
            Hold = Seconds(G(GHold, -12000)),
            Decay = Seconds(G(GDecay, -12000)),
            SustainCb = Math.Clamp(G(GSustain, 0), 0, 1440),
            Release = Seconds(G(GRelease, -12000)),
            HoldPerKey = G(GKeyToHold, 0),
            DecayPerKey = G(GKeyToDecay, 0),
            ModDelay = Seconds(G(GModDelay, -12000)),
            ModAttack = Seconds(G(GModAttack, -12000)),
            ModHold = Seconds(G(GModHold, -12000)),
            ModDecay = Seconds(G(GModDecay, -12000)),
            ModSustain = 1 - Math.Clamp(G(GModSustain, 0), 0, 1000) / 1000,
            ModRelease = Seconds(G(GModRelease, -12000)),
            ModEnvToPitch = G(GModEnvToPitch, 0),
            ModEnvToCutoff = G(GModEnvToCutoff, 0),
            Cutoff = Math.Clamp(G(GCutoff, OpenCutoff), 1500, OpenCutoff),
            Resonance = Math.Clamp(G(GResonance, 0), 0, 960),
            VibDelay = Seconds(G(GVibDelay, -12000)),
            VibHz = Hz(G(GVibFreq, 0)),
            VibToPitch = G(GVibLfoToPitch, 0),
            ModLfoDelay = Seconds(G(GModLfoDelay, -12000)),
            ModLfoHz = Hz(G(GModLfoFreq, 0)),
            ModLfoToPitch = G(GModLfoToPitch, 0),
            ModLfoToCutoff = G(GModLfoToCutoff, 0),
            ModLfoToVolume = G(GModLfoToVolume, 0),
            Exclusive = I(GExclusive, 0),
        };
    }
}

/// <summary>
/// The GM bank the app ships (GeneralUser GS, <c>tools\get-gm.ps1</c>, next to the exe in <c>gm\</c>), read once on first
/// use and kept for every track and export. Any thread; the first caller pays the read (about half a second).
/// </summary>
public static class GmBank
{
    public const string FileName = "GeneralUser-GS.sf2";
    static readonly Lock gate = new();
    static SoundFont? font;
    static bool failed;

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "gm", FileName);

    /// <summary>True once it is in memory: no wait to play it.</summary>
    public static bool Loaded => Volatile.Read(ref font) != null;

    /// <summary>The bank; null when the file is missing or broken (asked again only after <see cref="Retry"/>).</summary>
    public static SoundFont? Load()
    {
        if (Volatile.Read(ref font) is { } f) return f;
        lock (gate)
        {
            if (font != null || failed) return font;
            try { font = SoundFont.Read(Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or EndOfStreamException or ArgumentException)
            {
                failed = true;
            }
            return font;
        }
    }

    /// <summary>Looks for the file again on the next <see cref="Load"/> (it was installed meanwhile).</summary>
    public static void Retry()
    {
        lock (gate) failed = false;
    }
}
