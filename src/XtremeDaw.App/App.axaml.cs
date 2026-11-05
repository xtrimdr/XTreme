using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace XtremeDaw.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Loc.Load();
        UiTheme.Apply(UiTheme.Read(), persist: false);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime bench && Bench.In(bench.Args ?? []))
        {
            // the bench alone: the same look (theme, fonts, size) and nothing of the DAW
            Controls.UiFonts.Load();
            Bench.Run(bench);
            base.OnFrameworkInitializationCompleted();
            return;
        }
        Shortcuts.Load();
        SaveMode.Load();
        ImportMode.Load();
        TrackKnobs.Load();
        Controls.KnobLooks.Load();
        Controls.FaderLooks.Load();
        Controls.MeterLooks.Load();
        Controls.WaveLooks.Load();
        NoteNames.Load();
        Controls.UiFonts.Load();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // XTreme.exe [carpeta | proyecto.xtp] (a double click on a .xtp passes the file)
            var dir = desktop.Args?.FirstOrDefault() is { Length: > 0 } arg ? XtremeDaw.Projects.ProjectStore.DirOf(arg) : null;
            // the splash paints first; the main window (slow to build) comes once it is on screen
            var splash = new SplashWindow();
            splash.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var main = new MainWindow(dir);
                desktop.MainWindow = main;
                main.Opened += (_, _) => _ = splash.Dismiss();
                main.Show();
            }, DispatcherPriority.Background);
            splash.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
