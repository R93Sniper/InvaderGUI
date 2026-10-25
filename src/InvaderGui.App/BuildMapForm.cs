using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InvaderGui.Core;
using Microsoft.Win32;

namespace InvaderGui.App;

public sealed class BuildMapForm : StackPanel
{
    private readonly ShellViewModel shell;
    private readonly Action<Guid?> showActivity;
    private readonly Action<bool> enableStart;
    private readonly List<Action> bind = new();
    private readonly List<Action> enabledBindings = new();
    private readonly TextBlock validation = DialogUi.Text("");
    private readonly TextBlock paths = DialogUi.Text("");
    private readonly TextBlock result = DialogUi.Text("");
    private readonly ComboBox presets = new() { MinWidth = 170, DisplayMemberPath = "Name" };
    private readonly ListBox roots = new() { Height = 85 };
    private readonly StackPanel fields = new();
    private readonly TextBlock stateError = DialogUi.Text("");
    private readonly Button reset;
    private readonly ComboBox addRoot = new() { MinWidth = 180, DisplayMemberPath = "Name" };
    private readonly ComboBox buildRoot = new() { DisplayMemberPath = "Name", ToolTip = "Choose the tag folder to build from. A single folder is used exclusively for scenario and tag lookup. Ordered tag folders uses the list under Editing kit paths and output." };
    private readonly FrameworkElement resourcesPanel, anniversary, stock, compression;
    private BuildMapState model = new();
    private BuildMapValidation? cachedValidation;
    private ProjectProfile? loadedProfile;
    private string? loadError;
    private bool binding;
    private DateTime nextSave = DateTime.MinValue;
    private Guid? selectedPresetId;
    private Action? refreshRenameDefault;
    public StackPanel StatusPanel { get; } = new();
    public StackPanel TagFolderSelector { get; } = new();
    public BuildMapState FormState => BuildMapState.Read(JsonSerializer.SerializeToElement(model, JsonFiles.Options));

