using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InvaderGui.Core;
using Microsoft.Win32;

namespace InvaderGui.App;

public sealed class ToolOperationForm : StackPanel
{
    private readonly ShellViewModel shell;
    private readonly Action<Guid?> showActivity;
    private readonly Action<bool> enableStart;
    private readonly List<Action> applicability = new();
    private readonly TextBlock validation = DialogUi.Text("");
    private readonly TextBlock result = DialogUi.Text("");
    private ProjectProfile? loadedProfile;
    private ToolFormState model;
    private bool building;
    private string? loadError;
    private DateTime nextSave = DateTime.MaxValue;
    private Guid? selectedPreset;
    public string OperationId { get; }
    public StackPanel StatusPanel { get; } = new();
    public StackPanel TagFolderSelector { get; } = new();
    private FrameworkElement? folderOrder;
    public ToolFormState FormState => ToolFormState.Read(model.Write());
    private Window Owner => Window.GetWindow(this);

    public ToolOperationForm(string operation, ShellViewModel shell, Action<Guid?> showActivity, Action<bool> enableStart)
    {
        OperationId = operation; this.shell = shell; this.showActivity = showActivity; this.enableStart = enableStart;
        model = ToolForms.New(operation); validation.Foreground = Brushes.LightGoldenrodYellow;
        StatusPanel.Children.Add(validation); StatusPanel.Children.Add(result);
        BuildUi();
    }
    private static StackPanel Group(Panel parent, string name)
    {
        var content = new StackPanel(); parent.Children.Add(new GroupBox { Header = name, Content = content, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 4) }); return content;
    }
    private sealed record Choice(string Id, string Label);
    private ComboBox Select(Panel panel, string label, IEnumerable<Choice> choices, string current, Action<string> set, string tip)
    {
        if (label.Length > 0) panel.Children.Add(DialogUi.Text(label)); var items = choices.ToArray();
        var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = "Label", SelectedItem = items.FirstOrDefault(i => i.Id == current), ToolTip = tip, IsTextSearchEnabled = true };
        panel.Children.Add(combo);
        combo.SelectionChanged += (_, _) => { if (!building && combo.SelectedItem is Choice choice) { set(choice.Id); Edited(); } };
        return combo;
    }
    private CheckBox Toggle(Panel panel, string label, bool value, Action<bool> set, string tip)
    {
        var control = new CheckBox { Content = label, IsChecked = value, ToolTip = tip, Margin = new Thickness(3, 5, 3, 5) }; panel.Children.Add(control);
        control.Click += (_, _) => { if (!building) { set(control.IsChecked == true); Edited(); } }; return control;
    }
    private TextBox Field(Panel panel, string label, string value, Action<string> set, string tip)
    {
        var field = DialogUi.Field(panel, label, value); field.ToolTip = tip;
        field.TextChanged += (_, _) => { if (!building) { set(field.Text); Edited(); } }; return field;
    }
    private TextBox PathField(Panel panel, string label, string value, Action<string> set, string kind, string tip, string filter = "All files (*.*)|*.*", Func<string>? currentKind = null)
    {
        panel.Children.Add(DialogUi.Text(label)); var row = new DockPanel(); panel.Children.Add(row);
        var field = new TextBox { Text = value, ToolTip = tip };
        var browse = DialogUi.Button("Browse…", () =>
        {
            var path = Pick(currentKind?.Invoke() ?? kind, field.Text, filter); if (path is not null) field.Text = path;
        }); DockPanel.SetDock(browse, Dock.Left); row.Children.Add(browse); row.Children.Add(field);
        field.TextChanged += (_, _) => { if (!building) { set(field.Text); Edited(); } }; return field;
    }
    private string? Pick(string kind, string initial, string filter = "All files (*.*)|*.*")
    {
        if (kind == "folder") return DialogUi.PickFolder(Owner, initial);
        FileDialog dialog = kind == "save" ? new SaveFileDialog { OverwritePrompt = true } : new OpenFileDialog { CheckFileExists = true };
        dialog.Filter = filter;
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        else if (File.Exists(initial)) { dialog.InitialDirectory = Path.GetDirectoryName(initial); dialog.FileName = Path.GetFileName(initial); }
        else if (Directory.Exists(loadedProfile?.WorkingDirectory)) dialog.InitialDirectory = loadedProfile!.WorkingDirectory;
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
    private void OptionalPath(Panel panel, string label, string? value, Action<string?> set, Func<string> kind, string tip, Func<bool>? active = null)
    {
        var wrapper = new StackPanel(); panel.Children.Add(wrapper); var row = new StackPanel();
        var toggle = Toggle(wrapper, label, value is not null, enabled => { row.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed; set(enabled ? DescendantsText(row).Text : null); }, tip);
        wrapper.Children.Add(row); row.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        PathField(row, "Value", value ?? "", text => { if (toggle.IsChecked == true) set(text); }, "open", tip, currentKind: kind);
        if (active is not null) applicability.Add(() => wrapper.Visibility = active() ? Visibility.Visible : Visibility.Collapsed);
    }
    private static TextBox DescendantsText(Panel panel) => panel.Children.OfType<TextBox>().FirstOrDefault() ?? panel.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<TextBox>()).Single();
    private void BuildUi()
    {
        building = true;
        try
        {
            Children.Clear(); TagFolderSelector.Children.Clear(); folderOrder = null; applicability.Clear(); IsEnabled = loadedProfile is not null;
            var more = new StackPanel();
            StackPanel? scopedInput = null;
            if (loadError is not null)
            {
                Children.Add(DialogUi.Text("Saved form could not be loaded. Original values are preserved. " + loadError));
                Children.Add(DialogUi.Button("Reset saved form…", Reset)); return;
            }
            if (ToolForms.HasTags(OperationId)) Roots();
            if (OperationId is not ("compare" or "resource-maps"))
            {
                var input = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                if (OperationId == "recover-source") scopedInput = input; else Children.Add(input);
                var field = Field(input, InputLabel(), model.Input, v => model.Input = v,
                    ToolForms.IsMap(OperationId) ? "Existing map file. Relative paths resolve beneath the Editing Kit Folder." : "Logical path beneath the selected folder. Browse derives the correct path and extension for this operation.");
                input.Children.Add(CompactUi.BrowseRow(field, "Browse…", () => BrowseInput(field)));
                if (ToolForms.SupportsFilesystem(OperationId))
                    Toggle(input, "Use filesystem path", model.FilesystemPath, ChangePathMode, "Interpret the input as an existing physical file under its applicable folder. Bitmap, Font and Sound use a tag file; Collection and Strings use a data source file; Model also accepts a data asset folder.");
                if (OperationId == "recover-source") applicability.Add(() => input.Visibility = model.Get("scope", "single") == "single" ? Visibility.Visible : Visibility.Collapsed);
                if (OperationId == "lightmap-meshes")
                {
                    var bsp = Field(input, "BSP name", model.SecondaryInput, v => model.SecondaryInput = v, "BSP basename, without its group extension. The mesh path appears in Ready status.");
                    input.Children.Add(CompactUi.BrowseRow(bsp, "Browse BSP…", () => { var path = Pick("open", "", "BSP tags (*.scenario_structure_bsp)|*.scenario_structure_bsp"); if (path is not null) bsp.Text = Path.GetFileNameWithoutExtension(path); }));
                }
            }
            if (OperationId == "compare") ComparisonPanels();
            var options = Group(this, "Operation settings");
            if (ToolForms.Fields[OperationId].Length == 0) ((GroupBox)options.Parent).Visibility = Visibility.Collapsed;
            var optionColumns = CompactUi.Options(); options.Children.Add(optionColumns);
            var additionalColumns = CompactUi.Options();
            var hasAdditional = false;
            foreach (var option in ToolForms.Fields[OperationId])
            {
                if (option.Key == "scope") continue;
                var additional = OperationId == "import-bitmaps" && option.Key is not ("source" or "format" or "type" or "usage") ||
                    OperationId == "import-sounds" && option.Key is "threads" or "compression" or "quality" or "bitrate";
                Option(additional ? additionalColumns : optionColumns, option); hasAdditional |= additional;
            }
            if (hasAdditional) options.Children.Add(new Expander { Header = "Additional settings", Content = additionalColumns, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 3, 0, 2) });
            if (OperationId == "archive-tags")
            {
                OptionalPath(this, "Override output file / copy folder", model.Output, v => model.Output = v,
                    () => model.Get("mode", "archive") == "copy" ? "folder" : "save", "Unchecked uses the target basename and selected archive format, or a basename folder in Copy mode.");
                Rows(Group(this, "Exclude tags present in folders"), model.ExclusionRoots, "folder");
                Rows(Group(this, "Exclude functionally matched tags"), model.MatchedExclusionRoots, "folder");
            }
            if (OperationId == "map-index") PathField(this, "Output index file", model.Output ?? "", v => model.Output = v, "save", "Writes or replaces this file. It must differ from the input map.");
            if (OperationId == "extract-tags")
                OptionalPath(this, "Override resource-map folder", model.ResourceDirectory, v => model.ResourceDirectory = v, () => "folder", "Unchecked searches the input map's folder. Choose resources belonging to the input map.", () => !model.On("ignore-resources"));
            StackPanel? scope = null;
            if (OperationId == "recover-source")
            {
                scope = Group(this, "Scope");
                Option(scope, ToolForms.Fields[OperationId].Single(option => option.Key == "scope"));
                if (scopedInput is not null) scope.Children.Add(scopedInput);
            }
            if (OperationId is "extract-tags" or "compare" or "recover-source")
            {
                var patterns = Group(scope ?? (Panel)this, "Patterns");
                patterns.Children.Add(DialogUi.Text("Patterns use * and ?. Includes are ORed; exclusions win. Compare/Extract with no patterns consider all tags."));
                if (OperationId == "recover-source")
                {
                    patterns.Children.Add(DialogUi.Button("All tags", () => { model.Includes = ["*"]; Edited(); BuildUi(); }));
                    applicability.Add(() => ((GroupBox)patterns.Parent).Visibility = model.Get("scope", "single") == "batch" ? Visibility.Visible : Visibility.Collapsed);
                }
                Rows(Group(patterns, "Include patterns"), model.Includes); Rows(Group(patterns, "Exclude patterns"), model.Excludes);
            }
            if (OperationId == "compile-scripts")
            {
                var explicitSources = Group(this, "Explicit script source names");
                explicitSources.Children.Add(DialogUi.Text("Names in data/<scenario parent>/scripts, without .hsc. An empty list uses Invader's normal source selection; global scripts are included unless excluded."));
                Rows(explicitSources, model.ScriptSources);
                explicitSources.Children.Add(DialogUi.Button("Add script file…", () => AddScript()));
                applicability.Add(() => ((GroupBox)explicitSources.Parent).Visibility = model.Get("mode", "disk") == "disk" ? Visibility.Visible : Visibility.Collapsed);
            }
            if (OperationId == "resource-maps") ResourcePanels();
            if (ToolForms.HasData(OperationId) || OperationId == "resource-maps")
            {
                var paths = new StackPanel(); more.Children.Add(new Expander { Header = "Profile path overrides", Content = paths, Foreground = Brushes.WhiteSmoke });
                paths.Children.Add(DialogUi.Text("Unchecked uses this profile's configured folder. Overrides stay with this operation and profile."));
                if (ToolForms.HasData(OperationId)) OptionalPath(paths, "Override data folder", model.DataOverride, v => model.DataOverride = v, () => "folder", "Source or recovered-data destination, according to the operation. Relative paths resolve beneath the Editing Kit Folder.");
                if (OperationId == "resource-maps") OptionalPath(paths, "Override output maps folder", model.MapsOverride, v => model.MapsOverride = v, () => "folder", "Resource maps are written here; the folder must already exist.");
            }
            if (folderOrder is not null) more.Children.Add(folderOrder);
            var help = OperationId switch
            {
                "import-strings" => "String lists read .txt; HUD messages read .hmt with a 16-bit BOM. Input encoding is preserved; conversion diagnostics appear in Output Log.",
                "import-fonts" => "Source: data/<logical path>.ttf or .otf. Output: selected tag folder/<logical path>.font; generation may replace an existing tag.",
                "import-bitmaps" => "Image sources: data/<logical path>.tif, .tiff, .png, .tga or .bmp. Embedded color plate mode uses an existing bitmap tag instead. Omitted settings retain existing tag values.",
                "import-sounds" => "Sources: data/<logical path>/*.wav, *.wave or *.flac, or pitch-range subfolders. A new sound requires a class. Generated tags may replace existing tags.",
                "import-models" => "Select the asset folder, containing models/*.jms. Output: first tag folder/<asset>/<asset basename>.<type>.",
                "lightmap-meshes" => "Mesh: data/<scenario parent>/lightmaps/<BSP name>.lightmap_mesh. Import modifies tags after external baking.",
                "map-crc" => "Inspect is read-only. Fix header and Forge modify the selected map. Invader does not support Xbox maps for this operation.", _ => null
            };
            if (help is not null) more.Children.Add(DialogUi.Text(help));
            more.Children.Add(CompactUi.SavedSettings(new ToolPresetBar(OperationId, shell, () => model, value => { model = value; Edited(); BuildUi(); }, selectedPreset, id => selectedPreset = id)));
            Children.Add(new Expander { Header = "More options", Content = more, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 3, 0, 2), ToolTip = "Path overrides, folder ordering, input help and optional saved settings. Current settings save automatically." });
        }
        finally { building = false; ApplyEnabled(); }
    }
    private string InputLabel() => OperationId switch
    {
        "archive-tags" => "Scenario or tag logical path", "compile-scripts" or "lightmap-meshes" => "Scenario logical path",
        "import-models" => "Asset logical directory", "import-sounds" => "Sound logical path / source directory",
        "recover-source" => "Tag logical path (including group)", "tag-collection" => "Collection logical path",
        _ => ToolForms.IsMap(OperationId) ? "Map file" : "Asset logical path (extension optional)"
    };
    private static string Label(string id) => id switch
    {
        "" => "Default / retain existing", "single-tag" => "Single tag", "embedded-color-plate" => "Embedded color plate", "gbxmodel" => "gbxmodel (PC / MCC)",
        "model" => "model (Xbox)", "fix-header" => "Fix header", "no-split" => "Do not split", "disk" => "Compile disk sources", "embedded" => "Recompile embedded sources", "clear" => "Clear script data",
        _ => BuildMapBuilder.Engines.TryGetValue(id, out var engine) ? engine : id.Replace('_', ' ').Replace('-', ' ')
    };
    private void Option(Panel panel, ToolOption option)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 6, 2) }; panel.Children.Add(row);
        if (option.Kind == "flag") Toggle(row, option.Label, model.On(option.Key), value => { if (value) model.Options[option.Key] = "true"; else model.Options.Remove(option.Key); }, option.Tip);
        else if (option.Kind == "choice")
        {
            var required = option.Key == "type" && OperationId is "resource-maps" or "import-strings" or "import-models" || option.Key == "engine" && OperationId is "resource-maps" or "archive-tags";
            var items = new[] { new Choice("", option.Default.Length == 0 ? required ? "Select…" : "Default / retain existing" : "Use default (" + Label(option.Default) + ")") }.Concat(option.Choices.Select(c => new Choice(c, Label(c))));
            Select(row, option.Label, items, model.Get(option.Key), value => { if (value.Length == 0) model.Options.Remove(option.Key); else model.Options[option.Key] = value; }, option.Tip);
        }
        else if (option.Kind == "hex")
            Field(row, option.Label, model.Get(option.Key), value => model.Options[option.Key] = value, option.Tip);
        else
        {
            var valuePanel = new StackPanel();
            var toggle = Toggle(row, "Override " + option.Label, model.Options.ContainsKey(option.Key), on =>
            {
                valuePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) model.Options[option.Key] = valuePanel.Children.OfType<TextBox>().Single().Text; else model.Options.Remove(option.Key);
            }, option.Tip);
            row.Children.Add(valuePanel); valuePanel.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Field(valuePanel, option.Label, model.Get(option.Key), value => { if (toggle.IsChecked == true) model.Options[option.Key] = value; }, option.Tip);
        }
        applicability.Add(() => row.Visibility = option.Active(model) ? Visibility.Visible : Visibility.Collapsed);
    }
    private void Rows(Panel panel, List<string> values, string? browseKind = null)
    {
        for (int i = 0; i < values.Count; i++)
        {
            var index = i; var row = new DockPanel(); panel.Children.Add(row);
            var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
            buttons.Children.Add(DialogUi.Button("Remove", () => { values.RemoveAt(index); Edited(); BuildUi(); }));
            buttons.Children.Add(DialogUi.Button("↑", () => Move(values, index, -1))); buttons.Children.Add(DialogUi.Button("↓", () => Move(values, index, 1)));
            var field = new TextBox { Text = values[i] }; row.Children.Add(field);
            field.TextChanged += (_, _) => { if (!building) { values[index] = field.Text; Edited(); } };
            if (browseKind is not null) buttons.Children.Add(DialogUi.Button("Browse…", () => { var path = Pick(browseKind, field.Text); if (path is not null) field.Text = path; }));
        }
        panel.Children.Add(DialogUi.Button("Add row", () => { values.Add(""); Edited(); BuildUi(); }));
    }
    private void Move<T>(List<T> values, int index, int delta)
    {
        var next = index + delta; if (next < 0 || next >= values.Count) return;
        (values[index], values[next]) = (values[next], values[index]); Edited(); BuildUi();
    }
    private void Roots()
    {
        var headerFolder = Operations.All.Single(o => o.Id == OperationId).SlotId is "asset-interchange" or "tag-handling" or "build-map";
        var pickerPanel = headerFolder ? TagFolderSelector : new StackPanel();
        if (!headerFolder) Children.Add(pickerPanel);
        var roots = loadedProfile?.Paths.TagRoots ?? new(); var multiple = ToolForms.MultipleRoots(OperationId);
        var choices = roots.Select(r => new Choice(r.Id, r.ToString())).ToList();
        if (multiple) choices.Insert(0, new("", "Use ordered profile folders"));
        var current = model.TagRootIds is null ? "" : model.TagRootIds.Count == 1 ? model.TagRootIds[0] : "@custom-order";
        if (multiple && current == "@custom-order") choices.Insert(1, new(current, "Use custom folder order below"));
        if (current.Length > 0 && !choices.Any(c => c.Id == current)) choices.Add(new(current, "Missing saved tag folder — choose a replacement"));
        Select(pickerPanel, headerFolder ? "" : "Tag folder", choices, current, value => { if (value != "@custom-order") model.TagRootIds = value.Length == 0 ? null : [value]; BuildUi(); }, "Select the source/destination tag folder. Ordered folders use first-match precedence; creation writes to the first folder.");
        if (!multiple) return;
        var panel = new StackPanel();
        folderOrder = new Expander { Header = "Folder order (advanced)", Content = panel, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 3, 0, 2) };
        var ids = model.TagRootIds?.ToList() ?? roots.Select(r => r.Id).ToList();
        var list = new ListBox { Height = 85, ItemsSource = ids.Select(id => roots.FirstOrDefault(r => r.Id == id)?.ToString() ?? "Missing folder: " + id).ToArray() }; panel.Children.Add(list);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        buttons.Children.Add(DialogUi.Button("↑", () => { if (list.SelectedIndex > 0) { model.TagRootIds = ids; Move(ids, list.SelectedIndex, -1); } }));
        buttons.Children.Add(DialogUi.Button("↓", () => { if (list.SelectedIndex >= 0 && list.SelectedIndex + 1 < ids.Count) { model.TagRootIds = ids; Move(ids, list.SelectedIndex, 1); } }));
        buttons.Children.Add(DialogUi.Button("Remove", () => { if (list.SelectedIndex < 0) return; ids.RemoveAt(list.SelectedIndex); model.TagRootIds = ids; Edited(); BuildUi(); }));
        var picker = new ComboBox { ItemsSource = roots, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 140 }; buttons.Children.Add(picker);
        buttons.Children.Add(DialogUi.Button("Add folder", () => { if (picker.SelectedItem is TagRoot root && !ids.Contains(root.Id)) { ids.Add(root.Id); model.TagRootIds = ids; Edited(); BuildUi(); } }));
    }
    private void ComparisonPanels()
    {
        var panel = Group(this, "Ordered comparison inputs");
        for (int i = 0; i < model.ComparisonInputs.Count; i++)
        {
            var index = i; var input = model.ComparisonInputs[i]; var group = Group(panel, "Input " + (i + 1));
            Select(group, "Input type", [new("map", "Map"), new("tags", "Tag folders")], input.Kind, value => { input.Kind = value; BuildUi(); }, "Each input is either one map or an ordered list of tag folders.");
            if (input.Kind == "map")
            {
                PathField(group, "Map file", input.Map, v => input.Map = v, "open", "Existing map for this input.", "Maps (*.map)|*.map|All files (*.*)|*.*");
                Toggle(group, "Ignore resources for this map", input.IgnoreResources, value => { input.IgnoreResources = value; BuildUi(); }, "Do not load external resources for this input.");
                OptionalPath(group, "Override resources for this map", input.ResourceDirectory, v => input.ResourceDirectory = v, () => "folder", "Unchecked searches this map's parent directory. Choose the resources used by this particular map.", () => !input.IgnoreResources);
            }
            else Rows(group, input.TagDirectories, "folder");
            var actions = new WrapPanel(); group.Children.Add(actions);
            actions.Children.Add(DialogUi.Button("Remove input", () => { model.ComparisonInputs.RemoveAt(index); Edited(); BuildUi(); }));
            actions.Children.Add(DialogUi.Button("Move input ↑", () => Move(model.ComparisonInputs, index, -1))); actions.Children.Add(DialogUi.Button("Move input ↓", () => Move(model.ComparisonInputs, index, 1)));
        }
        panel.Children.Add(DialogUi.Button("Add map input", () => { model.ComparisonInputs.Add(new()); Edited(); BuildUi(); }));
        panel.Children.Add(DialogUi.Button("Add tag-folder input", () => { model.ComparisonInputs.Add(new() { Kind = "tags", TagDirectories = SelectedRoots().ToList() }); Edited(); BuildUi(); }));
    }
    private void ResourcePanels()
    {
        OptionalPath(this, "Concatenate existing resource map", model.Concatenate, v => model.Concatenate = v, () => "open", "Reuse a resource map. Not applicable to localization resources.", () => model.Get("type") != "loc");
        var panel = Group(this, "Ordered map and index sources");
        panel.Children.Add(DialogUi.Text("Map and index rows run in this order. Demo, retail and MCC require at least one row. Output uses the resource type's filename in the maps folder."));
        for (int i = 0; i < model.ResourceSources.Count; i++)
        {
            var index = i; var source = model.ResourceSources[i]; var row = Group(panel, "Source " + (i + 1));
            Select(row, "Source type", [new("map", "Map"), new("index", "Index file")], source.Kind, value => source.Kind = value, "Map and index sources may be interleaved.");
            PathField(row, "Source file", source.Path, value => source.Path = value, "open", "Existing source map or index file.");
            var buttons = new WrapPanel(); row.Children.Add(buttons);
            buttons.Children.Add(DialogUi.Button("Remove source", () => { model.ResourceSources.RemoveAt(index); Edited(); BuildUi(); }));
            buttons.Children.Add(DialogUi.Button("Move source ↑", () => Move(model.ResourceSources, index, -1))); buttons.Children.Add(DialogUi.Button("Move source ↓", () => Move(model.ResourceSources, index, 1)));
        }
        panel.Children.Add(DialogUi.Button("Add map source", () => { model.ResourceSources.Add(new()); Edited(); BuildUi(); }));
        panel.Children.Add(DialogUi.Button("Add index source", () => { model.ResourceSources.Add(new() { Kind = "index" }); Edited(); BuildUi(); }));
    }
    private string[] SelectedRoots()
    {
        if (loadedProfile is null) return [];
        var roots = model.TagRootIds is null ? loadedProfile.Paths.TagRoots : model.TagRootIds.Select(id => loadedProfile.Paths.TagRoots.SingleOrDefault(r => r.Id == id) ?? throw new ArgumentException("Select a replacement for the missing tag folder."));
        return roots.Select(r => Path.GetFullPath(r.Path, loadedProfile.WorkingDirectory)).ToArray();
    }
    private string DataRoot() => loadedProfile is null ? "" : Path.GetFullPath(model.DataOverride ?? loadedProfile.Paths.DataRoot, loadedProfile.WorkingDirectory);
    private string ExpectedExtension() => OperationId switch
    {
        "compile-scripts" or "lightmap-meshes" => ".scenario", "archive-tags" => model.Get("target", "scenario") == "scenario" ? ".scenario" : "",
        "import-bitmaps" => ".bitmap", "import-fonts" => ".font", "import-sounds" => ".sound", "tag-collection" => ".txt",
        "import-strings" => model.Get("type") == "hud_message_text" ? ".hmt" : ".txt", _ => ""
    };
    private void ChangePathMode(bool physical)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(model.Input))
            {
                var data = OperationId is "tag-collection" or "import-strings" or "import-models";
                var roots = data ? [DataRoot()] : SelectedRoots();
                if (physical)
                {
                    var logical = TagHandlingBuilder.NormalizeLogical(model.Input); var ext = ExpectedExtension();
                    if (ext.Length > 0 && !logical.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) logical += ext;
                    model.Input = roots.Select(r => Path.Combine(r, logical)).FirstOrDefault(File.Exists) ?? Path.Combine(roots[0], logical);
                }
                else
                {
                    var full = Path.GetFullPath(model.Input, loadedProfile!.WorkingDirectory);
                    if (OperationId == "import-models" && File.Exists(full)) { var logical = TagHandlingBuilder.LogicalFromFile(full, SelectedRoots()); model.Input = Path.GetDirectoryName(logical) ?? ""; }
                    else { model.Input = TagHandlingBuilder.LogicalFromFile(full, roots); var ext = ExpectedExtension(); if (ext.Length > 0 && model.Input.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) model.Input = model.Input[..^ext.Length]; }
                }
            }
            model.FilesystemPath = physical; BuildUi();
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); BuildUi(); }
    }
    private void BrowseInput(TextBox field)
    {
        try
        {
            if (ToolForms.IsMap(OperationId)) { var path = Pick("open", model.Input, "Maps (*.map)|*.map|All files (*.*)|*.*"); if (path is not null) field.Text = path; return; }
            var dataSource = OperationId is "tag-collection" or "import-strings" or "import-models" || OperationId is "import-bitmaps" or "import-fonts" or "import-sounds" && !model.FilesystemPath && !(OperationId == "import-bitmaps" && model.Get("source", "image") == "embedded-color-plate");
            if (OperationId == "import-models" && model.FilesystemPath) dataSource = false;
            var roots = dataSource ? [DataRoot()] : SelectedRoots();
            var folder = dataSource && OperationId is "import-models" or "import-sounds";
            var ext = ExpectedExtension();
            var filter = dataSource ? OperationId switch
            {
                "import-bitmaps" => "Source images|*.tif;*.tiff;*.png;*.tga;*.bmp", "import-fonts" => "Font sources|*.ttf;*.otf", _ => "Source files|*.txt;*.hmt|All files|*.*"
            } : ext.Length > 0 ? $"Tags (*{ext})|*{ext}|All files|*.*" : "Tag files|*.*";
            var selected = Pick(folder ? "folder" : "open", roots.FirstOrDefault(Directory.Exists) ?? "", filter); if (selected is null) return;
            if (model.FilesystemPath) field.Text = selected;
            else
            {
                var logical = TagHandlingBuilder.LogicalFromFile(selected, roots);
                if (dataSource && !folder || !dataSource && ext.Length > 0) logical = logical[..^Path.GetExtension(logical).Length];
                field.Text = logical;
            }
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
    }
    private void AddScript()
    {
        try
        {
            var logical = model.FilesystemPath ? TagHandlingBuilder.LogicalFromFile(Path.GetFullPath(model.Input, loadedProfile!.WorkingDirectory), SelectedRoots()) : TagHandlingBuilder.NormalizeLogical(model.Input);
            var directory = Path.Combine(DataRoot(), Path.GetDirectoryName(logical) ?? "", "scripts");
            var selected = Pick("open", directory, "Halo scripts (*.hsc)|*.hsc"); if (selected is null) return;
            if (!string.Equals(Path.GetDirectoryName(selected), directory, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a file in this scenario's scripts folder.");
            model.ScriptSources.Add(Path.GetFileNameWithoutExtension(selected)); Edited(); BuildUi();
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
    }
    private void ApplyEnabled() { foreach (var apply in applicability) apply(); }
    private void Edited()
    {
        if (building || loadedProfile is null || loadError is not null) return;
        loadedProfile.OperationState[OperationId] = model.Write(); shell.Profiles.MarkDirty(); nextSave = DateTime.UtcNow.AddMilliseconds(700);
        ApplyEnabled(); RefreshValidation();
    }
    public void Flush() { if (shell.Profiles.IsDirty) shell.Profiles.Save(); nextSave = DateTime.MaxValue; }
    public void RefreshContext()
    {
        if (!ReferenceEquals(loadedProfile, shell.SelectedProfile))
        {
            Flush(); loadedProfile = shell.SelectedProfile; model = ToolForms.New(OperationId); loadError = null;
            if (loadedProfile?.OperationState.TryGetValue(OperationId, out var saved) == true)
            {
                try { model = ToolFormState.Read(saved); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) { loadError = ex.Message; }
            }
            if (loadedProfile is not null && ToolForms.HasTags(OperationId) && !ToolForms.MultipleRoots(OperationId) && model.TagRootIds is null)
                model.TagRootIds = loadedProfile.Paths.TagRoots.Take(1).Select(r => r.Id).ToList();
            BuildUi();
        }
        if (shell.Profiles.IsDirty && DateTime.UtcNow >= nextSave) Flush();
        RefreshValidation();
    }
    private TaskRun? LastRun() => shell.Coordinator.Runs.LastOrDefault(r => r.Snapshot.PageId == OperationId && r.Snapshot.ProfileId == loadedProfile?.Id);
    private RunView? LastHistory() => shell.History.FirstOrDefault(r => r.Snapshot.PageId == OperationId && r.Snapshot.ProfileId == loadedProfile?.Id);
    private void RefreshValidation()
    {
        var operation = Operations.All.Single(o => o.Id == OperationId); var check = ToolForms.Build(OperationId, model, loadedProfile);
        var available = shell.Scan?.For(operation).Available == true; var busy = shell.Coordinator.Owner(operation.SlotId);
        validation.Text = loadError is not null ? "Reset or repair the saved form before starting." : busy is not null ? $"Busy: {busy.Snapshot.OperationName} · {busy.Snapshot.ProfileName}. Open Output Log to monitor or cancel." :
            !available ? $"Unavailable: {operation.Executable}. Use Invader to select its installation." : check.Invocation is null ? string.Join("\n", check.Errors) : "Ready. " + check.Invocation.Summary;
        var last = LastRun()?.View ?? LastHistory(); result.Text = last is null ? "No run for this operation and profile yet." : $"Last run: {last.Status.ToDisplayText()} · Exit: {last.ExitCode?.ToString() ?? "—"}. Inspect Output Log for output." + (last.Diagnostic is null ? "" : "\n" + last.Diagnostic);
        result.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        enableStart(loadError is null && busy is null && available && check.Invocation is not null);
    }
    public void Start()
    {
        try
        {
            RefreshContext(); if (loadError is not null) throw new InvalidOperationException("Reset or repair the saved form first.");
            var check = ToolForms.Build(OperationId, model, loadedProfile); if (check.Invocation is not { } invocation) throw new ArgumentException(string.Join("\n", check.Errors));
            if (shell.Scan is null) throw new InvalidOperationException("Select an Invader installation.");
            Edited(); Flush();
            var operation = Operations.All.Single(o => o.Id == OperationId);
            var snapshot = TaskLaunchSnapshot.Create(operation, shell.Scan, invocation.EffectiveProfile, invocation.Arguments, invocation.Summary, shell.LogsDirectory, options: invocation.Options);
            if (!shell.Coordinator.TryStart(snapshot, out var run)) throw new InvalidOperationException("This tool tab is already running. Open Output Log to monitor or cancel it.");
            showActivity(run!.Snapshot.RunId);
            RefreshValidation();
        }
        catch (Exception ex) { DialogUi.Error(Owner, ex); }
    }
    private void Reset()
    {
        if (loadedProfile is null || MessageBox.Show(Owner, "Reset this operation's saved form? The original form is preserved under a recovery key.", "Reset saved form", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (loadedProfile.OperationState.TryGetValue(OperationId, out var saved)) loadedProfile.OperationState[OperationId + ".recovery." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")] = saved;
        model = ToolForms.New(OperationId);
        if (ToolForms.HasTags(OperationId) && !ToolForms.MultipleRoots(OperationId)) model.TagRootIds = loadedProfile.Paths.TagRoots.Take(1).Select(r => r.Id).ToList();
        loadError = null; Edited(); Flush(); BuildUi();
    }
}

internal sealed class ToolPresetBar : GroupBox
{
    public ToolPresetBar(string operation, ShellViewModel shell, Func<ToolFormState> get, Action<ToolFormState> apply, Guid? selectedId, Action<Guid?> selected)
    {
        BorderThickness = new Thickness(0); Padding = new Thickness(0); Margin = new Thickness(0);
        var panel = new StackPanel(); Content = panel; panel.Children.Add(DialogUi.Text("Current settings save automatically. Use Save as… to name an option set; select it and Apply to reuse it. Inputs and folders stay with the profile."));
        var row = new WrapPanel(); panel.Children.Add(row); var picker = new ComboBox { MinWidth = 170, DisplayMemberPath = "Name" }; row.Children.Add(picker);
        void Refresh(Guid? id = null) { picker.ItemsSource = shell.Presets.Value.Presets.Where(p => p.OperationId == operation).ToArray(); picker.SelectedItem = shell.Presets.Value.Presets.FirstOrDefault(p => p.Id == id && p.OperationId == operation); }
        picker.SelectionChanged += (_, _) => selected((picker.SelectedItem as OperationPreset)?.Id);
        Window Owner() => Window.GetWindow(this);
        void Persist() { shell.Presets.MarkDirty(); if (!shell.Presets.Save()) new StorageWindow(shell) { Owner = Owner() }.ShowDialog(); }
        row.Children.Add(DialogUi.Button("Apply", () => { if (picker.SelectedItem is OperationPreset preset) try { apply(ToolForms.ApplyPreset(operation, get(), preset)); } catch (Exception ex) { DialogUi.Error(Owner(), ex); } }));
        void Save(bool saveAs)
        {
            try
            {
                var errors = ToolForms.OptionErrors(operation, get()); if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
                var preset = picker.SelectedItem as OperationPreset;
                if (saveAs || preset is null)
                {
                    var dialog = new UtilityWindow { Title = "Save operation preset", Width = 410, Height = 180, Owner = Owner(), WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    var content = DialogUi.Column(); var name = DialogUi.Field(content, "Preset name", "");
                    content.Children.Add(DialogUi.Button("Save", () =>
                    {
                        if (string.IsNullOrWhiteSpace(name.Text)) return;
                        if (shell.Presets.Value.Presets.Any(p => p.OperationId == operation && string.Equals(p.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(dialog, "This operation already has a preset with that name."); return; }
                        dialog.DialogResult = true;
                    })); dialog.Content = content; if (dialog.ShowDialog() != true) return;
                    preset = new() { OperationId = operation, Name = name.Text.Trim() }; shell.Presets.Value.Presets.Add(preset);
                }
                preset.Settings = ToolForms.PresetSettings(operation, get()); Persist(); Refresh(preset.Id);
            }
            catch (Exception ex) { DialogUi.Error(Owner(), ex); }
        }
        row.Children.Add(DialogUi.Button("Save", () => Save(false))); row.Children.Add(DialogUi.Button("Save as…", () => Save(true)));
        row.Children.Add(DialogUi.Button("Delete", () => { if (picker.SelectedItem is OperationPreset preset && MessageBox.Show(Owner(), $"Delete preset '{preset.Name}'?", "Delete preset", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { shell.Presets.Value.Presets.Remove(preset); Persist(); Refresh(); } })); Refresh(selectedId);
    }
}
