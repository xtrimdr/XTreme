using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The timeline tools' pointers (native only): each tool's own icon, the same glyph as its button in the tool strip,
/// white with a dark outline so it reads on clips and on the empty timeline alike. Built once into a bitmap (twice for
/// Zoom: + and, with Alt, −). Rango uses the system's text cursor and Seleccionar the usual ones, so they are not here.
/// The hand is drawn from <see cref="FxIcon"/>'s path because Segoe Fluent Icons has no open hand. <see cref="Stretch"/>
/// is no tool: it is the pointer on a clip's edge with Ctrl, a two-way arrow under a stopwatch, so it never reads as a trim.
/// <see cref="Loop"/> is the pointer on a clip's or block's loop corner: the two round arrows its repeats show (<see cref="LoopGlyph"/>).
/// </summary>
static class ToolCursors
{
    /// <summary>Px of the cursor bitmap (Windows' standard size at 100 %).</summary>
    const int Size = 32;
    /// <summary>Px of the icon inside it.</summary>
    const double IconSize = 20;

    static readonly Typeface Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Dictionary<(TimelineTool, bool), Cursor> cache = [];

    /// <summary>The Segoe glyph of each tool, shared with its button; Zoom with Alt zooms out (−). The hand is
    /// <see cref="FxIcon.Hand"/>, a drawn icon's key.</summary>
    public static string Glyph(TimelineTool t, bool alt = false) => t switch
    {
        TimelineTool.Select => "\uE8B0", TimelineTool.Draw => "\uE70F", TimelineTool.Paint => "\uE771",TimelineTool.Range => "\uE933", TimelineTool.Split => "\uE8C6",
        TimelineTool.Erase => "\uE75C", TimelineTool.Listen => "\uE7F6", TimelineTool.Zoom => alt ? "\uE71F" : "\uE8A3",
        _ => FxIcon.Hand,
    };

    /// <summary>The stopwatch over the arrow of <see cref="Stretch"/>, smaller than a tool's icon.</summary>
    const string StretchGlyph = "";
    const double StretchGlyphSize = 13;
    /// <summary>The arrow's line in the cursor, and the arrow itself: the click lands on it.</summary>
    const int StretchArrowY = 22;
    static Cursor? stretch, loop;

    public static Cursor Stretch => stretch ??= MakeStretch();

    /// <summary>The Segoe glyph of a loop (two round arrows): the loop handle, its pointer and every repeat show it.</summary>
    public const string LoopGlyph = "";

    public static Cursor Loop => loop ??= MakeLoop();

    static Cursor MakeLoop()
    {
        try
        {
            var bmp = new RenderTargetBitmap(new PixelSize(Size, Size));
            using (var ctx = bmp.CreateDrawingContext())
            {
                var outline = new TextLayout(LoopGlyph, Icons, IconSize, Brushes.Black);
                var glyph = new TextLayout(LoopGlyph, Icons, IconSize, Brushes.White);
                var at = new Point((Size - glyph.Width) / 2, (Size - glyph.Height) / 2);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0) outline.Draw(ctx, at + new Point(dx, dy));
                glyph.Draw(ctx, at);
            }
            return new Cursor(bmp, new PixelPoint(Size / 2, Size / 2));
        }
        catch { return new Cursor(StandardCursorType.RightSide); }
    }

    static Cursor MakeStretch()
    {
        try { return new Cursor(RenderStretch(), new PixelPoint(Size / 2, StretchArrowY)); }
        catch { return new Cursor(StandardCursorType.SizeWestEast); }
    }

    static Bitmap RenderStretch()
    {
        var bmp = new RenderTargetBitmap(new PixelSize(Size, Size));
        using var ctx = bmp.CreateDrawingContext();
        var arrow = Geometry.Parse($"M4 {StretchArrowY}H28 M9 {StretchArrowY - 5}L4 {StretchArrowY}L9 {StretchArrowY + 5} M23 {StretchArrowY - 5}L28 {StretchArrowY}L23 {StretchArrowY + 5}");
        ctx.DrawGeometry(null, new Pen(Brushes.Black, 4.5, lineCap: PenLineCap.Round), arrow);
        ctx.DrawGeometry(null, new Pen(Brushes.White, 2, lineCap: PenLineCap.Round), arrow);
        var outline = new TextLayout(StretchGlyph, Icons, StretchGlyphSize, Brushes.Black);
        var glyph = new TextLayout(StretchGlyph, Icons, StretchGlyphSize, Brushes.White);
        var at = new Point((Size - glyph.Width) / 2, 0);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) outline.Draw(ctx, at + new Point(dx, dy));
        glyph.Draw(ctx, at);
        return bmp;
    }

    public static Cursor Of(TimelineTool t, bool alt)
    {
        alt &= t == TimelineTool.Zoom;
        if (cache.TryGetValue((t, alt), out var c)) return c;
        // where the click lands: the pencil's, the brush's and the eraser's tips, the lens' middle; the rest, the middle
        var hot = t switch
        {
            TimelineTool.Erase => new PixelPoint(10, 22),
            TimelineTool.Draw or TimelineTool.Paint => new PixelPoint(7, 25),
            TimelineTool.Zoom => new PixelPoint(14, 14),
            _ => new PixelPoint(Size / 2, Size / 2),
        };
        try { c = new Cursor(Render(t, alt), hot); }
        catch { c = new Cursor(t == TimelineTool.Hand ? StandardCursorType.SizeAll : StandardCursorType.Cross); }
        cache[(t, alt)] = c;
        return c;
    }

    static Bitmap Render(TimelineTool t, bool alt)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(Size, Size));
        using var ctx = bmp.CreateDrawingContext();
        var at = new Point((Size - IconSize) / 2, (Size - IconSize) / 2);
        if (t == TimelineTool.Hand)
        {
            var box = new Rect(at, new Size(IconSize, IconSize));
            FxIcon.Draw(ctx, FxIcon.Hand, box, Brushes.Black, 4.5);
            FxIcon.Draw(ctx, FxIcon.Hand, box, Brushes.White, 2);
            return bmp;
        }
        var outline = new TextLayout(Glyph(t, alt), Icons, IconSize, Brushes.Black);
        var glyph = new TextLayout(Glyph(t, alt), Icons, IconSize, Brushes.White);
        // centered by its line box, which is taller than the glyph
        at = new Point((Size - glyph.Width) / 2, (Size - glyph.Height) / 2);
        // the outline: the glyph in black, a pixel off in every direction, under the white one
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) outline.Draw(ctx, at + new Point(dx * 1.2, dy * 1.2));
        glyph.Draw(ctx, at);
        return bmp;
    }
}
