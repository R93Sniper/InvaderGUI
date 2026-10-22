using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using InvaderGui.Core;

namespace InvaderGui.App;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class NavigationItem : Bindable
{
    public string SlotId { get; }
    public string Name { get; }
    private string label;
    public string Label { get => label; set { if (label != value) { label = value; Changed(); } } }
    public NavigationItem(string slotId, string name) { SlotId = slotId; Name = name; label = name; }
}

public sealed class ShellViewModel : Bindable
{
    public JsonDocumentStore<SettingsDocument> Settings { get; }
    public JsonDocumentStore<ProfilesDocument> Profiles { get; }
    public JsonDocumentStore<PresetsDocument> Presets { get; }
    public TaskCoordinator Coordinator { get; } = new();
    public IUnsavedPlanGuard PlanGuard { get; set; } = new NoUnsavedPlan();
    public InstallationScan? Scan { get; set; }
    public List<RunView> History { get; private set; } = new();
    public ObservableCollection<NavigationItem> Navigation { get; } = new();
    public ObservableCollection<ProjectProfile> ProfileItems { get; } = new();
    private ProjectProfile? selectedProfile;
    public ProjectProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            selectedProfile = value;
            Settings.Value.SelectedProfileId = value?.Id;
            Settings.MarkDirty(); Changed();
        }
    }
    public string ApplicationDirectory { get; } = AppContext.BaseDirectory;
    public string LogsDirectory => Path.GetFullPath(Settings.Value.Logs.Directory, ApplicationDirectory);
    public string? StartupWarning { get; private set; }

    public ShellViewModel()
    {
        Settings = new(Path.Combine(ApplicationDirectory, "invader-gui.settings.json"), DocumentValidation.Settings);
        Profiles = new(Path.Combine(ApplicationDirectory, "invader-gui.profiles.json"), DocumentValidation.Profiles);
        Presets = new(Path.Combine(ApplicationDirectory, "presets.json"), DocumentValidation.Presets);
        Settings.Load(); Profiles.Load(); Presets.Load();
        if (Operations.NormalizeSelections(Settings.Value.Ui.OperationSelections)) Settings.MarkDirty();
        foreach (var group in Operations.Visible.GroupBy(o => o.SlotId))
            Navigation.Add(new(group.Key, group.Key switch { "asset-interchange" => "Asset Interchange", "tag-handling" => "Tag Handling", "map-tools" => "Map Tools", _ => group.First().Name }));
        RefreshProfiles(Settings.Value.SelectedProfileId);
        try
        {
            History = RunHistory.Load(LogsDirectory, Settings.Value.Logs.RetainRunCount, out var warning).ToList();
            StartupWarning = warning;
        }
        catch (Exception ex) { StartupWarning = $"History could not be loaded: {ex.Message}"; }
    }

    public void RefreshProfiles(Guid? preferredId)
    {
        ProfileItems.Clear();
        foreach (var profile in Profiles.Value.Profiles) ProfileItems.Add(profile);
        SelectedProfile = ProfileItems.FirstOrDefault(p => p.Id == preferredId) ?? ProfileItems.FirstOrDefault();
    }

    public async Task<InstallationScan> RescanAsync(string directory)
    {
        var scan = await Task.Run(() => InstallationDiscovery.Scan(directory));
        Scan = scan;
        Settings.Value.Installation.Directory = directory;
        Settings.Value.Installation.LastScanUtc = scan.ScannedUtc;
        Settings.Value.Installation.ToolAvailability = scan.Tools.ToDictionary(t => t.Executable, t => new CachedAvailability(t.Available, t.Reason));
        Settings.MarkDirty();
        return scan;
    }

    public IReadOnlyList<string> StorageErrors => new[] { Settings.Error, Profiles.Error, Presets.Error }.Where(e => e is not null).Cast<string>().ToArray();

    public IReadOnlyList<string> ClearCompletedLogs(IEnumerable<Guid> runIds, bool all = false)
    {
        var errors = new List<string>();
        string? warning = null;
        var saved = all ? RunHistory.Load(LogsDirectory, int.MaxValue, out warning).ToArray() : [];
        if (all && warning is not null) errors.Add(warning);
        var ids = all ? runIds.Concat(Coordinator.Runs.Select(run => run.Snapshot.RunId)).Concat(History.Select(run => run.Snapshot.RunId)).Concat(saved.Select(run => run.Snapshot.RunId)) : runIds;
        foreach (var id in ids.Distinct().ToArray())
        {
            var live = Coordinator.Runs.FirstOrDefault(run => run.Snapshot.RunId == id);
            var view = live?.View ?? History.FirstOrDefault(run => run.Snapshot.RunId == id) ?? saved.FirstOrDefault(run => run.Snapshot.RunId == id);
            if (view is null) continue;
            if (view.Active || live is not null && !live.Completion.IsCompleted) continue;
            try
            {
                RunHistory.DeleteFiles(view, LogsDirectory);
                Coordinator.ForgetCompleted(id);
                History.RemoveAll(run => run.Snapshot.RunId == id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            { errors.Add($"{view.Snapshot.OperationName}: {ex.Message}"); }
        }
        return errors;
    }

    public bool SaveAll()
    {
        var settings = Settings.Save();
        var profiles = Profiles.Save();
        var presets = Presets.Save();
        return settings && profiles && presets;
    }
}
