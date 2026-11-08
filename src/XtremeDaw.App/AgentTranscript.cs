using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XtremeDaw.App;

/// <summary>
/// A Claude session's conversation as the bench's Agentes window shows it (asked by the user, #278): what the person
/// wrote, what Claude answered and a line per tool, read from the session's transcript (<c>~\.claude\projects\…\&lt;id&gt;.jsonl</c>).
/// The file only grows, so it is read from where the last read ended: a fill every few seconds costs only the new lines.
/// It also tells whether the session waits for the person (<see cref="Waiting"/>): a turn that ended or a question
/// open come from the transcript itself; a permission prompt leaves nothing there, so the hook writes it down
/// (<c>u-&lt;session hash&gt;.json</c>, Notification event of <c>tools\bloqueos.ps1</c>) and a newer line clears it.
/// </summary>
public sealed partial class AgentTranscript
{
    public enum Kind { User, Claude, Tool }

    /// <summary>One line of the console: who, the text (a tool's own summary for a tool) and the tool's name.</summary>
    public sealed record Entry(Kind Kind, string Text, string Tool, long At);

    /// <summary>What a session waits from the person: nothing, its next message, a permission or an answer.</summary>
    public enum Wait { None, Turn, Permission, Question }

    /// <summary>The console keeps this many lines; older ones go.</summary>
    const int MaxEntries = 400;
    /// <summary>A first read of a long transcript starts this far from its end.</summary>
    const long MaxFirstRead = 4 << 20;
    /// <summary>A text longer than this is cut: the console is for following, not for reading whole reports.</summary>
    const int MaxText = 4000;
    /// <summary>A tool's summary is one line of at most this.</summary>
    const int MaxTool = 140;
    /// <summary>How often the transcript's file is looked for again while not found.</summary>
    const long FindSec = 10;

    static readonly Dictionary<string, AgentTranscript> all = new(StringComparer.OrdinalIgnoreCase);

    public string Session { get; }
    public List<Entry> Entries { get; } = [];
    /// <summary>Lines dropped from the front so far: with <see cref="Entries"/>' count, where the console is.</summary>
    public int Dropped { get; private set; }
    /// <summary>Unix seconds of the newest message line.</summary>
    public long LastAt { get; private set; }
    /// <summary>The newest message line is Claude ending its turn, or the person stopping it.</summary>
    public bool TurnEnded { get; private set; }
    public bool Found => file != null;

    string? file;
    long offset, lookedAt;
    readonly HashSet<string> asks = [];

    AgentTranscript(string session) => Session = session;

    /// <summary>The session's transcript, read up to now.</summary>
    public static AgentTranscript For(string session)
    {
        if (!all.TryGetValue(session, out var t)) all[session] = t = new AgentTranscript(session);
        t.Refresh();
        return t;
    }

