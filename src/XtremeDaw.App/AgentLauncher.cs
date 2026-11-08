using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace XtremeDaw.App;

/// <summary>
/// Calls the agents from the Banco de pruebas (asked by the user): "Revisar" and the skill buttons open an interactive
/// session in the repository, never a background one (decisión del usuario): the skill asks before design decisions,
/// and the person answers there. Each action has two buttons (pedido del usuario, 2026-11-04): a new session of the
/// Claude desktop app by its documented link, which prefills the message and asks to confirm the folder, so the person
/// sends it; or a console with Claude Code, which starts by itself. Without the app's link registered, both use the console.
/// The console runs inside the bench (<see cref="InBench"/>, #278); Windows Terminal (<see cref="InTerminal"/>) only when
/// the pseudo console cannot start.
/// </summary>
static class AgentLauncher
{
    public const string ReviewSkill = "xt_revisar-workbench";
    /// <summary>The desktop app's link: a new Code session with its folder and the message prefilled (never sent).</summary>
    const string DesktopLink = "claude://code/new";
    /// <summary>
    /// Model and effort of every console session (decisión del usuario, #315: always Opus, medium effort), passed on the
    /// command line so neither the account's default nor the host session's variables (dropped) decide it.
    /// </summary>
    static readonly string[] ModelArgs = ["--model", "claude-opus-5-5", "--effort", "medium"];

