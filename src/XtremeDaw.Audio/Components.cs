using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace XtremeDaw.Audio;

/// <summary>Something that can be installed at once: all of a component, or the part one option needs.</summary>
/// <param name="Id">Stable; the app's text is <c>component.package.{Id}</c>.</param>
/// <param name="Bytes">What is left to download; 0 when unknown.</param>
public sealed record ComponentPackage(string Id, IReadOnlyList<string> Files, long Bytes);

/// <summary>
/// What the app can do without but offers to install (asked by the user): the one list behind the notice at startup,
/// Configuración, Acerca de and the disabled features, so whatever comes later only has to be added to
/// <see cref="Components.All"/>. Names and descriptions are the window's (<c>component.{Id}.name</c>); installing fails
/// with an exception whose message is for the user.
/// </summary>
public abstract class OptionalComponent(string id)
{
    public string Id { get; } = id;
    public abstract bool Ready { get; }
    /// <summary>Some of it is here: enough for what is, not for everything.</summary>
    public virtual bool Partial => false;
    /// <summary>Looks again; may take a moment, so it runs on a worker thread.</summary>
    public abstract void Refresh();
    /// <summary>Whether <see cref="InstallAsync"/> can run on this PC right now.</summary>
    public abstract bool CanInstall { get; }
    /// <summary>A page to get it by hand, when there is one.</summary>
    public virtual string? ManualUrl => null;
    /// <summary>Where the terms of something that isn't under XTreme's license are; the install shows them.</summary>
    public virtual string? TermsPage => null;
    /// <summary>The choices, biggest last; the last is the default.</summary>
    public abstract IReadOnlyList<ComponentPackage> Packages { get; }
    /// <param name="package">Null installs everything.</param>
    /// <param name="progress">0..1, or below 0 when it can't be known.</param>
    public abstract Task InstallAsync(ComponentPackage? package, Action<double> progress, CancellationToken ct);
}

/// <summary>FFmpeg, on the PATH; <see cref="FfmpegSetup"/> installs it with winget.</summary>
public sealed class FfmpegComponent() : OptionalComponent("ffmpeg")
{
    string? version;
    bool winget;

    /// <summary>What it answered; null when it isn't there.</summary>
    public string? Version => version;
    public override bool Ready => version != null;
    public override bool CanInstall => winget;
    public override string ManualUrl => FfmpegSetup.DownloadUrl;
    public override IReadOnlyList<ComponentPackage> Packages => [new("ffmpeg", [], 0)];

    public override void Refresh()
    {
        FfmpegSetup.RefreshPath();
        VideoEncoders.Forget();
        version = FfmpegInfo.Probe();
        if (version == null) winget = FfmpegSetup.HasWinget();
    }

    public override async Task InstallAsync(ComponentPackage? package, Action<double> progress, CancellationToken ct)
    {
        progress(-1);
        var r = await FfmpegSetup.InstallAsync(ct).ConfigureAwait(false);
        if (r.Ready) return;
        // winget said yes but this process still can't see it: a new PATH entry Windows hasn't published yet
        throw new InvalidOperationException(r.ExitCode == 0
            ? Texts.T("err.ffmpeg.restart", "reiniciá XTreme para usarlo")
            : r.Log.Split('\n')[^1]);
    }
}

/// <summary>
/// The Demucs models of <see cref="StemSeparator"/>. Nothing of Meta's is hosted or shipped (the user's decision): each
/// option downloads its checkpoints straight from Meta to this PC and <see cref="StemConverter"/> builds the ONNX there, so
/// each costs only what it uses. They come under Meta's own terms, not XTreme's license, which the install says (see
/// <see cref="TermsUrl"/>). <c>XTREME_MODELS_URL</c> points it at a test server; the hashes are checked either way.
/// </summary>
public sealed class StemsComponent() : OptionalComponent("stems")
{
    /// <summary>Meta's own files, the ones Demucs downloads.</summary>
    const string SourceUrl = "https://dl.fbaipublicfiles.com/demucs/hybrid_transformer";
    public const string TermsUrl = "https://github.com/facebookresearch/demucs";
    public const string UrlVariable = "XTREME_MODELS_URL";

    public sealed record Model(string File, string Checkpoint, long Bytes, string Sha256);

    public static readonly Model[] Models =
    [
        new("htdemucs_6s.onnx", "5c90dfd2-34c22ccb.th", 54996327, "34c22ccb381c6f9fdbf324f04e1e2fe21aaaf293f5ded163a162697ff9a02ddd"),
        new("htdemucs.onnx", "955717e8-8726e21a.th", 84141911, "8726e21a993978c7ba086d3872e7608d7d5bfca646ca4aca459ffda844faa8b4"),
        new("htdemucs_ft_vocals.onnx", "04573f0d-f3cf25b2.th", 84141271, "f3cf25b222c4eed7cd49dd8b2c9597d50c18bd154090f7b919cfa5f93cf22c49"),
        new("htdemucs_ft_drums.onnx", "f7e0c4bc-ba3fe64a.th", 84141271, "ba3fe64ae8ef66ac9a4857222ce48efbdc5eb3ad375cb79dd13debee5aaa4066"),
        new("htdemucs_ft_bass.onnx", "d12395a8-e57c48e6.th", 84141271, "e57c48e6b0e38af4f7118d7bd08c49f0a0c0edf7d09143bdd902ea0d237303e6"),
    ];

    public static string BaseUrl
    {
        get
        {
            string? url = Environment.GetEnvironmentVariable(UrlVariable);
            return (string.IsNullOrWhiteSpace(url) ? SourceUrl : url).TrimEnd('/');
        }
    }

