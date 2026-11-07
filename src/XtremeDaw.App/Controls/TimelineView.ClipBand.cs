using Avalonia;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Where the labels of a clip go (asked by the user, #240: every new icon ended up covering another). Along a clip's
/// top runs one strip, <see cref="ClipBand"/>: every icon, tab and pill asks it for room (<see cref="ClipBand.TakeLeft"/>
/// from the start, <see cref="ClipBand.TakeRight"/> from the end), in order of importance, and the name takes what is
/// left between them. What doesn't fit is left out, never drawn over another. The strip keeps clear of the edge tabs,
/// the fade lines at the top and the loop corner, so nothing that is grabbed covers a label either.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>
    /// The label strip of one clip: <see cref="H"/> px high, <see cref="Below"/> px under the clip's top, from
    /// <see cref="Left"/> to <see cref="Right"/>. Each take moves one end inward, so two labels can never meet.
    /// </summary>
    struct ClipBand
    {
        /// <summary>Px from the clip's top to the strip, and its height (a tab's).</summary>
        public const double Below = 4, H = 16;
        /// <summary>Px between two labels.</summary>
        public const double Gap = 4;
        /// <summary>Lowest clip that has a strip: under it the labels would cover the content.</summary>
        public const double MinClipH = 24;

        double left, right;
        public readonly double Top;
        public readonly bool Shown;

        public ClipBand(Rect clip, double left, double right)
        {
            Top = clip.Top + Below;
            Shown = clip.Height >= MinClipH;
            this.left = left;
            this.right = right;
        }

        public readonly double Left => left;
        public readonly double Right => right;
        public readonly double CenterY => Top + H / 2;

        /// <summary>Takes <paramref name="w"/> px at the start, or nothing (false) when they don't fit.</summary>
        public bool TakeLeft(double w, out Rect r)
        {
            r = default;
            if (!Shown || left + w > right) return false;
            r = new Rect(left, Top, w, H);
            left += w + Gap;
            return true;
        }

        /// <summary>Takes <paramref name="w"/> px at the end, or nothing (false) when they don't fit.</summary>
        public bool TakeRight(double w, out Rect r)
        {
            r = default;
            if (!Shown || right - w < left) return false;
            r = new Rect(right - w, Top, w, H);
            right -= w + Gap;
            return true;
        }
    }

    /// <summary>Room kept at a clip's end for its loop corner (<see cref="DrawLoopCorner"/>), drawn or not, so the tabs never move on hover.</summary>
    const double ClipCornerRoom = LoopGrab + 2;

    /// <summary>
    /// The label strip of a clip spanning <paramref name="x0"/>..<paramref name="x1"/>: kept in view, past the edge tab
    /// and the end of its fade in at the start, short of its fade out and its loop corner at the end.
    /// </summary>
    ClipBand BandOf(Rect clip, double x0, double x1, double fadeInX, double fadeOutX, double W)
    {
        double left = Math.Max(Math.Max(x0, HeaderW) + ClipLabelInset, fadeInX > x0 ? fadeInX + ClipBand.Gap : 0);
        double right = Math.Min(Math.Min(x1, W) - ClipCornerRoom, fadeOutX < x1 ? fadeOutX - ClipBand.Gap : double.MaxValue);
        return new ClipBand(clip, left, right);
    }

    /// <summary>Which part of a clip a call draws: a frozen track draws its clips dimmed, its render's wave over them, and then the labels at full strength.</summary>
    [Flags]
    enum ClipLayer { Body = 1, Labels = 2, All = Body | Labels }

    /// <summary>Width of the snowflake chip on a frozen track's clips, and of a loop icon in the strip.</summary>
    const double FrozenChipW = 16, LoopGlyphW = 12;

    /// <summary>The snowflake on a frozen track's clip (asked by the user, #240): on a chip of the accent, first in the strip.</summary>
    static void DrawFrozenChip(DrawingContext ctx, Rect r)
    {
        ctx.DrawRectangle(Palette.Accent, null, r, 4, 4);
        Snowflake.Draw(ctx, r.Center, r.Height - 5, Palette.App);
    }
}
