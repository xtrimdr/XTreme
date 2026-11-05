using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace XtremeDaw.Projects;

/// <summary>
/// Which agents' Claude sessions are still running (asked by the user, #173: a ticket kept showing "Tomado" after its
/// session was closed). The lock hook (<c>tools\bloqueos.ps1</c>) writes down each session's claude.exe in
/// <c>.claude\bloqueos\p-*.json</c> (pid, start and session); a session whose process is gone, or is another one with
/// the same pid, has ended and holds nothing. A session with no file says nothing either way (a hand run, an old hook):
/// for it only the time limit counts.
/// </summary>
public sealed class AgentSessions
{
    const string Agent = "agent:";

    /// <summary>Each written-down session id, and whether its Claude is running.</summary>
    readonly Dictionary<string, bool> alive;

    AgentSessions(Dictionary<string, bool> alive) => this.alive = alive;

    /// <summary>Nothing known: every take counts until it goes stale.</summary>
    public static readonly AgentSessions None = new([]);

    /// <summary>The repository of a list at <c>pruebas\lista.json</c>: where <c>.claude</c> lives.</summary>
    public static string? RootOf(string listPath) => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(listPath)));

    /// <summary>The sessions written down under <paramref name="root"/>, checked now: a handful of small files.</summary>
    public static AgentSessions Read(string? root)
    {
        var all = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (root == null) return None;
        try
        {
            var dir = Path.Combine(root, ".claude", "bloqueos");
            if (!Directory.Exists(dir)) return None;
            foreach (var f in Directory.EnumerateFiles(dir, "p-*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var e = doc.RootElement;
                    if (!e.TryGetProperty("session", out var s) || s.ValueKind != JsonValueKind.String || s.GetString() is not { Length: > 0 } session) continue;
                    if (!e.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out int id)) continue;
                    if (!e.TryGetProperty("started", out var started) || !started.TryGetInt64(out long ticks)) continue;
                    bool running = Running(id, ticks);
                    all[session] = running || (all.TryGetValue(session, out bool had) && had);
                }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return new(all);
    }

    /// <summary>The process <paramref name="pid"/> is running and is the one that started at <paramref name="ticks"/> (UTC).</summary>
    static bool Running(int pid, long ticks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (Exception x) when (x is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>
    /// Whether the session that took a ticket (<c>agent:1a2b3c4d</c>, its first characters) is running: true or false
    /// when it was written down, null when nothing is known of it.
    /// </summary>
    public bool? Alive(string? takenBy)
    {
        if (takenBy is not { Length: > 6 } || !takenBy.StartsWith(Agent, StringComparison.Ordinal)) return null;
        string prefix = takenBy[Agent.Length..];
        bool? found = null;
        foreach (var (session, running) in alive)
        {
            if (!session.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (running) return true;
            found = false;
        }
        return found;
    }

    /// <summary>A stamp of which sessions are running: the bench reads the list again when one ends or starts.</summary>
    public long Stamp
    {
        get
        {
            long stamp = 0;
            foreach (var (session, running) in alive)
                if (running) stamp = unchecked(stamp + StringComparer.OrdinalIgnoreCase.GetHashCode(session));
            return stamp;
        }
    }
}
