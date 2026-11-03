using NAudio.Wave;

namespace XtremeDaw.Audio;

/// <summary>
/// Loads audio files into stereo float32 planar buffers at the engine's sample rate (48 kHz by default),
/// matching Web Audio's <c>decodeAudioData</c> behavior in the web app's <c>mixGraph.ts</c>.
/// Used by both live playback (<c>MediaAudio</c> / <c>LiveTrack</c>) and offline rendering (<c>xtlab mezclar</c>
/// and export).
/// <para>
/// Known difference vs. the web app: when a source file is not at <c>targetSampleRate</c> (e.g. 44.1 kHz
/// imported into a 48 kHz project), <see cref="Resample"/> uses a 32-tap Blackman-windowed sinc filter
/// in C#, which preserves duration and pitch but does not match Chromium's internal <c>SincResampler</c>
/// sample for sample. Files recorded at <c>targetSampleRate</c> (48 kHz) pass through untouched.
/// </para>
/// </summary>
public static class AudioLoader
{
    public const int DefaultSampleRate = 48000;

    /// <param name="Converted">It came from <see cref="MediaDecoder"/>'s FFmpeg: a project keeps these samples as a WAV.</param>
    public readonly record struct LoadedAudio(float[] Left, float[] Right, int SampleRate, int SourceSampleRate, int SourceChannels, bool Converted = false)
    {
        public double DurationSec => SampleRate > 0 ? Left.Length / (double)SampleRate : 0;
    }

    /// <summary>What part of a load is decoding when the file also has to be resampled; the rest is resampling.</summary>
    const double DecodeShare = 0.6;

    /// <summary>
    /// Reads an audio file (WAV, MP3, etc.) into planar stereo float32 arrays and resamples to
    /// <paramref name="targetSampleRate"/> if the file's sample rate differs. <paramref name="progress"/> goes
    /// from 0 to 1, from whichever thread is working. With <paramref name="maxSec"/> it stops after that much of the file.
    /// </summary>
    public static LoadedAudio Load(string path, int targetSampleRate = DefaultSampleRate, Action<double>? progress = null, double maxSec = 0)
    {
        float[] l;
        float[] r;
        int srcRate, ch;
        using (var reader = new AudioFileReader(path))
        {
            srcRate = reader.WaveFormat.SampleRate;
            ch = reader.WaveFormat.Channels;
            double share = targetSampleRate > 0 && srcRate != targetSampleRate ? DecodeShare : 1;
            // The reader's Length is only a hint: Media Foundation (FLAC, M4A…) can report less than the file
            // holds, so read until the decoder runs dry and grow the buffer when the hint was short.
            long limit = maxSec > 0 ? (long)(maxSec * srcRate) * ch : long.MaxValue;
            int hint = (int)Math.Min(limit, Math.Max(65536, reader.Length / 4));
            var all = new float[hint];
            int read = 0, n;
            while (read < limit)
            {
                if (read == all.Length) Array.Resize(ref all, all.Length * 2);
                if ((n = reader.Read(all, read, Math.Min(65536, all.Length - read))) <= 0) break;
                read += n;
                progress?.Invoke(Math.Min(1, read / (double)hint) * share);
            }
            int len = read / ch;
            l = new float[len];
            r = new float[len];
            for (int i = 0; i < len; i++)
            {
                l[i] = all[i * ch];
                r[i] = ch > 1 ? all[i * ch + 1] : l[i];
            }
        }

        if (targetSampleRate > 0 && srcRate != targetSampleRate)
        {
            (l, r) = Resample(l, r, srcRate, targetSampleRate, progress == null ? null : p => progress(DecodeShare + (1 - DecodeShare) * p));
            return new LoadedAudio(l, r, targetSampleRate, srcRate, ch);
        }

        return new LoadedAudio(l, r, srcRate, srcRate, ch);
    }

