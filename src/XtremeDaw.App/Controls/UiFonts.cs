using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The interface's font and size (asked by the user: Configuración → Apariencia), both applied live and kept in
/// config.json ("uiFont", "uiScale"). The font is the <c>Ui</c> resource (App.axaml, read as a DynamicResource), Fluent's
/// <c>ContentControlThemeFontFamily</c> (menus, tooltips) and <see cref="Palette.Ui"/> for what is drawn by hand. The
/// numbers' font is picked apart ("uiNumberFont": the <c>Mono</c> resource and <see cref="Palette.Mono"/>), with digital
/// ones bundled because Windows has none (asked by the user); icons stay in Segoe Fluent Icons. The size is one scale for the whole interface instead of every
/// FontSize: each window and popup gets a <see cref="LayoutTransformControl"/> round its template's layer manager when
/// its template is applied, so layout, hit testing and popups placement all follow it, and the window's content is never
/// touched (the floating panels move theirs in and out). Windows without decorations (the splash, the keys panel inside
/// a plugin's window) are sized from outside and keep 100 %.
/// </summary>
public static class UiFonts
{
    /// <summary>The font the app was designed with; also what an unknown or missing font falls back to.</summary>
    public const string Default = "Segoe UI Variable Text";
    /// <summary>The system's interface font (Avalonia's default family).</summary>
    public const string System = "system";
    public const string DefaultFamily = Default + ", Segoe UI";

    /// <summary>The fonts offered after the default and the system's, in the picker's order; only those installed show.</summary>
    static readonly string[] Candidates =
    [
        "Segoe UI", "Aptos", "Bahnschrift", "Calibri", "Inter", "Roboto", "Open Sans", "Noto Sans", "Source Sans 3",
        "IBM Plex Sans", "Arial", "Verdana", "Tahoma", "Trebuchet MS",
    ];

    /// <summary>The size is picked as the px of the interface's body text (12 at 100 %), in half-pixel steps (asked by
    /// the user, #133: the four fixed sizes jumped too much); the scale is that over <see cref="BasePx"/>.</summary>
    public const double BasePx = 12, MinPx = 9, MaxPx = 18, PxStep = 0.5;
    /// <summary>What Ctrl + and Ctrl − move.</summary>
    public const double KeyStep = 1;

    /// <summary>The numbers' font the app was designed with (values, times, the clock).</summary>
    public const string MonoFamily = "Cascadia Mono, Consolas";
    public const string DefaultNumbers = "mono";
    /// <summary>Where the bundled fonts live (DSEG's Mini cut, to fit the rows' height, and Orbitron; both SIL OFL 1.1).</summary>
    static readonly string FontsUri = Loc.Assets + "Fonts#";

    /// <summary>The numbers' fonts, in the picker's order: id → family. The digital ones are bundled (Windows has none);
    /// a glyph they lack (letters, ∞) falls back to the system's. Only the 14-segment DSEG stays: the 7-segment cuts
    /// read badly in the app (asked by the user, #132).</summary>
    static readonly (string Id, string Family, string? Name)[] NumberFonts =
    [
        (DefaultNumbers, MonoFamily, "Cascadia Mono"),
        ("consolas", "Consolas", "Consolas"),
        ("lucida", "Lucida Console", "Lucida Console"),
        ("dseg14", FontsUri + "DSEG14 Classic Mini", null),
        ("orbitron", FontsUri + "Orbitron", "Orbitron"),
    ];

    const string FontKey = "uiFont", ScaleKey = "uiScale", NumbersKey = "uiNumberFont";
    /// <summary>Fluent's font for its controls and popups (menus, tooltips): ours only when another one is picked, so
    /// the default look stays as it was.</summary>
    const string ThemeFontKey = "ContentControlThemeFontFamily";

    public static string Current { get; private set; } = Default;
    public static double Scale { get; private set; } = 1;
    /// <summary>The numbers' font id (<see cref="NumberFonts"/>).</summary>
    public static string NumberFont { get; private set; } = DefaultNumbers;

    /// <summary>The font, the numbers' font or the size changed (already applied to the windows and to <see cref="Palette"/>).</summary>
    public static event Action? Changed;

    static HashSet<string>? installed;
    static string[]? available;

