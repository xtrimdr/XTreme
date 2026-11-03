using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.App.Dialogs;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The "Para probar" list in a test copy (<see cref="TestList"/>): the button in the title bar with what is left to
/// try, and <see cref="TestListWindow"/>, which opens by itself when something is waiting. Agents add to the list from
/// other processes, so the file is watched for as long as the window is open. The list is a window of its own, with its
/// button in the taskbar (asked by the user), like the panels' windows. Outside a test copy none of this runs.
/// </summary>
public partial class MainWindow
{
    static readonly TimeSpan TestListPoll = TimeSpan.FromSeconds(2);
    /// <summary>Space between the panel and the corner of the window, in logical pixels.</summary>
    const int TestPanelGapX = 16, TestPanelGapY = 96;

    TestListWindow? testPanel;
    PixelPoint? testPanelPlace;
    DateTime testListStamp;
    int testListSeen = -1;

    void InitTestList()
    {
        if (TestBuild.ListPath is not { } path) return;
        TestListButton.IsVisible = true;
        TestListButton.Click += (_, _) => ToggleTestPanel(path, activate: true);
        Opened += (_, _) =>
        {
            if (ReadTestList(path) is { } d && d.Count(TestItem.Pending) > 0) ToggleTestPanel(path, activate: false);
        };
        var timer = new DispatcherTimer { Interval = TestListPoll };
        timer.Tick += (_, _) =>
        {
            if (File.GetLastWriteTimeUtc(path) != testListStamp) ReadTestList(path);
        };
        timer.Start();
        // its own window (with its taskbar button, asked by the user): it closes with the main one
        Closed += (_, _) => testPanel?.Close();
    }

    /// <summary>Reads the file and updates the button and the panel; null if it is being swapped (the next tick reads it).</summary>
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
                if (i.Id > testListSeen) Toast(i.Text, Loc.T("test.list.title"));
        testListSeen = Math.Max(testListSeen, d.Next - 1);
        testPanel?.Fill(d);
        return d;
    }

    void ToggleTestPanel(string path, bool activate)
    {
        if (testPanel != null)
        {
            testPanel.Close();
            return;
        }
        var panel = new TestListWindow(path) { ShowActivated = activate };
        double scale = RenderScaling;
        panel.Position = testPanelPlace ?? new PixelPoint(
            Position.X + (int)((Bounds.Width - panel.Width - TestPanelGapX) * scale),
            Position.Y + (int)(TestPanelGapY * scale));
        panel.Changed += () => ReadTestList(path);
        panel.Closing += (_, _) => testPanelPlace = panel.Position;
        panel.Closed += (_, _) => testPanel = null;
        testPanel = panel;
        ReadTestList(path);
        panel.Show();
    }
}
