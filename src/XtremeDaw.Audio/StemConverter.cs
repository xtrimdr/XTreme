using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace XtremeDaw.Audio;

/// <summary>
/// Turns a Demucs checkpoint from Meta into the ONNX of <see cref="StemSeparator"/> on the user's PC, with no Python: the
/// app ships the graph without its weights (<c>tools/make-demucs-templates.py</c>) and a <c>.map</c> that says which tensor
/// of the <c>.th</c> (a zip) fills each one, so the checkpoint is copied, never interpreted. The export sliced the attention
/// matrices and scaled the frequency embedding, hence the modes: copy (C), transposed rows (T), embedding (E). The result
/// must match the map's SHA-256, the same data as the files of <c>tools/get-demucs.ps1</c>. The graph takes its name last,
/// so a finished <c>.onnx</c> always has its weights next to it.
/// </summary>
public static class StemConverter
{
    /// <summary>The weightless graphs, next to the program.</summary>
    public static string GraphsDir => Path.Combine(AppContext.BaseDirectory, "demucs-graphs");
    /// <summary>ScaledEmbedding's scale, which the export folded into the graph.</summary>
    const float EmbeddingScale = 10;

    public static bool HasGraph(string file) =>
        File.Exists(Path.Combine(GraphsDir, file)) && File.Exists(Path.Combine(GraphsDir, Path.ChangeExtension(file, ".map")));

    /// <summary>Writes <paramref name="file"/> and its data in <paramref name="dir"/>.</summary>
    public static void Convert(string th, string file, string dir, CancellationToken ct = default)
    {
        var lines = File.ReadAllLines(Path.Combine(GraphsDir, Path.ChangeExtension(file, ".map")));
        var head = lines[0].Split(' ');
        Directory.CreateDirectory(dir);
        string data = Path.Combine(dir, file + ".data"), part = data + ".part";
        using (var zip = ZipFile.OpenRead(th))
        using (var output = new FileStream(part, FileMode.Create, FileAccess.Write))
        {
            var entries = zip.Entries.ToDictionary(e => e.FullName[(e.FullName.LastIndexOf("/data/", StringComparison.Ordinal) + 6)..]);
            foreach (var line in lines.Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                if (line.Length > 0) output.Write(Tensor(line.Split(' '), entries));
            }
            output.Flush(true);
        }
        string hash;
        using (var f = File.OpenRead(part)) hash = System.Convert.ToHexString(SHA256.HashData(f));
        if (!hash.Equals(head[1], StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException(Texts.T("err.download.hash", "el archivo descargado está dañado, volvé a intentarlo"));
        }
        File.Move(part, data, true);
        string graph = Path.Combine(dir, file);
        File.Copy(Path.Combine(GraphsDir, file), graph + ".part", true);
        File.Move(graph + ".part", graph, true);
    }

    /// <param name="f">entry, source type, target type, mode, a, b, start, offset, bytes.</param>
    static byte[] Tensor(string[] f, Dictionary<string, ZipArchiveEntry> entries)
    {
        bool fromHalf = f[1] == "f2", toHalf = f[2] == "f2";
        int a = int.Parse(f[4]), b = int.Parse(f[5]);
        float start = float.Parse(f[6], CultureInfo.InvariantCulture);
        int bytes = int.Parse(f[8]), size = toHalf ? 2 : 4, n = bytes / size;

        using var stream = entries[f[0]].Open();
        var raw = new byte[entries[f[0]].Length];
        stream.ReadExactly(raw);
        float At(int i) => fromHalf ? (float)BitConverter.ToHalf(raw, i * 2) : BitConverter.ToSingle(raw, i * 4);

        var values = new float[n];
        switch (f[3])
        {
            case "C":
                for (int i = 0; i < n; i++) values[i] = At((int)start + i);
                break;
            case "T":
                for (int row = 0; row < b; row++)
                    for (int col = 0; col < a; col++) values[col * b + row] = At(((int)start + row) * a + col);
                break;
            default:
                for (int freq = 0; freq < a; freq++)
                    for (int c = 0; c < b; c++) values[c * a + freq] = At(freq * b + c) * EmbeddingScale * start;
                break;
        }
        var result = new byte[bytes];
        for (int i = 0; i < n; i++)
            if (toHalf) BitConverter.TryWriteBytes(result.AsSpan(i * 2), (Half)values[i]);
            else BitConverter.TryWriteBytes(result.AsSpan(i * 4), values[i]);
        return result;
    }
}
