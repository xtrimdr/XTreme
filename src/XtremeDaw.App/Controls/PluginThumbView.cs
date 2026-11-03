using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// A plugin's picture (<see cref="PluginThumbs"/>) in the window: a small one for the rows of Configuración → Plugins,
/// kept decoded because the list is rebuilt on every keystroke of its search, and a large one in a tooltip, in the rows
/// and in the plugin menus, read only while it is shown: a menu has too little room to tell plugins apart by a small one.
/// </summary>
public static class PluginThumbView
{
    const double RowW = 64, RowH = 40, TipW = 320;
    /// <summary>Twice the row's width, for screens at 200 %.</summary>
    const int RowDecodeW = 128;
    static readonly TimeSpan TipDelay = TimeSpan.FromMilliseconds(250);
    static readonly Dictionary<string, (DateTime Stamp, Bitmap? Picture)> rows = [];

    static Bitmap? Read(string file, int width)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    static Bitmap? RowPicture(string file)
    {
        var stamp = File.GetLastWriteTimeUtc(file);
        if (rows.TryGetValue(file, out var known) && known.Stamp == stamp) return known.Picture;
        var picture = Read(file, RowDecodeW);
        rows[file] = (stamp, picture);
        return picture;
    }

    /// <summary>The picture for a row, or an empty place of the same size, so the names line up.</summary>
    public static Control Row(VstPluginInfo p)
    {
        var box = new Border { Width = RowW, Height = RowH, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Palette.Raised };
        if (PluginThumbs.Find(p.Path, p.ClassId) is { } file && RowPicture(file) is { } picture)
        {
            box.Child = new Image { Source = picture, Stretch = Stretch.UniformToFill };
            AddTip(box, p);
        }
        return box;
    }

    /// <summary>Shows the large picture over <paramref name="target"/>, if the plugin has one; false if not.</summary>
    public static bool AddTip(Control target, VstPluginInfo p)
    {
        if (PluginThumbs.Find(p.Path, p.ClassId) is not { } file) return false;
        var image = new Image { Width = TipW, Stretch = Stretch.Uniform };
        image.AttachedToVisualTree += (_, _) => image.Source = Read(file, (int)TipW * 2);
        image.DetachedFromVisualTree += (_, _) =>
        {
            (image.Source as Bitmap)?.Dispose();
            image.Source = null;
        };
        ToolTip.SetTip(target, image);
        ToolTip.SetPlacement(target, PlacementMode.Right);
        ToolTip.SetShowDelay(target, (int)TipDelay.TotalMilliseconds);
        return true;
    }
}