    /// <summary>
    /// Resamples stereo planar buffers from <paramref name="srcRate"/> to <paramref name="dstRate"/>
    /// using a 32-tap Blackman-windowed sinc filter.
    /// </summary>
    public static (float[] Left, float[] Right) Resample(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        int srcRate,
        int dstRate) => Resample(left.ToArray(), right.ToArray(), srcRate, dstRate);

    /// <summary>
    /// The resampler proper. The source positions of a rational ratio only ever fall on
    /// <c>dstRate / gcd</c> fractions of a sample, so the filter is worked out once per fraction: computing it per
    /// sample (a sine and two cosines for each of the 32 taps) took 11 s for 5 minutes of music, which is what
    /// opening a project or importing a 44.1 kHz file waited for. Blocks of output run on several cores, leaving one
    /// free for the audio thread.
    /// </summary>
    public static (float[] Left, float[] Right) Resample(float[] left, float[] right, int srcRate, int dstRate, Action<double>? progress = null)
    {
        if (srcRate == dstRate || left.Length == 0)
            return ((float[])left.Clone(), (float[])right.Clone());

        int srcLen = left.Length;
        int dstLen = Math.Max(1, (int)Math.Round((long)srcLen * dstRate / (double)srcRate));
        var outL = new float[dstLen];
        var outR = new float[dstLen];

        const int halfTaps = 16, taps = 2 * halfTaps;
        const int blockFrames = 1 << 16;
        int gcd = (int)System.Numerics.BigInteger.GreatestCommonDivisor(srcRate, dstRate);
        int phases = dstRate / gcd, step = srcRate / gcd;
        var filter = SincTable(phases, Math.Min(1.0, dstRate / (double)srcRate) * 0.92, halfTaps);

        int blocks = (dstLen + blockFrames - 1) / blockFrames, finished = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, blocks, options, b =>
        {
            for (int i = b * blockFrames, end = Math.Min(dstLen, i + blockFrames); i < end; i++)
            {
                long pos = i * (long)step;
                int center = (int)(pos / phases);
                int first = center - halfTaps + 1;
                int t = (int)(pos % phases) * taps;
                double sumL = 0, sumR = 0;
                if (first >= 0 && first + taps <= srcLen)
                {
                    for (int k = 0; k < taps; k++)
                    {
                        double c = filter[t + k];
                        sumL += left[first + k] * c;
                        sumR += right[first + k] * c;
                    }
                }
                else
                {
                    // the ends of the file: what falls outside counts as silence
                    for (int k = 0; k < taps; k++)
                    {
                        if ((uint)(first + k) >= (uint)srcLen) continue;
                        double c = filter[t + k];
                        sumL += left[first + k] * c;
                        sumR += right[first + k] * c;
                    }
                }
                outL[i] = (float)sumL;
                outR[i] = (float)sumR;
            }
            progress?.Invoke(Interlocked.Increment(ref finished) / (double)blocks);
        });

        return (outL, outR);
    }

    /// <summary>The filter for each fraction of a sample, one after the other, normalized to unity gain.</summary>
    static double[] SincTable(int phases, double cutoff, int halfTaps)
    {
        int taps = 2 * halfTaps;
        var table = new double[phases * taps];
        for (int p = 0; p < phases; p++)
        {
            double frac = p / (double)phases, sum = 0;
            for (int j = 0; j < taps; j++)
            {
                double x = j - halfTaps + 1 - frac;
                double wPos = (x + halfTaps) / (2.0 * halfTaps);
                double win = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * wPos) + 0.08 * Math.Cos(4.0 * Math.PI * wPos);
                double cx = x * cutoff;
                double sinc = Math.Abs(cx) < 1e-8 ? 1.0 : Math.Sin(Math.PI * cx) / (Math.PI * cx);
                table[p * taps + j] = sinc * win * cutoff;
                sum += table[p * taps + j];
            }
            if (sum > 1e-9)
                for (int j = 0; j < taps; j++) table[p * taps + j] /= sum;
        }
        return table;
    }
}
