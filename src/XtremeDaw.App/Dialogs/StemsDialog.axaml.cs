using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Separar en stems": how many and how good, with how long it will take here, since the best of them takes several
/// times the length of the audio. Same frame as <see cref="UnsavedDialog"/>. It opens on what was chosen last (the user's
/// pick the first time: 4 stems, Alta). An option whose models are missing offers to download just those. Enter separates, Escape or a click outside cancels.
/// </summary>
public sealed partial class StemsDialog : UserControl
{
    const string LayoutKey = "stemsLayout", QualityKey = "stemsQuality";

    TaskCompletionSource<StemSeparator.Settings?>? pending;
    double seconds;
    StemSeparator.Layout layout = StemSeparator.Layout.Four;
    StemSeparator.Quality quality = StemSeparator.Quality.High;

    public StemsDialog()
    {
        InitializeComponent();
        foreach (var (b, v) in new[] { (Two, StemSeparator.Layout.Two), (Four, StemSeparator.Layout.Four), (Six, StemSeparator.Layout.Six) })
            b.Click += (_, _) => { layout = v; Show(); };
        foreach (var (b, v) in new[] { (Fast, StemSeparator.Quality.Fast), (High, StemSeparator.Quality.High), (Max, StemSeparator.Quality.Max) })
            b.Click += (_, _) => { quality = v; Show(); };
        SeparateButton.Click += (_, _) => Go();
        CancelButton.Click += (_, _) => Answer(null);
        PointerPressed += (_, e) => { if (e.Source == this) Answer(null); };
        ComponentInstall.Changed += () => { if (IsVisible) Show(); };
    }

    /// <summary>Asks for a clip of <paramref name="length"/> seconds; null when cancelled.</summary>
    public Task<StemSeparator.Settings?> Ask(string clip, double length)
    {
        pending?.TrySetResult(null);
        pending = new();
        seconds = length;
        ClipName.Text = clip;
        try
        {
            if (Enum.TryParse(AppConfig.Get(LayoutKey), out StemSeparator.Layout l)) layout = l;
            if (Enum.TryParse(AppConfig.Get(QualityKey), out StemSeparator.Quality q)) quality = q;
        }
        catch { }
        Show();
        IsVisible = true;
        SeparateButton.Focus();
        return pending.Task;
    }

    void Show()
    {
        void Pick(ToggleButton b, bool on) => b.IsChecked = on;
        Pick(Two, layout == StemSeparator.Layout.Two);
        Pick(Four, layout == StemSeparator.Layout.Four);
        Pick(Six, layout == StemSeparator.Layout.Six);
        Pick(Fast, quality == StemSeparator.Quality.Fast);
        Pick(High, quality == StemSeparator.Quality.High);
        Pick(Max, quality == StemSeparator.Quality.Max);
        CountHelp.Text = Loc.T(layout switch
        {
            StemSeparator.Layout.Two => "stems.two.tip",
            StemSeparator.Layout.Six => "stems.six.tip",
            _ => "stems.four.tip",
        });
        bool available = StemSeparator.Available(layout, quality);
        var stems = Components.Stems;
        SeparateButton.IsEnabled = available || stems.CanInstall && ComponentInstall.Active == null;
        SeparateButton.Content = available ? Loc.T("stems.go") : Loc.T("component.install.size", ComponentInstall.Size(StemsComponent.For(layout, quality).Bytes));
        Estimate.Text = available
            ? Loc.T(StemSeparator.EstimateIsCeiling ? "stems.estimate.ceiling" : "stems.estimate", Time(StemSeparator.EstimateSec(layout, quality, seconds)))
            : Loc.T(ComponentInstall.Active == stems ? "component.state.installing" : stems.CanInstall ? "stems.option.missing" : "component.stems.unavailable");
    }

    static string Time(double s) => s < 90 ? $"{Math.Max(5, Math.Round(s / 5) * 5):0} s" : $"{Math.Ceiling(s / 60):0} min";

    void Go()
    {
        if (!StemSeparator.Available(layout, quality))
        {
            _ = ComponentInstall.Run(Components.Stems, StemsComponent.For(layout, quality));
            return;
        }
        try
        {
            AppConfig.Set(LayoutKey, layout.ToString());
            AppConfig.Set(QualityKey, quality.ToString());
        }
        catch { }
        Answer(new(layout, quality));
    }

    void Answer(StemSeparator.Settings? s)
    {
        IsVisible = false;
        var p = pending;
        pending = null;
        p?.TrySetResult(s);
    }

    /// <summary>Enter separates, Escape cancels; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) Answer(null);
        else if (e.Key is Key.Enter && SeparateButton.IsEnabled) Go();
        else if (e.Key is Key.Tab or Key.Space) return false;
        e.Handled = true;
        return true;
    }
}
