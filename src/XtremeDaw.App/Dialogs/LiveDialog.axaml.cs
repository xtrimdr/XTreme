using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The live session (#291), from its button beside Exportar. With no session, two choices: Crear sesión (then where:
/// over the internet, with a short code through the relay, or on the local network, with the long code, the link and
/// the address; #320) or Unirme (a field for either code or the link); in
/// one, what to pass on (the host's), who is in (one row each: their color, name, role, the link's latency where this
/// copy measures it, and what they are doing) and Finalizar. It only asks: the window (<c>MainWindow.Live.cs</c>)
/// does the joining and hosting through <see cref="InviteRequested"/>, <see cref="JoinRequested"/> and
/// <see cref="LeaveRequested"/>. Esc or a click outside closes it.
/// </summary>
public sealed partial class LiveDialog : UserControl
{
    const string NameKey = "liveName";

    /// <summary>Crear sesión, with the name and whether it goes over the internet.</summary>
    public event Action<string, bool>? InviteRequested;
    public event Action<LiveInvite, string>? JoinRequested;
    public event Action? LeaveRequested;

    LiveSession? shown;
    /// <summary>Unirme was picked: the code field instead of the two choices.</summary>
    bool joining;
    /// <summary>Crear sesión was picked: where it goes (internet or local network) instead of the two choices.</summary>
    bool creating;

    public LiveDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Back();
        PointerPressed += (_, e) => { if (e.Source == this) Hide(); };
        CreateButton.Click += (_, _) =>
        {
            if (TakeName() == null) return;
            creating = true;
            Notice(null);
            Refresh(shown, CreateButton.IsEnabled);
            OnlineButton.Focus();
        };
        OnlineButton.Click += (_, _) => { if (TakeName() is { } n) InviteRequested?.Invoke(n, true); };
        LocalButton.Click += (_, _) => { if (TakeName() is { } n) InviteRequested?.Invoke(n, false); };
        JoinChoice.Click += (_, _) =>
        {
            if (TakeName() == null) return;
            joining = true;
            Notice(null);
            Refresh(shown, CreateButton.IsEnabled);
            CodeBox.Focus();
        };
        JoinButton.Click += (_, _) => Join();
        LeaveButton.Click += (_, _) => LeaveRequested?.Invoke();
        CopyCodeButton.Click += (_, _) => Copy(shown?.Invite.Code);
        CopyLinkButton.Click += (_, _) => Copy(shown?.Invite.Link);
        CopyIpButton.Click += (_, _) => Copy(shown is { } s && s.Invite.Addresses.Count > 0 ? $"{s.Invite.Addresses[0]}:{s.Invite.Port}" : null);
    }

    async void Copy(string? text)
    {
        if (text == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        // another program can hold the clipboard: copying again works, crashing the session does not
        try { await clip.SetTextAsync(text); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

    /// <summary>Opens it on what there is: the session, or the two choices.</summary>
    public void Show(LiveSession? session, bool canInvite)
    {
        IsVisible = true;
        joining = creating = false;
        Notice(null);
        Refresh(session, canInvite);
        if (session == null)
        {
            try { NameBox.Text ??= AppConfig.Get(NameKey) ?? Environment.UserName; }
            catch (IOException) { NameBox.Text ??= Environment.UserName; }
            CreateButton.Focus();
        }
        else CancelButton.Focus();
    }

    /// <summary>Redraws what it shows (someone joined, a file arrived); nothing when it's closed.</summary>
    public void Refresh(LiveSession? session, bool canInvite, string? state = null)
    {
        shown = session;
        bool on = session != null;
        if (on) joining = creating = false;
        ChoosePanel.IsVisible = !on && !joining && !creating;
        WherePanel.IsVisible = !on && creating;
        JoinPanel.IsVisible = JoinButton.IsVisible = !on && joining;
        ActivePanel.IsVisible = LeaveButton.IsVisible = OnBadge.IsVisible = on;
        CreateButton.IsEnabled = canInvite;
        ToolTip.SetTip(CreateButton, Loc.T(canInvite ? "live.create.tip" : "live.create.off"));
        CancelButton.Content = Loc.T(on ? "common.close" : joining || creating ? "live.back" : "common.cancel");
        if (session == null) return;
        LeaveButton.Content = Loc.T(session.IsHost ? "live.end" : "live.leave");
        OnText.Text = Loc.T(session.IsHost ? "live.badgeHost" : "live.badgeGuest");
        InvitePanel.IsVisible = session.IsHost;
        bool online = session.Invite.Online;
        InviteBox.Text = session.Invite.Code;
        RoomText.Text = session.Invite.Code;
        InviteBox.IsVisible = CopyIpButton.IsVisible = AddressText.IsVisible = !online;
        RoomText.IsVisible = online;
        PassOnText.Text = Loc.T(online ? "live.passOnOnline" : "live.passOn");
        CopyIpButton.IsEnabled = session.Invite.Addresses.Count > 0;
        AddressText.Text = Loc.T("live.addresses", string.Join(" · ", session.Invite.Addresses), session.Invite.Port);
        Rows(session);
        StateText.Text = state ?? "";
    }

    /// <summary>A person's row: the parts that change, kept so a refresh only rewrites their text.</summary>
    sealed record Row(string Who, Ellipse Dot, TextBlock Latency, TextBlock Status);

    readonly List<Row> rows = [];

    /// <summary>The people list: rebuilt when who is in changes, otherwise only its colors, latencies and states.</summary>
    void Rows(LiveSession s)
    {
        if (!rows.Select(r => r.Who).SequenceEqual(s.People))
        {
            rows.Clear();
            PeopleList.Children.Clear();
            foreach (var who in s.People) PeopleList.Children.Add(NewRow(s, who));
        }
        foreach (var r in rows)
        {
            r.Dot.Fill = new SolidColorBrush(Palette.PersonColor(Math.Max(0, s.ColorOf(r.Who))));
            r.Latency.Text = s.LatencyOf(r.Who) is { } ms ? Loc.T("live.latency", ms < 1 ? "<1" : $"{ms:0}")
                : s.IsHost && r.Who != s.Name || !s.IsHost && r.Who == s.Name ? "—" : "";
            r.Status.Text = StatusOf(s, r.Who);
        }
    }

    Control NewRow(LiveSession s, string who)
    {
        bool me = who == s.Name, isHost = who == s.HostName;
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var name = new TextBlock
        {
            Text = me ? Loc.T("live.you", who) : who, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var role = new Border
        {
            Background = isHost ? Palette.AccentSoft : Palette.Hover, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock { Text = Loc.T(isHost ? "live.role.host" : "live.role.guest"), FontSize = 11, Foreground = isHost ? Palette.Accent : Palette.Dim },
        };
        var latency = new TextBlock { Classes = { "dim", "mono" }, MinWidth = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { FontSize = 11, Foreground = Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(18, 2, 0, 0) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        grid.Children.Add(dot);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(role, 2);
        grid.Children.Add(role);
        Grid.SetColumn(latency, 3);
        grid.Children.Add(latency);
        Grid.SetRow(status, 1);
        Grid.SetColumnSpan(status, 4);
        grid.Children.Add(status);
        rows.Add(new(who, dot, latency, status));
        return new Border
        {
            Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7), Child = grid,
        };
    }

    /// <summary>What someone is doing, from where they are (<see cref="LiveSession.Presence"/>), and whether plugins are missing for them.</summary>
    static string StatusOf(LiveSession s, string who)
    {
        string doing = s.Presence.TryGetValue(who, out var pr) && pr.Recording ? Loc.T("live.status.recording")
            : pr?.Playing != null ? Loc.T("live.status.playing")
            : s.EditingBy(who).Count > 0 ? Loc.T("live.status.editing")
            : pr?.Rolling == true ? Loc.T("live.status.rolling")
            : Loc.T("live.status.connected");
        return s.LacksAny(who) ? doing + " · " + Loc.T("live.status.lacks") : doing;
    }

    /// <summary>A line of trouble (couldn't connect, another version), or null to take it away.</summary>
    public void Notice(string? text)
    {
        NoticeBox.IsVisible = text != null;
        NoticeText.Text = text ?? "";
    }

    /// <summary>While it connects, nothing else can be pressed.</summary>
    public void Busy(bool busy)
    {
        OnlineButton.IsEnabled = LocalButton.IsEnabled = !busy;
        JoinButton.IsEnabled = CodeBox.IsEnabled = CancelButton.IsEnabled = !busy;
        JoinButton.Content = Loc.T(busy ? "live.connecting" : "live.joinButton");
    }

    string? TakeName()
    {
        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) { Notice(Loc.T("live.needName")); NameBox.Focus(); return null; }
        try { AppConfig.Set(NameKey, name); } catch (IOException) { }
        return name;
    }

    void Join()
    {
        if (TakeName() is not { } name) return;
        if (LiveInvite.Parse(CodeBox.Text ?? "") is not { } invite)
        {
            Notice(Loc.T("live.badCode"));
            CodeBox.Focus();
            return;
        }
        Notice(null);
        JoinRequested?.Invoke(invite, name);
    }

    /// <summary>Cancelar / Volver: from the code field back to the two choices; otherwise it closes.</summary>
    void Back()
    {
        if ((joining || creating) && shown == null)
        {
            bool wasCreating = creating;
            joining = creating = false;
            Notice(null);
            Refresh(null, CreateButton.IsEnabled);
            (wasCreating ? CreateButton : JoinChoice).Focus();
            return;
        }
        Hide();
    }

    public void Hide() => IsVisible = false;

    /// <summary>Enter joins (in the code field), Escape goes back or closes; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) { Back(); e.Handled = true; return true; }
        if (e.Key == Key.Enter && shown == null && joining && JoinButton.IsEnabled && e.Source is not Button) { Join(); e.Handled = true; return true; }
        // typing in the fields, Tab and the buttons' own keys go on as usual; the window's shortcuts don't
        return true;
    }
}
