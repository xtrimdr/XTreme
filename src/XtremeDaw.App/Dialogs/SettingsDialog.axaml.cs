using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Video;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// Port of SettingsDialog (the web app's SettingsDialog.tsx): tabs "Entradas y salidas",
/// "Cámara" and "Sincronización", every control live. Same as the web except what ASIO imposes:
/// "Interfaz" lists the ASIO drivers and "Dispositivo" the driver's output pairs (no "Predeterminada de
/// Windows"), texts that say "navegador" say "driver", and the driver and output are remembered between
/// sessions (the web remembers the output; it re-picks the interface by name). The camera tab edits
/// <see cref="LiveCamera"/> (the cameras are the ones FFmpeg lists through DirectShow). Like the web, gain,
/// channels, monitoring, the fine adjustment and the camera settings live only in memory, and the
/// calibration is stored per input + output pair. The "Atajos de teclado" tab is native only (the web's keys are
/// fixed): it edits <see cref="Shortcuts"/>; a click on a key waits for the next press, Esc cancels. The timeline's
/// wheel gestures wait for a wheel turn instead, and take the modifiers held during it.
/// "Guardado" is native only too: it picks <see cref="SaveMode"/>; so are "Plugins" (SettingsDialog.Plugins.cs) and
/// "MIDI" (SettingsDialog.Midi.cs).
/// </summary>
public sealed partial class SettingsDialog : UserControl
{
    static readonly CameraResMode[] ResModes = [CameraResMode.Native, CameraResMode.R1080, CameraResMode.R720, CameraResMode.R480];

    PlaybackEngine? engine;
    LiveCamera? cam;
    Func<bool> busy = () => false;
    CancellationTokenSource? calibration;
    CalibrationMethod? running;
    CalibrationProgress? progress;
    string? calError;
    bool reopening;
    bool filling;

