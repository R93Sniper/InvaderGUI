using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InvaderGui.Core;

public interface IVersionedDocument { int SchemaVersion { get; set; } }

public sealed class SettingsDocument : IVersionedDocument
{
    public int SchemaVersion { get; set; } = 1;
    public InstallationSettings Installation { get; set; } = new();
    public Guid? SelectedProfileId { get; set; }
    public WindowSettings Window { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public LogSettings Logs { get; set; } = new();
}
public sealed class InstallationSettings
{
    public string Directory { get; set; } = "";
    public DateTimeOffset? LastScanUtc { get; set; }
    public Dictionary<string, CachedAvailability> ToolAvailability { get; set; } = new();
}
public sealed record CachedAvailability(bool Available, string? Reason);
public sealed class WindowSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 720;
    public string State { get; set; } = "normal";
}
public sealed class UiSettings
{
    public string LastPageId { get; set; } = "build-map";
    public bool ActivityPaneOpen { get; set; }
    public string Theme { get; set; } = "dark";
    public double SidebarWidth { get; set; } = 190;
    public double RefactorHierarchyHeight { get; set; } = 320;
    public double RefactorQueueWidth { get; set; } = 190;
    public Dictionary<string, string> RefactorTagFolderSelections { get; set; } = new();
    public Dictionary<string, string> OperationSelections { get; set; } = new();
}
public sealed class LogSettings
{
    public string Directory { get; set; } = "logs";
    public int RetainRunCount { get; set; } = 200;
    public bool OpenActivityOnFailure { get; set; } = true;
}
public sealed class ProfilesDocument : IVersionedDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ProjectProfile> Profiles { get; set; } = new();
}
public sealed class ProjectProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public string WorkingDirectory { get; set; } = "";
    public ProfilePaths Paths { get; set; } = new();
    public Dictionary<string, JsonElement> OperationState { get; set; } = new();
    public override string ToString() => Name;

    public ProjectProfile Copy() => JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(this, JsonFiles.Options), JsonFiles.Options)!;

    public static ProjectProfile CreateNew(string name = "New profile") => new()
    {
        Name = name,
        Paths = new ProfilePaths { TagRoots = new() { new TagRoot() } }
    };

    public bool RemoveTagRoot(string rootId)
    {
        var root = Paths.TagRoots.FirstOrDefault(r => r.Id == rootId);
        if (root is null || Paths.TagRoots.Count <= 1) return false;
        Paths.TagRoots.Remove(root);
        var replacementId = Paths.TagRoots[0].Id;
        foreach (var (operation, saved) in OperationState.ToArray())
        {
            if (saved.ValueKind != JsonValueKind.Object) continue;
            var settings = JsonNode.Parse(saved.GetRawText())!.AsObject();
            bool changed = false;
            if (settings["selectedTagRootId"] is JsonValue selected && selected.TryGetValue<string>(out var selectedId) && selectedId == rootId)
            {
                settings["selectedTagRootId"] = replacementId;
                changed = true;
            }
            if (settings["tagRootIds"] is JsonArray ids)
            {
                bool removedSelection = false;
                for (int i = ids.Count - 1; i >= 0; i--)
                {
                    if (ids[i] is JsonValue value && value.TryGetValue<string>(out var id) && id == rootId)
                    {
                        ids.RemoveAt(i);
                        removedSelection = true;
                        changed = true;
                    }
                }
                if (removedSelection && ids.Count == 0) ids.Add(replacementId);
            }
            if (changed) OperationState[operation] = JsonSerializer.SerializeToElement(settings, JsonFiles.Options);
        }
        return true;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Id == Guid.Empty) errors.Add("Profile identity is missing.");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Enter a profile name.");
        if (!Path.IsPathFullyQualified(WorkingDirectory) || !System.IO.Directory.Exists(WorkingDirectory))
            errors.Add("Select an existing absolute Editing Kit Folder.");
        if (Paths.TagRoots.Count == 0) errors.Add("Add at least one tag root.");
        if (Paths.TagRoots.Select(r => r.Id).Distinct().Count() != Paths.TagRoots.Count)
            errors.Add("Tag root identities must be unique.");
        foreach (var root in Paths.TagRoots)
        {
            if (string.IsNullOrWhiteSpace(root.Id) || string.IsNullOrWhiteSpace(root.Name)) errors.Add("Every tag root needs an identity and name.");
            ValidatePath(root.Path, "Tag root", errors);
        }
        ValidatePath(Paths.DataRoot, "Data root", errors);
        ValidatePath(Paths.MapsRoot, "Maps root", errors);
        if (!string.IsNullOrWhiteSpace(Paths.ResourceMapsRoot)) ValidatePath(Paths.ResourceMapsRoot, "Resource maps root", errors);
        return errors;
    }

    private void ValidatePath(string path, string label, List<string> errors)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException();
            _ = Path.GetFullPath(path, WorkingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { errors.Add($"{label} must be a valid absolute path or a path relative to the Editing Kit Folder."); }
    }
}
public sealed class ProfilePaths
{
    public string DataRoot { get; set; } = "data";
    public List<TagRoot> TagRoots { get; set; } = new();
    public string MapsRoot { get; set; } = "maps";
    public string? ResourceMapsRoot { get; set; }
    [JsonIgnore]
    public string EffectiveResourceMapsRoot => string.IsNullOrWhiteSpace(ResourceMapsRoot) ? MapsRoot : ResourceMapsRoot;
}
public sealed class TagRoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "Tags";
    public string Path { get; set; } = "tags";
    public override string ToString() => $"{Name} — {Path}";
}
public sealed class PresetsDocument : IVersionedDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<OperationPreset> Presets { get; set; } = new();
}
public sealed class OperationPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string OperationId { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, JsonElement> Settings { get; set; } = new();
}

