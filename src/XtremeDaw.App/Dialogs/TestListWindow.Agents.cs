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
/// The bench's Agentes view (asked by the user, #269: lost in how to sort out a jam between agents). On top, each
/// session whose Claude is open, by its ticket: what it waits for (its lock light) and the files it holds, each with
/// "Pedir" (asks its holder to let it go, as <c>-PideArchivo</c>). Below, the conversation between the sessions of the
/// last day, oldest first like a chat (the hook's alerts and messages, <see cref="AgentAlert.History"/>), and a box to
/// write to one session or all of them as the person (<see cref="AgentAlert.Send"/>). The box is kept across fills, and a
/// fill waits while it is being typed in, like the ticket's reply.
/// </summary>
public sealed partial class TestListWindow
{
    /// <summary>The Agentes view's id among the <see cref="Views"/>; it shows no tickets.</summary>
    const string AgentsView = "agents";
    /// <summary>Segoe Fluent Icons "People" and "Lock": the sessions' head and a held file.</summary>
    const string PeopleGlyph = "", LockGlyph = "";

    TextBox? agentBox;
    /// <summary>Whom the box writes to: a session, or "" = every open one.</summary>
    string agentTo = "";
    /// <summary>The sessions open at the last fill: "" sends to these.</summary>
    List<string> agentLive = [];
    /// <summary>The chat's length when last drawn: a new line scrolls to the end.</summary>
    int chatCount = -1;

    bool AgentTyping => agentBox?.IsFocused == true && view == AgentsView;

