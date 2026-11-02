using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Novedades": what changed in each version (<see cref="NewsGuide"/> has the contents). It opens by itself the first
/// time a new version runs, and from the title bar, the project manager and the help. The newest version comes first,
/// with the older ones a click away; each item can lead to its help topic.
/// </summary>
public sealed partial class NewsDialog : UserControl
{
    const double BadgeSize = 34;

    public event Action? Dismissed;

    /// <summary>"Ver en la ayuda" of an item; this window has closed already.</summary>
    public event Action<string>? TopicRequested;

    public NewsDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => HideDialog();
        OkButton.Click += (_, _) => HideDialog();
        PointerPressed += (_, e) => { if (e.Source == this) HideDialog(); };
    }

    public void Show()
    {
        NewsGuide.MarkSeen();
        if (NewsGuide.Latest is not { } latest) return;
        ShowRelease(latest);
        IsVisible = true;
        OkButton.Focus();
    }

    public void HideDialog()
    {
        IsVisible = false;
        Dismissed?.Invoke();
    }

    /// <summary>Escape closes; Tab and Enter work its own buttons; nothing else leaves it.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        // what asked for the focus while this opened (the components notice, below) gets no key
        if (e.Key is Key.Tab or Key.Enter or Key.Space && e.Source is Visual v && this.IsVisualAncestorOf(v)) return false;
        if (e.Key == Key.Escape) HideDialog();
        e.Handled = true;
        return true;
    }

    void ShowRelease(NewsGuide.Release release)
    {
        Versions.Children.Clear();
        foreach (var r in NewsGuide.Releases)
        {
            var chip = new Button { Classes = { "chip" }, Content = Loc.T("news.version", r.Version), Margin = new Thickness(0, 0, 8, 8) };
            chip.Classes.Set("on", r == release);
            chip.Click += (_, _) => ShowRelease(r);
            Versions.Children.Add(chip);
        }
        List.Children.Clear();
        foreach (var item in release.Items) List.Children.Add(Row(item));
        Scroller.Offset = default;
    }

    Border Row(NewsGuide.Item item)
    {
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(new TextBlock { Text = item.Title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(HelpDialog.Rich(item.Text, Palette.Dim, 12, 18));
        if (item.Topic is { } topic && HelpGuide.Find(topic) != null)
        {
            var link = new Button { Classes = { "link" }, Content = Loc.T("news.seeHelp"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            link.Click += (_, _) => { HideDialog(); TopicRequested?.Invoke(topic); };
            body.Children.Add(link);
        }
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        grid.Children.Add(HelpDialog.Badge(HelpGuide.Glyph(item.Icon), BadgeSize));
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = grid };
    }
}
