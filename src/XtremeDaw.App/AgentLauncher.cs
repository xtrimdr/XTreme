using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace XtremeDaw.App;

/// <summary>
/// Calls the agents from the Banco de pruebas (asked by the user): "Revisar" and the skill buttons open an interactive
/// session in the repository, never a background one (decisión del usuario): the skill asks before design decisions,
/// and the person answers there. Each action has two buttons (pedido del usuario, 2026-11-04): a new session of the
/// Claude desktop app by its documented link, which prefills the message and asks to confirm the folder, so the person
/// sends it; or a console with Claude Code, which starts by itself. Without the app's link registered, both use the console.
/// </summary>
static class AgentLauncher
{
    public const string ReviewSkill = "xt_revisar-workbench";
    /// <summary>The desktop app's link: a new Code session with its folder and the message prefilled (never sent).</summary>
    const string DesktopLink = "claude://code/new";

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
    /// Returns why it could not start, or null.
    /// </summary>
    public static string? OpenApp(string root)
    {
        var script = Path.Combine(root, "tools", "probar.ps1");
        if (!File.Exists(script)) return script;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
        };
        foreach (var a in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, "-Abrir", "-Acceso"])
            start.ArgumentList.Add(a);
        try
        {
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return e.Message; }
    }

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
    /// once. The prompt goes through cmd's command line: quotes, percent signs and newlines (and Windows Terminal's
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
        start.ArgumentList.Add(prompt);
        start.UseShellExecute = false;
        start.WorkingDirectory = root;
        DropHostSession(start);
        try
        {
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return e.Message; }
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
}
