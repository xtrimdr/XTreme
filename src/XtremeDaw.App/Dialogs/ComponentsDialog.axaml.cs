using Avalonia.Controls;
using Avalonia.Input;
using XtremeDaw.Audio;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// What is missing and the way to install it, at startup and from whatever needs it. "Omitir" stops the question at
/// startup; the features stay disabled and Configuración still offers them. It closes itself when nothing is missing.
/// </summary>
public sealed partial class ComponentsDialog : UserControl
{
    public ComponentsDialog()
    {
        InitializeComponent();
        RecheckButton.Click += async (_, _) => await ComponentInstall.RefreshAsync();
        SkipButton.Click += (_, _) => Skip();
        ComponentInstall.Changed += () =>
        {
            if (IsVisible && Components.All.All(c => c.Ready || !c.AskAtStart)) IsVisible = false;
        };
    }

    public void Show()
    {
        List.Rebuild();
        IsVisible = true;
        Focus();
    }

    void Skip()
    {
        ComponentInstall.Skip(Components.All.Where(c => !c.Ready));
        IsVisible = false;
    }

    /// <summary>Escape closes for now, like every other dialog (only "Omitir" stops the question at startup); Tab,
    /// Enter and Space reach the buttons but never the window's shortcuts (Space would play).</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key is Key.Tab or Key.Enter or Key.Space) return true;
        if (e.Key == Key.Escape) IsVisible = false;
        e.Handled = true;
        return true;
    }
}