    public BuildMapForm(ShellViewModel shell, Action<Guid?> showActivity, Action<bool> enableStart)
    {
        this.shell = shell; this.showActivity = showActivity; this.enableStart = enableStart;
        stateError.Foreground = Brushes.Salmon; Children.Add(stateError);
        reset = DialogUi.Button("Reset saved Build Map form…", ResetSavedState); Children.Add(reset);
        Children.Add(fields);
        var input = Group(fields, "Scenario and target");
        TagFolderSelector.Children.Add(buildRoot);
        buildRoot.SelectionChanged += (_, _) =>
        {
            if (binding || buildRoot.SelectedItem is not BuildRootChoice selected) return;
            model.TagRootIds = selected.Id is null ? null : new() { selected.Id };
            Edited(); BindRoots();
        };
        var scenario = Text(input, "Scenario tag", () => model.Scenario, v => { model.Scenario = v; refreshRenameDefault?.Invoke(); },
            "Select an existing .scenario under a tag root. Logical paths are relative to the ordered roots and omit .scenario.");
        var inputButtons = new WrapPanel(); input.Children.Add(inputButtons);
        inputButtons.Children.Add(DialogUi.Button("Browse scenario…", () => BrowseScenario(scenario)));
        Toggle(inputButtons, "Use filesystem path", () => model.FilesystemPath, v => ChangePathMode(v),
            "Pass the selected physical scenario path using Invader's filesystem interpretation. The file must be beneath an effective tag root.");
        Choice(input, "Game engine (required)", () => model.Engine, v => model.Engine = v,
            new[] { new ChoiceItem("", "Select an engine…") }.Concat(BuildMapBuilder.Engines.Select(e => new ChoiceItem(e.Key, e.Value + " (" + e.Key + ")"))),
            "Choose the engine that will load the map. Engine-specific settings are enabled below.");
        var common = Group(fields, "Build settings");
        var flags = new WrapPanel(); common.Children.Add(flags);
        Toggle(flags, "Optimize tag space", () => model.Optimize, v => model.Optimize = v, "Optimize tag space; this can substantially increase build time.");
        Toggle(flags, "Hide minor warnings", () => model.HideWarnings, v => model.HideWarnings = v, "Suppress pedantic warnings while retaining other diagnostics.");
        Toggle(flags, "Extend file limits", () => model.ExtendLimits, v => model.ExtendLimits = v, "Request a 2 GiB file limit even if the target engine cannot load it.");
        resourcesPanel = Choice(common, "Resource usage", () => model.ResourceUsage ?? "", v => model.ResourceUsage = NullDefault(v),
            Choices(("", "Default (None)"), ("none", "None (explicit)"), ("check", "Check resource maps"), ("always", "Always index (Custom Edition only)")),
            "Default omits the override. Check uses resource maps; Always indexes resource tags and is limited to Custom Edition. Native and Xbox targets do not use resource maps.");
        Choice(common, "Script source", () => model.ScriptSource ?? "", v => model.ScriptSource = NullDefault(v),
            Choices(("", "Default (Tags)"), ("tags", "Tags (explicit)"), ("data", "Data")), "Use embedded script sources from tags or compile disk sources from the data directory.");

        var pathFields = new StackPanel();
        fields.Children.Add(new Expander { Header = "Editing kit paths and output", Foreground = Brushes.WhiteSmoke, Content = pathFields, Margin = new Thickness(0, 8, 0, 8) });
        pathFields.Children.Add(paths);
        Optional(pathFields, "Override data directory", () => model.DataOverride, v => model.DataOverride = v, "Default uses the profile's data directory. Relative overrides resolve beneath the Editing Kit Folder.", "folder");
        Optional(pathFields, "Override maps directory", () => model.MapsOverride, v => model.MapsOverride = v, "Overrides the profile's maps directory. Used for default output and resource lookup when the profile has no resource-map directory.", "folder");
        Optional(pathFields, "Override output map directory", () => model.OutputDirectory, v => model.OutputDirectory = v,
            "Select the folder for the built map. The filename uses the scenario name, or Rename scenario when enabled. Does not change resource-map lookup.", "folder");
        var resourceOverride = Optional(pathFields, "Override resource-map directory",
            () => model.ResourceDirectoryMode == "custom" ? model.ResourceDirectory : null,
            v => { model.ResourceDirectoryMode = v is null ? "profile" : "custom"; if (v is not null) model.ResourceDirectory = v; },
            "Default uses the profile's resource-map directory, or the effective maps directory if it is blank. Enable to select a custom resource-map folder. Applies when Check or Always resource usage is active.", "folder");
        enabledBindings.Add(() => resourceOverride.IsEnabled = BuildMapBuilder.SupportsResources(model.Engine) && model.ResourceUsage is "check" or "always");
        pathFields.Children.Add(DialogUi.Text("Tag roots in precedence order (first match wins)")); pathFields.Children.Add(roots);
        var rootButtons = new WrapPanel(); pathFields.Children.Add(rootButtons); rootButtons.Children.Add(addRoot);
        rootButtons.Children.Add(DialogUi.Button("Add root", () =>
        {
            if (addRoot.SelectedItem is not TagRoot root) return;
            model.TagRootIds ??= loadedProfile?.Paths.TagRoots.Select(r => r.Id).ToList() ?? new();
            if (!model.TagRootIds.Contains(root.Id)) model.TagRootIds.Add(root.Id);
            Edited(); BindRoots();
        }));
        rootButtons.Children.Add(DialogUi.Button("Remove", () =>
        {
            if (roots.SelectedIndex < 0) return;
            model.TagRootIds ??= loadedProfile!.Paths.TagRoots.Select(r => r.Id).ToList();
            model.TagRootIds.RemoveAt(roots.SelectedIndex); Edited(); BindRoots();
        }));
        rootButtons.Children.Add(DialogUi.Button("Up", () => MoveRoot(-1)));
        rootButtons.Children.Add(DialogUi.Button("Down", () => MoveRoot(1)));
        rootButtons.Children.Add(DialogUi.Button("Use profile order", () => { model.TagRootIds = null; Edited(); BindRoots(); }));

        var advanced = new StackPanel(); fields.Children.Add(new Expander { Header = "Additional settings", Foreground = Brushes.WhiteSmoke, Content = advanced, Margin = new Thickness(0, 4, 0, 8) });
        anniversary = Toggle(advanced, "Anniversary graphics and audio (MCC CEA only)", () => model.Anniversary, v => model.Anniversary = v, "Enable Anniversary graphics and audio for MCC CEA. Retained but omitted for other targets.");
        stock = Toggle(advanced, "Stock resource bounds (Custom Edition only)", () => model.StockBounds, v => model.StockBounds = v, "Index only resource tags within stock Custom Edition resource bounds. Retained but omitted for other targets.");
        Toggle(advanced, "Stock network compatibility (auto-forge)", () => model.AutoForge, v => model.AutoForge = v, "Use applicable stock index and CRC defaults. Explicit index/CRC settings remain in effect.");
        Toggle(advanced, "Errors-only output", () => model.Quiet, v => model.Quiet = v, "Ask Invader to emit only errors. Output Log still captures stdout and stderr.");
        Optional(advanced, "Override build string", () => model.BuildString, v => model.BuildString = v, "Fewer than 32 UTF-8 bytes. An enabled empty value explicitly supplies an empty build string.");
        Optional(advanced, "Forge CRC (hexadecimal)", () => model.ForgeCrc, v => model.ForgeCrc = v, "One to eight hexadecimal digits, optionally prefixed with 0x. Zero is a valid explicit value.");
        Optional(advanced, "Rename scenario", () => model.RenameScenario, v => model.RenameScenario = v, "New scenario basename, without a directory or extension; also changes the default output filename.",
            defaultText: () =>
            {
                var basename = Path.GetFileName(model.Scenario.Trim().Replace('/', '\\'));
                return basename.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase) ? basename[..^9] : basename;
            });
        compression = Optional(advanced, "Override Xbox compression level", () => model.CompressionLevel, v => model.CompressionLevel = v, "Integer 0–9; Invader defaults to 9. Retained but omitted for non-Xbox targets.");
        Optional(advanced, "Override tag space", () => model.TagSpace, v => model.TagSpace = v, "Unsigned byte count, uppercase K (KiB), M (MiB), or 0x hexadecimal; for example 64M or 0x4000000. An explicit zero is preserved.");
        Optional(advanced, "Use source index file", () => model.IndexFile, v => model.IndexFile = v, "Use an existing index file to control tag ordering.", "open");

