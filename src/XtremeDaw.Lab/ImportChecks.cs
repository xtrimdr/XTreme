using System.Diagnostics;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;
using NAudio.Wave;

namespace XtremeDaw.Lab;

/// <summary>Regression cases for importing outside audio into a project (<see cref="AudioImport"/>).</summary>
static class ImportChecks
{
    /// <summary>What each step of bringing a file in costs, to find out where an import spends its time.</summary>
    public static int Times(Args a)
    {
        string file = a.Positional.FirstOrDefault() ?? throw new ArgumentException("Falta el archivo.");
        var clock = Stopwatch.StartNew();
        double Lap() { double s = clock.Elapsed.TotalSeconds; clock.Restart(); return s; }
        var raw = AudioLoader.Load(file, 0);
        Console.WriteLine($"{Path.GetFileName(file)}: {raw.DurationSec:0.0} s, {raw.SampleRate} Hz, {raw.SourceChannels} canales");
        Console.WriteLine($"  decodificar {Lap():0.00} s");
        var (l, r) = AudioLoader.Resample(raw.Left, raw.Right, raw.SampleRate, AudioLoader.DefaultSampleRate);
        Console.WriteLine($"  remuestrear {Lap():0.00} s");
        LevelAnalysis.Analyze([l, r], AudioLoader.DefaultSampleRate);
        CompPresets.Analyze([l, r], AudioLoader.DefaultSampleRate);
        Console.WriteLine($"  analizar   {Lap():0.00} s");
        return 0;
    }

    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Importar audio: {text}");
            n++;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "gc-import-" + Guid.NewGuid().ToString("N")[..8]);
        string proj = Path.Combine(tmp, "proyecto");
        Directory.CreateDirectory(proj);
        try
        {
            string src = Path.Combine(tmp, "Base - tema (v2).wav");
            using (var w = new WaveFileWriter(src, new WaveFormat(44100, 16, 1)))
                w.Write(new byte[44100 * 2 * 3], 0, 44100 * 2 * 3);

            var a = AudioImport.CopyIn(proj, src);
            Check(a.DurationSec is > 2.99 and < 3.01 && a.SampleRate == 44100 && a.Channels == 1, "lee la duración y el formato");
            Check(File.Exists(Path.Combine(proj, a.RelativePath)) && File.Exists(src), "copia adentro y deja el original");
            var b = AudioImport.CopyIn(proj, src);
            Check(a.RelativePath != b.RelativePath && File.Exists(Path.Combine(proj, b.RelativePath)), "el mismo archivo dos veces no se pisa");
            Check(!Directory.GetFiles(Path.Combine(proj, "media")).Any(f => f.EndsWith(".importando")), "no deja temporales");

            var p = new Project();
            var empty = new Track { Name = "Pista 1" };
            p.Tracks.Add(empty);
            var t = AudioImport.AddClip(p, a, 1.2344, empty.Id);
            Check(t == empty && t.Clips.Count == 1 && t.Clips[0].AtSec == 1.234, "cae en la pista vacía elegida, en su tiempo");
            Check(!t.Video && t.Name == a.Name && !t.Channel.Comp.On && !t.Channel.Sat.On, "pista de audio, sin compresor ni saturación");
            Check(p.FindMedia(t.Clips[0].MediaId) is { Kind: "import", SyncMs: 0 }, "el medio queda como importado y sin sincronía");

            var t2 = AudioImport.AddClip(p, b, -5, empty.Id);
            Check(t2 != empty && p.Tracks.Count == 2 && t2.Clips[0].AtSec == 0, "con la pista ocupada crea una nueva; nunca antes de 0");
            Check(p.Log.Last().Type == "audio-imported", "queda en la actividad");

            int copies = Directory.GetFiles(Path.Combine(proj, "media")).Length;
            var linked = AudioImport.LinkIn(proj, src);
            Check(linked.RelativePath == Path.GetFullPath(src) && Directory.GetFiles(Path.Combine(proj, "media")).Length == copies, "vincular deja el archivo donde está y no copia nada");
            var lt = AudioImport.AddClip(p, linked, 0);
            var lm = p.FindMedia(lt.Clips[0].MediaId)!;
            Check(lm.Linked && !p.FindMedia(t.Clips[0].MediaId)!.Linked && lm.Audio == Path.GetFullPath(src), "el medio queda vinculado, con su ruta entera");
            var saved = ProjectLibrary.Clone(p);
            Check(saved.FindMedia(lm.Id)?.Audio == lm.Audio, "la ruta sobrevive al guardado");
            var (gone, bytes, failure) = ProjectHistory.TrashMedia(proj, [lm]);
            Check(gone.Count == 1 && bytes == 0 && failure == null && File.Exists(src), "borrar la toma vinculada no toca el archivo ni dice que libera lugar");
            var soundless = new AudioLoader.LoadedAudio(new float[4800], new float[4800], 48000, 48000, 2, Converted: true);
            var wav = AudioImport.LinkIn(proj, src, soundless);
            Check(!Path.IsPathRooted(wav.RelativePath) && wav.RelativePath.EndsWith(".wav") && File.Exists(Path.Combine(proj, wav.RelativePath)), "lo que solo lee FFmpeg entra como WAV aunque se vincule");
            var picture = AudioImport.LinkVideoIn(src, wav);
            Check(picture.Video == Path.GetFullPath(src) && !Path.IsPathRooted(picture.RelativePath), "la imagen se vincula aunque su sonido sea un WAV");

            string bad = Path.Combine(tmp, "roto.mp3");
            File.WriteAllBytes(bad, [1, 2, 3, 4, 5, 6, 7, 8]);
            int before = Directory.GetFiles(Path.Combine(proj, "media")).Length;
            bool failed = false;
            try { AudioImport.CopyIn(proj, bad); } catch (InvalidDataException) { failed = true; }
            Check(failed && Directory.GetFiles(Path.Combine(proj, "media")).Length == before, "un archivo roto avisa y no deja nada");
            File.WriteAllText(Path.Combine(tmp, "nota.txt"), "x");
            failed = false;
            try { AudioImport.CopyIn(proj, Path.Combine(tmp, "nota.txt")); } catch (InvalidDataException) { failed = true; }
            Check(failed, "una extensión que no es de audio avisa");

            var noise = new Random(7);
            var sig = new float[9000];
            for (int i = 0; i < sig.Length; i++) sig[i] = (float)(0.5 * Math.Sin(i * 0.05) + 0.2 * (noise.NextDouble() - 0.5));
            foreach (var (from, to) in new[] { (44100, 48000), (48000, 44100), (48000, 22050), (22050, 48000), (44100, 47999) })
            {
                var (l, r) = AudioLoader.Resample(sig, sig, from, to);
                var slow = SlowResample(sig, from, to);
                double worst = 0;
                for (int i = 0; i < slow.Length; i++) worst = Math.Max(worst, Math.Abs(l[i] - slow[i]));
                Check(l.Length == slow.Length && r.AsSpan().SequenceEqual(l) && worst < 1e-5, $"el remuestreo de {from} a {to} suena igual que el filtro calculado muestra por muestra");
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
        Console.WriteLine($"Importar audio: {n} comprobaciones bien.");
        return 0;
    }

    /// <summary>The resampling filter worked out for every sample, as the reference for the table in <see cref="AudioLoader"/>.</summary>
    static float[] SlowResample(float[] src, int srcRate, int dstRate)
    {
        const int half = 16;
        var dst = new float[(int)Math.Round((long)src.Length * dstRate / (double)srcRate)];
        double ratio = srcRate / (double)dstRate, cutoff = Math.Min(1.0, dstRate / (double)srcRate) * 0.92;
        for (int i = 0; i < dst.Length; i++)
        {
            double pos = i * ratio;
            int center = (int)Math.Floor(pos);
            double sum = 0, weight = 0;
            for (int k = -half + 1; k <= half; k++)
            {
                double x = k - (pos - center), w = (x + half) / (2.0 * half), cx = x * cutoff;
                double win = 0.42 - 0.5 * Math.Cos(2 * Math.PI * w) + 0.08 * Math.Cos(4 * Math.PI * w);
                double c = (Math.Abs(cx) < 1e-8 ? 1 : Math.Sin(Math.PI * cx) / (Math.PI * cx)) * win * cutoff;
                weight += c;
                if ((uint)(center + k) < (uint)src.Length) sum += src[center + k] * c;
            }
            dst[i] = (float)(sum / weight);
        }
        return dst;
    }
}
