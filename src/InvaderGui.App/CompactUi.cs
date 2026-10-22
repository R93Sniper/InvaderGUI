using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InvaderGui.App;

internal static class CompactUi
{
    public static Expander SavedSettings(UIElement content) => new()
    {
        Header = "Saved settings (optional)", Content = content, Foreground = Brushes.WhiteSmoke,
        Margin = new Thickness(0, 4, 0, 2),
        ToolTip = "Your current settings save automatically with the profile. Expand to save named option sets and apply them later. Inputs and folders stay with the profile."
    };
    public static DockPanel BrowseRow(TextBox field, string label, Action browse)
    {
        if (field.Parent is Panel parent) parent.Children.Remove(field);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
        var button = DialogUi.Button(label, browse); DockPanel.SetDock(button, Dock.Left);
        row.Children.Add(button); row.Children.Add(field); return row;
    }
    public static WrapPanel Options()
    {
        var panel = new WrapPanel();
        panel.SizeChanged += (_, e) => panel.ItemWidth = panel.Children.Count > 1 && e.NewSize.Width >= 440 ? e.NewSize.Width / 2 : Math.Max(1, e.NewSize.Width);
        return panel;
    }
}
