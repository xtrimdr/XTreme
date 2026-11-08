using System.Text.Json;

namespace XtremeDaw.App;

/// <summary>
/// An alert between agents' sessions (asked by the user, #263), as the bench shows it when it arrives. The lock hook
/// (<c>tools\bloqueos.ps1</c>) writes one <c>m-*.json</c> per alert in <c>.claude\bloqueos</c>: the ones it makes by
/// itself (a file let go, a file taken that another session changed, a command that changed someone's file, a build
/// that fails in another session's file) and the ones an agent sends (<c>-Avisa</c>, <c>-PideArchivo</c>). The agent
/// reads it on its next step; the hook keeps a read one a while and then deletes it, so the bench reads what is there.
/// The bench's Agentes view (#269) reads the whole day from the hook's log instead (<see cref="History"/>) and sends
/// from the person (<see cref="Send"/>).
/// </summary>
public sealed record AgentAlert(string Id, string To, string From, string Kind, string Text, long At, bool Read)
{
    /// <summary>The recipient's session: an alert can be for one subagent (<c>session/agent</c>).</summary>
    public string ToSession => To.Split('/')[0];

    /// <summary>One that asks the agent to stop and tell the user.</summary>
    public bool Urgent => Kind == "atencion";

    /// <summary>Every alert on disk, oldest first. A handful of small files, read once per fill.</summary>
    public static List<AgentAlert> Load(string? root)
    {
        var all = new List<AgentAlert>();
        if (root == null) return all;
        try
        {
            var dir = Path.Combine(root, ".claude", "bloqueos");
            if (!Directory.Exists(dir)) return all;
            foreach (var f in Directory.EnumerateFiles(dir, "m-*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var e = doc.RootElement;
                    if (Str(e, "to") is not { Length: > 0 } to || Str(e, "text") is not { Length: > 0 } text) continue;
                    all.Add(new AgentAlert(Path.GetFileName(f), to, Str(e, "from") ?? "", Str(e, "kind") ?? "aviso", text, Long(e, "at"), Long(e, "read") > 0));
                }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        all.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : string.CompareOrdinal(a.Id, b.Id));
        return all;
    }

    /// <summary>The alerts for the session that took a ticket (<c>agent:1a2b3c4d</c>), newest first.</summary>
    public static IEnumerable<AgentAlert> For(List<AgentAlert> all, string? takenBy)
    {
        const string agent = "agent:";
        if (takenBy is not { Length: > 6 } || !takenBy.StartsWith(agent, StringComparison.Ordinal)) yield break;
        string prefix = takenBy[agent.Length..];
        for (int i = all.Count - 1; i >= 0; i--)
            if (all[i].ToSession.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return all[i];
    }

    /// <summary>What the hook's log keeps (its LogKeepSec): the bench's conversation shows the last day.</summary>
    const long LogKeepSec = 86400;

    /// <summary>
    /// The conversation between the sessions (asked by the user, #269), oldest first: every alert of the last day from
    /// the hook's log (<c>avisos.jsonl</c>, one JSON per line, written as each alert is sent), and the ones on disk that
    /// came before the log existed.
    /// </summary>
    public static List<AgentAlert> History(string? root, List<AgentAlert> onDisk)
    {
        var all = new List<AgentAlert>();
        if (root == null) return all;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var log = Path.Combine(root, ".claude", "bloqueos", LogName);
            if (File.Exists(log))
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                int n = 0;
                while (reader.ReadLine() is { } line)
                {
                    n++;
                    if (line.Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var e = doc.RootElement;
                        if (Str(e, "to") is not { Length: > 0 } to || Str(e, "text") is not { Length: > 0 } text) continue;
                        long at = Long(e, "at");
                        if (now - at > LogKeepSec) continue;
                        var a = new AgentAlert("log:" + n, to, Str(e, "from") ?? "", Str(e, "kind") ?? "aviso", text, at, true);
                        if (seen.Add(a.Key)) all.Add(a);
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        foreach (var a in onDisk) if (seen.Add(a.Key)) all.Add(a);
        all.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : string.CompareOrdinal(a.Id, b.Id));
        return all;
    }

    /// <summary>The same alert in the log and on disk: who, when and what.</summary>
    internal string Key => $"{At}|{To}|{Text}";

    /// <summary>
    /// Whether the agent got it (#283): the hook marks its file read when it hands it over, and deletes it a while
    /// later, so one no longer on disk was read.
    /// </summary>
    public bool ReadIn(List<AgentAlert> onDisk)
    {
        foreach (var d in onDisk)
            if (d.Key == Key) return d.Read;
        return true;
    }

    const string LogName = "avisos.jsonl";

    /// <summary>
    /// Sends from the bench, as the person (asked by the user, #269): what <c>tools\bloqueos.ps1 -Avisa</c> or
    /// <c>-PideArchivo</c> run by hand would leave, an <c>m-*.json</c> for the session (the hook gives it to the agent on its
    /// next step) and a line in the log. Written here and not by running the script: no console, and the text arrives as
    /// typed. <paramref name="kind"/> is "mensaje" or "pedido"; false when it could not be written.
    /// </summary>
    public static bool Send(string? root, string to, string kind, string text, string path = "")
    {
        if (root == null || to == "" || string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        text = char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
        try
        {
            var dir = Path.Combine(root, ".claude", "bloqueos");
            Directory.CreateDirectory(dir);
            var m = new Dictionary<string, object>
            {
                ["to"] = to, ["from"] = "", ["kind"] = kind, ["text"] = text, ["path"] = path,
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["read"] = 0,
            };
            string json = JsonSerializer.Serialize(m);
            var file = Path.Combine(dir, $"m-{Hash(to)}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Random.Shared.Next(100000)}.json");
            var tmp = file + ".bench.tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
            var log = Path.Combine(dir, LogName);
            for (int n = 0; ; n++)
            {
                try
                {
                    File.AppendAllText(log, json + "\n");
                    break;
                }
                catch (IOException) when (n < 20) { Thread.Sleep(25); }
            }
            return true;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The hook's name for a session's files: SHA-1 of the id in lower case, in hex.</summary>
    internal static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant())));

    /// <summary>The sessions whose Claude is open now (the hook's <c>p-*.json</c> of a running process), oldest first.</summary>
    public static List<string> Live(string? root)
    {
        var all = new List<(string Session, long Started)>();
        if (root == null) return [];
        try
        {
            var dir = Path.Combine(root, ".claude", "bloqueos");
            if (!Directory.Exists(dir)) return [];
            foreach (var f in Directory.EnumerateFiles(dir, "p-*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var e = doc.RootElement;
                    long pid = Long(e, "pid");
                    if (Str(e, "session") is not { Length: > 0 } session || pid <= 0) continue;
                    long started = Long(e, "started");
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (p.StartTime.ToUniversalTime().Ticks != started) continue;
                    if (!all.Exists(s => s.Session.Equals(session, StringComparison.OrdinalIgnoreCase))) all.Add((session, started));
                }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException or ArgumentException
                                           or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        all.Sort((a, b) => a.Started.CompareTo(b.Started));
        return [.. all.Select(s => s.Session)];
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;
}
