using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace InvaderGui.App;

public class UtilityWindow : Window
{
    public UtilityWindow() => SetResourceReference(StyleProperty, typeof(Window));
}

internal static class DialogUi
{
    public static StackPanel Column() => new() { Margin = new Thickness(12) };
    public static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
    public static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 2, 5, 2), Padding = new Thickness(8, 3, 8, 3) };
        button.Click += (_, _) => action();
        return button;
    }
    public static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 3) };
    public static TextBox Field(Panel parent, string label, string value)
    {
        parent.Children.Add(Text(label));
        var field = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 3), MinWidth = 120 };
        parent.Children.Add(field); return field;
    }
    public static TextBox Folder(Panel parent, string label, string value, Window owner)
    {
        parent.Children.Add(Text(label));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var field = new TextBox { Text = value, MinWidth = 100 };
        var browse = Button("Browse…", () =>
        {
            var path = PickFolder(owner, field.Text);
            if (path is not null) field.Text = path;
        });
        DockPanel.SetDock(browse, Dock.Left); row.Children.Add(browse); row.Children.Add(field); parent.Children.Add(row);
        return field;
    }
    public static string? PickFolder(Window owner, string? initial = null)
    {
        var picker = new OpenFolderDialog { Title = "Select folder", Multiselect = false };
        if (System.IO.Directory.Exists(initial)) picker.InitialDirectory = initial;
        return picker.ShowDialog(owner) == true ? picker.FolderName : null;
    }
    public static void Error(Window owner, Exception ex) => MessageBox.Show(owner, ex.Message, "Invader GUI", MessageBoxButton.OK, MessageBoxImage.Error);
    public static void SaveWithRecovery(Window owner, ShellViewModel state)
    {
        if (!state.SaveAll()) new StorageWindow(state) { Owner = owner }.ShowDialog();
    }
}
