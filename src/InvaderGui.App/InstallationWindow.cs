using System.Windows;
using System.Windows.Controls;

namespace InvaderGui.App;

public sealed class InstallationWindow : UtilityWindow
{
    private readonly ShellViewModel state;
    private readonly TextBox directory;
    private readonly TextBlock status = DialogUi.Text("");
    private readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 280 };
    private readonly Button scanButton;

    public InstallationWindow(ShellViewModel state)
    {
        this.state = state;
        Title = "Installation and settings — global"; Width = 780; Height = 690; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = DialogUi.Column(); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(DialogUi.Text("Choose your existing Invader installation. Available operations remain usable when other executables are missing."));
        directory = DialogUi.Folder(panel, "Global Invader directory", state.Settings.Value.Installation.Directory, this);
        var installationActions = DialogUi.Row(); panel.Children.Add(installationActions);
        scanButton = DialogUi.Button("Rescan", () => _ = ScanAsync());
        scanButton.ToolTip = "Scan the selected directory and refresh tool availability.";
        installationActions.Children.Add(scanButton);
        installationActions.Children.Add(DialogUi.Button("Open Invader Folder", () =>
        {
            if (!System.IO.Directory.Exists(directory.Text)) { MessageBox.Show(this, "Select an existing Invader directory first."); return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                { UseShellExecute = false, ArgumentList = { System.IO.Path.GetFullPath(directory.Text) } });
            }
            catch (Exception ex) { DialogUi.Error(this, ex); }
        }));
        panel.Children.Add(status); panel.Children.Add(details);
        var logs = DialogUi.Folder(panel, "Log directory (relative paths resolve beside the GUI)", state.Settings.Value.Logs.Directory, this);
        var retention = DialogUi.Field(panel, "Recent history records to load", state.Settings.Value.Logs.RetainRunCount.ToString());
        var openFailure = new CheckBox { Content = "Open Activity when a task fails", IsChecked = state.Settings.Value.Logs.OpenActivityOnFailure, Margin = new Thickness(0, 6, 0, 6) }; panel.Children.Add(openFailure);
        var actions = DialogUi.Row(); panel.Children.Add(actions);
        actions.Children.Add(DialogUi.Button("Save log preferences", () =>
        {
            if (!int.TryParse(retention.Text, out var count) || count < 1 || count > 10000) { MessageBox.Show(this, "Enter a history count from 1 to 10000."); return; }
            try { _ = System.IO.Path.GetFullPath(logs.Text, state.ApplicationDirectory); }
            catch (Exception ex) { DialogUi.Error(this, ex); return; }
            state.Settings.Value.Logs.Directory = logs.Text; state.Settings.Value.Logs.RetainRunCount = count;
            state.Settings.Value.Logs.OpenActivityOnFailure = openFailure.IsChecked == true;
            DialogUi.SaveWithRecovery(this, state);
        }));
        actions.Children.Add(DialogUi.Button("Configuration recovery…", () => new StorageWindow(state) { Owner = this }.ShowDialog()));
        actions.Children.Add(DialogUi.Button("Close", Close));
        UpdateDetails();
    }

    private async Task ScanAsync()
    {
        scanButton.IsEnabled = false;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(directory.Text)) { MessageBox.Show(this, "Select an absolute installation directory."); return; }
            var result = await state.RescanAsync(directory.Text);
            UpdateDetails(); DialogUi.SaveWithRecovery(this, state);
            var missing = result.Tools.Where(t => !t.Available).Select(t => t.Executable).ToArray();
            if (missing.Length > 0) MessageBox.Show(this, "Missing executables:\n\n" + string.Join("\n", missing) + "\n\nOnly the affected operations are unavailable.", "Installation scan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { DialogUi.Error(this, ex); }
        finally { scanButton.IsEnabled = true; }
    }
    private void UpdateDetails()
    {
        if (state.Scan is not { } scan) { status.Text = "No installation scanned. Browse to a directory and scan it."; return; }
        status.Text = $"Available: {scan.Tools.Count(t => t.Available)} / {scan.Tools.Count} · Scan: {scan.ScannedUtc.LocalDateTime:g}";
        details.Text = string.Join(Environment.NewLine, scan.Tools.Select(t => $"{(t.Available ? "Available" : "Missing")}  {t.Executable}  {t.Reason}"));
    }
}
