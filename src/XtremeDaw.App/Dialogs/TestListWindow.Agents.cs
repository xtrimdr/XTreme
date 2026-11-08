using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The bench's Agentes window (asked by the user, #269: lost in how to sort out a jam between agents; #273: a map, in a
/// window of its own). The header's button opens <see cref="AgentsWindow"/>, and each fill of the list draws it again:
/// the map of the open sessions and the files they hold or wait for (<see cref="AgentMap"/>), the picked session's
/// console, and a box to write to one session or all of them as the person (<see cref="AgentAlert.Send"/>). The box is built once
/// and never redrawn, so a fill never takes it away while typing.
/// </summary>
public sealed partial class TestListWindow
{
    AgentsWindow? agentsWindow;
    TextBox? agentBox;
    TextBlock? agentToLabel;
    Button? agentTarget, agentSend;
    /// <summary>Whom the box writes to: a session, or "" = every open one.</summary>
    string agentTo = "";
    /// <summary>The sessions open at the last fill: "" sends to these.</summary>
    List<string> agentLive = [];
    /// <summary>The session whose console is shown (#278), "" = none picked yet.</summary>
    string consoleOf = "";
    /// <summary>Where the console is drawn up to: lines dropped by the transcript plus lines shown.</summary>
    int consoleShown, consoleDropped;
    /// <summary>Each live session's wait at the last fill: a change draws the map again.</summary>
    readonly Dictionary<string, AgentTranscript.Wait> waits = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each live session's last tool at the last fill (#278: the map says what it is doing): a change draws it again.</summary>
    readonly Dictionary<string, string> doing = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The session of each running Claude, from the hook's notes: how a bench console finds its session.</summary>
    readonly Dictionary<int, string> sessionOfPid = [];
    /// <summary>The console is drawn at this level (#278); another one draws it again whole.</summary>
    int consoleLevel = -1;
    /// <summary>The person's messages to the shown session already in its console (#283): the hook's alerts never reach the transcript.</summary>
    readonly HashSet<string> consoleSent = new(StringComparer.Ordinal);
    /// <summary>Those still unread, with the line's state: each fill asks the hook's files again.</summary>
    readonly List<(AgentAlert Alert, TextBlock State)> consoleUnread = [];
    /// <summary>Something was sent, or the console is drawn again: its messages are read from the log at the next fill.</summary>
    bool sentPending = true;
    /// <summary>The console's "nothing yet" line, gone when the first one comes.</summary>
    Control? consoleNone;

    void InitAgents()
    {
        ToolTip.SetTip(AgentsButton, Loc.T("test.list.agents.open.tip"));
        AgentsButton.IsVisible = root != null;
        AgentsButton.Click += (_, _) => OpenAgents();
        AgentsBigButton.Click += (_, _) => OpenAgents();
        Closed += (_, _) => agentsWindow?.Close();
    }

    void OpenAgents()
    {
        if (agentsWindow != null)
        {
            if (agentsWindow.WindowState == WindowState.Minimized) agentsWindow.WindowState = WindowState.Normal;
            agentsWindow.Activate();
            return;
        }
        var w = new AgentsWindow();
        w.Map.Empty = Loc.T("test.list.agents.none");
        w.Map.SessionClicked += PickSession;
        w.SessionPicked += PickSession;
        w.Following += FollowAgents;
        w.LevelChanged += FillConsole;
        w.Ended += () =>
        {
            if (ConsoleOf(consoleOf) is { } c) DropConsole(c);
            else if (PidOf(consoleOf) is { } pid) EndSession(pid);
        };
        w.Map.FileClicked += (file, owner) =>
        {
            var ask = new MenuItem { Header = Loc.T("test.list.agents.ask.tip") };
            ask.Click += (_, _) => AgentSent(AgentAlert.Send(root, owner, "pedido",
                // for the agent, in the hook's words (-PideArchivo): agents read Spanish
                $"El usuario necesita {file}. Cuando termines con él, soltalo: tools\\bloqueos.ps1 -SueltaArchivo {file} (o terminá tu turno).", file));
            var menu = new MenuFlyout();
            menu.Items.Add(ask);
            menu.ShowAt(w.Map, showAtPointer: true);
        };
        w.Composer.Child = AgentComposer();
        w.Closed += (_, _) =>
        {
            agentsWindow = null;
            consoleOf = "";
            consoleLevel = -1;
            waits.Clear();
            doing.Clear();
            // the bench console shown goes on working without a window
            w.Terminal.Child = null;
            // the box goes with the window; a new one is built when it opens again
            agentBox = null;
        };
        agentsWindow = w;
        if (current != null) FillAgents(current);
        w.Show();
        // #278: it opens on a session: the one that waits for the person, or the first
        var live = AgentAlert.Live(root);
        string first = live.FirstOrDefault(s => AgentTranscript.For(s).Waiting(root) != AgentTranscript.Wait.None) ?? live.FirstOrDefault() ?? "";
        if (first != "") PickSession(first);
    }

