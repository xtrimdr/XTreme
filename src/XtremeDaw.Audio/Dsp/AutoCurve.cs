using XtremeDaw.Audio.Vst;

namespace XtremeDaw.Audio.Dsp;

/// <summary>How a curve's position (0..1, what a lane draws) becomes the parameter's value.</summary>
public enum AutoLaw { Linear, Fader, Pan }

/// <summary>
/// One automation lane as the engine reads it: its points in seconds with their position (0..1) in straight lines, or
/// held until the next point when <paramref name="stepped"/> (a switch). <paramref name="bend"/> gives each stretch its
/// curve, by the point it starts at (<see cref="Shape"/>); null = all straight. Before the first point and after the last
/// it holds their value. Immutable, so the audio thread can hold it; <see cref="At"/> never allocates.
/// </summary>
public sealed class AutoCurve(double[] sec, float[] position, bool stepped = false, AutoLaw law = AutoLaw.Linear, float[]? bend = null)
{
    /// <summary>A full bend's exponent: 1 goes through the stretch as t⁴, -1 as t^¼, 0 straight.</summary>
    public const double BendBase = 4;

    /// <summary>How far along a stretch's height it is at <paramref name="t"/> (0..1) of its length, with its bend (-1..1).</summary>
    public static double Shape(double t, double bend) => bend == 0 ? t : Math.Pow(t, Math.Pow(BendBase, bend));

    /// <summary>The fader's top: the same 1.5 as <c>Fader.VolMax</c>, with its square-root travel.</summary>
    const double FaderMax = 1.5;

    public int Count => sec.Length;

    /// <summary>The curve's position (0..1) at a time.</summary>
    public double PositionAt(double t)
    {
        int n = sec.Length;
        if (n == 0) return 0;
        if (t <= sec[0]) return position[0];
        if (t >= sec[n - 1]) return position[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (sec[mid] <= t) lo = mid;
            else hi = mid;
        }
        if (stepped) return position[lo];
        double span = sec[hi] - sec[lo];
        if (span <= 0) return position[hi];
        double along = (t - sec[lo]) / span;
        return position[lo] + (position[hi] - position[lo]) * (bend == null ? along : Shape(along, bend[lo]));
    }

    /// <summary>The parameter's value at a time, through its law.</summary>
    public double At(double t)
    {
        double k = PositionAt(t);
        return law switch
        {
            AutoLaw.Fader => FaderMax * k * k,
            AutoLaw.Pan => k * 2 - 1,
            _ => k,
        };
    }
}

/// <summary>What a track's fader follows: its volume, pan and mute lanes (null = the channel's own setting). Swapped whole.</summary>
public sealed record TrackAutomation(AutoCurve? Volume, AutoCurve? Pan, AutoCurve? Mute);

/// <summary>
/// A VST3 plugin's automated parameters. Each block <see cref="Push"/> hands the ones that moved to the bridge, which
/// carries them to the plugin with the block (<see cref="VstBridge.QueueParam"/>), live and in the export alike. It
/// remembers what the plugin has, so swapped whole it sends everything once. Audio thread (or the export's). Never allocates.
/// </summary>
public sealed class VstParamCurves(uint[] ids, AutoCurve[] curves)
{
    /// <summary>A move under this (of 0..1) isn't worth a message to the plugin.</summary>
    const float Least = 1f / 4096;
    readonly float[] sent = Filled(ids.Length);
    volatile bool again;

    static float[] Filled(int n)
    {
        var a = new float[n];
        Array.Fill(a, float.NaN);
        return a;
    }

    /// <summary>The plugin forgot where its parameters were (the transport started): they all go again.</summary>
    public void Resend() => again = true;

    public void Push(VstBridge bridge, long frame)
    {
        if (again)
        {
            again = false;
            Array.Fill(sent, float.NaN);
        }
        double t = frame / (double)bridge.SampleRate;
        for (int i = 0; i < ids.Length; i++)
        {
            float v = (float)curves[i].PositionAt(t);
            if (Math.Abs(v - sent[i]) < Least) continue; // false for NaN: the first time it always goes
            if (!bridge.QueueParam(ids[i], v)) return;
            sent[i] = v;
        }
    }
}
