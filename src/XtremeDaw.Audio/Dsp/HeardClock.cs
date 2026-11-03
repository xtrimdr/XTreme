namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// Where the song is being heard, given where the engine is reading it: with the delay compensation (PDC) the master comes
/// out that many frames after the tracks read them, so the picture and the playhead have to wait as much. Straight after a
/// jump the master is still putting out what came before it, so the plain subtraction lands where nothing is playing: after
/// a start or a seek the playhead holds at the target until its sound arrives (it never steps back), and after a loop's
/// turn it finishes the lap it was in. Written by the audio thread only (a sequence counter, so it never waits and the UI
/// never reads half a jump), read from any thread. Never allocates.
/// </summary>
public sealed class HeardClock
{
    /// <summary>Reads that may run into a write before it gives up on that tick's exact value; a write lasts nanoseconds.</summary>
    const int MaxTries = 64;

    long start, lap;
    int version;

    /// <summary>The engine started reading at <paramref name="frame"/> (play, a seek): nothing before it will be heard.</summary>
    public void Start(long frame) => Set(frame, 0);

    /// <summary>A loop turned to <paramref name="start"/>: what is still on its way out is the end of a lap of <paramref name="lap"/> frames.</summary>
    public void Turn(long start, long lap) => Set(start, Math.Max(0, lap));

    void Set(long s, long l)
    {
        Interlocked.Increment(ref version);
        Volatile.Write(ref start, s);
        Volatile.Write(ref lap, l);
        Interlocked.Increment(ref version);
    }

    /// <summary>The next frame to be heard when the engine reads at <paramref name="pos"/> and the mix is <paramref name="delay"/> frames late.</summary>
    public long At(long pos, int delay)
    {
        long s, l;
        int v, tries = 0;
        do
        {
            v = Volatile.Read(ref version);
            s = Volatile.Read(ref start);
            l = Volatile.Read(ref lap);
        } while (((v & 1) != 0 || v != Volatile.Read(ref version)) && ++tries < MaxTries);
        long heard = pos - delay;
        if (heard >= s) return heard;
        // back through the laps, so a loop shorter than the delay stays inside it too
        return l <= 0 ? s : s + l - 1 - (s - heard - 1) % l;
    }
}