    /// <summary>Reads both settings and hooks every window and popup to come. Call once, after App.axaml is loaded and
    /// before the first window is created.</summary>
    public static void Load()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<WindowBase>(OnTemplateApplied);
        try
        {
            if (AppConfig.Get(FontKey) is { } font && Available.Contains(font)) Current = font;
            // older copies saved one of four scales: any number snaps to the px grid
            if (double.TryParse(AppConfig.Get(ScaleKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0) Scale = ToScale(s * BasePx);
            if (AppConfig.Get(NumbersKey) is { } n && NumberFonts.Any(f => f.Id == n)) NumberFont = n;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the defaults */ }
        if (Current != Default) ApplyFont();
        if (NumberFont != DefaultNumbers) ApplyNumbers();
    }

    /// <summary>Whether each setting is still the one the app came with (Apariencia's "Restablecer").</summary>
    public static bool FontIsDefault => Current == Default;
    public static bool NumbersAreDefault => NumberFont == DefaultNumbers;
    public static bool ScaleIsDefault => Scale == 1;

    static FontFamily NumberFamily(string id) => new(NumberFonts.First(f => f.Id == id).Family);

    public static void SetNumberFont(string id)
    {
        if (!NumberFonts.Any(f => f.Id == id) || id == NumberFont) return;
        NumberFont = id;
        ApplyNumbers();
        Changed?.Invoke();
        Save(NumbersKey, id);
    }

    static void ApplyNumbers()
    {
        var family = NumberFamily(NumberFont);
        if (Application.Current?.Resources is { } res) res["Mono"] = family;
        Palette.SetMonoFont(family);
        Invalidate();
    }

    /// <summary>The fonts the picker shows: the default, the system's and the <see cref="Candidates"/> installed here.</summary>
    public static string[] Available => available ??= [Default, System, .. Candidates.Where(IsInstalled)];

    static bool IsInstalled(string name)
    {
        if (installed == null)
        {
            installed = new(StringComparer.OrdinalIgnoreCase);
            try { foreach (var f in FontManager.Current.SystemFonts) installed.Add(f.Name); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { /* only the default and the system's */ }
        }
        return installed.Contains(name);
    }

    /// <summary>The family a font id stands for, with Segoe UI behind for the glyphs it lacks.</summary>
    public static FontFamily Family(string font) => font switch
    {
        System => FontFamily.Default,
        Default => new FontFamily(DefaultFamily),
        _ => new FontFamily(font + ", Segoe UI"),
    };

    public static void SetFont(string font)
    {
        if (!Available.Contains(font) || font == Current) return;
        Current = font;
        ApplyFont();
        Changed?.Invoke();
        Save(FontKey, font);
    }

    /// <summary>The body text's px at the current scale.</summary>
    public static double Px => Scale * BasePx;

    /// <summary>A px size, clamped and snapped to <see cref="PxStep"/>, as a scale.</summary>
    static double ToScale(double px) => Math.Clamp(Math.Round(px / PxStep) * PxStep, MinPx, MaxPx) / BasePx;

    public static void SetPx(double px) => SetScale(ToScale(px));

    /// <summary>Ctrl + / Ctrl − (and Ctrl 0 back to 100 %): <see cref="KeyStep"/> px each, from the grid's nearest.</summary>
    public static void Step(int dir) => SetPx(dir == 0 ? BasePx : Px + dir * KeyStep);

    public static void SetScale(double scale)
    {
        scale = ToScale(scale * BasePx);
        if (scale == Scale) return;
        Scale = scale;
        foreach (var host in Hosts()) host.LayoutTransform = Transform();
        foreach (var w in Windows()) ScaleTitleBar(w);
        Changed?.Invoke();
        SaveScaleSoon();
    }

    /// <summary>The slider moves the size many times a second: it is written once it rests.</summary>
    static Avalonia.Threading.DispatcherTimer? scaleSave;

    static void SaveScaleSoon()
    {
        if (scaleSave == null)
        {
            scaleSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
            scaleSave.Tick += (_, _) =>
            {
                scaleSave.Stop();
                Save(ScaleKey, Scale.ToString(CultureInfo.InvariantCulture));
            };
        }
        scaleSave.Stop();
        scaleSave.Start();
    }

    static void Save(string key, string value) => _ = Task.Run(() =>
    {
        try { AppConfig.Set(key, value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
    });

    static void ApplyFont()
    {
        var family = Family(Current);
        if (Application.Current?.Resources is { } res)
        {
            res["Ui"] = family;
            if (Current == Default) res.Remove(ThemeFontKey);
            else res[ThemeFontKey] = family;
        }
        Palette.SetUiFont(family);
        Invalidate();
    }

    /// <summary>What is drawn by hand measures and paints with <see cref="Palette"/>'s fonts: nothing tells it they changed.</summary>
    static void Invalidate()
    {
        foreach (var w in Windows())
            foreach (var v in w.GetVisualDescendants())
                if (v is Control c)
                {
                    c.InvalidateMeasure();
                    c.InvalidateVisual();
                }
    }

    static IEnumerable<Window> Windows() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.Windows : [];

    // --- scale ----------------------------------------------------------------------------------

    static readonly List<WeakReference<LayoutTransformControl>> hosts = [];
    /// <summary>Each window's own title bar height, before the scale (its draggable strip grows with the content).</summary>
    static readonly ConditionalWeakTable<Window, StrongBox<double>> titleBars = new();

    static ITransform? Transform() => Scale == 1 ? null : new ScaleTransform(Scale, Scale);

    static IEnumerable<LayoutTransformControl> Hosts()
    {
        hosts.RemoveAll(r => !r.TryGetTarget(out _));
        foreach (var r in hosts)
            if (r.TryGetTarget(out var h)) yield return h;
    }

    static void OnTemplateApplied(WindowBase w, TemplateAppliedEventArgs e)
    {
        if (w is Window { SystemDecorations: SystemDecorations.None }) return;
        if (e.NameScope.Find<ContentPresenter>("PART_ContentPresenter") is not { } presenter) return;
        // the layer manager when there is one: adorners and overlays scale with the content
        Control target = presenter.GetVisualParent() is VisualLayerManager layers ? layers : presenter;
        var host = new LayoutTransformControl { LayoutTransform = Transform() };
        switch (target.GetVisualParent())
        {
            case Panel p:
                int i = p.Children.IndexOf(target);
                p.Children.RemoveAt(i);
                host.Child = target;
                p.Children.Insert(i, host);
                break;
            case Decorator d:
                d.Child = null;
                host.Child = target;
                d.Child = host;
                break;
            default:
                return;
        }
        hosts.RemoveAll(r => !r.TryGetTarget(out _));
        hosts.Add(new(host));
        if (w is Window win) ScaleTitleBar(win);
    }

    static void ScaleTitleBar(Window w)
    {
        if (!titleBars.TryGetValue(w, out var height))
        {
            if (w.ExtendClientAreaTitleBarHeightHint <= 0) return;
            titleBars.Add(w, height = new(w.ExtendClientAreaTitleBarHeightHint));
        }
        w.ExtendClientAreaTitleBarHeightHint = height.Value * Scale;
    }

    // --- pickers --------------------------------------------------------------------------------

    /// <summary>The font picker for Configuración → Apariencia: a chip per <see cref="Available"/> font, each name
    /// written in its own font.</summary>
    public static Control FontPicker()
    {
        var grid = new UniformGrid { Columns = 4 };
        var buttons = new List<ToggleButton>();
        foreach (string font in Available)
        {
            string name = font == System ? Loc.T("set.look.font.system") : font == Default ? "Segoe UI Variable" : font;
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = font, Margin = new Thickness(3), Padding = new Thickness(8, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new TextBlock { Text = name, FontFamily = Family(font), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            Avalonia.Automation.AutomationProperties.SetName(b, name);
            b.Click += (_, _) => SetFont(font);
            buttons.Add(b);
            grid.Children.Add(b);
        }
        void Sync() { foreach (var b in buttons) b.IsChecked = (string)b.Tag! == Current; }
        return Synced(grid, Sync);
    }

    /// <summary>The numbers' font picker for Configuración → Apariencia: a chip per <see cref="NumberFonts"/>, each a
    /// clock written in its font over its name.</summary>
    public static Control NumberPicker()
    {
        var grid = new UniformGrid { Columns = 3 };
        var buttons = new List<ToggleButton>();
        foreach (var (id, family, fixedName) in NumberFonts)
        {
            string name = fixedName ?? Loc.T("set.look.numbers." + id);
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(8, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "01:23.456", FontFamily = new FontFamily(family), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            Avalonia.Automation.AutomationProperties.SetName(b, name);
            b.Click += (_, _) => SetNumberFont(id);
            buttons.Add(b);
            grid.Children.Add(b);
        }
        void Sync() { foreach (var b in buttons) b.IsChecked = (string)b.Tag! == NumberFont; }
        return Synced(grid, Sync);
    }

    /// <summary>The size picker for Configuración → Apariencia: a slider in px of the body text, from
    /// <see cref="MinPx"/> to <see cref="MaxPx"/> in <see cref="PxStep"/>, with its value beside it.</summary>
    public static Control ScalePicker()
    {
        var slider = new Slider
        {
            Minimum = MinPx, Maximum = MaxPx, SmallChange = PxStep, LargeChange = KeyStep, TickFrequency = PxStep,
            IsSnapToTickEnabled = true, Width = 300, VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock { Classes = { "dim", "mono" }, MinWidth = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        bool syncing = false, dragging = false;
        void Show(double px) => value.Text = Loc.T("set.look.scale.value", px.ToString("0.#", CultureInfo.CurrentCulture),
            Math.Round(px / BasePx * 100).ToString(CultureInfo.CurrentCulture));
        // dragging only shows the number: rescaling the window under the thumb would move it away from the mouse
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            if (dragging) Show(e.NewValue);
            else SetPx(e.NewValue);
        };
        slider.AddHandler(Thumb.DragStartedEvent, (_, _) => dragging = true);
        slider.AddHandler(Thumb.DragCompletedEvent, (_, _) => { dragging = false; SetPx(slider.Value); });
        void Sync()
        {
            syncing = true;
            slider.Value = Px;
            syncing = false;
            Show(Px);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { slider, value } };
        return Synced(row, Sync);
    }

    /// <summary>Keeps a picker's checks on the current value (a click on one, or another picker), while it is shown.</summary>
    static Control Synced(Control picker, Action sync)
    {
        sync();
        picker.AttachedToVisualTree += (_, _) => { Changed += sync; sync(); };
        picker.DetachedFromVisualTree += (_, _) => Changed -= sync;
        return picker;
    }
}
