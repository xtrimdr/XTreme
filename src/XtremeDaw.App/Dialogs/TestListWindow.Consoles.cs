using Avalonia.Controls;
using XtremeDaw.App.Terminal;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The Claude consoles inside the bench (asked by the user, #278): "Abrir en una consola" starts the session in a
/// pseudo console of the bench instead of Windows Terminal, which stays as the way out when that cannot start. The
/// consoles live here, not in a window: the Agentes window shows one when its session is picked (#278: map, chat and
/// console together), and closing that window leaves them working. Closing the bench with sessions running asks for a
/// second close, since it ends them.
/// </summary>
public sealed partial class TestListWindow
{
    readonly List<ConsoleSession> benchConsoles = [];
    /// <summary>The person already closed once with sessions running: the next close ends them.</summary>
    bool closeConfirmed;

    int BenchRunning => benchConsoles.Count(c => c.Running);

    void InitConsoles()
    {
        Closing += (_, e) =>
        {
            int n = BenchRunning;
            if (n == 0 || closeConfirmed || e.IsProgrammatic) return;
            e.Cancel = true;
            closeConfirmed = true;
            ErrorText.Text = Loc.T("test.consoles.quit", n);
        };
        Closed += (_, _) =>
        {
            foreach (var c in benchConsoles) c.Dispose();
            benchConsoles.Clear();
        };
    }

    /// <summary>Starts <paramref name="prompt"/> in a console of the bench and shows it in Agentes; the reason when it could not.</summary>
    string? RunInBench(string prompt)
    {
        if (root == null) return null;
        var (session, why) = AgentLauncher.InBench(root, prompt);
        if (session == null) return why;
        benchConsoles.Add(session);
        closeConfirmed = false;
        session.Ended += _ =>
        {
            closeConfirmed = false;
            // #287: an ended session closes at once, however it ended: no half conversation left on screen
            DropConsole(session);
        };
        OpenAgents();
        PickSession(PendingId(session));
        return null;
    }

    /// <summary>Ends a console's program (if it still runs) and takes it out.</summary>
    void DropConsole(ConsoleSession c)
    {
        if (!benchConsoles.Remove(c)) return;
        if (agentsWindow?.Terminal.Child == c.View) agentsWindow.Terminal.Child = null;
        c.Dispose();
        if (consoleOf.Equals(ConsoleKey(c), StringComparison.OrdinalIgnoreCase)) consoleOf = "";
        if (current != null) FillAgents(current);
    }

    /// <summary>A session running in the bench's consoles shown in Agentes; false when it runs elsewhere.</summary>
    bool ShowInBench(int pid)
    {
        var c = benchConsoles.Find(x => x.Running && x.ProcessId == pid);
        if (c == null) return false;
        OpenAgents();
        PickSession(ConsoleKey(c));
        return true;
    }

    /// <summary>The tab of a console whose Claude has not said its session yet (it is starting).</summary>
    static string PendingId(ConsoleSession c) => "pid:" + c.ProcessId;

    /// <summary>The session a bench console runs, or its pending id until the hook writes it down.</summary>
    string ConsoleKey(ConsoleSession c) => sessionOfPid.TryGetValue(c.ProcessId, out var s) ? s : PendingId(c);

    /// <summary>The bench console that runs <paramref name="key"/> (a session or a pending id), if any.</summary>
    ConsoleSession? ConsoleOf(string key) =>
        key == "" ? null : benchConsoles.Find(c => ConsoleKey(c).Equals(key, StringComparison.OrdinalIgnoreCase) || PendingId(c).Equals(key, StringComparison.OrdinalIgnoreCase));
}