    /// <summary>
    /// Whether the desktop app is installed: the Store's package (its data folder, <c>%LOCALAPPDATA%\Packages\Claude_*</c>),
    /// the classic installer's folder, or its link registered by hand. Not the registry alone: the packaged app writes
    /// <c>HKCU\Software\Classes\claude</c> in its own private view, which a program outside it (the bench) does not see,
    /// so the bench thought there was no app and opened the console.
    /// </summary>
    public static bool DesktopInstalled
    {
        get
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var packages = Path.Combine(local, "Packages");
                if (Directory.Exists(packages) && Directory.EnumerateDirectories(packages, "Claude_*").Any()) return true;
                if (Directory.Exists(Path.Combine(local, "AnthropicClaude"))) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\claude\shell\open\command");
            return key?.GetValue(null) is string { Length: > 0 };
        }
    }

    /// <summary>
    /// The repository a list belongs to (<c>&lt;repo&gt;\pruebas\lista.json</c>), or null when it has no review skill:
    /// then the bench shows no "Revisar".
    /// </summary>
    public static string? Root(string listPath)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(listPath)));
            return root != null && File.Exists(Path.Combine(root, ".claude", "skills", ReviewSkill, "SKILL.md")) ? root : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return null; }
    }


    /// <summary>
    /// The project's skills a bench button runs (<c>.claude\skills\xt_*\SKILL.md</c>), with the <c>description</c> of their
    /// frontmatter, by name.
    /// </summary>
    public static IReadOnlyList<(string Name, string Description)> Skills(string root)
    {
        try
        {
            var dir = Path.Combine(root, ".claude", "skills");
            return [.. Directory.EnumerateDirectories(dir, "xt_*")
                .Select(d => Path.Combine(d, "SKILL.md"))
                .Where(File.Exists)
                .Select(f =>
                {
                    var head = File.ReadLines(f).Skip(1).TakeWhile(l => l.TrimEnd() != "---").ToList();
                    string Value(string key) =>
                        head.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal)) is { } l ? l[(key.Length + 1)..].Trim().Trim('"', '\'') : "";
                    string name = Value("name");
                    return (Name: name != "" ? name : Path.GetFileName(Path.GetDirectoryName(f)!), Description: Value("description"));
                })
                .OrderBy(s => s.Name, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The newest test copy of XTreme, with no Claude in between (asked by the user): <c>tools\probar.ps1 -Abrir -Acceso</c>,
    /// as the desktop shortcut does, so an open XTreme is offered to close (it saves first) and a failure shows a window.
    /// With <paramref name="ticket"/>, the copy opens showing that ticket's toast (<c>-Probar</c>, asked by the user, #143).
    /// Returns why it could not start, or null.
    /// </summary>
    public static string? OpenApp(string root, int ticket = 0) => RunProbar(root, ticket, "-Abrir");

    /// <summary>
    /// The newest XT-WorkBench (<c>tools\probar.ps1 -Banco</c>): a change to the Banco is tried in the Banco, not in
    /// XTreme (asked by the user, 2026-11-05). An open bench from an older build closes and the newest opens.
    /// </summary>
    public static string? OpenBench(string root) => RunProbar(root, 0, "-Banco");

    static string? RunProbar(string root, int ticket, string what)
    {
        var script = Path.Combine(root, "tools", "probar.ps1");
        if (!File.Exists(script)) return script;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
        };
        foreach (var a in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, what, "-Acceso"])
            start.ArgumentList.Add(a);
        if (ticket > 0)
        {
            start.ArgumentList.Add("-Probar");
            start.ArgumentList.Add(ticket.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        try
        {
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return e.Message; }
    }

    /// <summary>
    /// Set in the console's environment (asked by the user, #143): the file <c>tools\cerrar-sesion.ps1</c> leaves when the
    /// agent finished its work well, and the console, seeing it once Claude ends, closes. Not a <c>CLAUDE</c> prefix, which
    /// <see cref="DropHostSession"/> takes out.
    /// </summary>
    const string CloseVar = "XT_BENCH_CLOSE";

    /// <summary>
    /// Opens Claude at <paramref name="root"/> on <paramref name="prompt"/>: in the desktop app, or in a console (also
    /// when the app is not installed). Returns why it could not, or null.
    /// </summary>
    public static string? Run(string root, string prompt, bool desktop) =>
        desktop && DesktopInstalled ? InDesktop(root, prompt) : InTerminal(root, prompt);

    /// <summary>
    /// A new session of the desktop app, with the skill's command as the person would type it (<c>/xt_revisar-workbench 60</c>,
    /// pedido del usuario): it waits in the message box and the person sends it.
    /// </summary>
    static string? InDesktop(string root, string prompt)
    {
        string url = $"{DesktopLink}?folder={Uri.EscapeDataString(root)}&q={Uri.EscapeDataString(prompt)}";
        try
        {
            // the shell follows the app's registered protocol (explorer.exe with the link opened Documentos instead)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // the link did not open: the terminal still gets the work done
            return InTerminal(root, prompt);
        }
    }

    /// <summary>
    /// Claude Code in a terminal at <paramref name="root"/> with <paramref name="prompt"/> as its first message, sent at
    /// once. It closes by itself when the agent says it finished well (<see cref="CloseVar"/>). The prompt goes through cmd's command line: quotes, percent signs and newlines (and Windows Terminal's
    /// <c>;</c>, which splits its commands) would break it, so they are left out.
    /// </summary>
    static string? InTerminal(string root, string prompt)
    {
        prompt = string.Join(' ', prompt.Replace('"', '\'').Replace('%', ' ').Replace(';', ',')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        // cmd /k keeps the window if claude is missing or ends, so what it said stays on the screen
        var start = File.Exists(wt) ? new ProcessStartInfo(wt) : new ProcessStartInfo("cmd.exe");
        if (File.Exists(wt))
        {
            foreach (var a in (string[])["-d", root, "--title", "XTreme · " + prompt, "cmd", "/k"]) start.ArgumentList.Add(a);
        }
        else start.ArgumentList.Add("/k");
        start.ArgumentList.Add(ClaudeExe());
        foreach (var a in ModelArgs) start.ArgumentList.Add(a);
        // default (least verbose) view on purpose (#148: --verbose was too much; Ctrl+O's screen cannot be the startup view)
        start.ArgumentList.Add(prompt);
        // once Claude ends: if the agent left the file (it finished well), the console goes; otherwise it stays to read
        string flag = Path.Combine(Path.GetTempPath(), $"xt-cerrar-{Guid.NewGuid():N}");
        foreach (var a in (string[])["&", "if", "exist", flag, "(del", flag, "&", "exit", "0)"]) start.ArgumentList.Add(a);
        start.UseShellExecute = false;
        start.WorkingDirectory = root;
        DropHostSession(start);
        start.Environment[CloseVar] = flag;
        try
        {
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return e.Message; }
    }

    /// <summary>
    /// Claude Code inside the bench (asked by the user, #278): the same session as <see cref="InTerminal"/>, in a pseudo
    /// console the bench draws, so it needs no Windows Terminal and the prompt goes whole (no cmd in between). Returns
    /// the console, or why it could not start.
    /// </summary>
    public static (Terminal.ConsoleSession? Console, string? Error) InBench(string root, string prompt)
    {
        var start = new ProcessStartInfo();
        DropHostSession(start);
        string flag = Path.Combine(Path.GetTempPath(), $"xt-cerrar-{Guid.NewGuid():N}");
        start.Environment[CloseVar] = flag;
        string line = Quote(ClaudeExe()) + " " + string.Join(' ', ModelArgs) + " " + Quote(prompt);
        try
        {
            return (Terminal.ConsoleSession.Start(prompt, line, root, start.Environment, flag), null);
        }
        catch (Exception e) when (e is Win32Exception or EntryPointNotFoundException or IOException) { return (null, e.Message); }
    }

    /// <summary>One argument of a Windows command line, quoted as the C runtime reads it back (backslashes before a quote doubled).</summary>
    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Claude Code's executable: its native installer's folder (<c>%USERPROFILE%\.local\bin</c>), so it opens even when
    /// the PATH this process got is older than the install; otherwise <c>claude</c>, by the PATH.
    /// </summary>
    static string ClaudeExe()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(exe) ? exe : "claude";
    }

    /// <summary>Prefixes of the variables a Claude session hands its children (the copy is opened from one).</summary>
    static readonly string[] HostPrefixes = ["CLAUDE", "ANTHROPIC_", "MCP_", "OTEL_"];

    /// <summary>
    /// Takes out what the bench inherited from the Claude session that opened it (<c>CLAUDECODE</c>, the desktop app's
    /// entry point, its proxy URL…): with them the console's Claude refuses to start inside "another session" or talks
    /// to the host's socket. Those the person set in Windows (user or machine) stay.
    /// </summary>
    static void DropHostSession(ProcessStartInfo start)
    {
        foreach (var name in start.Environment.Keys.ToList())
        {
            if (!HostPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) != null) continue;
            if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) != null) continue;
            start.Environment.Remove(name);
        }
    }

    /// <summary>
    /// The Claude process of the session that took a ticket (asked by the user, #164), or null when it ended. The lock
    /// hook writes down each session's claude.exe (<c>.claude\bloqueos\p-*.json</c>: pid, start and session); a ticket
    /// keeps only the session's first characters (<c>agent:1a2b3c4d</c>), so it is found by that prefix. The start time
    /// tells a reused pid from the session's.
    /// </summary>
    public static int? SessionProcess(string root, string? takenBy)
    {
        const string Agent = "agent:";
        if (takenBy is not { Length: > 6 } || !takenBy.StartsWith(Agent, StringComparison.Ordinal)) return null;
        string prefix = takenBy[Agent.Length..];
        foreach (var (session, pid) in LiveProcesses(root))
            if (session.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return pid;
        return null;
    }

    /// <summary>
    /// Each session whose claude.exe still runs, with its process (#164, #278: the bench tells its own consoles by it).
    /// The start time tells a reused pid from the session's.
    /// </summary>
    public static List<(string Session, int Pid)> LiveProcesses(string root)
    {
        var list = new List<(string, int)>();
        try
        {
            var dir = Path.Combine(root, ".claude", "bloqueos");
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, "p-*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var e = doc.RootElement;
                    if (!e.TryGetProperty("session", out var s) || s.GetString() is not { Length: > 0 } session) continue;
                    if (!e.TryGetProperty("pid", out var pid) || !e.TryGetProperty("started", out var started)) continue;
                    using var p = Process.GetProcessById(pid.GetInt32());
                    if (p.StartTime.ToUniversalTime().Ticks == started.GetInt64()) list.Add((session, p.Id));
                }
                catch (Exception x) when (x is ArgumentException or InvalidOperationException or Win32Exception or JsonException or FormatException or IOException) { }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>
    /// Each written-down session's model and effort (#315), as the hook read them from its claude.exe's command line,
    /// in the bench's words ("Opus 5.5 · medio"); a session started without them is left out. A handful of small files.
    /// </summary>
    public static Dictionary<string, string> Models(string? root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root == null) return map;
        try
        {
            var dir = Path.Combine(root, ".claude", "bloqueos");
            if (!Directory.Exists(dir)) return map;
            foreach (var f in Directory.EnumerateFiles(dir, "p-*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var e = doc.RootElement;
                    if (!e.TryGetProperty("session", out var s) || s.GetString() is not { Length: > 0 } session) continue;
                    string model = e.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? ModelName(m.GetString()!) : "";
                    string effort = e.TryGetProperty("effort", out var x) && x.ValueKind == JsonValueKind.String ? EffortName(x.GetString()!) : "";
                    string label = model != "" && effort != "" ? $"{model} · {effort}" : model + effort;
                    if (label != "") map[session] = label;
                }
                catch (Exception x) when (x is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return map;
    }

    /// <summary>The label of the session that took a ticket (<c>agent:1a2b3c4d</c>, its first characters), "" when unknown.</summary>
    public static string ModelOf(Dictionary<string, string> models, string? takenBy)
    {
        const string Agent = "agent:";
        if (takenBy is not { Length: > 6 } || !takenBy.StartsWith(Agent, StringComparison.Ordinal)) return "";
        string prefix = takenBy[Agent.Length..];
        foreach (var (session, label) in models)
            if (session.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return label;
        return "";
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5", "sonnet" → "Sonnet"; a name it does not know stays as it came.</summary>
    static string ModelName(string id)
    {
        var m = System.Text.RegularExpressions.Regex.Match(id, @"^(?:claude-)?(opus|sonnet|haiku|fable)(?:-(\d+)(?:-(\d{1,2}))?)?(?:-\d{8})?(\[1m\])?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return id;
        string name = char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..].ToLowerInvariant();
        if (m.Groups[2].Success) name += " " + m.Groups[2].Value + (m.Groups[3].Success ? "." + m.Groups[3].Value : "");
        return m.Groups[4].Success ? name + " 1M" : name;
    }

    static string EffortName(string effort) => effort.ToLowerInvariant() switch
    {
        "low" or "medium" or "high" or "xhigh" or "max" => Loc.T("test.list.effort." + effort.ToLowerInvariant()),
        _ => effort,
    };

    /// <summary>
    /// Brings forward the window of the Claude process <paramref name="pid"/> (#164): its console (in Windows Terminal,
    /// the window that hosts its tab, the root owner of the pseudo console's window), or else the nearest ancestor with
    /// a window (the desktop app, an editor). False when it has none.
    /// </summary>
    public static bool ShowSession(int pid)
    {
        var hwnd = ConsoleWindow(pid);
        if (hwnd == 0) hwnd = AncestorWindow(pid);
        if (hwnd == 0) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        return SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// The visible window of <paramref name="pid"/>'s console: the bench has no console of its own, so it attaches to
    /// that one for a moment to ask, and lets go.
    /// </summary>
    static nint ConsoleWindow(int pid)
    {
        if (GetConsoleWindow() != 0 || !AttachConsole((uint)pid)) return 0;
        try
        {
            var w = GetConsoleWindow();
            if (w == 0) return 0;
            var top = GetAncestor(w, GaRootOwner);
            if (top != 0) w = top;
            return IsWindowVisible(w) ? w : 0;
        }
        finally { FreeConsole(); }
    }

    /// <summary>The main window of the nearest process above <paramref name="pid"/> (and itself) that has one.</summary>
    static nint AncestorWindow(int pid)
    {
        var parents = ParentIds();
        for (int n = 0, id = pid; n < 10 && id > 0; n++)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                if (p.MainWindowHandle != 0) return p.MainWindowHandle;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return 0; }
            if (!parents.TryGetValue(id, out id)) return 0;
        }
        return 0;
    }

    /// <summary>Each running process's parent, from a Toolhelp snapshot (.NET does not give it).</summary>
    static Dictionary<int, int> ParentIds()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == -1) return map;
        try
        {
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[(int)e.ProcessId] = (int)e.ParentProcessId;
        }
        finally { CloseHandle(snap); }
        return map;
    }

    const int SwRestore = 9;
    const uint GaRootOwner = 3, SnapProcess = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll")] static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
}
