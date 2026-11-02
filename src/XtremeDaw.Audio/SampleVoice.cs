using NAudio.Wave;

namespace XtremeDaw.Audio;

/// <summary>
/// What the Biblioteca's sample preview plays: one sound in memory, at the engine's rate. It sums straight to the output
/// like the click, after the master and its limiter, so it is heard through the audio interface (a Windows output may
/// not even be the same device) while the project plays or stands still, and it can never end up in a take or an export.
/// The UI only leaves requests in fields the audio thread takes at the start of a block; every start, jump, stop and
/// loop turn fades over <see cref="Fade"/> frames, so the preview never clicks. With no driver open it runs on a Windows
/// output instead (<see cref="UseWindowsOutput"/>), through the same <see cref="Mix"/>.
/// </summary>
public sealed class SampleVoice : IDisposable
{
    /// <summary>5 ms at the engine's rate.</summary>
    const int Fade = 240;
    const long NoSeek = -1;

    sealed record Sound(float[] Left, float[] Right);

    // UI to audio
    Sound? pending;
    int pendingStop;
    long pendingSeek = NoSeek;
    volatile float gain = 1;
    volatile bool loop, paused;
    // audio thread only: what plays, and what it cut, fading out
    Sound? sound, cut;
    int pos, cutPos, cutLeft, fadeIn;
    bool wasPaused;
    // audio to UI
    long position;
    volatile bool active;
    WaveOutEvent? windows;

    public bool Active => active || Volatile.Read(ref pending) != null;
    public bool Paused { get => paused; set => paused = value; }
    public bool Loop { get => loop; set => loop = value; }
    public double PositionSec => Interlocked.Read(ref position) / (double)AudioLoader.DefaultSampleRate;
    public double Gain { set => gain = (float)value; }
    public bool OnWindows => windows != null;

    /// <summary>Starts a sound from its beginning, cutting the one before. <paramref name="left"/> and <paramref name="right"/> aren't copied.</summary>
    public void Play(float[] left, float[] right)
    {
        paused = false;
        Volatile.Write(ref pending, new Sound(left, right));
    }

    public void Stop()
    {
        Volatile.Write(ref pending, null);
        paused = false;
        Volatile.Write(ref pendingStop, 1);
    }

    public void Seek(double sec) => Interlocked.Exchange(ref pendingSeek, (long)(Math.Max(0, sec) * AudioLoader.DefaultSampleRate));

    /// <summary>Audio thread. Adds this block to the output.</summary>
    public void Mix(float[] left, float[] right, int n)
    {
        if (Interlocked.Exchange(ref pendingStop, 0) != 0) { Cut(); sound = null; }
        if (Interlocked.Exchange(ref pending, null) is { } next)
        {
            Cut();
            sound = next;
            pos = 0;
            fadeIn = Fade;
            wasPaused = false;
        }
        long seek = Interlocked.Exchange(ref pendingSeek, NoSeek);
        if (seek != NoSeek && sound is { } s0)
        {
            Cut();
            pos = (int)Math.Min(seek, Math.Max(0, s0.Left.Length - 1));
            fadeIn = Fade;
        }
        if (paused != wasPaused)
        {
            if (paused) Cut();
            else fadeIn = Fade;
            wasPaused = paused;
        }
        float g = gain;
        MixCut(left, right, n, g);
        if (sound is { } s && !paused) MixSound(s, left, right, n, g);
        Interlocked.Exchange(ref position, pos);
        active = sound != null || cut != null;
    }

    /// <summary>What was playing goes on fading out, from where it was, while the new position fades in.</summary>
    void Cut()
    {
        if (sound == null || wasPaused) return;
        cut = sound;
        cutPos = pos;
        cutLeft = Fade;
    }

    void MixCut(float[] left, float[] right, int n, float g)
    {
        if (cut is not { } c) return;
        int k = Math.Min(n, Math.Min(cutLeft, c.Left.Length - cutPos));
        for (int i = 0; i < k; i++)
        {
            float v = g * (cutLeft - i) / Fade;
            left[i] += c.Left[cutPos + i] * v;
            right[i] += c.Right[cutPos + i] * v;
        }
        cutPos += k;
        cutLeft -= k;
        if (cutLeft <= 0 || cutPos >= c.Left.Length) cut = null;
    }

    void MixSound(Sound s, float[] left, float[] right, int n, float g)
    {
        int len = s.Left.Length;
        for (int i = 0; i < n; i++)
        {
            if (pos >= len)
            {
                if (!loop)
                {
                    sound = null;
                    pos = 0;
                    return;
                }
                pos = 0;
                fadeIn = Fade;
            }
            // the sound's own ends may be cut hard: they fade too, which is also what hides the join of a loop
            float env = Math.Min(1, (len - pos) / (float)Fade);
            if (fadeIn > 0) env *= 1 - fadeIn-- / (float)Fade;
            left[i] += s.Left[pos] * g * env;
            right[i] += s.Right[pos] * g * env;
            pos++;
        }
    }

    /// <summary>Starts the Windows output; for when no driver is open. The first run blocks for a moment: UI gestures only.</summary>
    public void UseWindowsOutput()
    {
        if (windows != null) return;
        var o = new WaveOutEvent { DesiredLatency = 150 };
        try
        {
            o.Init(new Output(this));
            o.Play();
            windows = o;
        }
        catch (NAudio.MmException) { o.Dispose(); }
    }

    /// <summary>The driver opened meanwhile: the engine mixes it from now on, and two callers would play it twice as fast.</summary>
    public void DropWindowsOutput()
    {
        var o = windows;
        windows = null;
        o?.Dispose();
    }

    public void Dispose() => DropWindowsOutput();

    /// <summary>Hands <see cref="Mix"/> to a Windows output, never ending: it stays silent between sounds.</summary>
    sealed class Output(SampleVoice voice) : ISampleProvider
    {
        const int Chunk = 2048;
        readonly float[] l = new float[Chunk], r = new float[Chunk];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioLoader.DefaultSampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int done = 0; done < count;)
            {
                int n = Math.Min(Chunk, (count - done) / 2);
                Array.Clear(l, 0, n);
                Array.Clear(r, 0, n);
                voice.Mix(l, r, n);
                for (int i = 0; i < n; i++)
                {
                    buffer[offset + done++] = l[i];
                    buffer[offset + done++] = r[i];
                }
            }
            return count;
        }
    }
}
