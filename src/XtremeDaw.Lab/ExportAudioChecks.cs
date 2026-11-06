using System.Buffers.Binary;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Lab;

/// <summary>
/// The "Avanzado" of Exportar audio (#208), offline: the defaults leave the mix untouched, mono, resampling and the
/// normalize peak do what they say, the WAV and AIFF files carry the depth and rate chosen (16 bits dithered, 32 float
/// exact), a fixed tail is exactly that long, and a bus stem is what comes out of that bus and nothing else.
/// </summary>
static class ExportAudioChecks
{
    const int Sr = 48000;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        var l = new float[Sr];
        var r = new float[Sr];
        for (int i = 0; i < Sr; i++)
        {
            l[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / Sr));
            r[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 660 * i / Sr));
        }

        // 1. the easy export: nothing touched
        var same = AudioExporter.Finish(l, r, Sr, AudioExportSettings.Defaults);
        Row("Avanzado: por defecto no cambia", same.L == l && same.R == r ? "igual" : "copia", "igual", same.L == l && same.R == r && same.Channels == 2);

        // 2. mono: the average of both sides, one channel
        var mono = AudioExporter.Finish(l, r, Sr, AudioExportSettings.Defaults with { Mono = true });
        double md = 0;
        for (int i = 0; i < Sr; i++) md = Math.Max(md, Math.Abs(mono.L[i] - 0.5 * (l[i] + r[i])));
        Row("Avanzado: mono es el promedio", $"{md:0.0e0}", "0", md < 1e-7 && mono.Channels == 1);

        // 3. 44.1 kHz: as long as before in time
        var rs = AudioExporter.Finish(l, r, Sr, AudioExportSettings.Defaults with { SampleRate = 44100 });
        Row("Avanzado: 44,1 kHz dura lo mismo", $"{rs.L.Length}", "44100", Math.Abs(rs.L.Length - 44100) <= 1 && rs.Rate == 44100);

        // 4. normalize: the loudest true peak lands on the target
        var nm = AudioExporter.Finish(l, r, Sr, AudioExportSettings.Defaults with { NormalizeDbtp = -1 });
        double tp = TruePeak.MeasureDb(nm.L, nm.R, nm.L.Length);
        Row("Avanzado: normaliza a -1 dBTP", $"{tp:0.00}", "-1.00", Math.Abs(tp + 1) < 0.05);

        string tmp = Path.Combine(Path.GetTempPath(), $"xtlab-exp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // 5. 16 bits with dither: the header says so, and silence turns into ±1 LSB of noise, never more
            var quiet = new AudioExporter.FinishedPcm(new float[Sr], new float[Sr], Sr, 2);
            string w16 = Path.Combine(tmp, "16.wav");
            AudioExporter.WritePcm(w16, quiet, AudioExportSettings.Defaults with { BitDepth = 16 }, aiff: false);
            var b16 = File.ReadAllBytes(w16);
            int bits = BinaryPrimitives.ReadInt16LittleEndian(b16.AsSpan(34)), most = 0, nonzero = 0;
            for (int at = 44; at + 2 <= b16.Length; at += 2)
            {
                int v = BinaryPrimitives.ReadInt16LittleEndian(b16.AsSpan(at));
                most = Math.Max(most, Math.Abs(v));
                if (v != 0) nonzero++;
            }
            Row("WAV 16 bit: dither de ±1 LSB", $"{bits}b ±{most}", "16b ±1", bits == 16 && most == 1 && nonzero > Sr / 4 && b16.Length == 44 + Sr * 4);
            AudioExporter.WritePcm(w16, quiet, AudioExportSettings.Defaults with { BitDepth = 16, Dither = false }, aiff: false);
            b16 = File.ReadAllBytes(w16);
            bool allZero = b16.AsSpan(44).IndexOfAnyExcept((byte)0) < 0;
            Row("WAV 16 bit sin dither: silencio", allZero ? "0" : "ruido", "0", allZero);

            // 6. 32-bit float: format 3, every sample exactly as it was
            string w32 = Path.Combine(tmp, "32.wav");
            AudioExporter.WritePcm(w32, new AudioExporter.FinishedPcm(l, r, Sr, 2), AudioExportSettings.Defaults with { BitDepth = 32 }, aiff: false);
            var b32 = File.ReadAllBytes(w32);
            bool exact = BinaryPrimitives.ReadInt16LittleEndian(b32.AsSpan(20)) == 3;
            for (int i = 0; i < Sr && exact; i += 97)
                exact = BinaryPrimitives.ReadSingleLittleEndian(b32.AsSpan(44 + i * 8)) == l[i] && BinaryPrimitives.ReadSingleLittleEndian(b32.AsSpan(48 + i * 8)) == r[i];
            Row("WAV 32 float: idéntico", exact ? "igual" : "distinto", "igual", exact);

            // 7. AIFF 24 bits at 44.1 kHz: big-endian, the rate as an 80-bit float, a sample where it should be
            string aif = Path.Combine(tmp, "a.aiff");
            AudioExporter.WritePcm(aif, rs, AudioExportSettings.Defaults with { Format = AudioFormat.Aiff, SampleRate = 44100 }, aiff: true);
            var ba = File.ReadAllBytes(aif);
            int exp = BinaryPrimitives.ReadUInt16BigEndian(ba.AsSpan(28)) - 16383;
            double rate = BinaryPrimitives.ReadUInt64BigEndian(ba.AsSpan(30)) / Math.Pow(2, 63 - exp);
            int at100 = 54 + 100 * 6;
            int s100 = (ba[at100] << 24 | ba[at100 + 1] << 16 | ba[at100 + 2] << 8) >> 8;
            int want100 = (int)Math.Round(Math.Clamp(rs.L[100], -1f, 1f) * 8388607.0);
            bool aiffOk = ba.AsSpan(0, 4).SequenceEqual("FORM"u8) && ba.AsSpan(8, 4).SequenceEqual("AIFF"u8)
                && rate == 44100 && BinaryPrimitives.ReadInt16BigEndian(ba.AsSpan(26)) == 24 && s100 == want100
                && BinaryPrimitives.ReadUInt32BigEndian(ba.AsSpan(4)) + 8 == ba.Length;
            Row("AIFF 24 bit 44,1 kHz", $"{rate:0}", "44100", aiffOk);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }

        // 8. a fixed tail is exactly that long, silence included
        MixTrackSpec[] one = [new MixTrackSpec { Clips = [new MixClipSpec { Left = l, Right = r, DurationSec = 1 }] }];
        var fixedMix = MixMaster.Render(one, new MixBusSpec(), 1, Sr, 3, fixedTail: true);
        var autoMix = MixMaster.Render(one, new MixBusSpec(), 1, Sr, 3);
        Row("Cola fija de 3 s", $"{fixedMix.DurationSec:0.000}", "4.000", Math.Abs(fixedMix.DurationSec - 4) < 1.0 / Sr && autoMix.DurationSec < 1.5);

        // 9. a bus stem: what comes out of the bus (the track routed there), not the track that goes to the master
        var bus = new MixBusSpec { Nodes = [new MixNodeSpec { Name = "Bus" }] };
        MixTrackSpec[] two =
        [
            new MixTrackSpec { Name = "A", Output = 0, Clips = [new MixClipSpec { Left = l, Right = l, DurationSec = 1 }] },
            new MixTrackSpec { Name = "B", Clips = [new MixClipSpec { Left = r, Right = r, DurationSec = 1 }] },
        ];
        var (stemL, _) = MixRenderer.Render(two, bus, 1, Sr, onlyNode: 0);
        var (aloneL, _) = MixRenderer.Render(two, bus, 1, Sr, only: 0);
        double bd = 0;
        for (int i = 0; i < stemL.Length; i++) bd = Math.Max(bd, Math.Abs(stemL[i] - aloneL[i]));
        Row("Stem de bus: solo lo que pasa por él", $"{bd:0.0e0}", "0", bd < 1e-6);

        return allOk;
    }
}
