using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Audio;

/// <summary>wav and aiff = PCM, flac = lossless, mp3 (LAME) and ogg (Vorbis) = lossy; all but WAV and AIFF go through FFmpeg.</summary>
public enum AudioFormat { Wav, Mp3, Flac, Aiff, Ogg }

/// <summary>
/// "Avanzado" of "Exportar audio" (native only, pedido #208). <see cref="Defaults"/> is what the easy dialog always did
/// (24-bit, 48 kHz, 320 kbps, the tail until it falls quiet, the level as the master leaves it, stereo), so folded
/// nothing changes. <see cref="Kbps"/> null = variable bitrate (LAME V0, Vorbis q8). <see cref="TailSec"/> null = automatic
/// (<see cref="MixMaster.MaxTailSec"/> at most, cut where it falls quiet); a number is that many seconds exactly.
/// <see cref="NormalizeDbtp"/> brings the loudest true peak there after the master; null leaves the level.
/// </summary>
public sealed record AudioExportSettings
{
    public static readonly AudioExportSettings Defaults = new();
    public static readonly int[] Depths = [16, 24, 32];
    public static readonly int[] Rates = [44100, 48000, 96000];
    public static readonly int[] Bitrates = [128, 192, 256, 320];
    public const int DefaultDepth = 24, DefaultRate = 48000, DefaultKbps = 320;
    public const double DefaultNormalizeDbtp = -1.0, MinNormalizeDbtp = -6.0;

    public AudioFormat Format { get; init; } = AudioFormat.Wav;
    /// <summary>16, 24 or 32 (32-bit float, WAV only). Ignored by MP3 and OGG.</summary>
    public int BitDepth { get; init; } = DefaultDepth;
    public int SampleRate { get; init; } = DefaultRate;
    /// <summary>TPDF dither when going down to 16 bits.</summary>
    public bool Dither { get; init; } = true;
    public int? Kbps { get; init; } = DefaultKbps;
    public double? TailSec { get; init; }
    public double? NormalizeDbtp { get; init; }
    public bool Mono { get; init; }

    public bool Lossy => Format is AudioFormat.Mp3 or AudioFormat.Ogg;

    /// <summary>The bit depths <paramref name="f"/> can take: 32-bit float only in WAV, none in the lossy ones.</summary>
    public static int[] DepthsFor(AudioFormat f) => f switch
    {
        AudioFormat.Wav => Depths,
        AudioFormat.Flac or AudioFormat.Aiff => [16, 24],
        _ => [],
    };

    /// <summary>The rates <paramref name="f"/> can take: MP3 has no 96 kHz, and Vorbis at 96 kHz is no use either.</summary>
    public static int[] RatesFor(AudioFormat f) => f is AudioFormat.Mp3 or AudioFormat.Ogg ? [44100, 48000] : Rates;

    /// <summary>The same choice made valid: an unknown or unsupported value goes to the nearest the format takes.</summary>
    public AudioExportSettings Normalized()
    {
        var depths = DepthsFor(Format);
        var rates = RatesFor(Format);
        // the lossy formats have no depth: the WAV's is kept for when the format changes back
        int depth = depths.Length == 0 ? (Depths.Contains(BitDepth) ? BitDepth : DefaultDepth) : depths.Contains(BitDepth) ? BitDepth : depths.MinBy(d => Math.Abs(d - BitDepth));
        int rate = rates.Contains(SampleRate) ? SampleRate : rates.MinBy(r => Math.Abs(r - SampleRate));
        int? kbps = Kbps is { } k ? Bitrates.MinBy(b => Math.Abs(b - k)) : null;
        double? tail = TailSec is { } t && double.IsFinite(t) ? Math.Round(Math.Clamp(t, 0, MixMaster.MaxFixedTailSec), 1) : null;
        double? norm = NormalizeDbtp is { } n && double.IsFinite(n) ? Math.Round(Math.Clamp(n, MinNormalizeDbtp, 0), 1) : null;
        return this with { BitDepth = depth, SampleRate = rate, Kbps = kbps, TailSec = tail, NormalizeDbtp = norm };
    }
}

/// <summary>Where an audio export is: mixing, converting to MP3 or saving the file.</summary>
public enum AudioExportStage { Mix, Encode, Finalize }