    /// <summary>#278: a node or its tab, the same: the box writes to it and its console shows below.</summary>
    void PickSession(string session)
    {
        agentTo = session;
        if (current != null) UpdateComposer(current.Items);
        OpenConsole(session);
        if (agentsWindow is { TerminalOn: false }) agentBox?.Focus();
    }

    /// <summary>Reads which session each running Claude is; false when nothing changed.</summary>
    bool MapProcesses()
    {
        if (root == null) return false;
        var now = AgentLauncher.LiveProcesses(root);
        bool changed = now.Count != sessionOfPid.Count;
        if (!changed)
            foreach (var (s, pid) in now)
                if (!sessionOfPid.TryGetValue(pid, out var had) || !had.Equals(s, StringComparison.OrdinalIgnoreCase)) { changed = true; break; }
        if (!changed) return false;
        sessionOfPid.Clear();
        foreach (var (s, pid) in now) sessionOfPid[pid] = s;
        return true;
    }

    /// <summary>The Claude process of an open session, null when it is not known (an old hook, a session that closed).</summary>
    int? PidOf(string session)
    {
        foreach (var (pid, s) in sessionOfPid)
            if (s.Equals(session, StringComparison.OrdinalIgnoreCase)) return pid;
        return null;
    }

    /// <summary>
    /// Ends a session that runs outside the bench's consoles (#294): its Claude process goes, so the hook frees its
    /// files at once (each lock keeps its process) and it leaves the map at the next fill.
    /// </summary>
    void EndSession(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (Exception x) when (x is ArgumentException or InvalidOperationException or Win32Exception) { }
        sessionOfPid.Remove(pid);
        if (current != null) FillAgents(current);
    }

    /// <summary>What a working session is doing now (#278): its last tool, while it is not waiting; "" otherwise.</summary>
    static string DoingOf(string session, AgentTranscript.Wait wait)
    {
        if (wait != AgentTranscript.Wait.None) return "";
        var t = AgentTranscript.For(session);
        if (t.Entries.Count == 0) return "";
        var e = t.Entries[^1];
        if (e.Kind == AgentTranscript.Kind.User) return Loc.T("test.list.agents.doing.thinking");
        if (e.Kind != AgentTranscript.Kind.Tool) return Loc.T("test.list.agents.doing.writing");
        return e.Text == "" ? e.Tool : $"{e.Tool} · {e.Text}";
    }

