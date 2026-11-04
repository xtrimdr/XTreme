using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>Port of TakeInspector.tsx inside App.tsx's left sidebar ("Canal", folds to a column of icons and remembers
/// it like gc.sidebarCollapsed). Track effects edit the channel; sync, fades and framing edit the selected native clip.
/// The EQ draws the selected track's live spectrum (<see cref="SpectrumTap"/>, FFT on the UI thread). Native
/// differences: multiple clips per track, offline status; Avalonia Fluent controls and Segoe icons follow the
/// platform. Media corrections remain immutable.</summary>
public partial class MainWindow
{
    readonly List<Action> inspectorRefresh = [];
    /// <summary>The channel's sections (Mezcla, effects, sends, Transponer): shown for an empty track too.</summary>
    readonly StackPanel takeSections = new() { Spacing = 0 };
    /// <summary>The selected clip's sections (Sincronización, Clip, Encuadre) and their refreshes: only with a clip.</summary>
    readonly StackPanel clipSections = new() { Spacing = 0 };
    readonly List<Action> clipRefresh = [];
    readonly StackPanel emptySections = new() { Spacing = 8 };
    readonly StackPanel noSelection = new() { Margin = new Thickness(10, 32), Spacing = 4 };
    readonly TextBlock noSelectionHint = new() { FontSize = 12, Foreground = Palette.Faint, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly Grid takeHeader = new() { ColumnDefinitions = new ColumnDefinitions("24,*,Auto") };
    readonly TrackBadge takeBadge = new() { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox takeName = new() { Classes = { "title" } };
    readonly TextBlock takeNamePencil = new() { Classes = { "icon" }, Text = "\uE70F", FontSize = 11, Foreground = Palette.Faint, Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
    readonly Border takeHead = new() { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(-14, 0, -14, -12), Padding = new Thickness(14, 0, 14, 12) };
    readonly TextBlock takeInfo = new() { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 6, 0, 0) };
    readonly WrapPanel takeFacts = new() { Margin = new Thickness(34, 6, 0, 0) };
    readonly TextBlock takeLength = new(), takeFormat = new(), takeChannels = new();
    readonly TextBlock GrText = new() { Classes = { "dim", "mono" }, FontSize = 11 };
    readonly Border GrBar = new() { Width = 0, HorizontalAlignment = HorizontalAlignment.Right, Background = Palette.Accent, CornerRadius = new CornerRadius(2) };
    readonly Border GrTrack = new() { Height = 6, Background = Palette.App, CornerRadius = new CornerRadius(2) };
    string? inspectorTrack;
    readonly Dictionary<string, (StackPanel Body, Control? Action, SectionHeader Header)> inspectorSections = [];
    readonly HashSet<string> collapsedSections = [];
    Button? collapseAll;
    readonly PathIcon collapseAllIcon = new() { Width = 14, Height = 14 };
    Task inspectorPrefsSaving = Task.CompletedTask;
    const double InspectorCollapsedWidth = 44;
    readonly DockPanel inspectorExpanded = new();
    readonly StackPanel inspectorCollapsed = new() { Spacing = 4, Margin = new Thickness(0, 8), HorizontalAlignment = HorizontalAlignment.Center };
    readonly SpectrumTap spectrumTap = new();
    readonly float[] spectrumBins = new float[SpectrumTap.Bins];
    LiveTrack? spectrumTrack;
    long spectrumFrom;

    bool IsCollapsed(string title) => collapsedSections.Contains(title);

    void SetSectionCollapsed(string title, bool hide)
    {
        if (hide) collapsedSections.Add(title); else collapsedSections.Remove(title);
        if (inspectorSections.TryGetValue(title, out var section))
        {
            section.Body.IsVisible = !hide;
            if (section.Action != null) section.Action.IsVisible = !hide;
            section.Header.SetCollapsed(hide);
        }
    }
    void SaveCollapsedSections()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(collapsedSections);
        inspectorPrefsSaving = inspectorPrefsSaving.ContinueWith(_ =>
        {
            try { AppConfig.Set("collapsed.inspector", json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* View preference only. */ }
        }, TaskScheduler.Default);
        UpdateCollapseButton();
    }
    /// <summary>TakeInspector.tsx's CollapseFxButton (ChevronsDownUp / ChevronsUpDown), but it folds every section of
    /// the take, not only the effects: the user asked for "colapsar todo".</summary>
    void UpdateCollapseButton()
    {
        if (collapseAll == null) return;
        bool all = FoldableSections.All(IsCollapsed);
        ToolTip.SetTip(collapseAll, all ? Loc.T("insp.sections.show") : Loc.T("insp.sections.hide"));
        collapseAllIcon.Data = (Geometry)Application.Current!.FindResource(all ? "ExpandAllIcon" : "CollapseAllIcon")!;
    }

    /// <summary>The sections "plegar todo" folds.</summary>
    IEnumerable<string> FoldableSections => inspectorSections.Keys;

    /// <summary>A quiet chip under the take name (duration, format…): Hover fill, Dim mono text.</summary>
    static Border FactBadge(string glyph, TextBlock text)
    {
        text.Classes.Add("mono"); text.FontSize = 11; text.Foreground = Palette.Dim; text.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (glyph != "") row.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 10, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(text);
        return new Border { Background = Palette.Hover, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 4, 4), Child = row };
    }

    /// <summary>App.tsx's left aside: the "Canal" header with Loc.T("insp.hide"), or the folded column of icons.</summary>
    void InitInspectorBar()
    {
        var scroll = (Control)InspectorBar.Child!;
        InspectorBar.Child = null;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), Height = 44, Margin = new Thickness(16, 0, 6, 0) };
        header.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 14, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var title = new TextBlock { Text = Loc.T("insp.title"), FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        var hide = inspectorHide = RailButton("", Loc.T("insp.hide"), () => SetInspectorCollapsed(true));
        shortcutTips.Add((hide, Loc.T("insp.hide"), "leftPanel"));
        hide.Foreground = Palette.Faint;
        var floatButton = FloatButton(FloatInspector, chip: false);
        var panes = InspectorPaneButtons();
        Grid.SetColumn(panes, 2); header.Children.Add(panes);
        Grid.SetColumn(hide, 3); header.Children.Add(hide);
        Grid.SetColumn(floatButton, 4); header.Children.Add(floatButton);
        var top = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        DockPanel.SetDock(top, Dock.Top);
        inspectorExpanded.Children.Add(top);
        inspectorExpanded.Children.Add(InspectorBody((ScrollViewer)scroll));
        foreach (var (glyph, tip) in new[] { ("", Loc.T("insp.rail.show")), ("", Loc.T("insp.title")) })
        {
            var b = RailButton(glyph, tip, () => SetInspectorCollapsed(false));
            shortcutTips.Add((b, tip, "leftPanel"));
            b.Foreground = Palette.Faint; inspectorCollapsed.Children.Add(b);
        }
        InitInspectorRail();
        inspectorWidth = SavedSidebarWidth("sidebarWidth");
        inspectorGrip = SidebarGrip(true, () => inspectorWidth, () => ShownWidth(ProjectBar), SetInspectorWidth, "sidebarWidth");
        InspectorBar.Child = new Panel { Children = { inspectorExpanded, inspectorCollapsed, inspectorGrip } };
        SetInspectorCollapsed(AppConfig.Get("sidebarCollapsed") == "1", save: false);
    }

