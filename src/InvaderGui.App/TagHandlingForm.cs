using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InvaderGui.Core;
using Microsoft.Win32;

namespace InvaderGui.App;

public sealed class TagHandlingForm : StackPanel
{
    private readonly ShellViewModel shell;
    private readonly Action<Guid?> showActivity;
    private readonly Action<bool> enableStart;
    private readonly List<Action> applicability = new();
    private readonly TextBlock validation = DialogUi.Text("");
    private readonly TextBlock result = DialogUi.Text("");
    private TagHandlingState model;
    private ProjectProfile? loadedProfile;
    private TagHandlingValidation? cachedValidation;
    private bool building;
    private bool preparing;
    private bool preparationAbandoned;
    private string? loadError;
    private Guid? selectedPresetId;
    private DateTime nextSave = DateTime.MaxValue;
    public string OperationId { get; }
    public StackPanel StatusPanel { get; } = new();
    public StackPanel TagFolderSelector { get; } = new();
    public TagHandlingState FormState => TagHandlingStates.Read(OperationId, TagHandlingStates.Write(model));
    private Window Owner => Window.GetWindow(this);

    public TagHandlingForm(string operationId, ShellViewModel shell, Action<Guid?> showActivity, Action<bool> enableStart)
    {
        OperationId = operationId; this.shell = shell; this.showActivity = showActivity; this.enableStart = enableStart;
        model = TagHandlingStates.New(operationId);
        validation.Foreground = Brushes.LightGoldenrodYellow; StatusPanel.Children.Add(validation); StatusPanel.Children.Add(result);
        BuildUi();
    }
    private void BuildUi()
    {
        building = true;
        try
        {
            Children.Clear(); TagFolderSelector.Children.Clear(); applicability.Clear();
            IsEnabled = loadedProfile is not null;
            var operation = Operations.All.Single(o => o.Id == OperationId);
            if (loadError is not null)
            {
                var error = DialogUi.Text("Saved form could not be loaded. The original is preserved. " + loadError); error.Foreground = Brushes.Salmon; Children.Add(error);
                Children.Add(DialogUi.Button("Reset saved form…", Reset)); return;
            }
            var rootPicker = new ComboBox { DisplayMemberPath = "Label", ToolTip = "Choose which profile tag folder this operation uses. Dependency searches can also use ordered folders." };
            var choices = new List<RootChoice>();
            if (model is DependencyState) choices.Add(new(null, "Use ordered tag folders"));
            choices.AddRange(loadedProfile?.Paths.TagRoots.Select(r => new RootChoice(r.Id, r.ToString())) ?? Enumerable.Empty<RootChoice>());
            string? selectedId = model is DependencyState d ? d.TagRootIds is { Count: 1 } ? d.TagRootIds[0] : null : model.SelectedTagRootId;
            if (selectedId is not null && !choices.Any(c => c.Id == selectedId)) choices.Add(new(selectedId, "Missing saved tag folder — select a replacement"));
            rootPicker.ItemsSource = choices; rootPicker.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedId); TagFolderSelector.Children.Add(rootPicker);
            rootPicker.SelectionChanged += (_, _) =>
            {
                if (building || rootPicker.SelectedItem is not RootChoice selected) return;
                if (model is DependencyState dep) dep.TagRootIds = selected.Id is null ? null : new() { selected.Id };
                else model.SelectedTagRootId = selected.Id;
                Edited(); if (model is DependencyState) BuildUi();
            };
            var input = new StackPanel();
            var scope = model is BatchTagState ? new GroupBox { Header = "Scope", Content = input, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 4) } : null;
            if (scope is null) Children.Add(input);
            if (model is DependencyState dependency) DependencyRoots(input, dependency);
            if (model is BatchTagState batch)
            {
                Choice(input, "Scope", batch.Batch ? "batch" : "single", new[] { ("single", "Single tag"), ("batch", "Batch") }, value => batch.Batch = value == "batch", "Batch uses include/exclude patterns; Single tag uses the tag input below.");
            }
            var single = new StackPanel(); input.Children.Add(single);
            var tag = Text(single, model is ConvertTagsState ? "Source tag (source-group extension optional)" : "Tag (including group extension)", model.Input, value => model.Input = value,
                "Logical tag path, for example levels\\test\\bloodgulch\\bloodgulch.sky. Browse maps a file beneath the selected tag folder(s). Reverse dependency queries may name a missing logical tag.");
            single.Children.Add(CompactUi.BrowseRow(tag, "Browse…", () => BrowseTag(tag)));
            if (model is not ConvertTagsState)
                Toggle(single, "Use filesystem path", model.FilesystemPath, ChangePathMode, "Interpret the input as a physical tag file beneath the selected tag folder(s). Disabled in Batch mode.");
            applicability.Add(() => single.Visibility = model is BatchTagState { Batch: true } ? Visibility.Collapsed : Visibility.Visible);
            if (model is BatchTagState filters)
            {
                var batchPanel = Group(input, "Batch patterns");
                batchPanel.Children.Add(DialogUi.Text("Patterns use * and ?. Includes are ORed; exclusions win. Exclusions alone are valid. Single-tag input is ignored in Batch mode."));
                batchPanel.Children.Add(DialogUi.Button("All tags", () => { filters.Includes = new() { "*" }; Edited(); BuildUi(); }));
                Rows(batchPanel, "Include patterns", filters.Includes);
                Rows(batchPanel, "Exclude patterns", filters.Excludes);
                applicability.Add(() => ((GroupBox)batchPanel.Parent).Visibility = filters.Batch ? Visibility.Visible : Visibility.Collapsed);
            }
            // Explicit operation controls: no reflection or automatic CLI-generated forms.
            switch (model)
            {
                case RepairTagsState repair:
                    var repairPanel = Group(this, "Bludgeon repairs");
                    repairPanel.Children.Add(DialogUi.Text("Choose the Bludgeon step below. Reference normalization and Strip cleanup are separate steps and can modify tags even when Bludgeon is set to Detect only."));
                    Choice(repairPanel, "Action", repair.RepairMode,
                        new[] { ("detect", "Detect only"), ("selected", "Selected repairs"), ("everything", "Everything"), ("skip", "Skip Bludgeon (references / cleanup only)") }, value => repair.RepairMode = value,
                        "Detect emits no repair types. Everything requests all supported repairs. Choose Selected repairs for a specific checklist.");
                    var checklist = new WrapPanel(); repairPanel.Children.Add(checklist);
                    foreach (var type in TagHandlingBuilder.Repairs)
                    {
                        var current = type;
                        Toggle(checklist, Friendly(type), repair.Repairs.Contains(type), enabled =>
                        {
                            if (enabled && !repair.Repairs.Contains(current)) repair.Repairs.Add(current);
                            else if (!enabled) repair.Repairs.Remove(current);
                        }, RepairTip(type));
                    }
                    applicability.Add(() => checklist.Visibility = repair.RepairMode == "selected" ? Visibility.Visible : Visibility.Collapsed);
                    var threadsPanel = new StackPanel(); repairPanel.Children.Add(threadsPanel);
                    OptionalText(threadsPanel, "Override batch threads", repair.Threads, value => repair.Threads = value,
                        "Unchecked uses the CPU thread count. Enter a whole number of at least 1. Used only by Bludgeon in Batch mode; retained when Bludgeon is skipped.");
                    Choice(repairPanel, "Model references", repair.ModelReferences,
                        new[] { ("unchanged", "Leave references unchanged"), ("model-to-gbxmodel", "Change model references to gbxmodel (PC / MCC)"), ("gbxmodel-to-model", "Change gbxmodel references to model (Xbox)") }, value => repair.ModelReferences = value,
                        "Run invader-refactor in references-only mode. Changes reference classes using existing target tags; model files are not converted or moved. Single and batch filters scope which tags are edited.");
                    Toggle(repairPanel, "Strip hidden data after repair / reference changes", repair.StripHiddenData, value => repair.StripHiddenData = value,
                        "Run invader-strip last using the same tag folder and scope. Changed tags are rewritten; unchanged tags may be reported as Skipped.");
                    repairPanel.Children.Add(DialogUi.Text("Order: Bludgeon → reference changes → Strip (only enabled steps run). A failed or canceled step stops later steps; completed changes are not undone."));
                    break;
                case ConvertTagsState convert:
                    var conversion = Group(this, "Conversion");
                    Choice(conversion, "From group", convert.FromGroup,
                        new[] { ("", "Select source group…") }.Concat(TagHandlingBuilder.ConversionGroups.Select(g => (g, g))), value => { convert.FromGroup = value; },
                        "Supported conversions: model ↔ gbxmodel, extended Chicago shader → Chicago shader, or different object groups.");
                    Choice(conversion, "To group", convert.ToGroup,
                        new[] { ("", "Select destination group…") }.Concat(TagHandlingBuilder.ConversionGroups.Select(g => (g, g))), value => convert.ToGroup = value,
                        "The selected conversion must be a supported pair and cannot convert a group to itself.");
                    OptionalText(conversion, "Override destination tag folder", convert.OutputTags, value => convert.OutputTags = value,
                        "Unchecked writes into the selected source tag folder. An override must be an existing folder; relative paths resolve beneath the Editing Kit Folder.", true);
                    Toggle(conversion, "Overwrite destination tags", convert.Overwrite, value => convert.Overwrite = value,
                        "Allow Invader to replace existing destination tags; otherwise existing outputs are skipped.");
                    break;
                case StripTagsState:
                    Children.Add(DialogUi.Text("Strip rewrites tags whose hidden data can be removed. An unchanged single tag may be reported as Skipped with a nonzero exit; inspect the raw output."));
                    break;
                case DependencyState dep:
                    var directions = Group(this, "Dependency search");
                    Choice(directions, "Direction", dep.Reverse ? "reverse" : "forward",
                        new[] { ("forward", "Dependencies of this tag"), ("reverse", "Tags referencing this tag") }, value => dep.Reverse = value == "reverse",
                        "Reverse logical-path searches can query references to a tag that does not currently exist.");
                    Toggle(directions, "Recursive search", dep.Recursive, value => dep.Recursive = value, "Follow dependencies recursively. Output retains broken-reference markers.");
                    break;
            }
            if (scope is not null) Children.Add(scope);
            Children.Add(CompactUi.SavedSettings(new TagPresetBar(OperationId, shell, () => model, value => { model = value; Edited(); BuildUi(); }, selectedPresetId, id => selectedPresetId = id)));
            IsEnabled = loadedProfile is not null;
        }
        finally { building = false; ApplyEnabled(); }
    }
    private sealed record RootChoice(string? Id, string Label);
    private static string Friendly(string value) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' '));
    private static string RepairTip(string type) => type switch
    {
        "invalid-reference-classes" => "Repair tag references using an invalid tag class. Useful for incompatible reference-class values; Invader reports which references it can repair.",
        "invalid-uppercase-references" => "Normalize uppercase tag-reference paths.",
        "missing-script-source" => "Recover embedded script source where supported.",
        "missing-vertices" => "Regenerate missing compressed or uncompressed vertices.",
        _ => "Apply the documented " + type + " repair. Invader reports the detected and repaired results."
    };
    private static StackPanel Group(Panel parent, string title)
    {
        var content = new StackPanel(); parent.Children.Add(new GroupBox { Header = title, Content = content, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 4) }); return content;
    }
    private TextBox Text(Panel panel, string label, string value, Action<string> set, string tip)
    {
        var field = DialogUi.Field(panel, label, value); field.ToolTip = tip;
        field.TextChanged += (_, _) => { if (!building) { set(field.Text); Edited(); } }; return field;
    }
    private void Choice(Panel panel, string label, string value, IEnumerable<(string Id, string Label)> choices, Action<string> set, string tip)
    {
        panel.Children.Add(DialogUi.Text(label));
        var items = choices.Select(c => new RootChoice(c.Id, c.Label)).ToArray();
        var control = new ComboBox { ItemsSource = items, DisplayMemberPath = "Label", ToolTip = tip, SelectedItem = items.FirstOrDefault(c => c.Id == value) };
        panel.Children.Add(control);
        control.SelectionChanged += (_, _) => { if (!building && control.SelectedItem is RootChoice choice) { set(choice.Id!); Edited(); } };
    }
    private void Toggle(Panel panel, string label, bool value, Action<bool> set, string tip)
    {
        var control = new CheckBox { Content = label, IsChecked = value, ToolTip = tip, Margin = new Thickness(3, 5, 15, 5) }; panel.Children.Add(control);
        control.Click += (_, _) => { if (!building) { set(control.IsChecked == true); Edited(); } };
    }
    private void OptionalText(Panel panel, string label, string? value, Action<string?> set, string tip, bool folder = false)
    {
        var enabled = new CheckBox { Content = label, IsChecked = value is not null, ToolTip = tip, Margin = new Thickness(3, 5, 3, 4) }; panel.Children.Add(enabled);
        var row = new DockPanel { Visibility = value is not null ? Visibility.Visible : Visibility.Collapsed }; panel.Children.Add(row);
        var field = new TextBox { Text = value ?? "", ToolTip = tip };
        if (folder)
        {
            var browse = DialogUi.Button("Browse…", () => { var path = DialogUi.PickFolder(Owner, field.Text); if (path is not null) field.Text = path; });
            DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse);
        }
        row.Children.Add(field);
        enabled.Click += (_, _) => { row.Visibility = enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; set(enabled.IsChecked == true ? field.Text : null); Edited(); };
        field.TextChanged += (_, _) => { if (!building && enabled.IsChecked == true) { set(field.Text); Edited(); } };
    }
    private void Rows(Panel panel, string label, List<string> values)
    {
        panel.Children.Add(DialogUi.Text(label));
        for (int i = 0; i < values.Count; i++)
        {
            var index = i; var row = new DockPanel(); panel.Children.Add(row);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            actions.Children.Add(DialogUi.Button("Remove", () => { values.RemoveAt(index); Edited(); BuildUi(); }));
            actions.Children.Add(DialogUi.Button("↑", () => { if (index == 0) return; (values[index], values[index - 1]) = (values[index - 1], values[index]); Edited(); BuildUi(); }));
            actions.Children.Add(DialogUi.Button("↓", () => { if (index + 1 >= values.Count) return; (values[index], values[index + 1]) = (values[index + 1], values[index]); Edited(); BuildUi(); }));
            var field = new TextBox { Text = values[index], ToolTip = "Invader wildcard pattern (* and ?). Empty active rows are invalid." }; row.Children.Add(field);
            field.TextChanged += (_, _) => { if (!building) { values[index] = field.Text; Edited(); } };
        }
        panel.Children.Add(DialogUi.Button("Add " + label.ToLowerInvariant(), () => { values.Add(""); Edited(); BuildUi(); }));
    }
    private void DependencyRoots(Panel panel, DependencyState state)
    {
        var content = new StackPanel(); panel.Children.Add(new Expander { Header = "Ordered search folders", Foreground = Brushes.WhiteSmoke, Content = content });
        var ids = state.TagRootIds ?? loadedProfile?.Paths.TagRoots.Select(r => r.Id).ToList() ?? new();
        var list = new ListBox { Height = 80, ItemsSource = ids.Select(id => loadedProfile?.Paths.TagRoots.FirstOrDefault(r => r.Id == id)?.ToString() ?? "Missing folder: " + id).ToArray() }; content.Children.Add(list);
        var picker = new ComboBox { ItemsSource = loadedProfile?.Paths.TagRoots, DisplayMemberPath = "Name", SelectedIndex = 0 }; content.Children.Add(picker);
        var actions = new WrapPanel(); content.Children.Add(actions);
        void Capture() => state.TagRootIds = ids.ToList();
        actions.Children.Add(DialogUi.Button("Add folder", () => { if (picker.SelectedItem is TagRoot root && !ids.Contains(root.Id)) { ids.Add(root.Id); Capture(); Edited(); BuildUi(); } }));
        actions.Children.Add(DialogUi.Button("Remove folder", () => { if (list.SelectedIndex < 0) return; ids.RemoveAt(list.SelectedIndex); Capture(); Edited(); BuildUi(); }));
        void Move(int delta) { var index = list.SelectedIndex; var next = index + delta; if (index < 0 || next < 0 || next >= ids.Count) return; (ids[index], ids[next]) = (ids[next], ids[index]); Capture(); Edited(); BuildUi(); }
        actions.Children.Add(DialogUi.Button("Up", () => Move(-1))); actions.Children.Add(DialogUi.Button("Down", () => Move(1)));
        actions.Children.Add(DialogUi.Button("Use profile order", () => { state.TagRootIds = null; Edited(); BuildUi(); }));
    }
    private string[] RootPaths() => loadedProfile is null ? [] : TagHandlingBuilder.SelectRoots(model, loadedProfile).Select(r => Path.GetFullPath(r.Path, loadedProfile.WorkingDirectory)).ToArray();
    private void BrowseTag(TextBox field)
    {
        try
        {
            var roots = RootPaths();
            var filter = model is ConvertTagsState c && !string.IsNullOrEmpty(c.FromGroup) ? $"Source tags (*.{c.FromGroup})|*.{c.FromGroup}|All files (*.*)|*.*" : "Tag files (*.*)|*.*";
            var dialog = new OpenFileDialog { Filter = filter, Title = "Select tag", InitialDirectory = roots.FirstOrDefault(Directory.Exists) ?? "" };
            if (dialog.ShowDialog(Owner) == true) field.Text = model.FilesystemPath && model is not ConvertTagsState ? dialog.FileName : TagHandlingBuilder.LogicalFromFile(dialog.FileName, roots);
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
    }
    private void ChangePathMode(bool physical)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(model.Input)) model.Input = physical
                ? RootPaths().Select(r => Path.Combine(r, TagHandlingBuilder.NormalizeLogical(model.Input))).FirstOrDefault(File.Exists) ?? throw new ArgumentException("Choose an existing tag before switching to filesystem mode.")
                : TagHandlingBuilder.LogicalFromFile(Path.GetFullPath(model.Input, loadedProfile!.WorkingDirectory), RootPaths());
            model.FilesystemPath = physical;
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
        BuildUi();
    }
    private void ApplyEnabled() { foreach (var apply in applicability) apply(); }
    private void Edited()
    {
        if (building || loadedProfile is null || loadError is not null) return;
        cachedValidation = null;
        loadedProfile.OperationState[OperationId] = TagHandlingStates.Write(model); shell.Profiles.MarkDirty(); nextSave = DateTime.UtcNow.AddMilliseconds(700);
        ApplyEnabled(); RefreshValidation();
    }
    public void Flush() { if (shell.Profiles.IsDirty) shell.Profiles.Save(); nextSave = DateTime.UtcNow.AddSeconds(2); }
    public void CancelPreparation() => preparationAbandoned = true;
    public void RefreshContext(bool revalidate = false)
    {
        if (revalidate) cachedValidation = null;
        if (!ReferenceEquals(loadedProfile, shell.SelectedProfile))
        {
            cachedValidation = null;
            Flush(); loadedProfile = shell.SelectedProfile; model = TagHandlingStates.New(OperationId); loadError = null;
            if (loadedProfile?.OperationState.TryGetValue(OperationId, out var saved) == true)
            {
                try { model = TagHandlingStates.Read(OperationId, saved); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) { loadError = ex.Message; }
            }
            var initializeRoot = loadError is null && model is not DependencyState && model.SelectedTagRootId is null && loadedProfile is not null;
            if (initializeRoot)
                model.SelectedTagRootId = loadedProfile!.Paths.TagRoots.FirstOrDefault()?.Id;
            BuildUi();
            if (initializeRoot) Edited();
        }
        if (shell.Profiles.IsDirty && DateTime.UtcNow >= nextSave) Flush();
        RefreshValidation();
    }
    private TaskRun? LastRun() => shell.Coordinator.Runs.LastOrDefault(r => r.Snapshot.PageId == OperationId && r.Snapshot.ProfileId == loadedProfile?.Id);
    private RunView? LastHistory() => shell.History.FirstOrDefault(r => r.Snapshot.PageId == OperationId && r.Snapshot.ProfileId == loadedProfile?.Id);
    private void RefreshValidation()
    {
        var check = cachedValidation ??= TagHandlingBuilder.Build(OperationId, model, loadedProfile);
        var operation = Operations.All.Single(o => o.Id == OperationId);
        var unavailable = model is RepairTagsState repair ? RepairWorkflow.AvailabilityErrors(repair, shell.Scan) :
            shell.Scan?.For(operation).Available == true ? Array.Empty<string>() : new[] { $"Unavailable: {operation.Executable}. Use Invader to select its installation." };
        var available = unavailable.Count == 0;
        var busy = shell.Coordinator.Owner("tag-handling");
        validation.Text = loadError is not null ? "Reset or repair the saved form before starting." : preparing ? "Preparing the selected tag scope…" : busy is not null ? $"Busy: {busy.Snapshot.OperationName} · {busy.Snapshot.ProfileName}. Open Output Log to monitor or cancel it." :
            !available ? string.Join("\n", unavailable) : check.Errors.Count > 0 ? string.Join("\n", check.Errors) :
            "Ready. " + check.Invocation!.Summary;
        var last = LastRun()?.View ?? LastHistory();
        result.Text = last is null ? "No run for this operation and profile yet." : $"Last run: {last.Status.ToDisplayText()} · Exit: {last.ExitCode?.ToString() ?? "—"}. Inspect output for per-tag results.";
        result.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        if (last?.Diagnostic is not null) result.Text += "\n" + last.Diagnostic;
        enableStart(!preparing && loadError is null && busy is null && available && check.Invocation is not null);
    }
    public async void Start()
    {
        if (preparing) return;
        preparationAbandoned = false;
        try
        {
            RefreshContext(); if (loadError is not null) throw new InvalidOperationException("Repair or reset the saved form first.");
            var check = cachedValidation = TagHandlingBuilder.Build(OperationId, model, loadedProfile);
            if (check.Invocation is not { } invocation) throw new ArgumentException(string.Join("\n", check.Errors));
            if (shell.Scan is null) throw new InvalidOperationException("Select an Invader installation.");
            // Store even an untouched initial root choice as a stable identity before launching.
            Edited(); Flush();
            TaskLaunchSnapshot snapshot;
            if (model is RepairTagsState)
            {
                var captured = (RepairTagsState)FormState; var profile = loadedProfile!.Copy(); var scan = shell.Scan; var logs = shell.LogsDirectory;
                if (captured.ModelReferences != "unchanged" && captured.Batch && (captured.Excludes.Count > 0 || !captured.Includes.Contains("*")))
                {
                    preparing = true; RefreshValidation();
                    snapshot = await Task.Run(() => RepairWorkflow.Create(captured, profile, scan, logs));
                    if (preparationAbandoned) return;
                }
                else snapshot = RepairWorkflow.Create(captured, profile, scan, logs);
            }
            else snapshot = TaskLaunchSnapshot.Create(Operations.All.Single(o => o.Id == OperationId), shell.Scan, invocation.EffectiveProfile,
                invocation.Arguments, invocation.Summary, shell.LogsDirectory, options: invocation.Options);
            if (!shell.Coordinator.TryStart(snapshot, out var run)) throw new InvalidOperationException("Tag Handling is already running another operation. Open Output Log.");
            showActivity(run!.Snapshot.RunId); RefreshValidation();
        }
        catch (Exception ex) { if (!preparationAbandoned) DialogUi.Error(Owner, ex); }
        finally { preparing = false; RefreshValidation(); }
    }
    private void Reset()
    {
        if (loadedProfile is null || MessageBox.Show(Owner, "Reset this saved form? The original will remain under a recovery key in the profiles document.", "Reset saved form", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (loadedProfile.OperationState.TryGetValue(OperationId, out var saved)) loadedProfile.OperationState[OperationId + "-recovery-" + Guid.NewGuid().ToString("N")] = saved.Clone();
        loadError = null; model = TagHandlingStates.New(OperationId); model.SelectedTagRootId = loadedProfile.Paths.TagRoots.FirstOrDefault()?.Id;
        Edited(); Flush(); BuildUi();
    }
}

internal sealed class TagPresetBar : GroupBox
{
    public TagPresetBar(string operation, ShellViewModel shell, Func<TagHandlingState> get, Action<TagHandlingState> apply, Guid? selectedId, Action<Guid?> selected)
    {
        BorderThickness = new Thickness(0); Padding = new Thickness(0); Margin = new Thickness(0);
        var panel = new StackPanel(); Content = panel;
        panel.Children.Add(DialogUi.Text("Current settings save automatically. Use Save as… to name an option set; select it and Apply to reuse it. Inputs and folders stay with the profile."));
        var row = new WrapPanel(); panel.Children.Add(row);
        var picker = new ComboBox { MinWidth = 170, DisplayMemberPath = "Name" }; row.Children.Add(picker);
        bool refreshing = false;
        picker.SelectionChanged += (_, _) => { if (!refreshing) selected((picker.SelectedItem as OperationPreset)?.Id); };
        void Refresh(Guid? id = null) { refreshing = true; picker.ItemsSource = shell.Presets.Value.Presets.Where(p => p.OperationId == operation).ToArray(); picker.SelectedItem = shell.Presets.Value.Presets.FirstOrDefault(p => p.Id == id && p.OperationId == operation); refreshing = false; selected((picker.SelectedItem as OperationPreset)?.Id); }
        Window Owner() => Window.GetWindow(this);
        void Persist() { shell.Presets.MarkDirty(); if (!shell.Presets.Save()) new StorageWindow(shell) { Owner = Owner() }.ShowDialog(); }
        row.Children.Add(DialogUi.Button("Apply", () =>
        {
            if (picker.SelectedItem is not OperationPreset preset) return;
            try { apply(TagHandlingStates.ApplyPreset(operation, get(), preset)); } catch (Exception ex) { DialogUi.Error(Owner(), ex); }
        }));
        void Save(bool saveAs)
        {
            try
            {
                var errors = TagHandlingBuilder.OptionErrors(get()); if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
                var preset = picker.SelectedItem as OperationPreset;
                if (saveAs || preset is null)
                {
                    var dialog = new UtilityWindow { Title = "Save tag operation preset", Width = 410, Height = 180, Owner = Owner(), WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    var content = DialogUi.Column(); var name = DialogUi.Field(content, "Preset name", "");
                    content.Children.Add(DialogUi.Button("Save", () =>
                    {
                        if (string.IsNullOrWhiteSpace(name.Text)) return;
                        if (shell.Presets.Value.Presets.Any(p => p.OperationId == operation && string.Equals(p.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(dialog, "This operation already has a preset with that name."); return; }
                        dialog.DialogResult = true;
                    })); dialog.Content = content;
                    if (dialog.ShowDialog() != true) return;
                    preset = new() { OperationId = operation, Name = name.Text.Trim() }; shell.Presets.Value.Presets.Add(preset);
                }
                preset.Settings = TagHandlingStates.PresetSettings(operation, get()); Persist(); Refresh(preset.Id);
            }
            catch (Exception ex) { DialogUi.Error(Owner(), ex); }
        }
        row.Children.Add(DialogUi.Button("Save", () => Save(false))); row.Children.Add(DialogUi.Button("Save as…", () => Save(true)));
        row.Children.Add(DialogUi.Button("Delete", () =>
        {
            if (picker.SelectedItem is OperationPreset preset && MessageBox.Show(Owner(), $"Delete preset '{preset.Name}'?", "Delete preset", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            { shell.Presets.Value.Presets.Remove(preset); Persist(); Refresh(); }
        })); Refresh(selectedId);
    }
}
