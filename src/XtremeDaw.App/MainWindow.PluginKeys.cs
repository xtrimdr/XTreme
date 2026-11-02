using Avalonia.Input;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Audio.Vst;

namespace XtremeDaw.App;

/// <summary>
/// The transport's shortcuts while a plugin's window has the keyboard. That window is another process's, so its keys never
/// reach <see cref="OnKey"/>: the host takes the ones listed here before the plugin sees them (not while the plugin has a
/// text field in use) and says which one went down, and it goes through <see cref="OnKey"/> like any other. Only the
/// transport (<see cref="Shortcuts.Transport"/>): the rest edits the project, too much for a key the plugin may have meant.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the hosts were told, in that order: they answer with the position.</summary>
    KeyGesture[] pluginKeys = [];

    static int HostMods(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? 1 : 0) | (m.HasFlag(KeyModifiers.Shift) ? 2 : 0) | (m.HasFlag(KeyModifiers.Alt) ? 4 : 0);

    void TellPluginKeys(VstBridge bridge)
    {
        pluginKeys = [.. Shortcuts.Transport.SelectMany(a => Shortcuts.Keys(a.Id)).Where(g => Shortcuts.VirtualKey(g.Key) != 0)];
        bridge.Command("transport-keys " + string.Join(' ', pluginKeys.Select(g => $"{Shortcuts.VirtualKey(g.Key)}:{HostMods(g.KeyModifiers)}")));
    }

    /// <summary>The user changed a key: the plugins already open hear it too.</summary>
    void PluginKeysChanged()
    {
        foreach (var i in instruments.Values) TellPluginKeys(i.Bridge);
        foreach (var e in effects.Values) TellPluginKeys(e.Bridge);
    }

    /// <summary>True when the line was a key from a plugin's window. One of the computer keyboard's notes stays a note, as in <see cref="VirtualKeyDown"/>.</summary>
    bool PluginKeySaid(string line)
    {
        if (!line.StartsWith("transport-key ", StringComparison.Ordinal)) return false;
        var n = line.Split(' ');
        if (n.Length != 3 || !int.TryParse(n[1], out int i) || !int.TryParse(n[2], out int scan) || i < 0 || i >= pluginKeys.Length) return true;
        var g = pluginKeys[i];
        bool command = (g.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;
        if (DialogOpen || VirtualKeysOn && !command && VirtualKeyboard.CharOf(scan) is var c and not '\0' && virtualKeys.IsKey(c)) return true;
        // the host only says a fresh press: no release comes to clear the key
        transportKeys.Remove(g.Key);
        OnKey(this, new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = g.Key, KeyModifiers = g.KeyModifiers });
        Timeline.SetModifiers(KeyModifiers.None);
        return true;
    }
}
