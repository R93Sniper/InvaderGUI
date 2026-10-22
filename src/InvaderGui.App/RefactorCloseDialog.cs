using System.Windows;
using System.Windows.Controls;

namespace InvaderGui.App;

public enum RefactorCloseChoice { Cancel, CancelCommand, FinishCommand }

public sealed class RefactorCloseDialog : UtilityWindow
{
    public RefactorCloseChoice Choice { get; private set; } = RefactorCloseChoice.Cancel;

    public RefactorCloseDialog(Window owner)
    {
        Owner = owner; Title = "Close while Refactor is running";
        Width = 520; Height = 225; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        Content = content;
        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); content.Children.Add(actions);
        var cancel = DialogUi.Button("Cancel", () => { Choice = RefactorCloseChoice.Cancel; DialogResult = false; });
        cancel.IsCancel = true; DockPanel.SetDock(cancel, Dock.Left); actions.Children.Add(cancel);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right); actions.Children.Add(right);
        right.Children.Add(DialogUi.Button("No", () => { Choice = RefactorCloseChoice.CancelCommand; DialogResult = true; }));
        var yes = DialogUi.Button("Yes", () => { Choice = RefactorCloseChoice.FinishCommand; DialogResult = true; });
        yes.IsDefault = true; right.Children.Add(yes);
        content.Children.Add(new TextBlock
        {
            Text = "Let the current command finish before closing?\n\nYes: finish the current command, then stop and close.\nNo: cancel the current command, then close.\nCancel: keep the application open.",
            TextWrapping = TextWrapping.Wrap
        });
    }
}
