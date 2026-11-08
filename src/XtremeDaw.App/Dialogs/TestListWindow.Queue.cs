using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The most agents at once (asked by the user, #318): a launch past that waits in a queue, shown as "En cola" on its
/// tickets and as a dimmed tab in Agentes, and starts when an agent ends its turn. The sessions at work count (see
/// <see cref="ActiveAgents"/>), plus the ones starting, so one that is starting is never counted out. The limit is
/// the slider in the footer, saved in <c>config.json</c>; the queue lives while the bench is open.
/// </summary>
public sealed partial class TestListWindow
{
    const string MaxAgentsKey = "benchMaxAgents";
    const int MaxAgentsDefault = 4, MaxAgentsMin = 1, MaxAgentsMax = 10;
    /// <summary>How often the queue looks for a free place: an agent that ends shows up only in the hook's files.</summary>
    static readonly TimeSpan QueuePoll = TimeSpan.FromSeconds(3);

    /// <summary>The launches waiting for a place, in the order they came; <c>Key</c> names its tab in Agentes.</summary>
    readonly List<(string Prompt, bool Desktop, List<int> Ids, string Key)> queue = [];
    int queueKeys;
    /// <summary>What a queued launch's tab in Agentes is called by (no session has that form).</summary>
    const string QueueKeyPrefix = "cola:";
    /// <summary>A launch counts as an agent this long after it starts, until its session shows up working.</summary>
    static readonly TimeSpan LaunchCounts = TimeSpan.FromSeconds(30);
    /// <summary>The launches started from the queue or the list, by when: counted until their session shows up.</summary>
    readonly List<DateTime> launchedAt = [];
    int liveAtLaunch;

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

    /// <summary>
    /// The agents working now (#318, 2nd try: counting every open Claude, the queue never started, since a session that
    /// ended its turn stays open, and so does the person's own). A session counts while it works or waits for a
    /// permission or an answer mid-task; one whose turn ended does not. Plus the bench consoles whose session is not
    /// known yet, and the launches of the last moments whose session has not shown up (a Windows Terminal or the app).
    /// </summary>
    int ActiveAgents()
    {
        if (root == null) return 0;
        MapProcesses();
        var live = AgentAlert.Live(root);
        int working = live.Count(s => AgentTranscript.For(s).Waiting(root) != AgentTranscript.Wait.Turn);
        int consoles = benchConsoles.Count(c => c.Running && !sessionOfPid.ContainsKey(c.ProcessId));
        launchedAt.RemoveAll(t => DateTime.UtcNow - t >= LaunchCounts);
        // a launch is in the count once a new session shows up, whatever it is doing
        int fresh = Math.Max(0, launchedAt.Count - Math.Max(0, live.Count - liveAtLaunch));
        return working + Math.Max(consoles, fresh);
    }

    /// <summary>Notes a launch: it counts as an agent until its session shows up.</summary>
    void Launched()
    {
        if (launchedAt.Count == 0) liveAtLaunch = AgentAlert.Live(root).Count;
        launchedAt.Add(DateTime.UtcNow);
    }

    void Enqueue(string prompt, bool desktop, IReadOnlyList<TestItem> items)
    {
        queue.Add((prompt, desktop, items.Select(i => i.Id).ToList(), QueueKeyPrefix + (++queueKeys).ToString(CultureInfo.InvariantCulture)));
        ErrorText.Text = Loc.T("test.list.queued.added", MaxAgents);
        Refill();
    }

    /// <summary>How Agentes names a queued launch: "En cola · " and its ticket, or the start of its prompt.</summary>
    string QueueName((string Prompt, bool Desktop, List<int> Ids, string Key) q)
    {
        string what = q.Ids.Count > 0 && current?.Items.Find(i => i.Id == q.Ids[0]) is { } item
            ? $"#{item.Id} · {Subject(item)}" + (q.Ids.Count > 1 ? $" +{q.Ids.Count - 1}" : "")
            : q.Prompt.Length > 40 ? q.Prompt[..39] + "…" : q.Prompt;
        return $"{Loc.T("test.list.queued")} · {what}";
    }

    bool IsQueueKey(string key) => key.StartsWith(QueueKeyPrefix, StringComparison.Ordinal);

    /// <summary>Starts the queued launches while there is room; drops one whose tickets all went to another agent.</summary>
    void PumpQueue()
    {
        if (queue.Count == 0 || root == null) return;
        bool changed = false;
        while (queue.Count > 0)
        {
            var (prompt, desktop, ids, _) = queue[0];
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
