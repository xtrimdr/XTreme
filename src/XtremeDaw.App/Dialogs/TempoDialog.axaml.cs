using Avalonia.Controls;
using Avalonia.Input;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "¿Estirar los clips?", asked when the tempo changes with audio on the timeline (asked by the user): the clips can
/// follow the new tempo or stay as they are while only the grid moves. Same frame as <see cref="UnsavedDialog"/>.
/// Enter stretches, Escape or a click outside cancels and the tempo stays.
/// </summary>
public sealed partial class TempoDialog : UserControl
{
    public enum Choice { Stretch, Grid, Cancel }

    TaskCompletionSource<Choice>? pending;

    public TempoDialog()
    {
        InitializeComponent();
        StretchButton.Click += (_, _) => Answer(Choice.Stretch);
        GridButton.Click += (_, _) => Answer(Choice.Grid);
        CancelButton.Click += (_, _) => Answer(Choice.Cancel);
        PointerPressed += (_, e) => { if (e.Source == this) Answer(Choice.Cancel); };
    }

    /// <summary>Shows the question for a tempo going from <paramref name="from"/> to <paramref name="to"/> and waits for the answer.</summary>
    public Task<Choice> Ask(string from, string to)
    {
        pending?.TrySetResult(Choice.Cancel);
        pending = new TaskCompletionSource<Choice>();
        Body.Text = Loc.T("tempo.ask.body", from, to);
        IsVisible = true;
        StretchButton.Focus();
        return pending.Task;
    }

    void Answer(Choice c)
    {
        IsVisible = false;
        var p = pending;
        pending = null;
        p?.TrySetResult(c);
    }

    /// <summary>Enter stretches, Escape cancels; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) Answer(Choice.Cancel);
        else if (e.Key is Key.Enter or Key.Space)
            Answer(e.Source == GridButton ? Choice.Grid : e.Source == CancelButton ? Choice.Cancel : Choice.Stretch);
        e.Handled = true;
        return true;
    }
}
