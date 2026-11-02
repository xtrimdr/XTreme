using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Wave;

namespace XtremeDaw.Audio;

/// <summary>
/// Reads the sound of nearly any audio or video file (the Biblioteca's samples, imports). NAudio goes first on what it
/// reads natively, because it needs nothing else installed; whatever it can't read, and every other format, is decoded
/// by ffmpeg.exe through a pipe at the engine's rate, stereo. A result that came from FFmpeg is marked
/// <see cref="AudioLoader.LoadedAudio.Converted"/>: the project keeps it as a WAV, so opening that project later never
/// depends on FFmpeg being there. Lists of extensions, not sniffing, because a sample folder holds thousands of files.
/// </summary>
public static class MediaDecoder
{
    /// <summary>What NAudio and Media Foundation read on their own.</summary>
    static readonly string[] NativeExtensions = [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aif", ".aiff"];

    public static readonly string[] AudioExtensions =
    [
        ".wav", ".wave", ".bwf", ".w64", ".rf64", ".mp3", ".mp2", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".m4b", ".aac", ".ac3", ".eac3",
        ".dts", ".wma", ".aif", ".aiff", ".aifc", ".caf", ".ape", ".wv", ".tta", ".tak", ".alac", ".amr", ".au", ".snd", ".ra", ".mka", ".mpc", ".spx", ".voc",
    ];

    public static readonly string[] VideoExtensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob", ".mxf", ".asf",
    ];

    /// <summary>Stereo float: what the pipe carries per frame.</summary>
    const int FrameBytes = 8;
    const int PipeChunk = 1 << 18;
    /// <summary>A probe that takes longer than this counts as a file it can't read.</summary>
    const int ProbeTimeoutMs = 10000;
    /// <summary>The last of FFmpeg's error output, for the message.</summary>
    const int ErrorChars = 300;

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsMedia(string path) => IsAudio(path) || IsVideo(path);

    /// <summary>
    /// The file's sound at <paramref name="sampleRate"/> (0 = its own with NAudio, the engine's with FFmpeg), at most
    /// <paramref name="maxSec"/> seconds of it (0 = all). Throws <see cref="InvalidDataException"/> with a message for the user.
    /// </summary>
    public static AudioLoader.LoadedAudio Load(string path, int sampleRate = 0, Action<double>? progress = null, double maxSec = 0, CancellationToken ct = default)
    {
        if (NativeExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            try { return AudioLoader.Load(path, sampleRate, progress, maxSec); }
            catch (Exception e) when (e is not OperationCanceledException && !FfmpegReady) { throw new InvalidDataException(e.Message.TrimEnd('.'), e); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // an unusual flavor of a common format (A-law, 64-bit, an M4A without its Windows codec): FFmpeg below
            }
        return Ffmpeg(path, sampleRate > 0 ? sampleRate : AudioLoader.DefaultSampleRate, progress, maxSec, ct);
    }

    /// <summary>The file's length in seconds, without decoding it: 0 when it has no audio or can't be read.</summary>
    public static double Duration(string path)
    {
        if (NativeExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            try
            {
                using var r = new AudioFileReader(path);
                return r.TotalTime.TotalSeconds;
            }
            catch (Exception e) when (e is not OperationCanceledException) { /* FFmpeg may still read it */ }
        var psi = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration:format=duration", "-of", "csv=p=0", path }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return 0;
            _ = p.StandardError.ReadToEndAsync();
            string text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(ProbeTimeoutMs)) { try { p.Kill(); } catch (InvalidOperationException) { } return 0; }
            // a file with no audio stream gives only the container's line, so one line means nothing to hear
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || (IsVideo(path) && lines.Length < 2)) return 0;
            foreach (string line in lines)
                if (double.TryParse(line.Trim(','), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) && sec > 0) return sec;
            return 0;
        }
        catch (Win32Exception) { return 0; }
    }

    /// <summary>The loudest sample of each of <paramref name="bins"/> equal slices (0..1, both channels together).</summary>
    public static byte[] Peaks(float[] left, float[] right, int bins)
    {
        var peaks = new byte[bins];
        if (left.Length == 0) return peaks;
        float max = 1e-4f;
        var raw = new float[bins];
        for (int i = 0; i < left.Length; i++)
        {
            int b = (int)(i * (long)bins / left.Length);
            float v = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            if (v > raw[b]) raw[b] = v;
            if (v > max) max = v;
        }
        for (int b = 0; b < bins; b++) peaks[b] = (byte)Math.Round(raw[b] / max * 255);
        return peaks;
    }

    static bool FfmpegReady => FfmpegInfo.Probe() != null;

    static AudioLoader.LoadedAudio Ffmpeg(string path, int rate, Action<double>? progress, double maxSec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-v", "error", "-nostdin", "-i", path, "-vn", "-sn", "-dn", "-map", "0:a:0", "-ac", "2", "-ar", rate.ToString(CultureInfo.InvariantCulture) };
        if (maxSec > 0) args.AddRange(["-t", maxSec.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-f", "f32le", "pipe:1"]);
        foreach (string a in args) psi.ArgumentList.Add(a);

        double expected = progress == null ? 0 : maxSec > 0 ? maxSec : Duration(path);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception) { p = null; }
        if (p == null) throw new InvalidDataException(Texts.T("err.decode.ffmpeg", "hace falta FFmpeg para leer este formato"));
        using (p)
        using (ct.Register(() => { try { p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { } }))
        {
            var stderr = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            var bytes = new byte[PipeChunk];
            var all = new float[PipeChunk / sizeof(float) * 8];
            long got = 0;
            var pipe = p.StandardOutput.BaseStream;
            for (int n; (n = pipe.Read(bytes, 0, bytes.Length)) > 0;)
            {
                if ((got + n) / sizeof(float) >= all.Length) Array.Resize(ref all, checked(all.Length * 2));
                Buffer.BlockCopy(bytes, 0, all, checked((int)got), n);
                got += n;
                if (expected > 0) progress?.Invoke(Math.Min(1, got / (double)FrameBytes / rate / expected));
            }
            p.WaitForExit();
            ct.ThrowIfCancellationRequested();
            int frames = (int)(got / FrameBytes);
            if (frames == 0)
            {
                string why;
                lock (stderr) why = stderr.ToString().Trim();
                if (why.Length > ErrorChars) why = why[^ErrorChars..];
                throw new InvalidDataException(why.Contains("matches no streams", StringComparison.OrdinalIgnoreCase) || why.Length == 0
                    ? Texts.T("err.decode.noAudio", "no tiene audio") : why);
            }
            var l = new float[frames];
            var r = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                l[i] = all[2 * i];
                r[i] = all[2 * i + 1];
            }
            return new AudioLoader.LoadedAudio(l, r, rate, rate, 2, Converted: true);
        }
    }
}