    /// <summary>Folded to its column of icons (in its own window it is always open, and folds again when it comes back).</summary>
    bool inspectorFolded;
    Button? inspectorHide;

    void SetInspectorCollapsed(bool collapsed, bool save = true)
    {
        inspectorFolded = collapsed;
        ShowInspectorFold();
        if (save) SaveViewPref("sidebarCollapsed", collapsed ? "1" : "2");
    }

    void ShowInspectorFold()
    {
        bool outside = InspectorOut, folded = inspectorFolded && !outside;
        InspectorBar.Width = folded ? InspectorCollapsedWidth : inspectorWidth;
        if (inspectorGrip != null) inspectorGrip.IsVisible = !folded && !outside;
        if (inspectorHide != null) inspectorHide.IsVisible = !outside;
        inspectorExpanded.IsVisible = !folded;
        inspectorCollapsed.IsVisible = folded;
    }

    /// <summary>The big EQ's draw loop fed from the selected track: the tap hangs on that track's chain only while the big
    /// view is open, and the spectrum counts as live (the web's readSpectrum ok) while the transport plays.</summary>
    void TickInspectorSpectrum()
    {
        var want = eqBig != null && SelectedInspectorClip != null ? sel : null;
        if (spectrumTrack != want)
        {
            if (spectrumTrack != null) spectrumTrack.Chain.Spectrum = null;
            spectrumTrack = want;
            spectrumTap.ResetSmoothing();
            spectrumFrom = spectrumTap.Written;
            if (want != null) want.Chain.Spectrum = spectrumTap;
        }
        if (eqBig == null) return;
        bool live = want != null && engine.Playing && spectrumTap.Written > spectrumFrom;
        if (live) spectrumTap.Read(spectrumBins);
        eqBig?.PushSpectrum(live ? spectrumBins : null, Audio.PlaybackEngine.SampleRate);
    }
    LiveClip? SelectedInspectorClip => sel?.Clips.FirstOrDefault(c => c.Clip.Id == Timeline.SelectedClipId)
        ?? sel?.ShownClip ?? sel?.Clips.FirstOrDefault();

