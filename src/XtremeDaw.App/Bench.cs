using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XtremeDaw.App.Dialogs;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// XT-WorkBench, the Banco de pruebas as a program of its own (asked by the user: it opens from XTreme and from outside,
/// and closing XTreme leaves it open): <c>XTreme.exe --banco [--lista lista.json] [--fondo]</c> starts only
/// <see cref="TestListWindow"/>, without the audio engine or the main window. It is the same exe, so it carries the
/// app's styles, texts and theme with nothing copied.
/// <para>
/// It is always the newest copy's (asked by the user: an open XTreme or an old bench used to start or keep an older one).
/// One runs at a time: a start from a copy no newer than the open bench brings that one forward (<see cref="ShowEvent"/>);
/// one from a newer copy closes it (<see cref="QuitEvent"/>) and takes its place. The open bench moves to a newer copy
/// by itself once one is built, while it is not in use. It reads the list itself, every <see cref="Poll"/>.
/// </para>
/// </summary>
static class Bench
{
    public const string Arg = "--banco";
    const string ListArg = "--lista";
    /// <summary>Started by XTreme on its own (something is pending), or moving to a newer copy: it shows without the focus.</summary>
    const string QuietArg = "--fondo";
    /// <summary>Exists while a bench runs; set, the bench comes forward. Per user session (<c>Local\</c>).</summary>
    const string ShowEvent = @"Local\XTreme.Banco.Show";
    /// <summary>Set by a bench from a newer copy: the open one closes (its notes saved) and leaves it the place.</summary>
    const string QuitEvent = @"Local\XTreme.Banco.Quit";
    /// <summary>Next to the list: the copy the open bench runs from, so a new start knows whether it is newer.</summary>
    const string RunningFile = "banco.txt";
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    /// <summary>How long a newer bench waits for the old one to close before giving up.</summary>
    static readonly TimeSpan TakeOverWait = TimeSpan.FromSeconds(10);
    /// <summary>Lets the process that sets <see cref="ShowEvent"/> hand the foreground to the bench.</summary>
    const int AnyProcess = -1;

    /// <summary>Kept for the life of the process: an event lives as long as one handle to it does.</summary>
    static EventWaitHandle? show, quit;

    public static bool In(string[] args) => args.Contains(Arg);

