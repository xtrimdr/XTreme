using XtremeDaw.Audio.Vst;

namespace XtremeDaw.Audio.Dsp;

/// <summary>How a curve's position (0..1, what a lane draws) becomes the parameter's value.</summary>
public enum AutoLaw { Linear, Fader, Pan }

/// <summary>
/// How a stretch goes from one point to the next (as in FL Studio, asked by the user), each with its tension (-1..1):
/// a curve (0 = straight), a double curve (an S), a hold, stairs, smooth stairs, pulses and a wave; the last four take
/// their number of steps or cycles from the tension (<see cref="AutoShapes.Count"/>). Saved by name in the project.
/// </summary>
public enum AutoShape : byte { Curve, Double, Hold, Stairs, SmoothStairs, Pulse, Wave }

/// <summary>
/// The shapes' math, shared by the engine, the timeline and the project. A curve is an exponential,
/// (e^(k·t) − 1) / (e^k − 1) with k = tension × <see cref="CurveK"/>: any piece of it is the same curve with a smaller k,
/// so cutting a block keeps it exact, and a double curve is two of them (k, then −k). Never allocates.
/// </summary>
public static class AutoShapes
{
    /// <summary>A full tension's exponent: at the middle of the stretch it is 0.7 % (or 99.3 %) of the way.</summary>
    public const double CurveK = 10;
    /// <summary>The steps or cycles at tension -1, 0 and 1: 2, 8 and 32.</summary>
    const double CountAt0 = 3, CountSpan = 2;

    public static int Count(double tension) => (int)Math.Round(Math.Pow(2, CountAt0 + CountSpan * Math.Clamp(tension, -1, 1)));

    /// <summary>How far along the stretch's height it is at <paramref name="t"/> (0..1) of its length.</summary>
    public static double Along(AutoShape shape, double t, double tension)
    {
        switch (shape)
        {
            case AutoShape.Hold:
                return t >= 1 ? 1 : 0;
            case AutoShape.Double:
                return t < 0.5 ? 0.5 * Exp(2 * t, tension) : 1 - 0.5 * Exp(2 - 2 * t, tension);
            case AutoShape.Stairs:
            {
                int n = Count(tension);
                return Math.Min(Math.Floor(t * n), n - 1) / (n - 1);
            }
            case AutoShape.SmoothStairs:
            {
                int n = Count(tension);
                double x = t * n, i = Math.Min(Math.Floor(x), n - 1);
                return (i + (1 - Math.Cos(Math.PI * Math.Min(1, x - i))) / 2) / n;
            }
            case AutoShape.Pulse:
            {
                // n pulses: down, up, ... ending up, so it starts and ends on its points
                int m = 2 * Count(tension);
                return (int)Math.Min(Math.Floor(t * m), m - 1) % 2;
            }
            case AutoShape.Wave:
                return (1 - Math.Cos(Math.PI * (2 * Count(tension) - 1) * t)) / 2;
            default:
                return Exp(t, tension);
        }
    }

    /// <summary>The single curve; positive tension starts slow.</summary>
    public static double Exp(double t, double tension)
    {
        double k = tension * CurveK;
        return Math.Abs(k) < 1e-6 ? t : (Math.Exp(k * t) - 1) / (Math.Exp(k) - 1);
    }

    /// <summary>The curve's tension that goes through <paramref name="along"/> (0..1) at the middle of its stretch.</summary>
    public static double TensionThrough(double along) =>
        Math.Clamp(2 * Math.Log(1 / Math.Clamp(along, 1e-4, 1 - 1e-4) - 1) / CurveK, -1, 1);

    /// <summary>The flat pieces a stepped shape is made of (its jumps go between them); 0 for the smooth ones.</summary>
    public static int Pieces(AutoShape shape, double tension) => shape switch
    {
        AutoShape.Hold => 1,
        AutoShape.Stairs => Count(tension),
        AutoShape.Pulse => 2 * Count(tension),
        _ => 0,
    };

    /// <summary>Whether its tension does something.</summary>
    public static bool Tensed(AutoShape shape) => shape != AutoShape.Hold;
}

/// <summary>
/// One automation lane as the engine reads it: its points in seconds with their position (0..1), each stretch with its
/// shape and tension by the point it starts at (<see cref="AutoShapes"/>; null = all straight), or held until the next
/// point when <paramref name="stepped"/> (a switch). Before the first point and after the last it holds their value.
/// Immutable, so the audio thread can hold it; <see cref="At"/> never allocates.
/// </summary>
public sealed class AutoCurve(double[] sec, float[] position, bool stepped = false, AutoLaw law = AutoLaw.Linear, AutoShape[]? shapes = null, float[]? tension = null)
{
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
        return position[lo] + (position[hi] - position[lo]) * (shapes == null ? along : AutoShapes.Along(shapes[lo], along, tension![lo]));
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
