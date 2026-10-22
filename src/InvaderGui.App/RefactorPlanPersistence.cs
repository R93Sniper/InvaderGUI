using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using InvaderGui.Core;
using Microsoft.Win32;

namespace InvaderGui.App;

public partial class RefactorPreview
{
    private RefactorProfileBinding? rememberedBinding;
    private RefactorProfileBinding? SelectedTextBinding() => browsingProfile is null || Snapshot is null ? null :
        Binding(browsingProfile.Id, browsingProfile.Name, browsingProfile.WorkingDirectory, Snapshot.Roots);
    private string savedPlanSignature = "";
    private string? savedContentHash, editDraftBaseline;
    private bool planFileBusy, saveNeedsRetry;
    private long planRevision, planIoVersion;
    public string? PlanFilePath { get; private set; }
    public string? LastPlanError { get; private set; }
    public bool HasUnsavedChanges => saveNeedsRetry || PendingPlanSignature() != savedPlanSignature || HasUnappliedEditorChanges;
    public bool HasUnappliedEditorChanges => editingCommandId is not null && EditorFingerprint() != editDraftBaseline;

    private static bool SameDirectory(string? first, string? second) => first is not null && second is not null &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static RefactorProfileBinding Binding(Guid id, string? name, string workingDirectory, IReadOnlyList<TagHierarchyRoot> roots) => new()
    {
        ProfileId = id, ProfileNameAtSave = string.IsNullOrWhiteSpace(name) ? "Profile" : name, WorkingDirectory = workingDirectory,
        TagRoots = roots.Select((root, index) => new RefactorPlanRoot(root.Id, root.Path, index) { Name = root.Name }).ToImmutableArray()
    };

    public RefactorPlanDocument CapturePlan()
    {
        var binding = boundProfileId is { } id && boundWorkingDirectory is not null && boundRoots is not null ?
            Binding(id, boundProfileName, boundWorkingDirectory, boundRoots) : rememberedBinding;
        if (binding is null)
        {
            if (browsingProfile is null || Snapshot is null) throw new ArgumentException("Select a profile and refresh its tag folders before saving a new plan.");
            binding = Binding(browsingProfile.Id, browsingProfile.Name, browsingProfile.WorkingDirectory, Snapshot.Roots);
        }
        return RefactorPlanFiles.Validate(new() { ProfileBinding = binding, Queue = pendingCommands.ToImmutableArray() });
    }

    private string PendingPlanSignature() => pendingCommands.Count == 0 && boundRoots is null && rememberedBinding is null ? "" : RefactorPlanFiles.Serialize(CapturePlan());

    private string EditorFingerprint() => JsonSerializer.Serialize(new
    {
        Mode = SelectedMode, Kind = MappingSelector.SelectedIndex, Source = SourceField.Text, Destination = DestinationField.Text,
        Pairs = extraPairs.Select(pair => new { Source = pair.Source.Text, Destination = pair.Destination.Text }).ToArray(),
        Groups = additionalGroups.Select(pair => new { Source = pair.Source.Text, Destination = pair.Destination.Text }).ToArray(),
        Replacements = replacements.Select(pair => new { Find = pair.Find.Text, Replace = pair.Replace.Text }).ToArray(),
        Limit = LimitReferences.IsChecked, Scope = SingleTagScope.Text, Unsafe = AllowMissingDestinations.IsChecked
    });
    private void RememberEditDraft() => editDraftBaseline = EditorFingerprint();
    private void MarkPlanChanged() { planRevision++; UpdatePlanFileUi(); }

