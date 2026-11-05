using Microsoft.ML.OnnxRuntime;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Audio;

/// <summary>
/// Pulls a mix apart into stems ("Separar en stems", asked by the user) with Demucs (Rouard et al., Meta; the graphs come
/// from <c>tools/get-demucs.ps1</c>): voice, drums, bass and the rest, or those plus guitar and piano. The user picks how
/// many and how good: more quality means another model (<c>htdemucs_ft</c> has one trained for each stem) or the same one
/// again a little later in time (Demucs' "shifts"), and each costs a full pass (<see cref="Plan(Layout, Quality)"/>). The advanced mode (#146) picks the model, the stems
/// and the passes by hand (<see cref="Settings.Model"/>). It runs through ONNX
/// Runtime on the graphics card, NVIDIA, AMD or Intel, with DirectML, or else on the CPU. A driver can give wrong audio
/// with no error (DirectML did until the Sigmoids were exposed, see <c>make-demucs-onnx.py</c>), so before a model is
/// trusted on a card it has to match the CPU on a segment of the audio at hand and beat its time (<see cref="Choose"/>).
/// The graphs hold only the network; the STFT and its inverse are here, written to match <c>HTDemucs._spec</c> and
/// <c>_ispec</c> sample for sample (<c>xtlab separar</c> measured it against PyTorch), and the audio goes through
/// in overlapping 7.8 s segments as <c>apply_model</c> does. The stems always add up to the mix
/// (<see cref="Complete"/>). Offline only (allocates, runs on a worker thread); each model is loaded for its
/// pass and released, since they are large and this is seldom used.
/// </summary>
public static class StemSeparator
{
    public enum Layout { Two, Four, Six }
    public enum Quality { Fast, High, Max }
    /// <summary>
    /// The model picked by hand in the advanced mode (#146); <see cref="Auto"/> lets <see cref="Layout"/> and
    /// <see cref="Quality"/> choose. <see cref="Tuned"/> is <c>htdemucs_ft</c>, one model per stem.
    /// </summary>
    public enum Model { Auto, General, Tuned, Six }

    /// <param name="Id">vocals, drums, bass, guitar, piano or other.</param>
    public sealed record Stem(string Id, float[] L, float[] R);

    /// <param name="Shifts">Overrides how many passes each model does; 0 is one pass with no shift at all.</param>
    /// <param name="CpuOnly">Leaves the graphics card alone.</param>
    /// <param name="Model">A model by hand; then <paramref name="Layout"/> and <paramref name="Quality"/> don't count.</param>
    /// <param name="Only">The stems wanted (ids of <see cref="StemsOf"/>); the rest go to "other". Null is all of them.</param>
    public sealed record Settings(Layout Layout = Layout.Four, Quality Quality = Quality.High, int? Shifts = null, bool CpuOnly = false,
        Model Model = Model.Auto, IReadOnlyList<string>? Only = null);

    /// <summary>The stems a model gives besides "other", in the order they are shown.</summary>
    public static IReadOnlyList<string> StemsOf(Model model) =>
        model == Model.Six ? ["vocals", "drums", "bass", "guitar", "piano"] : ["vocals", "drums", "bass"];

    /// <summary>The most passes the advanced mode offers: each one costs a full run of the model.</summary>
    public const int MaxShifts = 8;

    const int ModelRate = 44100;
    /// <summary>The STFT of the models: 4096 points, a frame every 1024 samples, without the top bin.</summary>
    const int Nfft = 4096, Hop = 1024, Bins = Nfft / 2;
    /// <summary>One segment (7.8 s) and its frames, the only length the graphs take.</summary>
    const int Segment = 343980, Frames = 336;
    /// <summary>25 % of overlap between segments, as Demucs by default.</summary>
    const int Stride = (int)(0.75 * Segment);
    /// <summary>Demucs' own padding around the STFT, so frames line up with the segment.</summary>
    const int PadLeft = Hop / 2 * 3, PadRight = PadLeft + Frames * Hop - Segment;
    /// <summary>The padding of a centered STFT, on top of <see cref="PadLeft"/>.</summary>
    const int Center = Nfft / 2;
    const int Padded = Center * 2 + PadLeft + Segment + PadRight;
    /// <summary>The frames skipped on each side of the STFT.</summary>
    const int FrameSkip = 2;
    /// <summary>The largest time shift of a pass, half a second.</summary>
    const int MaxShift = ModelRate / 2;
    /// <summary>A guitar or piano stem this far under the mix is empty (the song has none): it goes to "other".</summary>
    const double EmptyStemDb = -45;
    /// <summary>Seconds of work per second of audio and pass, on the 16 threads where it was measured (Ryzen 7 5700G).</summary>
    const double PassCost = 0.42, MeasuredThreads = 16;
    /// <summary>The same on a graphics card that passed (RX 6800 XT), with its STFT on the CPU.</summary>
    const double GpuPassCost = 0.06;
    /// <summary>How far a card may be from the CPU: under this the difference can't be heard.</summary>
    const double MaxGpuErrorDb = -40;
    /// <summary>Adapters tried the first time: a computer can have a weak one built in besides the real card.</summary>
    const int MaxAdapters = 3;
    const int NoGpu = -1;

