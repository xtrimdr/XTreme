using Avalonia.Controls;
using Avalonia.Input;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The live session (#291), from its button beside Exportar. With no session, two choices: Crear sesión (it starts
/// right away and shows the code, the link and the address to pass on) or Unirme (a field for the code or link); in
/// one, what to pass on (the host's), who is in and Finalizar. It only asks: the window (<c>MainWindow.Live.cs</c>)
/// does the joining and hosting through <see cref="InviteRequested"/>, <see cref="JoinRequested"/> and
/// <see cref="LeaveRequested"/>. Esc or a click outside closes it.
/// </summary>
public sealed partial class LiveDialog : UserControl
{
    const string NameKey = "liveName";

    public event Action<string>? InviteRequested;
    public event Action<LiveInvite, string>? JoinRequested;
    public event Action? LeaveRequested;

    LiveSession? shown;
    /// <summary>Unirme was picked: the code field instead of the two choices.</summary>
    bool joining;

    public LiveDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Back();
        PointerPressed += (_, e) => { if (e.Source == this) Hide(); };
        CreateButton.Click += (_, _) => { if (TakeName() is { } n) InviteRequested?.Invoke(n); };
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
        await clip.SetTextAsync(text);
    }

    /// <summary>Opens it on what there is: the session, or the two choices.</summary>
    public void Show(LiveSession? session, bool canInvite)
    {
        IsVisible = true;
        joining = false;
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
        if (on) joining = false;
        ChoosePanel.IsVisible = !on && !joining;
        JoinPanel.IsVisible = JoinButton.IsVisible = !on && joining;
        ActivePanel.IsVisible = LeaveButton.IsVisible = OnBadge.IsVisible = on;
        CreateButton.IsEnabled = canInvite;
        ToolTip.SetTip(CreateButton, Loc.T(canInvite ? "live.create.tip" : "live.create.off"));
        CancelButton.Content = Loc.T(on ? "common.close" : joining ? "live.back" : "common.cancel");
        if (session == null) return;
        LeaveButton.Content = Loc.T(session.IsHost ? "live.end" : "live.leave");
        OnText.Text = Loc.T(session.IsHost ? "live.badgeHost" : "live.badgeGuest");
        InvitePanel.IsVisible = session.IsHost;
        InviteBox.Text = session.Invite.Code;
        CopyIpButton.IsEnabled = session.Invite.Addresses.Count > 0;
        AddressText.Text = Loc.T("live.addresses", string.Join(" · ", session.Invite.Addresses), session.Invite.Port);
        PeopleText.Text = string.Join(" · ", session.People);
        StateText.Text = state ?? "";
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
        if (joining && shown == null)
        {
            joining = false;
            Notice(null);
            Refresh(null, CreateButton.IsEnabled);
            JoinChoice.Focus();
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
