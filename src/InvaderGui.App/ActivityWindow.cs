using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InvaderGui.Core;
using Microsoft.Win32;

namespace InvaderGui.App;

public sealed class ActivityWindow : UtilityWindow
{
    private readonly ShellViewModel state;
    private readonly ListBox list = new() { Height = 165, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox summary = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 155 };
    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button cancel;
    private readonly Button clearSelected, clearAll;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Guid? selectedId;
    private string? historyOutput;
    private bool refreshing;

    public ActivityWindow(ShellViewModel state)
    {
        this.state = state; Title = "Output Log"; Width = 960; Height = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(list); top.Children.Add(summary);
        var searchRow = new DockPanel(); top.Children.Add(searchRow);
        var clearButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 4, 0, 4) };
        DockPanel.SetDock(clearButtons, Dock.Right); searchRow.Children.Add(clearButtons);
        clearSelected = DialogUi.Button("Clear Selected Log", () => ClearLogs(false));
        clearAll = DialogUi.Button("Clear All Logs", () => ClearLogs(true));
        clearSelected.ToolTip = "Remove the selected completed log and its saved history. Running logs cannot be cleared.";
        clearAll.ToolTip = "Remove all completed logs and their saved history. Running logs are kept.";
        clearButtons.Children.Add(clearSelected); clearButtons.Children.Add(clearAll);
        var buttons = DialogUi.Row(); searchRow.Children.Add(buttons);
        var actions = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = false };
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        cancel = DialogUi.Button("Cancel", () =>
        {
            if (MessageBox.Show(this, "Request process-tree termination? Stopping does not undo filesystem changes.", "Cancel task", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                state.Coordinator.Runs.FirstOrDefault(r => r.Snapshot.RunId == selectedId)?.Cancel();
        }); DockPanel.SetDock(cancel, Dock.Left); actions.Children.Add(cancel);
        var export = DialogUi.Button("Export", Export);
        export.Margin = new Thickness(0, 2, 0, 2);
        DockPanel.SetDock(export, Dock.Right); actions.Children.Add(export);
        var search = new TextBox { Width = 180, Margin = new Thickness(4), ToolTip = "Search displayed output" }; buttons.Children.Add(search);
        buttons.Children.Add(DialogUi.Button("Find next", () =>
        {
            if (search.Text.Length == 0) return;
            int index = output.Text.IndexOf(search.Text, Math.Min(output.SelectionStart + output.SelectionLength, output.Text.Length), StringComparison.OrdinalIgnoreCase);
            if (index < 0) index = output.Text.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) { output.Focus(); output.Select(index, search.Text.Length); output.ScrollToLine(output.GetLineIndexFromCharacterIndex(index)); }
        }));
        panel.Children.Add(output);
        list.SelectionChanged += (_, _) => { if (!refreshing && list.SelectedItem is RunView view) { selectedId = view.Snapshot.RunId; historyOutput = null; UpdateOutput(); } };
        timer.Tick += (_, _) => Refresh(); timer.Start();
        Closed += (_, _) => timer.Stop(); Refresh();
    }
    public void Select(Guid? id)
    {
        if (id is not null) { selectedId = id; historyOutput = null; } Refresh();
    }
    private void Refresh()
    {
        var live = state.Coordinator.Runs.Select(r => r.View).Reverse().ToArray();
        var ids = live.Select(v => v.Snapshot.RunId).ToHashSet();
        var all = live.Concat(state.History.Where(v => !ids.Contains(v.Snapshot.RunId))).ToArray();
        refreshing = true;
        list.ItemsSource = all;
        list.SelectedItem = all.FirstOrDefault(v => v.Snapshot.RunId == selectedId) ?? all.FirstOrDefault();
        if (list.SelectedItem is RunView view) selectedId = view.Snapshot.RunId;
        bool Clearable(RunView item) => !item.Active && state.Coordinator.Runs.FirstOrDefault(run => run.Snapshot.RunId == item.Snapshot.RunId)?.Completion.IsCompleted != false;
        clearSelected.IsEnabled = list.SelectedItem is RunView selected && Clearable(selected);
        clearAll.IsEnabled = all.Any(Clearable);
        refreshing = false; UpdateOutput();
    }
    private void UpdateOutput()
    {
        if (list.SelectedItem is not RunView view) { summary.Text = "No task history yet. Task output and launch details will appear here when an operation runs."; output.Clear(); cancel.IsEnabled = false; return; }
        var snapshot = view.Snapshot;
        summary.Text = $"{snapshot.Summary}\nStatus: {view.Status.ToDisplayText()} · Exit: {view.ExitCode?.ToString() ?? "—"} · Duration: {view.Duration:g} · Cancellation requested: {view.CancellationRequested}\nProfile: {snapshot.ProfileName} ({snapshot.ProfileId})\nExecutable: {snapshot.ExecutablePath}\nEditing Kit Folder: {snapshot.WorkingDirectory}\nSlot: {snapshot.SlotId} · Scan: {snapshot.DiscoveryScanId}\nTag roots: {string.Join(" → ", snapshot.TagRoots.Select(r => r.Path))}\nArguments: {JsonSerializer.Serialize(snapshot.Arguments)}\nLog: {snapshot.LogFilePath}\n{view.Diagnostic}\n{(view.LogError is null ? "" : "Log write failed; output remains in memory: " + view.LogError)}";
        if (!snapshot.Steps.IsDefaultOrEmpty) summary.Text += "\nSteps:\n" + string.Join("\n", snapshot.Steps.Select((step, i) => $"{i + 1}. {step.Name}: {step.ExecutablePath} {JsonSerializer.Serialize(step.Arguments)}"));
        if (!view.StepResults.IsDefaultOrEmpty) summary.Text += "\nStep results:\n" + string.Join("\n", view.StepResults.Select(step => $"{step.Index}. {step.Name}: {step.Status.ToDisplayText()}, exit {step.ExitCode?.ToString() ?? "—"}. {step.Diagnostic}"));
        cancel.IsEnabled = view.Active;
        string text;
        if (state.Coordinator.Runs.Any(r => r.Snapshot.RunId == snapshot.RunId)) text = view.Output;
        else
        {
            try { text = historyOutput ??= RunHistory.ReadOutput(view); }
            catch (Exception ex) { text = historyOutput ??= $"Saved output could not be read: {ex.Message}"; }
        }
        if (output.Text != text)
        {
            bool follow = output.VerticalOffset >= output.ExtentHeight - output.ViewportHeight - 25;
            output.Text = text; if (follow) output.ScrollToEnd();
        }
    }
    private void ClearLogs(bool all)
    {
        if (!all && selectedId is null) return;
        if (all && MessageBox.Show(this, "Clear all completed logs and their saved output/history? Running logs will be kept.",
            "Clear All Logs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var errors = state.ClearCompletedLogs(selectedId is { } id ? new[] { id } : [], all);
        historyOutput = null; selectedId = null; Refresh();
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Some logs could not be cleared", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Export()
    {
        var picker = new SaveFileDialog { FileName = "invader-output.txt", Filter = "Text files|*.txt" };
        if (picker.ShowDialog(this) == true)
        {
            try { File.WriteAllText(picker.FileName, summary.Text + Environment.NewLine + Environment.NewLine + output.Text); }
            catch (Exception ex) { DialogUi.Error(this, ex); }
        }
    }
}

