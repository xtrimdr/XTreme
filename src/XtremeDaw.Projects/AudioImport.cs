using XtremeDaw.Audio;
using NAudio.Utils;
using NAudio.Wave;

namespace XtremeDaw.Projects;

/// <summary>
/// Brings an audio file from outside (a backing track, a loop, a take made elsewhere) into the project
/// (native only: the web only plays what it records). Three steps so the slow ones can run off the UI thread:
/// <see cref="Read"/> decodes the file, <c>CopyIn</c> copies it into media/, <see cref="AddClip"/> then puts it
/// on a track of the project. The original is never touched and the copy is never rewritten, like any media;
/// it keeps its own extension (the loaders pick the decoder by it) and gets a unique name in media/. A format only
/// FFmpeg reads goes in as a WAV of its sound instead: the project must open on a PC without FFmpeg. A video also
/// brings its picture when asked (<see cref="CopyVideoIn"/>), next to that WAV, as a take does.
/// Linking (<see cref="LinkIn(string, string, AudioLoader.LoadedAudio)"/>, the user's choice) leaves the file where it
/// is and keeps its absolute path, so nothing is duplicated; if it moves, its clips go offline until <see cref="LinkedMedia"/>
/// finds it. The WAV rule still wins.
/// The track is audio only (no video panel) with a neutral strip: no compressor or saturation, because
/// an imported mix is already finished.
/// </summary>
public static class AudioImport
{
    /// <summary>How much is copied between progress reports.</summary>
    const int CopyChunk = 1 << 20;
    /// <summary>Ends the name of a file still being copied into media/; only a finished one gets its real name.</summary>
    public const string PartialCopy = ".importando";

    public static bool IsSupported(string path) => MediaDecoder.IsMedia(path);

    /// <summary>
    /// A file already in media/, or linked (then <paramref name="RelativePath"/> is absolute), ready for <see cref="AddClip"/>;
    /// <paramref name="Video"/> = its picture, when it brought it; <paramref name="Bytes"/> and <paramref name="VideoBytes"/> = the sizes of what stayed outside.
    /// </summary>
    public sealed record Imported(string Name, string RelativePath, double DurationSec, int SampleRate, int Channels, string? Video = null, long Bytes = 0, long VideoBytes = 0);

