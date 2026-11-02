using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The mixer's small &lt;select&gt; (h-6, 11 px, Raised box): the chosen option and a chevron, the list in a flyout.
/// Avalonia's ComboBox keeps a wide drop-down column that leaves no room for the text in a 200 px strip.
/// Options with a group go in a submenu with its name, in the order they come. Setting the items or
/// <see cref="SelectedIndex"/> from code never raises <see cref="Selected"/>.
/// </summary>
public sealed class SelectButton : Button
{
    readonly TextBlock text = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    string[] items = [];
    string?[] hints = [];
    string?[]? groups;
    int selected = -1;
    public event Action<int>? Selected;
    /// <summary>The options that get a separator above them in the list.</summary>
    public int[] BreaksBefore { get; set; } = [];

    public SelectButton()
    {
        Height = MinHeight = 24;
        Padding = new Thickness(6, 0, 4, 0);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        BorderBrush = Palette.Line;
        Background = Palette.Raised;
        Foreground = Palette.Fg;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        var chevron = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 8, Foreground = Palette.Dim, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        Content = grid;
        Click += (_, _) => Open();
    }

    /// <summary>Replaces the options (only when they changed); hints show as each option's tooltip, groups name each one's submenu.</summary>
    public void SetItems(string[] next, string?[]? nextHints = null, string?[]? nextGroups = null)
    {
        hints = nextHints ?? new string?[next.Length];
        groups = nextGroups;
        if (items.SequenceEqual(next)) return;
        items = next;
        SelectedIndex = selected;
    }

    public int SelectedIndex
    {
        get => selected;
        set { selected = value; text.Text = value >= 0 && value < items.Length ? items[value] : ""; }
    }

    /// <summary>Shows a text of its own, with no option chosen (tracks edited together that don't share one).</summary>
    public void ShowNone(string label)
    {
        selected = -1;
        text.Text = label;
    }

    void Open()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var parents = new Dictionary<string, MenuItem>();
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            if (BreaksBefore.Contains(i)) menu.Items.Add(new Separator());
            var item = new MenuItem { Header = items[i], FontSize = 12, FontWeight = i == selected ? FontWeight.SemiBold : FontWeight.Normal };
            if (hints[i] is { } hint) ToolTip.SetTip(item, hint);
            item.Click += (_, _) => { if (index != selected) Selected?.Invoke(index); };
            if (groups?[i] is not { } group)
            {
                menu.Items.Add(item);
                continue;
            }
            if (!parents.TryGetValue(group, out var parent))
            {
                parent = new MenuItem { Header = group, FontSize = 12 };
                parents[group] = parent;
                menu.Items.Add(parent);
            }
            if (i == selected) parent.FontWeight = FontWeight.SemiBold;
            parent.Items.Add(item);
        }
        menu.ShowAt(this);
    }
}
