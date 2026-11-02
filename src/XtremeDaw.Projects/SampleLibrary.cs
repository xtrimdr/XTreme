using System.Globalization;
using System.Text;
using System.Text.Json;
using XtremeDaw.Audio;

namespace XtremeDaw.Projects;

/// <summary>A folder of the PC the Biblioteca takes samples from. It belongs to the machine, not to a project.</summary>
public sealed record SampleFolder(string Path, string Name);

/// <summary>
/// One file of a sample folder. What the disk says (<see cref="Size"/>, <see cref="Ticks"/>) is read while scanning;
/// what it takes decoding to know (<see cref="DurationSec"/>, <see cref="Peaks"/>) comes later, only for the rows that
/// were on screen. Those two are written once by the analysis thread and only read by the UI.
/// </summary>
public sealed class SampleFile(string path, string root, long size, long ticks)
{
    public string Path { get; } = path;
    /// <summary>The <see cref="SampleFolder.Path"/> it was found under.</summary>
    public string Root { get; } = root;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public long Size { get; } = size;
    public long Ticks { get; } = ticks;
    public bool IsVideo { get; } = MediaDecoder.IsVideo(path);
    /// <summary>The path from the root, folded for searching (<see cref="SampleLibrary.Fold"/>).</summary>
    public string Search { get; } = SampleLibrary.Fold(System.IO.Path.GetRelativePath(root, path));
    /// <summary>0 = not analyzed yet; below 0 = no sound could be read from it.</summary>
    public volatile float DurationSecValue;
    public double DurationSec => DurationSecValue;
    public bool Broken => DurationSecValue < 0;
    /// <summary>The row's wave (<see cref="SampleLibrary.PeakBins"/> values); null if not read yet or too long to read.</summary>
    public volatile byte[]? Peaks;
}

/// <summary>A folder of the tree: its subfolders and files sorted by name, and how many files hang from it.</summary>
public sealed class SampleNode(string path, string name, int depth)
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public int Depth { get; } = depth;
    public List<SampleNode> Folders { get; } = [];
    public List<SampleFile> Files { get; } = [];
    public int Count { get; set; }
    /// <summary>Only on a root: the folder isn't there now (a disk that was unplugged).</summary>
    public bool Offline { get; init; }
}

/// <summary>One row of the list: a folder of the tree, or a file.</summary>
public readonly record struct SampleRow(SampleNode? Folder, SampleFile? File, int Depth);

/// <summary>
/// The sample folders (Biblioteca → Samples): which ones, what is in them and what was learned about each file. Reading
/// the disk takes long on a big library, so the last index opens at once (<c>samples.idx</c> in the app's folder, written
/// atomically; losing it only costs reading it all again) and a scan runs behind it. The files of a folder that isn't
/// there stay listed as offline, because a disk being unplugged must not forget its analysis. The file list is replaced
/// whole (<see cref="Files"/>), so any thread can read it and the search runs off the UI thread. It only looks at the
/// folders: nothing in them is ever written.
/// </summary>
public sealed class SampleLibrary : IDisposable
{
    /// <summary>Values in a row's wave: one byte each, kept for every file in the index.</summary>
    public const int PeakBins = 48;
    /// <summary>A file longer than this gets its length but no wave: decoding it for a row isn't worth it.</summary>
    public const double MaxWaveSec = 120;
    const int IndexVersion = 1;
    /// <summary>Changes in a folder usually come in bursts (a pack being unzipped): the rescan waits for this much quiet.</summary>
    const int ChangeQuietMs = 2000;

    readonly object gate = new();
    readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer rescan;
    readonly Stack<SampleFile> wanted = new();
    readonly HashSet<SampleFile> queued = [];
    readonly CancellationTokenSource closing = new();
    volatile SampleFile[] files = [];
    volatile SampleFolder[] folders = [];
    HashSet<string> favorites = new(StringComparer.OrdinalIgnoreCase);
    volatile bool scanning;
    bool analyzing, dirty;

