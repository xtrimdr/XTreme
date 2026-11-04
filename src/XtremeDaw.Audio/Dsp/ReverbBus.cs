using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// Port of <c>ReverbSettings</c> in the web app's <c>types.ts</c>.
/// </summary>
public readonly record struct ReverbParams(
    double Decay,
    double PreDelayMs,
    double Tone,
    double LowCut,
    double Width,
    double ReturnGain,
    bool Muted = false)
{
    public static readonly ReverbParams Defaults = new(
        Decay: 1.8,
        PreDelayMs: 20,
        Tone: 6000,
        LowCut: 150,
        Width: 1,
        ReturnGain: 0.8,
        Muted: false);
}

public sealed record ReverbPresetDef(
    string Label,
    string Hint,
    double Decay,
    double PreDelayMs,
    double Tone,
    double LowCut,
    double Width);

/// <summary>
/// Port of the reverb bus in the web app's <c>mixGraph.ts</c> and presets in <c>Mixer.tsx</c>:
/// <c>lowCut</c> (high-pass) → <c>preDelay</c> → stereo convolution with the seeded synthetic impulse
/// (<see cref="MakeImpulse"/>) → <c>returnGain</c>.
/// Uses zero-latency multi-stage partitioned convolution (128-tap SIMD head + 128-sample short FFT tail +
/// 1024-sample SIMD long FFT tail) with preallocated buffers so it is exact sample-for-sample and very fast
/// in real-time ASIO callbacks.
/// </summary>
public sealed class ReverbBus
{
    public static readonly ReverbPresetDef[] Presets =
    [
        new("Cuarto", "Ambiente corto y cercano", 0.6, 5, 7000, 200, 0.8),
        new("Sala", "Sala chica, natural", 1.3, 15, 6000, 150, 1),
        new("Salón", "Salón grande, más cola", 2.4, 25, 5000, 150, 1),
        new("Placa", "Brillante, clásica para punteos", 1.8, 10, 9500, 250, 1),
        new("Catedral", "Enorme y oscura", 5, 40, 4000, 120, 1),
    ];

    public static string PresetOf(ReverbParams r)
    {
        foreach (var p in Presets)
        {
            if (Math.Abs(r.Decay - p.Decay) < 0.01 &&
                Math.Abs(r.PreDelayMs - p.PreDelayMs) < 0.01 &&
                Math.Abs(r.Tone - p.Tone) < 0.01 &&
                Math.Abs(r.LowCut - p.LowCut) < 0.01 &&
                Math.Abs(r.Width - p.Width) < 0.01)
                return p.Label;
        }
        return "";
    }

    public static ReverbParams ApplyPreset(ReverbParams current, string label)
    {
        foreach (var p in Presets)
        {
            if (p.Label == label)
            {
                return current with
                {
                    Decay = p.Decay,
                    PreDelayMs = p.PreDelayMs,
                    Tone = p.Tone,
                    LowCut = p.LowCut,
                    Width = p.Width,
                };
            }
        }
        return current;
    }