    private void InitializePlanPersistence()
    {
        OperationEditor.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdatePlanFileUi()));
        OperationEditor.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => UpdatePlanFileUi()));
        SingleTagScope.TextChanged += (_, _) => UpdatePlanFileUi();
        foreach (var toggle in new[] { LimitReferences, AllowMissingDestinations })
        { toggle.Checked += (_, _) => UpdatePlanFileUi(); toggle.Unchecked += (_, _) => UpdatePlanFileUi(); }
        UpdatePlanFileUi();
    }

    private void UpdatePlanFileUi()
    {
        if (!stagingReady) return;
        PlanSaveButton.IsEnabled = !IsExecuting && !planFileBusy && (boundRoots is not null || rememberedBinding is not null || Snapshot is not null && browsingProfile is not null);
        PlanSaveButton.ContextMenu.IsEnabled = PlanSaveButton.IsEnabled;
        PlanSaveAsButton.IsEnabled = PlanSaveButton.IsEnabled;
        PlanLoadButton.IsEnabled = !IsExecuting && !planFileBusy;
        PlanRebindButton.Visibility = pendingCommands.Count > 0 && Snapshot is not null && !PlanIsActive() ? Visibility.Visible : Visibility.Collapsed;
        PlanRebindButton.IsEnabled = !IsExecuting && !planFileBusy && editingCommandId is null;
    }

    private bool PlanFailure(string message)
    {
        LastPlanError = message;
        EditorValidation.Foreground = Brushes.Salmon;
        EditorValidation.Text = message;
        return false;
    }

    public bool TrySavePlan(string path, bool saveAs = false)
    {
        if (IsExecuting) return PlanFailure("Wait for execution to finish before saving the pending queue.");
        LastPlanError = null;
        try
        {
            var absolute = RefactorPlanFiles.FilePath(path);
            if (editingCommandId is { } id && !TryUpdateCommand(id, ReadEditorCommand()))
                return PlanFailure("Apply a valid command edit before saving: " + LastValidationMessage);
            var document = CapturePlan();
            var hash = RefactorPlanFiles.Save(absolute, document,
                !saveAs && string.Equals(PlanFilePath, absolute, StringComparison.OrdinalIgnoreCase) ? savedContentHash : null);
            PlanFilePath = absolute; savedContentHash = hash; rememberedBinding = document.ProfileBinding;
            saveNeedsRetry = false;
            savedPlanSignature = RefactorPlanFiles.Serialize(document);
            UpdatePlanFileUi();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            if (ex is IOException or UnauthorizedAccessException) saveNeedsRetry = true;
            return PlanFailure($"Could not save Refactor plan '{path}': {ex.Message}");
        }
    }

    private bool SaveWithDialog(bool saveAs)
    {
        if (!saveAs && PlanFilePath is not null) return TrySavePlan(PlanFilePath);
        var picker = new SaveFileDialog
        {
            Title = "Save Refactor plan", Filter = "Text command lists (*.txt)|*.txt",
            DefaultExt = RefactorPlanFiles.Extension, AddExtension = true, OverwritePrompt = true,
            FileName = PlanFilePath is null ? "Refactor plan" + RefactorPlanFiles.Extension : Path.GetFileName(PlanFilePath)
        };
        if (PlanFilePath is not null) picker.InitialDirectory = Path.GetDirectoryName(PlanFilePath);
        var owner = Window.GetWindow(this);
        var accepted = owner is null ? picker.ShowDialog() : picker.ShowDialog(owner);
        return accepted == true && TrySavePlan(picker.FileName, saveAs: true);
    }

    public Task<bool> SaveAsync()
    {
        LastPlanError = null;
        var saved = SaveWithDialog(false);
        if (!saved && LastPlanError is not null && Window.GetWindow(this) is { } owner)
            MessageBox.Show(owner, LastPlanError, "Refactor plan", MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.FromResult(saved);
    }
    private async void SavePlanClick(object sender, RoutedEventArgs e) => await SaveAsync();
    private void SavePlanAsClick(object sender, RoutedEventArgs e)
    {
        LastPlanError = null;
        if (!SaveWithDialog(true) && LastPlanError is not null && Window.GetWindow(this) is { } owner)
            MessageBox.Show(owner, LastPlanError, "Refactor plan", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool TryLoadPlan(string path, bool discardUnsavedChanges = false)
    {
        if (IsExecuting) return PlanFailure("Wait for execution to finish before loading another plan.");
        try
        {
            var absolute = RefactorPlanFiles.FilePath(path);
            var loaded = RefactorPlanFiles.Read(absolute, SelectedTextBinding());
            if (HasUnsavedChanges && !discardUnsavedChanges) return PlanFailure("Save or explicitly discard unsaved Refactor changes before loading another plan.");
            ApplyLoadedPlan(absolute, loaded);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException or System.Security.SecurityException)
        { return PlanFailure($"Could not load Refactor plan '{path}': {ex.Message}"); }
    }

    private void SetBinding(RefactorProfileBinding binding)
    {
        boundProfileId = binding.ProfileId; boundProfileName = binding.ProfileNameAtSave; boundWorkingDirectory = binding.WorkingDirectory;
        boundRoots = binding.TagRoots.Select(root => new TagHierarchyRoot(root.RootId, root.Name ?? root.RootId, root.Path, root.Precedence)).ToArray();
    }
    private void ApplyLoadedPlan(string path, RefactorPlanRead loaded)
    {
        CancelScan();
        pendingCommands.Clear(); pendingCommands.AddRange(loaded.Document.Queue);
        executionErrors.Clear();
        SetBinding(loaded.Document.ProfileBinding);
        rememberedBinding = loaded.Document.ProfileBinding;
        PlanFilePath = path; savedContentHash = loaded.ContentHash; savedPlanSignature = RefactorPlanFiles.Serialize(loaded.Document);
        editingCommandId = null; saveNeedsRetry = false; ClearEditor(); LastPlanError = null; planRevision++;
        RefreshHierarchy(); UpdatePlanViews();
    }

    private async void LoadPlanClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Load Refactor plan", CheckFileExists = true, Multiselect = false,
            Filter = "Text command lists (*.txt)|*.txt|Batch command lists (*.bat)|*.bat|Legacy JSON plans (*.json)|*.json"
        };
        if (PlanFilePath is not null) picker.InitialDirectory = Path.GetDirectoryName(PlanFilePath);
        var owner = Window.GetWindow(this);
        if ((owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) != true) return;
        var request = ++planIoVersion;
        planFileBusy = true; UpdatePlanFileUi();
        RefactorPlanRead loaded;
        var textBinding = SelectedTextBinding();
        try { loaded = await Task.Run(() => RefactorPlanFiles.Read(picker.FileName, textBinding)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            planFileBusy = false;
            if (request == planIoVersion) { UpdatePlanFileUi(); PlanFailure($"Could not load Refactor plan '{picker.FileName}': {ex.Message}"); }
            return;
        }
        if (request != planIoVersion) { planFileBusy = false; return; }
        planFileBusy = false; UpdatePlanFileUi();
        if (HasUnsavedChanges)
        {
            var choice = MessageBox.Show(owner, "Save your current Refactor changes before loading this plan? No discards those unsaved changes; Cancel keeps the current plan.",
                "Unsaved Refactor plan", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel || choice == MessageBoxResult.Yes && !await SaveAsync()) return;
            if (choice == MessageBoxResult.Yes)
            {
                // Save may have written the very file being loaded. Do not restore a stale
                // pre-save snapshot or associate its old fingerprint with the new file.
                try { loaded = RefactorPlanFiles.Read(picker.FileName, textBinding); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException or System.Security.SecurityException)
                { PlanFailure($"Could not reload Refactor plan '{picker.FileName}' after saving: {ex.Message}"); return; }
            }
        }
        ApplyLoadedPlan(RefactorPlanFiles.FilePath(picker.FileName), loaded);
    }

    public async Task<bool> RebindPlanToCurrentAsync()
    {
        if (IsExecuting) return PlanFailure("Wait for execution to finish before rebinding.");
        LastPlanError = null;
        if (browsingProfile is null || Snapshot is null || editingCommandId is not null) return PlanFailure("Select valid tag folders and finish the current edit before rebinding.");
        var revision = planRevision;
        RefreshHierarchy(); var version = scanVersion;
        await HierarchyRefresh;
        if (Snapshot is null || scanVersion != version || planRevision != revision) return PlanFailure("The folders or queue changed during revalidation. Review them and try rebinding again.");
        var binding = Binding(browsingProfile.Id, browsingProfile.Name, browsingProfile.WorkingDirectory, Snapshot.Roots);
        var replay = RefactorPlanner.Replay(Snapshot, pendingCommands);
        SetBinding(binding); MarkPlanChanged(); UpdatePlanViews(replay);
        if (!replay.IsValid)
        { EditorValidation.Foreground = Brushes.Salmon; EditorValidation.Text = "Plan rebound with conflicts. Repair the flagged commands before execution.\n" + string.Join("\n", replay.Entries.SelectMany(entry => entry.Errors)); }
        return true;
    }

    private async void RebindPlanClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var oldRoots = string.Join("\n", (boundRoots ?? []).Select((root, index) => $"{index + 1}. {root.Path}"));
        var newRoots = string.Join("\n", (Snapshot?.Roots ?? []).Select((root, index) => $"{index + 1}. {root.Path}"));
        if (MessageBox.Show(owner, $"Current plan: {boundProfileName}\n{oldRoots}\n\nSelected profile: {browsingProfile?.Name}\n{newRoots}\n\nUse the selected profile and folders for this plan? Tags will be refreshed and commands revalidated. No tag files are changed.",
            "Rebind Refactor plan", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        planFileBusy = true; UpdatePlanFileUi();
        try { await RebindPlanToCurrentAsync(); }
        finally { planFileBusy = false; UpdatePlanFileUi(); if (LastPlanError is not null) PlanFailure(LastPlanError); }
    }

    public void Discard()
    {
        if (IsExecuting) return;
        executionErrors.Clear();
        pendingCommands.Clear(); boundProfileId = null; boundProfileName = boundWorkingDirectory = null; boundRoots = null;
        rememberedBinding = null; PlanFilePath = savedContentHash = null; savedPlanSignature = ""; editingCommandId = null;
        saveNeedsRetry = false; ClearEditor(); LastPlanError = null; planRevision++; UpdatePlanViews(); UpdatePlanFileUi();
    }

    public void CancelPendingPlanOperations() => planIoVersion++;
}
