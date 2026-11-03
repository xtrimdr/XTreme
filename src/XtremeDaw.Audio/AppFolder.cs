namespace XtremeDaw.Audio;

/// <summary>
/// The program's own folder, %APPDATA%\XTreme (settings, recent projects, presets, plugin list, latency, logs).
/// It lives here, in the lowest layer, because the latency store needs it too.
/// </summary>
public static class AppFolder
{
    public const string Name = "XTreme";

    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
}
