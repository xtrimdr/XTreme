using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// What an import does with the file (asked by the user: copying every sample fills the disk with duplicates), chosen in
/// Configuración → "Guardado y exportación" or with "No mostrar más" in <see cref="Dialogs.ImportDialog"/>, and kept in
/// config.json ("import.mode"): ask each time (the default), copy into media/, or link it where it is.
/// </summary>
public static class ImportMode
{
    public const string Ask = "ask", Copy = "copy", Link = "link";

    public static string Mode { get; private set; } = Ask;

    public static void Load()
    {
        try
        {
            string? m = AppConfig.Get("import.mode");
            Mode = m is Copy or Link ? m : Ask;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* defaults */ }
    }

    public static void Set(string mode)
    {
        mode = mode is Copy or Link ? mode : Ask;
        if (mode == Mode) return;
        Mode = mode;
        _ = Task.Run(() =>
        {
            try { AppConfig.Set("import.mode", mode); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }
}
