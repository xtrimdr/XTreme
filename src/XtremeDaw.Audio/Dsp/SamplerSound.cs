using System.Text.RegularExpressions;

namespace XtremeDaw.Audio.Dsp;

/// <summary>How a held note goes over its loop.</summary>
public enum SamplerLoop { Off, Forward, PingPong }

/// <summary>
/// A sample ready to play: never changed once built, so the UI swaps a new one in whole and a voice keeps the one it
/// started with. Built off the audio thread, and only again when something that changes the samples does (reverse, the
/// loop and its crossfade); the file itself is never touched.
/// <para>
/// It holds the sound <see cref="Levels"/> times, each an octave lower in rate, each band-limited. A note far above its
/// root reads one of those, so what would fold back over the Nyquist frequency is not there to begin with: the cost is
/// memory (less than double) and none on the audio thread. A forward loop is written into the buffers: the seam
/// crossfaded, and past its end the start of the loop again, so the interpolator reads across the turn without a branch.
/// </para>
/// <para>
/// The seam always fades, at least <see cref="MinSeam"/> (asked by the user: loops still clicked with the crossfade at
/// 0). It fades into what comes before the loop's start when there is enough of it; a loop that starts near the
/// beginning fades into its own first frames instead and turns back to <see cref="LoopBack"/>, just after them. The
/// gains follow how alike the two stretches are (<see cref="Seam"/>), so the turn neither dips nor swells. A ping-pong
/// loop mirrors the sound past its end, so the interpolator reads the same wave on both sides of the turn.
/// </para>
/// </summary>
public sealed class SamplerSound
{
    /// <summary>The most of a file it keeps in memory: 5 minutes is already far more than a sample.</summary>
    public const double MaxSec = 300;
    /// <summary>Levels of the band-limited pyramid: the last one covers up to four octaves above the root.</summary>
    public const int Levels = 5;
    /// <summary>Frames of silence around each level, so nothing reads out of the array. Also the longest loop guard.</summary>
    public const int Pad = 256;
    /// <summary>The half-band filter between levels: 31 taps leave the top of the audio band alone down to -80 dB.</summary>
    const int HalfTaps = 15;

    static readonly float[] HalfBand = MakeHalfBand();

    public readonly float[][] L = new float[Levels][], R = new float[Levels][];
    /// <summary>Frames of the sound, at level 0.</summary>
    public int Frames { get; }
    public SamplerLoop Loop { get; }
    /// <summary>The loop, in frames of level 0; only meaningful with <see cref="Loop"/>.</summary>
    public int LoopStart { get; }
    public int LoopEnd { get; }
    /// <summary>Where a forward loop turns back to from <see cref="LoopEnd"/>: <see cref="LoopStart"/>, or past the
    /// frames its seam faded into when there was nothing before it to fade into.</summary>
    public int LoopBack { get; }
    /// <summary>The loudest sample, for Normalizar.</summary>
    public float Peak { get; }