    /// <summary>The adapter to use: null until one was tried, <see cref="NoGpu"/> when none is worth it.</summary>
    static int? gpu;
    /// <summary>Models that already passed on <see cref="gpu"/>, and those that didn't. Only the driver could change it.</summary>
    static readonly HashSet<string> trusted = [], refused = [];

    static readonly string[] Four = ["drums", "bass", "other", "vocals"];
    static readonly string[] Six = ["drums", "bass", "other", "vocals", "guitar", "piano"];
    /// <summary>What a pass is run for: "other" comes out of the rest (<see cref="Complete"/>).</summary>
    static readonly string[] KeepFour = ["drums", "bass", "vocals"];
    static readonly string[] KeepSix = ["drums", "bass", "vocals", "guitar", "piano"];

    /// <param name="File">The graph, found by <see cref="ModelPath"/>.</param>
    /// <param name="Sources">What the graph returns, in order.</param>
    /// <param name="Keep">What this pass is run for.</param>
    /// <param name="Shifts">Times it goes over the audio, each one shifted a bit, averaged.</param>
    sealed record Pass(string File, string[] Sources, string[] Keep, int Shifts);

    static List<Pass> Plan(Layout layout, Quality quality)
    {
        static List<Pass> Tuned(string[] stems, int shifts) =>
            stems.Select(id => new Pass($"htdemucs_ft_{id}.onnx", Four, [id], shifts)).ToList();
        return (layout, quality) switch
        {
            (Layout.Two, Quality.Fast) => [new("htdemucs.onnx", Four, ["vocals"], 1)],
            (Layout.Two, Quality.High) => Tuned(["vocals"], 1),
            (Layout.Two, _) => Tuned(["vocals"], 3),
            (Layout.Four, Quality.Fast) => [new("htdemucs.onnx", Four, KeepFour, 1)],
            (Layout.Four, Quality.High) => Tuned(KeepFour, 1),
            (Layout.Four, _) => Tuned(KeepFour, 2),
            (_, Quality.Fast) => [new("htdemucs_6s.onnx", Six, KeepSix, 1)],
            (_, Quality.High) => [new("htdemucs_6s.onnx", Six, KeepSix, 2)],
            _ => [new("htdemucs_6s.onnx", Six, KeepSix, 4)],
        };
    }

    /// <summary>
    /// The passes of <paramref name="s"/>: by layout and quality, or by its model with one pass each (the shifts come
    /// from <see cref="Settings.Shifts"/>). Stems left out of <see cref="Settings.Only"/> aren't kept, and a pass with
    /// nothing to keep isn't run.
    /// </summary>
    static List<Pass> Plan(Settings s)
    {
        var plan = s.Model switch
        {
            Model.General => [new("htdemucs.onnx", Four, KeepFour, 1)],
            Model.Tuned => KeepFour.Select(id => new Pass($"htdemucs_ft_{id}.onnx", Four, [id], 1)).ToList(),
            Model.Six => [new("htdemucs_6s.onnx", Six, KeepSix, 1)],
            _ => Plan(s.Layout, s.Quality),
        };
        if (s.Only is not { } only) return plan;
        return plan.Select(p => p with { Keep = p.Keep.Where(only.Contains).ToArray() }).Where(p => p.Keep.Length > 0).ToList();
    }

    /// <summary>Where the app downloads the graphs (<see cref="StemsComponent"/>).</summary>
    public static string DownloadDir => Path.Combine(AppFolder.Dir, "demucs");

