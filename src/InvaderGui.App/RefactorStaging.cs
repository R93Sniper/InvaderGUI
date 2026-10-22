using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InvaderGui.Core;

namespace InvaderGui.App;

public partial class RefactorPreview
{
    private bool stagingReady;
    private readonly List<RefactorCommand> pendingCommands = new();
    private Guid? boundProfileId;
    private string? boundProfileName, boundWorkingDirectory;
    private IReadOnlyList<TagHierarchyRoot>? boundRoots;
    private readonly List<(ComboBox Source, ComboBox Destination)> extraPairs = new();
    private readonly List<(TextBox Find, TextBox Replace)> replacements = new();
    private readonly List<(ComboBox Source, ComboBox Destination)> additionalGroups = new();
    public IReadOnlyList<RefactorCommand> PendingCommands => pendingCommands.ToArray();
    public RefactorReplay? PlannedReplay { get; private set; }
    public bool HasPendingCommands => pendingCommands.Count > 0;
    public CheckBox LimitReferences { get; } = new() { Content = "Limit reference changes to one tag", Margin = new Thickness(0, 0, 16, 5) };
    public TextBox SingleTagScope { get; } = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 5), ToolTip = "Logical tag path, including its group extension." };
    public CheckBox AllowMissingDestinations { get; } = new() { Content = "Allow missing destinations (References only)", Margin = new Thickness(0, 0, 0, 5) };
    public string LastValidationMessage => EditorValidation.Text;

    private sealed record QueueItem(int Index, RefactorCommand Command, RefactorCommandResult? Result)
    {
        public string Caption => $"{Index}   {Command.Summary}";
        public string? ExecutionError { get; init; }
        public string Problem => string.Join("\n", (Result?.Errors ?? []).Concat(ExecutionError is null ? [] : new[] { ExecutionError }));
        public string Details => Command.Summary +
            (Command.GroupMappings.Length == 0 ? "" : "\nGroup mappings:\n" + string.Join("\n", Command.GroupMappings.Select(pair => $"{pair.SourceGroup} → {pair.DestinationGroup}"))) +
            (Result is null ? "" : $"\n{Result.AffectedTags} path/reference mapping(s).") +
            (Command.ReplaceStrings.Length == 0 ? "" : "\nReplacements, in order:\n" + string.Join("\n", Command.ReplaceStrings.Select(pair => $"{pair.Find} → {pair.Replace}"))) +
            (Command.SingleTagScope is null ? "" : "\nReference scope: " + Command.SingleTagScope) +
            (Command.Unsafe ? "\nMissing reference destinations allowed." : "") +
            (Result is null ? "" : "\n" + string.Join("\n", Result.Warnings.Concat(Result.Errors)));
    }

    private void InitializeStaging()
    {
        stagingReady = true;
        AttachPathSuggestions(SourceField, DestinationField);
        EditorActionsRow.SizeChanged += (_, _) => ArrangeEditorActions();
        ConfigureEditor(true);
        UpdateReferenceOptions(false);
        InitializePlanPersistence();
    }

    private string SelectedMode => ModeSelector.SelectedIndex switch { 1 => "copy", 2 => "no-move", _ => "move" };

    private void ModeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (stagingReady) ConfigureEditor(false);
    }

    private void ConfigureEditor(bool resetMappings)
    {
        var referencesOnly = SelectedMode == "no-move";
        ((ComboBoxItem)MappingSelector.Items[1]).IsEnabled = !referencesOnly;
        ((ComboBoxItem)MappingSelector.Items[2]).IsEnabled = referencesOnly;
        if (referencesOnly && MappingSelector.SelectedIndex == 1 || !referencesOnly && MappingSelector.SelectedIndex == 2)
        { MappingSelector.SelectedIndex = 0; return; }
        var index = MappingSelector.SelectedIndex;
        AdditionalGroups.Visibility = referencesOnly && index is 0 or 3 ? Visibility.Visible : Visibility.Collapsed;
        MappingFields.Visibility = index == 3 ? Visibility.Collapsed : Visibility.Visible;
        AddMappingButton.Visibility = index is 1 or 3 ? Visibility.Collapsed : Visibility.Visible;
        SourceLabel.Text = index == 1 ? "Source folder" : index == 2 ? "Source group" : "Source tag";
        DestinationLabel.Text = index == 1 ? "Destination folder" : index == 2 ? "Destination group" : "Destination tag";
        if (resetMappings)
        {
            ExtraMappings.Children.Clear(); extraPairs.Clear();
            foreach (var field in new[] { SourceField, DestinationField })
            {
                field.ItemsSource = index == 2 ? TagHierarchy.TagGroups : null;
                field.IsEditable = index != 2;
                field.Text = "";
            }
            if (index == 2) { SourceField.SelectedItem = "model"; DestinationField.SelectedItem = "gbxmodel"; }
        }
        if (index == 3) StringReplacements.IsExpanded = true;
        if (!referencesOnly) AllowMissingDestinations.IsChecked = false;
        UpdateReferenceOptions(Snapshot is not null && PlanIsActive());
        RefreshAllPathSuggestions();
    }

    private void UpdateReferenceOptions(bool enabled)
    {
        LimitReferences.IsEnabled = SingleTagScope.IsEnabled = enabled;
        AllowMissingDestinations.IsEnabled = enabled && SelectedMode == "no-move";
    }

    private void AddMapping(object sender, RoutedEventArgs e)
    {
        var group = MappingSelector.SelectedIndex == 2;
        AddMappingRow(group).Source.Focus();
    }

    private (ComboBox Source, ComboBox Destination) AddMappingRow(bool group, string sourceText = "", string destinationText = "")
    {
        var source = new ComboBox { IsEditable = !group, IsTextSearchEnabled = group };
        var destination = new ComboBox { IsEditable = !group, IsTextSearchEnabled = group };
        if (group) source.ItemsSource = destination.ItemsSource = TagHierarchy.TagGroups;
        var pair = (source, destination);
        var row = PairRow(source, destination, () => { extraPairs.Remove(pair); });
        extraPairs.Add(pair); ExtraMappings.Children.Add(row);
        if (!group) AttachPathSuggestions(source, destination);
        source.Text = sourceText; destination.Text = destinationText;
        if (!group) PopulatePathSuggestions(source, destination);
        return pair;
    }

    private static Grid PairRow(FrameworkElement source, FrameworkElement destination, Action remove)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(source);
        var arrow = new TextBlock { Text = "→", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1); row.Children.Add(arrow);
        Grid.SetColumn(destination, 2); row.Children.Add(destination);
        var button = new Button { Content = "×", ToolTip = "Remove this row", VerticalAlignment = VerticalAlignment.Top };
        button.Click += (_, _) => { remove(); if (row.Parent is Panel parent) parent.Children.Remove(row); };
        Grid.SetColumn(button, 3); row.Children.Add(button);
        return row;
    }

    private void AddReplacement(object sender, RoutedEventArgs e)
    {
        AddReplacementRow().Find.Focus();
    }

    private (TextBox Find, TextBox Replace) AddReplacementRow(string findText = "", string replacementText = "")
    {
        var find = new TextBox { ToolTip = "Find text in the tag path, excluding its group extension." };
        var replace = new TextBox { ToolTip = "Replacement text. An empty value removes the matching text." };
        var pair = (find, replace);
        replacements.Add(pair);
        ReplacementRows.Children.Add(PairRow(find, replace, () => replacements.Remove(pair)));
        find.Text = findText; replace.Text = replacementText;
        StringReplacements.IsExpanded = true; return pair;
    }

    private void AddGroupMapping(object sender, RoutedEventArgs e)
    {
        AddGroupMappingRow().Source.Focus();
    }

    private (ComboBox Source, ComboBox Destination) AddGroupMappingRow(string sourceGroup = "", string destinationGroup = "")
    {
        var source = new ComboBox { ItemsSource = TagHierarchy.TagGroups };
        var destination = new ComboBox { ItemsSource = TagHierarchy.TagGroups };
        var pair = (source, destination); additionalGroups.Add(pair);
        AdditionalGroupRows.Children.Add(PairRow(source, destination, () => additionalGroups.Remove(pair)));
        AdditionalGroups.IsExpanded = true;
        source.Text = sourceGroup; destination.Text = destinationGroup;
        return pair;
    }

    private void StageFromEditor(object sender, RoutedEventArgs e)
    {
        var command = ReadEditorCommand();
        if (editingCommandId is { } id) TryUpdateCommand(id, command);
        else TryStageCommand(command);
    }

    private RefactorCommand ReadEditorCommand()
    {
        var pairs = new[] { (SourceField, DestinationField) }.Concat(extraPairs).ToArray();
        var kind = MappingSelector.SelectedIndex;
        var compatibleGroups = AdditionalGroups.Visibility == Visibility.Visible ? additionalGroups : [];
        if (compatibleGroups.Count > 0 && SourceField.Text.Length == 0 && DestinationField.Text.Length == 0) pairs = pairs.Skip(1).ToArray();
        return new RefactorCommand
        {
            Mode = SelectedMode,
            TagMappings = kind != 0 ? [] : pairs.Select(pair => new RefactorTagMapping(pair.Item1.Text, pair.Item2.Text)).ToImmutableArray(),
            RecursiveMapping = kind != 1 ? null : new(SourceField.Text, DestinationField.Text),
            GroupMappings = (kind == 2 ? pairs.Select(pair => new RefactorGroupMapping(pair.Item1.Text, pair.Item2.Text)) :
                compatibleGroups.Select(pair => new RefactorGroupMapping(pair.Source.Text, pair.Destination.Text))).ToImmutableArray(),
            ReplaceStrings = replacements.Select(pair => new RefactorStringReplacement(pair.Find.Text, pair.Replace.Text)).ToImmutableArray(),
            SingleTagScope = LimitReferences.IsChecked == true ? SingleTagScope.Text : null,
            Unsafe = SelectedMode == "no-move" && AllowMissingDestinations.IsChecked == true
        };
    }

    private bool PlanIsActive()
    {
        if (pendingCommands.Count == 0) return true;
        return Snapshot is not null && browsingProfile is not null && browsingProfile.Id == boundProfileId &&
            SameDirectory(browsingProfile.WorkingDirectory, boundWorkingDirectory) &&
            boundRoots is not null && boundRoots.Count == Snapshot.Roots.Count &&
            boundRoots.Zip(Snapshot.Roots).All(pair => pair.First.Id == pair.Second.Id && SameDirectory(pair.First.Path, pair.Second.Path));
    }

    public bool TryStageCommand(RefactorCommand candidate)
    {
        try
        {
            if (IsExecuting) throw new ArgumentException("Wait for execution to finish before changing the queue.");
            if (Snapshot is null) throw new ArgumentException("Wait for the tag folders to load successfully before staging a command.");
            if (editingCommandId is not null) throw new ArgumentException("Apply or cancel the current command edit before staging another command.");
            if (!PlanIsActive()) throw new ArgumentException("This queue is bound to another profile or folder selection. Return to its original selection before adding commands.");
            if (pendingCommands.Any(command => command.Id == candidate.Id)) throw new ArgumentException("This command is already in the queue.");
            var normalized = RefactorPlanner.Normalize(candidate);
            var replay = RefactorPlanner.Replay(Snapshot, pendingCommands.Append(normalized));
            if (!replay.IsValid) throw new ArgumentException(string.Join("\n", replay.Entries.SelectMany(entry => entry.Errors)));
            if (pendingCommands.Count == 0)
            {
                boundProfileId = browsingProfile!.Id; boundProfileName = browsingProfile.Name;
                boundWorkingDirectory = browsingProfile.WorkingDirectory; boundRoots = Snapshot.Roots.ToArray();
            }
            pendingCommands.Add(normalized);
            MarkPlanChanged();
            UpdatePlanViews(replay);
            EditorValidation.Foreground = Brushes.LightGreen;
            EditorValidation.Text = "Command staged. No files have changed.";
            CommandQueue.SelectedIndex = pendingCommands.Count - 1;
            return true;
        }
        catch (ArgumentException ex)
        {
            EditorValidation.Foreground = Brushes.Salmon; EditorValidation.Text = ex.Message; return false;
        }
    }

    private void UpdatePlanViews(RefactorReplay? replay = null, Guid? selectedId = null)
    {
        var previousIndex = CommandQueue.SelectedIndex;
        selectedId ??= (CommandQueue.SelectedItem as QueueItem)?.Command.Id;
        var active = Snapshot is not null && PlanIsActive();
        PlannedReplay = pendingCommands.Count == 0 || !active ? null : replay ?? RefactorPlanner.Replay(Snapshot!, pendingCommands);
        PlannedTree.ItemsSource = PlannedReplay?.Planned.Nodes ?? Snapshot?.Nodes;
        CommandQueue.ItemsSource = pendingCommands.Select((command, index) => new QueueItem(index + 1, command, PlannedReplay?.Entries[index]) { ExecutionError = executionErrors.GetValueOrDefault(command.Id) }).ToArray();
        CommandQueue.SelectedItem = CommandQueue.Items.Cast<QueueItem>().FirstOrDefault(item => item.Command.Id == selectedId);
        if (CommandQueue.SelectedItem is null && pendingCommands.Count > 0) CommandQueue.SelectedIndex = Math.Clamp(previousIndex, 0, pendingCommands.Count - 1);
        QueueBindingStatus.Text = pendingCommands.Count == 0 ? "No commands staged." : !active ?
            $"Inactive · {boundProfileName}. Return to the original tag-folder selection." :
            $"{pendingCommands.Count} pending" + (IsExecuting ? StopRequested ? " · Stopping" : " · Running" : executionErrors.Count > 0 ? " · Last attempt stopped" : " · Not run") + (PlannedReplay?.IsValid == false ? " · Conflicts detected" : "");
        OperationEditor.IsEnabled = active && !IsExecuting;
        UpdateReferenceOptions(active && !IsExecuting);
        EditorValidation.Foreground = Brushes.Salmon;
        EditorValidation.Text = !active && pendingCommands.Count > 0 ? "Return to the queue's original profile and tag-folder selection to stage more commands." :
            PlannedReplay?.IsValid == false ? string.Join("\n", PlannedReplay.Entries.SelectMany(entry => entry.Errors)) : "";
        UpdateQueueControls();
        if (Snapshot is null) return;
        HierarchyStatus.Text = $"{Snapshot.TagCount:N0} tags · {Snapshot.Roots.Count} folder(s) · {Snapshot.ShadowedCount:N0} shadowed definitions. " +
            (PlannedReplay is null ? "Planned matches Current." : PlannedReplay.IsValid ? "Planned shows staged changes only." : "Some queued commands are invalid; review the queue.") +
            (Snapshot.SkippedLinks > 0 ? $" Skipped {Snapshot.SkippedLinks:N0} linked files/folders." : "");
    }

    private void TagSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!stagingReady || editingCommandId is not null || !OperationEditor.IsEnabled || e.NewValue is not TagHierarchyNode node) return;
        if (node.IsFolder && SelectedMode == "no-move") ModeSelector.SelectedIndex = 0;
        MappingSelector.SelectedIndex = node.IsFolder ? 1 : 0;
        SourceField.Text = node.LogicalPath;
    }

    public bool TryStageMove(string sourcePath, string destinationPath)
    {
        var nodes = PlannedReplay?.Planned.Nodes ?? Snapshot?.Nodes ?? [];
        var node = RefactorPlanner.Flatten(nodes).FirstOrDefault(item => item.LogicalPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
        if (node is null) { EditorValidation.Text = "Select an existing item in Planned."; EditorValidation.Foreground = Brushes.Salmon; return false; }
        return TryStageCommand(new()
        {
            RecursiveMapping = node.IsFolder ? new(node.LogicalPath, destinationPath) : null,
            TagMappings = node.IsFolder ? [] : [new(node.LogicalPath, destinationPath)],
            SingleTagScope = LimitReferences.IsChecked == true ? SingleTagScope.Text : null
        });
    }

    public bool TryStageRename(string sourcePath, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(['\\', '/']) >= 0)
        { EditorValidation.Text = "Enter a name without a folder path."; EditorValidation.Foreground = Brushes.Salmon; return false; }
        var node = RefactorPlanner.Flatten(PlannedReplay?.Planned.Nodes ?? Snapshot?.Nodes ?? []).FirstOrDefault(item => item.LogicalPath == sourcePath);
        if (node is null) return false;
        if (!node.IsFolder)
        {
            var extension = Path.GetExtension(node.Name);
            if (!newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) newName += extension;
        }
        var separator = sourcePath.LastIndexOf('\\');
        return TryStageMove(sourcePath, (separator < 0 ? "" : sourcePath[..(separator + 1)]) + newName);
    }

    private void RenameSelected(object sender, RoutedEventArgs e)
    {
        if (!OperationEditor.IsEnabled || editingCommandId is not null || PlannedTree.SelectedItem is not TagHierarchyNode node) return;
        var dialog = new UtilityWindow { Title = node.IsFolder ? "Rename folder" : "Rename tag", Owner = Window.GetWindow(this), Width = 430, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = DialogUi.Column(); dialog.Content = panel;
        var name = DialogUi.Field(panel, "New name", node.IsFolder ? node.Name : node.Name[..^Path.GetExtension(node.Name).Length]);
        if (!node.IsFolder) panel.Children.Add(DialogUi.Text("Tag extension stays " + Path.GetExtension(node.Name)));
        var message = DialogUi.Text(""); message.Foreground = Brushes.Salmon; panel.Children.Add(message);
        var buttons = DialogUi.Row(); buttons.HorizontalAlignment = HorizontalAlignment.Right; panel.Children.Add(buttons);
        var cancel = DialogUi.Button("Cancel", () => dialog.Close()); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var rename = DialogUi.Button("Stage rename", () => { if (TryStageRename(node.LogicalPath, name.Text)) dialog.Close(); else message.Text = LastValidationMessage; });
        rename.IsDefault = true; buttons.Children.Add(rename);
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); }; dialog.ShowDialog();
    }

    private void PlannedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        e.Handled = true; RenameSelected(sender, e);
    }
    private void PlannedRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindTreeItem(e.OriginalSource as DependencyObject);
        if (item is not null) { item.IsSelected = true; item.Focus(); }
    }
    private static TreeViewItem? FindTreeItem(DependencyObject? element)
    {
        while (element is not null && element is not TreeViewItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as TreeViewItem;
    }
    private Point dragStart;
    private string? dragSource;
    private sealed record DragPayload(RefactorPreview Owner, string Path);
    private const string DragFormat = "InvaderGui.RefactorPlannedTag";
    private void PlannedMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(PlannedTree);
        dragSource = e.OriginalSource is TextBlock ? (FindTreeItem(e.OriginalSource as DependencyObject)?.DataContext as TagHierarchyNode)?.LogicalPath : null;
    }
    private void PlannedMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragSource is null || editingCommandId is not null || !OperationEditor.IsEnabled) return;
        var point = e.GetPosition(PlannedTree);
        if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = dragSource; dragSource = null;
        DragDrop.DoDragDrop(PlannedTree, new DataObject(DragFormat, new DragPayload(this, source)), DragDropEffects.Move);
    }
    private string? DropDestination(DragEventArgs e)
    {
        if (!OperationEditor.IsEnabled || editingCommandId is not null || e.Data.GetData(DragFormat) is not DragPayload payload || payload.Owner != this) return null;
        var target = FindTreeItem(e.OriginalSource as DependencyObject)?.DataContext as TagHierarchyNode;
        if (target is { IsFolder: false }) return null;
        var name = payload.Path[(payload.Path.LastIndexOf('\\') + 1)..];
        var destination = target is null ? name : target.LogicalPath + "\\" + name;
        return destination.Equals(payload.Path, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(payload.Path + "\\", StringComparison.OrdinalIgnoreCase) ? null : destination;
    }
    private void PlannedDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropDestination(e) is null ? DragDropEffects.None : DragDropEffects.Move; e.Handled = true;
    }
    private void PlannedDrop(object sender, DragEventArgs e)
    {
        if (DropDestination(e) is { } destination && e.Data.GetData(DragFormat) is DragPayload payload) TryStageMove(payload.Path, destination);
        e.Handled = true;
    }
}
