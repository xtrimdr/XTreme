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
        Shortcuts.Load();
        SaveMode.Load();
        ImportMode.Load();
        TrackKnobs.Load();
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
