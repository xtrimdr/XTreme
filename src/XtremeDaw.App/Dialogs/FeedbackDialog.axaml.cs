using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Enviar comentarios", from the title bar and "Acerca de" (asked by the user): a bug or a suggestion that continues on
/// GitHub's new-issue page, filled in by <see cref="IssueReport"/>, where the user reviews it and sends it with their
/// account. The facts it adds are in view and can be left out, because issues are public. What was written stays until
/// it reaches the browser: closing keeps it for the next time, and a text too long for the link goes by the clipboard.
/// Same frame as <see cref="UnsavedDialog"/>.
/// </summary>
public sealed partial class FeedbackDialog : UserControl
{
    public event Action? Dismissed;

    /// <summary>The page opened; true = the text is on the clipboard, for pasting there.</summary>
    public event Action<bool>? Sent;

    IssueReport.Kind kind = IssueReport.Kind.Bug;
    IReadOnlyList<string> facts = [];

    public FeedbackDialog()
    {
        InitializeComponent();
        BugButton.Click += (_, _) => SetKind(IssueReport.Kind.Bug);
        IdeaButton.Click += (_, _) => SetKind(IssueReport.Kind.Idea);
        TitleBox.TextChanged += (_, _) => SendButton.IsEnabled = !string.IsNullOrWhiteSpace(TitleBox.Text);
        FactsBox.IsCheckedChanged += (_, _) => FactsText.Opacity = FactsBox.IsChecked == true ? 1 : 0.4;
        CancelButton.Click += (_, _) => HideDialog();
        SendButton.Click += (_, _) => Send();
        PointerPressed += (_, e) => { if (e.Source == this) HideDialog(); };
        SetKind(kind);
    }

    /// <summary>Opens it with <paramref name="system"/>, the lines the report adds about this machine.</summary>
    public void Show(IReadOnlyList<string> system)
    {
        facts = system;
        FactsText.Text = string.Join('\n', system);
        ProblemText.IsVisible = false;
        IsVisible = true;
        TitleBox.Focus();
    }

    void SetKind(IssueReport.Kind value)
    {
        kind = value;
        bool bug = value == IssueReport.Kind.Bug;
        BugButton.IsChecked = bug;
        IdeaButton.IsChecked = !bug;
        TitleBox.Watermark = Loc.T(bug ? "feedback.subject.bug" : "feedback.subject.idea");
        DescriptionBox.Watermark = Loc.T(bug ? "feedback.text.bug" : "feedback.text.idea");
    }

    async void Send()
    {
        string title = IssueReport.Title(kind, TitleBox.Text ?? "");
        string body = IssueReport.Body(DescriptionBox.Text ?? "", FactsBox.IsChecked == true ? facts : []);
        string url = IssueReport.Url(title, body, out bool complete);
        if (!complete)
        {
            try { await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(body) ?? Task.CompletedTask); }
            catch (Exception e) when (e is COMException or InvalidOperationException)
            {
                Problem("feedback.noClipboard");
                return;
            }
        }
        if (!Explorer.Browse(url))
        {
            Problem("feedback.noBrowser");
            return;
        }
        TitleBox.Clear();
        DescriptionBox.Clear();
        HideDialog();
        Sent?.Invoke(!complete);
    }

    void Problem(string key)
    {
        ProblemText.Text = Loc.T(key);
        ProblemText.IsVisible = true;
    }

    public void HideDialog()
    {
        IsVisible = false;
        Dismissed?.Invoke();
    }

    /// <summary>Escape closes; every other key goes to the fields, never to the window's shortcuts.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape)
        {
            HideDialog();
            e.Handled = true;
        }
        return true;
    }
}
