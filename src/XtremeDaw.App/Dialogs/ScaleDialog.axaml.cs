using Avalonia.Controls;
using Avalonia.Input;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// What "Detectar escala y acordes" heard: the scale, how sure it is of the scale and of the chords (estimates, not a
/// measured hit rate), which method gave it, its relative and notes, the chords by how long they sound, and a check
/// (remembered between uses) that puts the chords on the timeline when it closes. Same frame as
/// <see cref="StemsDialog"/>. Enter accepts; Escape or a click outside closes without creating anything.
/// </summary>
public sealed partial class ScaleDialog : UserControl
{
    const string MarkersKey = "scaleMarkers";
    /// <summary>Chords named in the summary; the markers have them all.</summary>
    const int MaxChordsShown = 12;
    /// <summary>From here on <see cref="ScaleDetector.Result.Confidence"/> reads as sure and as likely.</summary>
    const double SureAt = 0.6, LikelyAt = 0.4;

    TaskCompletionSource<bool>? pending;

    public ScaleDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Accept();
        PointerPressed += (_, e) => { if (e.Source == this) Answer(false); };
    }

    public static string Label(ScaleDetector.Scale s) => Loc.T(s.Minor ? "scale.minor" : "scale.major", s.Root);

    /// <summary>Shows the result; true when the chords have to become markers. <paramref name="canMark"/> is false when
    /// the project can't take them now (read-only, recording).</summary>
    public Task<bool> Show(string clip, ScaleDetector.Result r, bool canMark)
    {
        pending?.TrySetResult(false);
        pending = new();
        ClipName.Text = clip;
        ScaleName.Text = Label(r.Scale);
        string sure = Loc.T(r.Confidence >= SureAt ? "scale.sure" : r.Confidence >= LikelyAt ? "scale.likely" : "scale.doubt");
        string how = r.Method switch
        {
            ScaleDetector.Method.Stems => Loc.T("scale.byStems"),
            ScaleDetector.Method.Notes => Loc.T("scale.byNotes"),
            _ => r.Retried ? Loc.T("scale.noBetter") : "",
        };
        Summary.Text = string.Join(" ", new[] { sure, Loc.T("scale.relative", Label(r.Scale.Relative)), Loc.T("scale.confidence", Percent(r.Confidence), Percent(r.ChordFit)), how }.Where(x => x.Length > 0));
        Notes.Text = string.Join("  ", r.Scale.Notes);
        var names = r.Chords.GroupBy(c => c.Name).OrderByDescending(g => g.Sum(c => c.EndSec - c.StartSec)).Select(g => g.Key).ToList();
        Chords.Text = names.Count == 0 ? Loc.T("scale.noChords")
            : string.Join("  ", names.Take(MaxChordsShown)) + (names.Count > MaxChordsShown ? "  …" : "");
        MarkersCheck.Content = Loc.T("scale.markers", r.Chords.Count);
        MarkersCheck.IsEnabled = canMark && r.Chords.Count > 0;
        bool remembered = false;
        try { remembered = AppConfig.Get(MarkersKey) == "true"; }
        catch { }
        MarkersCheck.IsChecked = remembered;
        IsVisible = true;
        OkButton.Focus();
        return pending.Task;
    }

    static int Percent(double x) => (int)Math.Round(x * 100);

    void Accept()
    {
        try { if (MarkersCheck.IsEnabled) AppConfig.Set(MarkersKey, MarkersCheck.IsChecked == true ? "true" : "false"); }
        catch { }
        Answer(MarkersCheck.IsEnabled && MarkersCheck.IsChecked == true);
    }

    void Answer(bool markers)
    {
        IsVisible = false;
        var p = pending;
        pending = null;
        p?.TrySetResult(markers);
    }

    /// <summary>Enter accepts, Escape closes; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) Answer(false);
        else if (e.Key == Key.Enter) Accept();
        // Tab moves the focus and Space presses the focused button: kept from the window's shortcuts (Space would play)
        else if (e.Key is Key.Tab or Key.Space) return true;
        e.Handled = true;
        return true;
    }
}
