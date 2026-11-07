using XtremeDaw.Audio;

namespace XtremeDaw.App;

/// <summary>
/// Optional components in the window: the look at startup, the question when something installable is missing, and
/// what follows from it (FFmpeg's chip, the about box). <see cref="ComponentInstall"/> does the work.
/// </summary>
public partial class MainWindow
{
    void InitComponents()
    {
        ComponentInstall.Toasts = Toasts;
        ComponentInstall.Toast = m => Toast(m);
        ComponentInstall.Status = m => Status(m);
        ComponentInstall.Changed += () =>
        {
            ShowFfmpeg(Components.Ffmpeg.Version);
            // a GM bank installed from Configuración: the tracks that chose it play it, the windows offer it
            SyncGms();
            RefreshGmWindows();
        };
        FfmpegState.PointerPressed += (_, _) => ComponentsHelp.Show();
        Opened += async (_, _) =>
        {
            await ComponentInstall.RefreshAsync();
            if (!Components.Ffmpeg.Ready) Status(Loc.T("title.ffmpeg.missing.status"));
            if (Components.All.Any(c => c.AskAtStart && !c.Ready && !c.Partial && !ComponentInstall.Skipped(c) && (c.CanInstall || c.ManualUrl != null)))
                ComponentsHelp.Show();
        };
    }

    /// <summary>Export needs FFmpeg for video: says so and offers it instead of opening.</summary>
    bool NeedFfmpeg()
    {
        if (Components.Ffmpeg.Ready) return false;
        Toasts.Show(Loc.T("component.ffmpeg.needed"), action: (Loc.T("component.install"), () => ComponentsHelp.Show()));
        return true;
    }
}
