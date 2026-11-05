using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XtremeDaw.App;

/// <summary>
/// XT-WorkBench, the Banco de pruebas, is a program of its own: <c>xtbench.exe [--lista lista.json] [--fondo]</c>
/// (src/XtremeDaw.Bench, asked by the user, #142: every build of XTreme used to close it, since it ran from a test copy
/// and moved to each newer one). <c>tools\probar.ps1</c> puts it in <c>pruebas\banco\&lt;fecha&gt;\</c>, a new folder only
/// when its build changed, and nothing closes one that is open: a newer bench is the one the next start takes.
/// <para>
/// This half is shared by XTreme (its button, and <c>XTreme.exe --banco</c> from older shortcuts) and the bench: one
/// bench runs at a time, a start while one is open brings it forward (<see cref="ShowEvent"/>). The bench's own half is
/// <c>BenchRun.cs</c> in that project.
/// </para>
/// </summary>
static partial class Bench
{
    /// <summary>What XTreme copies took before the bench was a program of its own; they still pass it on.</summary>
    public const string Arg = "--banco";
    const string ListArg = "--lista";
    /// <summary>Started by XTreme on its own (something is pending): it shows without the focus.</summary>
    const string QuietArg = "--fondo";
    /// <summary>Exists while a bench runs; set, the bench comes forward. Per user session (<c>Local\</c>).</summary>
    const string ShowEvent = @"Local\XTreme.Bench.Show";
    /// <summary>The bench that ran inside XTreme.exe (until #142) closes when this is set: the new one takes its place.</summary>
    const string OldQuitEvent = @"Local\XTreme.Banco.Quit";
    /// <summary>Next to the list: the bench's builds, one folder each.</summary>
    const string BenchDir = "banco";
    const string BenchExe = "xtbench.exe";
    /// <summary>Written last in a bench folder (the build's hash): a folder without it is still being copied.</summary>
    const string BenchMark = "firma.txt";
    /// <summary>Lets the process that sets <see cref="ShowEvent"/> hand the foreground to the bench.</summary>
    const int AnyProcess = -1;

    public static bool In(string[] args) => args.Contains(Arg);

    /// <summary>The list named by <c>--lista</c>, this copy's, or the repository's.</summary>
    public static string? ListFrom(string[] args) => ArgAfter(args, ListArg) ?? TestBuild.ListPath ?? FindList();

    public static bool Quiet(string[] args) => args.Contains(QuietArg);

    /// <summary>
    /// Brings the open bench forward (only if <paramref name="activate"/>) or starts the newest one on
    /// <paramref name="listPath"/>. Returns why it could not, or null.
    /// </summary>
    public static string? Open(string listPath, bool activate)
    {
        if (EventWaitHandle.TryOpenExisting(ShowEvent, out var open))
        {
            using (open)
            {
                if (activate)
                {
                    AllowSetForegroundWindow(AnyProcess);
                    open.Set();
                }
                return null;
            }
        }
        if (NewestBench(listPath) is not { } exe) return Loc.T("test.list.bench.missing");
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add(ListArg);
            start.ArgumentList.Add(Path.GetFullPath(listPath));
            if (!activate) start.ArgumentList.Add(QuietArg);
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return e.Message; }
    }

    /// <summary>The newest bench build's folder name ("20261105-111354"), or null: a Banco ticket is tried there.</summary>
    public static string? NewestName(string listPath) =>
        NewestBench(listPath) is { } exe ? Path.GetFileName(Path.GetDirectoryName(exe)) : null;

    /// <summary>The build this bench runs from, or null when this isn't a bench from <c>pruebas\banco\</c> (XTreme, bin\).</summary>
    public static readonly string? RunningBuild = OwnBuild();

    static string? OwnBuild()
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return File.Exists(Path.Combine(dir, BenchMark)) && File.Exists(Path.Combine(dir, BenchExe)) ? Path.GetFileName(dir) : null;
    }

    /// <summary>The newest finished bench next to the list (<c>pruebas\banco\*\xtbench.exe</c>), or null.</summary>
    static string? NewestBench(string listPath)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(listPath))!, BenchDir);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, BenchMark)) && File.Exists(Path.Combine(d, BenchExe)))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Select(d => Path.Combine(d, BenchExe))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? ArgAfter(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && args[i + 1].Length > 0 ? args[i + 1] : null;
    }

    /// <summary>Outside a test copy (bin\, or the bench's own folder): the repository's <c>pruebas\lista.json</c>, up from the exe.</summary>
    static string? FindList()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var list = Path.Combine(dir.FullName, "pruebas", "lista.json");
            if (File.Exists(list)) return list;
            if (dir.Name == "pruebas" && File.Exists(Path.Combine(dir.FullName, "lista.json"))) return Path.Combine(dir.FullName, "lista.json");
        }
        return null;
    }

    /// <summary>
    /// The newest finished XTreme copy next to the list (<c>prueba.txt</c> is written last): the one the person tries, so
    /// the bench says which build that is.
    /// </summary>
    static string? NewestCopy(string listPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(listPath));
            if (dir == null) return null;
            return Directory.EnumerateDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, "prueba.txt")) && File.Exists(Path.Combine(d, "XTreme.exe")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Max(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
