using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The most agents at once (asked by the user, #318): a launch past that waits in a queue, shown as "En cola" on its
/// tickets, and starts when an agent ends. Open sessions count (every Claude of the project the hook knows), plus the
/// bench consoles whose session is not written down yet, so one that is starting is never counted out. The limit is
/// the slider in the footer, saved in <c>config.json</c>; the queue lives while the bench is open.
/// </summary>
public sealed partial class TestListWindow
{
    const string MaxAgentsKey = "benchMaxAgents";
    const int MaxAgentsDefault = 4, MaxAgentsMin = 1, MaxAgentsMax = 10;
    /// <summary>How often the queue looks for a free place: an agent that ends shows up only in the hook's files.</summary>
    static readonly TimeSpan QueuePoll = TimeSpan.FromSeconds(3);

    /// <summary>The launches waiting for a place, in the order they came.</summary>
    readonly List<(string Prompt, bool Desktop, List<int> Ids)> queue = [];

    int MaxAgents => (int)Math.Round(MaxAgentsSlider.Value);

    void InitQueue()
    {
        int max = MaxAgentsDefault;
        try
        {
            if (int.TryParse(AppConfig.Get(MaxAgentsKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
                max = Math.Clamp(m, MaxAgentsMin, MaxAgentsMax);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        MaxAgentsSlider.Minimum = MaxAgentsMin;
        MaxAgentsSlider.Maximum = MaxAgentsMax;
        MaxAgentsSlider.Value = max;
        MaxAgentsText.Text = max.ToString(CultureInfo.CurrentCulture);
        var save = new DispatcherTimer { Interval = ZoomSaveDelay };
        save.Tick += (_, _) =>
        {
            save.Stop();
            try { AppConfig.Set(MaxAgentsKey, MaxAgents.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preference only. */ }
        };
        MaxAgentsSlider.ValueChanged += (_, _) =>
        {
            MaxAgentsText.Text = MaxAgents.ToString(CultureInfo.CurrentCulture);
            save.Stop();
            save.Start();
            PumpQueue();
        };
        MaxAgentsSlider.DoubleTapped += (_, _) => MaxAgentsSlider.Value = MaxAgentsDefault;
        ToolTip.SetTip(MaxAgentsText, Loc.T("test.list.agents.max.tip"));
        ToolTip.SetTip(MaxAgentsIcon, Loc.T("test.list.agents.max.tip"));
        MaxAgentsPanel.IsVisible = root != null;

        var poll = new DispatcherTimer { Interval = QueuePoll };
        poll.Tick += (_, _) => PumpQueue();
        poll.Start();
        Closed += (_, _) => poll.Stop();
    }

    /// <summary>The agents running now: the open sessions, and the bench consoles still starting.</summary>
    int ActiveAgents()
    {
        if (root == null) return 0;
        var live = AgentAlert.Live(root);
        return live.Count + benchConsoles.Count(c => c.Running && !sessionOfPid.ContainsKey(c.ProcessId));
    }

    void Enqueue(string prompt, bool desktop, IReadOnlyList<TestItem> items)
    {
        queue.Add((prompt, desktop, items.Select(i => i.Id).ToList()));
        ErrorText.Text = Loc.T("test.list.queued.added", MaxAgents);
        Refill();
    }

    /// <summary>Starts the queued launches while there is room; drops one whose tickets all went to another agent.</summary>
    void PumpQueue()
    {
        if (queue.Count == 0 || root == null) return;
        bool changed = false;
        while (queue.Count > 0)
        {
            var (prompt, desktop, ids) = queue[0];
            var items = ids.Select(id => current?.Items.Find(i => i.Id == id)).OfType<TestItem>().ToList();
            // gone from the list, or every ticket taken meanwhile: nothing left for it to do
            if (ids.Count > 0 && (items.Count == 0 || items.All(i => TestList.Held(i, sessions))))
            {
                queue.RemoveAt(0);
                changed = true;
                continue;
            }
            if (ActiveAgents() >= MaxAgents) break;
            queue.RemoveAt(0);
            changed = true;
            // one per look: a Windows Terminal launch counts only once its session shows up
            Launch(prompt, desktop, items);
            break;
        }
        if (changed) Refill();
    }

    /// <summary>A launch for <paramref name="item"/> waits in the queue.</summary>
    bool Queued(TestItem item) => queue.Exists(q => q.Ids.Contains(item.Id));

    bool Queued(string prompt) => queue.Exists(q => q.Prompt == prompt);
}