    /// <summary>The list changed. Comes from any thread.</summary>
    public event Action? Changed;
    /// <summary>A file got its length or wave. Comes from the analysis thread.</summary>
    public event Action? Analyzed;

    public IReadOnlyList<SampleFolder> Folders => folders;
    public IReadOnlyList<SampleFile> Files => files;
    public bool Scanning => scanning;
    static string IndexPath => Path.Combine(AppConfig.Dir, "samples.idx");

    public SampleLibrary() => rescan = new Timer(_ => Scan());

    /// <summary>Reads the folders and the last index, and starts looking at the disk. Off the UI thread: the index can be big.</summary>
    public void Open()
    {
        folders = ReadFolders();
        favorites = new HashSet<string>(ReadList("sampleFavorites"), StringComparer.OrdinalIgnoreCase);
        files = ReadIndex();
        Changed?.Invoke();
        Scan();
    }

    public void AddFolder(string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (folders.Any(f => Same(f.Path, path))) return;
        string name = Path.GetFileName(path);
        folders = [.. folders, new SampleFolder(path, name.Length > 0 ? name : path)];
        SaveFolders();
        Scan();
    }

    public void RemoveFolder(string path)
    {
        folders = [.. folders.Where(f => !Same(f.Path, path))];
        files = [.. files.Where(f => !Same(f.Root, path))];
        SaveFolders();
        Save();
        Changed?.Invoke();
        Scan();
    }

    public void RenameFolder(string path, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        folders = [.. folders.Select(f => Same(f.Path, path) ? f with { Name = name } : f)];
        SaveFolders();
        Changed?.Invoke();
    }

    public bool IsFavorite(SampleFile f)
    {
        lock (gate) return favorites.Contains(f.Path);
    }

