using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// What the mixer's header shows beyond the basics. The mix A | B (<see cref="MainWindow.BuildMixCompare"/>) is hidden by
/// default (asked by the user, #211: one more button that adds nothing for most); Configuración → Apariencia turns it on.
/// Kept in config.json ("mixer.showAb"). Hiding it changes nothing in the project: the active mix stays the one heard.
/// </summary>
public static class MixerOptions
{
    const string AbKey = "mixer.showAb";

    public static bool ShowAb { get; private set; }

    /// <summary>The choice changed: the mixer shows or hides the buttons.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try { ShowAb = AppConfig.Get(AbKey) == "true"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* hidden */ }
    }

    public static void SetShowAb(bool show)
    {
        if (show == ShowAb) return;
        ShowAb = show;
        Changed?.Invoke();
        string value = show ? "true" : "false";
        _ = Task.Run(() =>
        {
            try { AppConfig.Set(AbKey, value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }
}
