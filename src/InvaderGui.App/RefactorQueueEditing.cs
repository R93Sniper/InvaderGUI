using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;
using InvaderGui.Core;

namespace InvaderGui.App;

public partial class RefactorPreview
{
    private Guid? editingCommandId;
    public Guid? EditingCommandId => editingCommandId;
    private bool CanReviewQueue => !IsExecuting && Snapshot is not null && PlanIsActive();
    private Guid? SelectedCommandId => (CommandQueue.SelectedItem as QueueItem)?.Command.Id;

    private void UpdateQueueControls()
    {
        if (!stagingReady) return;
        var index = pendingCommands.FindIndex(command => command.Id == SelectedCommandId);
        QueueAddButton.IsEnabled = CanReviewQueue && editingCommandId is null;
        QueueEditButton.IsEnabled = CanReviewQueue && index >= 0 && editingCommandId is null;
        QueueRemoveButton.IsEnabled = !IsExecuting && index >= 0;
        QueueUpButton.IsEnabled = CanReviewQueue && index > 0;
        QueueDownButton.IsEnabled = CanReviewQueue && index >= 0 && index < pendingCommands.Count - 1;
        StageActionButton.Content = editingCommandId is null ? "Stage action" : "Apply changes";
        CancelEditButton.Visibility = EditorModeStatus.Visibility = editingCommandId is null ? Visibility.Collapsed : Visibility.Visible;
        PlannedTree.ContextMenu.IsEnabled = CanReviewQueue && editingCommandId is null;
        if (editingCommandId is { } id)
            EditorModeStatus.Text = $"Editing command {pendingCommands.FindIndex(command => command.Id == id) + 1} · Apply changes or Cancel edit";
        ArrangeEditorActions();
        RefreshAllPathSuggestions();
        UpdatePlanFileUi();
    }

    private void ArrangeEditorActions()
    {
        if (EditorActionsRow.ActualWidth <= 0) return;
        var dock = EditorActionsRow.ActualWidth < (editingCommandId is null ? 350 : 440) ? Dock.Top : Dock.Right;
        if (DockPanel.GetDock(EditorActionButtons) != dock) DockPanel.SetDock(EditorActionButtons, dock);
    }

    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateQueueControls();

    private void ClearEditor()
    {
        ExtraMappings.Children.Clear(); extraPairs.Clear();
        ReplacementRows.Children.Clear(); replacements.Clear();
        AdditionalGroupRows.Children.Clear(); additionalGroups.Clear();
        ModeSelector.SelectedIndex = 0; MappingSelector.SelectedIndex = 0; ConfigureEditor(true);
        LimitReferences.IsChecked = false; AllowMissingDestinations.IsChecked = false; SingleTagScope.Text = "";
        StringReplacements.IsExpanded = AdditionalGroups.IsExpanded = false;
        EditorValidation.Text = "";
    }

    private void NewQueueCommand(object sender, RoutedEventArgs e)
    {
        if (!CanReviewQueue || editingCommandId is not null) return;
        ClearEditor(); SourceField.Focus(); OperationEditor.BringIntoView();
    }

    public bool BeginEditCommand(Guid id)
    {
        if (!CanReviewQueue || editingCommandId is not null) return false;
        var command = pendingCommands.FirstOrDefault(command => command.Id == id);
        if (command is null) return false;
        ClearEditor();
        ModeSelector.SelectedIndex = command.Mode switch { "copy" => 1, "no-move" => 2, _ => 0 };
        var kind = command.RecursiveMapping is not null ? 1 : command.TagMappings.Length > 0 ? 0 : command.GroupMappings.Length > 0 ? 2 : 3;
        MappingSelector.SelectedIndex = kind; ConfigureEditor(true);
        var pairs = kind == 0 ? command.TagMappings.Select(pair => (pair.Source, pair.Destination)).ToArray() :
            kind == 2 ? command.GroupMappings.Select(pair => (pair.SourceGroup, pair.DestinationGroup)).ToArray() : [];
        if (pairs.Length > 0)
        {
            SourceField.Text = pairs[0].Item1; DestinationField.Text = pairs[0].Item2;
            foreach (var pair in pairs.Skip(1)) AddMappingRow(kind == 2, pair.Item1, pair.Item2);
        }
        if (command.RecursiveMapping is { } recursive)
        { SourceField.Text = recursive.SourceDirectory; DestinationField.Text = recursive.DestinationDirectory; }
        if (kind != 2) foreach (var pair in command.GroupMappings) AddGroupMappingRow(pair.SourceGroup, pair.DestinationGroup);
        foreach (var pair in command.ReplaceStrings) AddReplacementRow(pair.Find, pair.Replace);
        LimitReferences.IsChecked = command.SingleTagScope is not null;
        SingleTagScope.Text = command.SingleTagScope ?? "";
        AllowMissingDestinations.IsChecked = command.Unsafe;
        editingCommandId = id;
        RememberEditDraft();
        UpdateQueueControls();
        SourceField.Focus(); OperationEditor.BringIntoView();
        return true;
    }

