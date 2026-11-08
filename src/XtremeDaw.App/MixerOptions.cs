using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// What the mixer's header shows beyond the basics. The mix A | B (<see cref="MainWindow.BuildMixCompare"/>) is hidden by
/// default (asked by the user, #211: one more button that adds nothing for most); Configuración → Apariencia turns it on.
/// Kept in config.json ("mixer.showAb"). Hiding it changes nothing in the project: the active mix stays the one heard.
/// Also where each strip's name goes (#277, like FL Studio's "Alt" layouts): on top, as always, or at its foot under the
/// fader ("mixer.namePos": "top" | "bottom"); a mixer view (MainWindow.MixerViews.cs) can set it too.
/// And the side a bus left in its default place goes to (#301, <see cref="MixerSides.BusDefault"/>): the right dock unless
/// the user picks the left side ("mixer.busDock": "right" | "left"; #277's "middle" reads as the left side, #303).
/// </summary>
public static class MixerOptions
{
    const string AbKey = "mixer.showAb";
    public const string NamePosKey = "mixer.namePos";
    const string BusDockKey = "mixer.busDock";
    static readonly string[] BusDocks = ["left", "right"];

    /// <summary>The buses' default dock, as <see cref="MixerSides.BusDefault"/> (a dock index).</summary>
    public static int BusDock => MixerSides.BusDefault;

    public static void SetBusDock(int dock)
    {
        if (dock == MixerSides.BusDefault || dock is < MixerSides.LeftSide or > MixerSides.RightSide) return;
        MixerSides.BusDefault = dock;
        Changed?.Invoke();
        Save(BusDockKey, BusDocks[dock]);
    }

    public static bool ShowAb { get; private set; }

    /// <summary>The strips' names at their foot, under the fader, instead of on top (the default).</summary>
    public static bool NameAtFoot { get; private set; }

    /// <summary>The choice changed: the mixer shows or hides the buttons.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            ShowAb = AppConfig.Get(AbKey) == "true";
            NameAtFoot = AppConfig.Get(NamePosKey) == "bottom";
            string? saved = AppConfig.Get(BusDockKey);
            if (Array.IndexOf(BusDocks, saved == "middle" ? "left" : saved) is >= 0 and var dock) MixerSides.BusDefault = dock;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* hidden, names on top */ }
    }

    public static void SetNameAtFoot(bool foot)
    {
        if (foot == NameAtFoot) return;
        NameAtFoot = foot;
        Changed?.Invoke();
        Save(NamePosKey, foot ? "bottom" : "top");
    }

    static void Save(string key, string value) => _ = Task.Run(() =>
    {
        try { AppConfig.Set(key, value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
    });

    public static void SetShowAb(bool show)
    {
        if (show == ShowAb) return;
        ShowAb = show;
        Changed?.Invoke();
        Save(AbKey, show ? "true" : "false");
    }
}
