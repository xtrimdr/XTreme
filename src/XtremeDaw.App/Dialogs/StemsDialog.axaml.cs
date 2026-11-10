using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>What the user chose in <see cref="StemsDialog"/>: how to separate, whether only the timeline's selection, and what happens with the original.</summary>
public sealed record StemsChoice(StemSeparator.Settings Settings, bool OnlyRange, StemOriginal Original);

/// <summary>
/// "Separar en stems": how many and how good, with how long it will take here, since the best of them takes several
/// times the length of the audio. Same frame as <see cref="UnsavedDialog"/>. It opens on what was chosen last (the user's
/// pick the first time: 4 stems, Alta). An option whose models are missing offers to download just those. Enter separates, Escape or a click outside cancels.
/// Avanzado (asked by the user, #146) trades count and quality for the model, which stems, how many passes, the
/// timeline's selection instead of the whole clip, and whether the original is muted, left or taken out; Simple runs as
/// always (whole clip, muted). Both remember their choices apart.
/// </summary>
public sealed partial class StemsDialog : UserControl
{
    const string LayoutKey = "stemsLayout", QualityKey = "stemsQuality", ModeKey = "stemsAdvanced", ModelKey = "stemsModel",
        OnlyKey = "stemsOnly", PassesKey = "stemsPasses", RangeKey = "stemsRange", OriginalKey = "stemsOriginal";
    /// <summary>The passes offered: each one doubles the cost of the one before.</summary>
    static readonly int[] PassChoices = [1, 2, 4, StemSeparator.MaxShifts];

    TaskCompletionSource<StemsChoice?>? pending;
    double seconds;
    double? rangeSeconds;
    StemSeparator.Layout layout = StemSeparator.Layout.Four;
    StemSeparator.Quality quality = StemSeparator.Quality.High;
    bool advanced;
    StemSeparator.Model model = StemSeparator.Model.Tuned;
    HashSet<string> only = ["vocals", "drums", "bass", "guitar", "piano"];
    int passes = 1;
    bool useRange;
    StemOriginal original = StemOriginal.Mute;

    public StemsDialog()
    {
        InitializeComponent();
        foreach (var (b, v) in new[] { (Two, StemSeparator.Layout.Two), (Four, StemSeparator.Layout.Four), (Six, StemSeparator.Layout.Six) })
            b.Click += (_, _) => { layout = v; Show(); };
        foreach (var (b, v) in new[] { (Fast, StemSeparator.Quality.Fast), (High, StemSeparator.Quality.High), (Max, StemSeparator.Quality.Max) })
            b.Click += (_, _) => { quality = v; Show(); };
        SimpleMode.Click += (_, _) => { advanced = false; Show(); };
        AdvancedMode.Click += (_, _) => { advanced = true; Show(); };
        foreach (var (b, v) in new[] { (ModelGeneral, StemSeparator.Model.General), (ModelTuned, StemSeparator.Model.Tuned), (ModelSix, StemSeparator.Model.Six) })
            b.Click += (_, _) => { model = v; Show(); };
        foreach (int n in PassChoices)
        {
            var b = new ToggleButton { Content = $"{n}", Tag = n };
            b.Classes.Add("seg");
            b.Click += (_, _) => { passes = n; Show(); };
            Passes.Children.Add(b);
        }
        WholeClip.Click += (_, _) => { useRange = false; Show(); };
        OnlyRange.Click += (_, _) => { useRange = true; Show(); };
        foreach (var (b, v) in new[] { (OriginalMute, StemOriginal.Mute), (OriginalKeep, StemOriginal.Keep), (OriginalRemove, StemOriginal.Remove) })
            b.Click += (_, _) => { original = v; Show(); };
        SeparateButton.Click += (_, _) => Go();
        CancelButton.Click += (_, _) => Answer(null);
        PointerPressed += (_, e) => { if (e.Source == this) Answer(null); };
        ComponentInstall.Changed += () => { if (IsVisible) Show(); };
    }

    /// <summary>
    /// Asks for a clip of <paramref name="length"/> seconds; <paramref name="range"/> is how much of it the timeline's
    /// selection covers, if it covers any. Null when cancelled.
    /// </summary>
    public Task<StemsChoice?> Ask(string clip, double length, double? range = null)
    {
        pending?.TrySetResult(null);
        pending = new();
        seconds = length;
        rangeSeconds = range;
        ClipName.Text = clip;
        try
        {
            if (Enum.TryParse(AppConfig.Get(LayoutKey), out StemSeparator.Layout l)) layout = l;
            if (Enum.TryParse(AppConfig.Get(QualityKey), out StemSeparator.Quality q)) quality = q;
            advanced = AppConfig.Get(ModeKey) == "true";
            if (Enum.TryParse(AppConfig.Get(ModelKey), out StemSeparator.Model m) && m != StemSeparator.Model.Auto) model = m;
            if (AppConfig.Get(OnlyKey) is { Length: > 0 } ids) only = [.. ids.Split(',')];
            if (int.TryParse(AppConfig.Get(PassesKey), out int n) && PassChoices.Contains(n)) passes = n;
            useRange = AppConfig.Get(RangeKey) == "true";
            if (Enum.TryParse(AppConfig.Get(OriginalKey), out StemOriginal o)) original = o;
        }
        catch { }
        Show();
        IsVisible = true;
        SeparateButton.Focus();
        return pending.Task;
    }

    /// <summary>The stems of the model that are ticked.</summary>
    List<string> Picked() => StemSeparator.StemsOf(model).Where(only.Contains).ToList();

