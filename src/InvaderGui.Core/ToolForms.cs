using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvaderGui.Core;

public sealed class ToolFormState
{
    public int SchemaVersion { get; set; } = 1;
    public string Input { get; set; } = "";
    public string SecondaryInput { get; set; } = "";
    public string? Output { get; set; }
    public bool FilesystemPath { get; set; }
    public List<string>? TagRootIds { get; set; }
    public string? DataOverride { get; set; }
    public string? MapsOverride { get; set; }
    public string? ResourceDirectory { get; set; }
    public string? Concatenate { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
    public List<string> Includes { get; set; } = new();
    public List<string> Excludes { get; set; } = new();
    public List<string> ExclusionRoots { get; set; } = new();
    public List<string> MatchedExclusionRoots { get; set; } = new();
    public List<string> ScriptSources { get; set; } = new();
    public List<ComparisonInput> ComparisonInputs { get; set; } = new();
    public List<ResourceSource> ResourceSources { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    public string Get(string key, string fallback = "") => Options.GetValueOrDefault(key, fallback);
    public bool On(string key) => Get(key) == "true";
    public JsonElement Write() => JsonSerializer.SerializeToElement(this, JsonFiles.Options);
    public static ToolFormState Read(JsonElement json)
    {
        var state = json.Deserialize<ToolFormState>(JsonFiles.Options) ?? throw new JsonException("Saved form is null.");
        if (state.SchemaVersion != 1 || state.Input is null || state.SecondaryInput is null || state.Options is null ||
            state.Includes is null || state.Excludes is null || state.ExclusionRoots is null || state.MatchedExclusionRoots is null ||
            state.ScriptSources is null || state.ComparisonInputs is null || state.ResourceSources is null ||
            state.Options.Any(p => p.Value is null) || state.TagRootIds?.Any(string.IsNullOrWhiteSpace) == true || state.ComparisonInputs.Any(i => i is null || i.TagDirectories is null || i.Map is null || i.Kind is null) ||
            state.ResourceSources.Any(i => i is null || i.Kind is null || i.Path is null)) throw new JsonException("Unsupported saved form version or invalid structure.");
        return state;
    }
}
public sealed class ComparisonInput
{
    public string Kind { get; set; } = "map";
    public string Map { get; set; } = "";
    public string? ResourceDirectory { get; set; }
    public bool IgnoreResources { get; set; }
    public List<string> TagDirectories { get; set; } = new();
}
public sealed class ResourceSource
{
    public string Kind { get; set; } = "map";
    public string Path { get; set; } = "";
}

// Hand-authored graphical controls and applicability rules for this fixed CLI release.
// These are not discovered from command help or generated from reflection.
public sealed record ToolOption(string Key, string Label, string? Argument, string Kind,
    string[] Choices, string Default, double? Minimum, double? Maximum, string Tip,
    Func<ToolFormState, bool>? When = null)
{
    public bool Active(ToolFormState state) => When?.Invoke(state) ?? true;
}
public static class ToolForms
{
    private static ToolOption Flag(string key, string label, string argument, string tip, Func<ToolFormState, bool>? when = null) =>
        new(key, label, argument, "flag", [], "", null, null, tip, when);
    private static ToolOption Select(string key, string label, string? argument, string choices, string fallback = "", Func<ToolFormState, bool>? when = null, string? tip = null) =>
        new(key, label, argument, "choice", choices.Split('|'), fallback, null, null, tip ?? "Default omits this setting and lets Invader choose or retain the existing tag setting.", when);
    private static ToolOption Number(string key, string label, string argument, double min, double max, bool integer = false, Func<ToolFormState, bool>? when = null) =>
        new(key, label, argument, integer ? "integer" : "number", [], "", min, max, $"Optional override, {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}. Unchecked retains Invader's default or existing tag setting.", when);
    private static bool Disk(ToolFormState s) => s.Get("mode", "disk") == "disk";
    private static bool Vorbis(ToolFormState s) => s.Get("format") is "" or "ogg_vorbis";
    private static readonly string Engines = string.Join('|', BuildMapBuilder.Engines.Keys);
    public static readonly string[] InfoQueries = ("overview|build|compression_ratio|crc32|crc32_mismatched|engine|external_bitmaps|external_bitmaps_count|external_bitmap_indices|external_bitmap_indices_count|external_bitmap_pointers|external_bitmap_pointers_count|external_indices|external_indices_count|external_loc_indices|external_loc_indices_count|external_sounds|external_sounds_count|external_sound_indices|external_sound_indices_count|external_sound_pointers|external_sound_pointers_count|external_tags|external_tags_count|internal_bitmaps|internal_bitmaps_count|internal_sounds|internal_sounds_count|is_compressed|is_dirty|is_protected|languages|map_type|protection_issues|scenario|scenario_path|stub_count|tag_order_match|tags|tags_count|uncompressed_size|uses_external_pointers").Split('|');
    public static readonly string[] SoundClasses = ("ambient_computers|ambient_machinery|ambient_nature|device_computers|device_door|device_force_field|device_machinery|device_nature|first_person_damage|game_event|music|object_impacts|particle_impacts|projectile_impact|projectile_detonation|scripted_dialog_force_unspatialized|scripted_dialog_other|scripted_dialog_player|scripted_effect|slow_particle_impacts|unit_dialog|unit_footsteps|vehicle_collision|vehicle_engine|weapon_charge|weapon_empty|weapon_fire|weapon_idle|weapon_overheat|weapon_ready|weapon_reload").Split('|');
    public static readonly IReadOnlyDictionary<string, ToolOption[]> Fields = new Dictionary<string, ToolOption[]>
    {
        ["archive-tags"] = [
            Select("target", "Target", null, "scenario|single-tag", "scenario"),
            Select("mode", "Output mode", null, "archive|copy", "archive"),
            Select("engine", "Engine", "--game-engine", Engines, when: s => s.Get("target", "scenario") == "scenario"),
            Select("format", "Archive format", "--format", "7z|tar-gz|tar-xz|tar-zst|zip", when: s => s.Get("mode", "archive") == "archive"),
            Flag("overwrite", "Overwrite copied tags", "--overwrite", "Replace destination tags in Copy mode.", s => s.Get("mode", "archive") == "copy"),
            Flag("verbose", "Detailed output", "--verbose", "Report omitted tags and comparisons.")],
        ["resource-maps"] = [
            Select("engine", "Engine", "--game-engine", "gbx-custom|gbx-demo|gbx-retail|mcc-cea"),
            Select("type", "Resource type", "--type", "bitmaps|sounds|loc"),
            Flag("no-stock", "Exclude stock tags", "--no-stock", "Do not automatically include stock Custom Edition tags.", s => s.Get("engine") == "gbx-custom"),
            Flag("show-matched", "Show matched tags", "--show-matched", "Report tags matching the concatenated resource map.", s => s.Concatenate is not null && s.Get("type") != "loc")],
        ["import-bitmaps"] = [
            Select("source", "Source", null, "image|embedded-color-plate", "image"),
            Select("format", "Pixel format", "--format", "32-bit|16-bit|monochrome|dxt5|dxt3|dxt1|auto"),
            Select("type", "Bitmap type", "--type", "2d_textures|3d_textures|cube_maps|interface_bitmaps|sprites"),
            Select("usage", "Usage", "--usage", "alpha_blend|default|height_map|detail_map|light_map|vector_map"),
            Number("alpha-bias", "Alpha bias", "--alpha-bias", -1, 1), Number("detail-fade", "Detail fade", "--detail-fade", 0, 1),
            Number("bump-height", "Bump height", "--bump-height", 0, 1), Number("mipmap-count", "Maximum mipmaps", "--mipmap-count", 0, 65535, true),
            Select("mipmap-scale", "Mipmap scaling", "--mipmap-scale", "linear|nearest_alpha|nearest"),
            Select("budget", "Sprite sheet size", "--budget", "32|64|128|256|512|1024"), Number("budget-count", "Sprite sheet budget count", "--budget-count", 0, uint.MaxValue, true),
            Select("dithering", "Dithering", "--dithering", "off|on"), Select("bump-palettize", "Bump palettizing", "--bump-palettize", "off|on"),
            Select("reg-point-hack", "Registration point fix", "--reg-point-hack", "off|on"),
            Flag("ignore-tag", "Ignore existing tag settings", "--ignore-tag", "Start with new-tag settings instead of existing values.", s => s.Get("source", "image") == "image"),
            Flag("allow-non-power-of-two", "Allow non-power-of-two images", "--allow-non-power-of-two", "Permit dimensions that are not powers of two."),
            Flag("square-sheets", "Force square sprite sheets", "--square-sheets", "Make generated sprite sheets square.")],
        ["import-fonts"] = [Number("font-size", "Font size in pixels", "--font-size", 1, int.MaxValue, true), Flag("8-bit", "Restrict to first 256 characters", "--8-bit", "Emit the supplied tool's 8-bit option.")],
        ["import-sounds"] = [
            Select("class", "Sound class", "--class", string.Join('|', SoundClasses)),
            Select("format", "Codec", "--format", "16-bit_pcm|ogg_vorbis|xbox_adpcm"),
            Select("channel-count", "Channels", "--channel-count", "mono|stereo"), Select("sample-rate", "Sample rate (Hz)", "--sample-rate", "22050|44100"),
            Number("threads", "Worker threads", "--threads", 1, int.MaxValue, true),
            Select("compression", "Vorbis compression setting", null, "default|quality|bitrate", "default", Vorbis),
            Number("quality", "Vorbis quality", "--compress-level", 0, 1, when: s => Vorbis(s) && s.Get("compression", "default") == "quality"),
            Number("bitrate", "Vorbis bitrate (kbps)", "--bitrate", 1, 65535, true, s => Vorbis(s) && s.Get("compression", "default") == "bitrate"),
            Select("split", "Splitting", null, "default|split|no-split", "default")],
        ["import-strings"] = [Select("type", "Output type", "--type", "hud_message_text|string_list|unicode_string_list")],
        ["tag-collection"] = [],
        ["compile-scripts"] = [
            Select("mode", "Operation", null, "disk|embedded|clear", "disk"), Select("engine", "Engine", "--game-engine", Engines),
            Flag("exclude-global-scripts", "Exclude global scripts", "--exclude-global-scripts", "Disk compilation normally includes data/global_scripts.hsc.", Disk),
            Flag("reload-scripts", "Reload referenced disk sources", "--reload-scripts", "Use existing source names, together with any explicit names below.", Disk)],
        ["compare"] = [
            Select("by-path", "Path matching", "--by-path", "same|any|different"), Select("show", "Results", "--show", "all|matched|mismatched"),
            Flag("all", "Require presence in every input", "--all", "Only match tags present in every input."),
            Flag("functional", "Functional comparison", "--functional", "Precompile tags; detailed field differences are unavailable in this mode."),
            Flag("precision", "Tolerate float precision differences", "--precision", "Allow small floating-point differences."),
            Flag("verbose", "Detailed field differences", "--verbose", "Report differing fields.", s => !s.On("functional")),
            Number("threads", "Comparison threads", "--threads", 1, int.MaxValue, true)],
        ["map-information"] = [Select("query", "Information query", "--type", string.Join('|', InfoQueries), "overview", tip: "Select a metadata query. Type to search the list; results are retained in Activity.")],
        ["map-index"] = [],
        ["map-crc"] = [Select("mode", "Operation", null, "inspect|fix-header|forge", "inspect"), new("crc", "CRC32 (hexadecimal)", "--forge", "hex", [], "", null, null, "One to eight hexadecimal digits, optionally prefixed with 0x. Forge modifies the input map and fixes its header.", s => s.Get("mode", "inspect") == "forge")],
        ["extract-tags"] = [
            Flag("ignore-resources", "Ignore resource maps", "--ignore-resources", "Extract without resource maps; external data may be unavailable."),
            Flag("non-mp-globals", "Allow non-multiplayer globals", "--non-mp-globals", "Allow extraction of non-multiplayer globals."),
            Flag("overwrite", "Overwrite existing tags", "--overwrite", "Replace matching destination tags."),
            Flag("recursive", "Include dependencies", "--recursive", "Also extract dependencies of matching tags.")],
        ["recover-source"] = [Select("scope", "Scope", null, "single|batch", "single"), Flag("overwrite", "Overwrite recovered files", "--overwrite", "Replace recovered source files in the data folder.")],
        ["import-models"] = [Select("type", "Output model type", "--type", "model|gbxmodel")],
        ["lightmap-meshes"] = [Select("mode", "Operation", null, "export|import", "export", tip: "Export mesh or import externally baked mesh. Invader does not bake lighting.")]
    };
    public static bool HasTags(string operation) => operation is not ("compare" or "map-information" or "map-index" or "map-crc");
    public static bool MultipleRoots(string operation) => operation is "archive-tags" or "resource-maps" or "compile-scripts" or "import-models" or "lightmap-meshes";
    public static bool HasData(string operation) => operation is "import-bitmaps" or "import-fonts" or "import-sounds" or "import-strings" or "tag-collection" or "compile-scripts" or "recover-source" or "import-models" or "lightmap-meshes";
    public static bool IsMap(string operation) => operation is "map-information" or "map-index" or "map-crc" or "extract-tags";
    public static bool SupportsFilesystem(string operation) => operation is "archive-tags" or "import-bitmaps" or "import-fonts" or "import-sounds" or "import-strings" or "tag-collection" or "compile-scripts" or "import-models" or "lightmap-meshes";
    public static ToolFormState New(string operation)
    {
        var state = new ToolFormState();
        foreach (var field in Fields[operation].Where(f => f.Default.Length > 0)) state.Options[field.Key] = field.Default;
        if (operation == "compare") state.ComparisonInputs = [new(), new()];
        return state;
    }
    public static Dictionary<string, JsonElement> PresetSettings(string operation, ToolFormState state) =>
        state.Options.Where(p => Fields[operation].Any(f => f.Key == p.Key)).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    public static ToolFormState ApplyPreset(string operation, ToolFormState state, OperationPreset preset)
    {
        if (preset.OperationId != operation || preset.Settings.Any(p => !Fields[operation].Any(f => f.Key == p.Key) || p.Value.ValueKind != JsonValueKind.String))
            throw new JsonException("Preset contains unsupported settings or input/output paths.");
        var copy = ToolFormState.Read(state.Write()); copy.Options = New(operation).Options;
        foreach (var pair in preset.Settings) copy.Options[pair.Key] = pair.Value.GetString()!;
        var errors = OptionErrors(operation, copy); if (errors.Count > 0) throw new JsonException(string.Join("\n", errors));
        return copy;
    }
    public static IReadOnlyList<string> OptionErrors(string operation, ToolFormState state)
    {
        var errors = new List<string>();
        foreach (var field in Fields[operation].Where(f => f.Active(state)))
        {
            if (!state.Options.TryGetValue(field.Key, out var value)) continue;
            if (value.Contains('\0') || field.Kind == "flag" && value is not ("true" or "false") || field.Kind == "choice" && !field.Choices.Contains(value))
                errors.Add($"{field.Label}: select a supported value.");
            if (field.Kind is "integer" or "number")
            {
                bool valid = field.Kind == "integer" ? ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                if (!valid || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < field.Minimum || n > field.Maximum)
                    errors.Add($"{field.Label}: enter a complete number between {field.Minimum} and {field.Maximum}.");
            }
            if (field.Kind == "hex" && !TryHex(value, out _)) errors.Add($"{field.Label}: enter one to eight hexadecimal digits.");
        }
        return errors;
    }
    private static bool TryHex(string value, out uint crc)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        crc = 0; return value.Length is >= 1 and <= 8 && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out crc);
    }

