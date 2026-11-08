namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// What a native effect is doing to the signal, for its picture (asked by the user, #312: to see how each one reacts to
/// the wave). The audio thread leaves two rings: every <see cref="Hop"/> frames the peak that came in and the one that went
/// out (about 11 s, for a level history), and every <see cref="Decim"/>th frame the samples themselves, in and out (mono)
/// and the output's L and R (a third of a second, for a scope, a transfer cloud or a goniometer). Optionally it feeds a
/// <see cref="SpectrumTap"/> with the output. The window creates it only for a slot it shows and calls <see cref="Watch"/>
/// on every tick: unwatched for a second, the audio thread skips it, so a closed window costs nothing. Written counts are
/// volatile and the writer counts a value only after writing it, so the UI never waits; nothing allocates.
/// </summary>
public sealed class FxTap(int sampleRate)
{
    /// <summary>Frames per level point: 5.3 ms at 48 kHz.</summary>
    public const int Hop = 256;
    /// <summary>Level points kept (a power of two): about 11 s at 48 kHz, as long as a long reverb's picture.</summary>
    public const int Points = 2048;
    /// <summary>One sample pair kept every this many frames: 12 kHz at 48 kHz, enough to draw a wave.</summary>
    public const int Decim = 4;
    /// <summary>Sample pairs kept (a power of two): a third of a second at 48 kHz.</summary>
    public const int Pairs = 4096;
    /// <summary>The level written for silence, dB.</summary>
    public const float Floor = -120;
    /// <summary>How long after the last <see cref="Watch"/> the audio thread still feeds it.</summary>
    const long WatchMs = 1000;

    readonly float[] inDb = new float[Points], outDb = new float[Points];
    readonly float[] inS = new float[Pairs], outS = new float[Pairs], outL = new float[Pairs], outR = new float[Pairs];
    volatile int written, pairs;
    volatile SpectrumTap? spectrum;
    long seen = long.MinValue / 2;
    int n, d;
    double inPeak, outPeak;

    /// <summary>Seconds between two level points.</summary>
    public double PointSec { get; } = Hop / (double)Math.Max(1, sampleRate);
    /// <summary>Seconds between two sample pairs.</summary>
    public double PairSec { get; } = Decim / (double)Math.Max(1, sampleRate);
    /// <summary>Level points written in all; the newest is at <c>Written - 1</c>.</summary>
    public int Written => written;
    /// <summary>Sample pairs written in all; the newest is at <c>PairsWritten - 1</c>.</summary>
    public int PairsWritten => pairs;
    /// <summary>A spectrum of the output, for a tone effect's picture; set by the UI, written while watched.</summary>
    public SpectrumTap? Spectrum { get => spectrum; set => spectrum = value; }

    /// <summary>Someone is looking: keep feeding for a second more. UI thread.</summary>
    public void Watch() => Volatile.Write(ref seen, Environment.TickCount64);

    /// <summary>Whether a picture looked in the last second. Audio thread, before it copies anything for <see cref="Add"/>.</summary>
    public bool Watched => Environment.TickCount64 - Volatile.Read(ref seen) < WatchMs;

    static float Db(double x) => x > 1e-6 ? (float)(20 * Math.Log10(x)) : Floor;

    /// <summary>One block, what came in and what went out (same length, aligned in time). Audio thread.</summary>
    public void Add(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, ReadOnlySpan<float> oL, ReadOnlySpan<float> oR)
    {
        int len = Math.Min(Math.Min(inL.Length, inR.Length), Math.Min(oL.Length, oR.Length));
        for (int i = 0; i < len; i++)
        {
            double a = Math.Max(Math.Abs(inL[i]), Math.Abs(inR[i])), b = Math.Max(Math.Abs(oL[i]), Math.Abs(oR[i]));
            if (a > inPeak) inPeak = a;
            if (b > outPeak) outPeak = b;
            if (++n >= Hop)
            {
                int w = written, at = w & (Points - 1);
                inDb[at] = Db(inPeak);
                outDb[at] = Db(outPeak);
                written = w + 1;
                n = 0;
                inPeak = outPeak = 0;
            }
            if (++d >= Decim)
            {
                d = 0;
                int p = pairs, at = p & (Pairs - 1);
                inS[at] = 0.5f * (inL[i] + inR[i]);
                outS[at] = 0.5f * (oL[i] + oR[i]);
                outL[at] = oL[i];
                outR[at] = oR[i];
                pairs = p + 1;
            }
        }
        spectrum?.Write(oL[..len], oR[..len]);
    }

    /// <summary>Level point <paramref name="index"/> (0 = the first ever written): its peaks in and out, dB. UI thread.</summary>
    public void ReadPoint(int index, out float input, out float output)
    {
        int at = index & (Points - 1);
        input = inDb[at];
        output = outDb[at];
    }

    /// <summary>Sample pair <paramref name="index"/>: in and out (mono) and the output's sides. UI thread.</summary>
    public void ReadPair(int index, out float input, out float output, out float left, out float right)
    {
        int at = index & (Pairs - 1);
        input = inS[at];
        output = outS[at];
        left = outL[at];
        right = outR[at];
    }
}
