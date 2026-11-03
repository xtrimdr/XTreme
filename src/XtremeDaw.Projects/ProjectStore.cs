using XtremeDaw.Audio;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtremeDaw.Projects;

/// <summary>
/// An open project folder:
/// <code>
///   Name.xtp                 the project (JSON inside), named like the project so it reads well in Explorer
///   Name.xtp.bak             the previous save
///   xtreme_project.lock      held while it's open (a second window opens it read-only)
///   historial/               snapshots, to go back further than the .bak (versiones.json: their names)
///   media/                   recordings and imported files (never rewritten)
///   exports/
/// </code>
/// Every save writes a temporary file, forces it to disk and swaps it in with File.Replace, so the
/// project file is always either the old save or the new one, never half of each.
/// </summary>
public sealed class ProjectStore : IDisposable
{
    /// <summary>The project file's extension ("XTreme project"); inside it is JSON.</summary>
    public const string Extension = ".xtp";
    /// <summary>The project file before .xtp: still read, and renamed to Name.xtp the first time it opens.</summary>
    public const string OldFileName = "xtreme_project.json";
    /// <summary>The project file of WildTrack and GuitarCam Studio Native: opened and turned into Name.xtp the same way.</summary>
    public const string WildTrackFileName = "guitarcam.json";
    static readonly string[] OldFileNames = [OldFileName, WildTrackFileName];
    public const string MediaDir = "media";
    public const string HistoryDir = "historial";
    /// <summary>What can be made again from the media (transposed takes); deleting it only costs time.</summary>
    public const string CacheDir = "cache";
    public const string LockName = "xtreme_project.lock";
    const string SnapshotStamp = "yyyy-MM-dd_HH-mm-ss";
    /// <summary>In historial/: what the user named each version and which export it was (<see cref="VersionInfo"/>).</summary>
    const string VersionsFile = "versiones.json";
    /// <summary>Longest name of a version.</summary>
    public const int MaxVersionName = 60;

