using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The design tokens for code that draws (same values as App.axaml / the web app's index.css). The surface and
/// accent tokens are mutable brushes so the theme picker can change them live (<see cref="UiTheme.Apply"/>): code
/// that keeps one of these brushes follows the theme, code that reads a color (<see cref="AccentColor"/>, …) must
/// read it again when it draws.
/// </summary>
static class Palette
{
    // Brushes and pens are asked for inside Render, every frame while audio runs: the same color gives back the same
    // immutable object instead of new garbage each time (see textCache). First in the class: the brushes below use them.
    static readonly Dictionary<string, IBrush> hexBrushes = [];
    static readonly Dictionary<Color, IBrush> alphaBrushes = [];
    static readonly Dictionary<(Color, double, double, PenLineCap, PenLineJoin), IPen> pens = [];
    /// <summary>Past this many entries a cache starts over (colors computed from a value, say).</summary>
    const int StyleCacheMax = 1024;

    static readonly SolidColorBrush app = new(Color.Parse("#0b0b0d")), panel = new(Color.Parse("#131316")),
        raised = new(Color.Parse("#1c1c20")), hover = new(Color.Parse("#26262b")), line = new(Color.Parse("#27272c")),
        fg = new(Color.Parse("#ececef")), dim = new(Color.Parse("#a1a1aa")), faint = new(Color.Parse("#6b6b74")),
        accent = new(Color.Parse("#22d3ee")), accentSoft = new(Color.Parse("#2422d3ee")), accentFg = new(Color.Parse("#0b0b0d"));

    public static Color AccentColor => accent.Color;
    public static IBrush App => app;
    public static IBrush Panel => panel;
    public static IBrush Raised => raised;
    public static IBrush Hover => hover;
    public static IBrush Line => line;
    public static IBrush Fg => fg;
    public static IBrush Dim => dim;
    public static IBrush Faint => faint;
    public static IBrush Accent => accent;
    public static IBrush AccentSoft => accentSoft;
    /// <summary>Text and icons on an accent fill: dark for light accents, white for deep ones (theme.ts accent-fg).</summary>
    public static IBrush AccentFg => accentFg;
    public static readonly Color RecColor = Color.Parse("#ef4444");
    public static readonly IBrush Rec = new ImmutableSolidColorBrush(RecColor);
    public static readonly IBrush RecSoft = Brush("#24ef4444");
    public static readonly IBrush Composition = Brush("#09090b");
    /// <summary>Mute on (M lit, a muted track) and the meters' -6..0 dB zone (App.axaml Mute): amber, or blue with a
    /// warm accent (<see cref="UiTheme.StateColors"/>).</summary>
    public static Color MuteColor => mute.Color;
    public static IBrush Mute => mute;
    static readonly SolidColorBrush mute = new(Color.Parse("#f59e0b")), inTune = new(Color.Parse("#22c55e"));
    public static Color AppColor => app.Color;
    public static Color PanelColor => panel.Color;
    public static Color RaisedColor => raised.Color;
    public static Color HoverColor => hover.Color;
    public static Color LineColor => line.Color;
    public static Color FgColor => fg.Color;
    public static Color DimColor => dim.Color;
    public static Color FaintColor => faint.Color;

    /// <summary>Called by <see cref="UiTheme.Apply"/> only.</summary>
    internal static void Set(UiTheme.Surface s, Color accentColor, Color onAccent, Color muteColor, Color inTuneColor)
    {
        mute.Color = muteColor;
        inTune.Color = inTuneColor;
        app.Color = s.App; panel.Color = s.Panel; raised.Color = s.Raised; hover.Color = s.Hover; line.Color = s.Line;
        fg.Color = s.Fg; dim.Color = s.Dim; faint.Color = s.Faint;
        accent.Color = accentColor;
        accentSoft.Color = Color.FromArgb(0x24, accentColor.R, accentColor.G, accentColor.B);
        accentFg.Color = onAccent;
    }

    /// <summary>
    /// Each effect family's color (asked by the user, #187: every native effect with an identity of its own, decided over
    /// "one accent"): its panel's title, its lit knobs and its graph. Fixed hues, like the meters', none of them the
    /// recording red; an effect without a family takes the accent.
    /// </summary>
    public static readonly Color FxDynamics = Color.Parse("#a78bfa"), FxTone = Color.Parse("#4ade80"), FxDrive = Color.Parse("#fb923c"),
        FxSpace = Color.Parse("#60a5fa"), FxMod = Color.Parse("#f472b6");

    public static Color FamilyColor(XtremeDaw.Projects.FxFamily f) => f switch
    {
        XtremeDaw.Projects.FxFamily.Dynamics => FxDynamics,
        XtremeDaw.Projects.FxFamily.Tone => FxTone,
        XtremeDaw.Projects.FxFamily.Drive => FxDrive,
        XtremeDaw.Projects.FxFamily.Space => FxSpace,
        XtremeDaw.Projects.FxFamily.Mod => FxMod,
        _ => AccentColor,
    };

    /// <summary>
    /// The people of a shared session (#291), by the color index the host gives each one (0 = the host): fixed, so a
    /// person has the same color on every copy whatever its theme; none is the record red or the mute amber.
    /// </summary>
    static readonly Color[] people =
    [
        Color.Parse("#a78bfa"), Color.Parse("#34d399"), Color.Parse("#f472b6"), Color.Parse("#60a5fa"),
        Color.Parse("#fb923c"), Color.Parse("#a3e635"), Color.Parse("#e879f9"), Color.Parse("#2dd4bf"),
    ];

    /// <summary>A person's color in a shared session by its index (<see cref="people"/>, round again after the last).</summary>
    public static Color PersonColor(int index) => people[(index % people.Length + people.Length) % people.Length];

    /// <summary>The mixer meters' gradient (Mixer.tsx METER_BG): green, yellow near 0 dB, red at the top.</summary>
    public static readonly Color MeterGreen = Color.Parse("#22c55e");
    public static readonly Color MeterYellow = Color.Parse("#eab308");
    public static readonly Color MeterRed = Color.Parse("#ef4444");
    /// <summary>"In tune" in the tuner: the meters' green, not a color of its own (App.axaml InTune), or cyan with a
    /// green accent (<see cref="UiTheme.StateColors"/>).</summary>
    public static IBrush InTune => inTune;

    /// <summary>The interface's font (<see cref="UiFonts"/>, picked in Configuración → Apariencia): read it again on
    /// every draw, it changes live.</summary>
    public static Typeface Ui { get; private set; } = new(UiFonts.DefaultFamily);
    public static Typeface UiBold { get; private set; } = new(UiFonts.DefaultFamily, FontStyle.Normal, FontWeight.SemiBold);
    /// <summary>The numbers' font (<see cref="UiFonts.NumberFont"/>, also picked in Apariencia): read it on every draw too.</summary>
    public static Typeface Mono { get; private set; } = new(UiFonts.MonoFamily);
    public static Typeface MonoBold { get; private set; } = new(UiFonts.MonoFamily, FontStyle.Normal, FontWeight.Bold);
    /// <summary>Key names and code-like text: always Cascadia Mono, a digital number font can't write them.</summary>
    public static readonly FontFamily Code = new(UiFonts.MonoFamily);

    /// <summary>Stores a new entry, starting the cache over when it is full. The lookups stay inline: a lambda that
    /// captures would be garbage of its own on every call.</summary>
    static TValue Store<TKey, TValue>(Dictionary<TKey, TValue> cache, TKey key, TValue value) where TKey : notnull
    {
        if (cache.Count >= StyleCacheMax) cache.Clear();
        return cache[key] = value;
    }

    public static IBrush Brush(string hex) =>
        hexBrushes.TryGetValue(hex, out var b) ? b : Store(hexBrushes, hex, new ImmutableSolidColorBrush(Color.Parse(hex)));

    public static IBrush Alpha(Color c, double a)
    {
        var k = Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);
        return alphaBrushes.TryGetValue(k, out var b) ? b : Store(alphaBrushes, k, new ImmutableSolidColorBrush(k));
    }

    public static IPen Pen(IBrush b, double w = 1) => Pen(b, w, PenLineCap.Flat, PenLineJoin.Miter);

    /// <summary>A pen frozen to the brush's current color (a theme change gives a different pen on the next frame).</summary>
    public static IPen Pen(IBrush b, double w, PenLineCap cap, PenLineJoin join = PenLineJoin.Miter)
    {
        if (b is not ISolidColorBrush s) return new ImmutablePen(b.ToImmutable(), w, null, cap, join);
        var k = (s.Color, s.Opacity, w, cap, join);
        return pens.TryGetValue(k, out var p) ? p : Store(pens, k, new ImmutablePen(b.ToImmutable(), w, null, cap, join));
    }

    static readonly Dictionary<(Color, double, double), IPen> dashedPens = [];
    /// <summary>The drag outlines' dash (a clip's ghost, a row's drop hole): 4 on, 3 off, in pen widths.</summary>
    static readonly ImmutableDashStyle DragDash = new([4, 3], 0);

    /// <summary>A dashed pen (<see cref="DragDash"/>) frozen to the brush's current color, cached like <see cref="Pen(IBrush, double, PenLineCap, PenLineJoin)"/>.</summary>
    public static IPen Dashed(IBrush b, double w)
    {
        if (b is not ISolidColorBrush s) return new ImmutablePen(b.ToImmutable(), w, DragDash);
        var k = (s.Color, s.Opacity, w);
        return dashedPens.TryGetValue(k, out var p) ? p : Store(dashedPens, k, new ImmutablePen(b.ToImmutable(), w, DragDash));
    }

    /// <summary>Laid-out texts by content and style. The drawn controls repaint every frame while audio runs, and laying
    /// the same labels out again each time churned ~20 MB/s: enough GCs to stall the audio callback. A
    /// <see cref="TextLayout"/>, unlike a FormattedText (which formats its lines again on every draw), is laid out once.
    /// Callers must not change what they get back.</summary>
    static readonly Dictionary<(string, double, IBrush, Typeface, double), TextLayout> textCache = [];
    /// <summary>Past this many texts (a scroll through many values) the cache starts over.</summary>
    const int TextCacheMax = 4096;

    public static TextLayout Text(string s, double size, IBrush brush, Typeface? face = null) => CachedText(s, size, brush, face ?? Ui, 0);

    static TextLayout CachedText(string s, double size, IBrush brush, Typeface face, double maxWidth)
    {
        var key = (s, size, brush, face, maxWidth);
        if (textCache.TryGetValue(key, out var t)) return t;
        if (textCache.Count >= TextCacheMax) textCache.Clear();
        t = maxWidth > 0
            ? new TextLayout(s, face, size, brush, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: maxWidth, maxLines: 1)
            : new TextLayout(s, face, size, brush);
        return textCache[key] = t;
    }

    /// <summary>Called by <see cref="UiFonts"/> only: the texts laid out in the old font are dropped with it.</summary>
    internal static void SetUiFont(FontFamily family)
    {
        Ui = new Typeface(family);
        UiBold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        textCache.Clear();
    }

    internal static void SetMonoFont(FontFamily family)
    {
        Mono = new Typeface(family);
        MonoBold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        textCache.Clear();
    }

    public static void DrawText(this DrawingContext ctx, TextLayout t, Point at) => t.Draw(ctx, at);

    public static void DrawText(this DrawingContext ctx, string s, double size, IBrush brush, Point at, Typeface? face = null, double maxWidth = 0) =>
        CachedText(s, size, brush, face ?? Ui, maxWidth).Draw(ctx, at);

    public static double ToDb(float x) => x > 1e-6 ? 20 * Math.Log10(x) : -120;
}
