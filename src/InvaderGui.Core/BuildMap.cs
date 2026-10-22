using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvaderGui.Core;

// Text for editable numeric overrides deliberately retains incomplete/invalid edits across navigation.
// The builder is the only boundary that converts those edits to validated CLI values.
public sealed class BuildMapState
{
    public int SchemaVersion { get; set; } = 1;
    public string Scenario { get; set; } = "";
    public bool FilesystemPath { get; set; }
    public string Engine { get; set; } = "";
    public string? DataOverride { get; set; }
    public string? MapsOverride { get; set; }
    public List<string>? TagRootIds { get; set; }
    public string ResourceDirectoryMode { get; set; } = "maps";
    public string ResourceDirectory { get; set; } = "";
    public string? Output { get; set; }
    public bool Optimize { get; set; }
    public bool HideWarnings { get; set; }
    public bool ExtendLimits { get; set; }
    public bool Quiet { get; set; }
    public bool Anniversary { get; set; }
    public bool StockBounds { get; set; }
    public bool AutoForge { get; set; }
    public string? ResourceUsage { get; set; }
    public string? ScriptSource { get; set; }
    public string? BuildString { get; set; }
    public string? ForgeCrc { get; set; }
    public string? RenameScenario { get; set; }
    public string? CompressionLevel { get; set; }
    public string? TagSpace { get; set; }
    public string? IndexFile { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static BuildMapState Read(JsonElement json)
    {
        var state = json.Deserialize<BuildMapState>(JsonFiles.Options) ?? throw new JsonException("Build Map state is null.");
        if (state.SchemaVersion != 1 || state.Scenario is null || state.Engine is null || state.ResourceDirectory is null ||
            state.ResourceDirectoryMode is null || state.TagRootIds?.Any(string.IsNullOrWhiteSpace) == true)
            throw new JsonException("Build Map state has an unsupported version or invalid structure.");
        return state;
    }

    // Deliberate allowlist: no inputs, root identities, filesystem paths, or path interpretation.
    public static IReadOnlyList<string> PresetFields { get; } = new[]
    {
        "engine", "optimize", "hideWarnings", "extendLimits", "quiet", "anniversary", "stockBounds", "autoForge",
        "resourceUsage", "scriptSource", "buildString", "forgeCrc", "renameScenario", "compressionLevel", "tagSpace"
    };
    public Dictionary<string, JsonElement> PresetSettings()
    {
        var json = JsonSerializer.SerializeToElement(this, JsonFiles.Options);
        return PresetFields.ToDictionary(k => k, k => json.GetProperty(k).Clone());
    }
    public BuildMapState ApplyPreset(OperationPreset preset)
    {
        if (preset.OperationId != "build-map" || preset.Settings.Keys.Any(k => !PresetFields.Contains(k)))
            throw new JsonException("This preset contains unsupported settings or filesystem paths.");
        var properties = JsonSerializer.SerializeToElement(this, JsonFiles.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        foreach (var field in preset.Settings) properties[field.Key] = field.Value.Clone();
        var result = Read(JsonSerializer.SerializeToElement(properties));
        var errors = BuildMapBuilder.OptionErrors(result);
        if (errors.Count > 0) throw new JsonException(string.Join("\n", errors));
        return result;
    }
}

public sealed record BuildMapInvocation(ProjectProfile EffectiveProfile, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Options, string ScenarioFile, string OutputFile, string Summary,
    IReadOnlyList<string> Warnings);
public sealed record BuildMapValidation(BuildMapInvocation? Invocation, IReadOnlyList<string> Errors);

public static class BuildMapBuilder
{
    public static IReadOnlyDictionary<string, string> Engines { get; } = new Dictionary<string, string>
    {
        ["gbx-custom"] = "Custom Edition", ["gbx-demo"] = "Halo Demo / Trial", ["gbx-retail"] = "Halo PC (retail)",
        ["mcc-cea"] = "MCC Combat Evolved Anniversary", ["native"] = "Invader Native",
        ["xbox-demo"] = "Xbox Demo", ["xbox-ntsc"] = "Xbox NTSC (US)", ["xbox-ntsc-jp"] = "Xbox NTSC (Japan)",
        ["xbox-ntsc-tw"] = "Xbox NTSC (Taiwan)", ["xbox-pal"] = "Xbox PAL"
    };
    public static bool SupportsResources(string engine) => engine is "gbx-custom" or "gbx-demo" or "gbx-retail" or "mcc-cea";
    public static bool IsXbox(string engine) => Engines.ContainsKey(engine) && engine.StartsWith("xbox-", StringComparison.Ordinal);

    public static IReadOnlyList<string> OptionErrors(BuildMapState s)
    {
        var errors = new List<string>();
        if (!Engines.ContainsKey(s.Engine)) errors.Add("Game engine: select a target engine.");
        if (s.ResourceUsage is not (null or "none" or "check" or "always")) errors.Add("Resources: select a supported usage.");
        if (SupportsResources(s.Engine) && s.ResourceUsage == "always" && s.Engine != "gbx-custom") errors.Add("Resources: Always index is available only for Custom Edition.");
        if (s.ScriptSource is not (null or "tags" or "data")) errors.Add("Script source: select Default, Tags, or Data.");
        if (s.BuildString is not null && (s.BuildString.Contains('\0') || Encoding.UTF8.GetByteCount(s.BuildString) >= 32)) errors.Add("Build string: use fewer than 32 UTF-8 bytes and no null characters.");
        if (s.ForgeCrc is not null && !TryCrc(s.ForgeCrc, out _)) errors.Add("Forge CRC: enter 1–8 hexadecimal digits, optionally prefixed with 0x.");
        if (s.TagSpace is not null && !TryTagSpace(s.TagSpace, out _)) errors.Add("Tag space: enter unsigned bytes, uppercase K/M, or 0x hexadecimal within UInt64 range.");
        if (IsXbox(s.Engine) && s.CompressionLevel is not null && (!int.TryParse(s.CompressionLevel, NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level > 9)) errors.Add("Xbox compression: enter an integer from 0 to 9.");
        if (s.RenameScenario is not null && !ValidComponent(s.RenameScenario)) errors.Add("Scenario name: enter a nonempty filename without a directory or extension.");
        return errors;
    }

    public static BuildMapValidation Build(BuildMapState s, ProjectProfile? profile)
    {
        var errors = OptionErrors(s).ToList();
        if (profile is null) { errors.Add("Select an editing kit profile."); return new(null, errors); }
        errors.AddRange(profile.Validate());
        if (errors.Count > 0) return new(null, errors);
        try
        {
            var p = profile.Copy();
            string Resolve(string path) => Path.GetFullPath(path, p.WorkingDirectory);
            p.Paths.DataRoot = Resolve(s.DataOverride ?? p.Paths.DataRoot);
            p.Paths.MapsRoot = Resolve(s.MapsOverride ?? p.Paths.MapsRoot);
            if (s.TagRootIds is not null)
            {
                if (s.TagRootIds.Count == 0 || s.TagRootIds.Distinct().Count() != s.TagRootIds.Count) errors.Add("Tag roots: select at least one root, without duplicates.");
                var selected = new List<TagRoot>();
                foreach (var id in s.TagRootIds)
                {
                    var root = p.Paths.TagRoots.FirstOrDefault(r => r.Id == id);
                    if (root is null) errors.Add($"Tag roots: saved root {id} no longer exists. Select roots explicitly.");
                    else selected.Add(root);
                }
                p.Paths.TagRoots = selected;
            }
            foreach (var root in p.Paths.TagRoots)
            {
                root.Path = Resolve(root.Path);
                if (!Directory.Exists(root.Path)) errors.Add($"Tag root '{root.Name}' does not exist: {root.Path}");
            }
            if (s.ScriptSource == "data" && !Directory.Exists(p.Paths.DataRoot)) errors.Add($"Data directory does not exist: {p.Paths.DataRoot}");
            string logical = "", scenarioFile = "";
            if (s.FilesystemPath)
            {
                scenarioFile = Resolve(s.Scenario);
                logical = ScenarioFromFile(scenarioFile, p.Paths.TagRoots.Select(r => r.Path));
            }
            else
            {
                logical = NormalizeScenario(s.Scenario);
                scenarioFile = p.Paths.TagRoots.Select(r => Path.Combine(r.Path, logical + ".scenario")).FirstOrDefault(File.Exists) ?? "";
            }
            if (!File.Exists(scenarioFile)) errors.Add("Scenario: select an existing .scenario tag under the effective tag roots.");
            if (s.FilesystemPath && File.Exists(scenarioFile))
            {
                var firstMatch = p.Paths.TagRoots.Select(r => Path.Combine(r.Path, logical + ".scenario")).FirstOrDefault(File.Exists);
                if (!string.Equals(firstMatch, scenarioFile, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Scenario: an earlier tag root owns this logical path. Reorder the roots to select this scenario; filesystem interpretation does not change tag precedence.");
            }
            var outputName = s.RenameScenario ?? Path.GetFileName(logical);
            var output = s.Output is null ? Path.Combine(p.Paths.MapsRoot, outputName + ".map") : Resolve(Required(s.Output, "Output map"));
            if (!Directory.Exists(Path.GetDirectoryName(output))) errors.Add($"Output map: parent folder does not exist: {Path.GetDirectoryName(output)}");
            if (Directory.Exists(output)) errors.Add("Output map: select a file, not a directory.");
            if (string.Equals(output, scenarioFile, StringComparison.OrdinalIgnoreCase)) errors.Add("Output map cannot replace the scenario input.");
            var usingResources = SupportsResources(s.Engine) && s.ResourceUsage is "check" or "always";
            string? resourceOverride = null;
            if (usingResources)
            {
                resourceOverride = s.ResourceDirectoryMode switch
                {
                    "maps" => null,
                    "profile" => Resolve(Required(p.Paths.ResourceMapsRoot, "Profile resource-map directory")),
                    "custom" => Resolve(Required(s.ResourceDirectory, "Resource-map directory")),
                    _ => throw new ArgumentException("Resource maps: choose Maps directory, Profile, or Custom.")
                };
                var directory = resourceOverride ?? p.Paths.MapsRoot;
                foreach (var name in s.Engine == "gbx-custom" ? new[] { "bitmaps.map", "sounds.map", "loc.map" } : new[] { "bitmaps.map", "sounds.map" })
                    if (!File.Exists(Path.Combine(directory, name))) errors.Add($"Resource maps: missing {name} in {directory}.");
            }
            p.Paths.ResourceMapsRoot = usingResources ? resourceOverride ?? p.Paths.MapsRoot : null;
            var index = s.IndexFile is null ? null : Resolve(Required(s.IndexFile, "Index file"));
            if (index is not null && !File.Exists(index)) errors.Add("Index file: select an existing file.");
            if (errors.Count > 0) return new(null, errors);
            var args = new List<string>();
            void Value(string flag, string? value) { if (value is not null) { args.Add(flag); args.Add(value); } }
            void Flag(string flag, bool enabled) { if (enabled) args.Add(flag); }
            Flag("--anniversary-mode", s.Engine == "mcc-cea" && s.Anniversary);
            Flag("--auto-forge", s.AutoForge);
            Flag("--stock-resource-bounds", s.Engine == "gbx-custom" && s.StockBounds);
            Value("--build-string", s.BuildString);
            Value("--forge-crc", s.ForgeCrc is null ? null : NormalizeCrc(s.ForgeCrc));
            Value("--data", p.Paths.DataRoot);
            Flag("--extend-file-limits", s.ExtendLimits);
            Value("--game-engine", s.Engine);
            Flag("--hide-pedantic-warnings", s.HideWarnings);
            Value("--level", IsXbox(s.Engine) ? s.CompressionLevel : null);
            Value("--maps", p.Paths.MapsRoot);
            Value("--rename-scenario", s.RenameScenario);
            Value("--output", s.Output is null ? null : output);
            Flag("--optimize", s.Optimize);
            Flag("--fs-path", s.FilesystemPath);
            Flag("--quiet", s.Quiet);
            Value("--resource-usage", SupportsResources(s.Engine) ? s.ResourceUsage : null);
            Value("--resource-maps", resourceOverride);
            Value("--script-source", s.ScriptSource);
            foreach (var root in p.Paths.TagRoots) Value("--tags", root.Path);
            Value("--tag-space", s.TagSpace is null ? null : NormalizeTagSpace(s.TagSpace));
            Value("--with-index", index);
            args.Add(s.FilesystemPath ? scenarioFile : logical);
            var warnings = new List<string>();
            if (File.Exists(output)) warnings.Add("The existing output map will be replaced by Invader.");
            if (!output.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) warnings.Add("The output extension differs from .map; the target game may not load it.");
            if (Path.GetFileNameWithoutExtension(output) != outputName && s.Engine != "mcc-cea") warnings.Add("The output basename differs from the scenario name; the target game may not load it.");
            var summary = $"Scenario: {logical}; engine: {Engines[s.Engine]}; tags: {string.Join(" → ", p.Paths.TagRoots.Select(r => r.Name))}; output: {output}";
            var options = new Dictionary<string, string> { ["formState"] = JsonSerializer.Serialize(s, JsonFiles.Options), ["scenario"] = logical,
                ["scenarioFile"] = scenarioFile, ["engine"] = s.Engine, ["output"] = output, ["resourceUsage"] = SupportsResources(s.Engine) ? s.ResourceUsage ?? "none (default)" : "none (unavailable)" };
            return new(new(p, args.AsReadOnly(), options, scenarioFile, output, summary, warnings.AsReadOnly()), errors);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        { errors.Add(ex.Message); return new(null, errors); }
    }

    private static string Required(string? value, string label) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{label}: enter a path.");
    private static bool ValidComponent(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.Contains('.') && !value.EndsWith(' ');
    public static string NormalizeScenario(string value)
    {
        value = value.Replace('/', '\\');
        if (value.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) value = value[..^9];
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Split('\\').Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Scenario: enter a logical path beneath a tag root, such as levels\\test\\bloodgulch\\bloodgulch.");
        return value;
    }
    public static string ScenarioFromFile(string file, IEnumerable<string> roots)
    {
        if (!file.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Scenario: select a .scenario file.");
        foreach (var root in roots)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(file));
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith("..\\", StringComparison.Ordinal)) return NormalizeScenario(relative);
        }
        throw new ArgumentException("Scenario: the selected file must be beneath an effective tag root.");
    }
    private static bool TryCrc(string text, out uint value)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        value = 0;
        return hex.Length is >= 1 and <= 8 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
    private static string NormalizeCrc(string text) { TryCrc(text, out var value); return "0x" + value.ToString("X8", CultureInfo.InvariantCulture); }
    private static bool TryTagSpace(string text, out ulong value)
    {
        value = 0;
        if (text.StartsWith("0x", StringComparison.Ordinal)) return text.Length > 2 && ulong.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        ulong scale = text.EndsWith('K') ? 1024UL : text.EndsWith('M') ? 1048576UL : 1;
        var digits = scale == 1 ? text : text[..^1];
        if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > ulong.MaxValue / scale) return false;
        value = count * scale; return true;
    }
    private static string NormalizeTagSpace(string text) { TryTagSpace(text, out var value); return value.ToString(CultureInfo.InvariantCulture); }
}