    static bool Have(string file) => File.Exists(StemSeparator.ModelPath(file));

    public override bool Ready => Models.All(m => Have(m.File));
    public override bool Partial => !Ready && StemSeparator.AnyAvailable;
    /// <summary>A build without the graphs (<c>tools/make-demucs-templates.py</c>) can't convert anything.</summary>
    public override bool CanInstall => Models.All(m => StemConverter.HasGraph(m.File));
    public override string TermsPage => TermsUrl;
    public override void Refresh() { }

    public override IReadOnlyList<ComponentPackage> Packages
    {
        get
        {
            var list = new List<ComponentPackage>();
            void Add(string id, IEnumerable<string> files)
            {
                var p = Package(id, files);
                if (p.Files.Count > 0 && list.All(o => o.Bytes != p.Bytes)) list.Add(p);
            }
            Add("stems6", ["htdemucs_6s.onnx"]);
            Add("stemsFast", ["htdemucs.onnx"]);
            Add("stemsHigh", StemSeparator.Files(StemSeparator.Layout.Four, StemSeparator.Quality.High));
            Add("stemsAll", Models.Select(m => m.File));
            return list;
        }
    }

    /// <summary>What one option of the dialog still needs.</summary>
    public static ComponentPackage For(StemSeparator.Layout layout, StemSeparator.Quality quality) =>
        Package("stemsOption", StemSeparator.Files(layout, quality));

    static ComponentPackage Package(string id, IEnumerable<string> files)
    {
        var missing = Models.Where(m => files.Contains(m.File) && !Have(m.File)).ToList();
        return new(id, missing.Select(m => m.File).ToList(), missing.Sum(m => m.Bytes));
    }

    public override async Task InstallAsync(ComponentPackage? package, Action<double> progress, CancellationToken ct)
    {
        var todo = Models.Where(m => (package == null || package.Files.Contains(m.File)) && !Have(m.File)).ToList();
        long total = Math.Max(1, todo.Sum(m => m.Bytes)), done = 0;
        foreach (var m in todo)
        {
            long before = done;
            string th = Path.Combine(StemSeparator.DownloadDir, m.Checkpoint);
            try
            {
                await FileDownload.GetAsync($"{BaseUrl}/{m.Checkpoint}", th, m.Bytes, m.Sha256,
                    got => progress((before + got) / (double)total), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TimeoutException)
            {
                throw new InvalidOperationException(Texts.T("err.stems.source", "la fuente original de Meta no responde"), e);
            }
            await Task.Run(() => StemConverter.Convert(th, m.File, StemSeparator.DownloadDir, ct), ct).ConfigureAwait(false);
            File.Delete(th);
            done += m.Bytes;
        }
        progress(1);
    }
}

/// <summary>The list; new components are added here.</summary>
public static class Components
{
    public static readonly FfmpegComponent Ffmpeg = new();
    public static readonly StemsComponent Stems = new();
    public static readonly OptionalComponent[] All = [Ffmpeg, Stems];

    public static void Refresh()
    {
        foreach (var c in All) c.Refresh();
    }
}

/// <summary>
/// Downloads a file without ever leaving half of it under its name: it goes to <c>.part</c>, which survives a cut or a
/// cancel, so the next try asks only for the rest (HTTP Range; a server that answers with the whole file starts over),
/// and it only takes the real name once its size and SHA-256 match. A cut is retried a few times by itself.
/// </summary>
public static class FileDownload
{
    const int BufferSize = 1 << 16;
    /// <summary>Retries of one file after a cut; each one continues where it stopped.</summary>
    const int Retries = 4;
    const int RetryWaitMs = 1000;
    /// <summary>A connection silent for this long counts as cut.</summary>
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <param name="got">Bytes of the file so far.</param>
    public static async Task GetAsync(string url, string dest, long size, string sha256, Action<long> got, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string part = dest + ".part";
        if (File.Exists(part) && new FileInfo(part).Length > size) File.Delete(part);
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dest))!).AvailableFreeSpace;
        if (free < size - have) throw new IOException(Texts.T("err.download.space", "no hay espacio en el disco"));

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await Fetch(url, part, size, got, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TimeoutException && !ct.IsCancellationRequested && attempt < Retries)
            {
                await Task.Delay(RetryWaitMs, ct).ConfigureAwait(false);
            }
        }
        string hash;
        await using (var f = File.OpenRead(part))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
        if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException(Texts.T("err.download.hash", "el archivo descargado está dañado, volvé a intentarlo"));
        }
        File.Move(part, dest, true);
    }

    static async Task Fetch(string url, string part, long size, Action<long> got, CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        got(have);
        if (have == size) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (reply.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(part);
            throw new IOException("range");
        }
        if ((int)reply.StatusCode >= 500) throw new HttpRequestException(reply.ReasonPhrase);
        if (!reply.IsSuccessStatusCode)
            throw new InvalidOperationException(Texts.T("err.download.http", "el servidor respondió {0}", (int)reply.StatusCode));
        bool resume = reply.StatusCode == HttpStatusCode.PartialContent && reply.Content.Headers.ContentRange?.From == have;
        if (!resume) have = 0;

        await using var body = await reply.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, true);
        var buffer = new byte[BufferSize];
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(Stall);
            int n;
            try { n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(); }
            if (n == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            have += n;
            got(have);
        }
        file.Flush(true);
        if (have != size) throw new IOException("short");
    }
}
