using XtremeDaw.Audio;

namespace XtremeDaw.Projects;

/// <summary>
/// Where a take's files are, once it is in the project: copying a linked take into media/ (<see cref="CopyIn"/>) and
/// finding a file that moved, chosen by hand (<see cref="Pick"/>) or looked for in a folder (<see cref="Search"/>).
/// All three only read outside files and never move or delete them; they run off the UI thread and touch no project
/// state. What they return is a <see cref="Place"/>, which <see cref="Apply"/> then puts on the media, as an edit with its
/// undo step (<see cref="UndoManager"/> keeps the places). A copy goes through a temporary name, forced to disk before it
/// gets its real one, and the project only points at it afterwards: dying at any moment leaves the link as it was, at
/// most with a finished copy that <see cref="ProjectStore"/> brings back as an unused take. The same goes for undoing a
/// copy: the copy stays in media/, since no media file is ever deleted by an edit, and comes back unused on the next open.
/// A file can't be told by its contents, so <see cref="Pick"/> refuses one of another length (the clips' cuts count from
/// the file's ends) and <see cref="Search"/> wants the name and, when known, the size; a link older than the sizes
/// goes by the name and the length, and never guesses between two that fit.
/// </summary>
public static class LinkedMedia
{
    /// <summary>How much a file's length can differ and still be the same take: decoders disagree on an MP3's padding.</summary>
    const double SameLengthSec = 0.1;

    /// <summary>Where a take's files are to go; the old paths let <see cref="Apply"/> skip a take that changed meanwhile.</summary>
    public sealed record Place(string MediaId, string? OldAudio, string? OldVideo, string? Audio, string? Video, long AudioBytes, long VideoBytes);

    /// <param name="Places">One per take with something found, even if another of its files is still missing.</param>
    /// <param name="NotFound">Names of the takes that stay offline.</param>
    /// <param name="Ambiguous">Those of them with several files that fit and no size to tell them apart.</param>
    public sealed record SearchResult(List<Place> Places, List<string> NotFound, List<string> Ambiguous);

    static string Full(string projectDir, string stored) => Path.IsPathRooted(stored) ? stored : Path.Combine(projectDir, stored);

    static bool Gone(string projectDir, string? stored) => !string.IsNullOrEmpty(stored) && !File.Exists(Full(projectDir, stored));

    static Place Same(Media m) => new(m.Id, m.Audio, m.Video, m.Audio, m.Video, m.AudioBytes, m.VideoBytes);

    /// <summary>A file of the take isn't where the project says; it can be inside the project too.</summary>
    public static bool IsMissing(string projectDir, Media m) => Gone(projectDir, m.Audio) || Gone(projectDir, m.Video);

    /// <summary>The name of the first file it lacks, for the dialog's title.</summary>
    public static string? Wanted(string projectDir, Media m) =>
        Path.GetFileName(Gone(projectDir, m.Audio) ? m.Audio : Gone(projectDir, m.Video) ? m.Video : null);

    /// <summary>The take is linked and its files are there to copy.</summary>
    public static bool CanCopy(string projectDir, Media m) => m.Linked && !IsMissing(projectDir, m);

