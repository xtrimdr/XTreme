using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The "Banco de pruebas" list in a test copy (<see cref="TestList"/>): the button in the title bar with what is left to
/// try, and the bench itself, a program of its own (<see cref="Bench"/>, asked by the user: it also opens from outside
/// and outlives XTreme), which the button brings forward and which opens by itself when something is waiting. Agents
/// add to the list from other processes, so the file is watched for the counter and the toasts. The user's own
/// requests do not come back as a toast. Outside a test copy none of this runs.
/// </summary>
public partial class MainWindow
{
    static readonly TimeSpan TestListPoll = TimeSpan.FromSeconds(2);
    /// <summary><c>XTreme.exe [proyecto] --probar 12</c>: the bench's Probar (asked by the user, #143) opens the copy with that ticket's toast.</summary>
    const string TryArg = "--probar";

    DateTime testListStamp;
    int testListSeen = -1;

    /// <summary>The project on the command line: the first argument that is not an option (nor <see cref="TryArg"/>'s number).</summary>
    public static string? ProjectArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == TryArg) i++;
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && args[i].Length > 0) return args[i];
        }
        return null;
    }

    static int TryFrom(string[] args)
    {
        int i = Array.IndexOf(args, TryArg);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id) ? id : 0;
    }

    void InitTestList()
    {
        if (TestBuild.ListPath is not { } path) return;
        TestListButton.IsVisible = true;
        TestListButton.Click += (_, _) => OpenBench(path, activate: true);
        int tryId = TryFrom(Environment.GetCommandLineArgs());
        Opened += (_, _) =>
        {
            var d = ReadTestList(path);
            if (d != null && d.Count(TestItem.Pending) > 0) OpenBench(path, activate: false);
            if (tryId > 0 && d?.Items.Find(i => i.Id == tryId) is { } item) ShowTry(item, path);
        };
        var timer = new DispatcherTimer { Interval = TestListPoll };
        timer.Tick += (_, _) =>
        {
            if (File.GetLastWriteTimeUtc(path) != testListStamp) ReadTestList(path);
        };
        timer.Start();
    }

    /// <summary>
    /// What to try in the ticket the bench opened this copy for: the latest fix's words ("Arreglo: …"), or the ticket's
    /// own when it has none. It stays until closed, and its button brings the bench forward to give the verdict.
    /// </summary>
    void ShowTry(TestItem item, string path)
    {
        string text = item.Thread.LastOrDefault(m => m.Kind == TestMessage.Fix && !string.IsNullOrWhiteSpace(m.Text))?.Text ?? item.Text;
        string title = Loc.T("test.list.try.toast", item.Id, item.Title ?? "");
        Toast(text, title.TrimEnd(' ', '·'), sticky: true, (Loc.T("test.list.try.verdict"), () => OpenBench(path, activate: true, item.Id)));
    }

    void OpenBench(string path, bool activate, int ticket = 0)
    {
        if (Bench.Open(path, activate, ticket) is { } why) Toast(Loc.T("test.list.open.error", why), Loc.T("test.list.title"));
    }

    /// <summary>Reads the file and updates the button; null if it is being swapped (the next tick reads it).</summary>
    TestListData? ReadTestList(string path)
    {
        TestListData d;
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
            d = TestList.Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        testListStamp = stamp;

        int pending = d.Count(TestItem.Pending), fails = d.Count(TestItem.Fails) + d.Count(TestItem.WorksBadly) + d.Count(TestItem.Improve);
        TestListCount.Text = pending.ToString();
        ToolTip.SetTip(TestListButton, Loc.T("test.list.tip", pending, fails));
        if (testListSeen >= 0)
            foreach (var i in d.Items)
                if (i.Id > testListSeen && i.State != TestItem.Request) Toast(i.Text, Loc.T("test.list.title"));
        testListSeen = Math.Max(testListSeen, d.Next - 1);
        return d;
    }
}
