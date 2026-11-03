using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// What the window opens with (asked by the user, 2026-11-03): the projects manager (the default) or straight into an
/// empty project without a place (<see cref="UntitledProjects"/>), saved where the user picks later. Chosen in
/// Configuración → "Guardado y exportación" or with "Mostrar al arrancar" in the manager, kept in config.json ("startup").
/// </summary>
public static class StartupMode
{
    public const string Projects = "projects", Empty = "empty";
    const string Key = "startup";

    public static string Mode
    {
        get
        {
            try { return AppConfig.Get(Key) == Empty ? Empty : Projects; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Projects; }
        }
    }

    public static event Action? Changed;

    public static void Set(string mode)
    {
        mode = mode == Empty ? Empty : Projects;
        if (mode == Mode) return;
        try { AppConfig.Set(Key, mode); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Changed?.Invoke();
    }
}
