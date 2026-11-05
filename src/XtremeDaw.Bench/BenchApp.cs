using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace XtremeDaw.App;

/// <summary>
/// The bench's <see cref="Application"/>: XtremeDaw.App's App.axaml (linked, so the tokens and styles are the app's
/// with nothing copied) with this code behind it instead of the DAW's. Same theme, fonts and size as XTreme.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // its own language and theme, apart from XTreme's (asked by the user, #151): the system's language and the
        // default theme until one is picked in the bench
        Loc.ConfigKey = "benchLanguage";
        UiTheme.ConfigKey = "benchTheme";
        Loc.Load();
        UiTheme.Apply(UiTheme.Read(), persist: false);
        Controls.UiFonts.Load();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) Bench.Run(desktop);
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>xtbench.exe: XT-WorkBench alone, no audio and nothing of the DAW.</summary>
static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
