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
            if (IsVisible && Components.All.All(c => c.Ready)) IsVisible = false;
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

    /// <summary>Escape omits; Tab and Enter work the buttons; nothing else leaves it.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key is Key.Tab or Key.Enter or Key.Space) return false;
        if (e.Key == Key.Escape) Skip();
        e.Handled = true;
        return true;
    }
}