    private void EditQueueCommand(object sender, RoutedEventArgs e)
    {
        if (SelectedCommandId is { } id) BeginEditCommand(id);
    }

    public void CancelEdit()
    {
        if (editingCommandId is null) return;
        editingCommandId = null; ClearEditor(); UpdateQueueControls();
        EditorValidation.Foreground = Brushes.LightGray; EditorValidation.Text = "Edit canceled. Queued commands are unchanged.";
    }
    private void CancelCommandEdit(object sender, RoutedEventArgs e) => CancelEdit();

    public bool TryUpdateCommand(Guid id, RefactorCommand replacement)
    {
        try
        {
            if (!CanReviewQueue) throw new ArgumentException("Return to the queue's original profile and tag-folder selection and refresh before editing.");
            if (editingCommandId is not null && editingCommandId != id) throw new ArgumentException("Apply or cancel the current edit before editing another command.");
            var index = pendingCommands.FindIndex(command => command.Id == id);
            if (index < 0) throw new ArgumentException("The command is no longer in the queue.");
            var normalized = RefactorPlanner.Normalize(replacement with { Id = id });
            // An unfinished queue must remain editable one command at a time.
            // Validate command structure here; replay reports filesystem/dependency conflicts after applying.
            normalized = RefactorPlanFiles.Validate(CapturePlan() with { Queue = [normalized] }).Queue[0];
            pendingCommands[index] = normalized;
            executionErrors.Remove(id);
            MarkPlanChanged();
            if (editingCommandId == id) { editingCommandId = null; ClearEditor(); }
            UpdatePlanViews(selectedId: id);
            EditorValidation.Foreground = PlannedReplay?.IsValid == false ? Brushes.Salmon : Brushes.LightGreen;
            EditorValidation.Text = PlannedReplay?.IsValid == false ? "Command updated. Queue conflicts remain; review the flagged commands.\n" +
                string.Join("\n", PlannedReplay.Entries.SelectMany(entry => entry.Errors)) : "Command updated. No files have changed.";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        { EditorValidation.Foreground = Brushes.Salmon; EditorValidation.Text = ex.Message; return false; }
    }

    public bool RemoveCommand(Guid id)
    {
        if (IsExecuting) return false;
        var index = pendingCommands.FindIndex(command => command.Id == id);
        if (index < 0) return false;
        pendingCommands.RemoveAt(index);
        executionErrors.Remove(id);
        if (editingCommandId == id) { editingCommandId = null; ClearEditor(); }
        if (pendingCommands.Count == 0)
        { boundProfileId = null; boundProfileName = boundWorkingDirectory = null; boundRoots = null; }
        MarkPlanChanged();
        UpdatePlanViews();
        return true;
    }
    private void RemoveQueueCommand(object sender, RoutedEventArgs e)
    {
        if (SelectedCommandId is { } id) RemoveCommand(id);
    }

    /// <summary>Moves an entire invocation to a final zero-based queue index.</summary>
    public bool MoveCommand(Guid id, int destinationIndex)
    {
        if (!CanReviewQueue || destinationIndex < 0 || destinationIndex >= pendingCommands.Count) return false;
        var index = pendingCommands.FindIndex(command => command.Id == id);
        if (index < 0) return false;
        if (index == destinationIndex) return true;
        var command = pendingCommands[index];
        pendingCommands.RemoveAt(index); pendingCommands.Insert(destinationIndex, command);
        MarkPlanChanged();
        UpdatePlanViews(selectedId: id);
        CommandQueue.ScrollIntoView(CommandQueue.SelectedItem);
        return true;
    }
    private void MoveQueueCommandUp(object sender, RoutedEventArgs e)
    { if (SelectedCommandId is { } id) MoveCommand(id, CommandQueue.SelectedIndex - 1); }
    private void MoveQueueCommandDown(object sender, RoutedEventArgs e)
    { if (SelectedCommandId is { } id) MoveCommand(id, CommandQueue.SelectedIndex + 1); }

    private void QueueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindQueueItem(e.OriginalSource as DependencyObject) is null) return;
        EditQueueCommand(sender, e); e.Handled = true;
    }
    private void QueueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveQueueCommand(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter) { EditQueueCommand(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && editingCommandId is not null) { CancelEdit(); e.Handled = true; }
        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Up) { MoveQueueCommandUp(sender, e); e.Handled = true; }
            if (key == Key.Down) { MoveQueueCommandDown(sender, e); e.Handled = true; }
        }
    }

    private sealed record QueueDragPayload(RefactorPreview Owner, Guid CommandId);
    private const string QueueDragFormat = "InvaderGui.RefactorQueueCommand";
    private Point queueDragStart;
    private Guid? queueDragSource;
    private long lastQueueScroll;
    private QueueInsertionAdorner? queueInsertion;

    private static ListBoxItem? FindQueueItem(DependencyObject? element)
    {
        while (element is not null && element is not ListBoxItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }
    private void QueueMouseDown(object sender, MouseButtonEventArgs e)
    {
        queueDragStart = e.GetPosition(CommandQueue);
        queueDragSource = (FindQueueItem(e.OriginalSource as DependencyObject)?.DataContext as QueueItem)?.Command.Id;
    }
    private void QueueMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || queueDragSource is null || !CanReviewQueue) return;
        var point = e.GetPosition(CommandQueue);
        if (Math.Abs(point.X - queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var id = queueDragSource.Value; queueDragSource = null;
        try { DragDrop.DoDragDrop(CommandQueue, new DataObject(QueueDragFormat, new QueueDragPayload(this, id)), DragDropEffects.Move); }
        finally { ClearQueueInsertion(); }
    }
    private (Guid Id, int Insertion, double Y)? QueueDropLocation(DragEventArgs e)
    {
        if (!CanReviewQueue || e.Data.GetData(QueueDragFormat) is not QueueDragPayload payload || payload.Owner != this ||
            pendingCommands.All(command => command.Id != payload.CommandId)) return null;
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != CommandQueue;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is ScrollBar) return null;
        var item = FindQueueItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is QueueItem row)
        {
            var after = e.GetPosition(item).Y >= item.ActualHeight / 2;
            var index = pendingCommands.FindIndex(command => command.Id == row.Command.Id);
            var y = item.TranslatePoint(new Point(0, after ? item.ActualHeight : 0), CommandQueue).Y;
            return (payload.CommandId, index + (after ? 1 : 0), y);
        }
        if (e.GetPosition(CommandQueue).Y < 4) return (payload.CommandId, 0, 2);
        var last = CommandQueue.ItemContainerGenerator.ContainerFromIndex(CommandQueue.Items.Count - 1) as ListBoxItem;
        var bottom = last is null ? CommandQueue.ActualHeight - 2 : last.TranslatePoint(new Point(0, last.ActualHeight), CommandQueue).Y;
        return (payload.CommandId, pendingCommands.Count, Math.Clamp(bottom, 2, Math.Max(2, CommandQueue.ActualHeight - 2)));
    }
    private void QueueDragOver(object sender, DragEventArgs e)
    {
        var location = QueueDropLocation(e);
        e.Effects = location is null ? DragDropEffects.None : DragDropEffects.Move; e.Handled = true;
        if (location is null) { ClearQueueInsertion(); return; }
        if (AdornerLayer.GetAdornerLayer(CommandQueue) is { } layer)
        {
            if (queueInsertion is null) { queueInsertion = new(CommandQueue); layer.Add(queueInsertion); }
            queueInsertion.Y = location.Value.Y; queueInsertion.InvalidateVisual();
        }
        var point = e.GetPosition(CommandQueue);
        if (Environment.TickCount64 - lastQueueScroll < 90) return;
        if (VisualChild<ScrollViewer>(CommandQueue) is { } scroll)
        {
            if (point.Y < 24) scroll.LineUp();
            else if (point.Y > CommandQueue.ActualHeight - 24) scroll.LineDown();
            lastQueueScroll = Environment.TickCount64;
        }
    }
    private static T? VisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (VisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void QueueDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (QueueDropLocation(e) is not { } location) return;
            var source = pendingCommands.FindIndex(command => command.Id == location.Id);
            MoveCommand(location.Id, location.Insertion > source ? location.Insertion - 1 : location.Insertion);
        }
        finally { ClearQueueInsertion(); e.Handled = true; }
    }
    private void QueueDragLeave(object sender, DragEventArgs e) => ClearQueueInsertion();
    private void ClearQueueInsertion()
    {
        if (queueInsertion is null) return;
        AdornerLayer.GetAdornerLayer(CommandQueue)?.Remove(queueInsertion); queueInsertion = null;
    }
    private sealed class QueueInsertionAdorner : Adorner
    {
        public double Y { get; set; }
        public QueueInsertionAdorner(UIElement element) : base(element) { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext context)
        {
            var y = Math.Clamp(Y, 2, Math.Max(2, AdornedElement.RenderSize.Height - 2));
            context.DrawLine(new Pen(Brushes.LightSkyBlue, 3), new Point(2, y), new Point(Math.Max(2, AdornedElement.RenderSize.Width - 18), y));
        }
    }
}