    static string LifeLabel(double v) => Loc.T(v < .2 ? "insp.life.0" : v < .45 ? "insp.life.1" : v < .7 ? "insp.life.2" : v < .9 ? "insp.life.3" : "insp.life.4");
    static string AmountLabel(double a) => Loc.T(a < .2 ? "insp.amount.0" : a < .45 ? "insp.amount.1" : a < .7 ? "insp.amount.2" : a < .9 ? "insp.amount.3" : "insp.amount.4");
    static string Db(double v) => ValueFormat.Gain(v);
    /// <summary>The volume slider's step in fader position (0..1): about 0,1 dB around 0 dB, like a fader's wheel with Shift.</summary>
    const double VolumeStep = 0.005;
    static string Ms(double v) => $"{v:+0.###############;-0.###############;0} ms";
    static string Sec(double v) => $"{v:0.00} s";
    static TextBlock Help(string text) => new() { Text = text, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };
    static Button InspectorIcon(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "ghost" }, Content = new TextBlock { Text = glyph, Classes = { "icon" }, FontSize = 12 }, MinHeight = 28, Padding = new Thickness(4) };
        ToolTip.SetTip(b, tip); b.Click += (_, _) => click(); return b;
    }
    /// <summary>Sections are keyed (and remembered folded) by their Spanish id; this is the text shown.</summary>
    static string SectionTitle(string id) => SendTitles.TryGetValue(id, out var send) ? send
        : BaseTitle(id) is var b && b != id ? $"{SectionTitle(b)} {id[(b.Length + 1)..]}" : Loc.T(id switch
    {
        "Mezcla" => "insp.section.mix", "Saturación" => "insp.section.sat", "Compresor" => "insp.section.comp", "EQ" => "insp.section.eq",
        "Reverb" => "insp.section.reverb", "Delay" => "insp.section.delay", "Transponer" => "insp.section.transpose", "Sincronización" => "insp.section.sync", "Clip" => "insp.section.clip",
        "Encuadre" => "insp.section.frame", _ => id,
    });
    /// <summary>"EQ 2" → "EQ": a repeated slot's section, without its number.</summary>
    static string BaseTitle(string id) => id.LastIndexOf(' ') is int sp && sp > 0 && int.TryParse(id[(sp + 1)..], out _) ? id[..sp] : id;

    /// <summary>A flat section of the inspector: its foldable title (icon, label, an action and its power) over its
    /// rows, tight like a console's inspector (asked by the user, after Fender Studio Pro's).</summary>
    StackPanel Section(string title, string icon, Func<bool>? on = null, Action<bool>? power = null, Control? action = null,
        StackPanel? host = null, List<Action>? refresh = null)
    {
        var refreshes = refresh ?? inspectorRefresh;
        var content = new StackPanel { Spacing = 2, Margin = new Thickness(16, 0, 16, 10) };
        var outer = new StackPanel();
        var border = new Border { Classes = { "section" }, Child = outer, Background = Palette.Panel,
            BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0), Margin = new Thickness(-14, 0) };
        var header = new SectionHeader(SectionTitle(title), icon) { Padding = new Thickness(16, 6), MinHeight = 36 };
        var sectionIcon = header.Icon;
        inspectorSections[title] = (content, action, header);
        sectionLooks[title] = (icon, on);
        header.Toggled += () => { SetSectionCollapsed(title, !IsCollapsed(title)); SaveCollapsedSections(); };
        PowerButton? powerButton = null;
        if (on != null && power != null)
        {
            var toggle = powerButton = new PowerButton(on, () => { if (!syncing) power(!on()); },
                lit => Loc.T(lit ? "mix.list.off" : "mix.list.on"))
                { Margin = new Thickness(6, 0, 0, 0) };
            refreshes.Add(toggle.Refresh);
        }
        header.SetTrailing(action, powerButton);
        outer.Children.Add(header); outer.Children.Add(content);
        (host ?? takeSections).Children.Add(border);
        refreshes.Add(() => { bool enabled = on == null || on(); content.Opacity = enabled ? 1 : 0.4; FxIcon.SetColor(sectionIcon, enabled ? Palette.Accent : Palette.Faint); });
        SetSectionCollapsed(title, IsCollapsed(title));
        return content;
    }

    InspectorSlider Slider(StackPanel host, string label, double min, double max, double step, double reset,
        Func<double> get, Action<double> set, Func<double, string> format, string? hint = null, Func<double>? maximum = null, List<Action>? refresh = null, Func<bool>? mixed = null,
        bool onRelease = false)
    {
        var slider = new InspectorSlider(label, min, max, step, reset, format, v => { if (!syncing) set(v); }, hint, compact: true) { OnRelease = onRelease };
        host.Children.Add(slider); (refresh ?? inspectorRefresh).Add(() => slider.Set(get(), maximum?.Invoke(), mixed?.Invoke() == true ? Loc.T("insp.mixed") : null)); return slider;
    }
    void ClipEdit(Action<Clip, Media?> edit, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly || SelectedInspectorClip is not { } live) return;
        // a locked clip keeps its place and length; the slider goes back to where it was
        if (key is "trim-start" or "trim-end" or "sync" && sel != null && ClipEditing.IsLocked(sel.Track, live.Clip))
        {
            ToastLocked(sel.Track, live.Clip.Id);
            RefreshInspectorText();
            return;
        }
        undo.Checkpoint(store.Project, $"clip:{live.Clip.Id}:{key}");
        var lengths = LiveLengths();
        var loops = ClipLoops.Take(store.Project, lengths);
        edit(live.Clip, live.Media);
        // a loop's repeats follow their source; a repeat edited by itself leaves the loop
        ClipLoops.Keep(store.Project, loops, lengths);
        if (loops.Any(l => l.SourceId == live.Clip.Id)) _ = ApplyTracks(store);
        live.RefreshCurve();
        engine.RefreshClipTiming();
        MarkDirty(); UpdateUndoButtons(); RefreshInspectorText();
        Timeline.LengthSec = tracks.Count == 0 ? 0 : tracks.Max(t => t.EndSec);
        Timeline.InvalidateVisual(); Preview.InvalidateVisual();
    }
    static double EstimatedCompression(CompSettings c) =>
        c.Extra?.TryGetValue("estGrDb", out var value) == true && value.TryGetDouble(out double gr) && double.IsFinite(gr)
            ? gr : Math.Max(0, -c.Threshold / 4);

    void InitInspector()
    {
        try
        {
            foreach (var title in System.Text.Json.JsonSerializer.Deserialize<string[]>(AppConfig.Get("collapsed.inspector") ?? "[]") ?? []) collapsedSections.Add(title);
        }
        catch (System.Text.Json.JsonException) { }
        InitInspectorBar();
        var header = takeHeader;
        takeBadge.Clicked += () => { if (sel != null) ShowTrackLook(sel, takeBadge); };
        header.Children.Add(takeBadge);
        takeName.InnerRightContent = takeNamePencil;
        takeName.PointerEntered += (_, _) => takeNamePencil.Opacity = takeName.IsFocused ? 0 : 1;
        takeName.PointerExited += (_, _) => takeNamePencil.Opacity = 0;
        takeName.GotFocus += (_, _) => takeNamePencil.Opacity = 0;
        ToolTip.SetTip(takeName, Loc.T("insp.name.tip"));
        takeName.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(takeName, 1); header.Children.Add(takeName);
        // Avalonia raises TextChanged after the fact (outside syncing), so filling the box must not count as an edit.
        takeName.TextChanged += (_, _) => { if (sel != null && (takeName.Text ?? "") != sel.Track.Name) Edit(t => t.Track.Name = takeName.Text ?? "", "name"); };
        collapseAll = new Button { Classes = { "ghost" }, Content = collapseAllIcon, Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        collapseAll.Click += (_, _) =>
        {
            bool hide = !FoldableSections.All(IsCollapsed);
            foreach (string title in FoldableSections.ToList()) SetSectionCollapsed(title, hide);
            SaveCollapsedSections();
        };
        Grid.SetColumn(collapseAll, 2); header.Children.Add(collapseAll);
        takeFacts.Children.Add(FactBadge("\uE916", takeLength));
        takeFacts.Children.Add(FactBadge("\uE8D6", takeFormat));
        takeFacts.Children.Add(FactBadge("", takeChannels));
        noSelection.Children.Add(new TextBlock { Text = Loc.T("insp.none"), FontSize = 14, Foreground = Palette.Dim, TextAlignment = TextAlignment.Center });
        noSelection.Children.Add(noSelectionHint);
        Inspector.Children.Add(noSelection);
        // the channel's name, fixed over the editor (MainWindow.InspectorStrip.cs); the file's facts go with its clip
        takeHead.Child = new StackPanel { Children = { header, takeInfo } };
        takeHead.Margin = new Thickness(0);
        takeHead.Padding = new Thickness(0);
        takeHead.BorderThickness = new Thickness(0);
        inspectorTop.Children.Add(takeHead);
        InitGroupInspector();
        inspectorTop.Children.Add(groupMuted);
        inspectorTop.Children.Add(busHead);
        InitInspectorStrip();
        var recordHint = new TextBlock { FontSize = 12, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        recordHint.Inlines!.Add(new Avalonia.Controls.Documents.Run(Loc.T("insp.empty.a")));
        recordHint.Inlines.Add(new Avalonia.Controls.Documents.Run(Shortcuts.First("record")) { FontFamily = Palette.Mono.FontFamily, Foreground = Palette.Fg });
        recordHint.Inlines.Add(new Avalonia.Controls.Documents.Run(Loc.T("insp.empty.b")));
        emptySections.Children.Add(recordHint);
        emptySections.Children.Add(new TextBlock { Text = Loc.T("insp.empty.c"), FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
        var removeEmpty = RemoveButton(Loc.T("insp.removeTrack")); removeEmpty.Margin = new Thickness(0, 12, 0, 0);
        removeEmpty.Click += (_, _) => { if (sel != null) AskRemoveTrack(sel.Track.Id, null, removeEmpty); }; emptySections.Children.Add(removeEmpty);
        // an empty track shows its channel too, with how to fill it and its remove button below
        // the track's section, then the clip's (MainWindow.InspectorStrip.cs); the strip is under both
        Inspector.Children.Add(takeSections); Inspector.Children.Add(emptySections); Inspector.Children.Add(clipArea);
        // several tracks picked: the strip's mute shows them all muted or not, so this says how many are
        inspectorRefresh.Add(() =>
        {
            var group = PickedGroup();
            int muted = group?.Count(t => t.Channel.Muted) ?? 0;
            groupMuted.IsVisible = muted > 0;
            if (group != null) groupMuted.Text = Loc.T("insp.multi.muted", muted, group.Count);
        });
        // native only: a track that sounds without a panel (a backing track, a click…)
        var inVideo = new CheckBox { Content = Loc.T("insp.inVideo"), FontSize = 12 };
        ToolTip.SetTip(inVideo, Loc.T("insp.inVideo.tip"));
        inVideo.IsCheckedChanged += async (_, _) =>
        {
            if (syncing || sel == null) return;
            if (PickedGroup() == null) await SetInVideo(sel, inVideo.IsChecked == true);
            else await SetInVideo(PickTargets(), inVideo.IsChecked == true);
        };
        // its own row: videoOnly already rules the box itself, and a MIDI track has no panel
        var inVideoRow = new Panel { Children = { inVideo }, Margin = new Thickness(0, 4, 0, 4) };
        inspectorRefresh.Add(() =>
        {
            inVideoRow.IsVisible = PickTargets().All(t => !t.Track.IsMidi);
            inVideo.IsChecked = PicksDiffer(t => t.Track.Video ? 1 : 0) ? null : PickLead.Track.Video;
        });
        takeSections.Children.Add(inVideoRow);
        videoOnly.Add(inVideo);
        // native only: the track's pitch, same length (MainWindow.Transpose.cs). It comes first in the strip's chain and
        // reads as off (dimmed) while the track plays at its original pitch.
        // the track's Transponer: with an audio track's clip (like Studio Pro's Transpose per event), else in the track's section
        var transpose = trackTranspose;
        takeSections.Children.Add(transpose);
        // the folded inspector's column still shows it, lit while the track is transposed
        sectionLooks["Transponer"] = (FxIcon.Transpose, () => PickTargets().Any(t => t.Track.Transpose != 0));
        Slider(transpose, SectionTitle("Transponer"), -PitchShifter.MaxSemitones, PitchShifter.MaxSemitones, 1, 0,
            () => PickLead.Track.Transpose, SetTranspose, TransposeText, Loc.T("insp.transpose.tip"), mixed: () => PicksDiffer(t => t.Track.Transpose), onRelease: true);
        InitTranspose();
        InitClipInspector();
        InitMidiInspector();
        UpdateCollapseButton();
    }

    void InitClipInspector()
    {
        Clip C() => SelectedInspectorClip!.Clip;
        Media? M() => SelectedInspectorClip!.Media;
        double Dur() => SelectedInspectorClip!.LengthSec;
        double Len() { var w = ClipEditing.Window(C(), M(), Dur()); return Math.Max(.01, w.End - w.Start); }
        // the clip's sections go apart from the channel's, refreshed only while there is a clip
        StackPanel ClipSection(string title, string icon, Control? action = null) => Section(title, icon, action: action, host: clipSections, refresh: clipRefresh);
        // what the file is (length, format, sides), over the clip's settings
        takeFacts.Margin = new Thickness(0, 0, 0, 6);
        clipSections.Children.Add(takeFacts);
        InspectorSlider ClipSlider(StackPanel host, string label, double min, double max, double step, double reset, Func<double> get, Action<double> set,
            Func<double, string> format, string? hint = null, Func<double>? maximum = null) => Slider(host, label, min, max, step, reset, get, set, format, hint, maximum, clipRefresh);
        var sync = ClipSection("Sincronización", "\uE823");
        void SetSync(double v) => ClipEdit((c, m) => InspectorEditing.SetSync(c, m, v), "sync");
        // ±1 s instead of the web's ±250 ms (user request): some interfaces and cameras lag more than that
        const double SyncRangeMs = 1000;
        var offset = ClipSlider(sync, Loc.T("insp.sync.offset"), -SyncRangeMs, SyncRangeMs, 1, 0, () => C().EffectiveSyncMs(M()), SetSync, Ms,
            Loc.T("insp.sync.offset.hint"));
        foreach (int delta in new[] { -5, 5 })
        {
            var b = new Button { Content = delta < 0 ? "−5" : "+5", Classes = { "chip", "mono" }, FontSize = 10.5, Padding = new Thickness(4, 0), MinHeight = 22 };
            b.Click += (_, _) => SetSync(C().EffectiveSyncMs(M()) + delta); offset.Trailing.Children.Add(b);
        }
        var cameraLatency = ClipSlider(sync, Loc.T("insp.sync.camera"), -SyncRangeMs, SyncRangeMs, 5, 80, () => C().EffectiveVideoOffsetMs(M()), v => ClipEdit((c, _) => c.VideoOffsetMs = v, "video-offset"), Ms,
            Loc.T("insp.sync.camera.hint"));
        videoOnly.Add(cameraLatency);
        var resetClip = InspectorIcon("\uE777", Loc.T("insp.clip.reset"), () => ClipEdit((c, _) => ClipEditing.Reset(store!.Project, c.Id), "reset"));
        var clip = ClipSection("Clip", "\uE8D6", resetClip);
        ClipSlider(clip, Loc.T("insp.clip.gain"), XtremeDaw.Projects.Clip.MinGainDb, XtremeDaw.Projects.Clip.MaxGainDb, .5, 0, () => C().GainDb, v => ClipEdit((c, _) => c.GainDb = v, "gain"), Db,
            Loc.T("insp.clip.gain.hint"));
        ClipSlider(clip, Loc.T("insp.clip.trimStart"), 0, 10, .01, 0, () => C().TrimStartSec, v => ClipEdit((c, _) => c.TrimStartSec = v, "trim-start"), Sec,
            Loc.T("insp.clip.trimStart.hint"), () => Math.Max(.01, Dur() - C().TrimEndSec - ClipEnvelope.MinClipSec));
        ClipSlider(clip, Loc.T("insp.clip.trimEnd"), 0, 10, .01, 0, () => C().TrimEndSec, v => ClipEdit((c, _) => c.TrimEndSec = v, "trim-end"), Sec,
            maximum: () => Math.Max(.01, Dur() - C().TrimStartSec - ClipEnvelope.MinClipSec));
        ClipEnvelope.FadePair Audio() => ClipEnvelope.FitFades(C().FadeInSec, C().FadeOutSec, Len());
        ClipSlider(clip, Loc.T("insp.clip.fadeIn"), 0, 10, .01, 0, () => Audio().FadeIn, v => ClipEdit((c, _) => c.FadeInSec = v, "fade-in"), Sec, maximum: Len);
        ClipSlider(clip, Loc.T("insp.clip.fadeOut"), 0, 10, .01, 0, () => Audio().FadeOut, v => ClipEdit((c, _) => c.FadeOutSec = v, "fade-out"), Sec, maximum: Len);
        var link = new CheckBox { Content = Loc.T("insp.clip.linkFades"), FontSize = 12 };
        ToolTip.SetTip(link, Loc.T("insp.clip.linkFades.tip"));
        link.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            var audio = Audio(); ClipEdit((c, _) => { c.LinkVideoFades = link.IsChecked == true;
                if (!c.LinkVideoFades) { c.VideoFadeInSec = audio.FadeIn; c.VideoFadeOutSec = audio.FadeOut; } }, "link-fades");
        };
        clip.Children.Add(link);
        videoOnly.Add(link);
        var video = new StackPanel { Spacing = 10 }; clip.Children.Add(video);
        ClipEnvelope.FadePair Video() => ClipEnvelope.FitFades(C().VideoFadeInSec, C().VideoFadeOutSec, Len());
        ClipSlider(video, Loc.T("insp.clip.vFadeIn"), 0, 10, .01, 0, () => Video().FadeIn, v => ClipEdit((c, _) => c.VideoFadeInSec = v, "video-fade-in"), Sec, maximum: Len);
        ClipSlider(video, Loc.T("insp.clip.vFadeOut"), 0, 10, .01, 0, () => Video().FadeOut, v => ClipEdit((c, _) => c.VideoFadeOutSec = v, "video-fade-out"), Sec, maximum: Len);
        clipRefresh.Add(() => { link.IsChecked = C().LinkVideoFades; video.IsVisible = !AudioOnly && !C().LinkVideoFades;
            resetClip.IsEnabled = C().GainDb != 0 || C().GainCurve.Count > 0 || C().TrimStartSec > 0 || C().TrimEndSec > 0 || C().FadeInSec > 0 || C().FadeOutSec > 0 || !C().LinkVideoFades && (C().VideoFadeInSec > 0 || C().VideoFadeOutSec > 0); });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var mirror = InspectorIcon("\uE8AB", Loc.T("insp.frame.mirror"), () => ClipEdit((c, _) => c.Framing.Mirror = !c.Framing.Mirror, "mirror"));
        actions.Children.Add(mirror); actions.Children.Add(InspectorIcon("\uE777", Loc.T("insp.frame.reset"), () => ClipEdit((c, _) => InspectorEditing.ResetFrame(c), "framing")));
        var frame = ClipSection("Encuadre", "\uE7A8", actions);
        videoOnly.Add((Control)frame.Parent!.Parent!); // the whole card
        clipRefresh.Add(() =>
        {
            // lit while on: the hover would grey the accent out
            bool on = C().Framing.Mirror;
            mirror.Classes.Set("lit", on);
            mirror.Foreground = on ? Palette.Accent : Palette.Dim;
        });
        ClipSlider(frame, Loc.T("insp.frame.zoom"), 1, 2.5, .01, 1, () => C().Framing.Zoom, v => ClipEdit((c, _) => InspectorEditing.Frame(c, zoom: v), "framing"), v => $"{v:0.00}×");
        frame.Children.Add(Help(Loc.T("insp.frame.hint")));
        var remove = RemoveButton(Loc.T("insp.removeClip")); remove.Margin = new Thickness(0, 16, 0, 0);
        remove.Click += async (_, _) => await DeleteTimelineSelection(); clipSections.Children.Add(remove);
        clipSections.Children.Add(Help(Loc.T("insp.removeClip.hint")));
    }

    /// <summary>The web's full-width "Quitar pista" / "Quitar del timeline" button with its MinusCircle icon.</summary>
    static Button RemoveButton(string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return new Button { Content = content, Classes = { "chip" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 32 };
    }

    void ShowTrack(LiveTrack? t)
    {
        sel = t;
        Inspector.IsEnabled = (t != null || SelectedFolder != null || SelectedBus != null) && !restoring && store?.ReadOnly != true;
        inspectorTrack = t?.Track.Id;
        grShown = 0; RefreshInspectorText();
    }
    void RefreshInspectorText()
    {
        syncing = true;
        try
        {
            takeName.Text = sel?.Track.Name ?? "";
            var c = SelectedInspectorClip;
            var group = PickedGroup();
            RefreshGroupNote(group);
            var folder = sel == null ? SelectedFolder : null;
            var bus = sel == null && folder == null ? SelectedBus : null;
            noSelection.IsVisible = sel == null && folder == null && bus == null;
            folderPanel.IsVisible = folder != null;
            if (folder != null) foreach (var refresh in folderRefresh) refresh();
            if (bus != null)
            {
                foreach (var refresh in busRefresh) refresh();
                RebuildInspectorChain(SlotsOf(bus), bus);
            }
            noSelectionHint.Text = tracks.Any(t => t.Track.HasContent) ? Loc.T("insp.none.hint") : Loc.T("insp.none.empty");
            takeHead.IsVisible = sel != null && group == null;
            takeBadge.Set(sel?.Track, sel == null ? 0 : tracks.IndexOf(sel) + 1, c != null, !AudioOnly);
            takeInfo.Text = sel == null ? "" : c == null ? Loc.T("insp.trackEmpty") : c.Offline ? Loc.T("clip.offline") : "";
            takeInfo.IsVisible = takeInfo.Text != "";
            takeFacts.IsVisible = c is { Offline: false };
            if (c is { Offline: false })
            {
                takeLength.Text = $"{c.LengthSec:0.0} s";
                // the file as it is, not as the engine plays it (resampled to 48 kHz, always two sides)
                string ext = Path.GetExtension(c.Media?.Audio ?? "").TrimStart('.').ToUpperInvariant();
                takeFormat.Text = $"{(ext == "" ? "" : ext + " ")}{c.Audio!.SourceSampleRate / 1000.0:0.#} kHz";
                takeChannels.Text = Loc.T(c.Audio.Mono ? "insp.mono" : "insp.stereo");
            }
            emptySections.IsVisible = sel != null && c == null && !sel.Track.IsMidi && group == null;
            midiPanel.IsVisible = sel is { Track.IsMidi: true } && group == null;
            if (sel is { Track.IsMidi: true } && group == null)
            {
                EnsurePlugins();
                RefreshMidiInspector();
            }
            // an empty audio track is still a channel: its strip and Transponer show (its clip section says there is none)
            // a MIDI track too, under its instrument (decisión del usuario, 2026-11-03); several tracks, of any kind:
            // only what they all have (MainWindow.InspectorGroup.cs)
            bool channel = sel != null || group != null;
            if (collapseAll != null) collapseAll.IsVisible = channel;
            if (channel)
            {
                if (group == null) RebuildInspectorChain(SlotsOf(sel!), sel!);
                RebuildInspectorSends(sel!, group == null ? null : GroupRouter(group));
            }
            LayoutInspector(c, group, bus);
            if (channel)
            {
                foreach (var refresh in inspectorRefresh) refresh();
                if (group == null && c != null) foreach (var refresh in clipRefresh) refresh();
                foreach (var refresh in sendRefresh) refresh();
            }
            if (channel && group == null || bus != null) foreach (var refresh in chainRefresh) refresh();
            if (stripWanted) foreach (var refresh in stripRefresh) refresh();
        }
        finally { syncing = false; }
        RefreshInspectorRail();
        RefreshMixer();
    }
}