    /// <summary>Draws the Agentes window, when it is open, and the count on the header's button.</summary>
    void FillAgents(TestListData data)
    {
        var live = AgentAlert.Live(root);
        AgentsCount.Text = live.Count.ToString(CultureInfo.CurrentCulture);
        AgentsCount.IsVisible = live.Count > 0;
        AgentsBigButton.IsVisible = root != null;
        int awaiting = live.Count(s => AgentTranscript.For(s).Waiting(root) != AgentTranscript.Wait.None);
        AgentsBigText.Text = awaiting > 0 ? Loc.T("test.list.agents.big.waiting", live.Count, awaiting) : Loc.T("test.list.agents.big", live.Count);
        if (queue.Count > 0) AgentsBigText.Text += " " + Loc.T("test.list.agents.big.queued", queue.Count);
        if (agentsWindow is not { } w) return;
        var held = LockLight.Held(root);
        var snags = LockLight.Snags(root);

        // the open sessions, and one that holds files without its Claude written down (an old hook): still somebody's
        var ids = new List<string>(live);
        foreach (var h in held)
            if (!ids.Exists(s => s.Equals(h.Session, StringComparison.OrdinalIgnoreCase))) ids.Add(h.Session);
        var shown = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        snags = snags.Where(n => shown.Contains(n.Session)).ToList();

        MapProcesses();
        // a bench console whose session is now known: its tab becomes the session's
        if (consoleOf.StartsWith("pid:", StringComparison.Ordinal)
            && int.TryParse(consoleOf.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpid)
            && sessionOfPid.TryGetValue(cpid, out var known))
        {
            consoleOf = known;
            if (agentTo.Equals("pid:" + cpid, StringComparison.Ordinal)) agentTo = known;
            w.Select(known);
        }
        // #287: a session that ended closes at once, its tab and its console, also the one shown
        foreach (var c in benchConsoles.Where(c => c.IsEnded).ToList())
        {
            benchConsoles.Remove(c);
            if (w.Terminal.Child == c.View) w.Terminal.Child = null;
            c.Dispose();
        }
        if (consoleOf == "" ? w.ConsoleOn : !shown.Contains(consoleOf) && ConsoleOf(consoleOf) == null)
        {
            // the one shown goes on to another open session (the one that waits for the person first), or to none
            string next = live.FirstOrDefault(s => AgentTranscript.For(s).Waiting(root) != AgentTranscript.Wait.None) ?? live.FirstOrDefault() ?? "";
            bool on = w.ConsoleOn;
            consoleOf = next;
            consoleLevel = -1;
            if (agentTo != "" && !shown.Contains(agentTo)) agentTo = next;
            w.Select(next);
            w.ShowConsole(on && next != "");
        }

        waits.Clear();
        doing.Clear();
        var sessionsOnMap = ids.Select(id =>
        {
            var light = lights.TryGetValue(id, out var l) ? l : LockLight.Free;
            int files = held.Count(h => h.Session.Equals(id, StringComparison.OrdinalIgnoreCase));
            var wait = WaitOf(id);
            doing[id] = DoingOf(id, wait);
            // #305: the node says only how many files the session is editing; its state shows in its color
            // #315: the model and effort beside the file count
            string status = AgentMap.Files(files);
            if (models.TryGetValue(id, out var model)) status += " · " + model;
            return new AgentMap.Session(id, SessionName(data.Items, id), light.State, status, wait != AgentTranscript.Wait.None);
        }).ToList();
        // #305: only the files with a conflict (a wait or a clash) get a node, with the line from whoever holds them
        var files = new List<AgentMap.File>();
        foreach (var n in snags)
        {
            if (n.Path == "" || files.Exists(f => f.Path.Equals(n.Path, StringComparison.OrdinalIgnoreCase))) continue;
            int at = held.FindIndex(x => x.Path.Equals(n.Path, StringComparison.OrdinalIgnoreCase));
            files.Add(at >= 0 ? new AgentMap.File(held[at].Path, held[at].Session, held[at].Owner) : new AgentMap.File(n.Path, "", ""));
        }
        w.Map.Show(new AgentMap.Snapshot(sessionsOnMap, files, snags));
        w.Count.Text = live.Count.ToString(CultureInfo.CurrentCulture);

        agentLive = live;
        UpdateComposer(data.Items);
        // #278: a tab per open session, always at hand; #287: one that closed is gone
        var tabs = sessionsOnMap.Select(s => (s.Id, s.Name, s.Awaiting, true)).ToList();
        // a bench console still starting has no session yet: its tab goes by its prompt
        foreach (var c in benchConsoles)
        {
            string key = ConsoleKey(c);
            if (!shown.Contains(key)) tabs.Add((key, c.Title, false, c.Running));
        }
        w.SetSessions(tabs);
        FillConsole();
    }

    /// <summary>What a live session waits from the person, read from its transcript (#278), and kept for <see cref="FollowAgents"/>.</summary>
    AgentTranscript.Wait WaitOf(string session)
    {
        var wait = AgentTranscript.For(session).Waiting(root);
        waits[session] = wait;
        return wait;
    }