    bool RangeOn => useRange && rangeSeconds > 0;

    StemOriginal OriginalNow => RangeOn && original == StemOriginal.Remove ? StemOriginal.Mute : original;

    StemSeparator.Settings Current() => advanced
        ? new(Shifts: passes, Model: model, Only: Picked())
        : new(layout, quality);

    void Show()
    {
        static void Pick(ToggleButton b, bool on) => b.IsChecked = on;
        Pick(SimpleMode, !advanced);
        Pick(AdvancedMode, advanced);
        SimplePanel.IsVisible = !advanced;
        AdvancedPanel.IsVisible = advanced;
        Card.Width = advanced ? 512 : 440;

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

        Pick(ModelGeneral, model == StemSeparator.Model.General);
        Pick(ModelTuned, model == StemSeparator.Model.Tuned);
        Pick(ModelSix, model == StemSeparator.Model.Six);
        ModelHelp.Text = Loc.T(model switch
        {
            StemSeparator.Model.General => "stems.model.general.tip",
            StemSeparator.Model.Six => "stems.model.six.tip",
            _ => "stems.model.tuned.tip",
        });
        if (advanced) FillChecks();
        foreach (var b in Passes.Children.OfType<ToggleButton>()) Pick(b, b.Tag is int n && n == passes);
        bool hasRange = rangeSeconds > 0;
        OnlyRange.IsEnabled = hasRange;
        Pick(WholeClip, !RangeOn);
        Pick(OnlyRange, RangeOn);
        RangeHelp.Text = hasRange ? Loc.T("stems.range.length", Time(rangeSeconds!.Value)) : Loc.T("stems.range.none");
        OriginalRemove.IsEnabled = !RangeOn;
        ToolTip.SetTip(OriginalRemove, RangeOn ? Loc.T("stems.original.remove.range") : null);
        Pick(OriginalMute, OriginalNow == StemOriginal.Mute);
        Pick(OriginalKeep, OriginalNow == StemOriginal.Keep);
        Pick(OriginalRemove, OriginalNow == StemOriginal.Remove);

        var settings = Current();
        bool none = advanced && Picked().Count == 0;
        bool available = StemSeparator.Available(settings);
        var stems = Components.Stems;
        SeparateButton.IsEnabled = !none && (available || stems.CanInstall && ComponentInstall.Active == null);
        SeparateButton.Content = available || none ? Loc.T("stems.go") : Loc.T("component.install.size", ComponentInstall.Size(StemsComponent.For(settings).Bytes));
        double length = advanced && RangeOn ? rangeSeconds!.Value : seconds;
        Estimate.Text = none ? Loc.T("stems.which.none")
            : available
            ? Loc.T(StemSeparator.EstimateIsCeiling ? "stems.estimate.ceiling" : "stems.estimate", Time(StemSeparator.EstimateSec(settings, length)))
            : Loc.T(ComponentInstall.Active == stems ? "component.state.installing" : stems.CanInstall ? "stems.option.missing" : "component.stems.unavailable");
    }

    /// <summary>A box per stem of the model; rebuilt only when the model changes what it gives.</summary>
    void FillChecks()
    {
        var ids = StemSeparator.StemsOf(model);
        if (!StemChecks.Children.Select(c => c.Tag as string).SequenceEqual(ids))
        {
            StemChecks.Children.Clear();
            foreach (string id in ids)
            {
                var box = new CheckBox { Content = Loc.T("stems." + id), Tag = id, Margin = new(0, 0, 16, 0) };
                box.IsCheckedChanged += (_, _) =>
                {
                    if (filling) return;
                    if (box.IsChecked == true) only.Add(id);
                    else only.Remove(id);
                    Show();
                };
                StemChecks.Children.Add(box);
            }
        }
        filling = true;
        foreach (var box in StemChecks.Children.OfType<CheckBox>())
            box.IsChecked = only.Contains((string)box.Tag!);
        filling = false;
    }

    /// <summary>The boxes are being set from <see cref="only"/>, not by the user.</summary>
    bool filling;

    static string Time(double s) => s < 90 ? $"{Math.Max(5, Math.Round(s / 5) * 5):0} s" : $"{Math.Ceiling(s / 60):0} min";

    void Go()
    {
        var settings = Current();
        if (advanced && Picked().Count == 0) return;
        if (!StemSeparator.Available(settings))
        {
            _ = ComponentInstall.Run(Components.Stems, StemsComponent.For(settings));
            return;
        }
        try
        {
            AppConfig.Set(ModeKey, advanced ? "true" : "false");
            if (advanced)
            {
                AppConfig.Set(ModelKey, model.ToString());
                AppConfig.Set(OnlyKey, string.Join(",", only));
                AppConfig.Set(PassesKey, passes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (rangeSeconds > 0) AppConfig.Set(RangeKey, useRange ? "true" : "false");
                AppConfig.Set(OriginalKey, original.ToString());
            }
            else
            {
                AppConfig.Set(LayoutKey, layout.ToString());
                AppConfig.Set(QualityKey, quality.ToString());
            }
        }
        catch { }
        Answer(advanced ? new(settings, RangeOn, OriginalNow) : new(settings, false, StemOriginal.Mute));
    }

    void Answer(StemsChoice? s)
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
        // Tab moves the focus and Space presses the focused button: kept from the window's shortcuts (Space would play)
        else if (e.Key is Key.Tab or Key.Space) return true;
        e.Handled = true;
        return true;
    }
}
