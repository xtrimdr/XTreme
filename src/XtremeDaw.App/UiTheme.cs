using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// UI themes, port of the web app's theme.ts: always dark, an accent color plus a surface palette (the greys,
/// tinted). Applied live to the App.axaml brushes, to <see cref="Palette"/> and to the Fluent accent colors.
/// Same as the web except it adds nine accent presets, three dark and six mid-grey surface palettes and four adjustments
/// (brightness, contrast, saturation, warmth; #317), and is remembered in the program's config ("theme") instead of
/// localStorage.
/// </summary>
static class UiTheme
{
    public sealed record Surface(string Id, string Name, Color App, Color Panel, Color Raised, Color Hover, Color Line, Color Fg, Color Dim, Color Faint);

    /// <summary>
    /// The accent, the surface palette and four adjustments over them (asked by the user, #317), each -1..1 with 0 as the
    /// palette comes: <see cref="Brightness"/> lifts or darkens the backgrounds, <see cref="Contrast"/> spreads or
    /// closes the layers and texts away from the window's background, <see cref="Saturation"/> tints the surfaces and
    /// the accent more or less, <see cref="Warmth"/> turns the surfaces warmer (amber) or cooler (blue).
    /// </summary>
    public sealed record Settings(string Accent, string Surface, double Brightness = 0, double Contrast = 0, double Saturation = 0, double Warmth = 0)
    {
        [JsonIgnore] public bool Adjusted => Brightness != 0 || Contrast != 0 || Saturation != 0 || Warmth != 0;

        /// <summary>The same colors, with no adjustment. Not saved: each read is a new record, so serializing it
        /// recursed until the cycle check threw and closed the app (#317).</summary>
        [JsonIgnore] public Settings Plain => this with { Brightness = 0, Contrast = 0, Saturation = 0, Warmth = 0 };
    }

    /// <summary>How far each adjustment goes at its end (±1): the backgrounds' lightness moves up to 0.12, the distance of
    /// layers and texts from the background up to ±40 %, the saturation down to grey or up to double, the warmth up to
    /// 14 levels of red against blue on the backgrounds (half on the texts, so they stay neutral enough to read).</summary>
    const double BrightnessSpan = 0.12, ContrastSpan = 0.4, WarmthSpan = 14;

    static Surface S(string id, string name, params string[] c) => new(id, name,
        Color.Parse(c[0]), Color.Parse(c[1]), Color.Parse(c[2]), Color.Parse(c[3]), Color.Parse(c[4]), Color.Parse(c[5]), Color.Parse(c[6]), Color.Parse(c[7]));

    public static readonly Surface[] Surfaces =
    [
        S("neutral", "Grafito", "#0b0b0d", "#131316", "#1c1c20", "#26262b", "#27272c", "#ececef", "#a1a1aa", "#6b6b74"),
        S("slate", "Pizarra", "#0a0c10", "#11151b", "#191e26", "#222833", "#232a35", "#e8edf3", "#9aa6b5", "#64707f"),
        S("warm", "Madera", "#0d0b0a", "#161311", "#201c19", "#2a2521", "#2b2622", "#efebe6", "#ada49a", "#756c63"),
        S("forest", "Bosque", "#090c0b", "#111614", "#1a201d", "#232b27", "#242c28", "#e8efeb", "#9cada5", "#64736c"),
        S("plum", "Ciruela", "#0c0a0e", "#151218", "#1e1a22", "#28232d", "#29242f", "#eeeaf1", "#a69eae", "#6e6677"),
        S("black", "Negro", "#000000", "#0a0a0b", "#141416", "#1e1e21", "#222225", "#ededef", "#a0a0a6", "#68686f"),
        S("ocean", "Océano", "#080c12", "#101720", "#18222e", "#202d3c", "#263445", "#e7eef7", "#9cacc1", "#687b93"),
        S("coffee", "Café", "#100b08", "#1a130e", "#261c15", "#32271e", "#382c22", "#f3ece3", "#b8a797", "#847161"),
        S("smoke", "Humo", "#101112", "#191b1d", "#232629", "#2d3135", "#34393e", "#eceff1", "#a8b0b7", "#747e87"),
        // mid greys (#272): lighter studio surfaces, the text still light. Each layer steps ~11 levels from the one
        // below and Line sits above Hover, so panels, sections and borders separate; Dim and Faint are raised so they
        // keep about 4:1 against Hover
        S("steel", "Acero", "#222326", "#2c2d31", "#37383d", "#43454b", "#50525a", "#f5f5f7", "#d0d1d6", "#a3a5ad"),
        S("cement", "Cemento", "#29292a", "#333334", "#3e3e40", "#4b4b4d", "#59595b", "#f7f7f7", "#d4d4d4", "#a8a8a8"),
        S("storm", "Tormenta", "#282e35", "#323942", "#3d454f", "#49535e", "#57626e", "#f3f7fb", "#d0d9e2", "#a5b0bc"),
        S("moss", "Musgo", "#292d28", "#333832", "#3e443d", "#4a5249", "#586157", "#f3f7f1", "#d2d9cf", "#a8b1a5"),
        S("sand", "Arena", "#2f2a26", "#3a342f", "#463f39", "#534b44", "#625952", "#f9f4ee", "#ddd2c7", "#b2a597"),
        S("ash", "Ceniza", "#36373a", "#404144", "#4c4d51", "#595a5e", "#67686d", "#fafafb", "#e2e3e6", "#b8babe"),
        // Tron Legacy (#191): the film's blue-black, with its edges and texts tinted the cyan of its light lines
        S(TronSurface, "Tron Legacy", "#010409", "#03090f", "#071420", "#0c1e2c", "#103446", "#e0f7ff", "#8ec3d3", "#4d7a8a"),
    ];