    /// <summary>Every couple of seconds while the window is open: the console's new lines, and the map again when a session starts or stops waiting.</summary>
    void FollowAgents()
    {
        if (current == null || agentsWindow == null) return;
        bool again = benchConsoles.Exists(c => !sessionOfPid.ContainsKey(c.ProcessId)) && MapProcesses();
        foreach (var (session, had) in waits)
        {
            if (again) break;
            var wait = AgentTranscript.For(session).Waiting(root);
            again = wait != had || (doing.TryGetValue(session, out var was) && was != DoingOf(session, wait));
        }
        if (again) FillAgents(current);
        else FillConsole();
    }

    /// <summary>Points the console at a session and shows it.</summary>
    void OpenConsole(string session)
    {
        if (agentsWindow is not { } w) return;
        if (!session.Equals(consoleOf, StringComparison.OrdinalIgnoreCase))
        {
            consoleOf = session;
            consoleLevel = -1;
        }
        w.Select(session);
        if (current != null) FillAgents(current);
        w.ShowConsole(true);
        FillConsole();
    }

    /// <summary>
    /// The picked session's console at the window's level (#278). Todo shows the real terminal of a session that runs in
    /// the bench; so does any level while it asks a permission or a question there, since the answer is typed in it.
    /// Otherwise the conversation read from the transcript: Normal with a line per tool, Resumen with the messages
    /// only and Claude's cut to their start. Only the lines that came since the last draw are added, so following a
    /// long session costs nothing; drawn again whole when the level changes or the transcript dropped lines from the front.
    /// </summary>
    void FillConsole()
    {
        if (agentsWindow is not { } w || consoleOf == "") return;
        var bench = ConsoleOf(consoleOf);
        bool pending = consoleOf.StartsWith("pid:", StringComparison.Ordinal);
        var wait = pending ? AgentTranscript.Wait.None : waits.TryGetValue(consoleOf, out var wt) ? wt : AgentTranscript.For(consoleOf).Waiting(root);
        bool real = bench != null && (pending || w.Level == 2 || wait is AgentTranscript.Wait.Permission or AgentTranscript.Wait.Question);
        w.Endable = bench is { Running: true } || PidOf(consoleOf) != null;
        w.ShowTerminal(real ? bench!.View : null);
        if (real || pending) return;

        var t = AgentTranscript.For(consoleOf);
        bool whole = t.Dropped != consoleDropped || w.Level != consoleLevel;
        if (whole)
        {
            w.Console.Children.Clear();
            consoleNone = null;
            consoleDropped = t.Dropped;
            consoleShown = t.Dropped;
            consoleLevel = w.Level;
            consoleSent.Clear();
            consoleUnread.Clear();
            sentPending = true;
        }
        // #283: the person's messages to it, from the log, in time order with the transcript's lines
        bool sentNow = sentPending && !whole;
        var sent = sentPending ? SentTo(consoleOf) : [];
        sentPending = false;
        UpdateSentStates();
        int from = Math.Max(0, consoleShown - t.Dropped);
        if (from >= t.Entries.Count && sent.Count == 0)
        {
            if (w.Console.Children.Count == 0)
                w.Console.Children.Add(consoleNone = new TextBlock { Classes = { "dim" }, Text = Loc.T("test.list.agents.console.none"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (consoleNone != null)
        {
            w.Console.Children.Remove(consoleNone);
            consoleNone = null;
        }
        var sv = w.ConsoleScroller;
        bool atEnd = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 24;
        bool brief = w.Level == 0;
        int j = 0;
        for (int i = from; i < t.Entries.Count; i++)
        {
            var e = t.Entries[i];
            while (j < sent.Count && sent[j].At <= e.At) AddSent(w, sent[j++]);
            if (brief && e.Kind == AgentTranscript.Kind.Tool) continue;
            w.Console.Children.Add(ConsoleLine(e, brief));
        }
        while (j < sent.Count) AddSent(w, sent[j++]);
        consoleShown = t.Dropped + t.Entries.Count;
        // a new line follows the end, unless the person scrolled up to read; what they just sent, always
        if (atEnd || whole || sentNow) Dispatcher.UIThread.Post(sv.ScrollToEnd, DispatcherPriority.Background);
    }

    /// <summary>The person's messages and file requests to a session (#283) not yet in its console, oldest first.</summary>
    List<AgentAlert> SentTo(string session)
    {
        alerts = AgentAlert.Load(root);
        return AgentAlert.History(root, alerts)
            .Where(a => a.From == "" && a.ToSession.Equals(session, StringComparison.OrdinalIgnoreCase) && !consoleSent.Contains(a.Key))
            .ToList();
    }

    void AddSent(AgentsWindow w, AgentAlert a)
    {
        consoleSent.Add(a.Key);
        bool read = a.ReadIn(alerts);
        var state = new TextBlock { Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        SetSentState(state, read);
        if (!read) consoleUnread.Add((a, state));
        w.Console.Children.Add(SentLine(a, state));
    }

    /// <summary>Asks the hook's files whether the unread ones were handed over: a few small files, only while some wait.</summary>
    void UpdateSentStates()
    {
        if (consoleUnread.Count == 0) return;
        var disk = AgentAlert.Load(root);
        for (int i = consoleUnread.Count - 1; i >= 0; i--)
        {
            if (!consoleUnread[i].Alert.ReadIn(disk)) continue;
            SetSentState(consoleUnread[i].State, true);
            consoleUnread.RemoveAt(i);
        }
    }

    static void SetSentState(TextBlock state, bool read)
    {
        state.Text = Loc.T(read ? "test.list.agents.sent.read" : "test.list.agents.sent.unread");
        state.Foreground = read ? Palette.Accent : null;
    }

    /// <summary>A message the person sent as an alert, in the console like one typed there, with whether Claude read it.</summary>
    Control SentLine(AgentAlert a, TextBlock state)
    {
        var box = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Text = When(DateTimeOffset.FromUnixTimeSeconds(a.At).LocalDateTime),
        });
        head.Children.Add(new TextBlock
        {
            FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Text = Loc.T("test.list.alert.user"),
        });
        if (a.Kind == "pedido") head.Children.Add(Badge(Loc.T("test.list.agents.kind.pedido"), false, YellowState));
        head.Children.Add(state);
        box.Children.Add(head);
        box.Children.Add(new SelectableTextBlock { Text = a.Text, TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Background = Palette.Alpha(Palette.AccentColor, CardWash * 2),
            BorderBrush = Palette.Line, BorderThickness = new(1), CornerRadius = new(8), Padding = new(12, 8), Child = box,
        };
    }

    /// <summary>One line of a session's console: the person's message on the accent's wash, Claude's text plain, a tool in one dim line.</summary>
    static Control ConsoleLine(AgentTranscript.Entry e, bool brief)
    {
        if (e.Kind == AgentTranscript.Kind.Tool)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(12, 0, 0, 0) };
            row.Children.Add(new TextBlock { Classes = { "mono" }, FontSize = 11, Foreground = Palette.Accent, Text = e.Tool, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = e.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
        bool user = e.Kind == AgentTranscript.Kind.User;
        var box = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Text = When(DateTimeOffset.FromUnixTimeSeconds(e.At).LocalDateTime),
        });
        head.Children.Add(new TextBlock
        {
            FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Text = user ? Loc.T("test.list.alert.user") : "Claude",
        });
        box.Children.Add(head);
        box.Children.Add(new SelectableTextBlock { Text = brief && !user ? Brief(e.Text) : e.Text, TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Background = user ? Palette.Alpha(Palette.AccentColor, CardWash * 2) : Palette.Raised,
            BorderBrush = Palette.Line, BorderThickness = new(1), CornerRadius = new(8), Padding = new(12, 8), Child = box,
        };
    }

    /// <summary>A Resumen's cut of Claude's text (#278): its first paragraph, at most this long.</summary>
    const int BriefText = 280;

    static string Brief(string text)
    {
        int para = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (para > 0) text = text[..para];
        return text.Length > BriefText ? text[..(BriefText - 1)] + "…" : text;
    }


    string ToName(IReadOnlyList<TestItem> items, string s) => s == "" ? Loc.T("test.list.agents.all") : SessionName(items, s);

    /// <summary>The box under the console: to whom (all the open sessions, or one) and the text; Ctrl+Enter or the arrow sends.</summary>
    Control AgentComposer()
    {
        agentToLabel = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        targetRow.Children.Add(agentToLabel);
        targetRow.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 8, VerticalAlignment = VerticalAlignment.Center });
        agentTarget = new Button { Classes = { "chip" }, Height = 30, MinHeight = 30, Padding = new(8, 0), Content = targetRow, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(agentTarget, Loc.T("test.list.agents.to"));
        ToolTip.SetTip(agentTarget, Loc.T("test.list.agents.to"));
        agentTarget.Click += (_, _) =>
        {
            if (current == null || agentTarget == null) return;
            var items = current.Items;
            var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            foreach (var s in agentLive.Prepend(""))
            {
                string pick = s;
                var item = new MenuItem { Header = ToName(items, s), FontWeight = s.Equals(agentTo, StringComparison.OrdinalIgnoreCase) ? FontWeight.SemiBold : FontWeight.Normal };
                item.Click += (_, _) =>
                {
                    agentTo = pick;
                    UpdateComposer(items);
                    agentBox?.Focus();
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(agentTarget);
        };

        agentBox = new TextBox
        {
            Classes = { "field", "note" }, Watermark = Loc.T("test.list.agents.hint"), TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, MinHeight = 30, MaxHeight = 140,
        };
        agentBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SendAgentText();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        agentSend = new Button { Classes = { "btn", "sm", "primary" }, Height = 30, Padding = new(10, 0), VerticalAlignment = VerticalAlignment.Top };
        agentSend.Content = new TextBlock { Classes = { "icon" }, Text = SendGlyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(agentSend, $"{Loc.T("test.list.agents.send")} (Ctrl+Enter)");
        AutomationProperties.SetName(agentSend, Loc.T("test.list.agents.send"));
        agentSend.Click += (_, _) => SendAgentText();

        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(agentTarget);
        Grid.SetColumn(agentBox, 1);
        grid.Children.Add(agentBox);
        Grid.SetColumn(agentSend, 2);
        grid.Children.Add(agentSend);
        return grid;
    }

    /// <summary>The box's recipient and whether it can send, in place: the text being typed stays.</summary>
    void UpdateComposer(IReadOnlyList<TestItem> items)
    {
        // a session that closed takes the box back to all of them
        if (agentTo != "" && !agentLive.Exists(s => s.Equals(agentTo, StringComparison.OrdinalIgnoreCase))) agentTo = "";
        bool any = agentLive.Count > 0;
        if (agentToLabel != null) agentToLabel.Text = ToName(items, agentTo);
        if (agentTarget != null) agentTarget.IsEnabled = any;
        if (agentSend != null) agentSend.IsEnabled = any;
        if (agentBox != null)
        {
            agentBox.IsEnabled = any;
            // in the console itself, or as an alert the agent reads at its next step
            agentBox.Watermark = Loc.T(ConsoleOf(agentTo) is { Running: true } ? "test.list.agents.hint.console" : "test.list.agents.hint");
        }
    }

    void SendAgentText()
    {
        if (agentBox == null || string.IsNullOrWhiteSpace(agentBox.Text)) return;
        var targets = agentTo == "" ? agentLive : [agentTo];
        if (targets.Count == 0) return;
        string text = agentBox.Text;
        // #278: one that runs in the bench gets it in its console, as if typed there: it answers a question right away
        if (ConsoleOf(agentTo) is { Running: true } c)
        {
            c.Send(text);
            agentBox.Text = "";
            ErrorText.Text = null;
            return;
        }
        bool ok = true;
        foreach (var s in targets) ok &= AgentAlert.Send(root, s, "mensaje", text);
        if (ok) agentBox.Text = "";
        AgentSent(ok);
    }

    /// <summary>After sending: a failure says so under the list; either way the window is drawn again with it.</summary>
    void AgentSent(bool ok)
    {
        ErrorText.Text = ok ? null : Loc.T("test.list.agents.error");
        // #283: it shows in the session's console right away
        sentPending = true;
        if (current != null) FillAgents(current);
    }

    /// <summary>How the map and the tabs name a session: its ticket and its subject, its title in Claude, or "otra sesión" and its first characters.</summary>
    static string SessionName(IReadOnlyList<TestItem> items, string session)
    {
        int t = LockLight.TicketOf(items, session);
        if (t > 0 && items.FirstOrDefault(i => i.Id == t) is { } item) return $"#{t} · {Subject(item)}";
        string title = LockLight.SessionTitle(session);
        if (title != "") return title;
        return $"{Loc.T("test.list.lock.other")} {session[..Math.Min(8, session.Length)]}";
    }
}
