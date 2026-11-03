using XtremeDaw.Audio;

namespace XtremeDaw.Projects;

/// <summary>
/// Projects that don't have a place yet (asked by the user, 2026-11-03: start straight in an empty session and choose
/// where it goes when saving). They live in %APPDATA%\XTreme\sin-guardar\, one folder each, so what gets recorded is
/// on disk from the first second like in any project; they stay out of the recent list. "Guardar" moves one to the
/// place the user picks (<see cref="ProjectLibrary.SaveAs"/>) and drops this folder. One left behind with something
/// in it (the program died) is opened again at the next start; an empty one is just deleted.
/// </summary>
public static class UntitledProjects
{
    public static string Root => Path.Combine(AppConfig.Dir, "sin-guardar");

    public static bool Is(string? dir) =>
        dir != null && Path.GetFullPath(dir).StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>It has something worth asking about before it goes: tracks or recorded / imported audio.</summary>
    public static bool HasContent(Project p) => p.Tracks.Count > 0 || p.Media.Count > 0;

    /// <summary>A new empty project without a place. Returns its folder.</summary>
    public static string Create(string mode)
    {
        string dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        ProjectLibrary.Create(Texts.T("lib.untitled", "Sin título"), null, dir, mode);
        RecentProjects.Forget(dir);
        return dir;
    }

    /// <summary>
    /// The newest one left with something in it (the program closed without asking), to open again; empty ones are
    /// deleted on the way. One open in another window is left alone.
    /// </summary>
    public static string? Leftover()
    {
        if (!Directory.Exists(Root)) return null;
        string? found = null;
        foreach (string dir in Directory.EnumerateDirectories(Root).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            if (!ProjectStore.IsProject(dir)) continue;
            bool content;
            try
            {
                using var store = ProjectStore.Open(dir);
                if (store.ReadOnly) continue;
                string media = Path.Combine(dir, "media");
                content = HasContent(store.Project) || Directory.Exists(media) && Directory.EnumerateFiles(media).Any();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                continue;
            }
            if (content) found ??= dir;
            else Remove(dir);
        }
        return found;
    }

    /// <summary>Deletes one with nothing in it (no takes to lose).</summary>
    public static void Remove(string dir)
    {
        if (!Is(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* tried again at the next start */ }
    }
}
