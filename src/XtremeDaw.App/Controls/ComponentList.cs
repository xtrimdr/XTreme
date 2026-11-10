using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.Audio;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The optional components as rows: what each is for, its state and the button that installs it through
/// <see cref="ComponentInstall"/>. The notice at startup, Configuración, Acerca de and the camera page all show this, so
/// a new component needs nothing here. It redraws itself when anything changes.
/// </summary>
public sealed class ComponentList : StackPanel
{
    /// <summary>Leaves out what is installed.</summary>
    public bool OnlyMissing { get; set; }
    /// <summary>Shows just this component.</summary>
    public string? Only { get; set; }

    public ComponentList()
    {
        Spacing = 10;
        ComponentInstall.Changed += Rebuild;
        AttachedToVisualTree += (_, _) => Rebuild();
    }

    public void Rebuild()
    {
        Children.Clear();
        foreach (var c in Components.All)
            if ((Only == null || c.Id == Only) && (!OnlyMissing || !c.Ready)) Children.Add(Row(c));
        IsVisible = Children.Count > 0;
    }

    static Control Row(OptionalComponent c)
    {
        bool active = ComponentInstall.Active == c;
        IReadOnlyList<ComponentPackage> packages = c.Ready ? [] : c.Packages;
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = Loc.T($"component.{c.Id}.name"), FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = Loc.T($"component.{c.Id}.desc"), Classes = { "dim" }, TextWrapping = TextWrapping.Wrap });
        string state = active ? Loc.T("component.state.installing") : c.Ready ? Loc.T("component.state.ready")
            : !c.CanInstall ? Loc.T($"component.{c.Id}.unavailable") : Loc.T(c.Partial ? "component.state.partial" : "component.state.missing");
        text.Children.Add(new TextBlock { Text = state, FontSize = 11, Foreground = c.Ready ? Palette.Dim : Palette.Accent, TextWrapping = TextWrapping.Wrap });

        if (!c.Ready && c.TermsPage != null)
            text.Children.Add(new TextBlock { Text = Loc.T($"component.{c.Id}.terms"), FontSize = 11, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        SelectButton? pick = null;
        if (!c.Ready && !active && c.CanInstall && packages.Count > 1)
        {
            var select = new SelectButton { MinWidth = 150 };
            select.SetItems([.. packages.Select(p => $"{Loc.T("component.package." + p.Id)} · {ComponentInstall.Size(p.Bytes)}")]);
            select.SelectedIndex = packages.Count - 1;
            // the list only reports the choice: the button shows it once it is set here
            select.Selected += i => select.SelectedIndex = i;
            buttons.Children.Add(pick = select);
        }
        if (!c.Ready && c.TermsPage is { } terms)
        {
            var page = new Button { Classes = { "btn" }, Content = Loc.T("component.terms") };
            page.Click += (_, _) => Explorer.Browse(terms);
            buttons.Children.Add(page);
        }
        if (active)
        {
            var stop = new Button { Classes = { "btn" }, Content = Loc.T("common.cancel") };
            stop.Click += (_, _) => ComponentInstall.Cancel();
            buttons.Children.Add(stop);
        }
        else if (!c.Ready)
        {
            if (c.ManualUrl is { } url && !c.CanInstall)
            {
                var page = new Button { Classes = { "btn" }, Content = Loc.T("component.page") };
                page.Click += (_, _) => Explorer.Browse(url);
                buttons.Children.Add(page);
            }
            long size = packages.Count == 1 ? packages[0].Bytes : 0;
            var install = new Button
            {
                Classes = { "btn", "primary" }, IsEnabled = c.CanInstall && ComponentInstall.Active == null,
                Content = size > 0 ? Loc.T("component.install.size", ComponentInstall.Size(size)) : Loc.T("component.install"),
            };
            install.Click += (_, _) => _ = ComponentInstall.Run(c, pick is { SelectedIndex: >= 0 } ? packages[pick.SelectedIndex] : packages.LastOrDefault());
            buttons.Children.Add(install);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = grid };
    }
}