    readonly StackPanel audioPage = new(), cameraPage = new(), syncPage = new(), shortcutsPage = new(), languagePage = new(), savePage = new(), viewPage = new();
    /// <summary>The action waiting for a key in "Atajos de teclado", and what the last change took from another one.</summary>
    string? capturing, capturingWheel;
    string? shortcutNote;
    readonly ComboBox driverBox = Combo(), outputBox = Combo();
    readonly List<ToggleButton> modeButtons = [];
    readonly InspectorSlider gain;
    readonly InspectorSlider transportFade;
    readonly List<ToggleButton> saveModeButtons = [];
    readonly InspectorSlider saveMinutes;
    readonly TextBlock saveHelp = Help("");
    readonly List<ToggleButton> encoderButtons = [];
    readonly TextBlock encoderHelp = Help("");
    readonly TextBlock importHelp = Help("");
    readonly List<ToggleButton> importButtons = [];
    /// <summary>What FFmpeg can do here; null while <see cref="VideoEncoders.DetectAsync"/> is still probing.</summary>
    FfmpegCaps? encoderCaps;
    readonly TextBlock outputHelp = Help("");
    readonly TextBlock bufferText = new() { FontSize = 12, Foreground = Palette.Dim, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button panelButton = new() { Classes = { "btn", "outline" }, Height = 28, Padding = new Thickness(10, 0), FontSize = 12, Content = Loc.T("set.panelButton") };
    DispatcherTimer? resetRetry;
    readonly ToggleSwitch monitorSwitch = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock pairText = new() { FontSize = 11, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel readout = new() { Spacing = 4 };
    readonly StackPanel calBox = new() { Spacing = 10 };
    readonly InspectorSlider fine;
    readonly InspectorSlider cameraLatency;
    readonly TextBlock cameraRes = new() { FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center };
    readonly List<ToggleButton> camKindButtons = [];
    readonly ComboBox camBox = Combo(), resolutionBox = Combo();
    readonly StackPanel driverFields = new() { Spacing = 14 };
    readonly Control ipField;
    readonly TextBox ipBox = new() { Height = 36, FontSize = 14, Classes = { "mono" }, Watermark = LiveCamera.DefaultIp, VerticalContentAlignment = VerticalAlignment.Center };
    readonly ToggleButton mirrorButton = new() { Classes = { "chip" }, Width = 32, Height = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };

    /// <summary>The input meter of the "Entrada" section; the window feeds it from the same read as the title bar's.</summary>
    public StereoMeter Meter { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    /// <summary>"Ajuste fino de audio (canales nuevos)": added to the latency when recording.</summary>
    public double OverdubOffsetMs { get; private set; }
    public event Action? Dismissed;
    /// <summary>The driver was reopened or the calibration changed: the window refreshes what it shows.</summary>
    public event Action? AudioChanged;

    public SettingsDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => HideDialog();
        AudioTab.Click += (_, _) => ShowTab("audio");
        MidiTab.Click += (_, _) => ShowTab("midi");
        CameraTab.Click += (_, _) => ShowTab("camera");
        SyncTab.Click += (_, _) => ShowTab("sync");
        ShortcutsTab.Click += (_, _) => ShowTab("shortcuts");
        LanguageTab.Click += (_, _) => ShowTab("language");
        SaveTab.Click += (_, _) => ShowTab("save");
        ViewTab.Click += (_, _) => ShowTab("view");
        PluginsTab.Click += (_, _) => ShowTab("plugins");
        ComponentsTab.Click += (_, _) => ShowTab("components");
        BuildLanguage();
        BuildPlugins();
        BuildMidi();
        BuildView();
        saveMinutes = BuildSave();
        Shortcuts.Changed += BuildShortcuts;
        AddHandler(PointerWheelChangedEvent, OnCaptureWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        InitFloating();

        // --- Entradas y salidas
        var refresh = Act("", Loc.T("set.refresh"), () => Refresh());
        var input = Section(Loc.T("set.input"), refresh);
        driverBox.SelectionChanged += async (_, _) =>
        {
            if (filling || driverBox.SelectedItem is not string d || d == engine?.DriverName) return;
            await Reopen(d, 0);
        };
        input.Children.Add(Field(Loc.T("set.driver"), driverBox));
        input.Children.Add(Meter);
        // native only: the buffer size lives in the driver's own panel (the web had the browser's fixed buffer)
        ToolTip.SetTip(panelButton, Loc.T("set.panel.tip"));
        panelButton.Click += (_, _) => ShowDriverPanel();
        input.Children.Add(Field(Loc.T("set.buffer"), Row(bufferText, panelButton)));
        var modes = new UniformGrid { Columns = 3 };
        foreach (var (mode, text, tip) in new[]
        {
            (InputChannelMode.Stereo, Loc.T("set.mode.stereo"), Loc.T("set.mode.stereo.tip")),
            (InputChannelMode.Ch1Mono, Loc.T("set.mode.mono1"), Loc.T("set.mode.mono1.tip")),
            (InputChannelMode.Ch2Mono, Loc.T("set.mode.mono2"), Loc.T("set.mode.mono2.tip")),
        })
        {
            var b = new ToggleButton { Content = text, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { if (engine != null) engine.InputChannelMode = mode; Refresh(); };
            b.Tag = mode;
            modeButtons.Add(b);
            modes.Children.Add(b);
        }
        input.Children.Add(Field(Loc.T("set.channels"), new Border { Classes = { "seg" }, Child = modes }));
        gain = new InspectorSlider(Loc.T("set.gain"), -24, 24, 1, InputProcessor.DefaultInputGainDb, v => $"{v:+0;-0;0} dB",
            v =>
            {
                if (engine != null) engine.InputGainDb = v;
                string text = v.ToString(CultureInfo.InvariantCulture);
                _ = Task.Run(() =>
                {
                    try { AppConfig.Set("audio.inputGainDb", text); }
                    catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
                });
                Refresh();
            }, Loc.T("set.gain.hint"));
        input.Children.Add(gain);
        audioPage.Children.Add(Card(input));

        var output = Section(Loc.T("set.output"));
        outputBox.SelectionChanged += async (_, _) =>
        {
            if (filling || engine == null || outputBox.SelectedIndex < 0 || outputBox.SelectedIndex * 2 == engine.OutputOffset) return;
            await Reopen(engine.DriverName, outputBox.SelectedIndex * 2);
        };
        output.Children.Add(Field(Loc.T("set.device"), outputBox));
        output.Children.Add(outputHelp);
        var monitorLabel = new TextBlock { Text = Loc.T("set.monitor"), FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        ToolTip.SetTip(monitorLabel, Loc.T("set.monitor.tip"));
        monitorSwitch.IsCheckedChanged += (_, _) => { if (!filling && engine != null) engine.SoftwareMonitor = monitorSwitch.IsChecked == true; };
        output.Children.Add(Row(monitorLabel, monitorSwitch));
        // native only: how long play and stop fade, saved like the input gain
        transportFade = new InspectorSlider(Loc.T("set.transportFade"), PlaybackEngine.MinTransportFadeMs, PlaybackEngine.MaxTransportFadeMs, 1,
            PlaybackEngine.DefaultTransportFadeMs, v => $"{v:0} ms",
            v =>
            {
                if (engine != null) engine.TransportFadeMs = v;
                string text = v.ToString(CultureInfo.InvariantCulture);
                _ = Task.Run(() =>
                {
                    try { AppConfig.Set("audio.transportFadeMs", text); }
                    catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
                });
            }, Loc.T("set.transportFade.hint"));
        output.Children.Add(transportFade);
        audioPage.Children.Add(Card(output));

        // --- Cámara
        var camera = Section(Loc.T("set.camera"), cameraRes);
        camera.Children.Add(new ComponentList { Only = "ffmpeg", OnlyMissing = true });
        var camKind = new UniformGrid { Columns = 2 };
        foreach (var (ip, text) in new[] { (false, Loc.T("set.cam.webcam")), (true, Loc.T("set.cam.ip")) })
        {
            var b = new ToggleButton { Content = text, Tag = ip, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += async (_, _) =>
            {
                if (cam == null) return;
                Refresh();
                await cam.SetUseIp(ip);
            };
            camKindButtons.Add(b);
            camKind.Children.Add(b);
        }
        camera.Children.Add(new Border { Classes = { "seg" }, Child = camKind });
        camBox.SelectionChanged += async (_, _) =>
        {
            if (filling || cam == null || camBox.SelectedItem is not string d || !cam.Devices.Contains(d)) return;
            await cam.SelectDevice(d);
        };
        var camRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        camRow.Children.Add(camBox);
        var camRefresh = Act("\uE72C", Loc.T("set.refresh"), () => _ = cam?.Refresh());
        camRefresh.Width = camRefresh.Height = 36;
        Grid.SetColumn(camRefresh, 1);
        camRow.Children.Add(camRefresh);
        driverFields.Children.Add(Field(Loc.T("set.device"), camRow));
        resolutionBox.ItemsSource = new[] { Loc.T("set.res.auto"), "1080p · 30 fps", "720p · 30 fps", "480p · 30 fps" };
        resolutionBox.SelectionChanged += async (_, _) =>
        {
            if (filling || cam == null || resolutionBox.SelectedIndex < 0) return;
            await cam.SetMode(ResModes[resolutionBox.SelectedIndex]);
        };
        driverFields.Children.Add(Field(Loc.T("set.resolution"), resolutionBox));
        camera.Children.Add(driverFields);
        ipBox.TextChanged += (_, _) => { if (!filling && cam != null) cam.IpInput = ipBox.Text ?? ""; };
        ipBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || cam == null) return;
            e.Handled = true;
            await cam.ConnectIp();
        };
        var connect = new Button { Content = Loc.T("set.connect"), Classes = { "btn", "primary" } };
        connect.Click += async (_, _) => { if (cam != null) await cam.ConnectIp(); };
        var ipRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        ipRow.Children.Add(ipBox);
        Grid.SetColumn(connect, 1);
        ipRow.Children.Add(connect);
        ipField = Field(Loc.T("set.ip"), ipRow);
        camera.Children.Add(ipField);
        mirrorButton.Content = new TextBlock { Classes = { "icon" }, Text = "\uE8AB", FontSize = 13 };
        ToolTip.SetTip(mirrorButton, Loc.T("set.mirror.tip"));
        mirrorButton.IsCheckedChanged += (_, _) => { if (!filling && cam != null) cam.Mirror = mirrorButton.IsChecked == true; };
        camera.Children.Add(Row(new TextBlock { Text = Loc.T("set.mirror"), FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center }, mirrorButton));
        cameraPage.Children.Add(Card(camera));

        // --- Sincronización
        var sync = Section(Loc.T("set.sync"));
        sync.Children.Add(pairText);
        sync.Children.Add(new Border { Background = Palette.Raised, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Child = readout });
        sync.Children.Add(new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = calBox });
        fine = new InspectorSlider(Loc.T("set.fine"), -100, 100, 1, 0, v => v > 0 ? $"+{v:0} ms" : $"{v:0} ms",
            v => { OverdubOffsetMs = v; Refresh(); }, Loc.T("set.fine.hint"));
        sync.Children.Add(fine);
        cameraLatency = new InspectorSlider(Loc.T("set.camLatency"), 0, 200, 5, LiveCamera.DefaultLatencyMs, v => $"{v:0} ms",
            v => { if (cam != null) cam.LatencyMs = v; }, Loc.T("set.camLatency.hint"));
        sync.Children.Add(cameraLatency);
        syncPage.Children.Add(Card(sync));
    }

    /// <summary>The engine whose devices and calibration this edits, the camera, and what says recording is going on.</summary>
    public void Attach(PlaybackEngine e, LiveCamera camera, Func<bool> recordingBusy)
    {
        engine = e;
        cam = camera;
        busy = recordingBusy;
        camera.Changed += () => Dispatcher.UIThread.Post(Refresh);
        e.DriverResetRequested += () => Dispatcher.UIThread.Post(DriverReset);
    }

    void ShowDriverPanel()
    {
        if (engine == null) return;
        try { engine.ShowDriverPanel(); }
        catch (Exception x) { bufferText.Text = Loc.T("set.panelFail", x.Message); }
    }

    /// <summary>
    /// The driver asked to be reopened (its buffer changed). While recording or calibrating it waits and tries
    /// again every second, so a take is never cut by a click in the driver's panel.
    /// </summary>
    void DriverReset()
    {
        if (engine is not { IsOpen: true } e) return;
        if (busy() || e.Calibrating || reopening)
        {
            resetRetry ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => DriverReset());
            resetRetry.Start();
            return;
        }
        resetRetry?.Stop();
        _ = Reopen(e.DriverName, e.OutputOffset);
    }

    public void Show(string startTab = "audio")
    {
        IsVisible = true;
        if (!placed) Center();
        ShowTab(startTab);
        Focus();
    }

    public void HideDialog()
    {
        calibration?.Cancel();
        StopCapture();
        StopMidi();
        IsVisible = false;
        Dismissed?.Invoke();
    }

    /// <summary>The panel's place and size over the window; kept while the app runs.</summary>
    double panelX, panelY, panelW = DefaultW, panelH = DefaultH;
    bool placed;
    /// <summary>What a drag on the header (move) or the grip (resize) started from.</summary>
    Point dragFrom;
    double fromX, fromY, fromW, fromH;
    bool moving, resizing;
    const double DefaultW = 768, DefaultH = 640, MinW = 560, MinH = 360;
    /// <summary>How much of the panel stays inside the window when dragged or when the window shrinks.</summary>
    const double KeepVisible = 48;

    /// <summary>No scrim and no click-outside close (#184): the window behind takes clicks and keys, so a setting of
    /// the interface can be tried while the panel stays open. The header moves it, the corner grip resizes it.</summary>
    void InitFloating()
    {
        Header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(Header).Properties.IsLeftButtonPressed || e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
            StartDrag(e, move: true);
        };
        Grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(Grip).Properties.IsLeftButtonPressed) return;
            StartDrag(e, move: false);
        };
        foreach (var c in new Control[] { Header, Grip })
        {
            c.PointerMoved += (_, e) => Drag(e);
            c.PointerReleased += (_, _) => moving = resizing = false;
            c.PointerCaptureLost += (_, _) => moving = resizing = false;
        }
        SizeChanged += (_, _) => { if (placed) Place(); };
    }

    void StartDrag(PointerPressedEventArgs e, bool move)
    {
        dragFrom = e.GetPosition(this);
        (fromX, fromY, fromW, fromH) = (panelX, panelY, panelW, panelH);
        moving = move;
        resizing = !move;
        e.Pointer.Capture((IInputElement?)e.Source);
        e.Handled = true;
    }

    void Drag(PointerEventArgs e)
    {
        if (!moving && !resizing) return;
        var d = e.GetPosition(this) - dragFrom;
        if (moving) (panelX, panelY) = (fromX + d.X, fromY + d.Y);
        else (panelW, panelH) = (fromW + d.X, fromH + d.Y);
        Place();
    }

    void Center()
    {
        if (Bounds.Width <= 0) { Dispatcher.UIThread.Post(() => { if (!placed && IsVisible) Center(); }, DispatcherPriority.Loaded); return; }
        panelW = Math.Min(DefaultW, Bounds.Width - 32);
        panelH = Math.Min(DefaultH, Bounds.Height * 0.88);
        panelX = (Bounds.Width - panelW) / 2;
        panelY = (Bounds.Height - panelH) / 2;
        placed = true;
        Place();
    }

    /// <summary>Clamps the size to the window and keeps the header reachable, then lays the panel out.</summary>
    void Place()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        panelW = Math.Clamp(panelW, Math.Min(MinW, w), w);
        panelH = Math.Clamp(panelH, Math.Min(MinH, h), h);
        panelX = Math.Clamp(panelX, KeepVisible - panelW, w - KeepVisible);
        panelY = Math.Clamp(panelY, 0, Math.Max(0, h - KeepVisible));
        Panel.Width = panelW;
        Panel.Height = panelH;
        Panel.Margin = new Thickness(panelX, panelY, 0, 0);
    }

    /// <summary>Escape closes (cancelling a running calibration first, like the web's close()); while a shortcut
    /// waits for its key, the press is the new key and Esc cancels.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        // the panel floats over a usable window (#184): with the focus outside it, the keys are the window's
        if (capturing == null && capturingWheel == null && !IsKeyboardFocusWithin) return false;
        if (capturingWheel != null)
        {
            if (e.Key == Key.Escape) StopCapture();
            e.Handled = true;
            return true;
        }
        if (capturing is { } id)
        {
            if (e.Key == Key.Escape) StopCapture();
            else if (Shortcuts.Assignable(e.Key))
            {
                var g = new KeyGesture(e.Key, e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt));
                capturing = null;
                var taken = Shortcuts.Assign(id, g);
                shortcutNote = taken != null ? Loc.T("set.shortcut.taken", Shortcuts.Label(g), taken, Shortcuts.All.First(a => a.Id == id).Name) : null;
                BuildShortcuts();
            }
            e.Handled = true;
            return true;
        }
        // the other keys reach the focused control (the search, IP and alias boxes need Backspace, arrows and
        // Enter), but never the window's shortcuts
        if (e.Key == Key.Escape)
        {
            HideDialog();
            e.Handled = true;
        }
        return true;
    }

    void ShowTab(string id)
    {
        AudioTab.Classes.Set("active", id == "audio");
        CameraTab.Classes.Set("active", id == "camera");
        SyncTab.Classes.Set("active", id == "sync");
        ShortcutsTab.Classes.Set("active", id == "shortcuts");
        LanguageTab.Classes.Set("active", id == "language");
        SaveTab.Classes.Set("active", id == "save");
        ViewTab.Classes.Set("active", id == "view");
        PluginsTab.Classes.Set("active", id == "plugins");
        ComponentsTab.Classes.Set("active", id == "components");
        MidiTab.Classes.Set("active", id == "midi");
        StopCapture();
        StopMidi();
        if (id == "save") { RefreshSave(); RefreshEncoder(); RefreshImport(); }
        if (id == "shortcuts") { shortcutNote = null; BuildShortcuts(); }
        if (id == "plugins") RefreshPlugins();
        Body.Content = id switch { "components" => componentsPage, "midi" => midiPage, "plugins" => pluginsPage, "camera" => cameraPage, "sync" => syncPage, "shortcuts" => shortcutsPage, "language" => languagePage, "save" => savePage, "view" => viewPage, _ => audioPage };
        if (id == "midi") RefreshMidi();
        Refresh();
    }

    async Task Reopen(string? driver, int outputOffset)
    {
        if (engine == null || reopening || busy() || engine.Calibrating) { Refresh(); return; }
        var e = engine;
        reopening = true;
        Refresh();
        // ASIO drivers are COM objects: they can only be created on an STA thread (the UI one), not the pool's
        await Task.Delay(50);
        e.Open(driver, 0, outputOffset);
        reopening = false;
        if (e.IsOpen)
        {
            string name = e.DriverName!, offset = e.OutputOffset.ToString(CultureInfo.InvariantCulture);
            _ = Task.Run(() =>
            {
                try { AppConfig.Set("audio.driver", name); AppConfig.Set("audio.outputOffset", offset); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
            });
        }
        AudioChanged?.Invoke();
        Refresh();
    }

    async Task Calibrate(CalibrationMethod method)
    {
        if (engine is not { IsOpen: true } e || busy() || running != null) return;
        e.StopTransport();
        var cts = calibration = new CancellationTokenSource();
        running = method;
        progress = null;
        calError = null;
        Refresh();
        try
        {
            var res = await e.MeasureLatencyAsync(method, p => Dispatcher.UIThread.Post(() =>
            {
                if (running != method) return;
                progress = p;
                Refresh();
            }), cts.Token);
            if (res is { } r)
            {
                var info = e.GetLatencyInfo();
                var stored = new StoredCalibration
                {
                    LatencyMs = Math.Round(r.LatencyMs, 1),
                    SpreadMs = Math.Round(r.SpreadMs, 1),
                    Method = LatencyCalibration.MethodToString(method),
                    EstimateMs = info.TotalMs,
                    SampleRate = info.SampleRate,
                    At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                LatencyCalibration.WriteCalibration(e.DriverName!, e.CalibrationOutputId, stored);
                e.SetCalibratedLatency(stored.LatencyMs);
            }
            else if (!cts.IsCancellationRequested)
                calError = method == CalibrationMethod.Tap
                    ? Loc.T("set.cal.tap.error")
                    : Loc.T("set.cal.loop.error");
        }
        catch (InvalidOperationException x)
        {
            calError = x.Message;
        }
        finally
        {
            running = null;
            calibration = null;
            AudioChanged?.Invoke();
            Refresh();
        }
    }

    void Forget()
    {
        if (engine?.DriverName is not { } d) return;
        engine.SetCalibratedLatency(null);
        LatencyCalibration.WriteCalibration(d, engine.CalibrationOutputId, null);
        calError = null;
        AudioChanged?.Invoke();
        Refresh();
    }

    /// <summary>Fills every control from the engine (the flag keeps that from looking like an edit).</summary>
    void Refresh()
    {
        if (!IsVisible || engine == null) return;
        var e = engine;
        bool locked = busy() || e.Calibrating || reopening;
        filling = true;
        try
        {
            var drivers = AsioDuplex.DriverNames();
            driverBox.ItemsSource = drivers.Length > 0 ? drivers : [Loc.T("set.noDevices")];
            driverBox.SelectedItem = drivers.Length > 0 ? e.DriverName : Loc.T("set.noDevices");
            driverBox.IsEnabled = drivers.Length > 0 && !locked;
            bufferText.Text = reopening ? Loc.T("set.reopening") : e.BufferFrames > 0
                ? Loc.T("set.buffer.value", e.BufferFrames, $"{1000.0 * e.BufferFrames / PlaybackEngine.SampleRate:0.0}") : Loc.T("set.noAudio");
            panelButton.IsEnabled = drivers.Length > 0 && !locked;
            foreach (var b in modeButtons) b.IsChecked = (InputChannelMode)b.Tag! == e.InputChannelMode;
            gain.Set(e.InputGainDb);

            int pairs = e.OutputChannels / 2;
            outputBox.ItemsSource = Enumerable.Range(0, pairs).Select(i => Loc.T("set.outputPair", 2 * i + 1, 2 * i + 2)).ToArray();
            outputBox.SelectedIndex = pairs > 0 ? e.OutputOffset / 2 : -1;
            outputBox.IsEnabled = pairs > 0 && !locked;
            outputHelp.Text = locked && !reopening ? Loc.T("set.locked")
                : Loc.T("set.output.help");
            monitorSwitch.IsChecked = e.SoftwareMonitor;
            transportFade.Set(Math.Round(e.TransportFadeMs));
            fine.Set(OverdubOffsetMs);
            if (cam != null) FillCamera(cam);
        }
        finally
        {
            filling = false;
        }
        RefreshSync(e);
    }

    void FillCamera(LiveCamera c)
    {
        foreach (var b in camKindButtons) b.IsChecked = (bool)b.Tag! == c.UseIp;
        cameraRes.Text = c.Resolution;
        driverFields.IsVisible = !c.UseIp;
        ipField.IsVisible = c.UseIp;
        camBox.ItemsSource = c.Devices.Length > 0 ? c.Devices : [Loc.T("set.noCameras")];
        camBox.SelectedItem = c.Devices.Length > 0 ? c.Device : Loc.T("set.noCameras");
        resolutionBox.SelectedIndex = Array.IndexOf(ResModes, c.Mode);
        if (ipBox.Text != c.IpInput) ipBox.Text = c.IpInput;
        mirrorButton.IsChecked = c.Mirror;
        cameraLatency.Set(c.LatencyMs);
    }

    void RefreshSync(PlaybackEngine e)
    {
        string pair = Loc.T("set.pair", e.InputOffset + 1, e.InputOffset + 2, e.OutputOffset + 1, e.OutputOffset + 2);
        pairText.Text = e.DriverName is { } d ? $"{d} · {pair}" : Loc.T("set.noInput");
        ToolTip.SetTip(pairText, pairText.Text);
        var stored = e.DriverName is { } dn ? LatencyCalibration.ReadCalibration(dn, e.CalibrationOutputId) : null;

        readout.Children.Clear();
        readout.IsVisible = e.IsOpen;
        if (e.IsOpen)
        {
            var info = e.GetLatencyInfo();
            bool calibrated = info.CalibratedMs != null;
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            top.Children.Add(new TextBlock { Text = Loc.T(calibrated ? "set.lat.calibrated" : "set.lat.estimated"), FontSize = 12 });
            var ms = new TextBlock { Text = $"{Math.Round(info.AppliedMs):0} ms", FontSize = 12, Classes = { "mono" } };
            Grid.SetColumn(ms, 1);
            top.Children.Add(ms);
            readout.Children.Add(top);
            string how = calibrated
                ? Loc.T("set.lat.driver", Loc.T(stored?.Method == "tap" ? "set.lat.tap" : "set.lat.loop"), $"{info.TotalMs:0.#}")
                : Loc.T("set.lat.parts", $"{info.OutputMs:0.#}", $"{info.InputMs:0.#}");
            readout.Children.Add(new TextBlock { Text = how + Loc.T("set.lat.auto"), FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap });
            if (info.OutputMs > 60)
                readout.Children.Add(Warning(Loc.T("set.lat.high")));
            if (stored != null && LatencyCalibration.IsCalibrationStale(stored, info.TotalMs, info.SampleRate))
                readout.Children.Add(Warning(Loc.T("set.lat.stale")));
        }

        calBox.Children.Clear();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = Loc.T("set.cal.title"), FontSize = 12, FontWeight = FontWeight.SemiBold });
        if (stored != null && running == null)
        {
            var useEstimate = new Button { Content = Loc.T("set.cal.useEstimate"), Classes = { "link" } };
            ToolTip.SetTip(useEstimate, Loc.T("set.cal.useEstimate.tip"));
            useEstimate.Click += (_, _) => Forget();
            Grid.SetColumn(useEstimate, 1);
            head.Children.Add(useEstimate);
        }
        calBox.Children.Add(head);
        string cableHow = Loc.T("set.cal.cableHow");
        string tapHow = Loc.T("set.cal.tapHow");
        if (running is { } m)
        {
            calBox.Children.Add(Help(m == CalibrationMethod.Tap ? tapHow : cableHow));
            string status = progress is not { } p ? Loc.T("set.cal.preparing")
                : p.CountIn ? Loc.T("set.cal.listen")
                : m == CalibrationMethod.Tap ? Loc.T("set.cal.playStep", p.Step, p.Total) : Loc.T("set.cal.pulseStep", p.Step, p.Total);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new Border
            {
                Background = Palette.Raised, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 6),
                Child = new TextBlock { Text = status, FontSize = 14, Classes = { "mono" }, Foreground = progress is { CountIn: true } ? Palette.Accent : Palette.Fg },
            });
            var cancel = new Button { Content = Loc.T("common.cancel"), Classes = { "chip" } };
            cancel.Click += (_, _) => calibration?.Cancel();
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);
            calBox.Children.Add(row);
        }
        else
        {
            bool can = e.IsOpen && !busy();
            var buttons = new UniformGrid { Columns = 2 };
            foreach (var (method, text, tip) in new[] { (CalibrationMethod.Loopback, Loc.T("set.cal.cable"), cableHow), (CalibrationMethod.Tap, Loc.T("set.cal.tap"), tapHow) })
            {
                var b = new Button { Content = text, Classes = { "chip" }, Height = 32, FontSize = 11, IsEnabled = can, Margin = new Thickness(method == CalibrationMethod.Tap ? 4 : 0, 0, method == CalibrationMethod.Loopback ? 4 : 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
                ToolTip.SetTip(b, tip);
                b.Click += async (_, _) => await Calibrate(method);
                buttons.Children.Add(b);
            }
            calBox.Children.Add(buttons);
            calBox.Children.Add(Help(Loc.T(can ? "set.cal.help.can" : "set.cal.help.cant")));
            if (stored != null)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(stored.At).LocalDateTime;
                calBox.Children.Add(new TextBlock { Text = Loc.T("set.cal.last", $"{stored.LatencyMs:0.#}", $"{stored.SpreadMs:0.#}", $"{at:d/M/yyyy, HH:mm:ss}"), FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" } });
                bool tap = stored.Method == "tap";
                if (stored.SpreadMs > (tap ? 20 : 2))
                    calBox.Children.Add(Warning(Loc.T("set.cal.varied", tap ? Loc.T("set.cal.varied.cable") : "")));
            }
        }
        if (calError != null) calBox.Children.Add(Warning(calError));
    }

    void RefreshSync() { if (engine != null) RefreshSync(engine); }

    void StopCapture()
    {
        if (capturing == null && capturingWheel == null) return;
        capturing = capturingWheel = null;
        BuildShortcuts();
    }

    /// <summary>While a wheel gesture waits, a turn with Ctrl, Shift or Alt held is its new modifiers (a plain turn
    /// is the scroll, so it doesn't count and the page scrolls as usual).</summary>
    void OnCaptureWheel(object? sender, PointerWheelEventArgs e)
    {
        if (capturingWheel is not { } id) return;
        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);
        if (mods == KeyModifiers.None) return;
        e.Handled = true;
        capturingWheel = null;
        var taken = Shortcuts.AssignWheel(id, mods);
        shortcutNote = taken != null ? Loc.T("set.shortcut.taken", Shortcuts.WheelLabel(id), taken, Shortcuts.Wheel.First(a => a.Id == id).Name) : null;
        BuildShortcuts();
    }

    /// <summary>"Idioma" (native only): the choice is stored right away and applies on the next start (see <see cref="Loc"/>).</summary>
    void BuildLanguage()
    {
        var box = Combo();
        box.ItemsSource = Loc.Languages.Select(l => l.Name).ToList();
        box.SelectedIndex = Array.FindIndex(Loc.Languages, l => l.Code == Loc.Language);
        var restart = Warning(Loc.T("settings.lang.restart"));
        restart.IsVisible = false;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            string code = Loc.Languages[box.SelectedIndex].Code;
            Loc.Choose(code);
            restart.IsVisible = code != Loc.Language;
        };
        var s = Section(Loc.T("settings.lang.section"));
        s.Children.Add(Field(Loc.T("settings.lang.field"), box));
        s.Children.Add(Help(Loc.T("settings.lang.help")));
        s.Children.Add(restart);
        languagePage.Children.Add(Card(s));
    }

    /// <summary>"Guardado" (native only): when edits reach the project, see <see cref="SaveMode"/>; applied at once.</summary>
    InspectorSlider BuildSave()
    {
        var modes = new UniformGrid { Columns = 3 };
        foreach (var (mode, text, tip) in new[]
        {
            (SaveMode.OnEdit, Loc.T("set.save.onEdit"), Loc.T("set.save.onEdit.tip")),
            (SaveMode.Interval, Loc.T("set.save.interval"), Loc.T("set.save.interval.tip")),
            (SaveMode.Manual, Loc.T("set.save.manual"), Loc.T("set.save.manual.tip")),
        })
        {
            var b = new ToggleButton { Content = text, Tag = mode, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { SaveMode.Set(mode, SaveMode.Minutes); RefreshSave(); };
            saveModeButtons.Add(b);
            modes.Children.Add(b);
        }
        var minutes = new InspectorSlider(Loc.T("set.save.minutes"), SaveMode.MinMinutes, SaveMode.MaxMinutes, 1, SaveMode.DefaultMinutes,
            v => Loc.T("set.save.minutes.value", $"{v:0}"),
            v => { SaveMode.Set(SaveMode.Mode, (int)Math.Round(v)); RefreshSave(); });
        var s = Section(Loc.T("set.save.section"));
        s.Children.Add(Field(Loc.T("set.save.when"), new Border { Classes = { "seg" }, Child = modes }));
        s.Children.Add(minutes);
        s.Children.Add(saveHelp);
        s.Children.Add(Help(Loc.T("set.save.always")));
        savePage.Children.Add(Card(s));
        BuildStartup();
        BuildImport();
        BuildEncoder();
        return minutes;
    }

    readonly List<ToggleButton> startupButtons = [];

    /// <summary>"Al arrancar": the projects manager or straight into an empty project (<see cref="StartupMode"/>);
    /// the same choice as "Mostrar al arrancar" in the manager.</summary>
    void BuildStartup()
    {
        var modes = new UniformGrid { Columns = 2 };
        foreach (var (mode, text, tip) in new[]
        {
            (StartupMode.Projects, Loc.T("set.startup.projects"), Loc.T("set.startup.projects.tip")),
            (StartupMode.Empty, Loc.T("set.startup.empty"), Loc.T("set.startup.empty.tip")),
        })
        {
            var b = new ToggleButton { Content = text, Tag = mode, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            // a click on the chosen one would uncheck it: always show the setting again
            b.Click += (_, _) => { StartupMode.Set(mode); Refresh(); };
            startupButtons.Add(b);
            modes.Children.Add(b);
        }
        void Refresh()
        {
            foreach (var b in startupButtons) b.IsChecked = (string)b.Tag! == StartupMode.Mode;
        }
        StartupMode.Changed += Refresh;
        Refresh();
        var s = Section(Loc.T("set.startup.section"));
        s.Children.Add(Field(Loc.T("set.startup.when"), new Border { Classes = { "seg" }, Child = modes }));
        s.Children.Add(Help(Loc.T("set.startup.help")));
        savePage.Children.Add(Card(s));
    }

    /// <summary>"Importación" (native only): what an import does with the file, see <see cref="ImportMode"/>; applied at once.</summary>
    void BuildImport()
    {
        var modes = new UniformGrid { Columns = 3 };
        foreach (var (mode, text, tip) in new[]
        {
            (ImportMode.Ask, Loc.T("set.import.ask"), Loc.T("set.import.ask.tip")),
            (ImportMode.Copy, Loc.T("set.import.copy"), Loc.T("set.import.copy.tip")),
            (ImportMode.Link, Loc.T("set.import.link"), Loc.T("set.import.link.tip")),
        })
        {
            var b = new ToggleButton { Content = text, Tag = mode, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { ImportMode.Set(mode); RefreshImport(); };
            importButtons.Add(b);
            modes.Children.Add(b);
        }
        var s = Section(Loc.T("set.import.section"));
        s.Children.Add(Field(Loc.T("set.import.when"), new Border { Classes = { "seg" }, Child = modes }));
        s.Children.Add(importHelp);
        savePage.Children.Add(Card(s));
    }

    void RefreshImport()
    {
        foreach (var b in importButtons) b.IsChecked = (string)b.Tag! == ImportMode.Mode;
        importHelp.Text = Loc.T($"set.import.{ImportMode.Mode}.help");
    }

    /// <summary>"Exportación de video" (native only): whether the export encodes on the video card (when FFmpeg finds
    /// one) or on the processor; machine-wide (<see cref="VideoEncoders.ConfigKey"/>), applied on the next export.</summary>
    void BuildEncoder()
    {
        var modes = new UniformGrid { Columns = 2 };
        foreach (var (id, text, tip) in new[]
        {
            (VideoEncoders.Auto, Loc.T("set.encoder.gpu"), Loc.T("set.encoder.gpu.tip")),
            (VideoEncoders.CpuId, Loc.T("set.encoder.cpu"), Loc.T("set.encoder.cpu.tip")),
        })
        {
            var b = new ToggleButton { Content = text, Tag = id, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                try { AppConfig.Set(VideoEncoders.ConfigKey, id); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                RefreshEncoder();
            };
            encoderButtons.Add(b);
            modes.Children.Add(b);
        }
        var s = Section(Loc.T("set.encoder.section"));
        s.Children.Add(Field(Loc.T("set.encoder.who"), new Border { Classes = { "seg" }, Child = modes }));
        s.Children.Add(encoderHelp);
        savePage.Children.Add(Card(s));
        _ = DetectEncoder();
    }

    async Task DetectEncoder()
    {
        encoderCaps = await VideoEncoders.DetectAsync();
        RefreshEncoder();
    }

    void RefreshEncoder()
    {
        string pref = VideoEncoders.ParsePreference(AppConfig.Get(VideoEncoders.ConfigKey)) == VideoEncoders.CpuId ? VideoEncoders.CpuId : VideoEncoders.Auto;
        foreach (var b in encoderButtons) b.IsChecked = (string)b.Tag! == pref;
        encoderHelp.Text = encoderCaps is not { } caps ? Loc.T("set.encoder.looking")
            : pref == VideoEncoders.CpuId ? Loc.T("set.encoder.cpu.help")
            : caps.Gpu is { } g ? Loc.T("set.encoder.found", VideoEncoders.Brand(g))
            : Loc.T("set.encoder.none");
    }

    /// <summary>"Apariencia" (native only): the knobs' look, each shown as a sample (<see cref="KnobLooks"/>), and which
    /// effect knobs the timeline's track headers show (<see cref="TrackKnobs"/>); all applied at once.</summary>
    void BuildView()
    {
        var looks = Section(Loc.T("set.look.knobs"));
        looks.Children.Add(Help(Loc.T("set.look.knobs.help")));
        var grid = new UniformGrid { Columns = 5 };
        var lookButtons = new List<ToggleButton>();
        foreach (string id in KnobLooks.All)
        {
            var sample = new Knob(40) { Look = id, Min = 0, Max = 1, Value = 0.62, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 12, 6, 8), IsChecked = id == KnobLooks.Current,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel { Spacing = 8, Children = { sample, new TextBlock { Text = Loc.T("set.look.knob." + id), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            AutomationProperties.SetName(b, Loc.T("set.look.knob." + id));
            b.Click += (_, _) => KnobLooks.Set(id);
            lookButtons.Add(b);
            grid.Children.Add(b);
        }
        void SyncKnobs() { foreach (var o in lookButtons) o.IsChecked = (string)o.Tag! == KnobLooks.Current; }
        looks.Children.Add(grid);
        viewPage.Children.Add(Card(looks));
        Resettable(looks, () => KnobLooks.Current == KnobLooks.Default, () => KnobLooks.Set(KnobLooks.Default),
            h => KnobLooks.Changed += h, h => KnobLooks.Changed -= h, SyncKnobs);

        // the rest of the looks build their own pickers (samples drawn by the same code, applied live)
        viewPage.Children.Add(LookCard("panels", PanelLooks.Picker(), () => PanelLooks.Current == PanelLooks.Default,
            () => PanelLooks.Set(PanelLooks.Default), h => PanelLooks.Changed += h, h => PanelLooks.Changed -= h));
        viewPage.Children.Add(LookCard("faders", FaderLooks.Picker(), () => FaderLooks.Current == FaderLooks.Default,
            () => FaderLooks.Set(FaderLooks.Default), h => FaderLooks.Changed += h, h => FaderLooks.Changed -= h));
        viewPage.Children.Add(LookCard("meters", MeterLooks.Picker(), () => MeterLooks.Current == MeterLooks.Default,
            () => MeterLooks.Set(MeterLooks.Default), h => MeterLooks.Changed += h, h => MeterLooks.Changed -= h));
        viewPage.Children.Add(LookCard("meterColors", MeterColors.Editor(), () => !MeterColors.Custom,
            () => MeterColors.SetCustom(false), h => MeterColors.Changed += h, h => MeterColors.Changed -= h));
        viewPage.Children.Add(LookCard("waves", WaveLooks.Picker(), () => WaveLooks.Current == WaveLooks.Default,
            () => WaveLooks.Set(WaveLooks.Default), h => WaveLooks.Changed += h, h => WaveLooks.Changed -= h));
        viewPage.Children.Add(LookCard("trackColor", TrackColorLooks.Picker(), () => TrackColorLooks.IsDefault,
            TrackColorLooks.Reset, h => TrackColorLooks.Changed += h, h => TrackColorLooks.Changed -= h));
        viewPage.Children.Add(LookCard("trackColorInspector", TrackColorLooks.InspectorPicker(), () => TrackColorLooks.InspectorIsDefault,
            TrackColorLooks.ResetInspector, h => TrackColorLooks.Changed += h, h => TrackColorLooks.Changed -= h));
        viewPage.Children.Add(LookCard("font", UiFonts.FontPicker(), () => UiFonts.FontIsDefault,
            () => UiFonts.SetFont(UiFonts.Default), h => UiFonts.Changed += h, h => UiFonts.Changed -= h));
        viewPage.Children.Add(LookCard("numbers", UiFonts.NumberPicker(), () => UiFonts.NumbersAreDefault,
            () => UiFonts.SetNumberFont(UiFonts.DefaultNumbers), h => UiFonts.Changed += h, h => UiFonts.Changed -= h));
        viewPage.Children.Add(LookCard("scale", UiFonts.ScalePicker(), () => UiFonts.ScaleIsDefault,
            () => UiFonts.SetScale(1), h => UiFonts.Changed += h, h => UiFonts.Changed -= h));

        var s = Section(Loc.T("set.view.knobs"));
        s.Children.Add(Help(Loc.T("set.view.knobs.help")));
        var list = new StackPanel { Spacing = 6 };
        var boxes = new List<(string Key, CheckBox Box)>();
        foreach (string key in TrackKnobs.All)
        {
            var icon = FxIcon.Create(key == TimelineView.SendsKey ? FxIcon.Reverb : key, 13);
            FxIcon.SetColor(icon, Palette.Accent);
            icon.VerticalAlignment = VerticalAlignment.Center;
            var name = new TextBlock { Text = KnobName(key), VerticalAlignment = VerticalAlignment.Center };
            var box = new CheckBox { IsChecked = TrackKnobs.Shows(key), FontSize = 12,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, name } } };
            box.IsCheckedChanged += (_, _) => TrackKnobs.Set(key, box.IsChecked == true);
            boxes.Add((key, box));
            list.Children.Add(box);
        }
        s.Children.Add(list);
        viewPage.Children.Add(Card(s));
        Resettable(s, () => TrackKnobs.All.All(TrackKnobs.Shows), () => { foreach (string k in TrackKnobs.All) TrackKnobs.Set(k, true); },
            h => TrackKnobs.Changed += h, h => TrackKnobs.Changed -= h, () => { foreach (var (k, b) in boxes) b.IsChecked = TrackKnobs.Shows(k); });

        // the mixer's A | B, hidden by default (#211)
        var mx = Section(Loc.T("set.view.mixer"));
        mx.Children.Add(Help(Loc.T("set.view.mixer.help")));
        var ab = new CheckBox { IsChecked = MixerOptions.ShowAb, FontSize = 12, Content = Loc.T("set.view.mixer.ab") };
        ab.IsCheckedChanged += (_, _) => MixerOptions.SetShowAb(ab.IsChecked == true);
        mx.Children.Add(ab);
        viewPage.Children.Add(Card(mx));
        Resettable(mx, () => !MixerOptions.ShowAb, () => MixerOptions.SetShowAb(false),
            h => MixerOptions.Changed += h, h => MixerOptions.Changed -= h, () => ab.IsChecked = MixerOptions.ShowAb);
    }

    /// <summary>A card of Apariencia: its title and help ("set.look.&lt;key&gt;", ".help") over the picker, with its
    /// "Restablecer".</summary>
    Control LookCard(string key, Control picker, Func<bool> isDefault, Action reset, Action<Action> hook, Action<Action> unhook)
    {
        var s = Section(Loc.T("set.look." + key));
        s.Children.Add(Help(Loc.T("set.look." + key + ".help")));
        s.Children.Add(picker);
        var card = Card(s);
        Resettable(s, isDefault, reset, hook, unhook);
        return card;
    }

    /// <summary>Puts "Restablecer" (back to how the app came, asked by the user) in a section's head: enabled only while
    /// the setting is not the factory one, kept up to date while the section is shown; <paramref name="sync"/> also runs
    /// on every change, for a picker that doesn't follow the setting by itself.</summary>
    static void Resettable(StackPanel section, Func<bool> isDefault, Action reset, Action<Action> hook, Action<Action> unhook, Action? sync = null)
    {
        var b = new Button { Content = Loc.T("set.look.reset"), Classes = { "link" } };
        ToolTip.SetTip(b, Loc.T("set.look.reset.tip"));
        b.Click += (_, _) => reset();
        void Refresh()
        {
            b.IsEnabled = !isDefault();
            sync?.Invoke();
        }
        Refresh();
        b.AttachedToVisualTree += (_, _) => { hook(Refresh); Refresh(); };
        b.DetachedFromVisualTree += (_, _) => unhook(Refresh);
        if (section.Children[0] is Grid head)
        {
            Grid.SetColumn(b, 1);
            head.Children.Add(b);
        }
    }

    static string KnobName(string key) => key switch
    {
        FxIcon.Sat => Loc.T("fx.life"),
        FxIcon.Comp => Loc.T("insp.section.comp"),
        TimelineView.SendsKey => Loc.T("set.view.knobs.sends"),
        _ => Loc.T("insp.section.transpose"),
    };

    void RefreshSave()
    {
        foreach (var b in saveModeButtons) b.IsChecked = (string)b.Tag! == SaveMode.Mode;
        saveMinutes.IsVisible = SaveMode.Mode == SaveMode.Interval;
        saveMinutes.Set(SaveMode.Minutes);
        saveHelp.Text = SaveMode.Mode switch
        {
            SaveMode.Interval => Loc.T("set.save.interval.help", SaveMode.Minutes),
            SaveMode.Manual => Shortcuts.Label("save") is { Length: > 0 } key ? Loc.T("set.save.manual.help", key) : Loc.T("set.save.manual.help.nokey"),
            _ => Loc.T("set.save.onEdit.help"),
        };
    }

    /// <summary>"Atajos de teclado": one card per group, a row per action with its key; rebuilt on every change.</summary>
    void BuildShortcuts()
    {
        shortcutsPage.Children.Clear();
        var resetAll = new Button { Content = Loc.T("set.sc.resetAll"), Classes = { "link" }, IsEnabled = !Shortcuts.AllDefault };
        ToolTip.SetTip(resetAll, Loc.T("set.sc.resetAll.tip"));
        resetAll.Click += (_, _) => { shortcutNote = null; capturing = capturingWheel = null; Shortcuts.ResetAll(); };
        var intro = Section(Loc.T("settings.tab.shortcuts"), resetAll);
        var layouts = new UniformGrid { Columns = 1 + KeyLayouts.Daws.Length };
        foreach (var (id, text) in KeyLayouts.Daws.Select(l => (l.Id, l.Name)).Prepend((KeyLayouts.Own, Loc.T("set.sc.layout.own"))))
        {
            var b = new ToggleButton { Content = text, Classes = { "seg" }, IsChecked = Shortcuts.Layout == id, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, id == KeyLayouts.Own ? Loc.T("set.sc.layout.own.tip") : Loc.T("set.sc.layout.tip", text));
            b.Click += (_, _) =>
            {
                shortcutNote = null;
                capturing = capturingWheel = null;
                Shortcuts.SetLayout(id);
                BuildShortcuts();
            };
            layouts.Children.Add(b);
        }
        intro.Children.Add(Field(Loc.T("set.sc.layout"), new Border { Classes = { "seg" }, Child = layouts }));
        intro.Children.Add(Help(Loc.T("set.sc.help")));
        if (shortcutNote != null) intro.Children.Add(Warning(shortcutNote));
        shortcutsPage.Children.Add(Card(intro));
        foreach (var group in Shortcuts.All.GroupBy(a => a.Group))
        {
            var s = FoldSection(group.Key, group.Key, group.Count());
            if (openGroups.Contains(group.Key))
                foreach (var a in group)
                    s.Children.Add(ShortcutRow(a.Id, a.Name, Shortcuts.Label(a.Id), capturing == a.Id, "set.sc.press",
                        () => { capturing = capturing == a.Id ? null : a.Id; capturingWheel = null; }, () => Shortcuts.Reset(a.Id)));
            shortcutsPage.Children.Add(Card(s));
        }
        var wheel = FoldSection(WheelGroup, Loc.T("sc.group.wheel"), Shortcuts.Wheel.Count() + 1);
        shortcutsPage.Children.Add(Card(wheel));
        if (!openGroups.Contains(WheelGroup)) return;
        wheel.Children.Add(Help(Loc.T("set.sc.wheel.help")));
        var scrollName = new TextBlock { Text = Loc.T("sc.wheelScroll"), FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        wheel.Children.Add(Row(scrollName, ReverseToggle(Shortcuts.WheelScroll)));
        foreach (var a in Shortcuts.Wheel)
            wheel.Children.Add(ShortcutRow(a.Id, a.Name, Shortcuts.WheelLabel(a.Id), capturingWheel == a.Id, "set.sc.wheel.press",
                () => { capturingWheel = capturingWheel == a.Id ? null : a.Id; capturing = null; }, () => Shortcuts.ResetWheel(a.Id), ReverseToggle(a.Id)));
    }

    /// <summary>Id of the wheel card in <see cref="openGroups"/> (the key groups use their own name).</summary>
    const string WheelGroup = "\0wheel";

    /// <summary>Shortcut groups unfolded in this dialog: all start folded (pedido del usuario, #190), so the page
    /// reads as a list of categories, each with how many actions it holds.</summary>
    readonly HashSet<string> openGroups = [];

    /// <summary>A shortcut card whose title folds it: chevron, title and count; a click flips it and rebuilds.</summary>
    StackPanel FoldSection(string id, string title, int count)
    {
        var open = openGroups.Contains(id);
        var s = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Classes = { "icon" }, Text = open ? "" : "", FontSize = 10, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = count.ToString(), Classes = { "dim", "mono" }, FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "link" }, Content = head, MinHeight = 20, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) =>
        {
            if (!openGroups.Remove(id)) openGroups.Add(id);
            BuildShortcuts();
        };
        s.Children.Add(b);
        return s;
    }

    /// <summary>"Invertir": turns one wheel gesture around, apart from the others.</summary>
    ToggleButton ReverseToggle(string id)
    {
        var b = new ToggleButton { Content = Loc.T("set.sc.wheel.reverse"), Classes = { "chip" }, Height = 30, Padding = new Thickness(10, 0), IsChecked = Shortcuts.WheelReversed(id) };
        ToolTip.SetTip(b, Loc.T("set.sc.wheel.reverse.tip"));
        b.Click += (_, _) => { Shortcuts.SetWheelReversed(id, b.IsChecked == true); BuildShortcuts(); };
        return b;
    }

    Control ShortcutRow(string id, string text, string label, bool waiting, string pressKey, System.Action toggle, System.Action resetTo, Control? extra = null)
    {
        var key = new Button
        {
            Classes = { "chip" }, FontFamily = Palette.Code, MinWidth = 120, Height = 30, Padding = new Thickness(10, 0), HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = waiting ? Loc.T(pressKey) : label.Length > 0 ? label : Loc.T("set.sc.none"),
        };
        key.Classes.Set("on", waiting);
        if (!waiting && label.Length == 0) key.Foreground = Palette.Faint;
        ToolTip.SetTip(key, Loc.T(waiting ? pressKey + ".tip" : "set.sc.change.tip"));
        key.Click += (_, _) =>
        {
            toggle();
            shortcutNote = null;
            BuildShortcuts();
            Focus();
        };
        var reset = Act("", Loc.T("set.sc.reset.tip"), () => { shortcutNote = null; resetTo(); });
        reset.IsVisible = !Shortcuts.IsDefault(id);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        right.Children.Add(reset);
        if (extra != null) right.Children.Add(extra);
        right.Children.Add(key);
        var name = new TextBlock { Text = text, FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        return Row(name, right);
    }

    // --- building blocks (SettingsDialog.tsx Section / Field) ------------------------------------

    readonly Control componentsPage = new ScrollViewer { Content = new ComponentList { Margin = new Thickness(16) } };

    static StackPanel Section(string title, Control? action = null)
    {
        var s = new StackPanel { Spacing = 14 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 20 };
        head.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        if (action != null)
        {
            Grid.SetColumn(action, 1);
            head.Children.Add(action);
        }
        s.Children.Add(head);
        return s;
    }

    static Border Card(Control c) => new() { Padding = new Thickness(16), BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = c };

    static Control Field(string label, Control control)
    {
        var f = new StackPanel { Spacing = 6 };
        f.Children.Add(new TextBlock { Text = label, Classes = { "dim" } });
        f.Children.Add(control);
        return f;
    }

    static Grid Row(Control left, Control right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    static ComboBox Combo() => new() { Classes = { "field" } };

    static TextBlock Help(string text) => new() { Text = text, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };

    static TextBlock Warning(string text) => new() { Text = text, FontSize = 11, Foreground = Palette.Accent, TextWrapping = TextWrapping.Wrap };

    static Button Act(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "act" }, Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 13 } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }
}