        var presetGroup = new StackPanel(); fields.Children.Add(CompactUi.SavedSettings(presetGroup));
        presetGroup.Children.Add(DialogUi.Text("Current settings save automatically. Use Save as… to name a build setup; select it and Apply to reuse its options. Scenario and folders stay with the profile."));
        var presetRow = new WrapPanel(); presetGroup.Children.Add(presetRow); presetRow.Children.Add(presets);
        presets.SelectionChanged += (_, _) => { if (!binding) selectedPresetId = (presets.SelectedItem as OperationPreset)?.Id; };
        presetRow.Children.Add(DialogUi.Button("Apply", ApplyPreset));
        presetRow.Children.Add(DialogUi.Button("Save", () => SavePreset(false)));
        presetRow.Children.Add(DialogUi.Button("Save as…", () => SavePreset(true)));
        presetRow.Children.Add(DialogUi.Button("Delete", DeletePreset));
        validation.Foreground = Brushes.LightGoldenrodYellow; StatusPanel.Children.Add(validation); StatusPanel.Children.Add(result);
        BindAll();
    }

    private Window Owner => Window.GetWindow(this);
    private static string? NullDefault(string value) => value.Length == 0 ? null : value;
    private sealed record ChoiceItem(string Id, string Label) { public override string ToString() => Label; }
    private sealed record BuildRootChoice(string? Id, string Name);
    private static IEnumerable<ChoiceItem> Choices(params (string Id, string Label)[] choices) => choices.Select(c => new ChoiceItem(c.Id, c.Label));
    private static StackPanel Group(Panel parent, string title)
    {
        var panel = new StackPanel(); parent.Children.Add(new GroupBox { Header = title, Content = panel, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 4) }); return panel;
    }
    private TextBox Text(Panel parent, string label, Func<string> get, Action<string> set, string tip)
    {
        var field = DialogUi.Field(parent, label, get()); field.ToolTip = tip;
        field.TextChanged += (_, _) => { if (!binding) { set(field.Text); Edited(); } };
        bind.Add(() => field.Text = get()); return field;
    }
    private CheckBox Toggle(Panel parent, string label, Func<bool> get, Action<bool> set, string tip)
    {
        var box = new CheckBox { Content = label, ToolTip = tip, Margin = new Thickness(3, 5, 14, 5) }; parent.Children.Add(box);
        box.Click += (_, _) => { if (!binding) { set(box.IsChecked == true); Edited(); } };
        bind.Add(() => box.IsChecked = get()); return box;
    }
    private ComboBox Choice(Panel parent, string label, Func<string> get, Action<string> set, IEnumerable<ChoiceItem> values, string tip)
    {
        parent.Children.Add(DialogUi.Text(label)); var items = values.ToArray();
        var combo = new ComboBox { ItemsSource = items, ToolTip = tip }; parent.Children.Add(combo);
        combo.SelectionChanged += (_, _) => { if (!binding && combo.SelectedItem is ChoiceItem item) { set(item.Id); Edited(); BindEnabled(); } };
        bind.Add(() => combo.SelectedItem = items.FirstOrDefault(c => c.Id == get())); return combo;
    }
    private FrameworkElement Optional(Panel parent, string label, Func<string?> get, Action<string?> set, string tip, string? picker = null, Func<string>? defaultText = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 5) }; parent.Children.Add(panel);
        var enabled = new CheckBox { Content = label, ToolTip = tip, Margin = new Thickness(3, 3, 3, 4) }; panel.Children.Add(enabled);
        var row = new DockPanel(); panel.Children.Add(row);
        var text = new TextBox { ToolTip = tip, MinWidth = 120 };
        if (picker is not null)
        {
            var browse = DialogUi.Button("Browse…", () =>
            {
                if (picker == "folder") PickFolder(text);
                else if (picker == "save")
                {
                    var dialog = new SaveFileDialog { Filter = "Cache map (*.map)|*.map|All files (*.*)|*.*", OverwritePrompt = false, FileName = "" };
                    if (dialog.ShowDialog(Owner) == true) text.Text = dialog.FileName;
                }
                else
                {
                    var dialog = new OpenFileDialog { Filter = "Index files (*.txt)|*.txt|All files (*.*)|*.*" };
                    if (dialog.ShowDialog(Owner) == true) text.Text = dialog.FileName;
                }
            }); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse);
        }
        row.Children.Add(text);
        if (defaultText is not null)
        {
            refreshRenameDefault = () =>
            {
                if (!string.IsNullOrWhiteSpace(get())) return;
                var wasBinding = binding;
                binding = true;
                try
                {
                    text.Text = defaultText();
                    if (enabled.IsChecked == true) set(text.Text);
                }
                finally { binding = wasBinding; }
            };
        }
        enabled.Click += (_, _) => { if (!binding) { row.Visibility = enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; set(enabled.IsChecked == true ? text.Text : null); if (enabled.IsChecked != true && defaultText is not null) text.Text = defaultText(); Edited(); } };
        text.TextChanged += (_, _) => { if (!binding && enabled.IsChecked == true) { set(text.Text); Edited(); } };
        bind.Add(() => { enabled.IsChecked = get() is not null; text.Text = get() ?? defaultText?.Invoke() ?? ""; row.Visibility = get() is not null ? Visibility.Visible : Visibility.Collapsed; });
        return panel;
    }
    private void PickFolder(TextBox field) { var path = DialogUi.PickFolder(Owner, field.Text); if (path is not null) field.Text = path; }
    private void BrowseScenario(TextBox field)
    {
        try
        {
            var effectiveRoots = EffectiveRoots();
            var picker = new OpenFileDialog { Filter = "Scenario tags (*.scenario)|*.scenario", Title = "Select scenario tag" };
            var initial = effectiveRoots.FirstOrDefault(Directory.Exists); if (initial is not null) picker.InitialDirectory = initial;
            if (picker.ShowDialog(Owner) != true) return;
            var logical = BuildMapBuilder.ScenarioFromFile(picker.FileName, effectiveRoots);
            var firstMatch = effectiveRoots.Select(r => Path.Combine(r, logical + ".scenario")).FirstOrDefault(File.Exists);
            if (!string.Equals(firstMatch, picker.FileName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An earlier tag root contains this logical scenario. Reorder the effective roots to target the selected file; filesystem interpretation does not change tag precedence.");
            field.Text = model.FilesystemPath ? picker.FileName : logical;
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
    }
    private string[] EffectiveRoots()
    {
        if (loadedProfile is null) return [];
        var selected = model.TagRootIds ?? loadedProfile.Paths.TagRoots.Select(r => r.Id).ToList();
        return selected.Select(id => loadedProfile.Paths.TagRoots.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException("A saved tag root is missing. Select the effective roots first."))
            .Select(r => Path.GetFullPath(r.Path, loadedProfile.WorkingDirectory)).ToArray();
    }
    private void ChangePathMode(bool filesystem)
    {
        try
        {
            if (model.Scenario.Length > 0)
            {
                if (filesystem)
                {
                    var logical = BuildMapBuilder.NormalizeScenario(model.Scenario);
                    model.Scenario = EffectiveRoots().Select(r => Path.Combine(r, logical + ".scenario")).FirstOrDefault(File.Exists)
                        ?? throw new ArgumentException("Select an existing scenario before changing to filesystem mode.");
                }
                else model.Scenario = BuildMapBuilder.ScenarioFromFile(model.Scenario, EffectiveRoots());
            }
            model.FilesystemPath = filesystem;
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
        BindAll();
    }
    private void MoveRoot(int delta)
    {
        var index = roots.SelectedIndex;
        var selected = model.TagRootIds ?? loadedProfile?.Paths.TagRoots.Select(r => r.Id).ToList() ?? new();
        var next = index + delta; if (index < 0 || next < 0 || next >= selected.Count) return;
        model.TagRootIds = selected;
        (model.TagRootIds[index], model.TagRootIds[next]) = (model.TagRootIds[next], model.TagRootIds[index]);
        Edited(); BindRoots(); roots.SelectedIndex = next;
    }
    private void BindRoots()
    {
        var wasBinding = binding; binding = true;
        try
        {
        var ids = model.TagRootIds ?? loadedProfile?.Paths.TagRoots.Select(r => r.Id).ToList() ?? new();
        roots.ItemsSource = ids.Select((id, i) => $"{i + 1}. {loadedProfile?.Paths.TagRoots.FirstOrDefault(r => r.Id == id)?.ToString() ?? "Missing root: " + id}").ToArray();
        addRoot.ItemsSource = loadedProfile?.Paths.TagRoots; addRoot.SelectedIndex = 0;
        var choices = new List<BuildRootChoice> { new(null, model.TagRootIds is { Count: > 1 } ? "Ordered tag folders (custom order — see paths below)" : "Use profile tag folders (ordered)") };
        choices.AddRange(loadedProfile?.Paths.TagRoots.Select(r => new BuildRootChoice(r.Id, r.ToString())) ?? Enumerable.Empty<BuildRootChoice>());
        var singleId = model.TagRootIds is { Count: 1 } ? model.TagRootIds[0] : null;
        if (singleId is not null && !choices.Any(c => c.Id == singleId)) choices.Add(new(singleId, "Missing tag folder — select another folder"));
        if (model.TagRootIds is { Count: 0 }) choices[0] = new(null, "No tag folders selected — select a folder");
        buildRoot.ItemsSource = choices; buildRoot.SelectedItem = choices.First(c => c.Id == singleId);
        }
        finally { binding = wasBinding; }
    }
    private void BindEnabled()
    {
        resourcesPanel.IsEnabled = BuildMapBuilder.SupportsResources(model.Engine);
        anniversary.IsEnabled = model.Engine == "mcc-cea"; stock.IsEnabled = model.Engine == "gbx-custom";
        compression.IsEnabled = BuildMapBuilder.IsXbox(model.Engine);
        foreach (var update in enabledBindings) update();
    }
    private void BindAll()
    {
        binding = true;
        try { foreach (var update in bind) update(); BindRoots(); RefreshPresets(); BindEnabled(); }
        finally { binding = false; }
    }
    private void Edited()
    {
        if (binding || loadError is not null || loadedProfile is null) return;
        cachedValidation = null;
        loadedProfile.OperationState["build-map"] = JsonSerializer.SerializeToElement(model, JsonFiles.Options);
        shell.Profiles.MarkDirty(); nextSave = DateTime.UtcNow.AddMilliseconds(700);
        RefreshValidation();
    }
    public void Flush()
    {
        if (shell.Profiles.IsDirty) shell.Profiles.Save();
        nextSave = DateTime.UtcNow.AddSeconds(2);
    }
    public void RefreshContext(bool revalidate = false)
    {
        if (revalidate) cachedValidation = null;
        if (!ReferenceEquals(loadedProfile, shell.SelectedProfile))
        {
            cachedValidation = null;
            Flush(); loadedProfile = shell.SelectedProfile; model = new(); loadError = null;
            if (loadedProfile?.OperationState.TryGetValue("build-map", out var saved) == true)
            {
                try { model = BuildMapState.Read(saved); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) { loadError = ex.Message; }
            }
            BindAll();
        }
        if (shell.Profiles.IsDirty && DateTime.UtcNow >= nextSave) Flush();
        fields.IsEnabled = loadedProfile is not null && loadError is null;
        stateError.Text = loadError is null ? "" : "Saved Build Map form could not be loaded. Original values are preserved. " + loadError;
        stateError.Visibility = loadError is null ? Visibility.Collapsed : Visibility.Visible;
        reset.Visibility = loadError is null ? Visibility.Collapsed : Visibility.Visible;
        paths.Text = loadedProfile is null ? "Select a profile in Settings." : $"Editing Kit Folder: {loadedProfile.WorkingDirectory}\nProfile data: {loadedProfile.Paths.DataRoot}\nProfile maps: {loadedProfile.Paths.MapsRoot}\nProfile resources: {loadedProfile.Paths.EffectiveResourceMapsRoot}\n{(model.TagRootIds is null ? "Using profile tag-root order." : "Using the selected tag-root order for Build Map.")}";
        RefreshValidation();
    }
    private TaskRun? LastRun() => shell.Coordinator.Runs.LastOrDefault(r => r.Snapshot.PageId == "build-map" && r.Snapshot.ProfileId == loadedProfile?.Id);
    private RunView? LastHistory() => shell.History.FirstOrDefault(r => r.Snapshot.PageId == "build-map" && r.Snapshot.ProfileId == loadedProfile?.Id);
    private void RefreshValidation()
    {
        var check = cachedValidation ??= BuildMapBuilder.Build(model, loadedProfile);
        var operation = Operations.All.Single(o => o.Id == "build-map");
        var available = shell.Scan?.For(operation).Available == true;
        var busy = shell.Coordinator.Owner("build-map");
        validation.Text = loadError is not null ? "Repair or reset the saved form before building." :
            busy is not null ? $"Busy: {busy.Snapshot.OperationName} · {busy.Snapshot.ProfileName}. Open Output Log to monitor or cancel." :
            !available ? "Build Map is unavailable. Use Invader to select a directory containing invader-build.exe." :
            check.Errors.Count > 0 ? string.Join("\n", check.Errors) : "Ready. Output: " + check.Invocation!.OutputFile +
            (check.Invocation.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", check.Invocation.Warnings));
        var last = LastRun()?.View ?? LastHistory();
        result.Text = last is null ? "No Build Map run for this profile yet." : $"Last build: {last.Status.ToDisplayText()} · Exit: {last.ExitCode?.ToString() ?? "—"} · {last.StartedUtc.LocalDateTime:g}";
        result.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        enableStart(loadError is null && available && busy is null && check.Invocation is not null);
    }
    public void Start()
    {
        try
        {
            RefreshContext();
            var check = cachedValidation = BuildMapBuilder.Build(model, loadedProfile);
            if (loadError is not null) throw new InvalidOperationException("Repair or reset the saved Build Map form first.");
            if (check.Invocation is not { } invocation) throw new ArgumentException(string.Join("\n", check.Errors));
            if (shell.Scan is null) throw new InvalidOperationException("Configure the Invader installation first.");
            Flush();
            var snapshot = TaskLaunchSnapshot.Create(Operations.All.Single(o => o.Id == "build-map"), shell.Scan,
                invocation.EffectiveProfile, invocation.Arguments, invocation.Summary, shell.LogsDirectory, options: invocation.Options);
            if (!shell.Coordinator.TryStart(snapshot, out var run)) throw new InvalidOperationException("Build Map is already running. Open Output Log to monitor it.");
            showActivity(run!.Snapshot.RunId); RefreshValidation();
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
    }
    private void RefreshPresets()
    {
        var previous = binding; binding = true;
        presets.ItemsSource = shell.Presets.Value.Presets.Where(p => p.OperationId == "build-map").ToArray();
        presets.SelectedItem = shell.Presets.Value.Presets.FirstOrDefault(p => p.Id == selectedPresetId);
        binding = previous;
    }
    private void ApplyPreset()
    {
        if (presets.SelectedItem is not OperationPreset preset) return;
        try { model = model.ApplyPreset(preset); BindAll(); Edited(); }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
    }
    private void SavePreset(bool saveAs)
    {
        var errors = BuildMapBuilder.OptionErrors(model); if (errors.Count > 0) { MessageBox.Show(Owner, string.Join("\n", errors), "Preset settings"); return; }
        var preset = presets.SelectedItem as OperationPreset;
        if (saveAs || preset is null)
        {
            var dialog = new UtilityWindow { Title = "Save Build Map preset", Width = 400, Height = 175, Owner = Owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var panel = DialogUi.Column(); var name = DialogUi.Field(panel, "Preset name", "");
            panel.Children.Add(DialogUi.Button("Save", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text)) return;
                if (shell.Presets.Value.Presets.Any(p => p.OperationId == "build-map" && string.Equals(p.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                { MessageBox.Show(dialog, "A Build Map preset already uses that name."); return; }
                dialog.DialogResult = true;
            })); dialog.Content = panel;
            if (dialog.ShowDialog() != true) return;
            preset = new OperationPreset { Name = name.Text.Trim(), OperationId = "build-map" }; shell.Presets.Value.Presets.Add(preset);
        }
        preset.Settings = model.PresetSettings(); selectedPresetId = preset.Id; shell.Presets.MarkDirty();
        if (!shell.Presets.Save()) new StorageWindow(shell) { Owner = Owner }.ShowDialog();
        RefreshPresets();
    }
    private void DeletePreset()
    {
        if (presets.SelectedItem is not OperationPreset preset || MessageBox.Show(Owner, $"Delete preset '{preset.Name}'?", "Delete preset", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        shell.Presets.Value.Presets.Remove(preset); selectedPresetId = null; shell.Presets.MarkDirty();
        if (!shell.Presets.Save()) new StorageWindow(shell) { Owner = Owner }.ShowDialog(); RefreshPresets();
    }
    private void ResetSavedState()
    {
        if (loadedProfile is null || MessageBox.Show(Owner, "Reset this profile's saved Build Map form? The original form will be retained under a recovery key in the profiles document.", "Reset form", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (loadedProfile.OperationState.TryGetValue("build-map", out var original)) loadedProfile.OperationState["build-map-recovery-" + Guid.NewGuid().ToString("N")] = original.Clone();
        loadError = null; model = new(); BindAll(); Edited(); Flush(); RefreshContext();
    }
}