    /// <summary>Reads the lines added since the last read; true when something new came.</summary>
    public bool Refresh()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (file == null)
        {
            if (now - lookedAt < FindSec) return false;
            lookedAt = now;
            file = Find(Session);
            if (file == null) return false;
        }
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = stream.Length;
            if (len < offset) { Entries.Clear(); asks.Clear(); offset = 0; }
            if (len == offset) return false;
            bool skipFirst = false;
            if (offset == 0 && len > MaxFirstRead) { offset = len - MaxFirstRead; skipFirst = true; }
            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[len - offset];
            int got = 0;
            while (got < bytes.Length)
            {
                int n = stream.Read(bytes, got, bytes.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got == 0) return false;
            // only whole lines: one being written is read next time
            int end = Array.LastIndexOf(bytes, (byte)'\n', got - 1);
            if (end < 0) return false;
            offset += end + 1;
            var text = Encoding.UTF8.GetString(bytes, 0, end);
            int before = Dropped + Entries.Count;
            long lastBefore = LastAt;
            foreach (var line in text.Split('\n'))
            {
                if (skipFirst) { skipFirst = false; continue; }
                Parse(line);
            }
            if (Entries.Count > MaxEntries)
            {
                int cut = Entries.Count - MaxEntries;
                Entries.RemoveRange(0, cut);
                Dropped += cut;
            }
            return Dropped + Entries.Count != before || LastAt != lastBefore;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>What the session waits from the person now; <paramref name="root"/> is the repository, for the hook's note.</summary>
    public Wait Waiting(string? root)
    {
        if (asks.Count > 0) return Wait.Question;
        if (root != null)
            try
            {
                var note = Path.Combine(root, ".claude", "bloqueos", $"u-{AgentAlert.Hash(Session)}.json");
                if (File.Exists(note))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(note));
                    var e = doc.RootElement;
                    long at = e.TryGetProperty("at", out var a) && a.TryGetInt64(out long v) ? v : 0;
                    string kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                    // a line written after the note (the tool ran, the person answered) means it no longer waits
                    if (LastAt <= at + 1)
                    {
                        if (kind == "permiso") return Wait.Permission;
                        if (kind == "pregunta") return Wait.Question;
                    }
                }
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException) { }
        return TurnEnded ? Wait.Turn : Wait.None;
    }

    static string? Find(string session)
    {
        try
        {
            var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(projects)) return null;
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var f = Path.Combine(dir, session + ".jsonl");
                if (File.Exists(f)) return f;
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return null;
    }

    void Parse(string line)
    {
        if (line.Length == 0) return;
        bool user = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
        if (!user && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            string type = Str(e, "type");
            if (type is not ("user" or "assistant")) return;
            if (Bool(e, "isSidechain")) return;
            long at = DateTimeOffset.TryParse(Str(e, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)
                ? ts.ToUnixTimeSeconds() : LastAt;
            if (!e.TryGetProperty("message", out var msg)) return;
            LastAt = Math.Max(LastAt, at);
            if (type == "assistant")
            {
                TurnEnded = Str(msg, "stop_reason") == "end_turn";
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
                foreach (var c in content.EnumerateArray())
                {
                    string ct = Str(c, "type");
                    if (ct == "text" && Str(c, "text").Trim() is { Length: > 0 } t) Add(Kind.Claude, t, "", at);
                    else if (ct == "tool_use")
                    {
                        string name = Str(c, "name");
                        if (name == "AskUserQuestion") asks.Add(Str(c, "id"));
                        Add(Kind.Tool, ToolText(name, c.TryGetProperty("input", out var input) ? input : default), name, at);
                    }
                }
                return;
            }

            TurnEnded = false;
            bool meta = Bool(e, "isMeta");
            if (!msg.TryGetProperty("content", out var body)) return;
            if (body.ValueKind == JsonValueKind.String)
            {
                if (!meta) AddUser(body.GetString() ?? "", at);
                return;
            }
            if (body.ValueKind != JsonValueKind.Array) return;
            foreach (var c in body.EnumerateArray())
            {
                string ct = Str(c, "type");
                if (ct == "tool_result") asks.Remove(Str(c, "tool_use_id"));
                else if (ct == "text" && !meta) AddUser(Str(c, "text"), at);
            }
        }
        catch (JsonException) { }
    }

    /// <summary>A message of the person: without what Claude Code adds to it, a slash command as typed.</summary>
    void AddUser(string text, long at)
    {
        text = Reminder().Replace(text, "").Trim();
        if (text.StartsWith("<local-command", StringComparison.Ordinal)) return;
        if (Tag("command-name", text) is { Length: > 0 } cmd) text = $"{cmd} {Tag("command-args", text)}".Trim();
        // the person stopped it: from here it waits for them
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal)) TurnEnded = true;
        if (text.Length > 0) Add(Kind.User, text, "", at);
    }

    void Add(Kind kind, string text, string tool, long at)
    {
        if (text.Length > MaxText) text = text[..(MaxText - 1)] + "…";
        Entries.Add(new Entry(kind, text, tool, at));
    }

    /// <summary>A tool in one line: the file, the command, the pattern or what it was told to do.</summary>
    static string ToolText(string name, JsonElement input)
    {
        string s = input.ValueKind != JsonValueKind.Object ? "" : name switch
        {
            "Read" or "Edit" or "Write" or "MultiEdit" => Str(input, "file_path"),
            "NotebookEdit" => Str(input, "notebook_path"),
            "Bash" or "PowerShell" => Str(input, "command"),
            "Grep" or "Glob" => Str(input, "pattern"),
            "Agent" or "Task" => Str(input, "description"),
            "Skill" => Str(input, "skill"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "AskUserQuestion" => input.TryGetProperty("questions", out var q) && q.ValueKind == JsonValueKind.Array && q.GetArrayLength() > 0
                ? Str(q[0], "question") : "",
            _ => "",
        };
        s = s.Replace("\r", "").Replace('\n', ' ').Trim();
        if (name is "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit")
        {
            int cut = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
            if (cut >= 0) s = s[(cut + 1)..];
        }
        if (s.Length > MaxTool) s = s[..(MaxTool - 1)] + "…";
        return s;
    }

    static string Tag(string name, string text)
    {
        int a = text.IndexOf($"<{name}>", StringComparison.Ordinal);
        if (a < 0) return "";
        a += name.Length + 2;
        int b = text.IndexOf($"</{name}>", a, StringComparison.Ordinal);
        return b < 0 ? "" : text[a..b].Trim();
    }

    static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex("<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex Reminder();
}
