using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Audio;

/// <summary>
/// Transposed and stretched copies of a take's audio on disk (<see cref="PitchShifter"/> takes seconds, the playback
/// wants them at once on the next open), in the project's cache folder. The original media is never touched. A file's
/// name says which take, how many semitones and how much longer; its hash covers the source file's path, size and date, its length and the
/// algorithm's version, so an edited source or a better algorithm makes a new one. Raw float32 behind a short header,
/// written to a temporary file and renamed, so a crash halfway leaves no broken cache, at most a stray ".tmp".
/// </summary>
public static class TransposeCache
{
    const uint Magic = 0x50544347; // "GCTP"
    const int HeaderBytes = 12;
    const string Extension = ".f32";

    /// <summary>A stretch rounded to what the cache tells apart, the same in the name and in the render.</summary>
    public static double Step(double stretch) => Math.Round(stretch, 6);

    /// <summary>The cache file for that source moved by <paramref name="semitones"/> and made <paramref name="stretch"/> times as long, whether it exists or not.</summary>
    public static string PathFor(string cacheDir, string sourcePath, int length, double semitones, double stretch = 1)
    {
        var info = new FileInfo(sourcePath);
        string key = $"{Path.GetFullPath(sourcePath).ToLowerInvariant()}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}|{length}|{PitchShifter.Version}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
        string semis = semitones.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture);
        string longer = Step(stretch) == 1 ? "" : "." + Step(stretch).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + "x";
        return Path.Combine(cacheDir, $"{Path.GetFileNameWithoutExtension(sourcePath)}.{semis}st{longer}.{hash}{Extension}");
    }

    /// <summary>
    /// The take moved by <paramref name="semitones"/> and made <paramref name="stretch"/> times as long: from the cache
    /// when it is there, else computed and saved (a cache that can't be written only costs the next open the wait).
    /// Neither gives the take itself.
    /// </summary>
    public static (float[] Left, float[] Right) Get(string cacheDir, string sourcePath, float[] left, float[] right, double semitones,
        double stretch = 1, Action<double>? progress = null, CancellationToken ct = default)
    {
        stretch = Step(stretch);
        if (Math.Abs(semitones) < 1e-6 && stretch == 1) return (left, right);
        string path = PathFor(cacheDir, sourcePath, left.Length, semitones, stretch);
        if (TryRead(path, PitchShifter.StretchedLength(left.Length, stretch)) is { } cached) return cached;
        var shifted = PitchShifter.Render(left, right, semitones, stretch, progress, ct);
        try { Write(path, shifted.Left, shifted.Right); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return shifted;
    }

    /// <summary>The cached audio, or null when it is missing, of another length or broken.</summary>
    public static (float[] Left, float[] Right)? TryRead(string path, int length)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != HeaderBytes + 8L * length) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Span<byte> head = stackalloc byte[HeaderBytes];
            fs.ReadExactly(head);
            if (BitConverter.ToUInt32(head) != Magic || BitConverter.ToInt32(head[4..]) != PitchShifter.Version || BitConverter.ToInt32(head[8..]) != length)
                return null;
            var l = new float[length];
            var r = new float[length];
            fs.ReadExactly(MemoryMarshal.AsBytes(l.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(r.AsSpan()));
            return (l, r);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static void Write(string path, float[] left, float[] right)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            Span<byte> head = stackalloc byte[HeaderBytes];
            BitConverter.TryWriteBytes(head, Magic);
            BitConverter.TryWriteBytes(head[4..], PitchShifter.Version);
            BitConverter.TryWriteBytes(head[8..], left.Length);
            fs.Write(head);
            fs.Write(MemoryMarshal.AsBytes(left.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(right.AsSpan()));
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes the cache files (and stray temporaries) that are not in <paramref name="keep"/>: the renders no
    /// clip uses any more. A file in use is left for the next time.
    /// </summary>
    public static void Prune(string cacheDir, IReadOnlyCollection<string> keep)
    {
        if (!Directory.Exists(cacheDir)) return;
        var wanted = new HashSet<string>(keep.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(cacheDir))
        {
            if (!f.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && !f.EndsWith(Extension + ".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (wanted.Contains(Path.GetFullPath(f))) continue;
            try { File.Delete(f); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
