using Avalonia.Controls;
using Avalonia.Input;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "¿Copiar o vincular?", asked when files come in while <see cref="ImportMode"/> says to ask (asked by the user).
/// Same frame as <see cref="UnsavedDialog"/>. Enter copies, what the project can't lose; Escape or a click outside
/// cancels the import. "No mostrar más" counts only with a choice, never with cancelling.
/// </summary>
public sealed partial class ImportDialog : UserControl
{
    public enum Choice { Copy, Link, Cancel }

    TaskCompletionSource<Choice>? pending;

    /// <summary>The answer given also holds from now on.</summary>
    public bool Remember => RememberBox.IsChecked == true;

    public ImportDialog()
    {
        InitializeComponent();
        CopyButton.Click += (_, _) => Answer(Choice.Copy);
        LinkButton.Click += (_, _) => Answer(Choice.Link);
        CancelButton.Click += (_, _) => Answer(Choice.Cancel);
        PointerPressed += (_, e) => { if (e.Source == this) Answer(Choice.Cancel); };
    }

    /// <summary>Shows the question for <paramref name="paths"/> and waits for the answer.</summary>
    public Task<Choice> Ask(IReadOnlyList<string> paths)
    {
        pending?.TrySetResult(Choice.Cancel);
        pending = new TaskCompletionSource<Choice>();
        Body.Text = paths.Count == 1 ? Loc.T("imp.ask.one", Path.GetFileName(paths[0])) : Loc.T("imp.ask.many", paths.Count);
        RememberBox.IsChecked = false;
        IsVisible = true;
        CopyButton.Focus();
        return pending.Task;
    }

    void Answer(Choice c)
    {
        IsVisible = false;
        var p = pending;
        pending = null;
        p?.TrySetResult(c);
    }

    /// <summary>Enter copies, Escape cancels; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) Answer(Choice.Cancel);
        else if (e.Key is Key.Enter or Key.Space)
        {
            if (e.Source == RememberBox) RememberBox.IsChecked = !Remember;
            else Answer(e.Source == LinkButton ? Choice.Link : e.Source == CancelButton ? Choice.Cancel : Choice.Copy);
        }
        e.Handled = true;
        return true;
    }
}
