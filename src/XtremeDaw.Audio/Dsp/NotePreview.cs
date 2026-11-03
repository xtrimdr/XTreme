namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// The test tone of the piano roll's keys and new notes on a track without an instrument (<see cref="NoteSynth.Audition"/>).
/// It belongs to the track and adds to what the track plays before its chain, so a note heard while writing already has
/// the track's slots, fader and sends, like an instrument's. It isn't a played key: it doesn't reach a take, the keyboard's
/// scale or the arpeggiator. <see cref="Post"/> comes from the UI, which builds the note there; <see cref="Mix"/> runs
/// on the audio thread, takes it whole with an exchange and allocates nothing. Each note cuts the one before with a short fade.
/// </summary>
public sealed class NotePreview(int sampleRate)
{
    /// <summary>How long a note cut by the next one takes to fade out, so it doesn't click.</summary>
    readonly int cutFade = (int)(0.005 * sampleRate);
    /// <summary>How long the chain keeps running after the note ends: a reverb slot's tail rings out.</summary>
    readonly long tailFrames = 3L * sampleRate;

    float[]? pending;
    // audio thread only: the note being heard and where it is, and the one it cut, fading out
    float[]? note, cut;
    int notePos, cutPos, cutLeft;
    long tail;

    /// <summary>A note to hear now, mono. UI thread; the array isn't touched again.</summary>
    public void Post(float[] samples) => Volatile.Write(ref pending, samples);

    /// <summary>Something to add, or a tail to let ring: the track has to run while the song is stopped.</summary>
    public bool Active => Volatile.Read(ref pending) != null || note != null || cut != null || tail > 0;

    /// <summary>Adds the next <paramref name="n"/> frames to <paramref name="l"/> and <paramref name="r"/>. Audio thread.</summary>
    public void Mix(int n, Span<float> l, Span<float> r)
    {
        if (Interlocked.Exchange(ref pending, null) is { } next)
        {
            if (note != null)
            {
                cut = note;
                cutPos = notePos;
                cutLeft = cutFade;
            }
            note = next;
            notePos = 0;
        }
        if (cut is { } fading)
        {
            int k = Math.Min(n, Math.Min(cutLeft, fading.Length - cutPos));
            for (int i = 0; i < k; i++)
            {
                float v = fading[cutPos + i] * (cutLeft - i) / cutFade;
                l[i] += v;
                r[i] += v;
            }
            cutPos += k;
            cutLeft -= k;
            if (cutLeft <= 0 || cutPos >= fading.Length) cut = null;
        }
        if (note is { } heard)
        {
            int k = Math.Min(n, heard.Length - notePos);
            for (int i = 0; i < k; i++)
            {
                float v = heard[notePos + i];
                l[i] += v;
                r[i] += v;
            }
            notePos += k;
            if (notePos >= heard.Length) note = null;
        }
        if (note != null || cut != null) tail = tailFrames;
        else if (tail > 0) tail = Math.Max(0, tail - n);
    }
}
