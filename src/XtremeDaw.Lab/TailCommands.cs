using System.Diagnostics;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// What the buses and the master cost per block while their tails die out, as the engine runs them with the transport
/// stopped. A tail fading through the numbers floats can barely hold (denormals) is many times slower to compute than
/// the sound before it, and nothing in a normal listen shows it: only the DSP meter climbing in silence.
/// </summary>
static class TailCommands
{
    public static int Run(Args a)
    {
        const int sr = 48000;
        int block = Math.Clamp(a.Int("bloque", 192), 32, TrackDspChain.MaxBlockSize);
        double soundSec = a.Double("segundos", 3), silenceSec = a.Double("silencio", 120);
        double step = a.Double("paso", 5);
        double level = a.Double("nivel", 0.1);
        // --caida 20: the input doesn't stop, it fades this many dB per second, as an instrument's own tail does
        double fade = a.Double("caida", 0);
        var project = new Project();
        if (a.Get("proyecto") is { } dir)
        {
            // a copy of its JSON, so a project open in the app stays as it is
            string copy = Path.Combine(Path.GetTempPath(), "wtlab-colas");
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(Path.GetFullPath(dir), "guitarcam.json"), Path.Combine(copy, "guitarcam.json"), true);
            using var store = ProjectStore.Open(copy);
            project = store.Project;
        }
        else project.Settings.Buses = ProjectBuses.Defaults();
        var spec = ChannelDsp.Bus(project);
        var nodes = spec.Nodes.Select(n =>
        {
            var chain = new BusDspChain(sr);
            chain.Configure(n);
            return chain;
        }).ToArray();
        var master = new MixGraphBus(sr);
        master.Configure(spec);
        var ml = new float[block];
        var mr = new float[block];
        var rng = new Random(1);
        float[] lp = new float[2];

        void RunBlock(double gain)
        {
            Array.Clear(ml);
            Array.Clear(mr);
            for (int k = 0; k < nodes.Length; k++)
            {
                var bl = nodes[k].BufferL(block);
                var br = nodes[k].BufferR(block);
                bl.Clear();
                br.Clear();
                if (gain > 0)
                    for (int i = 0; i < block; i++)
                    {
                        lp[0] += 0.2f * ((float)(rng.NextDouble() * 2 - 1) - lp[0]);
                        lp[1] += 0.2f * ((float)(rng.NextDouble() * 2 - 1) - lp[1]);
                        bl[i] = (float)(lp[0] * gain);
                        br[i] = (float)(lp[1] * gain);
                    }
                nodes[k].Process(bl, br, false, default, default);
                for (int i = 0; i < block; i++)
                {
                    ml[i] += bl[i];
                    mr[i] += br[i];
                }
            }
            master.Process(ml, mr);
        }

        long sound = (long)(soundSec * sr / block);
        for (long k = 0; k < sound; k++) RunBlock(level);
        Console.WriteLine($"{project.Settings.Buses.Count} buses, bloques de {block} ({block * 1000.0 / sr:0.0} ms); después de {soundSec:0.#} s de sonido, en silencio:");
        Console.WriteLine("    segundo   entrada dB   media µs   % bloque    peor µs   salida dB");
        double gain = level, perBlock = Math.Pow(10, -fade / 20 * block / sr);
        double budget = block * 1e6 / sr, us = 1e6 / Stopwatch.Frequency, worstMean = 0;
        int perStep = (int)(step * sr / block);
        double quiet = 0;
        for (int s = 0; quiet < silenceSec; s++, quiet = s * step)
        {
            long total = 0, worst = 0;
            float peak = 0;
            for (int k = 0; k < perStep; k++)
            {
                if (fade <= 0) gain = 0;
                long t0 = Stopwatch.GetTimestamp();
                RunBlock(gain);
                long t = Stopwatch.GetTimestamp() - t0;
                gain *= perBlock;
                total += t;
                worst = Math.Max(worst, t);
                for (int i = 0; i < block; i++) peak = Math.Max(peak, Math.Max(Math.Abs(ml[i]), Math.Abs(mr[i])));
            }
            double mean = total * us / perStep;
            worstMean = Math.Max(worstMean, mean);
            string db = peak > 0 ? $"{20 * Math.Log10(peak):0}" : "silencio";
            string input = gain > 0 ? $"{20 * Math.Log10(gain):0}" : "cero";
            Console.WriteLine($"  {s * step,9:0} {input,12} {mean,10:0.0} {100 * mean / budget,10:0.0} {worst * us,10:0} {db,11}");
        }
        Console.WriteLine($"Peor media: {worstMean:0.0} µs ({100 * worstMean / budget:0.0} % del bloque)");
        return 0;
    }
}
