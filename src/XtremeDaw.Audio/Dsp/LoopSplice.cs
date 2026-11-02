namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// How a loop goes from its end back to its start without a seam (asked by the user: the first hit of a lap has to come in
/// whole). A jump's crossfade would fade the start of the loop in, so this one ends where the seek's begins: over the
/// last <see cref="Frames"/> before B the audio before A comes up under the audio before B, and at B it is A itself, at
/// full level, from its first sample. Both are read from the same block, so the lap turns on the exact sample, wherever
/// the block ends, and nothing of the start is skipped. It keeps no state: each block works it out from the position.
/// </summary>
public static class LoopSplice
{
    /// <summary>5 ms: enough for a cut in mid-note not to click, short enough not to take from the end of the lap.</summary>
    public const int Frames = 240;

    /// <summary>
    /// Fills the weights of the block at <paramref name="pos"/>: <paramref name="wMain"/> for the song where it goes,
    /// <paramref name="wLap"/> for the same block one lap back. False, and nothing to read twice, when the block doesn't reach the splice.
    /// </summary>
    public static bool Weights(long pos, int n, long start, long end, Span<float> wMain, Span<float> wLap)
    {
        int len = (int)Math.Min(Frames, (end - start) / 2);
        if (len <= 0 || pos >= end || pos + n <= end - len) return false;
        for (int i = 0; i < n; i++)
        {
            float x = Math.Clamp((pos + i - (end - len) + 1) / (float)len, 0, 1);
            wMain[i] = MathF.Cos(x * MathF.PI / 2);
            wLap[i] = MathF.Sin(x * MathF.PI / 2);
        }
        return true;
    }

    /// <summary>Where the song is after a block that started at <paramref name="blockStart"/> and ends at <paramref name="pos"/>: past the end, that far into the next lap.</summary>
    public static bool Turned(long blockStart, long pos, long start, long end, out long to)
    {
        to = pos - (end - start);
        return end > start && blockStart < end && pos >= end;
    }
}