    /// <summary>
    /// From XTreme: brings the open bench forward (only if <paramref name="activate"/>) or starts the newest copy's on
    /// <paramref name="listPath"/>, which replaces an open one from an older copy. Returns why it could not, or null.
    /// </summary>
    public static string? Open(string listPath, bool activate)
    {
        string? newest = NewestCopy(listPath);
        if (EventWaitHandle.TryOpenExisting(ShowEvent, out var open))
        {
            using (open)
            {
                if (newest == null || Older(Running(listPath), newest) == false)
                {
                    if (activate)
                    {
                        AllowSetForegroundWindow(AnyProcess);
                        open.Set();
                    }
                    return null;
                }
            }
        }
        string exe = newest is { } copy ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(listPath))!, copy, "XTreme.exe") : "";
        return Start(File.Exists(exe) ? exe : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "XTreme.exe"), listPath, activate);
    }

    static string? Start(string exe, string listPath, bool activate)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory };
            start.ArgumentList.Add(Arg);
            start.ArgumentList.Add(ListArg);
            start.ArgumentList.Add(listPath);
            if (!activate) start.ArgumentList.Add(QuietArg);
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return e.Message; }
    }

    /// <summary>Runs the bench as the whole app; it ends when its window closes.</summary>
    public static void Run(IClassicDesktopStyleApplicationLifetime desktop)
    {
        string[] args = desktop.Args ?? [];
        string? path = ArgAfter(args, ListArg) ?? TestBuild.ListPath ?? FindList();
        string mine = TestBuild.CopyName ?? "";
        if (path == null || !TakePlace(path, mine))
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(() => desktop.Shutdown(path == null ? 1 : 0));
            return;
        }
        WriteRunning(path, mine);
        // what is done here is the person's: never "Nuevo" to them, and the history says so
        TestList.Who = TestList.User;

        var window = new TestListWindow(path)
        {
            ShowActivated = !args.Contains(QuietArg), WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.RestorePlace();
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        DateTime stamp = default;
        string? movedTo = null;
        void Read()
        {
            try
            {
                var at = File.GetLastWriteTimeUtc(path);
                // a list that cannot be understood keeps what is on the screen instead of showing it empty
                if (TestList.TryRead(path) is not { } d) return;
                stamp = at;
                window.Build = NewestCopy(path) ?? mine;
                window.Fill(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        window.Changed += Read;
        var timer = new DispatcherTimer { Interval = Poll };
        timer.Tick += (_, _) =>
        {
            if (File.GetLastWriteTimeUtc(path) != stamp) Read();
            // a newer copy was built: move to it, not while the person is on the window (a note being written)
            if (!window.IsActive && NewestCopy(path) is { } newest && Older(mine, newest) == true && newest != movedTo)
            {
                movedTo = newest;
                string exe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, newest, "XTreme.exe");
                if (File.Exists(exe)) Start(exe, path, activate: false);
            }
        };
        timer.Start();
        var watcher = Watch(path, Read);
        window.Closed += (_, _) =>
        {
            timer.Stop();
            watcher?.Dispose();
        };

        Listen(show!, () =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }, "XT-WorkBench: traer al frente");
        Listen(quit!, window.Close, "XT-WorkBench: dejar el lugar");

        Read();
        window.Show();
    }

    /// <summary>
    /// Becomes the one bench: the place is free, or held by an older copy, which is asked to close. A start from a copy
    /// no newer than the open one brings that one forward instead, and returns false.
    /// </summary>
    static bool TakePlace(string listPath, string mine)
    {
        show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent, out bool created);
        if (created)
        {
            quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
            return true;
        }
        if (Older(Running(listPath), mine) != true)
        {
            show.Set();
            show.Dispose();
            return false;
        }
        show.Dispose();
        if (EventWaitHandle.TryOpenExisting(QuitEvent, out var old))
            using (old) old.Set();
        // the event is gone once the old process let go of it
        var until = DateTime.UtcNow + TakeOverWait;
        while (DateTime.UtcNow < until)
        {
            if (!EventWaitHandle.TryOpenExisting(ShowEvent, out var still))
            {
                show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent, out created);
                if (!created)
                {
                    show.Dispose();
                    return false;
                }
                quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
                return true;
            }
            still.Dispose();
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// What an agent writes shows at once (asked by the user), not at the next <see cref="Poll"/>, which stays as the
    /// fallback (a watcher can miss events on a busy disk). A swap fires a few events: they wait <see cref="WatchSettle"/>
    /// and read once.
    /// </summary>
    static FileSystemWatcher? Watch(string path, Action read)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            var settle = new DispatcherTimer { Interval = WatchSettle };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                read();
            };
            void Kick(object? _, FileSystemEventArgs __) => Dispatcher.UIThread.Post(() =>
            {
                settle.Stop();
                settle.Start();
            });
            watcher.Changed += Kick;
            watcher.Created += Kick;
            watcher.Renamed += (s, e) => Kick(s, e);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    static readonly TimeSpan WatchSettle = TimeSpan.FromMilliseconds(120);

    static void Listen(EventWaitHandle e, Action act, string name)
    {
        new Thread(() =>
        {
            while (e.WaitOne()) Dispatcher.UIThread.Post(act);
        }) { IsBackground = true, Name = name }.Start();
    }

    /// <summary>Whether copy <paramref name="a"/> is older than <paramref name="b"/> (names start with the date); null if unknown.</summary>
    static bool? Older(string? a, string b) => a == null ? null : string.CompareOrdinal(a, b) < 0;

    static string? Running(string listPath)
    {
        try { return File.ReadAllText(RunningPath(listPath)).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static void WriteRunning(string listPath, string copy)
    {
        try { File.WriteAllText(RunningPath(listPath), copy); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string RunningPath(string listPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(listPath))!, RunningFile);

    static string? ArgAfter(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && args[i + 1].Length > 0 ? args[i + 1] : null;
    }

    /// <summary>Outside a test copy (run from bin\ by hand): the repository's <c>pruebas\lista.json</c>, up from the exe.</summary>
    static string? FindList()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var list = Path.Combine(dir.FullName, "pruebas", "lista.json");
            if (File.Exists(list)) return list;
        }
        return null;
    }

    /// <summary>
    /// The newest finished copy next to the list (<c>prueba.txt</c> is written last): the one the person tries, so an item
    /// made after the bench's own copy is not "missing" in it, and the one a bench is started from.
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
