using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Lab;

/// <summary>
/// The loop's rows of comprobar-dsp: <see cref="LoopSplice"/> run the way the engine runs it (block by block, two reads
/// and a turn at the end of the block) over several laps and driver blocks, on a signal where every sample is different,
/// so a lost sample or a fade on the first hit can't hide.
/// </summary>
static class LoopChecks
{
    const int Sr = 48000;

    public static bool Check()
    {
        var rnd = new Random(7);
        var noise = new float[Sr * 3];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rnd.NextDouble() * 2 - 1);
        float Noise(long f) => f >= 0 && f < noise.Length ? noise[f] : 0;
        const long A = Sr / 2, B = A + Sr;

        double worst = 0, first = 1;
        foreach (int block in new[] { 64, 128, 256, 333, 512, 1024 })
        {
            var heard = Play(Noise, A, B, B - 5000, block, Sr * 4);
            int lap = (int)(B - A);
            for (int t = 5000; t < heard.Length; t++)
            {
                int into = (t - 5000) % lap;
                if (into >= lap - LoopSplice.Frames) continue;
                worst = Math.Max(worst, Math.Abs(heard[t] - Noise(A + into)));
                if (into == 0) first = Math.Min(first, 1 - Math.Abs(heard[t] - noise[A]));
            }
        }
        bool whole = worst < 1e-6 && first > 0.999999;
        TransposeCommands.Row("Bucle: A entero, ninguna muestra perdida", $"{worst:0.0e0}", "<1e-6", whole);

        // a turn in mid-note: a plain cut would jump by up to the whole wave
        double step = 0;
        foreach (int block in new[] { 64, 256, 1024 })
        {
            var heard = Play(f => 0.5f * MathF.Sin(2 * MathF.PI * 440.3f * f / Sr), A, B + 77, B - 2000, block, 4000);
            for (int t = 1; t < heard.Length; t++) step = Math.Max(step, Math.Abs(heard[t] - heard[t - 1]));
        }
        bool smooth = step < 0.08;
        TransposeCommands.Row("Bucle: en medio de una nota, sin clic", $"{step:0.000}", "<0,08", smooth);

        // the whole song from zero: the lap's read goes before the start, where nothing sounds
        var song = Play(Noise, 0, Sr, Sr - 3000, 256, Sr * 2);
        bool zero = Math.Abs(song[3000] - noise[0]) < 1e-6 && Math.Abs(song[3000 + Sr / 2] - noise[Sr / 2]) < 1e-6;
        TransposeCommands.Row("Bucle desde cero", zero ? "igual" : "distinto", "igual", zero);
        return whole && smooth && zero;
    }

    /// <summary>What the engine plays from <paramref name="from"/>, in blocks of <paramref name="block"/>, for <paramref name="total"/> samples.</summary>
    static float[] Play(Func<long, float> song, long start, long end, long from, int block, int total)
    {
        var heard = new float[total];
        float[] main = new float[block], lapped = new float[block];
        long pos = from;
        for (int t = 0; t < total; t += block)
        {
            int n = Math.Min(block, total - t);
            bool splice = LoopSplice.Weights(pos, n, start, end, main, lapped);
            for (int i = 0; i < n; i++)
                heard[t + i] = splice ? main[i] * song(pos + i) + lapped[i] * song(pos + i - (end - start)) : song(pos + i);
            long next = pos + n;
            pos = LoopSplice.Turned(pos, next, start, end, out long to) ? to : next;
        }
        return heard;
    }
}
