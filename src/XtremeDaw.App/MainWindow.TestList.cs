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

    DateTime testListStamp;
    int testListSeen = -1;

    void InitTestList()
    {
        if (TestBuild.ListPath is not { } path) return;
        TestListButton.IsVisible = true;
        TestListButton.Click += (_, _) => OpenBench(path, activate: true);
        Opened += (_, _) =>
        {
            if (ReadTestList(path) is { } d && d.Count(TestItem.Pending) > 0) OpenBench(path, activate: false);
        };
        var timer = new DispatcherTimer { Interval = TestListPoll };
        timer.Tick += (_, _) =>
        {
            if (File.GetLastWriteTimeUtc(path) != testListStamp) ReadTestList(path);
        };
        timer.Start();
    }

    void OpenBench(string path, bool activate)
    {
        if (Bench.Open(path, activate) is { } why) Toast(Loc.T("test.list.open.error", why), Loc.T("test.list.title"));
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
