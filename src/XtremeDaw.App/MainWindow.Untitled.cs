using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The project without a place (<see cref="UntitledProjects"/>): what the window opens with when
/// <see cref="StartupMode"/> says so. It saves on its own like any project, in its folder under AppData; "Guardar"
/// (the key, the diskette, Archivo) asks where it goes instead and the window goes on there without reopening it, and
/// closing the window with something in it asks too.
/// Left with something in it (switching to another project), it goes to the recent list so the manager shows it.
/// </summary>
public partial class MainWindow
{
    /// <summary>The project open now has no place yet.</summary>
    bool Untitled => store != null && UntitledProjects.Is(store.Dir);

    /// <summary>A project without a place to leave behind when the window closes: recycled if it had something.</summary>
    (string Dir, bool Recycle)? untitledToDrop;

    /// <summary>The start without the manager: the one left from last time if it had something, else a new one.</summary>
    async Task StartUntitled()
    {
        try
        {
            string? left = await Task.Run(UntitledProjects.Leftover);
            // without video, like every new project
            await OpenProject(left ?? await Task.Run(() => UntitledProjects.Create("audio")));
            if (left != null) Toast(Loc.T("untitled.recovered"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Toast(Loc.T("main.openFail", e.Message));
            await ShowProjects();
        }
    }

    /// <summary>"Guardar": now, or asking where for a project without a place.</summary>
    void SaveNow()
    {
        if (Untitled) _ = SaveUntitled();
        else SaveSoon();
    }

    /// <summary>
    /// Asks where the project without a place goes, copies it there and goes on in the copy; its old folder goes.
    /// False when the user cancelled or it couldn't be done (the reason in a toast).
    /// </summary>
    async Task<bool> SaveUntitled()
    {
        if (store is not { } st || !Untitled || Projects.IsVisible) return false;
        string old = st.Dir;
        try
        {
            if (await SaveProjectCopy(old, st.Project.Name, saveAs: true) is not { } dir) return false;
            if (store != st) return false; // another project opened while it copied: the copy stays, as with "Guardar una copia"
            MoveUntitled(st, dir);
            Toast(Loc.T("proj.saveAs.done", st.Project.Name, dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Toast(Loc.T("proj.err.saveAs", e.Message.TrimEnd('.')));
            return false;
        }
        DropUntitled(old, recycle: false);
        return true;
    }

    /// <summary>
    /// The window goes on in the copy at <paramref name="dir"/> without reopening anything (#297): the project in
    /// memory, its tracks, plugins, undo and view stay; only where it is saved changes. The clips' paths follow the
    /// copy (so nothing reloads), and the video decoders reopen there, so the old folder can go.
    /// </summary>
    void MoveUntitled(ProjectStore st, string dir)
    {
        FlushSave();
        var clips = tracks.SelectMany(t => t.Clips).Where(c => c.Media != null).ToList();
        var audioHere = clips.Where(c => c.AudioPath != null && c.AudioPath == st.Resolve(c.Media!.Audio)).ToHashSet();
        var videoHere = clips.Where(c => c.VideoPath != null && c.VideoPath == st.Resolve(c.Media!.Video)).ToHashSet();
        st.MoveTo(dir);
        foreach (var c in audioHere) c.AudioPath = st.Resolve(c.Media!.Audio);
        foreach (var c in videoHere)
        {
            c.VideoPath = st.Resolve(c.Media!.Video);
            c.CloseVideo();
        }
        if (videoHere.Count > 0) _ = OpenMissingVideos(st);
        dirty = false;
        saveFailure = null;
        SessionName.Text = st.Project.Name;
        Title = WindowTitle(st.Project.Name);
        RecentProjects.Touch(st.Dir, st.Project.Name);
        RefreshHistory();
    }

    /// <summary>Takes a project without a place off the disk once nothing has it open: to the Recycle Bin when it had
    /// something (it can still be brought back), deleted when empty or already copied to its place.</summary>
    static void DropUntitled(string dir, bool recycle)
    {
        if (!UntitledProjects.Is(dir)) return;
        if (!recycle)
        {
            UntitledProjects.Remove(dir);
            RecentProjects.Forget(dir);
            return;
        }
        try { ProjectLibrary.Delete(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { /* left for the next start, which opens it again */ }
    }

    /// <summary>
    /// The project being left (another one opens): one without a place goes if empty; with something in it, to the
    /// recent list, so the manager still shows it.
    /// </summary>
    static void LeaveUntitled(string dir, string name, bool content)
    {
        if (content) RecentProjects.Touch(dir, name);
        else DropUntitled(dir, recycle: false);
    }
}
