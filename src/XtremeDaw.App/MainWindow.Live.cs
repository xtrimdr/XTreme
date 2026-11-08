using Avalonia.Threading;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Dialogs;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The window's side of a shared session (#291, <see cref="LiveSession"/>): its button beside Exportar (or Archivo → Sesión en vivo) opens
/// <see cref="LiveDialog"/>. Every edit (<see cref="MarkDirty"/>) is sent a moment later, in one patch per burst; what
/// comes from the others goes in through <see cref="RestoreEdit"/>, like an undo, so the engine and the screen follow
/// it the same way and the playhead keeps going. Nothing goes or comes in while a take is recorded: it waits and goes
/// after, with the take's file. A guest's copy is a project of its own, saved like any other; leaving the session
/// leaves it as it is. Who is where goes apart (MainWindow.Presence.cs), and so do the renders for plugins someone
/// lacks (MainWindow.LiveRender.cs). Someone joining or leaving says so in a toast with a short cue (<see cref="CueSounds"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>How long after an edit it is sent: a fader drag goes as a few patches a second, not one per pixel.</summary>
    static readonly TimeSpan LivePublishDelay = TimeSpan.FromMilliseconds(120);
    /// <summary>How often it tries again to apply changes that had to wait (a take, a dialog that holds the project).</summary>
    static readonly TimeSpan LiveRetry = TimeSpan.FromMilliseconds(250);

    LiveSession? live;
    bool livePumpQueued, liveJoining;
    DispatcherTimer? livePublishTimer, liveRetryTimer;
    /// <summary>Media whose files arrived while the tracks couldn't be reloaded.</summary>
    readonly HashSet<string> liveArrived = [];

    void InitLive()
    {
        livePublishTimer = new DispatcherTimer { Interval = LivePublishDelay };
        livePublishTimer.Tick += (_, _) => LivePublish();
        liveRetryTimer = new DispatcherTimer { Interval = LiveRetry };
        liveRetryTimer.Tick += (_, _) => LivePump();
        LiveAsk.InviteRequested += StartHosting;
        LiveAsk.JoinRequested += (invite, name) => _ = JoinLive(invite, name);
        LiveAsk.LeaveRequested += () => { EndLive(null); LiveAsk.Hide(); };
        LiveButton.Click += (_, _) => ShowLive();
        LiveButton.SizeChanged += (_, _) => PlaceTools();
        UpdateLiveButton();
    }

    void ShowLive() => LiveAsk.Show(live, CanHost);

    /// <summary>The button beside Exportar: "Sesión en vivo" with no session; in one, everyone's orbs (<see cref="LivePulse"/>) and how many are in.</summary>
    void UpdateLiveButton()
    {
        bool on = live != null;
        LiveButton.Classes.Set("on", on);
        LivePulse.IsVisible = on;
        if (live is { } p) LivePulse.SetPeople([.. p.People.Select(w => (w, Palette.PersonColor(Math.Max(0, p.ColorOf(w)))))]);
        else LivePulse.Clear();
        LiveButtonIcon.IsVisible = !on;
        LiveButtonText.Text = live is { } s ? Loc.T("live.buttonOn", s.People.Count) : Loc.T("live.button");
    }

    bool CanHost => store is { ReadOnly: false } && !restoring && MenuFree;

    /// <summary>Called on every edit: the change goes to the others shortly.</summary>
    void LiveSoon()
    {
        if (live == null || livePublishTimer == null) return;
        livePublishTimer.Stop();
        livePublishTimer.Start();
    }

    void LivePublish()
    {
        livePublishTimer!.Stop();
        if (live == null || store == null) return;
        // a take in progress goes when it's whole; an undo being shown goes when it's done
        if (restoring || RecordingBusy) { livePublishTimer.Start(); return; }
        live.Publish(store.Project);
        LivePulse.Spark(live.Name);
    }

    void OnLiveSignal()
    {
        if (livePumpQueued) return;
        livePumpQueued = true;
        Dispatcher.UIThread.Post(() => { livePumpQueued = false; LivePump(); });
    }

    async void LivePump()
    {
        if (live == null || store == null) { liveRetryTimer?.Stop(); return; }
        var st = store;
        var s = live;
        // while a take runs nothing is read: a newcomer's copy or a file asked for would carry the take half done
        if (restoring || RecordingBusy || Projects.IsVisible || st.ReadOnly) { liveRetryTimer!.Start(); return; }
        liveRetryTimer!.Stop();
        var r = s.Pump(st.Project, undo, applyEdits: false);
        Announce(r);
        if (r.Ended) { EndLive(s.EndReason); return; }
        if (r.PresenceChanged || r.PeopleChanged) ApplyPresence();
        foreach (var id in r.Arrived) liveArrived.Add(id);
        LiveSession.PumpResult? applied = null;
        await RestoreEdit(p =>
        {
            applied = s.Pump(p, undo, applyEdits: true);
            return applied.Changed;
        }, "");
        if (applied != null)
        {
            Announce(applied);
            if (live == s && (applied.PresenceChanged || applied.PeopleChanged)) ApplyPresence();
            foreach (var id in applied.Arrived) liveArrived.Add(id);
            if (applied.Changed && applied.LastAuthor != null)
            {
                Status(Loc.T("live.changed", applied.LastAuthor));
                LivePulse.Spark(applied.LastAuthor);
            }
        }
        if (liveArrived.Count > 0 && store == st && !restoring && !RecordingBusy) await ReloadArrived(st);
        RefreshLiveDialog();
    }

    void Announce(LiveSession.PumpResult r)
    {
        foreach (var n in r.Joined) Toast(Loc.T("live.joinedNow", n));
        foreach (var n in r.Left) Toast(Loc.T("live.leftNow", n));
        // a cue too (asked by the user), but never into the headphones of someone recording
        if (r.Joined.Count + r.Left.Count > 0 && !RecordingBusy) engine.PlayCue(r.Joined.Count > 0 ? CueSounds.Joined : CueSounds.Left);
        if (r.PluginsChanged) LivePluginsChanged();
        if (!r.PeopleChanged || live == null) return;
        Status(Loc.T("live.peopleNow", string.Join(", ", live.People)));
        UpdateLiveButton();
        RefreshLiveDialog();
        // the badges of Historial and Biblioteca take or lose their colors
        RefreshHistory();
    }

    void RefreshLiveDialog()
    {
        if (!LiveAsk.IsVisible) return;
        string? state = live is { Downloading: > 0 } l ? Loc.T("live.downloading", l.Downloading) : null;
        LiveAsk.Refresh(live, CanHost, state);
    }

    /// <summary>Clips whose file was missing and just arrived: read again, the rest stays as it is.</summary>
    async Task ReloadArrived(ProjectStore st)
    {
        var ids = liveArrived.ToHashSet();
        liveArrived.Clear();
        bool any = false;
        foreach (var c in tracks.SelectMany(t => t.Clips))
            if (c.Media != null && ids.Contains(c.Media.Id) && c.Audio == null)
            {
                c.AudioPath = null;
                any = true;
            }
        if (!any) return;
        restoring = true;
        try { await ApplyTracks(st); }
        catch (Exception e) { Toast(Loc.T("main.restoredNoView", e.Message)); }
        finally
        {
            restoring = false;
            Timeline.InvalidateVisual();
        }
    }

    void StartHosting(string name)
    {
        if (store == null || !CanHost) return;
        try
        {
            var s = LiveSession.Host(name, AboutDialog.AppVersion());
            if (s.Invite.Addresses.Count == 0)
            {
                s.Dispose();
                LiveAsk.Notice(Loc.T("live.noNetwork"));
                return;
            }
            s.Start(store.Project, store.Dir);
            Attach(s);
            Status(Loc.T("live.hosting"));
            ShowLive();
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
        {
            LiveAsk.Notice(Loc.T("live.cantHost", e.Message));
        }
    }

    async Task JoinLive(LiveInvite invite, string name)
    {
        if (liveJoining) return;
        liveJoining = true;
        LiveAsk.Busy(true);
        try
        {
            var (s, welcome) = await LiveSession.Join(invite, name, AboutDialog.AppVersion(), CancellationToken.None);
            try
            {
                var project = ProjectStore.Parse(welcome.Project);
                // the guest's own copy: a new folder in the library, so the host's project is never written over
                string dir = await Task.Run(() =>
                {
                    string d = LiveFolder(project.Name);
                    using (ProjectStore.Create(d, project)) { }
                    return d;
                });
                await OpenProject(dir);
                if (store == null || !ProjectLibrary.SamePath(store.Dir, dir)) throw new IOException(Loc.T("live.cantOpen"));
                s.Start(store.Project, store.Dir);
                Attach(s);
                Toast(Loc.T("live.joined", welcome.Host));
                ShowLive();
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or LiveRefusedException or System.Text.Json.JsonException)
        {
            LiveAsk.Notice(e.Message);
        }
        finally
        {
            liveJoining = false;
            LiveAsk.Busy(false);
        }
    }

    static string LiveFolder(string name)
    {
        string root = ProjectLibrary.Root, b = ProjectLibrary.FolderNameFor(name.Length > 0 ? name : "Sesión");
        for (int i = 1; ; i++)
        {
            string d = Path.Combine(root, i == 1 ? b : $"{b} ({i})");
            if (!Directory.Exists(d) && !File.Exists(d)) return d;
        }
    }

    void Attach(LiveSession s)
    {
        live = s;
        s.Signal += OnLiveSignal;
        // what this copy found tells the others what it can play; with nothing found yet it looks first (#291)
        s.Catalog = LiveCatalog;
        EnsurePlugins();
        if (store != null) s.ShareCatalog(store.Project);
        LivePluginsChanged();
        StartPresence();
        UpdateLiveButton();
        OnLiveSignal();
    }

    /// <summary>Leaves the session (another project opened, the window closing, Salir); the copy stays as it is.</summary>
    void EndLive(string? why)
    {
        if (live == null) return;
        var s = live;
        live = null;
        s.Signal -= OnLiveSignal;
        // what was just changed still goes, so the others don't lose it
        if (why == null && store != null && !RecordingBusy) s.Publish(store.Project);
        s.Dispose();
        livePublishTimer?.Stop();
        liveRetryTimer?.Stop();
        liveArrived.Clear();
        StopPresence();
        // out of the session every plugin plays again
        LivePluginsChanged();
        RefreshHistory();
        UpdateLiveButton();
        if (why != null) Toast(why);
        Status(Loc.T("live.left"));
        if (LiveAsk.IsVisible) LiveAsk.Refresh(null, CanHost);
    }

    /// <summary>Before another project replaces this one: the session was for this one.</summary>
    void LiveBeforeOpen()
    {
        if (!liveJoining) EndLive(null);
    }
}