public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>A failed read locks saving until the user repairs the original or explicitly preserves it as a backup.</summary>
public sealed class JsonDocumentStore<T> where T : class, IVersionedDocument, new()
{
    public string FilePath { get; }
    public T Value { get; private set; } = new();
    public bool IsDirty { get; private set; }
    public bool ReadBlocked { get; private set; }
    public string? Error { get; private set; }
    private readonly Action<T>? validate;

    public JsonDocumentStore(string filePath, Action<T>? validate = null)
    { FilePath = filePath; this.validate = validate; }

    public bool Load()
    {
        try
        {
            T loaded;
            if (!File.Exists(FilePath))
            {
                // File.Exists also returns false for inaccessible files. Opening distinguishes those cases.
                try { using var ignored = File.OpenRead(FilePath); }
                catch (FileNotFoundException) { loaded = new(); Value = loaded; ReadBlocked = false; Error = null; IsDirty = false; return true; }
                catch (DirectoryNotFoundException) { Value = new(); ReadBlocked = false; Error = null; IsDirty = false; return true; }
            }
            var json = File.ReadAllText(FilePath);
            using var syntax = JsonDocument.Parse(json);
            if (!syntax.RootElement.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1)
                throw new JsonException("Expected schemaVersion 1. This document has not been modified.");
            loaded = JsonSerializer.Deserialize<T>(json, JsonFiles.Options) ?? throw new JsonException("Document is null.");
            validate?.Invoke(loaded);
            Value = loaded; ReadBlocked = false; IsDirty = false; Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException)
        { ReadBlocked = true; Error = $"{FilePath}\n{ex.Message}"; return false; }
    }

    public void MarkDirty() => IsDirty = true;

    public bool Save()
    {
        IsDirty = true;
        if (ReadBlocked) return false;
        try { WriteAtomic(FilePath); Error = null; IsDirty = false; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Error = $"{FilePath}\n{ex.Message}"; return false; }
    }

    public void Export(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a different export path to preserve the original configuration.");
        WriteAtomic(path);
    }

    public void PreserveOriginalAndUnlock()
    {
        if (!ReadBlocked) return;
        // File.Move must succeed before saving is unlocked; inaccessible originals remain protected.
        File.Move(FilePath, FilePath + $".recovery-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak");
        ReadBlocked = false; Error = null; IsDirty = true;
    }

    private void WriteAtomic(string path)
    {
        validate?.Invoke(Value);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, Value, JsonFiles.Options);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class DocumentValidation
{
    public static void Settings(SettingsDocument document)
    {
        if (document.Installation is null || document.Window is null || document.Ui is null || document.Logs is null ||
            document.Installation.Directory is null || document.Installation.ToolAvailability is null || document.Ui.OperationSelections is null ||
            document.Ui.RefactorTagFolderSelections is null ||
            !double.IsFinite(document.Ui.SidebarWidth) ||
            !double.IsFinite(document.Ui.RefactorHierarchyHeight) || !double.IsFinite(document.Ui.RefactorQueueWidth) ||
            string.IsNullOrWhiteSpace(document.Logs.Directory) || document.Logs.RetainRunCount < 1 ||
            !double.IsFinite(document.Window.Width) || !double.IsFinite(document.Window.Height))
            throw new JsonException("Invalid settings structure or log/window values.");
    }
    public static void Profiles(ProfilesDocument document)
    {
        if (document.Profiles is null || document.Profiles.Any(p => p is null || p.Id == Guid.Empty || p.Name is null ||
            p.WorkingDirectory is null || p.Paths is null || p.OperationState is null || p.Paths.TagRoots is null ||
            p.Paths.DataRoot is null || p.Paths.MapsRoot is null || p.Paths.TagRoots.Any(r => r is null ||
                string.IsNullOrWhiteSpace(r.Id) || r.Name is null || r.Path is null)) ||
            document.Profiles.Select(p => p.Id).Distinct().Count() != document.Profiles.Count)
            throw new JsonException("Invalid profiles structure or duplicate profile identities.");
    }
    public static void Presets(PresetsDocument document)
    {
        if (document.Presets is null || document.Presets.Any(p => p is null || p.Settings is null || p.Name is null || p.OperationId is null))
            throw new JsonException("Invalid presets structure.");
    }
}

