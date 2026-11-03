using Avalonia.Platform.Storage;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// "Guardar como…" (the window goes on in the new project) and "Guardar una copia…" (it stays in this one), from the
/// project manager and from Ctrl+Shift+S: the user picks a .xtp anywhere, the open project is written first and
/// <see cref="ProjectLibrary.SaveAs"/> copies it there in the background, with its progress in a toast.
/// </summary>
public partial class MainWindow
{
    /// <summary>Where the last "Guardar como" went, so the next one starts there (config.json).</summary>
    const string SaveAsDirKey = "saveAsDir";

    static FilePickerFileType ProjectFiles => new(Loc.T("proj.fileType")) { Patterns = ["*" + ProjectStore.Extension] };

    /// <summary>
    /// Picks where and copies <paramref name="src"/> there. Returns the new project's folder, or null if the user
    /// cancelled. Throws with a message for the user.
    /// </summary>
    async Task<string?> SaveProjectCopy(string src, string name, bool saveAs)
    {
        if (RecordingBusy) throw new IOException(Loc.T("proj.saveAs.recording"));
        string start = AppConfig.Get(SaveAsDirKey) is { } last && Directory.Exists(last) ? last : ProjectLibrary.Root;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T(saveAs ? "proj.saveAs" : "proj.saveCopy"),
            SuggestedFileName = ProjectLibrary.FolderNameFor(saveAs ? name : Loc.T("proj.copyName", name)),
            DefaultExtension = ProjectStore.Extension[1..],
            FileTypeChoices = [ProjectFiles],
            SuggestedStartLocation = Directory.Exists(start) ? await StorageProvider.TryGetFolderFromPathAsync(start) : null,
            ShowOverwritePrompt = false, // it never overwrites: a taken folder is refused with its reason
        });
        if (file?.TryGetLocalPath() is not { } path) return null;
        if (!path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectStore.Extension;
        if (store != null && ProjectLibrary.SamePath(src, store.Dir)) await FlushProjectOperation();
        var toast = Toasts.ShowProgress(Loc.T("proj.saveAs.copying", Path.GetFileNameWithoutExtension(path)));
        try
        {
            string dir = await Task.Run(() => ProjectLibrary.SaveAs(src, path, new Progress<double>(toast.Report)));
            try { AppConfig.Set(SaveAsDirKey, Path.GetDirectoryName(path)); } catch (IOException) { }
            return dir;
        }
        finally
        {
            toast.Close();
        }
    }

    /// <summary>Ctrl+Shift+S: "Guardar como…" of the open project.</summary>
    async Task SaveAsShortcut()
    {
        if (store == null || Projects.IsVisible) return;
        try
        {
            string? dir = await SaveProjectCopy(store.Dir, store.Project.Name, saveAs: true);
            if (dir == null) return;
            await OpenProject(dir);
            Toast(Loc.T("proj.saveAs.done", store?.Project.Name ?? "", dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Toast(Loc.T("proj.err.saveAs", e.Message.TrimEnd('.')));
        }
    }
}
