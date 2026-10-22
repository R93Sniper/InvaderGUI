using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using InvaderGui.Core;

namespace InvaderGui.App;

/// <summary>Refactor workspace. Staging changes the preview and pending commands, never files.</summary>
public partial class RefactorPreview : UserControl, IUnsavedPlanGuard
{
    private readonly UiSettings? layoutSettings;
    private readonly Action? saveLayout;
    private string? profileSignature;
    private Guid? profileId;
    private ProjectProfile? browsingProfile;
    private CancellationTokenSource? scanCancellation;
    private long scanVersion;
    public Task HierarchyRefresh { get; private set; } = Task.CompletedTask;
    public TagHierarchySnapshot? Snapshot { get; private set; }
    private bool updatingFolders;
    private sealed record FolderChoice(string Id, string Name);
    public ComboBox TagFolderSelector { get; } = new()
    {
        DisplayMemberPath = "Name",
        ToolTip = "Choose a tag folder from the selected profile, or use all profile tag folders in their configured order."
    };
    public StackPanel ReferenceOptions { get; } = new() { Margin = new Thickness(2, 5, 2, 4) };

    public RefactorPreview() : this(null, null) { }

    public RefactorPreview(UiSettings? layoutSettings, Action? saveLayout)
    {
        this.layoutSettings = layoutSettings;
        this.saveLayout = saveLayout;
        InitializeComponent();
        TagFolderSelector.SelectionChanged += (_, _) =>
        {
            if (updatingFolders || profileId is null || TagFolderSelector.SelectedItem is not FolderChoice choice) return;
            if (layoutSettings is not null)
            {
                layoutSettings.RefactorTagFolderSelections[profileId.Value.ToString("D")] = choice.Id;
                saveLayout?.Invoke();
            }
            RefreshHierarchy();
        };
        if (layoutSettings is not null)
        {
            HierarchyRow.Height = new GridLength(Math.Clamp(layoutSettings.RefactorHierarchyHeight, HierarchyRow.MinHeight, HierarchyRow.MaxHeight));
            QueueColumn.Width = new GridLength(Math.Clamp(layoutSettings.RefactorQueueWidth, QueueColumn.MinWidth, QueueColumn.MaxWidth));
        }
        HierarchyResizeHandle.DragCompleted += (_, _) => SaveLayout();
        QueueResizeHandle.DragCompleted += (_, _) => SaveLayout();
        QueueResizeHandle.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right) SaveLayout();
        };
        var options = new WrapPanel();
        LimitReferences.Checked += (_, _) => SingleTagScope.Visibility = Visibility.Visible;
        LimitReferences.Unchecked += (_, _) => SingleTagScope.Visibility = Visibility.Collapsed;
        options.Children.Add(LimitReferences);
        options.Children.Add(AllowMissingDestinations);
        ReferenceOptions.Children.Add(options);
        ReferenceOptions.Children.Add(SingleTagScope);
        InitializeStaging();
    }

    public void RefreshProfile(ProjectProfile? profile)
    {
        if (IsExecuting) return;
        var signature = System.Text.Json.JsonSerializer.Serialize(new { profile?.Id, profile?.WorkingDirectory, profile?.Paths.TagRoots });
        if (profileSignature == signature) return;
        profileSignature = signature;
        profileId = profile?.Id;
        browsingProfile = profile?.Copy();
        updatingFolders = true;
        try
        {
            var roots = profile?.Paths.TagRoots;
            var choices = new List<FolderChoice>();
            if (roots is { Count: > 0 })
            {
                choices.Add(new("", "Use profile tag folders (ordered)"));
                choices.AddRange(roots.Select(root => new FolderChoice(root.Id, $"{root.Name} — {root.Path}")));
            }
            else choices.Add(new("", profile is null ? "Select a profile first" : "No tag folders configured"));
            var selectedId = profile is null ? "" : layoutSettings?.RefactorTagFolderSelections.GetValueOrDefault(profile.Id.ToString("D")) ?? "";
            if (selectedId.Length > 0 && choices.All(choice => choice.Id != selectedId))
                choices.Add(new(selectedId, "Saved tag folder unavailable — select another"));
            TagFolderSelector.ItemsSource = choices;
            TagFolderSelector.SelectedItem = choices.First(choice => choice.Id == selectedId);
            TagFolderSelector.IsEnabled = roots is { Count: > 0 };
        }
        finally { updatingFolders = false; }
        RefreshHierarchy();
    }

    public void RefreshHierarchy() => HierarchyRefresh = RefreshHierarchyAsync();

    public void CancelScan()
    {
        scanVersion++;
        scanCancellation?.Cancel();
    }

    private async Task RefreshHierarchyAsync()
    {
        CancelScan();
        var version = scanVersion;
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        Snapshot = null;
        PlannedReplay = null;
        OperationEditor.IsEnabled = false;
        UpdateReferenceOptions(false);
        UpdateQueueControls();
        CurrentTree.ItemsSource = PlannedTree.ItemsSource = null;
        HierarchyStatus.Foreground = new SolidColorBrush(Color.FromRgb(186, 186, 192));
        try
        {
            if (browsingProfile is null)
            {
                HierarchyStatus.Text = "Select a profile and tag folder to load tags.";
                return;
            }
            var selectedId = (TagFolderSelector.SelectedItem as FolderChoice)?.Id;
            var roots = TagHierarchy.ResolveRoots(browsingProfile, selectedId);
            HierarchyStatus.Text = "Reading tag folders…";
            var token = cancellation.Token;
            // The scanner returns null for a superseded scan. Do not schedule a canceled Task,
            // which would throw again at await and interrupt a Visual Studio debugging session.
            var snapshot = await Task.Run(() => TagHierarchy.Scan(roots, token));
            if (snapshot is null || version != scanVersion || token.IsCancellationRequested) return;
            Snapshot = snapshot;
            CurrentTree.ItemsSource = snapshot.Nodes;
            UpdatePlanViews();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            if (version != scanVersion) return;
            HierarchyStatus.Foreground = Brushes.Salmon;
            HierarchyStatus.Text = $"Could not refresh tags: {ex.Message}";
            if (HasPendingCommands) QueueBindingStatus.Text = "Pending commands retained. Refresh tags successfully to restore the preview.";
        }
        finally
        {
            if (ReferenceEquals(scanCancellation, cancellation)) scanCancellation = null;
        }
    }

    private void SaveLayout()
    {
        if (layoutSettings is null) return;
        layoutSettings.RefactorHierarchyHeight = HierarchyRow.Height.Value;
        layoutSettings.RefactorQueueWidth = QueueColumn.Width.Value;
        saveLayout?.Invoke();
    }

    private void ResizeHierarchies(object sender, DragDeltaEventArgs e)
    {
        HierarchyRow.Height = new GridLength(Math.Clamp(HierarchyRow.ActualHeight + e.VerticalChange,
            HierarchyRow.MinHeight, HierarchyRow.MaxHeight));
    }

    private void MappingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (stagingReady) ConfigureEditor(true);
    }
}
