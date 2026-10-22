using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvaderGui.Core;

public abstract class TagHandlingState
{
    public int SchemaVersion { get; set; } = 1;
    public string? SelectedTagRootId { get; set; }
    public string Input { get; set; } = "";
    public bool FilesystemPath { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
public abstract class BatchTagState : TagHandlingState
{
    public bool Batch { get; set; }
    public List<string> Includes { get; set; } = new();
    public List<string> Excludes { get; set; } = new();
}
public sealed class RepairTagsState : BatchTagState
{
    public string RepairMode { get; set; } = "detect";
    public List<string> Repairs { get; set; } = new();
    public string? Threads { get; set; }
    public string ModelReferences { get; set; } = "unchanged";
    public bool StripHiddenData { get; set; }
}
public sealed class ConvertTagsState : BatchTagState
{
    public string FromGroup { get; set; } = "";
    public string ToGroup { get; set; } = "";
    public string? OutputTags { get; set; }
    public bool Overwrite { get; set; }
}
public sealed class StripTagsState : BatchTagState { }
public sealed class DependencyState : TagHandlingState
{
    public List<string>? TagRootIds { get; set; }
    public bool Reverse { get; set; }
    public bool Recursive { get; set; }
}

public static class TagHandlingStates
{
    public static TagHandlingState New(string operation) => operation switch
    {
        "repair-tags" => new RepairTagsState(), "convert-tags" => new ConvertTagsState(),
        "strip-tags" => new StripTagsState(), "dependencies" => new DependencyState(),
        _ => throw new ArgumentException("Unknown Tag Handling operation.")
    };
    public static JsonElement Write(TagHandlingState state) => JsonSerializer.SerializeToElement(state, state.GetType(), JsonFiles.Options);
    public static TagHandlingState Read(string operation, JsonElement json)
    {
        var state = (TagHandlingState?)json.Deserialize(New(operation).GetType(), JsonFiles.Options) ?? throw new JsonException("Saved tag form is null.");
        if (state.SchemaVersion != 1 || state.Input is null || state is BatchTagState { Includes: null } or BatchTagState { Excludes: null } ||
            state is RepairTagsState { RepairMode: null } or RepairTagsState { Repairs: null } ||
            state is ConvertTagsState { FromGroup: null } or ConvertTagsState { ToGroup: null } ||
            state is DependencyState dependency && dependency.TagRootIds?.Any(string.IsNullOrWhiteSpace) == true)
            throw new JsonException("Saved tag form has an unsupported version or invalid structure.");
        return state;
    }
    private static string[] PresetFields(string operation) => operation switch
    {
        "repair-tags" => ["batch", "repairMode", "repairs", "threads", "modelReferences", "stripHiddenData"],
        "convert-tags" => ["batch", "fromGroup", "toGroup", "overwrite"],
        "strip-tags" => ["batch"], "dependencies" => ["reverse", "recursive"],
        _ => throw new ArgumentException("Unknown Tag Handling operation.")
    };
    public static Dictionary<string, JsonElement> PresetSettings(string operation, TagHandlingState state)
    {
        var json = Write(state); return PresetFields(operation).ToDictionary(k => k, k => json.GetProperty(k).Clone());
    }
    public static TagHandlingState ApplyPreset(string operation, TagHandlingState state, OperationPreset preset)
    {
        if (preset.OperationId != operation || preset.Settings.Keys.Any(k => !PresetFields(operation).Contains(k)))
            throw new JsonException("This preset contains unsupported settings or input/output paths.");
        var fields = Write(state).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        foreach (var field in preset.Settings) fields[field.Key] = field.Value.Clone();
        var result = Read(operation, JsonSerializer.SerializeToElement(fields));
        var errors = TagHandlingBuilder.OptionErrors(result);
        if (errors.Count > 0) throw new JsonException(string.Join("\n", errors));
        return result;
    }
}

public sealed record TagHandlingInvocation(ProjectProfile EffectiveProfile, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Options, string Summary);
public sealed record TagHandlingValidation(TagHandlingInvocation? Invocation, IReadOnlyList<string> Errors);

public static class TagHandlingBuilder
{
    public static IReadOnlyList<string> Repairs { get; } = new[]
    {
        "broken-lens-flare-function-scale", "incorrect-sound-buffer", "invalid-enums", "invalid-indices", "invalid-strings",
        "invalid-model-markers", "invalid-reference-classes", "invalid-uppercase-references", "mismatched-sound-enums",
        "missing-script-source", "missing-vertices", "nonnormal-vectors", "out-of-range", "missing-bitmap-sequences"
    };
    public static IReadOnlyList<string> ObjectGroups { get; } = new[]
    {
        "biped", "vehicle", "weapon", "equipment", "garbage", "projectile", "scenery", "device_machine", "device_control",
        "device_light_fixture", "placeholder", "sound_scenery"
    };
    public static IReadOnlyList<string> ConversionGroups { get; } = new[]
    { "model", "gbxmodel", "shader_transparent_chicago_extended", "shader_transparent_chicago" }.Concat(ObjectGroups).ToArray();
    public static bool CanConvert(string from, string to) => from != to &&
        ((from is "model" && to is "gbxmodel") || (from is "gbxmodel" && to is "model") ||
        (from is "shader_transparent_chicago_extended" && to is "shader_transparent_chicago") || (ObjectGroups.Contains(from) && ObjectGroups.Contains(to)));

    public static IReadOnlyList<string> OptionErrors(TagHandlingState state)
    {
        var errors = new List<string>();
        if (state is RepairTagsState repair)
        {
            if (repair.RepairMode is not ("detect" or "selected" or "everything" or "skip")) errors.Add("Choose Detect only, Selected repairs, Everything, or Skip Bludgeon.");
            if (repair.ModelReferences is not ("unchanged" or "model-to-gbxmodel" or "gbxmodel-to-model")) errors.Add("Choose a supported model-reference action.");
            if (repair.RepairMode == "skip" && repair.ModelReferences == "unchanged" && !repair.StripHiddenData) errors.Add("Select a reference or cleanup action, or enable a Bludgeon action.");
            if (repair.RepairMode == "selected" && (repair.Repairs.Count == 0 || repair.Repairs.Any(r => !Repairs.Contains(r)) || repair.Repairs.Distinct().Count() != repair.Repairs.Count))
                errors.Add("Choose at least one supported repair without duplicates.");
            if (repair.RepairMode != "skip" && repair.Batch && repair.Threads is not null && (!int.TryParse(repair.Threads, NumberStyles.None, CultureInfo.InvariantCulture, out var threads) || threads < 1))
                errors.Add("Threads: use Automatic or a whole number from 1 to 2147483647.");
        }
        if (state is ConvertTagsState convert && !CanConvert(convert.FromGroup, convert.ToGroup)) errors.Add("Select a supported From/To conversion between different groups.");
        return errors;
    }

    public static TagHandlingValidation BuildRepair(RepairTagsState state, ProjectProfile? profile) => Build("repair-tags", state, profile);
    public static TagHandlingValidation BuildConvert(ConvertTagsState state, ProjectProfile? profile) => Build("convert-tags", state, profile);
    public static TagHandlingValidation BuildStrip(StripTagsState state, ProjectProfile? profile) => Build("strip-tags", state, profile);
    public static TagHandlingValidation BuildDependencies(DependencyState state, ProjectProfile? profile) => Build("dependencies", state, profile);

    public static IReadOnlyList<TagRoot> SelectRoots(TagHandlingState state, ProjectProfile profile)
    {
        if (state is DependencyState dependency && dependency.TagRootIds is null) return profile.Paths.TagRoots;
        var ids = state is DependencyState d ? d.TagRootIds! : new List<string> { state.SelectedTagRootId ?? "" };
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new ArgumentException("Choose tag folders without empty or duplicate selections.");
        return ids.Select(id => profile.Paths.TagRoots.SingleOrDefault(r => r.Id == id) ?? throw new ArgumentException("A saved tag folder is missing. Select its replacement explicitly.")).ToArray();
    }

    public static TagHandlingValidation Build(string operation, TagHandlingState state, ProjectProfile? profile)
    {
        if (state.GetType() != TagHandlingStates.New(operation).GetType()) throw new ArgumentException("State does not match its operation.");
        var errors = OptionErrors(state).ToList();
        if (profile is null) { errors.Add("Select an editing kit profile."); return new(null, errors); }
        errors.AddRange(profile.Validate()); if (errors.Count > 0) return new(null, errors);
        try
        {
            var p = profile.Copy();
            p.Paths.TagRoots = SelectRoots(state, p).ToList();
            foreach (var root in p.Paths.TagRoots)
            {
                root.Path = Path.GetFullPath(root.Path, p.WorkingDirectory);
                if (!Directory.Exists(root.Path)) errors.Add($"Tag folder '{root.Name}' does not exist: {root.Path}");
            }
            var args = new List<string>();
            void Value(string flag, string value) { args.Add(flag); args.Add(value); }
            var batch = state is BatchTagState { Batch: true };
            if (batch)
            {
                var b = (BatchTagState)state;
                if (b.Includes.Count + b.Excludes.Count == 0) errors.Add("Batch: add include/exclude patterns, or choose All tags.");
                foreach (var pattern in b.Includes)
                {
                    if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains('\0')) errors.Add("Include patterns cannot be empty or contain null characters.");
                    else Value("--batch", pattern);
                }
                foreach (var pattern in b.Excludes)
                {
                    if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains('\0')) errors.Add("Exclude patterns cannot be empty or contain null characters.");
                    else Value("--batch-exclude", pattern);
                }
            }
            if (state is RepairTagsState repair)
            {
                if (repair.RepairMode != "skip" && repair.Batch && repair.Threads is not null) Value("--threads", int.Parse(repair.Threads, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                if (repair.RepairMode == "everything") Value("--type", "everything");
                else if (repair.RepairMode == "selected") foreach (var type in repair.Repairs) Value("--type", type);
            }
            if (state is ConvertTagsState convert)
            {
                args.Add("--groups"); args.Add(convert.FromGroup); args.Add(convert.ToGroup);
                if (convert.OutputTags is not null)
                {
                    if (string.IsNullOrWhiteSpace(convert.OutputTags)) errors.Add("Destination tag folder: choose a folder or use the source folder.");
                    else
                    {
                        var output = Path.GetFullPath(convert.OutputTags, p.WorkingDirectory);
                        if (!Directory.Exists(output)) errors.Add("Destination tag folder must already exist.");
                        Value("--output-tags", output);
                    }
                }
                if (convert.Overwrite) args.Add("--overwrite");
            }
            if (state is DependencyState dependency)
            {
                if (dependency.Reverse) args.Add("--reverse");
                if (dependency.Recursive) args.Add("--recursive");
            }
            if (!batch && state.FilesystemPath && state is not ConvertTagsState) args.Add("--fs-path");
            foreach (var root in p.Paths.TagRoots) Value("--tags", root.Path);
            string target = "Batch";
            if (!batch)
            {
                var physicalMode = state.FilesystemPath && state is not ConvertTagsState;
                var input = physicalMode ? Path.GetFullPath(state.Input, p.WorkingDirectory) : state.Input;
                var logical = physicalMode ? LogicalFromFile(input, p.Paths.TagRoots.Select(r => r.Path)) : NormalizeLogical(input);
                if (physicalMode && state is DependencyState && File.Exists(input))
                {
                    var owner = p.Paths.TagRoots.Select(r => Path.Combine(r.Path, logical)).FirstOrDefault(File.Exists);
                    if (!string.Equals(owner, input, StringComparison.OrdinalIgnoreCase)) errors.Add("An earlier search folder owns this tag. Reorder the search folders to target the selected file.");
                }
                if (state is ConvertTagsState c)
                {
                    var extension = "." + c.FromGroup;
                    if (logical.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) logical = logical[..^extension.Length];
                    else if (Path.HasExtension(logical)) throw new ArgumentException("Input tag extension must match the selected From group.");
                    if (!File.Exists(Path.Combine(p.Paths.TagRoots[0].Path, logical + extension))) errors.Add("Source tag does not exist in the selected folder.");
                }
                else
                {
                    var group = Path.GetExtension(logical);
                    if (group.Length < 2 || !group[1..].All(ch => char.IsAsciiLetterLower(ch) || ch == '_')) errors.Add("Tag: include its group extension, for example .sky or .scenario.");
                    var allowMissing = state is DependencyState { Reverse: true } && !physicalMode;
                    if (!allowMissing && !p.Paths.TagRoots.Any(r => File.Exists(Path.Combine(r.Path, logical)))) errors.Add("Tag does not exist under the selected folder(s).");
                }
                args.Add(physicalMode ? input : logical); target = logical;
            }
            if (errors.Count > 0) return new(null, errors);
            var action = state is RepairTagsState r ? r.RepairMode : operation;
            var summary = $"{Operations.All.Single(o => o.Id == operation).Name}: {target}; action: {action}; tags: {string.Join(" → ", p.Paths.TagRoots.Select(r => r.Name))}";
            if (state is RepairTagsState workflow) summary += $"; model references: {workflow.ModelReferences}; Strip cleanup: {(workflow.StripHiddenData ? "enabled" : "off")}";
            var options = new Dictionary<string, string> { ["formState"] = TagHandlingStates.Write(state).GetRawText(), ["target"] = target, ["action"] = action };
            if (state is ConvertTagsState cv) options["outputTags"] = cv.OutputTags is null ? p.Paths.TagRoots[0].Path : Path.GetFullPath(cv.OutputTags, p.WorkingDirectory);
            return new(new(p, args.AsReadOnly(), options, summary), errors);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        { errors.Add(ex.Message); return new(null, errors); }
    }
    public static string NormalizeLogical(string path)
    {
        path = path.Replace('/', '\\');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split('\\').Any(c => c.Length == 0 || c is "." or ".." || c.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || c.EndsWith(' ') || c.EndsWith('.')))
            throw new ArgumentException("Enter a logical tag path beneath the selected folder, without traversal or wildcard characters.");
        return path;
    }
    public static string LogicalFromFile(string file, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(file));
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith("..\\", StringComparison.Ordinal)) return NormalizeLogical(relative);
        }
        throw new ArgumentException("The selected file must be inside a selected tag folder.");
    }
}
