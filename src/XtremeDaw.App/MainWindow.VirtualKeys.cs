using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The virtual keyboard (asked by the user: to play without a MIDI device): a mode, because nearly every letter is a
/// shortcut. While it is on the keys of <see cref="VirtualKeyboard"/> play and every other shortcut keeps working; its
/// notes reach the same track as a real keyboard (<see cref="Hears"/>), and a strip under the
/// transport shows them, where they go, and the octave, transposition, velocity and range, remembered in config.json.
/// <para>
/// The notes don't wait for the UI thread: they come straight from <see cref="RawKeys"/> on its own thread, and the
/// window's key events only stop those keys from being shortcuts. <see cref="virtualLive"/> says if a key may play now
/// (no dialog, no text being typed), kept up to date every tick. If Raw Input isn't there, the window's events play.
/// </para>
/// <para>
/// An instrument's window is another process (wtvsthost.exe), which says where its window is and when it has the
/// keyboard: while the mode is on the window grows and carries the same panel at its bottom, a window of ours inside
/// the host's (<see cref="EmbedPanel"/>), and while that window is open the keys play on that plugin's track, not the selected one (<see cref="Hears"/>).
/// Its window also carries a bar with the keyboard's switch, the track's mute and solo and a stop for hung notes
/// (<see cref="ShowPluginBars"/>, <see cref="PluginBarClicked"/>).
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>One copy of the panel: the strip and what it says. There is one under the transport and one under a plugin.</summary>
    sealed class VirtualPanel
    {
        public required VirtualKeyboardView Strip { get; init; }
        public required Control Settings { get; init; }
        public required Control Root { get; init; }
        public readonly TextBlock Target = new() { FontSize = 11, Foreground = Palette.Dim, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        public readonly TextBlock Octave = VirtualValue();
        public readonly ValueBar Transpose = new(-VirtualKeyboard.MaxTranspose, VirtualKeyboard.MaxTranspose, 0, v => v == 0 ? "0" : v.ToString("+0;-0")) { Width = 92, Height = 26 };
        public readonly ValueBar Velocity = new(1, 127, VirtualKeyboard.DefaultVelocity, v => v.ToString()) { Width = 92, Height = 26, WheelStep = 5 };
        public readonly ValueBar Range = new(VirtualKeyboardView.MinOctaves, VirtualKeyboardView.MaxOctaves, VirtualKeyboardView.DefaultOctaves, v => v.ToString()) { Width = 64, Height = 26 };
        public readonly Avalonia.Controls.Primitives.ToggleButton Sustain = new() { Classes = { "chip" }, Height = 26, MinHeight = 0, Padding = new Thickness(8, 0), Focusable = false };
        public readonly TextBlock SustainState = new() { FontSize = 12, Width = 24, VerticalAlignment = VerticalAlignment.Center };
        public readonly Button Scale = new() { Classes = { "chip" }, Height = 26, MinHeight = 0, Padding = new Thickness(8, 0), Focusable = false, MaxWidth = 170 };
        public readonly Avalonia.Controls.Primitives.ToggleButton Chords = new() { Classes = { "chip" }, Height = 26, MinHeight = 0, Padding = new Thickness(8, 0), Focusable = false };
    }

    /// <summary>Under this width (of the plugin's window) the panel inside it is only the keys.</summary>
    const double VirtualSettingsWidth = 1100;
    /// <summary>The panel's height, the same as <c>VirtualKeysHost</c>'s in the window.</summary>
    const double VirtualHeight = 84;

    readonly VirtualKeyboard virtualKeys = new(MidiSetup.Inputs);
    readonly List<VirtualPanel> virtualPanels = [];
    RawKeys? rawKeys;
    volatile bool virtualLive;
    /// <summary>When the last key came through <see cref="RawKeys"/> (Stopwatch ticks), for what it saved.</summary>
    long rawKeyAt;
    long savedTicks, savedWorst;
    int savedCount;

    // the instruments' windows: where each one is, its process, whose the panel follows and whose has the keyboard
    readonly Dictionary<string, PixelRect> pluginWindows = [];
    readonly Dictionary<string, int> pluginProcesses = [];
    string? pluginFollowed;
    /// <summary>The track the keys play on instead of the selected one, with the mode on and its plugin's window open; null = none.</summary>
    string? keyTarget;
    /// <summary>What each instrument's bar shows now, so it is told only when something changes.</summary>
    readonly Dictionary<string, int> pluginBars = [];
    /// <summary>The track's name and color each instrument's window shows, to tell it when they change.</summary>
    readonly Dictionary<string, (string Name, string? Color)> pluginTracks = [];
    /// <summary>The panel inside each instrument's window, by track.</summary>
    readonly Dictionary<string, (Window Window, VirtualPanel Panel)> pluginPanels = [];

    bool VirtualKeysOn => VirtualKeysButton.IsChecked == true;

    static TextBlock VirtualValue() => new() { Classes = { "dim", "mono" }, Width = 28, TextAlignment = Avalonia.Media.TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    void InitVirtualKeys()
    {
        if (int.TryParse(AppConfig.Get("virtualOctave"), out int octave)) virtualKeys.Octave = octave;
        if (int.TryParse(AppConfig.Get("virtualVelocity"), out int velocity)) virtualKeys.Velocity = velocity;
        if (int.TryParse(AppConfig.Get("virtualTranspose"), out int transpose)) virtualKeys.Transpose = transpose;
        if (int.TryParse(AppConfig.Get("virtualRange"), out int range)) virtualRange = Math.Clamp(range, VirtualKeyboardView.MinOctaves, VirtualKeyboardView.MaxOctaves);
        virtualKeys.PlayChords = AppConfig.Get("virtualChords") == "true";
        VirtualKeysHost.Child = NewVirtualPanel(canPick: true).Root;
        // issue #2: the whole window growing, with nothing in the app that scales it
        ScalingChanged += (_, _) => SessionLog.Write($"escala de la ventana {RenderScaling:0.###}  tamaño {ClientSize.Width:0}x{ClientSize.Height:0}  teclado {VirtualKeysOn}{Environment.NewLine}{new StackTrace(1, false)}");

        virtualKeys.Changed += () => Dispatcher.UIThread.Post(ShowVirtualKeys);
        ShowVirtualKeys();
        VirtualKeysButton.IsCheckedChanged += (_, _) =>
        {
            VirtualKeysHost.IsVisible = VirtualKeysOn;
            if (VirtualKeysOn) rawKeys = RawKeys.Start(OnRawKey);
            else StopVirtualKeys();
            UpdateVirtualLive();
            UpdateKeyTarget();
            foreach (var id in pluginProcesses.Keys.ToList())
            {
                if (!VirtualKeysOn) ClosePluginPanel(id);
                if (instruments.TryGetValue(id, out var loaded)) TellPluginPanel(loaded.Bridge);
            }
        };
        AddHandler(KeyUpEvent, OnVirtualKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => virtualKeys.AllOff();
        // a window still open would keep the program from ending
        Closed += (_, _) =>
        {
            StopVirtualKeys();
            foreach (var id in pluginPanels.Keys.ToList()) ClosePluginPanel(id);
        };
    }

    int virtualRange = VirtualKeyboardView.MinOctaves;

    /// <param name="canPick">The scale can be changed from here. Not from the panel inside a plugin's window: it never
    /// takes the focus, which a picker needs to close.</param>
    VirtualPanel NewVirtualPanel(bool canPick)
    {
        var strip = new VirtualKeyboardView(virtualKeys) { Octaves = virtualRange };
        var settings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var root = new DockPanel();
        var panel = new VirtualPanel { Strip = strip, Settings = settings, Root = root };
        settings.Children.Add(new StackPanel
        {
            Width = 130, Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = Loc.T("vkeys.title"), Classes = { "label" } }, panel.Target },
        });
        settings.Children.Add(VirtualStepper(Loc.T("vkeys.octave"), panel.Octave,
            (Loc.T("vkeys.octave.down"), () => virtualKeys.Octave--), (Loc.T("vkeys.octave.up"), () => virtualKeys.Octave++)));
        panel.Transpose.Moved += v => virtualKeys.Transpose = v;
        panel.Velocity.Moved += v => virtualKeys.Velocity = v;
        panel.Range.Moved += SetVirtualRange;
        strip.Zoomed += SetVirtualRange;
        settings.Children.Add(VirtualBar(Loc.T("vkeys.transpose"), panel.Transpose, Loc.T("vkeys.transpose.tip")));
        settings.Children.Add(VirtualBar(Loc.T("vkeys.velocity"), panel.Velocity, Loc.T("vkeys.velocity.tip")));
        settings.Children.Add(VirtualBar(Loc.T("vkeys.range"), panel.Range, Loc.T("vkeys.range.tip")));
        panel.Sustain.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                panel.SustainState,
                new Border
                {
                    BorderBrush = Palette.Faint, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "Shift", FontSize = 10.5 },
                },
            },
        };
        ToolTip.SetTip(panel.Sustain, Loc.T("vkeys.sustain.tip"));
        panel.Sustain.Click += (_, _) => virtualKeys.ToggleSustain();
        settings.Children.Add(new StackPanel
        {
            Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = Loc.T("vkeys.sustain"), Classes = { "label" } }, panel.Sustain },
        });
        // the scale: the same choice as the piano roll's, and with it the keys that play whole chords
        panel.Scale.IsEnabled = canPick;
        ToolTip.SetTip(panel.Scale, Loc.T(canPick ? "scale.pick.tip" : "scale.pick.main"));
        panel.Scale.Click += (_, _) => ShowScalePicker(panel.Scale);
        panel.Chords.Content = Loc.T("vkeys.chords");
        ToolTip.SetTip(panel.Chords, Loc.T("vkeys.chords.tip"));
        panel.Chords.Click += (_, _) =>
        {
            virtualKeys.PlayChords = panel.Chords.IsChecked == true;
            SaveViewPref("virtualChords", virtualKeys.PlayChords ? "true" : "false");
        };
        settings.Children.Add(new StackPanel
        {
            Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = Loc.T("vkeys.scale"), Classes = { "label" } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { panel.Scale, panel.Chords } },
            },
        });
        DockPanel.SetDock(settings, Dock.Left);
        root.Children.Add(settings);
        root.Children.Add(strip);
        virtualPanels.Add(panel);
        FillVirtualPanel(panel);
        return panel;
    }

    void StopVirtualKeys()
    {
        rawKeys?.Dispose();
        rawKeys = null;
        virtualKeys.AllOff();
        virtualKeys.ReleaseSustain();
        if (savedCount > 0)
            SessionLog.Write($"Teclado virtual: {savedCount} teclas, la ventana las recibió {savedTicks * 1000.0 / savedCount / Stopwatch.Frequency:0.0} ms después en promedio, {savedWorst * 1000.0 / Stopwatch.Frequency:0.0} ms como máximo");
        savedTicks = savedWorst = savedCount = 0;
    }

    void SetVirtualRange(int octaves)
    {
        virtualRange = Math.Clamp(octaves, VirtualKeyboardView.MinOctaves, VirtualKeyboardView.MaxOctaves);
        foreach (var p in virtualPanels)
        {
            p.Strip.Octaves = virtualRange;
            p.Range.Value = virtualRange;
        }
        SaveViewPref("virtualRange", virtualRange.ToString());
    }

    static Control VirtualBar(string label, ValueBar bar, string tip)
    {
        ToolTip.SetTip(bar, tip);
        return new StackPanel
        {
            Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = label, Classes = { "label" } }, bar },
        };
    }

    /// <summary>A value with its two buttons, which never take the focus: the keys have to keep playing after a click.</summary>
    static Control VirtualStepper(string label, TextBlock value, (string Tip, Action Run) down, (string Tip, Action Run) up)
    {
        Button Step(string text, (string Tip, Action Run) step)
        {
            var b = new Button { Classes = { "chip" }, Content = text, Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, step.Tip);
            b.Click += (_, _) => step.Run();
            return b;
        }
        return new StackPanel
        {
            Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = label, Classes = { "label" } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { Step("−", down), value, Step("+", up) } },
            },
        };
    }

    void FillVirtualPanel(VirtualPanel p)
    {
        p.Sustain.IsChecked = virtualKeys.Sustain;
        p.SustainState.Text = Loc.T(virtualKeys.Sustain ? "vkeys.sustain.on" : "vkeys.sustain.off");
        p.Octave.Text = $"C{virtualKeys.Octave}";
        p.Velocity.Value = virtualKeys.Velocity;
        p.Transpose.Value = virtualKeys.Transpose;
        p.Range.Value = virtualRange;
        p.Scale.Content = ScaleLabel();
        p.Chords.IsChecked = virtualKeys.PlayChords;
        p.Chords.IsEnabled = virtualKeys.Scale != null;
    }

    int savedOctave = int.MinValue, savedVelocity, savedTranspose;

    /// <summary>The octave, transposition, velocity and pedal on screen, and saved when they move.</summary>
    void ShowVirtualKeys()
    {
        foreach (var p in virtualPanels) FillVirtualPanel(p);
        int octave = virtualKeys.Octave, velocity = virtualKeys.Velocity, transpose = virtualKeys.Transpose;
        if (octave == savedOctave && velocity == savedVelocity && transpose == savedTranspose) return;
        bool first = savedOctave == int.MinValue;
        (savedOctave, savedVelocity, savedTranspose) = (octave, velocity, transpose);
        if (first) return;
        SaveViewPref("virtualOctave", octave.ToString());
        SaveViewPref("virtualVelocity", velocity.ToString());
        SaveViewPref("virtualTranspose", transpose.ToString());
    }

    /// <summary>Which tracks the keys play on, or what to do when no track would hear them.</summary>
    void ShowVirtualTarget()
    {
        keyTrack = KeyTrack();
        var names = tracks.Where(Hears).Select(t => t.Track.Name).ToList();
        string text = names.Count == 0 ? Loc.T("vkeys.noTrack") : Loc.T("vkeys.track", string.Join(", ", names));
        foreach (var p in virtualPanels) p.Target.Text = text;
    }

    // --- under the plugin's window ------------------------------------------------------------------

    /// <summary>A line from an instrument's process about its window.</summary>
    void VirtualEditorSaid(string trackId, int process, string line)
    {
        if (line == "editor-active")
        {
            pluginProcesses[trackId] = process;
            RawKeys.AlsoFront = [.. pluginProcesses.Values];
            pluginFollowed = trackId;
            UpdateVirtualLive();
            UpdateKeyTarget();
        }
        else if (line.StartsWith("editor-panel ", StringComparison.Ordinal))
        {
            if (VirtualKeysOn && long.TryParse(line[13..], out long container)) EmbedPanel(trackId, (nint)container);
        }
        else if (line == "editor-closed") ForgetPluginWindow(trackId);
        else if (line.StartsWith("toolbar ", StringComparison.Ordinal)) PluginBarClicked(trackId, line[8..]);
        else if (line.StartsWith("editor-rect", StringComparison.Ordinal))
        {
            pluginProcesses[trackId] = process;
            RawKeys.AlsoFront = [.. pluginProcesses.Values];
            var n = line.Split(' ');
            if (n.Length == 5 && int.TryParse(n[1], out int left) && int.TryParse(n[2], out int top) && int.TryParse(n[3], out int right) && int.TryParse(n[4], out int bottom))
            {
                pluginWindows[trackId] = new PixelRect(left, top, right - left, bottom - top);
                pluginFollowed ??= trackId;
            }
            else pluginWindows.Remove(trackId);
            UpdateKeyTarget();
        }
    }

    /// <summary>Its window closed, or the plugin is gone: the panel follows another open one, if there is any.</summary>
    void ForgetPluginWindow(string trackId)
    {
        pluginWindows.Remove(trackId);
        if (pluginProcesses.Remove(trackId)) RawKeys.AlsoFront = [.. pluginProcesses.Values];
        pluginBars.Remove(trackId);
        pluginTracks.Remove(trackId);
        if (pluginFollowed == trackId) pluginFollowed = pluginWindows.Keys.FirstOrDefault();
        ClosePluginPanel(trackId);
        UpdateKeyTarget();
    }

    /// <summary>Which track the keys go to, now that a window opened, closed or took the keyboard.</summary>
    void UpdateKeyTarget()
    {
        keyTarget = VirtualKeysOn && pluginFollowed != null && pluginWindows.ContainsKey(pluginFollowed) ? pluginFollowed : null;
        midiListenKey = 0;
        ShowMidiListeners();
        ShowVirtualTarget();
    }

    /// <summary>The room for the panel in an instrument's window, by the mode, at 100 % (the host scales it to its screen): it answers where it goes (<see cref="EmbedPanel"/>).</summary>
    void TellPluginPanel(XtremeDaw.Audio.Vst.VstBridge bridge) =>
        bridge.Command($"keys-panel {(VirtualKeysOn ? (int)VirtualHeight : 0)}");

    /// <summary>
    /// The panel inside the plugin's window: a window of ours made a child of the host's, so it moves and minimizes with
    /// it. It dies with that window (it closed, or the plugin crashed): <see cref="Window.Closed"/> cleans up.
    /// </summary>
    void EmbedPanel(string trackId, nint container)
    {
        ClosePluginPanel(trackId);
        var panel = NewVirtualPanel(canPick: false);
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None, CanResize = false, ShowInTaskbar = false, ShowActivated = false, Focusable = false,
            Background = Palette.Panel, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000),
            Width = VirtualHeight, Height = VirtualHeight,
            Content = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6), Child = panel.Root },
        };
        window.SizeChanged += (_, e) => panel.Settings.IsVisible = e.NewSize.Width >= VirtualSettingsWidth;
        window.ScalingChanged += (_, _) => SessionLog.Write($"escala del panel del teclado {window.RenderScaling:0.###}");
        window.Closed += (_, _) =>
        {
            if (pluginPanels.TryGetValue(trackId, out var now) && now.Window == window) pluginPanels.Remove(trackId);
            virtualPanels.Remove(panel);
            panel.Strip.Detach();
        };
        pluginPanels[trackId] = (window, panel);
        window.Show();
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        SetWindowLongPtrW(hwnd, GwlStyle, (nint)((long)GetWindowLongPtrW(hwnd, GwlStyle) & ~WsPopup) | WsChild);
        SetWindowLongPtrW(hwnd, GwlExStyle, GetWindowLongPtrW(hwnd, GwlExStyle) | WsExNoActivate);
        SetParent(hwnd, container);
        GetClientRect(container, out var room);
        SetWindowPos(hwnd, 0, 0, 0, room.Right, room.Bottom, SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        ShowVirtualTarget();
    }

    void ClosePluginPanel(string trackId)
    {
        if (pluginPanels.TryGetValue(trackId, out var open)) open.Window.Close();
    }

    const int GwlExStyle = -20, GwlStyle = -16;
    const nint WsExNoActivate = 0x08000000, WsChild = 0x40000000;
    const long WsPopup = 0x80000000L;
    const uint SwpNoActivate = 0x10, SwpFrameChanged = 0x20, SwpShowWindow = 0x40;
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32")] static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32")] static extern nint SetParent(nint child, nint parent);
    [DllImport("user32")] static extern bool GetClientRect(nint hwnd, out NativeRect r);
    [DllImport("user32")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

    // --- the plugin's window ------------------------------------------------------------------------

    static string Hex(Avalonia.Media.IBrush brush) => brush is Avalonia.Media.ISolidColorBrush b ? $"{b.Color.R:x2}{b.Color.G:x2}{b.Color.B:x2}" : "000000";

    /// <summary>The window of a plugin in the app's colors, before it opens.</summary>
    void DressPluginWindow(XtremeDaw.Audio.Vst.VstBridge bridge, string title)
    {
        bridge.Command($"look {Hex(Palette.Panel)} {Hex(Palette.Raised)} {Hex(Palette.Fg)} {Hex(Palette.Dim)} {Hex(Palette.Accent)} {Hex(Palette.Line)}");
        bridge.Command($"title {title}");
        TellPluginKeys(bridge);
    }

    /// <summary>The window's title and its bar say whose it is.</summary>
    void TellPluginTrack(XtremeDaw.Audio.Vst.VstBridge bridge, string plugin, Track track)
    {
        pluginTracks[track.Id] = (track.Name, track.Color);
        bridge.Command($"title {plugin} · {track.Name}");
        bridge.Command($"toolbar-track {track.Color?.TrimStart('#')}|{track.Name.Replace('|', '/')}|{plugin}");
    }

    /// <summary>Every tick: the bar of each open instrument window catches up, if something it shows changed.</summary>
    void ShowPluginBars()
    {
        if (pluginProcesses.Count == 0) return;
        foreach (var t in tracks)
        {
            string id = t.Track.Id;
            if (!pluginProcesses.ContainsKey(id) || !instruments.TryGetValue(id, out var loaded)) continue;
            if (!pluginTracks.TryGetValue(id, out var shownTrack) || shownTrack.Name != t.Track.Name || shownTrack.Color != t.Track.Color)
                TellPluginTrack(loaded.Bridge, loaded.Plugin.Name, t.Track);
            int state = (VirtualKeysOn ? 1 : 0) | (t.Channel.Muted ? 2 : 0) | (t.Channel.Solo ? 4 : 0);
            if (pluginBars.TryGetValue(id, out int shown) && shown == state) continue;
            pluginBars[id] = state;
            loaded.Bridge.Command($"toolbar-state keys={state & 1} mute={state >> 1 & 1} solo={state >> 2 & 1}");
        }
    }

    void PluginBarClicked(string trackId, string button)
    {
        var t = tracks.FirstOrDefault(x => x.Track.Id == trackId);
        if (t == null) return;
        switch (button)
        {
            case "keys":
                VirtualKeysButton.IsChecked = !VirtualKeysOn;
                break;
            case "listen" when instruments.TryGetValue(trackId, out var loaded):
                ToggleLaneListen(loaded.Bridge, t, AutoTarget.Instrument);
                break;
            case "mute":
                EditTrack(t, x => x.Channel.Muted = !x.Channel.Muted, "mute");
                break;
            case "solo":
                EditTrack(t, x => x.Channel.Solo = !x.Channel.Solo, "solo");
                break;
            case "panic":
                virtualKeys.AllOff();
                virtualKeys.ReleaseSustain();
                MidiSetup.Inputs.Push(new MidiEvent { Ticks = Stopwatch.GetTimestamp(), Port = MidiInputs.VirtualPort, Channel = 1, Kind = MidiKind.AllOff });
                t.Instrument?.Panic();
                break;
        }
    }

    // --- the keys -----------------------------------------------------------------------------------

    /// <summary>Something on screen takes the keys ahead of the shortcuts.</summary>
    bool DialogOpen => Unsaved.IsVisible || TempoAsk.IsVisible || Feedback.IsVisible || Stems.IsVisible || ComponentsHelp.IsVisible || Export.IsVisible || Settings.IsVisible || About.IsVisible || HelpPanel.IsVisible || News.IsVisible || Projects.IsVisible;

    /// <summary>Every tick: whether a key may play now, the same conditions as <see cref="OnKey"/> reaching <see cref="VirtualKeyDown"/>.</summary>
    void UpdateVirtualLive()
    {
        virtualLive = VirtualKeysOn && !DialogOpen && (FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<TextBox>(true) == null;
        ShowPluginBars();
    }

    /// <summary><see cref="RawKeys"/>' thread. A release always counts: the key may have played before a dialog opened.</summary>
    void OnRawKey(int scan, bool down)
    {
        char c = VirtualKeyboard.CharOf(scan);
        if (!down)
        {
            if (VirtualKeyboard.IsShift(scan)) virtualKeys.ShiftKey(scan == 0x2A, false);
            else if (c != '\0') virtualKeys.KeyUp(c);
            return;
        }
        if (!virtualLive || !RawKeys.ProgramInFront() || RawKeys.CommandHeld()) return;
        if (VirtualKeyboard.IsShift(scan)) virtualKeys.ShiftKey(scan == 0x2A, true);
        else if (c != '\0' && virtualKeys.KeyDown(c)) Interlocked.CompareExchange(ref rawKeyAt, Stopwatch.GetTimestamp(), 0);
    }

    /// <summary>
    /// A key down while the mode is on. True when it is one of the keyboard's, so it isn't a shortcut: with Ctrl or Alt it
    /// always is one. The note already went (<see cref="OnRawKey"/>): here it only stops the key, and counts how long
    /// the window took to get it, which is what the note would have waited.
    /// </summary>
    bool VirtualKeyDown(KeyEventArgs e)
    {
        if (!VirtualKeysOn || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return false;
        if (rawKeys == null)
        {
            if (e.Key is Key.LeftShift or Key.RightShift) virtualKeys.ShiftKey(e.Key == Key.LeftShift, true);
            return VirtualChar(e.PhysicalKey) is var mine and not '\0' && virtualKeys.KeyDown(mine);
        }
        if (VirtualChar(e.PhysicalKey) is var c and not '\0' && virtualKeys.IsKey(c))
        {
            long at = Interlocked.Exchange(ref rawKeyAt, 0);
            if (at == 0) return true;
            long took = Stopwatch.GetTimestamp() - at;
            savedTicks += took;
            savedWorst = Math.Max(savedWorst, took);
            savedCount++;
            return true;
        }
        return false;
    }

    /// <summary>Whatever else is on screen by now, a key that played through the window has to end.</summary>
    void OnVirtualKeyUp(object? sender, KeyEventArgs e)
    {
        if (rawKeys != null) return;
        if (e.Key is Key.LeftShift or Key.RightShift) virtualKeys.ShiftKey(e.Key == Key.LeftShift, false);
        else if (VirtualChar(e.PhysicalKey) is var c and not '\0' && virtualKeys.KeyUp(c)) e.Handled = true;
    }

    static char VirtualChar(PhysicalKey key) => key switch
    {
        >= PhysicalKey.A and <= PhysicalKey.Z => (char)('a' + (key - PhysicalKey.A)),
        PhysicalKey.Comma => ',',
        PhysicalKey.Semicolon => ';',
        PhysicalKey.Quote => '\'',
        PhysicalKey.BracketRight => ']',
        _ => '\0',
    };
}
