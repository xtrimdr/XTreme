using Avalonia.Input;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The transport's shortcuts while a plugin's window has the keyboard. That window is another process's, so its keys never
/// reach <see cref="OnKey"/>: the host takes the ones listed here before the plugin sees them (not while the plugin has a
/// text field in use), also when its interface is a third process's (WebView2, CEF) or a thread of its own, and says which
/// one went down, and it goes through <see cref="OnKey"/> like any other. Only the
/// transport (<see cref="Shortcuts.Transport"/>): the rest edits the project, too much for a key the plugin may have meant.
/// That guess can miss a plugin's text field, so the window's bar can also send every key to the plugin, or every key to
/// the app (<see cref="pluginKeyRoute"/>, pedido del usuario), for all plugin windows at once.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the hosts were told, in that order: they answer with the position.</summary>
    KeyGesture[] pluginKeys = [];

    /// <summary>"auto", "plugin" or "app": where the keys of a plugin's window go. Kept in config.json ("pluginKeys").</summary>
    string pluginKeyRoute = AppConfig.Get("pluginKeys") switch { "plugin" => "plugin", "app" => "app", _ => "auto" };

    /// <summary>The two buttons of the plugin window's bar that pick <see cref="pluginKeyRoute"/>; both off is the guess.</summary>
    static string KeyRouteBarButtons => $"route-plugin=:{Loc.T("vst.bar.toPlugin")}|route-app=:{Loc.T("vst.bar.toApp")}";

    /// <summary>Windows' virtual keys back to Avalonia's, for <c>app-key</c>.</summary>
    static readonly Dictionary<int, Key> keyOfVirtual = Enum.GetValues<Key>().Where(k => Shortcuts.VirtualKey(k) != 0)
        .GroupBy(Shortcuts.VirtualKey).ToDictionary(g => g.Key, g => g.First());

    static int HostMods(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? 1 : 0) | (m.HasFlag(KeyModifiers.Shift) ? 2 : 0) | (m.HasFlag(KeyModifiers.Alt) ? 4 : 0);

    static KeyModifiers AppMods(int m) =>
        ((m & 1) != 0 ? KeyModifiers.Control : 0) | ((m & 2) != 0 ? KeyModifiers.Shift : 0) | ((m & 4) != 0 ? KeyModifiers.Alt : 0);

    void TellPluginKeys(VstBridge bridge)
    {
        pluginKeys = [.. Shortcuts.Transport.SelectMany(a => Shortcuts.Keys(a.Id)).Where(g => Shortcuts.VirtualKey(g.Key) != 0)];
        bridge.Command("transport-keys " + string.Join(' ', pluginKeys.Select(g => $"{Shortcuts.VirtualKey(g.Key)}:{HostMods(g.KeyModifiers)}")));
        bridge.Command($"key-route {pluginKeyRoute}");
    }

    /// <summary>Which of the bar's two buttons is lit: after its toolbar, which starts them off.</summary>
    void TellKeyRoute(VstBridge bridge) =>
        bridge.Command($"toolbar-state route-plugin={(pluginKeyRoute == "plugin" ? 1 : 0)} route-app={(pluginKeyRoute == "app" ? 1 : 0)}");

    /// <summary>The user changed a key: the plugins already open hear it too.</summary>
    void PluginKeysChanged()
    {
        foreach (var i in instruments.Values) TellPluginKeys(i.Bridge);
        foreach (var e in effects.Values) TellPluginKeys(e.Bridge);
    }

    /// <summary>A click on one of the route buttons: it takes that side, or back to the guess if it was lit.</summary>
    void SetKeyRoute(string route)
    {
        pluginKeyRoute = pluginKeyRoute == route ? "auto" : route;
        try { AppConfig.Set("pluginKeys", pluginKeyRoute); }
        catch (Exception ex) { SessionLog.Write($"no se pudo guardar pluginKeys: {ex.Message}"); }
        foreach (var bridge in instruments.Values.Select(i => i.Bridge).Concat(effects.Values.Select(e => e.Bridge)))
        {
            bridge.Command($"key-route {pluginKeyRoute}");
            TellKeyRoute(bridge);
        }
    }

    /// <summary>True when the line was about the keys of a plugin's window: one went down, or a route button was clicked.</summary>
    bool PluginKeySaid(string line)
    {
        if (line is "toolbar route-plugin" or "toolbar route-app")
        {
            SetKeyRoute(line[13..]);
            return true;
        }
        var n = line.Split(' ');
        if (line.StartsWith("app-key ", StringComparison.Ordinal))
        {
            if (n.Length == 4 && int.TryParse(n[1], out int vk) && int.TryParse(n[2], out int mods) && int.TryParse(n[3], out int scan) && keyOfVirtual.TryGetValue(vk, out var key))
                PluginKeyDown(new KeyGesture(key, AppMods(mods)), scan);
            return true;
        }
        if (!line.StartsWith("transport-key ", StringComparison.Ordinal)) return false;
        if (n.Length == 3 && int.TryParse(n[1], out int i) && int.TryParse(n[2], out int s) && i >= 0 && i < pluginKeys.Length) PluginKeyDown(pluginKeys[i], s);
        return true;
    }

    /// <summary>A key from a plugin's window, through <see cref="OnKey"/>. One of the computer keyboard's notes stays a note, as in <see cref="VirtualKeyDown"/>.</summary>
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
