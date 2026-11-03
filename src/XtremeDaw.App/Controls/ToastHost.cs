using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Toasts (native only, asked by the user): small cards in the bottom right corner, over everything: every message to
/// the user goes here. The same text again while its card is up restarts that card instead of stacking a copy.
/// A plain toast goes away by itself after <see cref="Life"/> (the pointer over it holds it); a sticky one
/// stays until closed. At most <see cref="MaxShown"/> at once, newest
/// at the bottom. No shadow and no animation, like the rest of the window. Bigger text than the rest of the window
/// (asked by the user: the toasts were hard to read at a glance).
/// </summary>
public sealed class ToastHost : StackPanel
{
    static readonly TimeSpan Life = TimeSpan.FromSeconds(6);
    const int MaxShown = 3;
    /// <summary>How long slow work runs before its card appears.</summary>
    static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(300);

    public ToastHost()
    {
        Spacing = 10;
        Width = 420;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
    }

    /// <summary>
    /// Shows a toast (UI thread). <paramref name="title"/> goes above as a section label; <paramref name="action"/>
    /// adds a button under the text ("Mostrar" on a finished export) that runs it and closes the toast.
    /// Returns what closes it, for a sticky toast about something that ends by itself.
    /// </summary>
    public Action Show(string text, string? title = null, bool sticky = false, (string Label, Action Run)? action = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return () => { };
        if (!sticky && action == null && Children.OfType<Border>().FirstOrDefault(c => c.Tag is Action && Equals(c.DataContext, text)) is { Tag: Action same })
            same();
        Border? card = null;
        DispatcherTimer? timer = null;
        void Close()
        {
            timer?.Stop();
            Children.Remove(card!);
        }

        var body = new StackPanel { Spacing = 6 };
        if (title != null)
            body.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Palette.Accent });
        body.Children.Add(new TextBlock { Text = text, FontSize = 14, LineHeight = 20, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });
        if (action is var (label, run))
        {
            var act = new Button { Classes = { "chip" }, Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            act.Click += (_, _) => { Close(); run(); };
            body.Children.Add(act);
        }

        var close = new Button
        {
            Classes = { "ghost" }, Width = 28, Height = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 },
        };
        ToolTip.SetTip(close, Loc.T("common.close"));
        Avalonia.Automation.AutomationProperties.SetName(close, Loc.T("common.close"));
        close.Click += (_, _) => Close();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(body);
        Grid.SetColumn(close, 1);
        row.Children.Add(close);

        card = new Border
        {
            Padding = new Thickness(16, 14, 8, 14), CornerRadius = new CornerRadius(8),
            Background = Palette.Raised, BorderBrush = sticky ? Palette.Alpha(Palette.AccentColor, 0.45) : Palette.Line,
            BorderThickness = new Thickness(1), Child = row,
            Tag = sticky ? null : (Action)Close,
            // the text, so the same message again replaces its card
            DataContext = text,
        };
        if (!sticky)
        {
            timer = new DispatcherTimer { Interval = Life };
            timer.Tick += (_, _) => Close();
            card.PointerEntered += (_, _) => timer.Stop();
            card.PointerExited += (_, _) => timer.Start();
            timer.Start();
        }

        Children.Add(card);
        // the oldest plain toast makes room first; sticky ones only go when closed
        Trim(card);
        return Close;
    }

    /// <summary>
    /// A card with a progress bar for slow work (UI thread): it stays until <see cref="ToastProgress.Close"/>, can't be
    /// closed by hand because the work goes on anyway, and only shows after <see cref="ProgressDelay"/>, so what
    /// finishes at once doesn't flash on screen.
    /// </summary>
    public ToastProgress ShowProgress(string text)
    {
        var card = new ToastProgress(text, ProgressDelay, c => Children.Remove(c));
        Children.Add(card);
        Trim(card);
        return card;
    }

    void Trim(Control keep)
    {
        while (Children.Count > MaxShown && Children.OfType<Border>().FirstOrDefault(c => c != keep && c.Tag is Action) is { Tag: Action closeOld })
            closeOld();
    }
}

/// <summary>
/// The card of <see cref="ToastHost.ShowProgress"/>: the text, the percentage and a bar. <see cref="Report"/> can be
/// called from any thread as often as it likes; a timer takes the value to the screen.
/// </summary>
public sealed class ToastProgress : Border
{
    static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(100);
    const float Unknown = -1;

    readonly DispatcherTimer timer = new() { Interval = Refresh };
    readonly TextBlock label, percent;
    readonly Grid bar, top;
    readonly Action<ToastProgress> remove;
    volatile float value;
    float shown = -1;

    internal ToastProgress(string text, TimeSpan delay, Action<ToastProgress> remove)
    {
        this.remove = remove;
        label = new TextBlock { Text = text, FontSize = 14, LineHeight = 20, Foreground = Palette.Fg, TextTrimming = TextTrimming.CharacterEllipsis };
        percent = new TextBlock { Classes = { "dim", "mono" }, VerticalAlignment = VerticalAlignment.Center };
        top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
        top.Children.Add(label);
        Grid.SetColumn(percent, 1);
        top.Children.Add(percent);

        bar = new Grid { ColumnDefinitions = new ColumnDefinitions("0*,1*"), Height = 4 };
        bar.Children.Add(new Border { Background = Palette.Accent, CornerRadius = new CornerRadius(2) });
        var track = new Border { Background = Palette.Hover, CornerRadius = new CornerRadius(2), ClipToBounds = true, Child = bar };

        Padding = new Thickness(16, 14);
        CornerRadius = new CornerRadius(8);
        Background = Palette.Raised;
        BorderBrush = Palette.Line;
        BorderThickness = new Thickness(1);
        Child = new StackPanel { Spacing = 10, Children = { top, track } };
        IsVisible = false;

        var started = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            if (!IsVisible && DateTime.UtcNow - started >= delay) IsVisible = true;
            Update();
        };
        timer.Start();
        Update();
    }

    /// <summary>The fraction done, 0 to 1; below 0 when it can't be known, which hides the percentage.</summary>
    public void Report(double fraction) => value = fraction < 0 ? Unknown : (float)Math.Clamp(fraction, 0, 1);

    /// <summary>Adds a button that stops the work (UI thread).</summary>
    public void Cancelable(string text, Action run)
    {
        var b = new Button { Classes = { "btn", "sm" }, Content = text };
        b.Click += (_, _) => run();
        Grid.SetColumn(b, 2);
        top.Children.Add(b);
    }

    /// <summary>What is being worked on (UI thread).</summary>
    public string Text { set => label.Text = value; }

    /// <summary>Takes the card away (UI thread).</summary>
    public void Close()
    {
        timer.Stop();
        remove(this);
    }

    void Update()
    {
        float v = value;
        if (v == shown) return;
        shown = v;
        percent.Text = v == Unknown ? "" : $"{v:0%}";
        v = Math.Max(v, 0);
        bar.ColumnDefinitions[0].Width = new GridLength(v, GridUnitType.Star);
        bar.ColumnDefinitions[1].Width = new GridLength(1 - v, GridUnitType.Star);
    }
}