    /// <summary>A new snapshot at most this often while saving.</summary>
    public static TimeSpan SnapshotEvery { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>The newest ones always stay; older ones keep one per day. Named and exported ones always stay.</summary>
    public const int KeepRecentSnapshots = 30;
    public const int KeepDailySnapshotsDays = 90;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly object writeLock = new();
    FileStream? lockFile;
    DateTime lastSnapshot = DateTime.MinValue;
    string? lastSnapshotText;

    public string Dir { get; }
    public Project Project { get; private set; }
    /// <summary>Set when the project can't be saved from here; the reason is shown to the user.</summary>
    public string? ReadOnlyReason { get; private set; }
    public bool ReadOnly => ReadOnlyReason != null;
    /// <summary>What happened while opening (recovered from a backup, missing files, recovered takes…).</summary>
    public List<string> Notices { get; } = [];

    /// <summary>Which takes were in the timeline at the last save, to log the ones that left or came back.</summary>
    Dictionary<string, bool> timeline;

    /// <summary>The project file; it follows the project's name (<see cref="MatchFileToName"/>).</summary>
    public string FilePath { get; private set; }
    string BakPath => FilePath + ".bak";
    string TmpPath => FilePath + ".tmp";

    ProjectStore(string dir, Project project, string file)
    {
        Dir = dir;
        Project = project;
        FilePath = file;
        timeline = ProjectHistory.TimelineState(project);
    }

    public static bool IsProject(string dir) => FindFile(dir) != null;

    /// <summary>A folder that lost its project file but still has the .bak or a snapshot: <see cref="Open"/> rescues it.</summary>
    public static bool IsRecoverable(string dir) => Baks(dir).Any() || Snapshots(dir).Count > 0;

    /// <summary>
    /// The project file of a folder: its .xtp (with several, the one named like the folder, else the newest), or the
    /// file of an older version (xtreme_project.json, WildTrack's guitarcam.json). Null when it has none.
    /// </summary>
    public static string? FindFile(string dir)
    {
        var xtp = Files(dir, "*" + Extension);
        if (xtp.Count > 0)
        {
            string own = FolderName(dir) + Extension;
            return xtp.Find(f => string.Equals(Path.GetFileName(f), own, StringComparison.OrdinalIgnoreCase))
                ?? xtp.MaxBy(File.GetLastWriteTimeUtc);
        }
        return OldFileNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
    }

    /// <summary>The folder a path means: a project file's folder, or the path itself.</summary>
    public static string DirOf(string path)
    {
        path = Path.GetFullPath(path);
        return IsProjectFile(path) && File.Exists(path) ? Path.GetDirectoryName(path)! : path.TrimEnd('\\', '/');
    }

    public static bool IsProjectFile(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || OldFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The project file a project named <paramref name="name"/> gets in <paramref name="dir"/>.</summary>
    public static string FileFor(string dir, string name) => Path.Combine(dir, ProjectLibrary.FolderNameFor(name) + Extension);

    static string FolderName(string dir) => Path.GetFileName(Path.GetFullPath(dir).TrimEnd('\\', '/'));

    /// <summary>A folder's files by pattern, checked by their real ending (Windows matches "*.xtp" on "a.xtpx" too).</summary>
    static List<string> Files(string dir, string pattern)
    {
        try
        {
            string end = pattern[1..];
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Where(f => f.EndsWith(end, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static IEnumerable<string> Baks(string dir) =>
        Files(dir, "*" + Extension + ".bak").Concat(OldFileNames.SelectMany(n => Files(dir, n + ".bak")));

    /// <summary>Creates the folder (it must not hold a project yet) and writes the first save.</summary>
    public static ProjectStore Create(string dir, Project project)
    {
        if (IsProject(dir)) throw new IOException("Esa carpeta ya tiene un proyecto");
        Directory.CreateDirectory(Path.Combine(dir, MediaDir));
        project.Normalize();
        var store = new ProjectStore(dir, project, FileFor(dir, project.Name));
        store.TakeLock();
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        store.Save();
        return store;
    }

    public static ProjectStore Open(string dir)
    {
        var (project, file, from, notices) = LoadWithFallback(dir);
        var store = new ProjectStore(dir, project, file);
        store.Notices.AddRange(notices);
        store.TakeLock();
        if (project.Format != Project.FormatId)
            store.ReadOnlyReason = Texts.T("store.notProject", "El archivo no es un proyecto de XTreme");
        else if (project.Version > Project.CurrentVersion)
            store.ReadOnlyReason = Texts.T("store.newer", "Guardado con una versión más nueva de XTreme: abierto en solo lectura");
        if (!store.ReadOnly)
        {
            Directory.CreateDirectory(Path.Combine(dir, MediaDir));
            Migrate(project);
            // an xtreme_project.json, a WildTrack guitarcam.json or a file still named after an old name becomes Name.xtp
            store.MatchFileToName();
            store.SweepPartialCopies();
            var recovered = store.RecoverRecordings();
            recovered.AddRange(store.RecoverMidiTakes());
            int adopted = store.AdoptStrayMedia();
            // a project rescued from the .bak or a snapshot goes back to being the main file right away; a converted one
            // is written as XTreme's at once (an old format id inside a .xtp would open, but shouldn't stay)
            if (from != file || store.FilePath != file || recovered.Count > 0 || adopted > 0) store.Save();
            foreach (var j in recovered) File.Delete(j); // only once the project has the takes
        }
        store.CheckMedia();
        return store;
    }

    /// <summary>Older versions of the format get upgraded here, one step at a time.</summary>
    static void Migrate(Project p)
    {
        // 1 -> 2: the channels' sat/comp/eq became insert slots; Channel.Normalize already turned them (so a project
        // opened read-only sounds right too), here it only gets the new number.
        // 2 -> 3: the fixed reverb and delay and the extra returns became buses, and the folders lost their fader, mute,
        // solo and slots; the normalization already did that too (ProjectSettings, Channel, Folder).
        p.Version = Project.CurrentVersion;
    }

    /// <summary>The project, the file it is saved in, the file it was read from and what to tell the user.</summary>
    static (Project Project, string File, string From, List<string> Notices) LoadWithFallback(string dir)
    {
        var notices = new List<string>();
        // without a main file, the newest .bak says what it was called
        string main = FindFile(dir)
            ?? Baks(dir).OrderByDescending(File.GetLastWriteTimeUtc).Select(b => b[..^".bak".Length]).FirstOrDefault()
            ?? Path.Combine(dir, FolderName(dir) + Extension);
        Exception? first = null;
        Project? TryParse(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
            {
                first ??= e;
                return null;
            }
        }
        if (TryParse(main) is { } ok) return (ok, main, main, notices);

        // the newest readable copy wins: the last snapshot can be newer than the .bak
        var best = new[] { main + ".bak" }
            .Concat(Snapshots(dir).Take(10).Select(s => s.Path))
            .Select(path => (Path: path, Project: TryParse(path)))
            .Where(c => c.Project != null)
            .MaxBy(c => c.Project!.UpdatedAt);
        if (best.Project == null) throw first ?? new FileNotFoundException(Texts.T("store.noProject", "La carpeta no tiene un proyecto de XTreme"), main);
        string what = best.Path.EndsWith(".bak")
            ? Texts.T("store.fromBak", "el guardado anterior (.bak)")
            : Texts.T("store.fromHistory", "la copia del historial {0}", Path.GetFileNameWithoutExtension(best.Path));
        notices.Add(Texts.T("store.damaged", "El proyecto estaba dañado: se abrió desde {0}", what));
        if (File.Exists(main))
        {
            // .json, not .xtp: it must never be taken for the project
            string keep = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(main)}.dañado-{DateTime.Now.ToString(SnapshotStamp, CultureInfo.InvariantCulture)}.json");
            File.Move(main, keep);
            notices.Add(Texts.T("store.damagedKept", "El archivo dañado quedó como {0}", Path.GetFileName(keep)));
        }
        return (best.Project, main, best.Path, notices);
    }

    public static Project Parse(string json)
    {
        var p = JsonSerializer.Deserialize<Project>(json, Json) ?? throw new InvalidDataException(Texts.T("store.empty", "El proyecto está vacío"));
        p.Normalize();
        return p;
    }

    /// <summary>Logs the takes that left or came back to the timeline since the last call (every save does it).</summary>
    public void LogTimelineChanges() => ProjectHistory.LogTimelineChanges(Project, timeline);

    public string Serialize()
    {
        LogTimelineChanges();
        Project.UpdatedAt = DateTime.UtcNow;
        return JsonSerializer.Serialize(Project, Json);
    }

    /// <summary>Serializes and writes now. Call it on the thread that owns the project.</summary>
    public void Save() => Write(Serialize());

    /// <summary>
    /// Writes an already serialized project (safe from any thread; writes run one at a time).
    /// tmp → flush to disk → replace (the old file becomes the .bak) → maybe a snapshot.
    /// </summary>
    public void Write(string json)
    {
        if (ReadOnly) return;
        lock (writeLock)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var fs = new FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            Retry(() =>
            {
                if (File.Exists(FilePath)) File.Replace(TmpPath, FilePath, BakPath, true);
                else File.Move(TmpPath, FilePath);
            });
            if (DateTime.UtcNow - lastSnapshot < SnapshotEvery) return;
            try
            {
                WriteSnapshot(json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the save itself went through; the next one tries the snapshot again
                lastSnapshot = DateTime.MinValue;
            }
        }
    }

    /// <summary>Antivirus and indexers sometimes hold the file for a moment.</summary>
    static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                a();
                return;
            }
            catch (IOException) when (i < 5)
            {
                Thread.Sleep(40 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 5)
            {
                Thread.Sleep(40 * (i + 1));
            }
        }
    }

    /// <param name="force">A version the user (or an export) asked for: written even if nothing changed since the last one.</param>
    Snapshot? WriteSnapshot(string json, bool force = false, VersionInfo? info = null)
    {
        lastSnapshot = DateTime.UtcNow;
        if (lastSnapshotText == null && Snapshots(Dir) is [var newest, ..]) lastSnapshotText = TryRead(newest.Path);
        if (json == lastSnapshotText && !force) return null;
        string dir = Path.Combine(Dir, HistoryDir);
        Directory.CreateDirectory(dir);
        string name = DateTime.Now.ToString(SnapshotStamp, CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, name + ".json");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name}_{i}.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, path);
        lastSnapshotText = json;
        if (info != null)
        {
            var all = ReadVersions(Dir);
            all[Path.GetFileName(path)] = info;
            WriteVersions(all);
        }
        PruneSnapshots();
        var stamp = DateTime.ParseExact(name, SnapshotStamp, CultureInfo.InvariantCulture);
        return new Snapshot(path, stamp, info?.Name, info?.Export, info?.Before);
    }

    static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return null;
        }
    }

    void PruneSnapshots()
    {
        var all = Snapshots(Dir);
        var keep = new HashSet<string>(all.Take(KeepRecentSnapshots).Select(s => s.Path));
        foreach (var s in all.Where(s => s.Kept)) keep.Add(s.Path);
        var cutoff = DateTime.Now.AddDays(-KeepDailySnapshotsDays);
        foreach (var day in all.Where(s => s.Time >= cutoff).GroupBy(s => s.Time.Date))
            keep.Add(day.First().Path); // newest of each day
        foreach (var s in all.Where(s => !keep.Contains(s.Path)))
        {
            try
            {
                File.Delete(s.Path);
            }
            catch (IOException)
            {
            }
        }
        var versions = ReadVersions(Dir);
        var files = keep.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = versions.Keys.Where(k => !files.Contains(k)).ToList();
        if (gone.Count == 0) return;
        foreach (var k in gone) versions.Remove(k);
        WriteVersions(versions);
    }

    /// <summary>
    /// A saved version: when, and what the user called it (<see cref="Name"/>), the export it was made with
    /// (<see cref="Export"/>, as in <see cref="ExportRecord.File"/>) or the version that going back left it for
    /// (<see cref="Before"/>, that one's file name). Named and exported ones are never pruned.
    /// </summary>
    public readonly record struct Snapshot(string Path, DateTime Time, string? Name = null, string? Export = null, string? Before = null)
    {
        public bool Kept => Name != null || Export != null;
        public string File => System.IO.Path.GetFileName(Path);
    }

    /// <summary>What versiones.json says of one version. Losing the file only loses the names: the versions stay.</summary>
    public sealed class VersionInfo
    {
        public string? Name { get; set; }
        public string? Export { get; set; }
        public string? Before { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    static Dictionary<string, VersionInfo> ReadVersions(string dir)
    {
        string path = Path.Combine(dir, HistoryDir, VersionsFile);
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var read = JsonSerializer.Deserialize<Dictionary<string, VersionInfo>>(File.ReadAllText(path, Encoding.UTF8), Json) ?? [];
            return read.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>tmp → move over, like the project: the names are either the old ones or the new ones.</summary>
    void WriteVersions(Dictionary<string, VersionInfo> all)
    {
        string dir = Path.Combine(Dir, HistoryDir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, VersionsFile), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json), Encoding.UTF8);
        Retry(() => File.Move(tmp, path, true));
    }

    /// <summary>
    /// Saves the project and a version of it now, with a name (null = none) or as the version of an export: those are
    /// kept until the user takes the name off or deletes the export. Safe from any thread, like <see cref="Write"/>.
    /// </summary>
    public Snapshot SaveVersion(string json, string? name, string? export = null)
    {
        if (ReadOnly) throw new InvalidOperationException(ReadOnlyReason);
        lock (writeLock)
        {
            lastSnapshot = DateTime.UtcNow; // this save makes its own version, not an automatic one too
            Write(json);
            return WriteSnapshot(json, force: true, new VersionInfo { Name = CleanName(name), Export = export })!.Value;
        }
    }

    /// <summary>Names a version or, with null or blank, takes its name off (an unnamed one can be pruned again).</summary>
    public void RenameVersion(Snapshot s, string? name)
    {
        if (ReadOnly) throw new InvalidOperationException(ReadOnlyReason);
        lock (writeLock)
        {
            var all = ReadVersions(Dir);
            var info = all.GetValueOrDefault(s.File) ?? new VersionInfo();
            info.Name = CleanName(name);
            if (info is { Name: null, Export: null, Before: null, Extra: null }) all.Remove(s.File);
            else all[s.File] = info;
            WriteVersions(all);
        }
    }

    /// <summary>The export was deleted: its version stops being kept (it stays while it is among the recent ones).</summary>
    public void UnlinkExport(string exportFile)
    {
        if (ReadOnly) return;
        lock (writeLock)
        {
            var all = ReadVersions(Dir);
            var linked = all.Values.Where(v => v.Export == exportFile).ToList();
            if (linked.Count == 0) return;
            foreach (var info in linked) info.Export = null;
            WriteVersions(all);
        }
    }

    static string? CleanName(string? name)
    {
        string n = name?.Trim() ?? "";
        return n.Length == 0 ? null : n.Length > MaxVersionName ? n[..MaxVersionName] : n;
    }

    /// <summary>The project's snapshots, newest first, with their names.</summary>
    public static List<Snapshot> Snapshots(string dir)
    {
        string h = Path.Combine(dir, HistoryDir);
        if (!Directory.Exists(h)) return [];
        var versions = ReadVersions(dir);
        var list = new List<Snapshot>();
        foreach (var f in Directory.GetFiles(h, "*.json"))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (n.Length >= SnapshotStamp.Length
                && DateTime.TryParseExact(n[..SnapshotStamp.Length], SnapshotStamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                var info = versions.GetValueOrDefault(Path.GetFileName(f));
                list.Add(new Snapshot(f, t, info?.Name, info?.Export, info?.Before));
            }
        }
        return list.OrderByDescending(s => s.Time).ThenByDescending(s => s.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Goes back to a snapshot. The current state is saved as a snapshot first, so this can be undone
    /// from the same list. Only the song goes back: the takes recorded since, the exports, the log and the name stay
    /// (a take only the old version knew comes back too, offline if its file went to the Recycle Bin).
    /// </summary>
    public void Restore(Snapshot s)
    {
        if (ReadOnly) throw new InvalidOperationException(ReadOnlyReason);
        var old = Parse(File.ReadAllText(s.Path, Encoding.UTF8));
        lock (writeLock) WriteSnapshot(Serialize(), force: true, new VersionInfo { Before = s.File });
        Migrate(old);
        var now = Project;
        var known = now.Media.Select(m => m.Id).ToHashSet();
        old.Media = [.. now.Media, .. old.Media.Where(m => !known.Contains(m.Id))];
        old.Exports = now.Exports;
        old.Log = now.Log;
        (old.Id, old.Name, old.CreatedAt) = (now.Id, now.Name, now.CreatedAt);
        Project = old;
        timeline = ProjectHistory.TimelineState(old);
        Project.AddLog("snapshot-restored", Path.GetFileNameWithoutExtension(s.Path));
        Save(); // no automatic version of it: it is the one just brought back
        Notices.Clear();
        CheckMedia();
    }

    /// <summary>
    /// Renames the project file (and its .bak) to Name.xtp after the project's name, so a renamed project, a copy or an
    /// old xtreme_project.json reads right in Explorer. Keeps the old name when the new one is taken or the file is held.
    /// </summary>
    public void MatchFileToName()
    {
        if (ReadOnly) return;
        lock (writeLock)
        {
            string target = FileFor(Dir, Project.Name);
            if (target == FilePath) return;
            // only the case changed: the same file on Windows
            bool same = string.Equals(target, FilePath, StringComparison.OrdinalIgnoreCase);
            if (!same && File.Exists(target)) return;
            try
            {
                string oldBak = BakPath;
                if (File.Exists(FilePath)) Retry(() => File.Move(FilePath, target));
                FilePath = target;
                if (!same && File.Exists(oldBak)) Retry(() => File.Move(oldBak, BakPath, true));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // held by something else: it keeps working under its old name
            }
        }
    }

    void TakeLock()
    {
        try
        {
            lockFile = new FileStream(Path.Combine(Dir, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var who = Encoding.UTF8.GetBytes($"{Environment.MachineName} {Environment.ProcessId} {DateTime.Now:s}");
            lockFile.SetLength(0);
            lockFile.Write(who);
            lockFile.Flush();
        }
        catch (IOException)
        {
            ReadOnlyReason = Texts.T("store.locked", "Abierto en otra ventana de XTreme: solo lectura");
        }
    }

    /// <summary>Absolute path of a media file (relative paths are inside the project).</summary>
    public string CachePath => Path.Combine(Dir, CacheDir);

    /// <summary>
    /// A take's audio as it plays on <paramref name="track"/>: moved by <see cref="Clip.SemitonesOn"/>, from the
    /// cache or computed now (seconds per minute of audio: call it off the UI thread), or the audio itself at 0.
    /// A reversed clip gets its own copy, backward.
    /// </summary>
    public (float[] Left, float[] Right) TrackAudio(Track track, Clip clip, string audioPath, float[] left, float[] right,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        var (l, r) = TransposeCache.Get(CachePath, audioPath, left, right, clip.SemitonesOn(track), clip.Stretch, progress, ct);
        if (!clip.Reversed) return (l, r);
        (l, r) = ((float[])l.Clone(), (float[])r.Clone());
        Array.Reverse(l);
        Array.Reverse(r);
        return (l, r);
    }

    public string? Resolve(string? rel) => string.IsNullOrEmpty(rel) ? null : Path.IsPathRooted(rel) ? rel : Path.Combine(Dir, rel);

    /// <summary>Clips whose media file is gone stay in the project, marked as offline by the app.</summary>
    void CheckMedia()
    {
        var used = ProjectHistory.UsedMediaIds(Project);
        var missing = Project.Media
            .Where(m => used.Contains(m.Id) && Resolve(m.Audio) is { } a && !File.Exists(a))
            .Select(m => m.Name)
            .ToList();
        if (missing.Count > 0) Notices.Add(Texts.T("store.missing", "Faltan archivos de audio: {0} (quedan sin conexión)", string.Join(", ", missing)));
        var orphan = Project.Tracks.SelectMany(t => t.Clips).Count(c => Project.FindMedia(c.MediaId) == null);
        if (orphan > 0) Notices.Add(Texts.T("store.orphan", "{0} clip(s) apuntan a medios que no están en el proyecto", orphan));
    }

    // --- recordings in progress ------------------------------------------------------------------

    /// <summary>
    /// Written next to a WAV before recording starts and deleted once the project has the take.
    /// If one is still there when the project opens, the program died mid-take.
    /// </summary>
    public sealed class RecordingJournal
    {
        public string MediaId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Audio { get; set; } = "";
        public string? Video { get; set; }
        public string? TrackId { get; set; }
        public double AtSec { get; set; }
        /// <summary>Where the playhead was when the take started: its first sample lands there (plus <see cref="SyncMs"/>).</summary>
        public double StartSec { get; set; }
        public double SyncMs { get; set; }
        public double VideoOffsetMs { get; set; } = 80;
        /// <summary>"Espejar imagen en vivo" when the take started: the clip shows mirrored, like the monitor.</summary>
        public bool Mirror { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    static string JournalPath(string dir, string mediaId) => Path.Combine(dir, MediaDir, mediaId + ".grabando.json");

    public void BeginRecording(RecordingJournal j)
    {
        string path = JournalPath(Dir, j.MediaId);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(j, Json), Encoding.UTF8);
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>Call after the project with the new take is saved.</summary>
    public void EndRecording(string mediaId) => File.Delete(JournalPath(Dir, mediaId));

    /// <summary>
    /// Turns every unfinished recording into a media + clip, so a crash never costs a take.
    /// Returns the journals to delete after the project is saved.
    /// </summary>
    List<string> RecoverRecordings()
    {
        var done = new List<string>();
        string media = Path.Combine(Dir, MediaDir);
        foreach (var jf in Directory.GetFiles(media, "*.grabando.json"))
        {
            RecordingJournal? j;
            try
            {
                j = JsonSerializer.Deserialize<RecordingJournal>(File.ReadAllText(jf, Encoding.UTF8), Json);
            }
            catch (JsonException)
            {
                j = null;
            }
            if (j == null || Project.FindMedia(j.MediaId) != null)
            {
                File.Delete(jf); // already in the project (it died after saving) or unreadable
                continue;
            }
            string? wav = Resolve(j.Audio);
            if (wav == null || !File.Exists(wav))
            {
                File.Delete(jf);
                continue;
            }
            Audio.WavFile.Info info;
            try
            {
                info = Audio.WavFile.Repair(wav);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Notices.Add(Texts.T("store.recoverFail", "No se pudo recuperar la grabación {0}: {1}", j.Name, e.Message));
                continue;
            }
            if (info.DurationSec < 0.05)
            {
                File.Delete(jf);
                continue;
            }
            string? video = Resolve(j.Video) is { } v && File.Exists(v) && new FileInfo(v).Length > 0 ? j.Video : null;
            // the camera's fragmented MP4 plays as it is; with the index up front it opens and seeks faster
            if (video != null) Audio.VideoTakeWriter.Faststart(Resolve(video)!);
            var m = new Media
            {
                Id = j.MediaId,
                Name = j.Name,
                Kind = "recording",
                Audio = j.Audio,
                Video = video,
                DurationSec = info.DurationSec,
                SampleRate = info.SampleRate,
                Channels = info.Channels,
                CreatedAt = j.StartedAt,
                SyncMs = j.SyncMs,
                VideoOffsetMs = j.VideoOffsetMs,
            };
            Project.Media.Add(m);
            var track = Project.Tracks.Find(t => t.Id == j.TrackId);
            if (track == null)
            {
                track = new Track { Name = j.Name, Video = video != null };
                Project.Tracks.Add(track);
            }
            track.Clips.Add(new Clip { MediaId = m.Id, AtSec = j.AtSec, Framing = new Framing { Mirror = j.Mirror && video != null } });
            Project.AddLog("recording-recovered", j.Name);
            Notices.Add(Texts.T("store.recovered", "Grabación interrumpida recuperada: \"{0}\" ({1:0.0} s)", j.Name, info.DurationSec));
            done.Add(jf);
        }
        return done;
    }

    /// <summary>
    /// The MIDI counterpart of <see cref="RecordingJournal"/>, written before the take starts and deleted once the project
    /// with its clip is saved. The notes go next to it (<see cref="MidiNotesPath"/>, <see cref="Audio.Midi.MidiTakeWriter"/>).
    /// The clip's id is decided here: finding it in the project means the take got there.
    /// </summary>
    public sealed class MidiTakeJournal
    {
        public string ClipId { get; set; } = "";
        public string? TrackId { get; set; }
        /// <summary>For the track it comes back on if the saved project doesn't have it, and the notice.</summary>
        public string TrackName { get; set; } = "";
        public double StartSec { get; set; }
        /// <summary>"Cuantizar al grabar" when the take started; 0 = as played.</summary>
        public double GridBeats { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    const string MidiJournalSuffix = ".grabando-midi.json";

    string MidiJournalPath(string clipId) => Path.Combine(Dir, MediaDir, clipId + MidiJournalSuffix);

    /// <summary>Where a MIDI take keeps its notes while it is recorded.</summary>
    public string MidiNotesPath(string clipId) => Path.Combine(Dir, MediaDir, clipId + ".notas");

    public void BeginMidiTake(MidiTakeJournal j)
    {
        string path = MidiJournalPath(j.ClipId);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(j, Json), Encoding.UTF8);
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>Call after the project with the clip is saved, or when the take is discarded.</summary>
    public void EndMidiTake(string clipId)
    {
        File.Delete(MidiNotesPath(clipId));
        File.Delete(MidiJournalPath(clipId));
    }

    /// <summary>
    /// Turns every unfinished MIDI take into a clip on its track, the way stopping would have: the same notes, bars and
    /// quantize. A take that only reached the count-in is gone, like when it's cancelled. Returns the files to delete
    /// after the project is saved.
    /// </summary>
    List<string> RecoverMidiTakes()
    {
        var done = new List<string>();
        foreach (var jf in Directory.GetFiles(Path.Combine(Dir, MediaDir), "*" + MidiJournalSuffix))
        {
            string id = Path.GetFileName(jf)[..^MidiJournalSuffix.Length];
            MidiTakeJournal? j;
            try
            {
                j = JsonSerializer.Deserialize<MidiTakeJournal>(File.ReadAllText(jf, Encoding.UTF8), Json);
            }
            catch (JsonException e)
            {
                j = null;
                if (File.Exists(MidiNotesPath(id)))
                {
                    // Without the journal there's no track or start, but the notes stay on disk: a take is never deleted.
                    Notices.Add(Texts.T("store.recoverFail", "No se pudo recuperar la grabación {0}: {1}", id, e.Message));
                    continue;
                }
            }
            if (j == null || !File.Exists(MidiNotesPath(id)) || MidiEditing.TryFind(Project, id) != null)
            {
                EndMidiTake(id); // already in the project (it died after saving) or never started
                continue;
            }
            Audio.Midi.MidiTakeWriter.Saved saved;
            try
            {
                saved = Audio.Midi.MidiTakeWriter.Read(MidiNotesPath(id));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Notices.Add(Texts.T("store.recoverFail", "No se pudo recuperar la grabación {0}: {1}", j.TrackName, e.Message));
                continue;
            }
            double endSec = saved.EndFrame / (double)saved.SampleRate;
            var played = endSec > j.StartSec ? MidiEditing.TakeNotes(Project, saved.Take, j.StartSec, endSec, saved.SampleRate) : [];
            if (played.Count == 0)
            {
                EndMidiTake(id);
                continue;
            }
            var track = Project.Tracks.Find(t => t.Id == j.TrackId && t.IsMidi);
            if (track == null)
            {
                string added = MidiEditing.AddTrack(Project);
                track = Project.Tracks.Find(t => t.Id == added)!;
                track.Name = j.TrackName;
            }
            var clip = MidiEditing.AddRecorded(Project, track.Id, j.StartSec, endSec, played, j.GridBeats, id)!;
            Project.AddLog("recording-recovered", track.Name);
            Notices.Add(Texts.T("store.recoveredMidi", "Toma MIDI interrumpida recuperada: \"{0}\" ({1} notas)", track.Name, clip.Notes.Count));
            done.Add(MidiNotesPath(id));
            done.Add(jf);
        }
        return done;
    }

    static readonly string[] AudioExts = [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aif", ".aiff"];
    static readonly string[] VideoExts = [".mp4", ".webm", ".mov", ".mkv"];

    /// <summary>
    /// A copy into media/ that the program died in the middle of (<see cref="AudioImport"/>, <see cref="LinkedMedia"/>):
    /// only its temporary name exists, nothing points to it, and the lock says no other window is writing here.
    /// </summary>
    void SweepPartialCopies()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Dir, MediaDir), "*" + AudioImport.PartialCopy))
        {
            try { File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the next open tries again
        }
    }

    /// <summary>
    /// Audio in media/ that no media points to (an older save was restored, or the project file was
    /// rescued from a backup) comes back as media without a clip, never lost in the folder.
    /// </summary>
    int AdoptStrayMedia()
    {
        string media = Path.Combine(Dir, MediaDir);
        var known = Project.Media.SelectMany(m => new[] { Resolve(m.Audio), Resolve(m.Video) })
            .Where(p => p != null)
            .Select(p => Path.GetFullPath(p!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var f in Directory.GetFiles(media))
        {
            if (!AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()) || known.Contains(Path.GetFullPath(f))) continue;
            if (File.Exists(f.Replace(".wav", ".grabando.json"))) continue;
            string stem = Path.GetFileNameWithoutExtension(f);
            string? video = VideoExts.Select(e => Path.Combine(media, stem + e)).FirstOrDefault(v => File.Exists(v) && !known.Contains(Path.GetFullPath(v)));
            var m = new Media
            {
                Name = stem,
                Audio = $"{MediaDir}/{Path.GetFileName(f)}",
                Video = video == null ? null : $"{MediaDir}/{Path.GetFileName(video)}",
                CreatedAt = File.GetCreationTimeUtc(f),
            };
            try
            {
                var info = Audio.WavFile.ReadInfo(f);
                (m.DurationSec, m.SampleRate, m.Channels) = (info.DurationSec, info.SampleRate, info.Channels);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                // not a WAV: the app reads its length when it loads it
            }
            Project.Media.Add(m);
            names.Add(stem);
        }
        if (names.Count > 0)
        {
            Project.AddLog("media-adopted", string.Join(", ", names));
            Notices.Add(Texts.T("store.adopted", "{0} archivo(s) de media/ agregados al proyecto ({1})", names.Count, string.Join(", ", names)));
        }
        return names.Count;
    }

    public void Dispose()
    {
        lockFile?.Dispose();
        lockFile = null;
    }
}