    /// <summary>
    /// Decodes <paramref name="source"/>, at <paramref name="sampleRate"/> (0 = its own). Throws
    /// <see cref="InvalidDataException"/> with a message for the user when the file can't be read as audio. The length
    /// comes from decoding it, not from the header: some formats report a wrong one there. The window keeps what this
    /// returns as the clip's audio, so a file is decoded once and not again after <see cref="CopyIn(string, string, AudioLoader.LoadedAudio, Action{double}?)"/>.
    /// </summary>
    public static AudioLoader.LoadedAudio Read(string source, int sampleRate = 0, Action<double>? progress = null)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("No encuentro ese archivo.");
        if (!IsSupported(source))
            throw new InvalidDataException(Texts.T("err.import.format", "Formato no compatible: \"{0}\".", Path.GetFileName(source)));
        AudioLoader.LoadedAudio audio;
        try { audio = MediaDecoder.Load(source, sampleRate, progress); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidDataException($"\"{Path.GetFileName(source)}\" no se pudo leer como audio ({e.Message.TrimEnd('.')}).");
        }
        if (audio.Left.Length == 0) throw new InvalidDataException($"\"{Path.GetFileName(source)}\" no tiene audio.");
        return audio;
    }

    /// <summary>The name a file gets in media/, without its extension.</summary>
    internal static string CleanStem(string source)
    {
        string stem = string.Concat(Path.GetFileNameWithoutExtension(source).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (stem.Length > 100) stem = stem[..100].TrimEnd();
        return stem.Length == 0 ? "Audio" : stem;
    }

    /// <summary>Checks the file and copies it in; see the overload that takes it already read.</summary>
    public static Imported CopyIn(string projectDir, string source) => CopyIn(projectDir, source, Read(source));

    /// <summary>
    /// Copies <paramref name="source"/>, already checked by <see cref="Read"/>, into the project's media/ folder
    /// (through a temporary name, so a cut never leaves half a file that looks like media).
    /// </summary>
    public static Imported CopyIn(string projectDir, string source, AudioLoader.LoadedAudio audio, Action<double>? progress = null)
    {
        if (audio.Converted) return WriteIn(projectDir, Path.GetFileNameWithoutExtension(source).Trim(), audio.Left, audio.Right, audio.SampleRate);
        string mediaDir = Path.Combine(projectDir, ProjectStore.MediaDir);
        Directory.CreateDirectory(mediaDir);
        string ext = Path.GetExtension(source).ToLowerInvariant();
        string dest = UniquePath(mediaDir, CleanStem(source), ext);
        string tmp = dest + PartialCopy;
        try
        {
            CopyFile(source, tmp, progress);
            File.Move(tmp, dest);
            return new Imported(Path.GetFileNameWithoutExtension(source).Trim(), $"{ProjectStore.MediaDir}/{Path.GetFileName(dest)}", audio.DurationSec, audio.SourceSampleRate, audio.SourceChannels);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Copies a video's picture into media/ next to its sound, under the same name (how a take's two files go together),
    /// and returns <paramref name="file"/> with it. The picture is the whole file: nothing is re-encoded.
    /// </summary>
    public static Imported CopyVideoIn(string projectDir, string source, Imported file, Action<double>? progress = null)
    {
        string stem = Path.GetFileNameWithoutExtension(file.RelativePath);
        string mediaDir = Path.Combine(projectDir, ProjectStore.MediaDir);
        string dest = UniquePath(mediaDir, stem, Path.GetExtension(source).ToLowerInvariant());
        string tmp = dest + PartialCopy;
        try
        {
            CopyFile(source, tmp, progress);
            File.Move(tmp, dest);
            return file with { Video = $"{ProjectStore.MediaDir}/{Path.GetFileName(dest)}" };
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>Checks the file and links it; see the overload that takes it already read.</summary>
    public static Imported LinkIn(string projectDir, string source) => LinkIn(projectDir, source, Read(source));

    /// <summary>
    /// Leaves <paramref name="source"/>, already checked by <see cref="Read"/>, where it is. What only FFmpeg reads can't
    /// stay outside (a project opens without FFmpeg): its sound goes into media/ as a WAV, as when copying.
    /// </summary>
    public static Imported LinkIn(string projectDir, string source, AudioLoader.LoadedAudio audio) => audio.Converted
        ? WriteIn(projectDir, Path.GetFileNameWithoutExtension(source).Trim(), audio.Left, audio.Right, audio.SampleRate)
        : new Imported(Path.GetFileNameWithoutExtension(source).Trim(), Path.GetFullPath(source), audio.DurationSec, audio.SourceSampleRate, audio.SourceChannels, Bytes: new FileInfo(source).Length);

    /// <summary>Returns <paramref name="file"/> with the video's picture left where it is; its sound can be a WAV in media/.</summary>
    public static Imported LinkVideoIn(string source, Imported file) => file with { Video = Path.GetFullPath(source), VideoBytes = new FileInfo(source).Length };

    /// <summary>Copies by hand to tell how far it is, and forces it to disk before it gets its final name.</summary>
    internal static void CopyFile(string from, string to, Action<double>? progress)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        var buf = new byte[CopyChunk];
        long total = Math.Max(1, input.Length), done = 0;
        for (int n; (n = input.Read(buf, 0, buf.Length)) > 0;)
        {
            output.Write(buf, 0, n);
            done += n;
            progress?.Invoke(done / (double)total);
        }
        output.Flush(true);
    }

    /// <summary>
    /// Writes audio made here (the stems of a clip) as a new 32-bit float WAV in media/, through a temporary name like
    /// <see cref="CopyIn"/>, so a cut never leaves half a file that looks like media.
    /// </summary>
    public static Imported WriteIn(string projectDir, string name, float[] left, float[] right, int sampleRate)
    {
        string mediaDir = Path.Combine(projectDir, ProjectStore.MediaDir);
        Directory.CreateDirectory(mediaDir);
        string stem = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        string dest = UniquePath(mediaDir, stem.Length == 0 ? "Audio" : stem, ".wav");
        string tmp = dest + PartialCopy;
        try
        {
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new WaveFileWriter(new IgnoreDisposeStream(file), WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
            {
                var buf = new float[8192];
                for (int i = 0; i < left.Length;)
                {
                    int n = Math.Min(buf.Length / 2, left.Length - i);
                    for (int j = 0; j < n; j++)
                    {
                        buf[2 * j] = left[i + j];
                        buf[2 * j + 1] = right[i + j];
                    }
                    w.WriteSamples(buf, 0, n * 2);
                    i += n;
                }
                w.Flush();
                file.Flush(true);
            }
            File.Move(tmp, dest);
            return new Imported(name, $"{ProjectStore.MediaDir}/{Path.GetFileName(dest)}", left.Length / (double)sampleRate, sampleRate, 2);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The stems of a clip: a track each, in a new folder right after the clip's track, with a clip at
    /// <paramref name="atSec"/> as loud as the original. The clip's track is muted, since together they would sound
    /// twice. Returns the folder's id.
    /// </summary>
    public static string AddStems(Project p, string trackId, string clipId, double atSec, IReadOnlyList<Imported> files, string folderName)
    {
        var source = p.Tracks.Find(t => t.Id == trackId);
        double gain = source?.Clips.Find(c => c.Id == clipId)?.GainDb ?? 0;
        int at = source == null ? p.Tracks.Count : p.Tracks.IndexOf(source) + 1;
        var ids = new List<string>();
        foreach (var file in files)
        {
            var media = new Media
            {
                Name = file.Name,
                Kind = "import",
                Audio = file.RelativePath,
                DurationSec = file.DurationSec,
                SampleRate = file.SampleRate,
                Channels = file.Channels,
                SyncMs = 0,
            };
            p.Media.Add(media);
            var track = new Track { Name = file.Name, Video = false };
            track.Clips.Add(new Clip { MediaId = media.Id, AtSec = Math.Max(0, Math.Round(atSec, 3)), GainDb = gain });
            p.Tracks.Insert(at + ids.Count, track);
            ids.Add(track.Id);
        }
        if (source != null) source.Channel.Muted = true;
        string folder = TrackFolders.Add(p, folderName, ids);
        p.AddLog("stems-separated", folderName);
        return folder;
    }

    internal static string UniquePath(string dir, string stem, string ext)
    {
        string path = Path.Combine(dir, stem + ext);
        for (int n = 2; File.Exists(path) || File.Exists(path + PartialCopy); n++) path = Path.Combine(dir, $"{stem} ({n}){ext}");
        return path;
    }

    /// <summary>
    /// Adds the media and a clip of it at <paramref name="atSec"/> on a new track at the end (with its video panel only
    /// when it brought a picture); with <paramref name="trackId"/> of an empty track it takes that one instead (like a
    /// take does). With <paramref name="fit"/> a track that already has clips also takes it, when it isn't locked and
    /// that stretch is free: dropping on a track never covers what is there. Returns the track.
    /// </summary>
    public static Track AddClip(Project p, Imported file, double atSec, string? trackId = null, bool fit = false)
    {
        var media = new Media
        {
            Name = file.Name,
            Kind = "import",
            Audio = file.RelativePath,
            DurationSec = file.DurationSec,
            SampleRate = file.SampleRate,
            Channels = file.Channels,
            SyncMs = 0,
            Video = file.Video,
            AudioBytes = file.Bytes,
            VideoBytes = file.VideoBytes,
            VideoOffsetMs = 0, // that default is a camera's latency, and this was not filmed here
        };
        p.Media.Add(media);
        double at = Math.Max(0, Math.Round(atSec, 3));
        var track = p.Tracks.Find(t => t.Id == trackId && !t.IsMidi && (t.Clips.Count == 0 || (fit && ClipEditing.IsFree(p, t, at, at + file.DurationSec))));
        if (track == null)
        {
            track = new Track { Video = false };
            p.Tracks.Add(track);
        }
        if (track.Clips.Count == 0)
        {
            track.Name = file.Name;
            track.Video = file.Video != null;
        }
        track.Clips.Add(new Clip { MediaId = media.Id, AtSec = at });
        p.AddLog("audio-imported", file.Name);
        return track;
    }
}