public sealed record AudioExportProgress(AudioExportStage Stage, double Fraction, double ElapsedSec);

/// <summary>
/// Exports the project's mix as an audio file (native only: the web exports only MP4). Same offline render as the
/// MP4 export (<see cref="MixRenderer"/>, the graph playback uses) and the same destination rules
/// (<see cref="Mp4Exporter.CheckDestination"/>, a hidden <c>.part</c> renamed at the end), so a crash or a cancel
/// never leaves half a file under the real name. WAV is written directly; MP3 goes through FFmpeg's LAME encoder.
/// </summary>
public static class AudioExporter
{
    public static string Extension(AudioFormat f) => "." + FormatId(f);

    public static AudioFormat ParseFormat(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "mp3" => AudioFormat.Mp3,
        "flac" => AudioFormat.Flac,
        "aiff" or "aif" => AudioFormat.Aiff,
        "ogg" => AudioFormat.Ogg,
        _ => AudioFormat.Wav,
    };

    public static string FormatId(AudioFormat f) => f switch
    {
        AudioFormat.Mp3 => "mp3",
        AudioFormat.Flac => "flac",
        AudioFormat.Aiff => "aiff",
        AudioFormat.Ogg => "ogg",
        _ => "wav",
    };

    /// <summary>"WAV", "FLAC"…: how the dialog names a format.</summary>
    public static string FormatLabel(AudioFormat f) => FormatId(f).ToUpperInvariant();

    /// <summary>The formats that need FFmpeg to be written.</summary>
    public static bool NeedsFfmpeg(AudioFormat f) => f is AudioFormat.Mp3 or AudioFormat.Flac or AudioFormat.Ogg;

    public static bool IsAudioFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".mp3" or ".flac" or ".aiff" or ".aif" or ".ogg";

    /// <summary>Rough size for the dialog, as the easy dialog exports <paramref name="f"/>.</summary>
    public static double EstimateBytes(AudioFormat f, double durationSec, int sampleRate = 48000) =>
        EstimateBytes(AudioExportSettings.Defaults with { Format = f, SampleRate = sampleRate }, durationSec);

    /// <summary>Rough size for the dialog; FLAC counts as about 60 % of the PCM, a variable bitrate as about 256 kbps.</summary>
    public static double EstimateBytes(AudioExportSettings s, double durationSec)
    {
        double sec = Math.Max(0, durationSec + (s.TailSec ?? 0));
        int ch = s.Mono ? 1 : 2;
        if (s.Lossy) return sec * (s.Kbps ?? 256) * 1000 / 8.0;
        double pcm = sec * s.SampleRate * ch * s.BitDepth / 8.0;
        return s.Format == AudioFormat.Flac ? pcm * 0.6 : pcm;
    }

