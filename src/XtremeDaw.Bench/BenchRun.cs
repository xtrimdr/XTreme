using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XtremeDaw.App.Dialogs;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The bench's own half of <see cref="Bench"/>: runs <see cref="TestListWindow"/> as the whole program. It reads the list
/// itself (a watcher, and every <see cref="Poll"/> as the fallback) and never closes by itself: a newer build of the bench
/// waits for the next start (asked by the user, #142).
/// </summary>
static partial class Bench
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    static readonly TimeSpan WatchSettle = TimeSpan.FromMilliseconds(120);

    /// <summary>Kept for the life of the process: an event lives as long as one handle to it does.</summary>
    static EventWaitHandle? show;

    /// <summary>Runs the bench; it ends when its window closes. A second start brings the open one forward and ends.</summary>
    public static void Run(IClassicDesktopStyleApplicationLifetime desktop)
    {
        string[] args = desktop.Args ?? [];
        string? path = ListFrom(args);
        show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent, out bool created);
        if (path == null || !created)
        {
            if (!created) show.Set();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(() => desktop.Shutdown(path == null ? 1 : 0));
            return;
        }
        // the bench that ran inside an XTreme copy leaves its place to this one
        if (EventWaitHandle.TryOpenExisting(OldQuitEvent, out var old))
            using (old) old.Set();
        // what is done here is the person's: never "Nuevo" to them, and the history says so
        TestList.Who = TestList.User;

        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        Open(desktop, path, null, !Quiet(args));

        new Thread(() =>
        {
            while (show.WaitOne())
                Dispatcher.UIThread.Post(() =>
                {
                    if (desktop.MainWindow is not { } w) return;
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Activate();
                });
        }) { IsBackground = true, Name = "XT-WorkBench: traer al frente" }.Start();
    }

    /// <summary>
    /// Builds the window, its reading of the list and its look picker, and shows it. A language picked there builds it
    /// again in that one (asked by the user, #151: at once, in the same place, keeping what was being written), as
    /// every text is read when a control is made.
    /// </summary>
    static TestListWindow Open(IClassicDesktopStyleApplicationLifetime desktop, string path, TestListWindow? old, bool activate)
    {
        var window = new TestListWindow(path) { ShowActivated = activate, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (old != null) window.TakeOver(old);
        else window.RestorePlace();
        window.AddLookPicker(code =>
        {
            Loc.Choose(code);
            Loc.Load();
            Open(desktop, path, window, true);
            window.Close();
        });
        desktop.MainWindow = window;

        DateTime stamp = default;
        // the agents' lock lights (#167) change without the list: they are read again when theirs change
        string? root = AgentLauncher.Root(path);
        long lights = LockLight.Stamp(root);
        // and so does a session that ends (#173): its tickets stop showing "Tomado"
        long alive = AgentSessions.Read(root).Stamp;
        void Read()
        {
            try
            {
                var at = File.GetLastWriteTimeUtc(path);
                // a list that cannot be understood keeps what is on the screen instead of showing it empty
                if (TestList.TryRead(path) is not { } d) return;
                stamp = at;
                window.Build = NewestCopy(path) ?? "";
                window.Fill(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        window.Changed += Read;
        var timer = new DispatcherTimer { Interval = Poll };
        timer.Tick += (_, _) =>
        {
            long now = LockLight.Stamp(root), running = AgentSessions.Read(root).Stamp;
            if (File.GetLastWriteTimeUtc(path) != stamp || now != lights || running != alive)
            {
                lights = now;
                alive = running;
                Read();
            }
        };
        timer.Start();
        var watcher = Watch(path, Read);
        // the rows drawn from code take the new colors with the next fill
        UiTheme.Changed += Read;
        window.Closed += (_, _) =>
        {
            timer.Stop();
            watcher?.Dispose();
            UiTheme.Changed -= Read;
        };

        Read();
        window.Show();
        return window;
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
}
