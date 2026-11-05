using Avalonia.Input;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Where the keys of a plugin's window go, picked with one button on its bar (pedido del usuario: on or off, no guess),
/// for every plugin window at once (instruments and effects) and kept in config.json ("pluginKeys"). Off, the default:
/// every key is the app's (the transport, the shortcuts, the computer keyboard's notes), but the system's (Alt+Tab,
/// Alt+F4…). That window is another process's, so its keys never reach <see cref="OnKey"/>: the host takes them before
/// the plugin sees them, also when its interface is a third process's (WebView2, CEF) or a thread of its own, says which
/// one went down, and it goes through <see cref="OnKey"/> like any other. On: every key is the plugin's while its window
/// has the keyboard, the computer keyboard's notes too (<see cref="UpdatePluginFront"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>Every key of a plugin's window goes to the plugin. Kept in config.json ("pluginKeys": "plugin", or "app").</summary>
    bool keysToPlugin = AppConfig.Get("pluginKeys") == "plugin";

    /// <summary>The bar's button that turns <see cref="keysToPlugin"/> on and off.</summary>
    static string KeyRouteBarButtons => $"route-plugin=:{Loc.T("vst.bar.keysToPlugin")}";

    /// <summary>Windows' virtual keys back to Avalonia's, for <c>app-key</c>.</summary>
    static readonly Dictionary<int, Key> keyOfVirtual = Enum.GetValues<Key>().Where(k => Shortcuts.VirtualKey(k) != 0)
        .GroupBy(Shortcuts.VirtualKey).ToDictionary(g => g.Key, g => g.First());

    static KeyModifiers AppMods(int m) =>
        ((m & 1) != 0 ? KeyModifiers.Control : 0) | ((m & 2) != 0 ? KeyModifiers.Shift : 0) | ((m & 4) != 0 ? KeyModifiers.Alt : 0);

    /// <summary>Where the window's keys go, before it opens.</summary>
    void TellPluginKeys(VstBridge bridge) => bridge.Command($"key-route {(keysToPlugin ? "plugin" : "app")}");

    /// <summary>Whether the bar's button is lit: after its toolbar, which starts it off.</summary>
    void TellKeyRoute(VstBridge bridge) => bridge.Command($"toolbar-state route-plugin={(keysToPlugin ? 1 : 0)}");

    /// <summary>A click on the bar's button: every window takes the other side.</summary>
    void ToggleKeysToPlugin()
    {
        keysToPlugin = !keysToPlugin;
        try { AppConfig.Set("pluginKeys", keysToPlugin ? "plugin" : "app"); }
        catch (Exception ex) { SessionLog.Write($"no se pudo guardar pluginKeys: {ex.Message}"); }
        foreach (var bridge in instruments.Values.Select(i => i.Bridge).Concat(effects.Values.Select(e => e.Bridge)))
        {
            TellPluginKeys(bridge);
            TellKeyRoute(bridge);
        }
        UpdatePluginFront();
    }

    /// <summary>Processes of effects whose window is open, by slot (<see cref="EffectKey"/>): their window counts as the app's for the computer keyboard's notes.</summary>
    readonly Dictionary<string, int> effectProcesses = [];

    /// <summary>
    /// Which plugin windows count as the app's for <see cref="RawKeys"/>: with the keys going to the app, every open one
    /// (instruments and effects), so the computer keyboard's notes play there; with them going to the plugin, none.
    /// </summary>
    void UpdatePluginFront() =>
        RawKeys.AlsoFront = keysToPlugin ? [] : [.. pluginProcesses.Values, .. effectProcesses.Values];

    /// <summary>A line from an effect's process about its window.</summary>
    void EffectWindowSaid(string key, int process, string line)
    {
        if (line == "editor-active" || line.StartsWith("editor-rect", StringComparison.Ordinal))
        {
            if (effectProcesses.TryGetValue(key, out int known) && known == process) return;
            effectProcesses[key] = process;
            UpdatePluginFront();
        }
        else if (line == "editor-closed") ForgetEffectWindow(key);
    }

    void ForgetEffectWindow(string key)
    {
        if (effectProcesses.Remove(key)) UpdatePluginFront();
    }

    /// <summary>True when the line was about the keys of a plugin's window: one went down, or the route button was clicked.</summary>
    bool PluginKeySaid(string line)
    {
        if (line == "toolbar route-plugin")
        {
            ToggleKeysToPlugin();
            return true;
        }
        if (!line.StartsWith("app-key ", StringComparison.Ordinal)) return false;
        var n = line.Split(' ');
        if (n.Length == 4 && int.TryParse(n[1], out int vk) && int.TryParse(n[2], out int mods) && int.TryParse(n[3], out int scan) && keyOfVirtual.TryGetValue(vk, out var key))
            PluginKeyDown(new KeyGesture(key, AppMods(mods)), scan);
        return true;
    }

    /// <summary>
    /// A key from a plugin's window, through <see cref="OnKey"/>. One of the computer keyboard's notes already played
    /// (<see cref="OnRawKey"/>: the window counts as the app's, <see cref="UpdatePluginFront"/>), so it stops here, as in
    /// <see cref="VirtualKeyDown"/>.
    /// </summary>
    void PluginKeyDown(KeyGesture g, int scan)
    {
        bool command = (g.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;
        if (DialogOpen || VirtualKeysOn && !command && VirtualKeyboard.CharOf(scan) is var c and not '\0' && virtualKeys.IsKey(c)) return;
        // the host only says a fresh press: no release comes to clear the key
        transportKeys.Remove(g.Key);
        OnKey(this, new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = g.Key, KeyModifiers = g.KeyModifiers });
        Timeline.SetModifiers(KeyModifiers.None);
    }
}
