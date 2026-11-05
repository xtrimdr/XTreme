using System.Diagnostics;
using System.Text.Json;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// A session's traffic light for the file locks, as the bench shows it on a ticket an agent is working on (asked by the
/// user, #167). The waits are read live from the lock hook's own files (<c>tools\bloqueos.ps1</c>, in
/// <c>.claude\bloqueos</c>): <c>l-*.json</c> is who holds a file and <c>q-*.json</c> each place in its line, so every
/// ticket in the same line counts the same line (2° de 3 and 3° de 3, never two "1° de 1") and says which ticket it
/// waits for. A snapshot written by the hook went stale as soon as the line moved. The reds come from the hook
/// (<c>t-*.json</c>): "bloqueado" is a line that loops back (the agent has to end its turn) and "atencion" a command
/// that changed another session's files. A session with several (its subagents) shows the worst. Whom it waits for is
/// its ticket, or the session's title (<see cref="Title"/>), never an id (asked by the user, #183).
/// </summary>
public sealed record LockLight(LockLight.Level State, string Path, int Place, int Line, string Holder, int HolderTicket = 0)
{
    public enum Level { Free, Waiting, Blocked, Clash }

    public static readonly LockLight Free = new(Level.Free, "", 0, 0, "");

    /// <summary>A place in line not refreshed in this long was given up (the hook's KeepSec).</summary>
    const long KeepSec = 300;

    /// <summary>A lock nobody touched in this long belongs to a session that's gone (the hook's StaleSec).</summary>
    const long StaleSec = 1800;

    /// <summary>What a ticket taken by a session says: <c>agent:</c> and the session's first characters.</summary>
    const string Agent = "agent:";

    sealed record Spot(string Owner, string Session, string Path, long Joined);

    /// <summary>
    /// Every session's light, keyed by its session id; only those that are not green. <paramref name="items"/> tells
    /// which ticket each session works on, to say whom a wait is for. Read once per fill: a handful of small files.
    /// </summary>
    public static Dictionary<string, LockLight> Read(string? root, IReadOnlyList<TestItem> items)
    {
        var all = new Dictionary<string, LockLight>(StringComparer.OrdinalIgnoreCase);
        if (root == null) return all;
        var dir = System.IO.Path.Combine(root, ".claude", "bloqueos");
        try
        {
            if (!Directory.Exists(dir)) return all;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // who holds each file
            var held = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir, "l-*.json"))
                if (Parse(f) is { } e && Str(e, "path") is { } path && Str(e, "session") is { Length: > 0 } session && !Stale(e, now))
                    held[path] = session;

