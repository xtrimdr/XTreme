namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// Where each frame of a <see cref="PitchShifter"/> render reads and writes, so a stretch keeps its attacks (asked by the
/// user: rhythm loops). An attack sits in every frame whose window covers it, and at an even stretch each of those frames
/// writes it somewhere else: that is the smear before and after the hit. So the time map goes at 1:1 around each attack,
/// as far as a frame reaches: all those frames write it on the same spot, its place in the stretched song, and the
/// stretch goes into what is between the attacks. Attacks close together get a narrower zone, never below what keeps
/// the part between them moving (<see cref="Share"/>). Audio with no attacks, or no stretch, keeps the even map.
/// </summary>
public static class TimeMap
{
    /// <summary>The spectrum that looks for attacks: 21 ms, every 5 ms.</summary>
    const int FluxSize = 1024, FluxHop = 256;
    /// <summary>A peak has to be this many times the flux around it (<see cref="Around"/> frames each side, 85 ms), plus <see cref="FluxFloor"/>.</summary>
    const double PeakOver = 2, FluxFloor = 0.05;
    const int Around = 16;
    /// <summary>A peak is the highest within this many frames each side (16 ms).</summary>
    const int PeakReach = 3;
    /// <summary>Hits closer than this are one attack (a flam, a doubled pick): 30 ms.</summary>
    const int MinGap = 1440;
    /// <summary>Blocks in which the exact sample of an attack is looked for: 1,3 ms every 0,7 ms.</summary>
    const int RiseBlock = 64, RiseHop = 32;
    /// <summary>What each attack may take of the gaps beside it: the part left between two zones still moves, so it never freezes.</summary>
    const double Share = 1.0 / 3;
    /// <summary>A zone narrower than this (5 ms) does nothing for a hit.</summary>
    const int MinZone = 256;
    /// <summary>How long a hop may get to land on a corner.</summary>
    const double CornerReach = 1.25;

    /// <summary>The samples where the attacks start, in order. Runs on a worker thread.</summary>
    public static int[] Attacks(float[] left, float[] right, CancellationToken ct = default)
    {
        int len = left.Length, count = len < FluxSize ? 0 : (len - FluxSize) / FluxHop + 1;
        if (count < 2 * Around) return [];
        var flux = Flux(left, right, count, ct);
        var found = new List<int>();
        double sum = 0;
        for (int f = 0; f < Math.Min(Around, count); f++) sum += flux[f];
        for (int f = 0; f < count; f++)
        {
            if (f + Around < count) sum += flux[f + Around];
            if (f - Around - 1 >= 0) sum -= flux[f - Around - 1];
            int n = Math.Min(count - 1, f + Around) - Math.Max(0, f - Around) + 1;
            if (flux[f] < PeakOver * sum / n + FluxFloor) continue;
            bool peak = true;
            for (int k = Math.Max(0, f - PeakReach); k <= Math.Min(count - 1, f + PeakReach) && peak; k++) peak = flux[k] <= flux[f];
            if (!peak) continue;
            int at = RiseAt(left, right, f * FluxHop);
            if (found.Count == 0 || at - found[^1] >= MinGap) found.Add(at);
        }
        return [.. found];
    }