    /// <summary>"WAV 24 bit · 48 kHz", "MP3 320 kbps · 48 kHz · mono": what the done screen says the file is.</summary>
    public static string Describe(AudioExportSettings s)
    {
        s = s.Normalized();
        string rate = s.SampleRate % 1000 == 0 ? $"{s.SampleRate / 1000} kHz" : (s.SampleRate / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " kHz";
        var parts = new List<string>
        {
            s.Lossy
                ? $"{FormatLabel(s.Format)} {(s.Kbps is { } k ? $"{k} kbps" : "VBR")}"
                : $"{FormatLabel(s.Format)} {(s.BitDepth == 32 ? "32 bit float" : $"{s.BitDepth} bit")}",
            rate,
        };
        if (s.Mono) parts.Add("mono");
        return string.Join(" · ", parts);
    }

    public static Task<ExportResult> ExportAsync(ExportJobSpec job, AudioFormat format,
        Action<AudioExportProgress>? onProgress = null, CancellationToken ct = default) =>
        ExportAsync(job, AudioExportSettings.Defaults with { Format = format }, onProgress, ct);

    /// <summary>
    /// The mix as an audio file with <paramref name="settings"/>: rendered and mastered at the engine's rate, then (in
    /// this order) folded to mono, resampled, brought to the normalize peak, and written at the chosen depth with
    /// dither when it goes down to 16 bits. WAV and AIFF are written here; FLAC, MP3 and OGG through FFmpeg from a WAV
    /// already at the final rate (and, for FLAC, depth), so the encoder changes nothing but the packing.
    /// </summary>
    public static async Task<ExportResult> ExportAsync(ExportJobSpec job, AudioExportSettings settings,
        Action<AudioExportProgress>? onProgress = null, CancellationToken ct = default)
    {
        settings = settings.Normalized();
        var format = settings.Format;
        if (job.MixTracks.All(t => !t.HasSound))
            throw new ExportException(Texts.T("err.export.noTracks", "No hay canales en el timeline."));
        string ext = Extension(format);
        var target = Mp4Exporter.CheckDestination(job.ProjectDir, job.OutDir,
            string.IsNullOrWhiteSpace(job.FileName) ? job.ProjectName : job.FileName, ext);
        if (target.Exists && !job.Overwrite)
            throw new ExportException($"Ya existe {target.FileName}{ext} en esa carpeta.", exists: true);
        if (!target.IsDefault && !Directory.Exists(target.Folder))
            throw new ExportException("La carpeta de destino ya no existe.");
        Directory.CreateDirectory(target.Folder);

        var sw = Stopwatch.StartNew();
        void Report(AudioExportStage stage, double f) => onProgress?.Invoke(new AudioExportProgress(stage, f, sw.Elapsed.TotalSeconds));

        string tempWorkDir = Path.Combine(Path.GetTempPath(), "XTremeExport");
        Directory.CreateDirectory(tempWorkDir);
        string mixWavPath = Path.Combine(tempWorkDir, $"mix_{Guid.NewGuid():N}.wav");
        string partPath = Path.Combine(target.Folder, $".{target.FileName}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.part");
        try
        {
            Report(AudioExportStage.Mix, 0);
            double content = Math.Max(1.0, MixRenderer.MixDurationSec(job.MixTracks, job.SampleRate));
            var range = ExportRange.Fit(job.Range, content);
            var mix = await Task.Run(() => MixMaster.Render(job.MixTracks, job.MixBus, content, job.SampleRate,
                settings.TailSec ?? job.TailSec, job.TargetLufs, f => Report(AudioExportStage.Mix, f), ct, range,
                fixedTail: settings.TailSec != null), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            double duration = mix.DurationSec;
            var pcm = await Task.Run(() => Finish(mix.Left, mix.Right, job.SampleRate, settings), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (!NeedsFfmpeg(format))
            {
                Report(AudioExportStage.Finalize, 0);
                await Task.Run(() => WritePcm(partPath, pcm, settings, aiff: format == AudioFormat.Aiff), ct).ConfigureAwait(false);
            }
            else
            {
                Report(AudioExportStage.Encode, 0);
                // lossy encoders take the float mix; FLAC the samples at their final depth, so it stores exactly those
                var wav = settings.Lossy ? settings with { Format = AudioFormat.Wav, BitDepth = 32 } : settings;
                await Task.Run(() => WritePcm(mixWavPath, pcm, wav, aiff: false), ct).ConfigureAwait(false);
                await Encode(mixWavPath, partPath, settings, duration, f => Report(AudioExportStage.Encode, f), ct).ConfigureAwait(false);
                Report(AudioExportStage.Finalize, 0);
            }
            ct.ThrowIfCancellationRequested();

            string outFilename = $"{target.FileName}{ext}";
            try
            {
                File.Move(partPath, target.FullPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ExportException(Texts.T("err.export.replace", "No se pudo reemplazar {0}: puede estar abierto en otro programa.", outFilename));
            }
            long bytes = File.Exists(target.FullPath) ? new FileInfo(target.FullPath).Length : 0;
            string rel = Path.GetRelativePath(Path.GetFullPath(job.ProjectDir), target.FullPath);
            string loggedFile = !string.IsNullOrEmpty(rel) && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)
                ? rel.Replace('\\', '/')
                : target.FullPath;
            // normalizing or folding to mono moves the level the master left: the done screen says what the file measures
            double lufs = settings.NormalizeDbtp != null || settings.Mono ? LoudnessMeter.Measure(pcm.L, pcm.R, pcm.Rate) : mix.Lufs;
            return new ExportResult(outFilename, loggedFile, target.FullPath, target.Folder, target.IsDefault, bytes, duration,
                sw.Elapsed.TotalSeconds, lufs, mix.TailSec);
        }
        finally
        {
            Mp4Exporter.TryDelete(partPath);
            Mp4Exporter.TryDelete(mixWavPath);
        }
    }

    /// <summary>
    /// Writes one WAV per track that sounds in the mix (native only, "stems"), each alone through its own chain and
    /// sends without the master limiter (<see cref="MixRenderer.Render"/> with <c>only</c>), all from 0 and as long as
    /// the export (<c>durationSec</c>, effect tails included) so they line up when dropped into another program. They
    /// keep the mix's level: the loudness gain of a social preset is for the finished mix only. 32-bit float, so a stem louder than 0 dBFS (no
    /// limiter) never clips. They go to "&lt;name&gt; - stems" next to the export ("(2)", "(3)"… when it exists and
    /// is not being overwritten); each file is written as a hidden .part and renamed. Returns the folder and the count.
    /// With the job's <see cref="ExportJobSpec.Range"/> they cover that stretch, like the export.
    /// "Avanzado" (#208): <paramref name="tracks"/> picks which tracks (indexes into the job's tracks; null = every one
    /// that sounds), <paramref name="nodes"/> adds a stem per folder or bus (indexes into <see cref="MixBusSpec.Nodes"/>:
    /// what comes out of it, <see cref="MixRenderer.Render"/> with <c>onlyNode</c>), and <paramref name="sampleRate"/>
    /// resamples them to the export's rate so they line up with it in another program.
    /// </summary>
    public static async Task<(string Folder, int Count)> ExportStemsAsync(ExportJobSpec job, string exportFullPath,
        Action<double>? onProgress = null, CancellationToken ct = default, double? durationSec = null,
        IReadOnlyCollection<int>? tracks = null, IReadOnlyList<int>? nodes = null, int? sampleRate = null)
    {
        var audible = MixRenderer.Audible(job.MixTracks).Where(i => tracks == null || tracks.Contains(i)).ToList();
        var buses = (nodes ?? []).Where(j => j >= 0 && j < job.MixBus.Nodes.Count).Distinct().ToList();
        int total = audible.Count + buses.Count;
        if (total == 0)
            throw new ExportException(Texts.T("err.export.noTracks", "No hay canales en el timeline."));
        int outRate = sampleRate ?? job.SampleRate;
        string parent = Path.GetDirectoryName(exportFullPath)!;
        string baseName = Path.GetFileNameWithoutExtension(exportFullPath) + " - stems";
        string folder = Path.Combine(parent, baseName);
        for (int k = 2; Directory.Exists(folder) && !job.Overwrite; k++) folder = Path.Combine(parent, $"{baseName} ({k})");
        Directory.CreateDirectory(folder);

        double song = Math.Max(1.0, MixRenderer.MixDurationSec(job.MixTracks, job.SampleRate));
        var range = ExportRange.Fit(job.Range, Math.Max(song, job.Range?.EndSec ?? 0));
        double from = range?.StartSec ?? 0, stop = range?.EndSec ?? double.PositiveInfinity;
        double duration = durationSec ?? (range is { } rg ? rg.LengthSec + job.TailSec : song);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int s = 0; s < total; s++)
        {
            ct.ThrowIfCancellationRequested();
            bool isNode = s >= audible.Count;
            int idx = isNode ? buses[s - audible.Count] : audible[s];
            string name = StemName(s + 1, isNode ? job.MixBus.Nodes[idx].Name : job.MixTracks[idx].Name, used);
            string path = Path.Combine(folder, name);
            string part = Path.Combine(folder, $".{name}.part");
            try
            {
                int at = s;
                var (l, r) = await Task.Run(() =>
                {
                    var (fl, fr) = MixRenderer.Render(job.MixTracks, job.MixBus, from + duration, job.SampleRate,
                        f => onProgress?.Invoke((at + f * 0.95) / total), ct, only: isNode ? -1 : idx, stopSec: stop,
                        onlyNode: isNode ? idx : -1);
                    var cut = ExportRange.From(fl, fr, from, job.SampleRate);
                    return outRate == job.SampleRate ? cut : AudioLoader.Resample(cut.Left, cut.Right, job.SampleRate, outRate);
                }, ct).ConfigureAwait(false);
                await Task.Run(() => Mp4Exporter.WriteFloatStereoWav(part, l, r, outRate), ct).ConfigureAwait(false);
                try
                {
                    File.Move(part, path, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new ExportException(Texts.T("err.export.replace", "No se pudo reemplazar {0}: puede estar abierto en otro programa.", name));
                }
            }
            finally
            {
                Mp4Exporter.TryDelete(part);
            }
            onProgress?.Invoke((s + 1.0) / total);
        }
        return (folder, total);
    }

    /// <summary>"01 Guitarra.wav": the number keeps the timeline's order; characters Windows refuses become "_".</summary>
    static string StemName(int number, string trackName, HashSet<string> used)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new string((trackName ?? "").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        string name = $"{number:00} {(clean.Length == 0 ? Texts.T("export.stem.track", "Pista") : clean)}";
        if (name.Length > 80) name = name[..80].TrimEnd();
        string file = name + ".wav";
        for (int k = 2; !used.Add(file); k++) file = $"{name} ({k}).wav";
        return file;
    }

    /// <summary>The mix ready to write: at its final rate, with how many channels go to the file (mono = L only).</summary>
    public sealed record FinishedPcm(float[] L, float[] R, int Rate, int Channels);

    /// <summary>
    /// What "Avanzado" does after the master, in this order: mono (the average of both sides, so a wide mix does not
    /// jump in level), the resampler (<see cref="AudioLoader.Resample"/>), and the normalize gain to the chosen true
    /// peak (measured on the resampled audio, which is what the file holds). With the defaults it hands back the mix as it is.
    /// </summary>
    public static FinishedPcm Finish(float[] left, float[] right, int sampleRate, AudioExportSettings s)
    {
        float[] l = left, r = right;
        if (s.Mono)
        {
            int n = Math.Min(l.Length, r.Length);
            var m = new float[n];
            for (int i = 0; i < n; i++) m[i] = 0.5f * (l[i] + r[i]);
            (l, r) = (m, m);
        }
        int rate = sampleRate;
        if (s.SampleRate != sampleRate)
        {
            var (rl, rr) = AudioLoader.Resample(l, r, sampleRate, s.SampleRate);
            (l, r, rate) = (rl, s.Mono ? rl : rr, s.SampleRate);
        }
        if (s.NormalizeDbtp is { } goal)
        {
            double peak = TruePeak.MeasureDb(l, r, l.Length);
            if (double.IsFinite(peak) && peak > -120)
            {
                float g = (float)Math.Pow(10, (goal - peak) / 20);
                var nl = new float[l.Length];
                for (int i = 0; i < nl.Length; i++) nl[i] = l[i] * g;
                var nr = nl;
                if (!s.Mono)
                {
                    nr = new float[r.Length];
                    for (int i = 0; i < nr.Length; i++) nr[i] = r[i] * g;
                }
                (l, r) = (nl, nr);
            }
        }
        return new FinishedPcm(l, r, rate, s.Mono ? 1 : 2);
    }

    /// <summary>
    /// Writes <paramref name="pcm"/> as WAV (little-endian; 32 bits is IEEE float) or AIFF (big-endian, integer only).
    /// 16 bits gets TPDF dither (two uniform randoms, ±1 LSB) when <see cref="AudioExportSettings.Dither"/> is on: without
    /// it a quiet fade turns into steps. The integer formats clamp: the mix comes out of the master limiter, so that only
    /// guards rounding (32-bit float keeps whatever is there). The seed is fixed so an export is the same every time.
    /// </summary>
    public static void WritePcm(string path, FinishedPcm pcm, AudioExportSettings s, bool aiff)
    {
        int ch = pcm.Channels, depth = s.BitDepth, bytes = depth / 8, frame = ch * bytes;
        bool fl = depth == 32 && !aiff;
        int n = Math.Min(pcm.L.Length, pcm.R.Length);
        long data = (long)n * frame;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        fs.Write(aiff ? AiffHeader(pcm.Rate, ch, depth, n) : WavHeader(pcm.Rate, ch, depth, fl, data));
        var rnd = new Random(208);
        bool dither = depth == 16 && s.Dither;
        double full = depth == 16 ? 32767.0 : depth == 24 ? 8388607.0 : 2147483647.0;
        byte[] buf = new byte[8192 * frame];
        for (int i = 0; i < n;)
        {
            int chunk = Math.Min(8192, n - i);
            for (int j = 0; j < chunk; j++)
                for (int c = 0; c < ch; c++)
                {
                    float v = c == 0 ? pcm.L[i + j] : pcm.R[i + j];
                    int at = (j * ch + c) * bytes;
                    if (fl)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(at), v);
                        continue;
                    }
                    double x = Math.Clamp((double)v, -1, 1) * full;
                    if (dither) x += rnd.NextDouble() - rnd.NextDouble();
                    long q = (long)Math.Round(x);
                    q = Math.Clamp(q, -(long)full - 1, (long)full);
                    for (int b = 0; b < bytes; b++)
                        buf[at + (aiff ? bytes - 1 - b : b)] = (byte)(q >> (8 * b));
                }
            fs.Write(buf, 0, chunk * frame);
            i += chunk;
        }
        if (aiff && (data & 1) == 1) fs.WriteByte(0); // chunks are padded to an even length
        fs.Flush(true);
    }

    static byte[] WavHeader(int rate, int ch, int depth, bool fl, long data)
    {
        int block = ch * depth / 8;
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, data + 36));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), (short)(fl ? 3 : 1)); // IEEE float or PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), (short)ch);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), rate * block);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), (short)block);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), (short)depth);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, data));
        return h;
    }

    /// <summary>FORM/AIFF with COMM (the rate as an 80-bit extended float, as the format asks) and SSND.</summary>
    static byte[] AiffHeader(int rate, int ch, int depth, int frames)
    {
        long data = (long)frames * ch * depth / 8;
        var h = new byte[54];
        "FORM"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, 46 + data + (data & 1)));
        "AIFFCOMM"u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(16), 18);
        BinaryPrimitives.WriteInt16BigEndian(h.AsSpan(20), (short)ch);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(22), (uint)frames);
        BinaryPrimitives.WriteInt16BigEndian(h.AsSpan(26), (short)depth);
        // 80-bit extended: sign and exponent (bias 16383), then a 64-bit mantissa with its explicit leading 1
        int exp = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)rate);
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(28), (ushort)(16383 + exp));
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(30), (ulong)rate << (63 - exp));
        "SSND"u8.CopyTo(h.AsSpan(38));
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(42), (uint)Math.Min(uint.MaxValue, data + 8));
        // offset and block size: both 0
        return h;
    }

    /// <summary>FFmpeg's arguments for <paramref name="s"/>'s format: the WAV it gets is already at the final rate and depth.</summary>
    static string[] EncoderArgs(AudioExportSettings s) => s.Format switch
    {
        AudioFormat.Mp3 => s.Kbps is { } k ? ["-c:a", "libmp3lame", "-b:a", $"{k}k", "-f", "mp3"] : ["-c:a", "libmp3lame", "-q:a", "0", "-f", "mp3"],
        AudioFormat.Ogg => s.Kbps is { } k ? ["-c:a", "libvorbis", "-b:a", $"{k}k", "-f", "ogg"] : ["-c:a", "libvorbis", "-q:a", "8", "-f", "ogg"],
        _ => ["-c:a", "flac", "-compression_level", "8", "-f", "flac"],
    };

    static async Task Encode(string wav, string part, AudioExportSettings s, double durationSec, Action<double> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg", ["-y", "-i", wav, .. EncoderArgs(s), "-progress", "pipe:1", "-nostats", part])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) throw new ExportException("No se pudo iniciar FFmpeg.");
        }
        catch (Win32Exception ex)
        {
            throw new ExportException(ex.NativeErrorCode == 2 ? Texts.T("err.ffmpeg.missing", "No se encontró FFmpeg en el PATH.") : Texts.T("err.ffmpeg.start", "No se pudo iniciar FFmpeg: {0}", ex.Message));
        }
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 20000) stderr.Remove(0, stderr.Length - 20000);
            }
        };
        proc.BeginErrorReadLine();
        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { }
        });
        while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
            if (double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out double us) && us > 0)
                progress(Math.Clamp(us / 1e6 / durationSec, 0, 1));
        }
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            string err;
            lock (stderr) err = stderr.ToString().Trim();
            throw new ExportException(Texts.T("err.export.encode", "No se pudo convertir a {0} con FFmpeg.", FormatLabel(s.Format)), err.Length > 1200 ? err[^1200..] : err);
        }
    }
}
