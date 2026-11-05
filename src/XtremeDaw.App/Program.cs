using Avalonia;

namespace XtremeDaw.App;

static class Program
{
    // ASIO drivers are COM objects: the UI thread that opens them must be STA.
    [STAThread]
    public static void Main(string[] args)
    {
        // the Banco de pruebas on its own (Bench): no audio, no session log of a session that is not one
        if (Bench.In(args))
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }
        // The ASIO callback is managed code, so every blocking GC stalls it: keep full collections in the background.
        // Short ones still stop it, so the UI avoids allocation bursts while audio runs (see MainWindow.mixerLayout).
        SessionLog.Start(args);
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        Audio.AudioWarmUp.Start();
        XtremeDaw.Audio.FfmpegSetup.RefreshPath();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
