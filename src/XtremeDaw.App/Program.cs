using Avalonia;

namespace XtremeDaw.App;

static class Program
{
    // ASIO drivers are COM objects: the UI thread that opens them must be STA.
    [STAThread]
    public static void Main(string[] args)
    {
        // the Banco de pruebas is xtbench.exe now (Bench); older shortcuts and benches still start XTreme.exe --banco
        if (Bench.In(args))
        {
            if (Bench.ListFrom(args) is { } list) Bench.Open(list, !Bench.Quiet(args));
            return;
        }
        // a .xtp opened from Windows while this XTreme is open goes to its window (ShellOpen)
        if (ShellOpen.ProjectArg(args) is { Length: > 0 } file && ShellOpen.Forward(file)) return;
        // The ASIO callback is managed code, so every blocking GC stalls it: keep full collections in the background.
        // Short ones still stop it, so the UI avoids allocation bursts while audio runs (see MainWindow.mixerLayout),
        // and the server GC (XtremeDaw.App.csproj) makes them rare.
        SessionLog.Start(args);
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        Audio.AudioWarmUp.Start();
        XtremeDaw.Audio.FfmpegSetup.RefreshPath();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
