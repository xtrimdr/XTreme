namespace XtremeDaw.App.Audio;

/// <summary>
/// The window's short cues, made once in code (there are no sound files): two soft sine notes, rising when someone
/// joins a shared session and falling when someone leaves (#291, asked by the user). Quiet (about -18 dBFS), with a
/// 5 ms attack and a decay, so they never click; played through <see cref="PlaybackEngine.PlayCue"/>.
/// </summary>
public static class CueSounds
{
    /// <summary>Each note's level, linear (about -18 dBFS).</summary>
    const float Level = 0.12f;
    /// <summary>How long each note rings, and when the second one starts (s).</summary>
    const double NoteSec = 0.22, StepSec = 0.11;

    static float[]? joined, left;

    /// <summary>Up a fourth: E5 then A5.</summary>
    public static float[] Joined => joined ??= Two(659.26, 880.0);
    /// <summary>Down a fourth: A5 then E5.</summary>
    public static float[] Left => left ??= Two(880.0, 659.26);

    static float[] Two(double first, double second)
    {
        int sr = PlaybackEngine.SampleRate;
        var buf = new float[(int)((StepSec + NoteSec) * sr)];
        Note(buf, 0, first, sr);
        Note(buf, (int)(StepSec * sr), second, sr);
        return buf;
    }

    static void Note(float[] buf, int at, double hz, int sr)
    {
        int n = Math.Min(buf.Length - at, (int)(NoteSec * sr));
        int attack = sr / 200;
        for (int i = 0; i < n; i++)
        {
            double env = Math.Min(1.0, i / (double)attack) * Math.Exp(-6.0 * i / n) * (1 - i / (double)n);
            buf[at + i] += (float)(Level * env * Math.Sin(2 * Math.PI * hz * i / sr));
        }
    }
}