            // each file's line, oldest first, as the hook orders it
            var lines = new Dictionary<string, List<Spot>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir, "q-*.json"))
            {
                if (Parse(f) is not { } e || Str(e, "path") is not { } path || Str(e, "session") is not { Length: > 0 } session) continue;
                if (now - Long(e, "seen") > KeepSec) continue;
                if (!lines.TryGetValue(path, out var line)) lines[path] = line = [];
                line.Add(new Spot(Str(e, "owner") ?? session, session, path, Long(e, "joined")));
            }
            foreach (var line in lines.Values)
            {
                line.Sort((a, b) => a.Joined != b.Joined ? a.Joined.CompareTo(b.Joined) : string.CompareOrdinal(a.Owner, b.Owner));
                for (int i = 0; i < line.Count; i++)
                {
                    var p = line[i];
                    // the file is held by someone else, or, let go, kept for whoever is ahead in line
                    string? holder = held.TryGetValue(p.Path, out var h) ? h : i > 0 ? line[0].Session : null;
                    // the first in line of a free file gets it on its next try; a session doesn't wait for itself
                    if (holder == null || holder.Equals(p.Session, StringComparison.OrdinalIgnoreCase)) continue;
                    var light = new LockLight(Level.Waiting, Rel(root, p.Path), i + 1, line.Count, Title(holder), TicketOf(items, holder));
                    if (!all.TryGetValue(p.Session, out var had) || had.State < light.State) all[p.Session] = light;
                }
            }

            foreach (var f in Directory.EnumerateFiles(dir, "t-*.json"))
            {
                if (Parse(f) is not { } e || Str(e, "session") is not { Length: > 0 } session) continue;
                var level = Str(e, "state") switch
                {
                    "bloqueado" => Level.Blocked,
                    "atencion" => Level.Clash,
                    // "cola" from an older hook: the line itself says it now
                    _ => Level.Free,
                };
                if (level == Level.Free) continue;
                string holder = Str(e, "holder") ?? "";
                var light = new LockLight(level, Str(e, "path") ?? "", 0, 0, holder == "" ? "" : Title(holder), holder == "" ? 0 : TicketOf(items, holder));
                if (!all.TryGetValue(session, out var had) || had.State < light.State) all[session] = light;
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return all;
    }

    /// <summary>
    /// The light of the session that took a ticket (<c>agent:1a2b3c4d</c>, its first characters); green for one that
    /// reported nothing.
    /// </summary>
    public static LockLight For(Dictionary<string, LockLight> all, string? takenBy)
    {
        if (takenBy is not { Length: > 6 } || !takenBy.StartsWith(Agent, StringComparison.Ordinal)) return Free;
        string prefix = takenBy[Agent.Length..];
        var worst = Free;
        foreach (var (session, light) in all)
            if (session.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && light.State > worst.State) worst = light;
        return worst;
    }

    /// <summary>
    /// A stamp of the locks on disk: the bench reads the list again when a file is taken or let go, a line moves or a
    /// light changes, not each time a holder edits (that only rewrites its lock).
    /// </summary>
    public static long Stamp(string? root)
    {
        if (root == null) return 0;
        long stamp = 0;
        try
        {
            var dir = System.IO.Path.Combine(root, ".claude", "bloqueos");
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                string name = System.IO.Path.GetFileName(f);
                long part = name[0] switch
                {
                    // a new lock is a new file: its creation, not its last touch
                    'l' => File.GetCreationTimeUtc(f).Ticks,
                    'q' or 'r' => 1,
                    't' => File.GetLastWriteTimeUtc(f).Ticks,
                    _ => 0,
                };
                if (part != 0) stamp = unchecked(stamp * 31 + StringComparer.Ordinal.GetHashCode(name) + part);
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return stamp;
    }

    /// <summary>The ticket a session works on (the newest it took), 0 for one without a ticket.</summary>
    static int TicketOf(IReadOnlyList<TestItem> items, string session)
    {
        int found = 0;
        foreach (var i in items)
            if (i.TakenBy is { Length: > 6 } by && by.StartsWith(Agent, StringComparison.Ordinal)
                && session.StartsWith(by[Agent.Length..], StringComparison.OrdinalIgnoreCase) && i.Id > found)
                found = i.Id;
        return found;
    }

    /// <summary>A lock whose session's Claude closed, or that nobody touched in StaleSec, holds nothing.</summary>
    static bool Stale(JsonElement e, long now)
    {
        if (now - Long(e, "touched") > StaleSec) return true;
        long pid = Long(e, "pid");
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return Long(e, "started") is > 0 and var started && p.StartTime.ToUniversalTime().Ticks != started;
        }
        catch (Exception x) when (x is ArgumentException or InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The longest session title the badge shows; past it, cut with "…".</summary>
    const int TitleMax = 48;

    /// <summary>A session's title is looked up again after this long (the user may rename it).</summary>
    const long TitleSec = 120;

    static readonly Dictionary<string, (string Title, long Read)> titles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What names a session without a ticket (asked by the user, #183: never an id): the title Claude shows for it, the
    /// one the user gave it (<c>custom-title</c>) or else the one Claude made up (<c>ai-title</c>), read from its
    /// transcript in <c>%USERPROFILE%\.claude\projects</c>. "" when there is none; the bench then says "otra sesión".
    /// Cached a while: the transcript can be several MB and the list is filled often.
    /// </summary>
    static string Title(string session)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (titles.TryGetValue(session, out var had) && now - had.Read < TitleSec) return had.Title;
        string title = "";
        try
        {
            var projects = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (Directory.Exists(projects))
                foreach (var dir in Directory.EnumerateDirectories(projects))
                    foreach (var file in Directory.EnumerateFiles(dir, session + "*.jsonl"))
                        if (title == "") title = TitleIn(file);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        title = title.Trim();
        if (title.Length > TitleMax) title = title[..(TitleMax - 1)].TrimEnd() + "…";
        titles[session] = (title, now);
        return title;
    }

    /// <summary>The user's title of a transcript if it has one, else Claude's; the newest of each wins.</summary>
    static string TitleIn(string file)
    {
        string custom = "", ai = "";
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            bool isCustom = line.Contains("\"type\":\"custom-title\"", StringComparison.Ordinal);
            if (!isCustom && !line.Contains("\"type\":\"ai-title\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (isCustom && Str(doc.RootElement, "customTitle") is { Length: > 0 } c) custom = c;
                else if (!isCustom && Str(doc.RootElement, "aiTitle") is { Length: > 0 } a) ai = a;
            }
            catch (JsonException) { }
        }
        return custom != "" ? custom : ai;
    }

    static string Rel(string root, string path) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..].TrimStart('\\', '/') : path;

    static JsonElement? Parse(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.Clone();
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;
}