    void FillAgents(TestListData data)
    {
        var live = AgentAlert.Live(root);
        var held = LockLight.Held(root);

        Rows.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("test.list.agents.sessions") });
        if (live.Count == 0)
            Rows.Children.Add(new TextBlock { Classes = { "dim" }, Text = Loc.T("test.list.agents.none"), TextWrapping = TextWrapping.Wrap });
        foreach (var session in live) Rows.Children.Add(SessionCard(data, session, held));
        // a file held by a session that is not in the list (its Claude was not written down): still somebody's
        var orphan = held.Where(h => !live.Exists(s => s.Equals(h.Session, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var group in orphan.GroupBy(h => h.Session, StringComparer.OrdinalIgnoreCase))
            Rows.Children.Add(SessionCard(data, group.Key, held));

        var chat = AgentAlert.History(root, alerts);
        var head = new TextBlock { Classes = { "label" }, Text = Loc.T("test.list.agents.chat"), Margin = new(0, 8, 0, 0) };
        Rows.Children.Add(head);
        if (chat.Count == 0)
            Rows.Children.Add(new TextBlock { Classes = { "dim" }, Text = Loc.T("test.list.agents.quiet"), TextWrapping = TextWrapping.Wrap });
        foreach (var a in chat) Rows.Children.Add(ChatLine(data.Items, a));
        Rows.Children.Add(AgentComposer(data, live));

        if (chat.Count != chatCount)
        {
            chatCount = chat.Count;
            Dispatcher.UIThread.Post(Scroller.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    /// <summary>A session: its name, what stops it, the files it holds; "Escribir" points the box at it.</summary>
    Control SessionCard(TestListData data, string session, List<LockLight.Hold> held)
    {
        var light = lights.TryGetValue(session, out var l) ? l : LockLight.Free;
        var c = light.State switch
        {
            LockLight.Level.Free => GreenState,
            LockLight.Level.Waiting => YellowState,
            _ => Palette.RecColor,
        };
        var box = new StackPanel { Spacing = 6 };

        var top = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        top.Children.Add(Dot(c));
        var name = new TextBlock { Text = SessionName(data.Items, session), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        top.Children.Add(name);
        var write = new Button { Classes = { "chip" }, MinHeight = 26, Padding = new(8, 0), Content = Loc.T("test.list.agents.write") };
        AutomationProperties.SetName(write, Loc.T("test.list.agents.write"));
        write.Click += (_, _) =>
        {
            agentTo = session;
            Refill();
            Dispatcher.UIThread.Post(() =>
            {
                Scroller.ScrollToEnd();
                agentBox?.Focus();
            });
        };
        Grid.SetColumn(write, 2);
        top.Children.Add(write);
        box.Children.Add(top);

        // what stops it, in words: the same text as the "en curso" tooltip, without its "Bloqueos:"
        box.Children.Add(new TextBlock
        {
            Classes = { "dim" }, TextWrapping = TextWrapping.Wrap, Foreground = light.State == LockLight.Level.Free ? Palette.Dim : Palette.Alpha(c, 1),
            Text = light.State == LockLight.Level.Free ? Loc.T("test.list.agents.free") : LightTip(light),
        });

        foreach (var h in held.Where(h => h.Session.Equals(session, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(new TextBlock { Classes = { "icon" }, Text = LockGlyph, FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
            var file = new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = h.Path, TextTrimming = TextTrimming.LeadingCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(file, h.Path);
            Grid.SetColumn(file, 1);
            row.Children.Add(file);
            var ask = new Button { Classes = { "chip" }, MinHeight = 24, Height = 24, Padding = new(8, 0), FontSize = 11, Content = Loc.T("test.list.agents.ask") };
            ToolTip.SetTip(ask, Loc.T("test.list.agents.ask.tip"));
            string path = h.Path, owner = h.Owner;
            ask.Click += (_, _) => AgentSent(AgentAlert.Send(root, owner, "pedido",
                // for the agent, in the hook's words (-PideArchivo): agents read Spanish
                $"El usuario necesita {path}. Cuando termines con él, soltalo: tools\\bloqueos.ps1 -SueltaArchivo {path} (o terminá tu turno).", path));
            Grid.SetColumn(ask, 2);
            row.Children.Add(ask);
            box.Children.Add(row);
        }

        return new Border
        {
            Background = Palette.Raised, BorderBrush = Palette.Alpha(c, StateEdge), BorderThickness = new(1),
            CornerRadius = new(8), Padding = new(12, 10), Child = box,
        };
    }

    /// <summary>One alert as a chat line: when, from whom to whom, its kind if it is not a plain message, and the text.</summary>
    Control ChatLine(IReadOnlyList<TestItem> items, AgentAlert a)
    {
        var box = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Text = When(DateTimeOffset.FromUnixTimeSeconds(a.At).LocalDateTime),
        });
        string from = a.From == "" ? Loc.T("test.list.alert.user") : SessionName(items, a.From);
        head.Children.Add(new TextBlock
        {
            FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Text = $"{from} → {SessionName(items, a.ToSession)}",
        });
        if (a.Kind != "mensaje")
        {
            var c = a.Kind switch { "atencion" => Palette.RecColor, "pedido" => YellowState, _ => Palette.AccentColor };
            head.Children.Add(Badge(Loc.T("test.list.agents.kind." + (a.Kind is "atencion" or "pedido" ? a.Kind : "aviso")), false, c));
        }
        box.Children.Add(head);
        box.Children.Add(new TextBlock { Text = a.Text, TextWrapping = TextWrapping.Wrap });
        bool mine = a.From == "";
        return new Border
        {
            Background = mine ? Palette.Alpha(Palette.AccentColor, CardWash * 2) : Palette.Raised,
            BorderBrush = Palette.Line, BorderThickness = new(1), CornerRadius = new(8), Padding = new(12, 8), Child = box,
        };
    }

    /// <summary>The box under the chat: to whom (all the open sessions, or one) and the text; Ctrl+Enter or the arrow sends.</summary>
    Control AgentComposer(TestListData data, List<string> live)
    {
        agentLive = live;
        // a session that closed takes the box back to all of them
        if (agentTo != "" && !live.Exists(s => s.Equals(agentTo, StringComparison.OrdinalIgnoreCase))) agentTo = "";
        string ToName(string s) => s == "" ? Loc.T("test.list.agents.all") : SessionName(data.Items, s);
        var label = new TextBlock { FontSize = 12, Text = ToName(agentTo), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center };
        var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        targetRow.Children.Add(label);
        targetRow.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 8, VerticalAlignment = VerticalAlignment.Center });
        var agentTarget = new Button { Classes = { "chip" }, Height = 30, MinHeight = 30, Padding = new(8, 0), Content = targetRow, IsEnabled = live.Count > 0 };
        AutomationProperties.SetName(agentTarget, Loc.T("test.list.agents.to"));
        ToolTip.SetTip(agentTarget, Loc.T("test.list.agents.to"));
        agentTarget.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            foreach (var s in agentLive.Prepend(""))
            {
                string pick = s;
                var item = new MenuItem { Header = ToName(s), FontWeight = s.Equals(agentTo, StringComparison.OrdinalIgnoreCase) ? FontWeight.SemiBold : FontWeight.Normal };
                item.Click += (_, _) =>
                {
                    agentTo = pick;
                    label.Text = ToName(pick);
                    agentBox?.Focus();
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(agentTarget);
        };

        if (agentBox == null)
        {
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
            // what came while typing is drawn once the box is left
            agentBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(FillWaiting);
        }
        (agentBox.Parent as Panel)?.Children.Remove(agentBox);

        var send = new Button { Classes = { "btn", "sm", "primary" }, Height = 30, Padding = new(10, 0), IsEnabled = live.Count > 0 };
        send.Content = new TextBlock { Classes = { "icon" }, Text = SendGlyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(send, $"{Loc.T("test.list.agents.send")} (Ctrl+Enter)");
        AutomationProperties.SetName(send, Loc.T("test.list.agents.send"));
        send.Click += (_, _) => SendAgentText();

        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 6, Margin = new(0, 4, 0, 0) };
        agentTarget.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(agentTarget);
        Grid.SetColumn(agentBox, 1);
        grid.Children.Add(agentBox);
        send.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(send, 2);
        grid.Children.Add(send);
        agentBox.IsEnabled = live.Count > 0;
        return grid;
    }

    void SendAgentText()
    {
        if (agentBox == null || string.IsNullOrWhiteSpace(agentBox.Text)) return;
        var targets = agentTo == "" ? agentLive : [agentTo];
        if (targets.Count == 0) return;
        string text = agentBox.Text;
        bool ok = true;
        foreach (var s in targets) ok &= AgentAlert.Send(root, s, "mensaje", text);
        if (ok) agentBox.Text = "";
        AgentSent(ok);
    }

    /// <summary>After sending: a failure says so under the list; either way the chat is drawn again with it.</summary>
    void AgentSent(bool ok)
    {
        ErrorText.Text = ok ? null : Loc.T("test.list.agents.error");
        bool typing = agentBox?.IsFocused == true;
        sent = true;
        Refill();
        // drawn again, the box is a child of a new row: it keeps the keyboard for the next message
        if (typing) Dispatcher.UIThread.Post(() => agentBox?.Focus());
    }

    /// <summary>How the chat and the cards name a session: its ticket and its subject, its title in Claude, or "otra sesión" and its first characters.</summary>
    static string SessionName(IReadOnlyList<TestItem> items, string session)
    {
        int t = LockLight.TicketOf(items, session);
        if (t > 0 && items.FirstOrDefault(i => i.Id == t) is { } item) return $"#{t} · {Subject(item)}";
        string title = LockLight.SessionTitle(session);
        if (title != "") return title;
        return $"{Loc.T("test.list.lock.other")} {session[..Math.Min(8, session.Length)]}";
    }
}