    /// <summary>How much the log spectrum rose at each frame: a hit raises many bins at once, and a quiet one counts as well as a loud one.</summary>
    static double[] Flux(float[] left, float[] right, int count, CancellationToken ct)
    {
        var fft = new Fft(FluxSize);
        var win = new double[FluxSize];
        for (int k = 0; k < FluxSize; k++) win[k] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * k / FluxSize);
        double[] re = new double[FluxSize], im = new double[FluxSize], now = new double[FluxSize / 2], before = new double[FluxSize / 2];
        var flux = new double[count];
        for (int f = 0; f < count; f++)
        {
            if ((f & 1023) == 0) ct.ThrowIfCancellationRequested();
            int at = f * FluxHop;
            for (int k = 0; k < FluxSize; k++)
            {
                re[k] = (left[at + k] + right[at + k]) * 0.5 * win[k];
                im[k] = 0;
            }
            fft.Transform(re, im, inverse: false);
            double rise = 0;
            for (int b = 0; b < now.Length; b++)
            {
                now[b] = Math.Log(1 + 1000 * Math.Sqrt(re[b] * re[b] + im[b] * im[b]) / (FluxSize / 2));
                rise += Math.Max(0, now[b] - before[b]);
            }
            flux[f] = f == 0 ? 0 : rise / now.Length;
            (now, before) = (before, now);
        }
        return flux;
    }

    /// <summary>The sample inside the frame at <paramref name="from"/> where the energy jumps the most. The spectrum sees a hit as soon as it enters the window, so it is in the later part.</summary>
    static int RiseAt(float[] left, float[] right, int from)
    {
        int best = from + FluxSize / 2, stop = Math.Min(left.Length - RiseBlock, from + FluxSize) - RiseHop;
        double most = 0, last = Energy(left, right, from + FluxSize / 4);
        for (int at = from + FluxSize / 4; at < stop; at += RiseHop)
        {
            double e = Energy(left, right, at + RiseHop);
            if (e - last > most) (most, best) = (e - last, at + RiseHop);
            last = e;
        }
        return best;
    }

    static double Energy(float[] left, float[] right, int at)
    {
        double e = 0;
        for (int i = at; i < at + RiseBlock; i++)
        {
            double v = left[i] + right[i];
            e += v * v;
        }
        return e;
    }

    /// <summary>
    /// The frames of a render <paramref name="stretch"/> times as long: where each one reads and where it writes, spaced by
    /// <paramref name="hop"/> on the side that moves the most, so the instantaneous frequencies are never measured across more than a hop.
    /// </summary>
    public static (int[] Read, int[] Written) Frames(float[] left, float[] right, double stretch, int hop, int frame, CancellationToken ct = default)
    {
        int len = left.Length, outLen = PitchShifter.StretchedLength(len, stretch);
        return Walk(Corners(stretch == 1 ? [] : Attacks(left, right, ct), len, stretch, frame), len, outLen, hop, frame);
    }

    /// <summary>The map as straight stretches: 1:1 around each attack, placed where the even stretch puts it, and even from the last one on.</summary>
    static List<(double In, double Out)> Corners(int[] attacks, int len, double stretch, int frame)
    {
        var corners = new List<(double In, double Out)> { (0, 0) };
        double small = Math.Min(1, stretch);
        for (int i = 0; i < attacks.Length; i++)
        {
            int at = attacks[i];
            double gap = Math.Min(at - (i > 0 ? attacks[i - 1] : 0), (i + 1 < attacks.Length ? attacks[i + 1] : len) - at);
            double zone = Math.Min(frame / 2, small * Share * gap);
            if (zone < MinZone) continue;
            corners.Add((at - zone, at * stretch - zone));
            corners.Add((at + zone, at * stretch + zone));
        }
        // past the end, so the last frames reach what is left of both sides
        double end = len + 2 * frame / small + frame;
        corners.Add((end, end * stretch));
        return corners;
    }

    static (int[] Read, int[] Written) Walk(List<(double In, double Out)> corners, int len, int outLen, int hop, int frame)
    {
        var read = new List<int>();
        var written = new List<int>();
        double x = 0, y = 0;
        int c = 1;
        while (true)
        {
            read.Add((int)Math.Round(x));
            written.Add((int)Math.Round(y));
            if (x >= len + frame / 2 && y >= outLen + frame / 2) break;
            var (toIn, toOut) = corners[c];
            double left = toIn - x, ahead = toOut - y, most = Math.Max(left, ahead);
            // a frame lands on a corner, not past it, and takes in what little is left before one: a hop of next to
            // nothing measures no frequency
            double step = most <= hop * CornerReach ? 1 : hop / most;
            x += left * step;
            y += ahead * step;
            if (step == 1 && c + 1 < corners.Count) c++;
        }
        return ([.. read], [.. written]);
    }
}
