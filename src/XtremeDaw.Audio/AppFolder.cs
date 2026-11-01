namespace XtremeDaw.Audio;

/// <summary>
/// The program's own folder, %APPDATA%\XTreme (settings, recent projects, presets, plugin list, latency, logs).
/// The app was "GuitarCam Studio" until 2026-10-29 and "WildTrack" until 2026-11-01, and kept it in
/// %APPDATA%\GuitarCam Studio and %APPDATA%\WildTrack: the first time the folder is asked for, what the old ones
/// have (the newest first) and the new one doesn't is copied over, then a mark is left so it happens once. It merges instead of waiting for an empty folder because something may write there first (the test
/// script's sesiones.log), and each file goes to a temporary name and is then moved into place, so a copy cut halfway
/// is redone next time instead of leaving half a settings file. The old folder stays, so an older version still finds
/// its own. It lives here, in the lowest layer, because the latency store needs it too.
/// </summary>
public static class AppFolder
{
    public const string Name = "XTreme";

    /// <summary>The folder's names before the renames, newest first.</summary>
    static readonly string[] LegacyNames = ["WildTrack", "GuitarCam Studio"];

    /// <summary>Left in the folder once the old one was copied.</summary>
    const string MigratedMark = "migrado-de-guitarcam.txt";

    static readonly Lazy<string> dir = new(Resolve);

    public static string Dir => dir.Value;

    static string Resolve()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string path = Path.Combine(root, Name);
        if (File.Exists(Path.Combine(path, MigratedMark))) return path;
        string[] found = LegacyNames.Select(n => Path.Combine(root, n)).Where(Directory.Exists).ToArray();
        if (found.Length == 0) return path;
        try
        {
            foreach (string legacy in found) CopyMissing(legacy, path);
            File.WriteAllText(Path.Combine(path, MigratedMark), $"Copiado de {string.Join(", ", found)} el {DateTime.Now:yyyy-MM-dd HH:mm}.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Starting with default settings beats not starting: the old folder is untouched, the next run retries
        }
        return path;
    }

    static void CopyMissing(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            string dest = Path.Combine(to, Path.GetFileName(f));
            if (File.Exists(dest)) continue;
            string temp = dest + ".migrando";
            File.Copy(f, temp, true);
            File.Move(temp, dest);
        }
        foreach (string d in Directory.GetDirectories(from)) CopyMissing(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
