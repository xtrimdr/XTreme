using Avalonia.Controls;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// What the transport bar shows (#36): a right click on the bar lists its optional controls, and the ones the user
/// turns off stay hidden at any window width (<see cref="TransportPanel.OffProperty"/>); the rest still hide by
/// themselves when the bar is short of room, so it never takes two rows. Saved in config.json, a habit of the user.
/// </summary>
public partial class MainWindow
{
    /// <summary>config.json key: the ids of the transport controls turned off, comma separated.</summary>
    const string TransportHiddenKey = "transport.hidden";

    /// <summary>The bar's optional controls in the order they sit, with their stable id and their name in the menu.</summary>
    (Control Cell, string Id, string Label)[] TransportOptions() =>
    [
        (AudioCell, "input", Loc.T("transport.show.input")),
        (MonitorToggle, "monitor", Loc.T("transport.monitor")),
        (TunerToggle, "tuner", Loc.T("sc.tuner")),
        (CutAudioButton, "cutAudio", Loc.T("sc.cutAudio")),
        (ReturnToggle, "returnOnStop", Loc.T("transport.show.return")),
        (MeterCell, "meter", Loc.T("transport.meter")),
        (ClickSoundButton, "clickSound", Loc.T("transport.show.clickSound")),
        (LufsCell, "lufs", "LUFS"),
        (MaxPanelButton, "maximize", Loc.T("sc.maximize")),
        (VirtualKeysButton, "virtualKeys", Loc.T("sc.virtualKeys")),
        (FitButton, "fit", Loc.T("sc.fit")),
    ];

    void InitTransportShow()
    {
        var off = (AppConfig.Get(TransportHiddenKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (cell, id, _) in TransportOptions())
            TransportPanel.SetOff(cell, off.Contains(id));
        TransportBar.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowTransportMenu();
        };
        // the tuner's panel opens over its cell, or over the bar while the cell is hidden
        TunerToggle.IsCheckedChanged += (_, _) =>
            TunerPopup.PlacementTarget = TunerToggle.IsEffectivelyVisible ? TunerToggle : TransportBar;
    }

    void ShowTransportMenu()
    {
        var refresh = new List<Action>();
        var items = new List<object>();
        foreach (var (cell, _, label) in TransportOptions())
        {
            var c = cell;
            items.Add(MenuCheck(label, () => !TransportPanel.GetOff(c), () => SetTransportShown(c, TransportPanel.GetOff(c)), refresh));
        }
        Popup(items, 220).Open(TransportBar);
    }

    void SetTransportShown(Control cell, bool shown)
    {
        TransportPanel.SetOff(cell, !shown);
        if (!shown && cell == TunerToggle) TunerToggle.IsChecked = false;
        TransportRow.InvalidateMeasure();
        var off = TransportOptions().Where(o => TransportPanel.GetOff(o.Cell)).Select(o => o.Id).ToArray();
        try { AppConfig.Set(TransportHiddenKey, off.Length == 0 ? null : string.Join(",", off)); }
        catch (Exception ex) { Status(ex.Message); }
    }
}