    /// <param name="loopStart">Where the loop begins, 0..1 of the sound as it plays (after <paramref name="reverse"/>).</param>
    /// <param name="loopEnd">Where it ends.</param>
    /// <param name="crossfade">How much of the loop the seam fades over, 0..1 of its length.</param>
    /// <param name="minSeam">The shortest seam, in frames (<see cref="MinSeam"/>; 0 only to check a seam as it is).</param>
    public SamplerSound(float[] left, float[] right, bool reverse, SamplerLoop loop, double loopStart, double loopEnd, double crossfade,
        CancellationToken ct = default, int minSeam = MinSeam)
    {
        int n = Frames = left.Length;
        var l = new float[n + 2 * Pad];
        var r = new float[n + 2 * Pad];
        left.CopyTo(l, Pad);
        (right.Length == n ? right : left).CopyTo(r, Pad);
        if (reverse)
        {
            Array.Reverse(l, Pad, n);
            Array.Reverse(r, Pad, n);
        }
        float peak = 0;
        for (int i = Pad; i < Pad + n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        Peak = peak;

        int from = (int)Math.Round(Math.Clamp(loopStart, 0, 1) * n), to = (int)Math.Round(Math.Clamp(loopEnd, 0, 1) * n);
        bool looped = loop != SamplerLoop.Off && to - from >= MinLoop;
        Loop = looped ? loop : SamplerLoop.Off;
        LoopStart = looped ? from : 0;
        LoopEnd = looped ? to : n;
        LoopBack = LoopStart;
        if (Loop == SamplerLoop.Forward)
        {
            int len = to - from;
            int x = Math.Min(Math.Max((int)(Math.Clamp(crossfade, 0, 1) * len), minSeam), len);
            // before the start if there is room for the whole fade; otherwise inside the loop (up to half of it), which
            // then turns back past it
            bool before = from >= x;
            if (!before) x = Math.Min(x, len / 2);
            int src = before ? from - x : from;
            Seam(l, r, to - x, src, x);
            LoopBack = before ? from : from + x;
            int guard = Math.Min(Pad, l.Length - (Pad + to));
            Array.Copy(l, Pad + LoopBack, l, Pad + to, guard);
            Array.Copy(r, Pad + LoopBack, r, Pad + to, guard);
        }
        else if (Loop == SamplerLoop.PingPong)
        {
            int guard = Math.Min(Math.Min(Pad, l.Length - (Pad + to)), to - from);
            for (int j = 1; j <= guard && Pad + to - j >= 0; j++)
            {
                l[Pad + to + j] = l[Pad + to - j];
                r[Pad + to + j] = r[Pad + to - j];
            }
        }
        L[0] = l;
        R[0] = r;
        for (int k = 1; k < Levels; k++)
        {
            ct.ThrowIfCancellationRequested();
            L[k] = Halve(L[k - 1]);
            R[k] = Halve(R[k - 1]);
        }
    }

    /// <summary>The shortest loop: 10 frames (a tone of 4.8 kHz); shorter would only buzz at a pitch of its own.</summary>
    public const int MinLoop = 10;

    /// <summary>How far a loop point looks for a zero crossing: 50 ms, a full cycle of the lowest note worth looping.</summary>
    public const int ZeroReach = 2400;

    /// <summary>
    /// The frame nearest to <paramref name="frame"/> where the sound, as it plays (after <paramref name="reverse"/>), goes
    /// from below zero to zero or above, or -1 with none within <paramref name="reach"/>. Always going up, so a loop's two
    /// ends meet in the same direction and the turn doesn't kink; on the sum of the channels, because a stereo sample
    /// rarely crosses in both at once. The frame returned is the first one of the rise: as a loop's start it plays first,
    /// as its end the one before it plays last, so the turn looks like any other crossing of the wave.
    /// </summary>
    public static int ZeroCrossing(float[] left, float[] right, bool reverse, int frame, int reach = ZeroReach)
    {
        int n = left.Length;
        if (right.Length != n) right = left;
        float At(int i)
        {
            int k = reverse ? n - 1 - i : i;
            return left[k] + right[k];
        }
        bool Rises(int i) => i >= 1 && i < n && At(i - 1) < 0 && At(i) >= 0;
        for (int d = 0; d <= reach; d++)
        {
            if (Rises(frame - d)) return frame - d;
            if (Rises(frame + d)) return frame + d;
        }
        return -1;
    }

    /// <summary>The shortest seam: 5 ms, below what an ear takes for a cut, above what a click needs to go away.</summary>
    public const int MinSeam = 240;

    /// <summary>
    /// The <paramref name="x"/> frames from <paramref name="at"/> (the end of the loop) fade into the ones from
    /// <paramref name="src"/>, which lead into where the loop turns back to, so the turn is a continuation. Alike
    /// stretches add up in step, so they take a straight fade; unrelated ones an equal-power one; the rest, in between by
    /// how alike they are (both channels together, so the image doesn't move).
    /// </summary>
    static void Seam(float[] l, float[] r, int at, int src, int x)
    {
        if (x <= 0) return;
        double ab = 0, aa = 0, bb = 0;
        for (int j = 0; j < x; j++)
        {
            double a1 = l[Pad + at + j], a2 = r[Pad + at + j], b1 = l[Pad + src + j], b2 = r[Pad + src + j];
            ab += a1 * b1 + a2 * b2;
            aa += a1 * a1 + a2 * a2;
            bb += b1 * b1 + b2 * b2;
        }
        double alike = aa > 0 && bb > 0 ? Math.Clamp(ab / Math.Sqrt(aa * bb), 0, 1) : 1;
        for (int j = 0; j < x; j++)
        {
            double t = (j + 1) / (double)(x + 1);
            double gOut = alike * (1 - t) + (1 - alike) * Math.Cos(t * Math.PI / 2);
            double gIn = alike * t + (1 - alike) * Math.Sin(t * Math.PI / 2);
            int i = Pad + at + j, k = Pad + src + j;
            l[i] = (float)(l[i] * gOut + l[k] * gIn);
            r[i] = (float)(r[i] * gOut + r[k] * gIn);
        }
    }

    /// <summary>The next level: filtered in place of aliased, every other sample, with its own <see cref="Pad"/> and its frame 0 on the same instant.</summary>
    static float[] Halve(float[] x)
    {
        int n = (x.Length - 2 * Pad + 1) / 2;
        var y = new float[n + 1 + 2 * Pad];
        var h = HalfBand;
        for (int i = -Pad / 2; i < y.Length - Pad; i++)
        {
            int c = Pad + 2 * i;
            if (c < 0 || c >= x.Length) continue;
            float s = 0;
            for (int j = -HalfTaps; j <= HalfTaps; j++)
            {
                int at = c + j;
                if (at >= 0 && at < x.Length) s += h[j + HalfTaps] * x[at];
            }
            y[Pad + i] = s;
        }
        return y;
    }

    static float[] MakeHalfBand()
    {
        var h = new float[2 * HalfTaps + 1];
        double sum = 0;
        for (int j = -HalfTaps; j <= HalfTaps; j++)
        {
            double sinc = j == 0 ? 0.5 : Math.Sin(Math.PI * j / 2) / (Math.PI * j);
            double w = SamplerKernel.Kaiser(j / (HalfTaps + 1.0), 8);
            h[j + HalfTaps] = (float)(sinc * w);
            sum += sinc * w;
        }
        for (int i = 0; i < h.Length; i++) h[i] = (float)(h[i] / sum);
        return h;
    }
}

/// <summary>
/// The interpolation of a voice: a windowed sinc of <see cref="Taps"/> points, tabulated at <see cref="Phases"/> offsets
/// (between two of them, a straight line). At offset 0 it is exactly the sample, so a note at its root plays the file
/// bit for bit.
/// </summary>
public static class SamplerKernel
{
    public const int Taps = 8, Phases = 128;
    /// <summary>The samples before the read point it uses; the rest come after.</summary>
    public const int Before = 3;
    public static readonly float[] Table = Make();

