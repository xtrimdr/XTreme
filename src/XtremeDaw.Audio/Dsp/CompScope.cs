namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// What a compressor shows on its graph, as the audio thread leaves it: every <see cref="Hop"/> frames, the peak that came
/// in, the peak that went out and the deepest reduction, in a ring of the last <see cref="Size"/> points (about 5 s). The
/// audio thread writes a point and then counts it (<see cref="Written"/>, volatile), so the UI reads only finished points
/// and never waits; nothing allocates. A point the writer may be overwriting is older than anything the graph draws.
/// </summary>
public sealed class CompScope
{
    /// <summary>Frames per point: 5.3 ms at 48 kHz, fine enough to see a drum hit's attack and release.</summary>
    public const int Hop = 256;
    public const int Size = 1024;
    /// <summary>The level written for silence, dB: the graph's floor is above it.</summary>
    public const float Floor = -120;

    readonly float[] inDb = new float[Size], outDb = new float[Size], grDb = new float[Size];
    volatile int written;
    int n;
    double inPeak, outPeak, gr;

    /// <summary>How many points have been written in all; the newest is at <c>Written - 1</c>.</summary>
    public int Written => written;

    static float Db(double x) => x > 1e-6 ? (float)(20 * Math.Log10(x)) : Floor;

    /// <summary>One frame: its input and output peaks (linear, both sides) and its reduction (dB, &lt;= 0). Audio thread.</summary>
    public void Add(double inAbs, double outAbs, double grNow)
    {
        if (inAbs > inPeak) inPeak = inAbs;
        if (outAbs > outPeak) outPeak = outAbs;
        if (grNow < gr) gr = grNow;
        if (++n < Hop) return;
        int w = written, at = w & (Size - 1);
        inDb[at] = Db(inPeak);
        outDb[at] = Db(outPeak);
        grDb[at] = (float)gr;
        written = w + 1;
        n = 0;
        inPeak = outPeak = gr = 0;
    }

    /// <summary>Point <paramref name="index"/> (0 = the first ever written): in, out and reduction, dB. UI thread.</summary>
    public void Read(int index, out float input, out float output, out float reduction)
    {
        int at = index & (Size - 1);
        input = inDb[at];
        output = outDb[at];
        reduction = grDb[at];
    }
}

/// <summary>
/// A compressor that can draw itself: its <see cref="Scope"/> and its static curve at the settings it has now, so the graph
/// shows what it does (the curve) and what it is doing (the points). The curve is for a steady tone, by its peak.
/// </summary>
public interface ICompGraph
{
    CompScope Scope { get; }
    /// <summary>The output level (dBFS, with its gain) for a steady tone with this peak at the slot's input. UI thread.</summary>
    double CurveOutDb(double inDb);
}