    static double NextMulberry32(ref uint a)
    {
        unchecked
        {
            a += 0x6d2b79f5u;
            uint t = a;
            t = (uint)((int)(t ^ (t >> 15)) * (int)(t | 1u));
            t ^= t + (uint)((int)(t ^ (t >> 7)) * (int)(t | 61u));
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>
    /// Port of <c>makeImpulse</c> in the web app's <c>mixGraph.ts</c>: seeded synthetic stereo room
    /// with RT60 exponential decay, high-frequency damping over time, width scaling and unity power gain.
    /// </summary>
    public static (float[] Left, float[] Right) MakeImpulse(int sampleRate, ReverbParams r)
    {
        double decay = Math.Clamp(r.Decay, 0.1, 12);
        int len = Math.Max(1, (int)Math.Ceiling(sampleRate * Math.Min(14.0, decay * 1.15 + 0.05)));
        double tone = Math.Clamp(r.Tone, 500, 20000);
        var ch0 = new float[len];
        var ch1 = new float[len];
        for (int c = 0; c < 2; c++)
        {
            uint seed = unchecked(0x9e3779b9u + (uint)(c * 7919));
            float[] d = c == 0 ? ch0 : ch1;
            double y = 0;
            for (int i = 0; i < len; i++)
            {
                double t = i / (double)sampleRate;
                double fc = tone * (1.0 - 0.75 * Math.Min(1.0, t / decay));
                double a = Math.Exp((-2.0 * Math.PI * fc) / sampleRate);
                y = (1.0 - a) * (NextMulberry32(ref seed) * 2.0 - 1.0) + a * y;
                double env = Math.Exp((-6.9078 * t) / decay) * Math.Min(1.0, t / 0.004);
                d[i] = (float)(y * env);
            }
        }
        double w = Math.Clamp(r.Width, 0, 1);
        double energy = 0;
        for (int i = 0; i < len; i++)
        {
            double m = ((double)ch0[i] + ch1[i]) / 2.0;
            double s = (((double)ch0[i] - ch1[i]) / 2.0) * w;
            ch0[i] = (float)(m + s);
            ch1[i] = (float)(m - s);
            energy += (double)ch0[i] * ch0[i] + (double)ch1[i] * ch1[i];
        }
        double k = energy > 0 ? 1.0 / Math.Sqrt(energy / 2.0) : 0;
        for (int i = 0; i < len; i++)
        {
            ch0[i] = (float)(ch0[i] * k);
            ch1[i] = (float)(ch1[i] * k);
        }
        return (ch0, ch1);
    }

    sealed record ReverbConfig(Biquad.Coeffs LowCut, double PreDelaySamples, float ReturnGain);

    readonly int fs;
    readonly Biquad lowCutFilter;
    readonly float[] preBufL;
    readonly float[] preBufR;
    readonly int preMax;
    int preWrite;
    int busTailRemaining;
    ReverbKey currentKey;
    int irVersion;
    volatile ReverbConfig config;
    volatile ConvolverPair convolver;

    readonly record struct ReverbKey(int DecayMilli, int ToneMilli, int WidthMilli);

    public ReverbBus(int sampleRate)
    {
        fs = sampleRate;
        lowCutFilter = new Biquad(sampleRate);
        preMax = sampleRate + 4; // max 1s DelayNode in mixGraph.ts
        preBufL = new float[preMax];
        preBufR = new float[preMax];
        convolver = new ConvolverPair([0f], [0f]);
        config = BuildConfig(ReverbParams.Defaults);
        Set(ReverbParams.Defaults);
    }

    static double F(double v) => (float)v;

    ReverbConfig BuildConfig(ReverbParams r)
    {
        var lowCut = Biquad.Compute(fs, new BiquadSpec(BiquadType.Highpass, F(Math.Clamp(r.LowCut, 20, 2000)), F(0.707), 0));
        double delaySec = F(Math.Clamp(r.PreDelayMs, 0, 500) / 1000.0);
        double preSamples = Math.Clamp(delaySec * fs, 0, preMax - 2);
        float ret = r.Muted ? 0f : (float)Math.Clamp(r.ReturnGain, 0, 2);
        return new ReverbConfig(lowCut, preSamples, ret);
    }

    static ReverbKey KeyOf(ReverbParams r) => new(
        (int)Math.Round(r.Decay * 1000),
        (int)Math.Round(r.Tone * 1000),
        (int)Math.Round(r.Width * 1000));

    public void Set(ReverbParams r)
    {
        config = BuildConfig(r);
        var key = KeyOf(r);
        if (key != currentKey)
        {
            currentKey = key;
            Interlocked.Increment(ref irVersion);
            var (irL, irR) = MakeImpulse(fs, r);
            convolver = new ConvolverPair(irL, irR);
        }
    }

    /// <summary>
    /// Updates the reverb bus from the UI thread during live playback: swaps scalar/filter parameters
    /// immediately and generates any new impulse response (up to 5 s for Catedral) on a background
    /// thread before atomically replacing the convolver reference.
    /// </summary>
    public void SetLive(ReverbParams r)
    {
        config = BuildConfig(r);
        var key = KeyOf(r);
        if (key != currentKey)
        {
            currentKey = key;
            int version = Interlocked.Increment(ref irVersion);
            int sampleRate = fs;
            Task.Run(() =>
            {
                var (irL, irR) = MakeImpulse(sampleRate, r);
                var pair = new ConvolverPair(irL, irR);
                if (Volatile.Read(ref irVersion) == version)
                    convolver = pair;
            });
        }
    }

    public void Reset()
    {
        lowCutFilter.Reset();
        Array.Clear(preBufL);
        Array.Clear(preBufR);
        preWrite = 0;
        busTailRemaining = 0;
        convolver.Reset();
        heard = convolver;
        fading = null;
    }

    /// <summary>A new impulse takes over in a crossfade this long (0.2 s at 48 kHz): an automated decay never cuts the tail.</summary>
    const int SwapFadeFrames = 9600;
    // audio thread: the impulse in use and the one it is fading out of
    ConvolverPair? heard, fading;
    int fadeLeft;
    readonly float[] oldL = new float[TrackDspChain.MaxBlockSize], oldR = new float[TrackDspChain.MaxBlockSize];

    void Convolve(Span<float> left, Span<float> right)
    {
        var now = convolver;
        if (now != heard)
        {
            fading = heard;
            heard = now;
            fadeLeft = fading == null ? 0 : SwapFadeFrames;
            if (fadeLeft == 0) fading = null;
        }
        if (fading is not { } old)
        {
            now.Process(left, right);
            return;
        }
        int n = left.Length;
        left.CopyTo(oldL);
        right.CopyTo(oldR);
        old.Process(oldL.AsSpan(0, n), oldR.AsSpan(0, n));
        now.Process(left, right);
        for (int i = 0; i < n; i++)
        {
            float g = fadeLeft / (float)SwapFadeFrames;
            if (fadeLeft > 0) fadeLeft--;
            left[i] += (oldL[i] - left[i]) * g;
            right[i] += (oldR[i] - right[i]) * g;
        }
        if (fadeLeft == 0) fading = null;
    }

    static bool HasAnySignal(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        for (int i = 0; i < l.Length; i++)
            if (l[i] != 0f || r[i] != 0f) return true;
        return false;
    }

    /// <summary>
    /// Processes the summed reverb send <paramref name="left"/> / <paramref name="right"/> in place
    /// into the stereo reverb return (ready to add into the master bus).
    /// </summary>
    public void Process(Span<float> left, Span<float> right)
    {
        if (HasAnySignal(left, right))
        {
            busTailRemaining = fs * 8;
        }
        else if (busTailRemaining <= 0)
        {
            return;
        }
        else
        {
            busTailRemaining -= left.Length;
        }

        var cfg = config;
        lowCutFilter.Process(left, right, cfg.LowCut);

        double d = cfg.PreDelaySamples;
        int dInt = (int)d;
        double frac = d - dInt;
        for (int i = 0; i < left.Length; i++)
        {
            preBufL[preWrite] = left[i];
            preBufR[preWrite] = right[i];

            int r0 = preWrite - dInt;
            if (r0 < 0) r0 += preMax;
            if (frac <= 1e-7)
            {
                left[i] = preBufL[r0];
                right[i] = preBufR[r0];
            }
            else
            {
                int r1 = r0 - 1;
                if (r1 < 0) r1 += preMax;
                left[i] = (float)(preBufL[r0] * (1.0 - frac) + preBufL[r1] * frac);
                right[i] = (float)(preBufR[r0] * (1.0 - frac) + preBufR[r1] * frac);
            }

            if (++preWrite == preMax) preWrite = 0;
        }

        Convolve(left, right);

        float g = cfg.ReturnGain;
        for (int i = 0; i < left.Length; i++)
        {
            left[i] *= g;
            right[i] *= g;
        }
    }

    sealed class ConvolverPair(float[] irL, float[] irR)
    {
        readonly PartitionedConvolver left = new(irL);
        readonly PartitionedConvolver right = new(irR);

        public void Reset()
        {
            left.Reset();
            right.Reset();
        }

        public void Process(Span<float> l, Span<float> r)
        {
            left.Process(l);
            right.Process(r);
        }
    }

    /// <summary>
    /// Zero-latency non-uniform partitioned overlap-add convolver:
    /// - Head [0..63]: 64-tap direct time-domain product (sample-by-sample, invariant to any buffer size:
    ///   64, 96, 100, 128, 144, 192, 441, 480, 1000…).
    /// - Mid [64..1023]: 15 partitions of 64 samples (128-point FFT, triggered every 64 samples).
    /// - Long [1024..end]: partitions of 1024 samples (2048-point FFT, triggered every 1024 samples).
    /// </summary>
    sealed class PartitionedConvolver
    {
        const int HeadLen = 64;
        const int MidBlock = 64;
        const int LongBlock = 1024;
        const int ScratchSize = 2048;

        readonly float[] head = new float[HeadLen];
        readonly float[] hist = new float[HeadLen * 2];
        readonly float[] tailScratch = new float[ScratchSize];
        readonly FftStage midStage;
        readonly FftStage longStage;
        readonly int maxSilenceTail;
        int histIdx;
        int tailRemaining;

        public PartitionedConvolver(float[] ir)
        {
            Array.Copy(ir, 0, head, 0, Math.Min(HeadLen, ir.Length));
            midStage = new FftStage(ir, startTap: HeadLen, maxTaps: LongBlock - HeadLen, blockSize: MidBlock);
            longStage = new FftStage(ir, startTap: LongBlock, maxTaps: Math.Max(0, ir.Length - LongBlock), blockSize: LongBlock);
            maxSilenceTail = ir.Length + LongBlock * 2 + HeadLen;
        }

        public void Reset()
        {
            Array.Clear(hist);
            histIdx = 0;
            tailRemaining = 0;
            midStage.Reset();
            longStage.Reset();
        }

        static bool HasNonZero(ReadOnlySpan<float> span)
        {
            for (int i = 0; i < span.Length; i++)
                if (span[i] != 0f) return true;
            return false;
        }

        public void Process(Span<float> buf)
        {
            if (HasNonZero(buf))
            {
                tailRemaining = maxSilenceTail;
            }
            else if (tailRemaining <= 0)
            {
                return;
            }
            else
            {
                tailRemaining -= buf.Length;
            }

            float[] hd = head;
            float[] hs = hist;
            int idx = histIdx;
            int step = Vector<float>.Count;
            var vHd0 = Unsafe.As<float, Vector<float>>(ref hd[0]);
            var vHd1 = Unsafe.As<float, Vector<float>>(ref hd[step]);
            var vHd2 = Unsafe.As<float, Vector<float>>(ref hd[step * 2]);
            var vHd3 = Unsafe.As<float, Vector<float>>(ref hd[step * 3]);
            var vHd4 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[32]) : Vector<float>.Zero;
            var vHd5 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[40]) : Vector<float>.Zero;
            var vHd6 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[48]) : Vector<float>.Zero;
            var vHd7 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[56]) : Vector<float>.Zero;