    /// <summary>Where the graphs may be: next to the program (a development build) and then what was downloaded.</summary>
    public static IEnumerable<string> ModelsDirs => [Path.Combine(AppContext.BaseDirectory, "demucs"), DownloadDir];

    /// <summary>A graph's file; in <see cref="DownloadDir"/> when it isn't anywhere yet.</summary>
    public static string ModelPath(string file) =>
        ModelsDirs.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists) ?? Path.Combine(DownloadDir, file);

    /// <summary>The graphs an option needs.</summary>
    public static IEnumerable<string> Files(Layout layout, Quality quality) => Files(new Settings(layout, quality));

    public static IEnumerable<string> Files(Settings s) => Plan(s).Select(p => p.File).Distinct();

    public static bool Available(Layout layout, Quality quality) => Available(new Settings(layout, quality));

    public static bool Available(Settings s) => Plan(s).All(p => File.Exists(ModelPath(p.File)));

    public static bool AnyAvailable => Enum.GetValues<Layout>().Any(l => Enum.GetValues<Quality>().Any(q => Available(l, q)));

    /// <summary>A rough idea of how long <paramref name="seconds"/> of audio take on this machine.</summary>
    public static double EstimateSec(Layout layout, Quality quality, double seconds) => EstimateSec(new Settings(layout, quality), seconds);

    public static double EstimateSec(Settings s, double seconds) =>
        Plan(s).Sum(p => Math.Max(1, s.Shifts ?? p.Shifts)) * seconds *
        (gpu >= 0 ? GpuPassCost : PassCost * MeasuredThreads / Math.Max(1, Environment.ProcessorCount));

    /// <summary>The estimate is the CPU's and a card may do much better: none has been tried yet.</summary>
    public static bool EstimateIsCeiling => gpu == null;

    /// <summary>Whether the last model ran on the graphics card.</summary>
    public static bool LastOnGpu { get; private set; }

    /// <summary>
    /// The stems of <paramref name="count"/> samples of <paramref name="l"/>/<paramref name="r"/> from
    /// <paramref name="start"/>, at the same rate and length. <paramref name="progress"/> goes 0..1.
    /// <paramref name="hold"/> stops the work between segments while it says so (the audio engine needs the CPU).
    /// </summary>
    public static List<Stem> Separate(float[] l, float[] r, int sampleRate, int start, int count, Settings? settings = null,
        Action<double>? progress = null, Func<bool>? hold = null, CancellationToken ct = default)
    {
        settings ??= new();
        start = Math.Clamp(start, 0, l.Length);
        count = Math.Clamp(count, 0, l.Length - start);
        if (count <= 0) return [];
        var plan = Plan(settings);
        if (!Available(settings))
            throw new InvalidOperationException(Texts.T("err.stems.noModel", "Faltan los modelos de separación: instalalos en Configuración → Componentes."));

        var (left, right) = AudioLoader.Resample(l.AsSpan(start, count), r.AsSpan(start, count), sampleRate, ModelRate);
        int n = left.Length;
        var (mean, std) = Stats(left, right);
        var mix = new float[(n + 2 * MaxShift) * 2];
        for (int i = 0; i < n; i++)
        {
            mix[MaxShift + i] = (left[i] - mean) / std;
            mix[mix.Length / 2 + MaxShift + i] = (right[i] - mean) / std;
        }

        var stems = new Dictionary<string, (float[] L, float[] R)>();
        var transforms = new Transforms();
        int segments = 0, done = 0;
        foreach (var p in plan) segments += Math.Max(1, settings.Shifts ?? p.Shifts) * ((n + Stride - 1) / Stride);
        void Done()
        {
            progress?.Invoke(Math.Min(0.97, ++done / (double)segments));
            ct.ThrowIfCancellationRequested();
            while (hold?.Invoke() == true)
            {
                Thread.Sleep(100);
                ct.ThrowIfCancellationRequested();
            }
        }
        foreach (var p in plan)
        {
            using var net = settings.CpuOnly ? new Net(p.File, null, ct) : Choose(p, mix, transforms, ct);
            LastOnGpu = net.Gpu;
            foreach (var (id, audio) in Run(net, p, settings.Shifts ?? p.Shifts, mix, n, transforms, Done))
                stems[id] = audio;
        }

        foreach (var (a, b) in stems.Values)
            for (int i = 0; i < n; i++)
            {
                a[i] = a[i] * std + mean;
                b[i] = b[i] * std + mean;
            }
        Complete(stems, left, right);
        progress?.Invoke(1);
        return new[] { "vocals", "drums", "bass", "guitar", "piano", "other" }
            .Where(stems.ContainsKey)
            .Select(id =>
            {
                var (a, b) = stems[id];
                if (sampleRate != ModelRate) (a, b) = AudioLoader.Resample(a, b, ModelRate, sampleRate);
                Array.Resize(ref a, count);
                Array.Resize(ref b, count);
                return new Stem(id, a, b);
            }).ToList();
    }

    /// <summary>Mean and deviation of the mono mix, which Demucs takes out before the network and puts back after.</summary>
    static (float Mean, float Std) Stats(float[] l, float[] r)
    {
        double sum = 0, sq = 0;
        for (int i = 0; i < l.Length; i++) sum += 0.5 * (l[i] + r[i]);
        double mean = sum / l.Length;
        for (int i = 0; i < l.Length; i++)
        {
            double d = 0.5 * (l[i] + r[i]) - mean;
            sq += d * d;
        }
        double std = Math.Sqrt(sq / Math.Max(1, l.Length - 1));
        return ((float)mean, std > 1e-8 ? (float)std : 1);
    }

    /// <summary>
    /// "other" is the mix minus every other stem, which is the network's own "other" plus what it left out, so the stems
    /// add up to the mix and no pass has to compute it. Empty stems are not kept: they end up there too.
    /// </summary>
    static void Complete(Dictionary<string, (float[] L, float[] R)> stems, float[] l, float[] r)
    {
        int n = l.Length;
        double mixPower = 1e-12;
        for (int i = 0; i < n; i++) mixPower += l[i] * l[i] + r[i] * r[i];
        foreach (string id in new[] { "guitar", "piano" })
        {
            if (!stems.TryGetValue(id, out var s)) continue;
            double power = 0;
            for (int i = 0; i < n; i++) power += s.L[i] * s.L[i] + s.R[i] * s.R[i];
            if (10 * Math.Log10(power / mixPower + 1e-12) < EmptyStemDb) stems.Remove(id);
        }
        var (a, b) = ((float[])l.Clone(), (float[])r.Clone());
        foreach (var s in stems.Values)
            for (int i = 0; i < n; i++)
            {
                a[i] -= s.L[i];
                b[i] -= s.R[i];
            }
        stems["other"] = (a, b);
    }

    static InferenceSession Open(string file, int? adapter)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // half of the threads: a project may be playing meanwhile
            IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
        };
        if (adapter is { } a)
        {
            // what DirectML asks for
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(a);
        }
        return new InferenceSession(ModelPath(file), options);
    }

    /// <summary>A model on an adapter or on the CPU. If the card fails halfway (out of memory, a driver reset), it goes on on the CPU.</summary>
    sealed class Net : IDisposable
    {
        readonly string file;
        readonly CancellationToken ct;
        readonly RunOptions run = new();
        readonly CancellationTokenRegistration stop;
        InferenceSession session;

        public bool Gpu { get; private set; }

        public Net(string file, int? adapter, CancellationToken ct)
        {
            this.file = file;
            this.ct = ct;
            try { session = Open(file, adapter); }
            catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or TypeInitializationException)
            {
                run.Dispose();
                if (adapter != null) throw;
                throw new InvalidOperationException(Texts.T("err.stems.engine", "No se pudo iniciar la separación: {0}", e.Message), e);
            }
            Gpu = adapter != null;
            // a segment takes seconds: cancelling stops the network there, not after it
            stop = ct.Register(() => run.Terminate = true);
        }

        public void Infer(float[] input, float[] mag, float[] spec, float[] wave)
        {
            using var mixValue = OrtValue.CreateTensorValueFromMemory(input, [1, 2, Segment]);
            using var magValue = OrtValue.CreateTensorValueFromMemory(mag, [1, 4, Bins, Frames]);
            try
            {
                using var result = session.Run(run, ["mix", "mag"], [mixValue, magValue], ["spec", "wave"]);
                result[0].GetTensorDataAsSpan<float>().CopyTo(spec);
                result[1].GetTensorDataAsSpan<float>().CopyTo(wave);
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (OnnxRuntimeException) when (Gpu)
            {
                session.Dispose();
                session = Open(file, null);
                Gpu = false;
                Infer(input, mag, spec, wave);
            }
        }

        public void Dispose()
        {
            stop.Dispose();
            run.Dispose();
            session.Dispose();
        }
    }

    /// <summary>
    /// Where a model runs. The first time it goes through the middle of the audio on the CPU and on each adapter: the
    /// fastest card that gives the same is kept, for this model and, as the adapter to use, for the others, which only
    /// have to pass on it. Anything else, and all that goes wrong on the way, leaves the model on the CPU.
    /// </summary>
    static Net Choose(Pass pass, float[] mix, Transforms tf, CancellationToken ct)
    {
        string file = pass.File;
        if (gpu == NoGpu || refused.Contains(file)) return new Net(file, null, ct);
        if (gpu is { } known && trusted.Contains(file))
        {
            try { return new Net(file, known, ct); }
            catch (OnnxRuntimeException) { return new Net(file, null, ct); }
        }

        var input = new float[Segment * 2];
        var mag = new float[4 * Bins * Frames];
        var spec = new float[pass.Sources.Length * mag.Length];
        var wave = new float[pass.Sources.Length * 2 * Segment];
        int total = mix.Length / 2, first = Math.Max(0, (total - Segment) / 2), len = Math.Min(Segment, total);
        Array.Copy(mix, first, input, 0, len);
        Array.Copy(mix, total + first, input, Segment, len);
        tf.Forward(input, mag);

        double fastest;
        using (var cpu = new Net(file, null, ct))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            cpu.Infer(input, mag, spec, wave);
            fastest = clock.Elapsed.TotalSeconds;
        }
        var refSpec = spec.ToArray();
        var refWave = wave.ToArray();
        Net? best = null;
        var adapters = gpu is { } one ? [one] : Enumerable.Range(0, MaxAdapters).ToArray();
        foreach (int adapter in adapters)
        {
            Net net;
            try { net = new Net(file, adapter, ct); }
            catch (OnnxRuntimeException) { continue; }
            try
            {
                net.Infer(input, mag, spec, wave); // the first one builds what the card runs
                var clock = System.Diagnostics.Stopwatch.StartNew();
                net.Infer(input, mag, spec, wave);
                double seconds = clock.Elapsed.TotalSeconds;
                if (net.Gpu && seconds < fastest && ErrorDb(spec, refSpec) < MaxGpuErrorDb && ErrorDb(wave, refWave) < MaxGpuErrorDb)
                {
                    best?.Dispose();
                    (best, net, fastest) = (net, null!, seconds);
                    gpu = adapter;
                }
            }
            finally { net?.Dispose(); }
        }
        if (best != null)
        {
            trusted.Add(file);
            return best;
        }
        refused.Add(file);
        gpu ??= NoGpu;
        return new Net(file, null, ct);
    }

    static double ErrorDb(float[] got, float[] want)
    {
        double error = 0, power = 1e-12;
        for (int i = 0; i < got.Length; i++)
        {
            double d = got[i] - want[i];
            error += d * d;
            power += want[i] * want[i];
        }
        return 10 * Math.Log10(error / power + 1e-15);
    }

    /// <summary>One model over the whole audio, <paramref name="shifts"/> times; only the stems of <see cref="Pass.Keep"/>.</summary>
    static Dictionary<string, (float[] L, float[] R)> Run(Net net, Pass pass, int shifts, float[] mix, int n, Transforms tf, Action done)
    {
        var keep = pass.Keep.Select(id => Array.IndexOf(pass.Sources, id)).ToArray();
        var sum = keep.ToDictionary(i => i, _ => (L: new float[n], R: new float[n]));
        var layer = keep.ToDictionary(i => i, _ => (L: new float[n], R: new float[n]));
        var weightSum = new float[n];
        int total = mix.Length / 2, sources = pass.Sources.Length;
        var inputL = new float[Segment * 2];
        var mag = new float[4 * Bins * Frames];
        var spec = new float[sources * mag.Length];
        var wave = new float[sources * 2 * Segment];
        var outL = new float[Segment];
        var outR = new float[Segment];

        for (int k = 0; k < Math.Max(1, shifts); k++)
        {
            int offset = shifts > 0 ? MaxShift * (k + 1) / (shifts + 1) : MaxShift;
            int from = offset, length = shifts > 0 ? n + MaxShift - offset : n, skip = MaxShift - offset;
            foreach (var (a, b) in layer.Values)
            {
                Array.Clear(a);
                Array.Clear(b);
            }
            Array.Clear(weightSum);
            for (int at = 0; at < length; at += Stride)
            {
                // a short last segment borrows audio from before it, and is cut back out of the result
                int len = Math.Min(length - at, Segment);
                int lead = (Segment - len) / 2;
                int first = from + at - lead;
                for (int i = 0; i < Segment; i++)
                {
                    int src = first + i;
                    bool inside = (uint)src < (uint)total;
                    inputL[i] = inside ? mix[src] : 0;
                    inputL[Segment + i] = inside ? mix[total + src] : 0;
                }
                tf.Forward(inputL, mag);
                net.Infer(inputL, mag, spec, wave);
                foreach (int src in keep)
                {
                    tf.Inverse(spec, src * mag.Length, outL, outR);
                    var (a, b) = layer[src];
                    int waveL = src * 2 * Segment, waveR = waveL + Segment;
                    for (int i = 0; i < len; i++)
                    {
                        int to = at + i - skip;
                        if ((uint)to >= (uint)n) continue;
                        float w = tf.Weight[i];
                        a[to] += w * (outL[lead + i] + wave[waveL + lead + i]);
                        b[to] += w * (outR[lead + i] + wave[waveR + lead + i]);
                    }
                }
                for (int i = 0; i < len; i++)
                {
                    int to = at + i - skip;
                    if ((uint)to < (uint)n) weightSum[to] += tf.Weight[i];
                }
                done();
            }
            foreach (int src in keep)
            {
                var (a, b) = layer[src];
                var (x, y) = sum[src];
                for (int i = 0; i < n; i++)
                {
                    float w = Math.Max(weightSum[i], 1e-6f);
                    x[i] += a[i] / w;
                    y[i] += b[i] / w;
                }
            }
        }
        int passes = Math.Max(1, shifts);
        return keep.ToDictionary(i => pass.Sources[i], i =>
        {
            var (a, b) = sum[i];
            for (int j = 0; j < n; j++)
            {
                a[j] /= passes;
                b[j] /= passes;
            }
            return (a, b);
        });
    }

    /// <summary>
    /// The STFT of the models and its inverse for one segment, in stereo. Both channels share one complex FFT: left as the
    /// real part, right as the imaginary one, which halves the work.
    /// </summary>
    sealed class Transforms
    {
        /// <summary>Scale of <c>normalized=True</c>, the same both ways: 1/√N.</summary>
        const double Scale = 1.0 / 64;
        readonly Fft fft = new(Nfft);
        readonly double[] window = new double[Nfft];
        /// <summary>For each sample of the padded signal, the sample of the segment it reflects.</summary>
        readonly int[] source = new int[Padded];
        /// <summary>The sum of the squared windows of all frames, which the inverse divides by.</summary>
        readonly double[] envelope = new double[Padded];
        readonly float[] frames = new float[Frames * Nfft * 2];
        /// <summary>The triangle that fades a segment into the next one, peak at 1.</summary>
        public readonly float[] Weight = new float[Segment];

        public Transforms()
        {
            for (int i = 0; i < Nfft; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Nfft);
            int inner = PadLeft + Segment + PadRight;
            for (int i = 0; i < Padded; i++)
            {
                int q = i - Center;
                q = q < 0 ? -q : q >= inner ? 2 * (inner - 1) - q : q;
                q -= PadLeft;
                source[i] = q < 0 ? -q : q >= Segment ? 2 * (Segment - 1) - q : q;
            }
            for (int t = 0; t < Frames + 2 * FrameSkip; t++)
                for (int i = 0; i < Nfft; i++)
                    envelope[t * Hop + i] += window[i] * window[i];
            for (int i = 0; i < Segment / 2; i++) Weight[i] = (i + 1f) / (Segment / 2);
            for (int i = Segment / 2; i < Segment; i++) Weight[i] = (float)(Segment - i) / (Segment - Segment / 2);
        }

        /// <summary><paramref name="audio"/> is left then right; <paramref name="mag"/> gets them as L re, L im, R re, R im.</summary>
        public void Forward(float[] audio, float[] mag) => Parallel.For(0, Frames,
            () => (Re: new double[Nfft], Im: new double[Nfft]),
            (t, _, b) =>
            {
                int first = (t + FrameSkip) * Hop;
                for (int i = 0; i < Nfft; i++)
                {
                    int src = source[first + i];
                    b.Re[i] = audio[src] * window[i];
                    b.Im[i] = audio[Segment + src] * window[i];
                }
                fft.Transform(b.Re, b.Im, false);
                for (int k = 0; k < Bins; k++)
                {
                    int m = (Nfft - k) % Nfft, at = k * Frames + t;
                    mag[at] = (float)(0.5 * (b.Re[k] + b.Re[m]) * Scale);
                    mag[Bins * Frames + at] = (float)(0.5 * (b.Im[k] - b.Im[m]) * Scale);
                    mag[2 * Bins * Frames + at] = (float)(0.5 * (b.Im[k] + b.Im[m]) * Scale);
                    mag[3 * Bins * Frames + at] = (float)(-0.5 * (b.Re[k] - b.Re[m]) * Scale);
                }
                return b;
            }, _ => { });

        /// <summary>The stereo segment of a spectrogram that starts at <paramref name="offset"/> of <paramref name="spec"/>.</summary>
        public void Inverse(float[] spec, int offset, float[] left, float[] right)
        {
            int ch = Bins * Frames;
            Parallel.For(0, Frames,
                () => (Re: new double[Nfft], Im: new double[Nfft]),
                (t, _, b) =>
                {
                    Array.Clear(b.Re);
                    Array.Clear(b.Im);
                    b.Re[0] = spec[offset + t];
                    b.Im[0] = spec[offset + 2 * ch + t];
                    for (int k = 1; k < Bins; k++)
                    {
                        int at = offset + k * Frames + t;
                        double lr = spec[at], li = spec[ch + at], rr = spec[2 * ch + at], ri = spec[3 * ch + at];
                        b.Re[k] = lr - ri;
                        b.Im[k] = li + rr;
                        b.Re[Nfft - k] = lr + ri;
                        b.Im[Nfft - k] = rr - li;
                    }
                    fft.Transform(b.Re, b.Im, true);
                    int to = t * Nfft * 2;
                    for (int i = 0; i < Nfft; i++)
                    {
                        frames[to + i] = (float)(b.Re[i] * window[i] * Scale);
                        frames[to + Nfft + i] = (float)(b.Im[i] * window[i] * Scale);
                    }
                    return b;
                }, _ => { });

            int skip = Center + PadLeft;
            Array.Clear(left);
            Array.Clear(right);
            for (int t = 0; t < Frames; t++)
            {
                int first = (t + FrameSkip) * Hop - skip, from = t * Nfft * 2;
                for (int i = Math.Max(0, -first); i < Nfft && first + i < Segment; i++)
                {
                    left[first + i] += frames[from + i];
                    right[first + i] += frames[from + Nfft + i];
                }
            }
            for (int i = 0; i < Segment; i++)
            {
                float e = (float)envelope[skip + i];
                left[i] /= e;
                right[i] /= e;
            }
        }

        /// <summary>How much the transforms alter noise, in dB under it, for the checks.</summary>
        public double RoundTripDb()
        {
            var rng = new Random(1);
            var audio = new float[Segment * 2];
            for (int i = 0; i < audio.Length; i++) audio[i] = (float)(rng.NextDouble() - 0.5);
            var mag = new float[4 * Bins * Frames];
            var l = new float[Segment];
            var r = new float[Segment];
            Forward(audio, mag);
            Inverse(mag, 0, l, r);
            double error = 0, power = 0;
            // the top bin is dropped by design, so noise cannot come back whole
            for (int i = 0; i < Segment; i++)
            {
                error += Math.Pow(audio[i] - l[i], 2) + Math.Pow(audio[Segment + i] - r[i], 2);
                power += Math.Pow(audio[i], 2) + Math.Pow(audio[Segment + i], 2);
            }
            return 10 * Math.Log10(error / power + 1e-15);
        }
    }

    /// <summary><see cref="Transforms.RoundTripDb"/>, for <c>xtlab separar --comprobar</c>.</summary>
    public static double TransformRoundTripDb() => new Transforms().RoundTripDb();
}