    public static TagHandlingValidation Build(string operation, ToolFormState s, ProjectProfile? profile)
    {
        if (!Fields.ContainsKey(operation)) throw new ArgumentException("Unknown tool form.");
        var errors = OptionErrors(operation, s).ToList();
        if (profile is null) { errors.Add("Select an editing kit profile."); return new(null, errors); }
        errors.AddRange(profile.Validate()); if (errors.Count > 0) return new(null, errors);
        try
        {
            var p = profile.Copy();
            string Resolve(string path) { if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A required file or folder is empty."); return Path.GetFullPath(path, p.WorkingDirectory); }
            string FileInput(string path) { var full = Resolve(path); if (!File.Exists(full)) errors.Add("File does not exist: " + full); return full; }
            string Folder(string path) { var full = Resolve(path); if (!Directory.Exists(full)) errors.Add("Folder does not exist: " + full); return full; }
            string Output(string path) { var full = Resolve(path); if (Directory.Exists(full)) errors.Add("Choose an output file, not a directory."); if (!Directory.Exists(Path.GetDirectoryName(full))) errors.Add("Output parent folder must exist: " + full); return full; }
            p.Paths.DataRoot = Resolve(s.DataOverride ?? p.Paths.DataRoot);
            p.Paths.MapsRoot = Resolve(s.MapsOverride ?? p.Paths.MapsRoot);
            if (HasTags(operation))
            {
                var ids = s.TagRootIds ?? (MultipleRoots(operation) ? p.Paths.TagRoots.Select(r => r.Id).ToList() : [p.Paths.TagRoots[0].Id]);
                if (ids.Count == 0 || ids.Distinct().Count() != ids.Count || !MultipleRoots(operation) && ids.Count != 1) throw new ArgumentException("Select the required number of tag folders without duplicates.");
                p.Paths.TagRoots = ids.Select(id => p.Paths.TagRoots.SingleOrDefault(r => r.Id == id) ?? throw new ArgumentException("A saved tag folder is missing. Select its replacement explicitly.")).ToList();
                foreach (var root in p.Paths.TagRoots) root.Path = Folder(root.Path);
            }
            var args = new List<string>();
            void Value(string flag, string value) { args.Add(flag); args.Add(value); }
            void Flag(string flag, bool on) { if (on) args.Add(flag); }
            void Required(string key, string label) { if (s.Get(key).Length == 0) errors.Add("Select " + label + "."); }
            void Patterns(IEnumerable<string> patterns, string flag)
            {
                foreach (var pattern in patterns) { if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains('\0')) errors.Add("Active pattern rows cannot be empty."); else Value(flag, pattern); }
            }
            foreach (var field in Fields[operation].Where(f => f.Argument is not null && f.Active(s)))
            {
                if (!s.Options.TryGetValue(field.Key, out var value)) continue;
                if (field.Kind == "flag") Flag(field.Argument!, value == "true");
                else
                {
                    if (field.Kind == "number") value = double.Parse(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                    if (field.Kind == "integer") value = ulong.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    if (field.Kind == "hex") { TryHex(value, out var crc); value = "0x" + crc.ToString("X8", CultureInfo.InvariantCulture); }
                    Value(field.Argument!, value);
                }
            }
            var roots = p.Paths.TagRoots.Select(r => Path.GetFullPath(r.Path, p.WorkingDirectory)).ToArray();
            bool NeedsData() => HasData(operation) && !(operation == "compile-scripts" && !Disk(s)) && !(operation == "import-bitmaps" && s.Get("source", "image") != "image");
            if (NeedsData()) Folder(p.Paths.DataRoot);
            if (HasData(operation)) Value("--data", p.Paths.DataRoot);
            if (HasTags(operation)) foreach (var root in roots) Value("--tags", root);
            var batch = operation == "recover-source" && s.Get("scope", "single") == "batch";
            var physical = SupportsFilesystem(operation) && s.FilesystemPath;
            string Logical(IEnumerable<string> searchRoots)
            {
                if (!physical) return TagHandlingBuilder.NormalizeLogical(s.Input);
                var full = FileInput(s.Input); var selectedRoots = searchRoots.ToArray();
                var logical = TagHandlingBuilder.LogicalFromFile(full, selectedRoots);
                var owner = selectedRoots.Select(r => Path.Combine(r, logical)).FirstOrDefault(File.Exists);
                if (!string.Equals(owner, full, StringComparison.OrdinalIgnoreCase)) errors.Add("An earlier folder owns this input. Reorder folders to target the selected file.");
                return logical;
            }
            string Strip(string logical, string extension) => logical.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? logical[..^extension.Length] : logical;
            void ExistingTag(string logical) { if (!roots.Any(r => File.Exists(Path.Combine(r, logical)))) errors.Add("Tag does not exist in the selected folder(s): " + logical); }
            string Scenario() { var logical = Strip(Logical(roots), ".scenario"); ExistingTag(logical + ".scenario"); return logical; }
            string Positional(string logical) => physical ? Resolve(s.Input) : logical;
            string target = s.Input;
            string? derived = null;
            switch (operation)
            {
                case "archive-tags":
                    var single = s.Get("target", "scenario") == "single-tag";
                    var archiveTarget = single ? Logical(roots) : Scenario();
                    if (single) { if (!Path.HasExtension(archiveTarget)) errors.Add("Single tag input needs its group extension."); ExistingTag(archiveTarget); } else Required("engine", "an engine");
                    Flag("--single-tag", single); Flag("--copy", s.Get("mode", "archive") == "copy");
                    foreach (var root in s.ExclusionRoots) Value("--exclude", Folder(root));
                    foreach (var root in s.MatchedExclusionRoots) Value("--exclude-matched", Folder(root));
                    if (s.Output is not null)
                    {
                        var output = s.Get("mode", "archive") == "copy" ? Resolve(s.Output) : Output(s.Output);
                        if (s.Get("mode", "archive") == "copy" && (!Directory.Exists(output) && !Directory.Exists(Path.GetDirectoryName(output)) || File.Exists(output))) errors.Add("Copy destination must be a folder or have an existing parent.");
                        Value("--output", output);
                    }
                    Flag("--fs-path", physical); args.Add(Positional(archiveTarget)); break;
                case "resource-maps":
                    Required("engine", "an engine"); Required("type", "a resource type");
                    if (s.Get("type") == "loc" && s.Get("engine") != "gbx-custom") errors.Add("Localization resources require Custom Edition.");
                    if (s.Get("engine") != "gbx-custom" && s.ResourceSources.Count == 0) errors.Add("This engine requires at least one map or index input.");
                    Value("--maps", Folder(p.Paths.MapsRoot));
                    if (s.Concatenate is not null && s.Get("type") != "loc") Value("--concatenate", FileInput(s.Concatenate));
                    foreach (var source in s.ResourceSources)
                    {
                        if (source.Kind is not ("map" or "index")) errors.Add("Select Map or Index for each resource source.");
                        else Value(source.Kind == "map" ? "--with-map" : "--with-index", FileInput(source.Path));
                    }
                    derived = Path.Combine(p.Paths.MapsRoot, s.Get("type") + ".map"); break;
                case "compare":
                    Patterns(s.Includes, "--search"); Patterns(s.Excludes, "--search-exclude");
                    if (s.ComparisonInputs.Count < 2) errors.Add("Add at least two comparison inputs.");
                    foreach (var input in s.ComparisonInputs)
                    {
                        args.Add("--input");
                        if (input.Kind == "map")
                        {
                            // This release accepts --maps before --map; after it the handler rejects the directory.
                            if (!input.IgnoreResources && input.ResourceDirectory is not null) Value("--maps", Folder(input.ResourceDirectory));
                            Value("--map", FileInput(input.Map)); Flag("--ignore-resources", input.IgnoreResources);
                        }
                        else if (input.Kind == "tags")
                        {
                            if (input.TagDirectories.Count == 0) errors.Add("Each tag input needs at least one folder.");
                            foreach (var root in input.TagDirectories) Value("--tags", Folder(root));
                        }
                        else errors.Add("Select Map or Tags for each comparison input.");
                    }
                    target = $"{s.ComparisonInputs.Count} inputs"; break;
                case "map-information": case "map-index": case "map-crc": case "extract-tags":
                    var map = FileInput(s.Input);
                    if (operation == "map-index")
                    {
                        var output = Output(s.Output ?? "");
                        if (string.Equals(map, output, StringComparison.OrdinalIgnoreCase)) errors.Add("Input map and output index must be different files.");
                        args.Add(map); args.Add(output);
                    }
                    else
                    {
                        if (operation == "map-crc")
                        {
                            Flag("--fix-header", s.Get("mode", "inspect") == "fix-header");
                            if (s.Get("mode", "inspect") == "forge" && s.Get("crc").Length == 0) errors.Add("Enter the CRC32 to forge.");
                        }
                        if (operation == "extract-tags")
                        {
                            if (s.ResourceDirectory is not null && !s.On("ignore-resources")) Value("--maps", Folder(s.ResourceDirectory));
                            Patterns(s.Includes, "--search"); Patterns(s.Excludes, "--search-exclude");
                        }
                        args.Add(map);
                    }
                    break;
                case "recover-source":
                    if (batch)
                    {
                        if (s.Includes.Count + s.Excludes.Count == 0) errors.Add("Batch needs include/exclude patterns or All tags.");
                        Patterns(s.Includes, "--batch"); Patterns(s.Excludes, "--batch-exclude"); target = "Batch";
                    }
                    else { var logical = TagHandlingBuilder.NormalizeLogical(s.Input); if (!Path.HasExtension(logical)) errors.Add("Include the tag's group extension."); ExistingTag(logical); args.Add(logical); }
                    break;
                case "compile-scripts": case "lightmap-meshes":
                    var scenario = Scenario(); Flag("--fs-path", physical);
                    if (operation == "compile-scripts")
                    {
                        Flag("--clear", s.Get("mode", "disk") == "clear"); Flag("--regenerate", s.Get("mode", "disk") == "embedded");
                        if (Disk(s)) foreach (var name in s.ScriptSources)
                        {
                            var source = Strip(TagHandlingBuilder.NormalizeLogical(name), ".hsc");
                            if (source.Contains('\\')) errors.Add("Explicit script names must be filenames in the scenario's scripts folder.");
                            FileInput(Path.Combine(p.Paths.DataRoot, Path.GetDirectoryName(scenario) ?? "", "scripts", source + ".hsc")); Value("--explicit", source);
                        }
                    }
                    else
                    {
                        var bsp = Strip(TagHandlingBuilder.NormalizeLogical(s.SecondaryInput), ".scenario_structure_bsp");
                        if (bsp.Contains('\\')) errors.Add("BSP name must be a basename without a directory.");
                        derived = Path.Combine(p.Paths.DataRoot, Path.GetDirectoryName(scenario) ?? "", "lightmaps", bsp + ".lightmap_mesh");
                        Flag("--export-mesh", s.Get("mode", "export") == "export"); Flag("--import-mesh", s.Get("mode", "export") == "import");
                        if (s.Get("mode", "export") == "import") FileInput(derived);
                        args.Add(Positional(scenario)); args.Add(bsp); break;
                    }
                    args.Add(Positional(scenario)); break;
                case "import-models":
                    Required("type", "a model type");
                    string asset;
                    if (physical)
                    {
                        var input = Resolve(s.Input);
                        if (Directory.Exists(input)) asset = TagHandlingBuilder.LogicalFromFile(input, [p.Paths.DataRoot]);
                        else { var logical = Logical(roots); if (Path.GetExtension(logical) is not (".model" or ".gbxmodel")) errors.Add("Select a model tag or data asset directory."); asset = Path.GetDirectoryName(logical) ?? ""; }
                    }
                    else asset = TagHandlingBuilder.NormalizeLogical(s.Input);
                    var modelDirectory = Folder(Path.Combine(p.Paths.DataRoot, asset, "models"));
                    if (Directory.Exists(modelDirectory) && !Directory.EnumerateFiles(modelDirectory).Any(f => Path.GetExtension(f).Equals(".jms", StringComparison.OrdinalIgnoreCase))) errors.Add("The asset's models folder must contain JMS source files.");
                    derived = Path.Combine(roots[0], asset, Path.GetFileName(asset) + "." + s.Get("type"));
                    Flag("--fs-path", physical); args.Add(Positional(asset)); break;
                default:
                    var underData = operation is "tag-collection" or "import-strings";
                    var extension = operation switch { "tag-collection" => ".txt", "import-strings" => s.Get("type") == "hud_message_text" ? ".hmt" : ".txt", "import-fonts" => ".font", "import-sounds" => ".sound", _ => ".bitmap" };
                    var logicalInput = Strip(Logical(underData ? [p.Paths.DataRoot] : roots), extension);
                    if (operation == "import-strings") Required("type", "a string output type");
                    if (operation == "import-bitmaps")
                    {
                        var regenerate = s.Get("source", "image") == "embedded-color-plate";
                        Flag("--regenerate", regenerate);
                        if (regenerate) ExistingTag(logicalInput + ".bitmap");
                        else if (!new[] { ".tif", ".tiff", ".png", ".tga", ".bmp" }.Any(ext => File.Exists(Path.Combine(p.Paths.DataRoot, logicalInput + ext)))) errors.Add("No source image exists at the logical path in the data folder.");
                    }
                    if (operation == "import-fonts" && !new[] { ".ttf", ".otf" }.Any(ext => File.Exists(Path.Combine(p.Paths.DataRoot, logicalInput + ext)))) errors.Add("No TTF/OTF source exists at the logical path in the data folder.");
                    if (underData) FileInput(Path.Combine(p.Paths.DataRoot, logicalInput + extension));
                    if (operation == "import-sounds")
                    {
                        Folder(Path.Combine(p.Paths.DataRoot, logicalInput));
                        if (s.Get("class").Length == 0 && !File.Exists(Path.Combine(roots[0], logicalInput + ".sound"))) errors.Add("Select a sound class when creating a new tag.");
                        Flag("--split", s.Get("split", "default") == "split"); Flag("--no-split", s.Get("split", "default") == "no-split");
                        if (Vorbis(s) && s.Get("compression") is "quality" or "bitrate" && s.Get(s.Get("compression")).Length == 0) errors.Add("Enter the selected quality or bitrate override.");
                    }
                    Flag("--fs-path", physical); args.Add(Positional(logicalInput));
                    derived = Path.Combine(roots[0], logicalInput + (operation == "tag-collection" ? ".tag_collection" : operation == "import-strings" ? "." + s.Get("type") : extension));
                    break;
            }
            if (errors.Count > 0) return new(null, errors);
            var options = new Dictionary<string, string> { ["formState"] = s.Write().GetRawText() };
            if (derived is not null) options["derivedOutput"] = derived;
            var summary = Operations.All.Single(o => o.Id == operation).Name + ": " + target + (derived is null ? "" : "; file: " + derived);
            return new(new(p, args.AsReadOnly(), options, summary), errors);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { errors.Add(ex.Message); return new(null, errors); }
    }
}
