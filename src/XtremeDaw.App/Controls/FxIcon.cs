using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Port of FX_ICONS (GuitarCam/src/components/fxIcons.ts): one icon per effect, shared by the inspector and the mixer
/// so both always show the same one. Drawn from lucide's own paths (24×24 grid, 2 px round stroke, like the web)
/// because Segoe Fluent Icons has no flame or waves: Sat = Flame, Comp = Gauge, EQ = SlidersVertical,
/// Reverb = Waves, Delay = Repeat, Gate = DoorOpen, a VST3 effect = Plug. <see cref="Create"/> also takes a Segoe glyph, so a section header accepts either.
/// </summary>
public sealed class FxIcon : Control
{
    public const string Sat = "sat", Comp = "comp", Eq = "eq", Reverb = "reverb", Delay = "delay", Gate = "gate", Transpose = "transpose", Vst = "vst";
    /// <summary>The master's effects, by their slot kinds; Limpieza and Color are EQs, Multibanda a compressor, Cinta a saturation.</summary>
    public const string Clean = "clean", Bands = "bands", Color = "color", Tape = "tape", WidthFx = "width", ClipFx = "softclip", Limit = "limiter";
    /// <summary>The modulation effects, by their slot kinds.</summary>
    public const string Chorus = "chorus", Flanger = "flanger", Phaser = "phaser", Tremolo = "tremolo";
    /// <summary>Not an effect: the timeline's Mano tool (its button and pointer), because Segoe has no open hand.</summary>
    public const string Hand = "hand";
    /// <summary>Not an effect: a bus, whatever its slots hold, and the badge of one without an icon of its own.</summary>
    public const string Bus = "bus";

    // lucide-react 1.59 paths; a relative "m" that starts a subpath is written absolute ("M… l…") so it doesn't
    // depend on where the previous subpath ended
    static readonly Dictionary<string, Geometry> Paths = new()
    {
        [Sat] = Geometry.Parse("M12 3q1 4 4 6.5t3 5.5a1 1 0 0 1-14 0 5 5 0 0 1 1-3 1 1 0 0 0 5 0c0-2-1.5-3-1.5-5q0-2 2.5-4"),
        [Comp] = Geometry.Parse("M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0"),
        [Eq] = Geometry.Parse("M10 8h4 M12 21v-9 M12 8V3 M17 16h4 M19 12V3 M19 21v-5 M3 14h4 M5 10V3 M5 21v-7"),
        [Reverb] = Geometry.Parse("M2 5q2.5 2 5 0t5 0 5 0 5 0 M2 12q2.5 2 5 0t5 0 5 0 5 0 M2 19q2.5 2 5 0t5 0 5 0 5 0"),
        [Delay] = Geometry.Parse("M17 2l4 4-4 4 M3 11v-1a4 4 0 0 1 4-4h14 M7 22l-4-4 4-4 M21 13v1a4 4 0 0 1-4 4H3"),
        // lucide "door-open"
        [Gate] = Geometry.Parse("M11 20H2 M11 4.562v16.157a1 1 0 0 0 1.242.97L19 20V5.562a2 2 0 0 0-1.515-1.94l-4-1A2 2 0 0 0 11 4.561z M11 4H8a2 2 0 0 0-2 2v14 M14 12h.01 M22 20h-3"),
        [Clean] = Geometry.Parse("M10 8h4 M12 21v-9 M12 8V3 M17 16h4 M19 12V3 M19 21v-5 M3 14h4 M5 10V3 M5 21v-7"),
        [Color] = Geometry.Parse("M10 8h4 M12 21v-9 M12 8V3 M17 16h4 M19 12V3 M19 21v-5 M3 14h4 M5 10V3 M5 21v-7"),
        [Bands] = Geometry.Parse("M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0"),
        [Tape] = Geometry.Parse("M12 3q1 4 4 6.5t3 5.5a1 1 0 0 1-14 0 5 5 0 0 1 1-3 1 1 0 0 0 5 0c0-2-1.5-3-1.5-5q0-2 2.5-4"),
        // lucide "move-horizontal", "audio-waveform" and "arrow-down-to-line"
        [WidthFx] = Geometry.Parse("M8 8l-4 4 4 4 M16 8l4 4-4 4 M2 12h20"),
        [ClipFx] = Geometry.Parse("M2 13a2 2 0 0 0 2-2V7a2 2 0 0 1 4 0v13a2 2 0 0 0 4 0V4a2 2 0 0 1 4 0v13a2 2 0 0 0 4 0v-4a2 2 0 0 1 2-2"),
        [Limit] = Geometry.Parse("M12 17V3 M6 11l6 6 6-6 M19 21H5"),
        // drawn for this grid: two waves side by side, one fast wave, a phase wheel; lucide "activity" for the tremolo
        [Chorus] = Geometry.Parse("M2 9q5-8 10 0t10 0 M2 15q5-8 10 0t10 0"),
        [Flanger] = Geometry.Parse("M2 12q2.5-9 5 0t5 0 5 0 5 0"),
        [Phaser] = Geometry.Parse("M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20 M12 2v20 M2 12h20"),
        [Tremolo] = Geometry.Parse("M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2"),
        // lucide "plug": a VST3 effect, whatever it does
        [Vst] = Geometry.Parse("M12 22v-5 M9 8V2 M15 8V2 M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z"),
        // lucide "music": a note for the track's pitch (Transponer)
        [Transpose] = Geometry.Parse("M9 18V5l12-2v13 M3 18a3 3 0 1 0 6 0a3 3 0 1 0-6 0 M15 16a3 3 0 1 0 6 0a3 3 0 1 0-6 0"),
        // lucide "hand"
        [Hand] = Geometry.Parse("M18 11V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2 M14 10V4a2 2 0 0 0-2-2a2 2 0 0 0-2 2v2 M10 10.5V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2v8 M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15"),
        // lucide "audio-lines"
        [Bus] = Geometry.Parse("M2 10v3 M6 6v11 M10 3v18 M14 8v7 M18 5v13 M22 10v3"),
    };

