using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// Images in the bench's tickets (asked by the user, #176: to show a problem better). A screenshot pasted with Ctrl+V or
/// image files dragged onto the window wait under the box they go with (the new request's, or the open ticket's reply),
/// each with its ×, and leave with the message. In the conversation they show as thumbnails; a click shows one whole over
/// the bench, and a click or Esc closes it. Thumbnails are decoded small and kept while the window lives, because the
/// ticket is drawn again every time the list changes.
/// </summary>
public sealed partial class TestListWindow
{
    /// <summary>How tall a thumbnail shows, the widest it gets, and the height it is decoded at (twice, for sharp screens).</summary>
    const double ThumbHeight = 72, ThumbMaxWidth = 180;
    const int ThumbDecode = 144;

    /// <summary>An image waiting to be sent, with its thumbnail.</summary>
    sealed record Pending(TestImage Image, Bitmap? Thumb);

    readonly List<Pending> requestImages = [];
    readonly List<Pending> replyImages = [];
    /// <summary>The ticket <see cref="replyImages"/> belong to: opening another one drops them.</summary>
    int replyImagesFor;
    readonly WrapPanel requestStrip = new() { ItemSpacing = 6, LineSpacing = 6, IsVisible = false };
    WrapPanel? replyStrip;
    /// <summary>The thumbnails of what was said, by file; null = it could not be read.</summary>
    readonly Dictionary<string, Bitmap?> thumbs = [];
    readonly Image viewerImage = new() { Stretch = Stretch.Uniform };
    readonly Border viewer = new() { IsVisible = false, Padding = new(24), Cursor = new Cursor(StandardCursorType.Hand) };

    /// <summary>Where a pasted or dropped image goes.</summary>
    enum ImageTarget { None, Request, Reply }