    /// <summary>The Kaiser window at <paramref name="x"/> (-1..1).</summary>
    internal static double Kaiser(double x, double beta) => Bessel(beta * Math.Sqrt(Math.Max(0, 1 - x * x))) / Bessel(beta);

    static double Bessel(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 32; k++)
        {
            term *= x / (2 * k) * (x / (2 * k));
            sum += term;
        }
        return sum;
    }

    static float[] Make()
    {
        var t = new float[(Phases + 1) * Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double f = p / (double)Phases, sum = 0;
            var c = new double[Taps];
            for (int j = 0; j < Taps; j++)
            {
                double d = j - Before - f;
                c[j] = (Math.Abs(d) < 1e-12 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d)) * Kaiser(d / (Taps / 2.0), 6);
                sum += c[j];
            }
            for (int j = 0; j < Taps; j++) t[p * Taps + j] = (float)(c[j] / sum);
        }
        // the sinc's zeros are only nearly zero in floating point
        for (int j = 0; j < Taps; j++)
        {
            t[j] = j == Before ? 1 : 0;
            t[Phases * Taps + j] = j == Before + 1 ? 1 : 0;
        }
        return t;
    }
}

/// <summary>
/// Works out a sample's root key: from its name if it has one ("Piano_C3", "Bass F#1"), or from its sound. Null when
/// neither says, as with a drum hit: then the note it was played at means nothing and it should not be tuned by ear.
/// </summary>
public static partial class SamplerRoot
{
    /// <summary>How clear a period has to be to count as a pitch (the dip of YIN's curve).</summary>
    const double Clear = 0.12;
    const double MinHz = 40, MaxHz = 1600;
    const int Window = 4096;
    /// <summary>The shortest stretch worth measuring: 42 ms in all at 48 kHz.</summary>
    const int MinWindow = 1024;