    /// <summary>The same property as TextBlock.Foreground, so <see cref="SetColor"/> paints either kind of icon.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<FxIcon>();

    static FxIcon() => AffectsRender<FxIcon>(ForegroundProperty);

    readonly Geometry path;

    FxIcon(Geometry path) => this.path = path;

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The effect's icon for one of the keys above, otherwise a Segoe glyph. A drawn icon takes a box 2 px
    /// bigger than the glyph's font size: lucide leaves a margin inside its grid that Segoe glyphs don't.</summary>
    public static Control Create(string icon, double fontSize) => Paths.TryGetValue(icon, out var path)
        ? new FxIcon(path) { Width = fontSize + 2, Height = fontSize + 2 }
        : new TextBlock { Classes = { "icon" }, Text = icon, FontSize = fontSize };

    public static void SetColor(Control icon, IBrush brush) => icon.SetValue(TextElement.ForegroundProperty, brush);

    /// <summary>Draws an effect's icon straight into a drawing context (the timeline's track headers are hand-drawn).</summary>
    public static void Draw(DrawingContext ctx, string icon, Rect box, IBrush brush, double stroke = 2)
    {
        if (!Paths.TryGetValue(icon, out var path)) return;
        double s = box.Width / 24;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(box.X, box.Y)))
            ctx.DrawGeometry(null, Palette.Pen(brush, stroke, PenLineCap.Round, PenLineJoin.Round), path);
    }

    public override void Render(DrawingContext ctx)
    {
        double s = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using (ctx.PushTransform(Matrix.CreateScale(s, s)))
            ctx.DrawGeometry(null, Palette.Pen(Foreground ?? Palette.Dim, 2, PenLineCap.Round, PenLineJoin.Round), path);
    }
}