    void InitImages()
    {
        if (RequestBox.Parent is Panel box) box.Children.Insert(box.Children.IndexOf(RequestBox) + 1, requestStrip);
        viewer.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0));
        viewer.Child = viewerImage;
        ToolTip.SetTip(viewer, Loc.T("test.list.image.close"));
        viewer.PointerPressed += (_, e) =>
        {
            CloseImage();
            e.Handled = true;
        };
        if (Content is Grid root)
        {
            Grid.SetRowSpan(viewer, Math.Max(1, root.RowDefinitions.Count));
            root.Children.Add(viewer);
        }
        // Tunnel: the boxes would take Ctrl+V as their own; the text still pastes, the image is added beside it
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (viewer.IsVisible && e.Key == Key.Escape)
            {
                CloseImage();
                e.Handled = true;
            }
            else if ((e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control) || (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift))
                PasteImages();
        }, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = Target() != ImageTarget.None && (DroppedFiles(e).Count > 0 || e.DataTransfer.Contains(DataFormat.Bitmap))
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            var target = Target();
            if (target == ImageTarget.None) return;
            var found = DroppedFiles(e).Select(ReadImage).OfType<TestImage>().ToList();
            if (found.Count == 0 && e.DataTransfer.TryGetBitmap() is { } bitmap) found.Add(Png(bitmap));
            Attach(target, found);
        });
        Closed += (_, _) =>
        {
            CloseImage();
            foreach (var t in thumbs.Values) t?.Dispose();
            thumbs.Clear();
            Drop(requestImages);
            Drop(replyImages);
        };
    }

    /// <summary>The composer while it writes a new request and has the keyboard (or no ticket is open), else the open ticket's reply.</summary>
    ImageTarget Target()
    {
        bool composing = Composer.IsEffectivelyVisible && !skillMode;
        bool replying = replyBox is { IsEffectivelyVisible: true } && replyItem != null;
        if (composing && (RequestBox.IsFocused || !replying)) return ImageTarget.Request;
        return replying ? ImageTarget.Reply : ImageTarget.None;
    }

    async void PasteImages()
    {
        var target = Target();
        int ticket = replyItem?.Id ?? 0;
        if (target == ImageTarget.None || Clipboard is not { } clip) return;
        var found = new List<TestImage>();
        try
        {
            foreach (var f in await clip.TryGetFilesAsync() ?? [])
                if (f.TryGetLocalPath() is { } file && IsImageFile(file) && ReadImage(file) is { } img) found.Add(img);
            if (found.Count == 0 && await clip.TryGetBitmapAsync() is { } bitmap)
                using (bitmap) found.Add(Png(bitmap));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            ErrorText.Text = Loc.T("test.list.image.error", e.Message);
            return;
        }
        // the ticket changed while the clipboard answered: the image would land in the wrong one
        if (target == ImageTarget.Reply && replyItem?.Id != ticket) return;
        Attach(target, found);
    }

    static TestImage Png(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return new(ms.ToArray(), ".png");
    }

    static bool IsImageFile(string file) => TestList.ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());

    static List<string> DroppedFiles(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().Where(f => IsImageFile(f) && File.Exists(f)).ToList();

    /// <summary>An image file's bytes; null (and it says why) when it is too big or cannot be read.</summary>
    TestImage? ReadImage(string file)
    {
        try
        {
            if (new FileInfo(file).Length > TestList.MaxImageBytes)
            {
                ErrorText.Text = Loc.T("test.list.image.big", Path.GetFileName(file), TestList.MaxImageBytes / (1024 * 1024));
                return null;
            }
            return new(File.ReadAllBytes(file), Path.GetExtension(file).ToLowerInvariant());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = Loc.T("test.list.image.error", e.Message);
            return null;
        }
    }

    void Attach(ImageTarget target, List<TestImage> found)
    {
        if (found.Count == 0) return;
        if (target == ImageTarget.Reply && replyItem is { } item && replyImagesFor != item.Id)
        {
            Drop(replyImages);
            replyImagesFor = item.Id;
        }
        var list = target == ImageTarget.Request ? requestImages : replyImages;
        foreach (var img in found) list.Add(new(img, Decode(() => new MemoryStream(img.Data))));
        RefreshStrips();
    }

    void RefreshStrips()
    {
        FillStrip(requestStrip, requestImages);
        if (replyStrip != null) FillStrip(replyStrip, replyImages);
    }

    /// <summary>The images waiting under a box, each with its × to take it out.</summary>
    void FillStrip(WrapPanel strip, List<Pending> list)
    {
        strip.Children.Clear();
        foreach (var p in list)
        {
            var cell = new Grid();
            var thumb = Thumb(p.Thumb, () => ShowImage(() => new MemoryStream(p.Image.Data)));
            cell.Children.Add(thumb);
            var remove = new Button
            {
                Classes = { "act" }, Width = 20, Height = 20, MinHeight = 20, Padding = new(0), Margin = new(0, 3, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = Palette.Panel,
                Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            ToolTip.SetTip(remove, Loc.T("test.list.image.remove"));
            remove.Click += (_, _) =>
            {
                list.Remove(p);
                p.Thumb?.Dispose();
                RefreshStrips();
            };
            cell.Children.Add(remove);
            strip.Children.Add(cell);
        }
        strip.IsVisible = list.Count > 0;
    }

    /// <summary>The reply's waiting images, under its box: the ones of this ticket, or none (another ticket's are dropped).</summary>
    Control ReplyStrip(TestItem item)
    {
        if (replyImagesFor != item.Id)
        {
            Drop(replyImages);
            replyImagesFor = item.Id;
        }
        replyStrip = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        FillStrip(replyStrip, replyImages);
        return replyStrip;
    }

    /// <summary>What the reply sends with it, taken out of the waiting list.</summary>
    List<TestImage> TakeReplyImages(int id)
    {
        if (replyImagesFor != id) return [];
        var list = replyImages.Select(p => p.Image).ToList();
        Drop(replyImages);
        if (replyStrip != null) FillStrip(replyStrip, replyImages);
        return list;
    }

    List<TestImage> TakeRequestImages()
    {
        var list = requestImages.Select(p => p.Image).ToList();
        DropRequestImages();
        return list;
    }

    void DropRequestImages()
    {
        Drop(requestImages);
        FillStrip(requestStrip, requestImages);
    }

    static void Drop(List<Pending> list)
    {
        foreach (var p in list) p.Thumb?.Dispose();
        list.Clear();
    }

    /// <summary>A message's images, as thumbnails under its text.</summary>
    Control MessageImages(List<string> images)
    {
        var row = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Margin = new(0, 3, 0, 0) };
        foreach (var image in images)
        {
            string file = TestList.ImagePath(path, image);
            if (!thumbs.TryGetValue(file, out var bitmap)) thumbs[file] = bitmap = File.Exists(file) ? Decode(() => File.OpenRead(file)) : null;
            row.Children.Add(Thumb(bitmap, () => ShowImage(() => File.OpenRead(file))));
        }
        return row;
    }

    /// <summary>A thumbnail that shows the image whole on a click; one that cannot be read says so.</summary>
    Control Thumb(Bitmap? bitmap, Action show)
    {
        var frame = new Border
        {
            CornerRadius = new(6), ClipToBounds = true, BorderBrush = Palette.Line, BorderThickness = new(1), Background = Palette.Panel,
            Height = ThumbHeight, MaxWidth = ThumbMaxWidth, MinWidth = ThumbHeight,
        };
        if (bitmap == null)
        {
            frame.Child = new TextBlock
            {
                Text = Loc.T("test.list.image.missing"), Classes = { "dim" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(6),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return frame;
        }
        frame.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        frame.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(frame, Loc.T("test.list.image.open"));
        frame.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed) return;
            show();
            e.Handled = true;
        };
        return frame;
    }

    static Bitmap? Decode(Func<Stream> open)
    {
        try
        {
            using var s = open();
            return Bitmap.DecodeToHeight(s, ThumbDecode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>One image whole, over the bench.</summary>
    void ShowImage(Func<Stream> open)
    {
        try
        {
            Bitmap bitmap;
            using (var s = open()) bitmap = new Bitmap(s);
            CloseImage();
            viewerImage.Source = bitmap;
            viewer.IsVisible = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ErrorText.Text = Loc.T("test.list.image.error", e.Message);
        }
    }

    void CloseImage()
    {
        viewer.IsVisible = false;
        (viewerImage.Source as IDisposable)?.Dispose();
        viewerImage.Source = null;
    }
}