    /// <summary>A note name surrounded by separators; a letter inside a word doesn't count.</summary>
    [GeneratedRegex(@"(?:^|[\s_\-.\(\[])([A-Ga-g])([#b]?)(-?[0-8])(?:$|[\s_\-.\)\]])")]
    private static partial Regex NoteName();

    static readonly int[] Semitones = [9, 11, 0, 2, 4, 5, 7];

    /// <summary>The key a file's name says (C4 = 60, the same as the keyboards here), or null.</summary>
    public static int? FromName(string name)
    {
        var m = NoteName().Match(name);
        if (!m.Success) return null;
        int semi = Semitones[char.ToUpperInvariant(m.Groups[1].Value[0]) - 'A'] + (m.Groups[2].Value == "#" ? 1 : m.Groups[2].Value == "b" ? -1 : 0);
        int key = (int.Parse(m.Groups[3].Value) + 1) * 12 + semi;
        return key is >= 0 and <= 127 ? key : null;
    }

    /// <summary>
    /// The nearest key of the steady part of frames <c>[from .. to)</c> of the sound (all of it by default) and how many
    /// cents it is off that key: YIN, on what follows its loudest moment. Null without a clear pitch.
    /// </summary>
    public static (int Key, double Cents)? FromSound(float[] left, float[] right, int sampleRate, int from = 0, int to = -1)
    {
        int n = left.Length;
        to = to < 0 ? n : Math.Min(to, n);
        from = Math.Clamp(from, 0, to);
        int window = Math.Min(Window, (to - from) / 2);
        if (window < MinWindow) return null;
        // past the attack: a pluck's first milliseconds are noise
        int at = from;
        float peak = 0;
        for (int i = from; i < Math.Min(to, from + sampleRate); i++)
            if (Math.Abs(left[i]) > peak) { peak = Math.Abs(left[i]); at = i; }
        if (peak < 1e-3f) return null;
        at = Math.Min(at + sampleRate / 20, to - 2 * window);
        var x = new float[2 * window];
        for (int i = 0; i < x.Length; i++) x[i] = 0.5f * (left[at + i] + (right.Length == n ? right[at + i] : left[at + i]));

        int lo = Math.Max(2, (int)(sampleRate / MaxHz)), hi = Math.Min(window - 1, (int)(sampleRate / MinHz));
        var d = new double[hi + 2];
        double running = 0;
        d[0] = 1;
        for (int tau = 1; tau <= hi + 1; tau++)
        {
            double sum = 0;
            for (int i = 0; i < window; i++)
            {
                double e = x[i] - x[i + tau];
                sum += e * e;
            }
            running += sum;
            d[tau] = running > 0 ? sum * tau / running : 1;
        }
        int best = -1;
        for (int tau = lo; tau <= hi; tau++)
        {
            if (d[tau] >= Clear) continue;
            while (tau < hi && d[tau + 1] < d[tau]) tau++;
            best = tau;
            break;
        }
        if (best < 0) return null;
        double a = d[best - 1], b = d[best], c = d[best + 1], div = a - 2 * b + c;
        double period = best + (Math.Abs(div) > 1e-12 ? 0.5 * (a - c) / div : 0);
        double midi = 69 + 12 * Math.Log2(sampleRate / period / 440);
        int key = (int)Math.Round(midi);
        return key is >= 0 and <= 127 ? (key, (midi - key) * 100) : null;
    }
}