    public void ToggleFavorite(SampleFile f)
    {
        string text;
        lock (gate)
        {
            if (!favorites.Remove(f.Path)) favorites.Add(f.Path);
            text = JsonSerializer.Serialize(favorites.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        Quietly(() => AppConfig.Set("sampleFavorites", text));
    }

    /// <summary>Looks at the disk again, in the background; a scan already running is left to finish, then runs once more.</summary>
    public void Scan()
    {
        lock (gate)
        {
            if (scanning) { dirty = true; return; }
            scanning = true;
            Task.Run(ScanNow);
        }
    }

    void ScanNow()
    {
        for (; ; )
        {
            var known = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var found = new List<SampleFile>(known.Count);
            foreach (var folder in folders)
            {
                if (closing.IsCancellationRequested) break;
                if (!Directory.Exists(folder.Path))
                {
                    found.AddRange(known.Values.Where(f => Same(f.Root, folder.Path)));
                    Unwatch(folder.Path);
                    continue;
                }
                Watch(folder.Path);
                try { found.AddRange(Walk(folder.Path, known)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    found.AddRange(known.Values.Where(f => Same(f.Root, folder.Path))); // unplugged while it was being read
                }
            }
            bool same = found.Count == known.Count && found.All(f => known.GetValueOrDefault(f.Path) == f);
            if (!same)
            {
                files = [.. found];
                Save();
            }
            lock (gate)
            {
                if (!dirty)
                {
                    scanning = false;
                    break;
                }
                dirty = false;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>What a folder holds now. A file that didn't change keeps what was learned about it.</summary>
    public static IEnumerable<SampleFile> Walk(string root, IReadOnlyDictionary<string, SampleFile>? known = null)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var info = new DirectoryInfo(root);
        foreach (var f in info.EnumerateFiles("*", options))
        {
            if (!MediaDecoder.IsMedia(f.FullName)) continue;
            long ticks = f.LastWriteTimeUtc.Ticks;
            if (known != null && known.TryGetValue(f.FullName, out var had) && had.Size == f.Length && had.Ticks == ticks) yield return had;
            else yield return new SampleFile(f.FullName, root, f.Length, ticks);
        }
    }

    /// <summary>The tree of the folders, with roots in the order they were added. Off the UI thread.</summary>
    public static List<SampleNode> Tree(IReadOnlyList<SampleFolder> folders, IReadOnlyList<SampleFile> files)
    {
        var roots = new List<SampleNode>(folders.Count);
        var nodes = new Dictionary<string, SampleNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var root = new SampleNode(folder.Path, folder.Name, 0) { Offline = !Directory.Exists(folder.Path) };
            nodes[folder.Path] = root;
            roots.Add(root);
        }
        SampleNode? NodeOf(string dir)
        {
            if (nodes.TryGetValue(dir, out var n)) return n;
            if (Path.GetDirectoryName(dir) is not { } up || NodeOf(up) is not { } parent) return null;
            n = new SampleNode(dir, Path.GetFileName(dir), parent.Depth + 1);
            nodes[dir] = n;
            parent.Folders.Add(n);
            return n;
        }
        foreach (var f in files)
        {
            if (Path.GetDirectoryName(f.Path) is not { } dir || NodeOf(dir) is not { } node) continue;
            node.Files.Add(f);
        }
        foreach (var root in roots) Finish(root);
        return roots;
    }

    static int Finish(SampleNode n)
    {
        n.Folders.Sort((a, b) => NameOrder(a.Name, b.Name));
        n.Files.Sort((a, b) => NameOrder(a.Name, b.Name));
        n.Count = n.Files.Count + n.Folders.Sum(Finish);
        return n.Count;
    }

    /// <summary>Numbers in a name sort by value ("Kick 2" before "Kick 10"), as in the Explorer.</summary>
    static int NameOrder(string a, string b) => CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// <summary>The tree as rows: a folder's content shows when <paramref name="open"/> holds it.</summary>
    public static List<SampleRow> Rows(IEnumerable<SampleNode> roots, IReadOnlySet<string> open)
    {
        var rows = new List<SampleRow>();
        void Add(SampleNode n)
        {
            rows.Add(new SampleRow(n, null, n.Depth));
            if (!open.Contains(n.Path)) return;
            foreach (var d in n.Folders) Add(d);
            foreach (var f in n.Files) rows.Add(new SampleRow(null, f, n.Depth + 1));
        }
        foreach (var r in roots) Add(r);
        return rows;
    }

    /// <summary>The files that have every word of <paramref name="query"/> somewhere in their name or folders, in any order.</summary>
    public static List<SampleFile> Search(IEnumerable<SampleFile> files, string query)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SampleFile>();
        foreach (var f in files)
        {
            bool all = true;
            foreach (string w in words)
                if (!f.Search.Contains(w, StringComparison.Ordinal)) { all = false; break; }
            if (all) hits.Add(f);
        }
        hits.Sort((a, b) => NameOrder(a.Name, b.Name));
        return hits;
    }

    /// <summary>Lowercase, without accents and with punctuation as spaces: "Kick_Seco-02.wav" is found by "kick seco 02".</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString();
    }

    /// <summary>Asks for a file's length and wave: the last asked goes first, because that is what is on screen now.</summary>
    public void Want(SampleFile f)
    {
        if (f.DurationSecValue != 0) return;
        lock (gate)
        {
            if (!queued.Add(f)) return;
            wanted.Push(f);
            if (analyzing) return;
            analyzing = true;
        }
        new Thread(Analyze) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "sample analysis" }.Start();
    }

    /// <summary>What a screenful asked for and nobody saw: scrolling fast through a library must not queue all of it.</summary>
    public void ForgetWanted()
    {
        lock (gate)
        {
            wanted.Clear();
            queued.Clear();
        }
    }

    void Analyze()
    {
        int done = 0;
        while (!closing.IsCancellationRequested)
        {
            SampleFile? f;
            lock (gate)
            {
                if (!wanted.TryPop(out f))
                {
                    analyzing = false;
                    break;
                }
                queued.Remove(f);
            }
            AnalyzeOne(f);
            done++;
            Analyzed?.Invoke();
        }
        if (done > 0) Save();
    }

    /// <summary>Learns a file's length and wave. Never throws: a file that can't be read is marked and stays listed.</summary>
    public static void AnalyzeOne(SampleFile f)
    {
        try
        {
            double sec = MediaDecoder.Duration(f.Path);
            if (sec > MaxWaveSec)
            {
                f.DurationSecValue = (float)sec;
                return;
            }
            var audio = MediaDecoder.Load(f.Path, 0);
            f.Peaks = MediaDecoder.Peaks(audio.Left, audio.Right, PeakBins);
            f.DurationSecValue = (float)audio.DurationSec;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OutOfMemoryException or OverflowException)
        {
            f.DurationSecValue = -1;
        }
    }

    void Watch(string path)
    {
        lock (gate)
        {
            if (watchers.ContainsKey(path)) return;
            try
            {
                var w = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler changed = (_, _) => RescanSoon();
                w.Created += changed;
                w.Deleted += changed;
                w.Changed += changed;
                w.Renamed += (_, _) => RescanSoon();
                w.Error += (_, _) => RescanSoon();
                w.EnableRaisingEvents = true;
                watchers[path] = w;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { /* a network share may refuse it: Scan still works */ }
        }
    }

    /// <summary>A watcher's event can still be on its way while the library closes.</summary>
    void RescanSoon()
    {
        try { rescan.Change(ChangeQuietMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    void Unwatch(string path)
    {
        lock (gate)
            if (watchers.Remove(path, out var w)) w.Dispose();
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string[] ReadList(string key)
    {
        try { return JsonSerializer.Deserialize<string[]>(AppConfig.Get(key) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    static SampleFolder[] ReadFolders()
    {
        try
        {
            return JsonSerializer.Deserialize<SampleFolder[]>(AppConfig.Get("sampleFolders") ?? "[]", ProjectStore.Json)?.Where(f => !string.IsNullOrEmpty(f.Path)).ToArray() ?? [];
        }
        catch (JsonException) { return []; }
    }

    void SaveFolders()
    {
        string text = JsonSerializer.Serialize(folders, ProjectStore.Json);
        Quietly(() => AppConfig.Set("sampleFolders", text));
    }

    /// <summary>Settings can't be written (a full disk, a locked file): the session keeps working with what it has.</summary>
    static void Quietly(Action write)
    {
        try { write(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    SampleFile[] ReadIndex()
    {
        var roots = folders;
        try
        {
            using var r = new BinaryReader(new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan), Encoding.UTF8);
            if (r.ReadInt32() != IndexVersion) return [];
            int count = r.ReadInt32();
            var list = new List<SampleFile>(count);
            for (int i = 0; i < count; i++)
            {
                string path = r.ReadString(), root = r.ReadString();
                long size = r.ReadInt64(), ticks = r.ReadInt64();
                float sec = r.ReadSingle();
                bool wave = r.ReadBoolean();
                var peaks = wave ? r.ReadBytes(PeakBins) : null;
                if (!roots.Any(f => Same(f.Path, root))) continue;
                list.Add(new SampleFile(path, root, size, ticks) { DurationSecValue = sec, Peaks = peaks });
            }
            return [.. list];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
        {
            return []; // missing or cut short: the scan makes it again
        }
    }

    /// <summary>Writes the index through a temporary name, like every settings file.</summary>
    void Save()
    {
        var list = files;
        string path = IndexPath, tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        lock (closing)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new BinaryWriter(file, Encoding.UTF8))
                {
                    w.Write(IndexVersion);
                    w.Write(list.Length);
                    foreach (var f in list)
                    {
                        w.Write(f.Path);
                        w.Write(f.Root);
                        w.Write(f.Size);
                        w.Write(f.Ticks);
                        w.Write(f.DurationSecValue);
                        var peaks = f.Peaks;
                        w.Write(peaks != null);
                        if (peaks != null) w.Write(peaks);
                    }
                    w.Flush();
                    file.Flush(true);
                }
                File.Move(tmp, path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            }
    }

    public void Dispose()
    {
        closing.Cancel();
        lock (gate)
        {
            foreach (var w in watchers.Values) w.Dispose();
            watchers.Clear();
        }
        rescan.Dispose();
    }
}
