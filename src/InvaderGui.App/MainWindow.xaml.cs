using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;
using InvaderGui.Core;

namespace InvaderGui.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel state;
    private readonly BuildMapForm buildMap;
    private readonly RefactorPreview refactorPreview;
    private readonly Dictionary<string, TagHandlingForm> tagForms = new();
    private readonly Dictionary<string, ToolOperationForm> toolForms = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ActivityWindow? activity;
    private bool ready, allowClose, closing;
    private bool updatingSelector;
    private string? selectorSlot;
    private OperationDefinition operation = Operations.All[0];
    private readonly HashSet<Guid> announcedFailures = new();

    public MainWindow()
    {
        state = new(); InitializeComponent(); DataContext = state;
        buildMap = new(state, OpenActivityInBackground, enabled => { if (operation.Id == "build-map") PrimaryAction.IsEnabled = enabled; });
        BuildMapHost.Content = buildMap;
        refactorPreview = new RefactorPreview(state.Settings.Value.Ui, () => { state.Settings.MarkDirty(); SaveSettings(); });
        RefactorHost.Content = refactorPreview;
        RefactorOptionsHost.Content = refactorPreview.ReferenceOptions;
        RefactorTagFolderHost.Content = refactorPreview.TagFolderSelector;
        state.PlanGuard = refactorPreview;
        refactorPreview.ConfigureExecution(state, OpenActivityInBackground);
        BuildMapStatusHost.Content = buildMap.StatusPanel;
        foreach (var definition in Operations.Visible.Where(o => o.SlotId == "tag-handling" && !ToolForms.Fields.ContainsKey(o.Id)))
        {
            var id = definition.Id;
            tagForms.Add(id, new(id, state, OpenActivityInBackground, enabled => { if (operation.Id == id) PrimaryAction.IsEnabled = enabled; }));
        }
        foreach (var id in ToolForms.Fields.Keys)
            toolForms.Add(id, new(id, state, OpenActivityInBackground, enabled => { if (operation.Id == id) PrimaryAction.IsEnabled = enabled; }));
        var window = state.Settings.Value.Window;
        Width = Math.Clamp(window.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(window.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        if (window.Left is double left && window.Top is double top && double.IsFinite(left) && double.IsFinite(top) &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 && left + Width > SystemParameters.VirtualScreenLeft + 80 &&
            top >= SystemParameters.VirtualScreenTop && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        { Left = left; Top = top; }
        if (window.State == "maximized") WindowState = WindowState.Maximized;
        var selectedOperation = Operations.All.FirstOrDefault(o => o.Id == Operations.ResolvePageId(state.Settings.Value.Ui.LastPageId)) ?? operation;
        state.Settings.Value.Ui.OperationSelections[selectedOperation.SlotId] = selectedOperation.Id;
        SidebarColumn.Width = new GridLength(Math.Clamp(state.Settings.Value.Ui.SidebarWidth, 150, 360));
        TaskList.SelectedItem = state.Navigation.First(n => n.SlotId == selectedOperation.SlotId);
        ready = true;
        timer.Tick += (_, _) => Tick(); timer.Start(); Render();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (state.StorageErrors.Count > 0) new StorageWindow(state) { Owner = this }.ShowDialog();
        if (state.StartupWarning is { } warning) MessageBox.Show(this, warning, "History warning");
        var directory = state.Settings.Value.Installation.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            new InstallationWindow(state) { Owner = this }.ShowDialog();
        else await ScanAsync();
        if (state.Settings.Value.Ui.ActivityPaneOpen) OpenActivityInBackground(null);
        Render();
    }
    private async Task ScanAsync()
    {
        try
        {
            var scan = await state.RescanAsync(state.Settings.Value.Installation.Directory);
            DialogUi.SaveWithRecovery(this, state);
            var missing = scan.Tools.Where(t => !t.Available).Select(t => t.Executable).ToArray();
            if (missing.Length > 0) MessageBox.Show(this, "Missing executables:\n\n" + string.Join("\n", missing) + "\n\nOther operations remain available.", "Installation scan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { DialogUi.Error(this, ex); }
        finally { Render(); }
    }
    private void Tick()
    {
        Render();
        foreach (var run in state.Coordinator.Runs)
        {
            var view = run.View;
            if ((view.Status is RunStatus.NonzeroExit or RunStatus.StartFailed || view.Diagnostic?.StartsWith("Invader reported tag errors.", StringComparison.Ordinal) == true) && announcedFailures.Add(view.Snapshot.RunId) && state.Settings.Value.Logs.OpenActivityOnFailure)
                OpenActivityInBackground(run.Snapshot.RunId);
        }
    }
    private void Render()
    {
        if (!ready || TaskList.SelectedItem is not NavigationItem navigation) return;
        var choices = Operations.Visible.Where(o => o.SlotId == navigation.SlotId).ToArray();
        if (selectorSlot != navigation.SlotId)
        {
            updatingSelector = true;
            try
            {
                selectorSlot = navigation.SlotId;
                OperationSelector.ItemsSource = choices;
                var remembered = Operations.ResolvePageId(state.Settings.Value.Ui.OperationSelections.GetValueOrDefault(navigation.SlotId) ?? "");
                OperationSelector.SelectedItem = choices.FirstOrDefault(o => o.Id == remembered) ?? choices[0];
            }
            finally { updatingSelector = false; }
        }
        operation = OperationSelector.SelectedItem as OperationDefinition ?? choices[0];
        TaskHeading.Text = navigation.Name;
        TaskHeading.ToolTip = operation.Description;
        OperationSelector.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        var observation = state.Scan?.For(operation);
        OperationDescription.Text = operation.Description;
        OperationStatus.Text = observation is null ? "Installation not configured. Use Invader to select your Invader directory." : observation.Available ? $"Available: {operation.Executable}" : $"Unavailable: {operation.Executable}\n{observation.Reason}";
        PhaseNotice.Text = operation.SlotId == "refactor" ? "The Refactor planner and FIFO queue arrive in Phase 5. No file changes can be staged or executed on this page yet." : "This operation's graphical form arrives in Phase 4. Option guidance will appear in tooltips beside the relevant controls; this placeholder does not process assets.";
        PrimaryAction.Content = operation.SlotId == "refactor" ? "Planner available in Phase 5" : $"{operation.Name} — form available in Phase 4";
        PrimaryAction.IsEnabled = false;
        var isBuildMap = operation.Id == "build-map";
        var isTagHandling = tagForms.ContainsKey(operation.Id);
        var isToolForm = toolForms.ContainsKey(operation.Id);
        var isRefactor = operation.SlotId == "refactor";
        RefactorScroll.Visibility = RefactorActions.Visibility = isRefactor ? Visibility.Visible : Visibility.Collapsed;
        PrimaryAction.Visibility = isRefactor ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderScroll.Visibility = isBuildMap || isTagHandling || isToolForm || isRefactor ? Visibility.Collapsed : Visibility.Visible;
        BuildMapScroll.Visibility = isBuildMap ? Visibility.Visible : Visibility.Collapsed;
        BuildMapStatusScroll.Visibility = isBuildMap ? Visibility.Visible : Visibility.Collapsed;
        if (isBuildMap) PrimaryAction.Content = "Build map";
        buildMap.RefreshContext();
        TagHandlingScroll.Visibility = TagHandlingStatusScroll.Visibility = isTagHandling ? Visibility.Visible : Visibility.Collapsed;
        foreach (var form in tagForms.Values) form.RefreshContext();
        if (isTagHandling)
        {
            var form = tagForms[operation.Id];
            if (!ReferenceEquals(TagHandlingHost.Content, form)) { TagHandlingHost.Content = form; TagHandlingStatusHost.Content = form.StatusPanel; }
            PrimaryAction.Content = operation.Id == "repair-tags" ? form.FormState is RepairTagsState { RepairMode: "detect", ModelReferences: "unchanged", StripHiddenData: false } ? "Detect tag issues" : "Run tag actions" : operation.Name;
        }
        ToolFormScroll.Visibility = ToolFormStatusScroll.Visibility = isToolForm ? Visibility.Visible : Visibility.Collapsed;
        if (isToolForm)
        {
            var form = toolForms[operation.Id];
            if (!ReferenceEquals(ToolFormHost.Content, form)) { ToolFormHost.Content = form; ToolFormStatusHost.Content = form.StatusPanel; }
            form.RefreshContext();
            PrimaryAction.Content = operation.Id == "map-crc" ? form.FormState.Get("mode", "inspect") switch { "inspect" => "Inspect CRC", "fix-header" => "Fix map header", _ => "Forge map CRC" } : operation.Name;
        }
        var headerFolder = operation.SlotId is "asset-interchange" or "tag-handling" or "build-map";
        AssetTagFolderColumn.Width = headerFolder || isRefactor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RefactorTagFolderHost.Visibility = isRefactor ? Visibility.Visible : Visibility.Collapsed;
        if (isRefactor) refactorPreview.RefreshProfile(state.SelectedProfile);
        RefactorRefreshButton.IsEnabled = !refactorPreview.IsExecuting;
        RefactorStopButton.IsEnabled = refactorPreview.IsExecuting && !refactorPreview.StopRequested;
        RefactorDryRunButton.IsEnabled = RefactorRunOneButton.IsEnabled = refactorPreview.CanExecute && refactorPreview.HasSelectedCommand;
        RefactorRunAllButton.IsEnabled = refactorPreview.CanExecute;
        AssetTagFolderLabel.Visibility = AssetTagFolderHost.Visibility = headerFolder ? Visibility.Visible : Visibility.Collapsed;
        AssetTagFolderHost.Content = !headerFolder ? null : isBuildMap ? buildMap.TagFolderSelector : isTagHandling ? tagForms[operation.Id].TagFolderSelector : toolForms[operation.Id].TagFolderSelector;
        var profile = state.SelectedProfile;
        ProjectFolderButton.IsEnabled = profile is not null && Directory.Exists(profile.WorkingDirectory);
        try
        {
            _ = EditorLauncher.CreateStartInfo(state.Settings.Value.Installation.Directory, profile);
            EditorButton.IsEnabled = true;
            EditorButton.ToolTip = "Launch invader-edit-qt with this profile's ordered tag roots. The editor runs independently.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            EditorButton.IsEnabled = false; EditorButton.ToolTip = ex.Message;
        }
        ToolTipService.SetShowOnDisabled(EditorButton, true);
        ProfileSummary.Text = profile is null ? "No profile selected. Use Profiles to configure an editing kit." : $"Profile: {profile.Name}\nEditing Kit Folder: {profile.WorkingDirectory}\nData: {profile.Paths.DataRoot}\nTag precedence: {string.Join(" → ", profile.Paths.TagRoots.Select(r => r.Name))}\nMaps: {profile.Paths.MapsRoot}";
        BusyStatus.Text = state.Coordinator.Owner(operation.SlotId) is { } owner ? $"Busy: {owner.Snapshot.OperationName} · {owner.Snapshot.ProfileName}. Open Output Log to view or cancel it." : "Execution slot: idle";
        foreach (var item in state.Navigation) item.Label = item.Name + (state.Coordinator.Owner(item.SlotId) is null ? "" : " · Running");
        var active = state.Coordinator.Runs.Where(r => r.View.Active).ToArray();
        ActivityButton.Content = active.Length == 0 ? "Output Log" : $"Output Log ({active.Length} running)";
        InstallationStatus.Text = state.Scan is { } scan ? $"Invader: {scan.Tools.Count(t => t.Available)} / 22 available" : "Installation not configured";
        RecoveryButton.Visibility = state.StorageErrors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void TaskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return; Render(); state.Settings.Value.Ui.LastPageId = operation.Id; SaveSettings();
    }
    private void OperationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updatingSelector || OperationSelector.SelectedItem is not OperationDefinition selected) return;
        state.Settings.Value.Ui.OperationSelections[selected.SlotId] = selected.Id;
        Render(); state.Settings.Value.Ui.LastPageId = operation.Id; SaveSettings();
    }
    private void SidebarResized(object sender, DragCompletedEventArgs e)
    {
        state.Settings.Value.Ui.SidebarWidth = SidebarColumn.ActualWidth;
        state.Settings.MarkDirty(); SaveSettings();
    }
    private void ProfileSelected(object sender, SelectionChangedEventArgs e) { if (ready) { Render(); SaveSettings(); } }
    private void SaveSettings() => state.Settings.Save();
    private void RefreshRefactor(object sender, RoutedEventArgs e) => refactorPreview.RefreshHierarchy();
    private void StopRefactor(object sender, RoutedEventArgs e) { refactorPreview.RequestStop(); Render(); }
    private async void DryRunRefactor(object sender, RoutedEventArgs e) => await refactorPreview.ExecuteQueueAsync(false, true);
    private async void RunOneRefactor(object sender, RoutedEventArgs e) => await refactorPreview.ExecuteQueueAsync(false);
    private async void RunAllRefactor(object sender, RoutedEventArgs e) => await refactorPreview.ExecuteQueueAsync(true);
    private void ManageProfiles(object sender, RoutedEventArgs e) { buildMap.Flush(); foreach (var form in tagForms.Values) form.Flush(); foreach (var form in toolForms.Values) form.Flush(); new ProfilesWindow(state) { Owner = this }.ShowDialog(); Render(); }
    private void StartOperation(object sender, RoutedEventArgs e) { if (operation.Id == "build-map") buildMap.Start(); else if (tagForms.TryGetValue(operation.Id, out var form)) form.Start(); else if (toolForms.TryGetValue(operation.Id, out var tool)) tool.Start(); }
    private void ManageInstallation(object sender, RoutedEventArgs e) { new InstallationWindow(state) { Owner = this }.ShowDialog(); Render(); }
    private void LaunchEditor(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = EditorLauncher.CreateStartInfo(state.Settings.Value.Installation.Directory, state.SelectedProfile);
            using var editor = Process.Start(start);
            if (editor is null) throw new InvalidOperationException("Windows did not start invader-edit-qt.");
        }
        catch (Exception ex) { DialogUi.Error(this, ex); }
    }
    private void OpenRecovery(object sender, RoutedEventArgs e) { new StorageWindow(state) { Owner = this }.ShowDialog(); Render(); }
    private void OpenProject(object sender, RoutedEventArgs e) => OpenFolder(state.SelectedProfile?.WorkingDirectory);
    private void OpenFolder(string? path)
    {
        if (!Directory.Exists(path)) { MessageBox.Show(this, "Configure an existing folder using Profiles or Invader first."); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(path!) }, UseShellExecute = false }); }
        catch (Exception ex) { DialogUi.Error(this, ex); }
    }
    private void ShowActivity(object sender, RoutedEventArgs e) => OpenActivity(state.Coordinator.Owner(operation.SlotId)?.Snapshot.RunId ?? state.Coordinator.Runs.LastOrDefault(r => r.Snapshot.PageId == operation.Id)?.Snapshot.RunId);
    private void OpenActivity(Guid? runId = null)
        => ShowActivityWindow(runId, true);
    private void OpenActivityInBackground(Guid? runId)
        => ShowActivityWindow(runId, false);
    private void ShowActivityWindow(Guid? runId, bool activate)
    {
        if (activity is null)
        {
            activity = new(state) { Owner = this };
            activity.Closed += (_, _) => { activity = null; state.Settings.Value.Ui.ActivityPaneOpen = false; SaveSettings(); };
        }
        state.Settings.Value.Ui.ActivityPaneOpen = true;
        activity.ShowActivated = activate;
        if (!activity.IsVisible) activity.Show();
        if (activate) activity.Activate();
        activity.Select(runId);
    }
    private void About(object sender, RoutedEventArgs e)
    {
        var dialog = new UtilityWindow
        {
            Owner = this,
            Title = "About Invader GUI",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var content = DialogUi.Column();
        content.Children.Add(DialogUi.Text("Invader GUI\n\nBuild 1.0.0\nInvader Version 0.55.0.r4134.33518109\n\nLicensed under GPLv3\nSource Code Available here:"));
        var link = new Hyperlink(new Run("https://github.com/R93Sniper/InvaderGUI/"))
        {
            NavigateUri = new Uri("https://github.com/R93Sniper/InvaderGUI/"),
            Foreground = System.Windows.Media.Brushes.LightSkyBlue
        };
        link.RequestNavigate += (_, args) =>
        {
            args.Handled = true;
            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { DialogUi.Error(dialog, ex); }
        };
        var source = DialogUi.Text("");
        source.Inlines.Add(link);
        content.Children.Add(source);
        var close = DialogUi.Button("OK", () => dialog.Close());
        close.IsDefault = true;
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.MinWidth = 75;
        content.Children.Add(close);
        dialog.Content = content;
        dialog.ShowDialog();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closing) return; closing = true;
        try
        {
            buildMap.Flush();
            if (refactorPreview.IsExecuting)
            {
                RefactorCloseChoice choice;
                Task<bool> completion;
                using (refactorPreview.DeferQueueAdvance())
                {
                    var dialog = new RefactorCloseDialog(this);
                    dialog.ShowDialog(); choice = dialog.Choice;
                    if (choice == RefactorCloseChoice.Cancel) return;
                    completion = refactorPreview.StopForCloseAsync(choice == RefactorCloseChoice.FinishCommand);
                }
                IsEnabled = false;
                if (choice == RefactorCloseChoice.CancelCommand && await Task.WhenAny(completion, Task.Delay(8000)) != completion)
                { MessageBox.Show(this, "Refactor has not finished stopping and refreshing tags. The application will remain open. No further queued command will start.", "Refactor is stopping"); return; }
                await completion;
                IsEnabled = true;
            }
            foreach (var form in tagForms.Values) { form.CancelPreparation(); form.Flush(); }
            foreach (var form in toolForms.Values) form.Flush();
            var active = state.Coordinator.Runs.Where(r => r.View.Active).ToArray();
            if (active.Length > 0 && MessageBox.Show(this, $"{active.Length} task(s) are running. Request cancellation and close after they stop?\n\nStopping does not undo filesystem changes. Choose No to keep the application open.", "Running tasks", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var discardPlanOnClose = false;
            if (state.PlanGuard.HasUnsavedChanges)
            {
                var choice = MessageBox.Show(this, "Save the unsaved Refactor plan before closing? No discards pending plan edits only.", "Unsaved plan", MessageBoxButton.YesNoCancel);
                if (choice == MessageBoxResult.Cancel || (choice == MessageBoxResult.Yes && !await state.PlanGuard.SaveAsync())) return;
                if (choice == MessageBoxResult.No) discardPlanOnClose = true;
            }
            if (active.Length > 0)
            {
                IsEnabled = false;
                foreach (var run in active) run.Cancel();
                var all = Task.WhenAll(active.Select(r => r.Completion));
                if (await Task.WhenAny(all, Task.Delay(8000)) != all)
                { MessageBox.Show(this, "Some tasks have not stopped. The application will remain open so they can be monitored in Output Log."); return; }
                await all;
            }
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            state.Settings.Value.Ui.SidebarWidth = SidebarColumn.ActualWidth;
            state.Settings.Value.Window = new() { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, State = WindowState == WindowState.Maximized ? "maximized" : "normal" };
            if (!state.SaveAll())
            {
                new StorageWindow(state) { Owner = this }.ShowDialog();
                if (state.Settings.IsDirty || state.Profiles.IsDirty || state.Presets.IsDirty)
                    if (MessageBox.Show(this, "Configuration changes are still unsaved. Close and discard the in-memory changes?", "Unsaved configuration", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            }
            refactorPreview.CancelScan();
            refactorPreview.CancelPendingPlanOperations();
            if (discardPlanOnClose) state.PlanGuard.Discard();
            timer.Stop(); allowClose = true;
            // Defer the final close until WPF has returned from the original Closing event.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        finally { closing = false; if (!allowClose) IsEnabled = true; }
    }
}