    /// <summary>The Tron Legacy theme's surface and accent (TronLegacy, in XTreme): the film's light-line cyan.</summary>
    public const string TronSurface = "tron", TronAccent = "#5ce1ff";

    /// <summary>Sorted by hue, warm to cool, so neighbours in the picker look alike.</summary>
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Fuego", "#ff5809"), ("Naranja", "#fb923c"), ("Ámbar", "#f5a524"), ("Oro", "#facc15"), ("Lima", "#a3e635"),
        ("Verde", "#4ade80"), ("Esmeralda", "#34d399"), ("Turquesa", "#2dd4bf"), ("Menta", "#99f6e4"), ("Cyan", "#22d3ee"),
        ("Celeste", "#7dd3fc"), ("Azul", "#60a5fa"), ("Índigo", "#818cf8"), ("Violeta", "#a78bfa"), ("Lavanda", "#c4b5fd"),
        ("Fucsia", "#e879f9"), ("Rosa", "#f472b6"),
    ];

    public static readonly Settings Default = new("#22d3ee", "neutral");

    /// <summary>Where the theme is kept in config.json: the bench keeps its own (asked by the user, #151).</summary>
    public static string ConfigKey { get; set; } = "theme";

    public static Settings Current { get; private set; } = Default;
    static Task saving = Task.CompletedTask;

    /// <summary>Fired after a live change, so the window refreshes what it painted from the old colors.</summary>
    public static event Action? Changed;

    static bool IsHex(string? s) => s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    public static Settings Read()
    {
        try
        {
            if (AppConfig.Get(ConfigKey) is { } json && JsonSerializer.Deserialize<Settings>(json, ProjectStore.Json) is { } t
                && IsHex(t.Accent) && Surfaces.Any(s => s.Id == t.Surface))
                return t with
                {
                    Accent = t.Accent.ToLowerInvariant(), Brightness = Unit(t.Brightness), Contrast = Unit(t.Contrast),
                    Saturation = Unit(t.Saturation), Warmth = Unit(t.Warmth),
                };
        }
        catch (JsonException) { }
        return Default;
    }

    static double Unit(double v) => double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;

    /// <summary>The surface with the theme's adjustments applied (see <see cref="Settings"/>); as it is with none.</summary>
    public static Surface Adjust(Surface s, Settings t)
    {
        if (!t.Adjusted) return s;
        double b = Unit(t.Brightness) * BrightnessSpan, k = 1 + Unit(t.Contrast) * ContrastSpan, sat = 1 + Unit(t.Saturation);
        double w = Unit(t.Warmth) * WarmthSpan;
        // contrast pivots on the window's background, once brightened: the layers and texts move away from it or closer
        double app = Math.Clamp(s.App.ToHsl().L + b, 0, 1);
        Color Tone(Color c, bool background)
        {
            var hsl = c.ToHsl();
            double l = background ? Math.Clamp(hsl.L + b, 0, 1) : hsl.L;
            l = Math.Clamp(app + (l - app) * k, 0, 1);
            var o = new HslColor(1, hsl.H, Math.Clamp(hsl.S * sat, 0, 1), l).ToRgb();
            double ww = background ? w : w / 2;
            return Color.FromRgb((byte)Math.Clamp(Math.Round(o.R + ww), 0, 255), o.G, (byte)Math.Clamp(Math.Round(o.B - ww), 0, 255));
        }
        return s with
        {
            App = Tone(s.App, true), Panel = Tone(s.Panel, true), Raised = Tone(s.Raised, true), Hover = Tone(s.Hover, true),
            Line = Tone(s.Line, true), Fg = Tone(s.Fg, false), Dim = Tone(s.Dim, false), Faint = Tone(s.Faint, false),
        };
    }

    /// <summary>The accent with the theme's saturation (the other adjustments are the surfaces').</summary>
    static Color AdjustAccent(Color c, Settings t)
    {
        if (t.Saturation == 0) return c;
        var hsl = c.ToHsl();
        return new HslColor(1, hsl.H, Math.Clamp(hsl.S * (1 + Unit(t.Saturation)), 0, 1), hsl.L).ToRgb();
    }

    static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    static Color Mix(Color c, Color to, double k) => Color.FromRgb(
        (byte)Math.Round(c.R + (to.R - c.R) * k), (byte)Math.Round(c.G + (to.G - c.G) * k), (byte)Math.Round(c.B + (to.B - c.B) * k));

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>The accent hues the state colors would be mistaken for (asked by the user: the states move, the accents stay).</summary>
    const double WarmFrom = 10, WarmTo = 60, GreenFrom = 75, GreenTo = 165;

    /// <summary>
    /// Mute and "in tune" for this accent: amber and green, unless the accent is near them (Fuego, Naranja, Ámbar, Oro
    /// turn mute blue; Lima, Verde, Esmeralda turn "in tune" cyan), so a lit M or a tuned note never reads as "selected".
    /// Recording stays red whatever the accent.
    /// </summary>
    public static (Color Mute, Color InTune) StateColors(Color accent)
    {
        var hsv = accent.ToHsv();
        bool colorful = hsv.S > 0.35;
        bool warm = colorful && hsv.H >= WarmFrom && hsv.H <= WarmTo, green = colorful && hsv.H >= GreenFrom && hsv.H <= GreenTo;
        return (Color.Parse(warm ? "#60a5fa" : "#f59e0b"), Color.Parse(green ? "#22d3ee" : "#22c55e"));
    }

    /// <summary>Remembers the current theme (in the background, in order, so the last pick is the one that stays).</summary>
    public static void Save()
    {
        string json = JsonSerializer.Serialize(Current, ProjectStore.Json);
        saving = saving.ContinueWith(_ =>
        {
            try { AppConfig.Set(ConfigKey, json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the theme still applies for this session */ }
        }, TaskScheduler.Default);
    }

    /// <summary>Applies the theme everywhere; <paramref name="persist"/> also remembers it (in the background).</summary>
    public static void Apply(Settings theme, bool persist = true)
    {
        var surface = Adjust(Surfaces.FirstOrDefault(s => s.Id == theme.Surface) ?? Surfaces[0], theme);
        var accent = AdjustAccent(IsHex(theme.Accent) ? Color.Parse(theme.Accent) : Color.Parse(Default.Accent), theme);
        var onAccent = Luminance(accent) > 0.3 ? Color.Parse("#0b0b0d") : Colors.White;
        Current = theme;
        var (mute, inTune) = StateColors(accent);
        Palette.Set(surface, accent, onAccent, mute, inTune);

        if (Application.Current is { } app)
        {
            var r = app.Resources;
            void Brush(string key, Color c)
            {
                if (r.TryGetResource(key, null, out var v) && v is SolidColorBrush b) b.Color = c;
            }
            (string, Color)[] tokens =
            [
                ("App", surface.App), ("Panel", surface.Panel), ("Raised", surface.Raised), ("Hover", surface.Hover), ("Line", surface.Line),
                ("Fg", surface.Fg), ("Dim", surface.Dim), ("Faint", surface.Faint), ("Accent", accent),
            ];
            foreach (var (key, c) in tokens)
            {
                Brush(key, c);
                r[key + "Color"] = c;
            }
            Brush("Mute", mute);
            Brush("InTune", inTune);
            Brush("AccentSoft", WithAlpha(accent, 0x24));
            Brush("AccentFg", onAccent);
            Brush("AccentHover", Mix(accent, Colors.White, 0.2));
            Brush("AccentTint", WithAlpha(accent, 0x0f));
            Brush("Accent25", WithAlpha(accent, 0x40));
            Brush("Accent40", WithAlpha(accent, 0x66));
            Brush("Accent50", WithAlpha(accent, 0x80));
            Brush("Accent70", WithAlpha(accent, 0xb3));
            if (r.TryGetResource("HoverGlow", null, out var g) && g is DropShadowEffect glow) glow.Color = accent;
            // the Fluent controls (sliders, switches) read these as dynamic resources
            r["SystemAccentColor"] = accent;
            r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.1);
            r["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.22);
            r["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.35);
            r["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.2);
            r["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.4);
            r["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.6);
        }

        if (persist) Save();
        Changed?.Invoke();
    }
}
