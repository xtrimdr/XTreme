using Avalonia.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// A .xtp opened from Windows while this window is open (<see cref="ShellOpen"/>, asked by the user, #254): the window
/// comes forward and opens it in place of the current project, as the project manager does (the current one is saved
/// first). Not while recording or exporting: that would cut them.
/// </summary>
public partial class MainWindow
{
    async Task OpenFromShell(string path)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        string dir = ProjectStore.DirOf(path);
        if (store != null && ProjectLibrary.SamePath(store.Dir, dir)) return;
        if (!ProjectStore.IsProject(dir))
        {
            Toast(Loc.T("main.openFail", path));
            return;
        }
        if (RecordingBusy || Export.IsVisible)
        {
            Toast(Loc.T("shell.busy", Path.GetFileNameWithoutExtension(path)));
            return;
        }
        try
        {
            await OpenProject(dir);
            if (Projects.IsVisible) Projects.Hide();
        }
        catch (Exception e)
        {
            Toast(Loc.T("main.openFail", e.Message));
        }
    }
}