            int offset = 0;
            while (offset < buf.Length)
            {
                int n = Math.Min(ScratchSize, buf.Length - offset);
                var slice = buf.Slice(offset, n);
                var tail = tailScratch.AsSpan(0, n);
                tail.Clear();

                midStage.AddCausal(slice, tail);
                longStage.AddCausal(slice, tail);

                if (step == 8)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float x = slice[i];
                        hs[idx] = x;
                        hs[idx + HeadLen] = x;

                        var vSum = Unsafe.As<float, Vector<float>>(ref hs[idx]) * vHd0
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 8]) * vHd1
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 16]) * vHd2
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 24]) * vHd3
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 32]) * vHd4
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 40]) * vHd5
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 48]) * vHd6
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 56]) * vHd7;

                        if (--idx < 0) idx = HeadLen - 1;
                        slice[i] = tail[i] + Vector.Sum(vSum);
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float x = slice[i];
                        hs[idx] = x;
                        hs[idx + HeadLen] = x;

                        var vSum = Vector<float>.Zero;
                        for (int k = 0; k < HeadLen; k += step)
                            vSum += Unsafe.As<float, Vector<float>>(ref hs[idx + k]) * Unsafe.As<float, Vector<float>>(ref hd[k]);

                        if (--idx < 0) idx = HeadLen - 1;
                        slice[i] = tail[i] + Vector.Sum(vSum);
                    }
                }
                offset += n;
            }
            histIdx = idx;
        }
    }

    /// <summary>
    /// One uniform stage: the input in blocks of <c>B</c>, each against every partition of its part of the impulse in the
    /// frequency domain. The partitions past the first only need older blocks, so they are summed a slice at a time while
    /// the next block comes in (<see cref="Slices"/>, at fixed places of the block: any callback size gives the same floats);
    /// a block's end only does the newest one and the two FFTs. Done all at once, the long stage took a whole callback
    /// every 1024 frames.
    /// <para>
    /// The sound and the impulse are real, so half of each spectrum mirrors the other: each 2B-point transform is a B-point
    /// complex one plus a pass that splits it (<see cref="RealForward"/>, <see cref="RealInverse"/>), and only B bins are
    /// multiplied. Bin 0 carries two real ones, DC in its real part and Nyquist in the imaginary one; those two are summed apart.
    /// </para>
    /// </summary>
    sealed class FftStage
    {
        /// <summary>How many pieces the older partitions' sum is cut into over a block.</summary>
        const int Slices = 16;

        readonly int B;
        readonly int sliceLen;
        /// <summary>The real transform's length (2B) and the complex one's under it (B).</summary>
        readonly int N, M;
        readonly int vecLen;
        readonly int parts;
        readonly Vector<float>[][] irRe;
        readonly Vector<float>[][] irIm;
        readonly Vector<float>[][] fdlRe;
        readonly Vector<float>[][] fdlIm;
        readonly bool[] fdlActive;
        readonly float[] blockIn;
        /// <summary>The 2B real samples going into or coming out of a transform.</summary>
        readonly float[] frame;
        readonly float[] fftRe;
        readonly float[] fftIm;
        readonly Vector<float>[] sumReVec;
        readonly Vector<float>[] sumImVec;
        readonly float[] tailOut;
        readonly float[] overlap;
        readonly float[] twiddleRe;
        readonly float[] twiddleIm;
        /// <summary>exp(-2πik/N) for k = 0 .. M/2: what joins the two halves of a real transform.</summary>
        readonly float[] splitRe;
        readonly float[] splitIm;

        int blockPos;
        int fdlIdx;
        /// <summary>The older partitions (1 ..) already in the sum for the block coming in.</summary>
        int summed;
        bool anySummed;
        /// <summary>The sum's DC and Nyquist bins, apart from the vectors (they share bin 0 there).</summary>
        float dcSum, nyquistSum;

        public FftStage(float[] ir, int startTap, int maxTaps, int blockSize)
        {
            B = blockSize;
            sliceLen = Math.Max(1, blockSize / Slices);
            N = blockSize * 2;
            M = blockSize;
            vecLen = M / Vector<float>.Count;
            int avail = Math.Max(0, Math.Min(maxTaps, ir.Length - startTap));
            parts = (avail + B - 1) / B;

            irRe = new Vector<float>[parts][];
            irIm = new Vector<float>[parts][];
            fdlRe = new Vector<float>[parts][];
            fdlIm = new Vector<float>[parts][];
            fdlActive = new bool[parts];
            blockIn = new float[B];
            frame = new float[N];
            fftRe = new float[M];
            fftIm = new float[M];
            sumReVec = new Vector<float>[vecLen];
            sumImVec = new Vector<float>[vecLen];
            tailOut = new float[B];
            overlap = new float[B];

            twiddleRe = new float[M];
            twiddleIm = new float[M];
            for (int len = 2; len <= M; len <<= 1)
            {
                int half = len >> 1;
                double step = -2.0 * Math.PI / len;
                for (int j = 0; j < half; j++)
                {
                    twiddleRe[half + j] = (float)Math.Cos(step * j);
                    twiddleIm[half + j] = (float)Math.Sin(step * j);
                }
            }
            splitRe = new float[M / 2 + 1];
            splitIm = new float[M / 2 + 1];
            for (int k = 0; k <= M / 2; k++)
            {
                splitRe[k] = (float)Math.Cos(-2.0 * Math.PI * k / N);
                splitIm[k] = (float)Math.Sin(-2.0 * Math.PI * k / N);
            }

            for (int p = 0; p < parts; p++)
            {
                var x = new float[N];
                var re = new float[M];
                var im = new float[M];
                int offset = startTap + p * B;
                int count = Math.Min(B, startTap + avail - offset);
                for (int i = 0; i < count; i++) x[i] = ir[offset + i];
                RealForward(x, re, im);
                irRe[p] = MemoryMarshal.Cast<float, Vector<float>>(re.AsSpan()).ToArray();
                irIm[p] = MemoryMarshal.Cast<float, Vector<float>>(im.AsSpan()).ToArray();
                fdlRe[p] = new Vector<float>[vecLen];
                fdlIm[p] = new Vector<float>[vecLen];
            }
        }

        public void Reset()
        {
            Array.Clear(blockIn);
            Array.Clear(tailOut);
            Array.Clear(overlap);
            Array.Clear(fdlActive);
            for (int p = 0; p < parts; p++)
            {
                Array.Clear(fdlRe[p]);
                Array.Clear(fdlIm[p]);
            }
            Array.Clear(sumReVec);
            Array.Clear(sumImVec);
            dcSum = nyquistSum = 0;
            blockPos = 0;
            fdlIdx = 0;
            summed = 0;
            anySummed = false;
        }

        public void AddCausal(ReadOnlySpan<float> input, Span<float> output)
        {
            if (parts == 0) return;
            float[] tOut = tailOut;
            float[] bIn = blockIn;
            int idx = 0;
            while (idx < input.Length)
            {
                int rem = Math.Min(input.Length - idx, sliceLen - blockPos % sliceLen);
                for (int k = 0; k < rem; k++)
                {
                    output[idx + k] += tOut[blockPos + k];
                    bIn[blockPos + k] = input[idx + k];
                }
                blockPos += rem;
                idx += rem;
                if (blockPos == B)
                {
                    blockPos = 0;
                    StepBlock();
                }
                else if (blockPos % sliceLen == 0) SumOlder((parts - 1) * (blockPos / sliceLen) / Slices);
            }
        }

        /// <summary>Adds partitions up to <paramref name="upTo"/> (of 1 .. parts - 1) to the sum for the block coming in, in order.</summary>
        void SumOlder(int upTo)
        {
            for (; summed < upTo; summed++)
            {
                int p = summed + 1;
                // the block coming in goes to fdlIdx: partition p plays the one p blocks before it
                int slot = fdlIdx - p;
                if (slot < 0) slot += parts;
                if (!fdlActive[slot]) continue;
                anySummed = true;
                Accumulate(fdlRe[slot], fdlIm[slot], irRe[p], irIm[p]);
            }
        }

        /// <summary>Adds one block's spectrum times one partition's to the sum; bin 0 apart, as two real products.</summary>
        void Accumulate(Vector<float>[] xRe, Vector<float>[] xIm, Vector<float>[] hRe, Vector<float>[] hIm)
        {
            Vector<float>[] vSumRe = sumReVec;
            Vector<float>[] vSumIm = sumImVec;
            for (int k = 0; k < vecLen; k++)
            {
                var xr = xRe[k];
                var xi = xIm[k];
                var hr = hRe[k];
                var hi = hIm[k];
                vSumRe[k] += xr * hr - xi * hi;
                vSumIm[k] += xr * hi + xi * hr;
            }
            dcSum += xRe[0][0] * hRe[0][0];
            nyquistSum += xIm[0][0] * hIm[0][0];
        }

        void StepBlock()
        {
            SumOlder(parts - 1);
            bool nonZero = false;
            float[] bIn = blockIn;
            for (int i = 0; i < B; i++)
            {
                if (bIn[i] != 0f) { nonZero = true; break; }
            }

            if (nonZero)
            {
                Array.Copy(bIn, 0, frame, 0, B);
                Array.Clear(frame, B, B);
                RealForward(frame, fftRe, fftIm);
                MemoryMarshal.Cast<float, Vector<float>>(fftRe.AsSpan()).CopyTo(fdlRe[fdlIdx]);
                MemoryMarshal.Cast<float, Vector<float>>(fftIm.AsSpan()).CopyTo(fdlIm[fdlIdx]);
                fdlActive[fdlIdx] = true;
                Accumulate(fdlRe[fdlIdx], fdlIm[fdlIdx], irRe[0], irIm[0]);
            }
            else
            {
                fdlActive[fdlIdx] = false;
            }
            bool anyActive = anySummed || nonZero;

            if (++fdlIdx == parts) fdlIdx = 0;

            float[] tOut = tailOut;
            float[] ov = overlap;
            if (anyActive)
            {
                MemoryMarshal.AsBytes(sumReVec.AsSpan()).CopyTo(MemoryMarshal.AsBytes(fftRe.AsSpan()));
                MemoryMarshal.AsBytes(sumImVec.AsSpan()).CopyTo(MemoryMarshal.AsBytes(fftIm.AsSpan()));
                fftRe[0] = dcSum;
                fftIm[0] = nyquistSum;
                Array.Clear(sumReVec);
                Array.Clear(sumImVec);
                dcSum = nyquistSum = 0;
                RealInverse(fftRe, fftIm, frame);
                for (int k = 0; k < B; k++)
                {
                    tOut[k] = frame[k] + ov[k];
                    ov[k] = frame[B + k];
                }
            }
            else
            {
                for (int k = 0; k < B; k++)
                {
                    tOut[k] = ov[k];
                    ov[k] = 0f;
                }
            }
            summed = 0;
            anySummed = false;
        }

        /// <summary>The spectrum of <paramref name="x"/> (N real samples): bins 1 .. M-1, and DC / Nyquist in bin 0's real / imaginary parts.</summary>
        void RealForward(float[] x, float[] re, float[] im)
        {
            for (int n = 0; n < M; n++)
            {
                re[n] = x[2 * n];
                im[n] = x[2 * n + 1];
            }
            Fft(re, im, invert: false);
            float z0r = re[0], z0i = im[0];
            re[0] = z0r + z0i;
            im[0] = z0r - z0i;
            for (int k = 1; k <= M / 2; k++)
            {
                int m = M - k;
                float ar = re[k], ai = im[k], br = re[m], bi = im[m];
                // the even samples' spectrum and the odd ones', out of the packed one
                float er = 0.5f * (ar + br), ei = 0.5f * (ai - bi);
                float or = 0.5f * (ai + bi), oi = -0.5f * (ar - br);
                float wr = splitRe[k], wi = splitIm[k];
                float tr = wr * or - wi * oi, ti = wr * oi + wi * or;
                re[k] = er + tr;
                im[k] = ei + ti;
                re[m] = er - tr;
                im[m] = ti - ei;
            }
        }

        /// <summary>Back from a spectrum laid out as <see cref="RealForward"/> leaves it to N real samples in <paramref name="y"/>; overwrites re / im.</summary>
        void RealInverse(float[] re, float[] im, float[] y)
        {
            float x0 = re[0], xm = im[0];
            re[0] = 0.5f * (x0 + xm);
            im[0] = 0.5f * (x0 - xm);
            for (int k = 1; k <= M / 2; k++)
            {
                int m = M - k;
                float ar = re[k], ai = im[k], br = re[m], bi = im[m];
                float er = 0.5f * (ar + br), ei = 0.5f * (ai - bi);
                float dr = 0.5f * (ar - br), di = 0.5f * (ai + bi);
                float wr = splitRe[k], wi = splitIm[k];
                float or = dr * wr + di * wi, oi = di * wr - dr * wi;
                re[k] = er - oi;
                im[k] = ei + or;
                re[m] = er + oi;
                im[m] = or - ei;
            }
            Fft(re, im, invert: true);
            for (int n = 0; n < M; n++)
            {
                y[2 * n] = re[n];
                y[2 * n + 1] = im[n];
            }
        }

        void Fft(float[] re, float[] im, bool invert)
        {
            for (int i = 1, j = 0; i < M; i++)
            {
                int bit = M >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            float sign = invert ? -1f : 1f;
            for (int len = 2; len <= M; len <<= 1)
            {
                int half = len >> 1;
                for (int i = 0; i < M; i += len)
                {
                    for (int j = 0; j < half; j++)
                    {
                        float wRe = twiddleRe[half + j];
                        float wIm = twiddleIm[half + j] * sign;
                        int u = i + j, v = u + half;
                        float vr = re[v] * wRe - im[v] * wIm;
                        float vi = re[v] * wIm + im[v] * wRe;
                        re[v] = re[u] - vr;
                        im[v] = im[u] - vi;
                        re[u] += vr;
                        im[u] += vi;
                    }
                }
            }
            if (invert)
            {
                float invM = 1f / M;
                for (int i = 0; i < M; i++)
                {
                    re[i] *= invM;
                    im[i] *= invM;
                }
            }
        }
    }
}
