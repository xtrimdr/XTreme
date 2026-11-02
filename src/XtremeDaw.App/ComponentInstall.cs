using XtremeDaw.App.Controls;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The one way to install an optional component, wherever it is asked from: one at a time, with a toast that shows
/// progress and can cancel. <see cref="Changed"/> tells every list and disabled feature to look again.
/// </summary>
public static class ComponentInstall
{
    const string SkippedKey = "componentsSkipped";

    static CancellationTokenSource? cancel;

    /// <summary>What is being installed.</summary>
    public static OptionalComponent? Active { get; private set; }
    public static event Action? Changed;

    /// <summary>Set by the window: its toast host, and how it reports.</summary>
    public static ToastHost? Toasts;
    public static Action<string>? Toast, Status;

    public static void Cancel() => cancel?.Cancel();

    /// <summary>Looks again at what is installed.</summary>
    public static async Task RefreshAsync()
    {
        await Task.Run(Components.Refresh);
        Changed?.Invoke();
    }

    public static bool Skipped(OptionalComponent c)
    {
        try { return (AppConfig.Get(SkippedKey) ?? "").Split(',').Contains(c.Id); }
        catch { return false; }
    }

    /// <summary>"Omitir": it stops asking at startup; Configuración still has it.</summary>
    public static void Skip(IEnumerable<OptionalComponent> list)
    {
        try { AppConfig.Set(SkippedKey, string.Join(',', Components.All.Where(c => Skipped(c) || list.Contains(c)).Select(c => c.Id))); }
        catch { /* Preference only. */ }
    }

    /// <summary>A download's size for a button or a list; empty when unknown.</summary>
    public static string Size(long bytes) => bytes <= 0 ? "" : $"{bytes / 1e6:0} MB";

    public static async Task Run(OptionalComponent c, ComponentPackage? package = null)
    {
        if (Active != null)
        {
            Toast?.Invoke(Loc.T("component.busy"));
            return;
        }
        string name = Loc.T($"component.{c.Id}.name");
        Active = c;
        using var source = cancel = new CancellationTokenSource();
        Changed?.Invoke();
        var bar = Toasts?.ShowProgress(Loc.T("component.installing", name));
        bar?.Cancelable(Loc.T("transport.count.cancel"), Cancel);
        string? message = null;
        try
        {
            await Task.Run(() => c.InstallAsync(package, f => bar?.Report(f), source.Token));
            message = Loc.T("component.ready", name);
        }
        catch (OperationCanceledException) { Status?.Invoke(Loc.T("component.canceled", name)); }
        catch (Exception e) { message = Loc.T("component.failed", name, e.Message.TrimEnd('.')); }
        finally
        {
            cancel = null;
            Active = null;
            bar?.Close();
        }
        await RefreshAsync();
        if (message != null) Toast?.Invoke(message);
    }
}