    /// <summary>
    /// Copies what the take has outside into media/ and says where it went. Audio and video end up with the same name, as
    /// a take's two files do. Both are copied before either gets its real name.
    /// </summary>
    public static Place CopyIn(string projectDir, Media m, Action<double>? progress = null)
    {
        if (!m.Linked) return Same(m);
        if (IsMissing(projectDir, m))
            throw new FileNotFoundException(Texts.T("err.linked.missing", "Falta el archivo de «{0}».", m.Name));
        string mediaDir = Path.Combine(projectDir, ProjectStore.MediaDir);
        Directory.CreateDirectory(mediaDir);
        bool audioOut = Path.IsPathRooted(m.Audio), videoOut = Path.IsPathRooted(m.Video);
        bool shared = audioOut && videoOut && string.Equals(Path.GetFullPath(m.Audio!), Path.GetFullPath(m.Video!), StringComparison.OrdinalIgnoreCase);
        var sources = new List<string>();
        if (audioOut) sources.Add(m.Audio!);
        if (videoOut && !shared) sources.Add(m.Video!);
        long total = Math.Max(1, sources.Sum(s => new FileInfo(s).Length)), done = 0;
        var copies = new List<(string Tmp, string Dest)>();
        try
        {
            // the name it goes together under: its sound's, which can already be in media/ when only the picture is linked
            string stem = AudioImport.CleanStem(m.Audio ?? m.Video!);
            foreach (string source in sources)
            {
                string dest = AudioImport.UniquePath(mediaDir, stem, Path.GetExtension(source).ToLowerInvariant());
                stem = Path.GetFileNameWithoutExtension(dest);
                string tmp = dest + AudioImport.PartialCopy;
                copies.Add((tmp, dest));
                long size = new FileInfo(source).Length, before = done;
                AudioImport.CopyFile(source, tmp, f => progress?.Invoke((before + f * size) / total));
                if (new FileInfo(tmp).Length != size) throw new IOException(Texts.T("err.linked.changed", "«{0}» cambió mientras se copiaba.", Path.GetFileName(source)));
                done += size;
            }
            foreach (var (tmp, dest) in copies) File.Move(tmp, dest);
        }
        finally
        {
            foreach (var (tmp, _) in copies)
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
        string Inside(string dest) => $"{ProjectStore.MediaDir}/{Path.GetFileName(dest)}";
        string? audio = audioOut ? Inside(copies[0].Dest) : m.Audio;
        string? video = shared ? audio : videoOut ? Inside(copies[^1].Dest) : m.Video;
        long Size(string? stored) => stored != null ? new FileInfo(Full(projectDir, stored)).Length : 0;
        return new(m.Id, m.Audio, m.Video, audio, video, Size(audio), Size(video));
    }

    /// <summary>
    /// Takes <paramref name="file"/>, chosen by hand, as the take's missing file, audio or video by its kind when both are.
    /// The other one, when missing too, is looked for next to it. Throws <see cref="InvalidDataException"/> with a
    /// message for the user when it can't be that file.
    /// </summary>
    public static Place Pick(string projectDir, Media m, string file)
    {
        string full = Path.GetFullPath(file);
        bool audioGone = Gone(projectDir, m.Audio), videoGone = Gone(projectDir, m.Video);
        if (!audioGone && !videoGone) return Same(m);
        if (!File.Exists(full)) throw new FileNotFoundException(Texts.T("err.linked.noFile", "Ese archivo ya no está."));
        bool shared = m.Audio == m.Video;
        var place = Same(m);
        if (videoGone && !shared && (!audioGone || MediaDecoder.IsVideo(full)))
        {
            long size = new FileInfo(full).Length;
            if (m.VideoBytes > 0 && size != m.VideoBytes) throw new InvalidDataException(Other(full));
            place = place with { Video = Stored(projectDir, full), VideoBytes = size };
        }
        else
        {
            var audio = AudioImport.Read(full);
            if (m.DurationSec > 0 && Math.Abs(audio.DurationSec - m.DurationSec) > SameLengthSec) throw new InvalidDataException(Other(full));
            // what only FFmpeg reads goes in as a WAV, as when linking it the first time
            string kept = Stored(projectDir, AudioImport.LinkIn(projectDir, full, audio).RelativePath);
            long size = new FileInfo(full).Length;
            place = place with { Audio = kept, AudioBytes = audio.Converted ? 0 : size };
            if (shared) place = place with { Video = Stored(projectDir, full), VideoBytes = size };
        }
        var rest = new Media { Id = m.Id, Name = m.Name, DurationSec = m.DurationSec, Audio = place.Audio, Video = place.Video, AudioBytes = place.AudioBytes, VideoBytes = place.VideoBytes };
        return IsMissing(projectDir, rest) && Search(projectDir, [rest], Path.GetDirectoryName(full)!, deep: false).Places.FirstOrDefault() is { } next
            ? next with { OldAudio = m.Audio, OldVideo = m.Video }
            : place;
    }

    static string Other(string file) => Texts.T("err.linked.other", "«{0}» no es el mismo archivo: no coincide en duración o tamaño.", Path.GetFileName(file));

    /// <summary>A file inside the project stays relative, so the folder can still be moved.</summary>
    static string Stored(string projectDir, string path)
    {
        if (!Path.IsPathRooted(path)) return path;
        return ProjectHistory.IsOwn(projectDir, path) ? Path.GetRelativePath(projectDir, path).Replace('\\', '/') : path;
    }

    /// <summary>
    /// Looks in <paramref name="folder"/> (and under it, with <paramref name="deep"/>) for what <paramref name="media"/> lacks.
    /// A whole folder that moved comes back in one go. Reads the folder once; it decodes only files without a known size.
    /// </summary>
    public static SearchResult Search(string projectDir, IReadOnlyList<Media> media, string folder, bool deep = true, CancellationToken ct = default)
    {
        var lost = media.Where(m => IsMissing(projectDir, m)).ToList();
        var names = lost.SelectMany(m => new[] { m.Audio, m.Video }).Where(s => Gone(projectDir, s)).Select(s => Path.GetFileName(s)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        var walk = new EnumerationOptions
        {
            RecurseSubdirectories = deep,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System, // a link back up would never end
        };
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", walk))
        {
            ct.ThrowIfCancellationRequested();
            if (!names.Contains(f.Name)) continue;
            if (!files.TryGetValue(f.Name, out var list)) files[f.Name] = list = [];
            list.Add(f);
        }
        // the closest to the folder first, in the same order every time
        List<FileInfo> Named(string stored) => files.TryGetValue(Path.GetFileName(stored), out var list)
            ? [.. list.OrderBy(f => f.FullName.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)]
            : [];

        var places = new List<Place>();
        var notFound = new List<string>();
        var ambiguous = new List<string>();
        foreach (var m in lost)
        {
            ct.ThrowIfCancellationRequested();
            var place = Same(m);
            bool unclear = false;
            bool shared = m.Audio == m.Video;
            if (Gone(projectDir, m.Audio))
            {
                var hit = FindAudio(Named(m.Audio!), m, ref unclear);
                if (hit != null) place = place with { Audio = Stored(projectDir, hit.FullName), AudioBytes = hit.Length };
                if (hit != null && shared) place = place with { Video = place.Audio, VideoBytes = hit.Length };
            }
            if (!shared && Gone(projectDir, m.Video))
            {
                var hit = FindVideo(Named(m.Video!), m, ref unclear);
                if (hit != null) place = place with { Video = Stored(projectDir, hit.FullName), VideoBytes = hit.Length };
            }
            if (place != Same(m)) places.Add(place);
            if (Gone(projectDir, place.Audio) || Gone(projectDir, place.Video))
            {
                notFound.Add(m.Name);
                if (unclear) ambiguous.Add(m.Name);
            }
        }
        return new(places, notFound, ambiguous);
    }

    static FileInfo? FindAudio(List<FileInfo> named, Media m, ref bool unclear)
    {
        if (m.AudioBytes > 0) return named.Find(f => f.Length == m.AudioBytes);
        var fits = named.Where(f => Fits(f.FullName, m)).ToList();
        unclear |= fits.Count > 1;
        return fits.Count == 1 ? fits[0] : null;
    }

    static FileInfo? FindVideo(List<FileInfo> named, Media m, ref bool unclear)
    {
        if (m.VideoBytes > 0) return named.Find(f => f.Length == m.VideoBytes);
        unclear |= named.Count > 1;
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>The file reads without FFmpeg, as a link has to, and lasts what the take lasts.</summary>
    static bool Fits(string path, Media m)
    {
        try
        {
            var audio = AudioImport.Read(path);
            return !audio.Converted && (m.DurationSec <= 0 || Math.Abs(audio.DurationSec - m.DurationSec) <= SameLengthSec);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Puts the take where <paramref name="place"/> says, unless it isn't where that started from anymore.</summary>
    public static bool Apply(Project p, Place place)
    {
        if (p.FindMedia(place.MediaId) is not { } m || m.Audio != place.OldAudio || m.Video != place.OldVideo) return false;
        (m.Audio, m.Video, m.AudioBytes, m.VideoBytes) = (place.Audio, place.Video, place.AudioBytes, place.VideoBytes);
        return true;
    }
}
