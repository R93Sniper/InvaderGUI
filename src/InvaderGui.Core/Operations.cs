namespace InvaderGui.Core;

public sealed record OperationDefinition(string Id, string SlotId, string Name, string Executable)
{
    public string Description => Id switch
    {
        "build-map" => "Build a cache map from a scenario and its referenced tags.",
        "archive-tags" => "Archive or copy the tags required by a scenario or a single tag.",
        "resource-maps" => "Build bitmap, sound, or localization resource maps from tags and ordered map/index inputs.",
        "import-bitmaps" => "Create or update bitmap tags from source images, or regenerate from an embedded color plate.",
        "import-fonts" => "Generate a font tag from a selected font and character settings.",
        "import-sounds" => "Create or update sound tags from audio permutations under the editing kit's data directory.",
        "import-strings" => "Import text sources into string-list, Unicode string-list, or HUD message tags.",
        "tag-collection" => "Create a tag collection from a text source listing tag references.",
        "compile-scripts" => "Compile disk script sources, recompile embedded sources, or clear script data in a scenario tag.",
        "compare" => "Compare tags or maps using ordered inputs with independently configured tag roots and resource settings.",
        "map-information" => "Inspect a cache map and display the selected information query.",
        "map-index" => "Export a cache map's tag index to a separate file.",
        "map-crc" => "Inspect a cache map's CRC or apply the tool's supported CRC operation.",
        "dependencies" => "Find tag dependencies or references using the selected tag roots and scope.",
        "repair-tags" => "Detect or repair tags, normalize model references, and optionally strip hidden data.",
        "convert-tags" => "Convert supported tag types individually or in a batch.",
        "strip-tags" => "Remove hidden data from tags. Changed tags are rewritten in place.",
        "extract-tags" => "Extract tags from a cache map into a selected destination tag root.",
        "recover-source" => "Recover supported source data from existing tags into the data directory.",
        "import-models" => "Import model source files from the asset's models directory into a model tag.",
        "lightmap-meshes" => "Export meshes for external lightmap baking or import baked meshes. The GUI does not bake lighting.",
        "refactor" => "Plan tag moves, copies, and reference edits in an ordered queue with current and planned views.",
        _ => Name
    };
}

public static class Operations
{
    public static IEnumerable<OperationDefinition> Visible => All.Where(o => o.Id != "strip-tags");
    public static string ResolvePageId(string id) => id == "strip-tags" ? "repair-tags" : id;
    public static bool NormalizeSelections(IDictionary<string, string> selections)
    {
        var changed = false;
        var rememberedPages = selections.Values.Select(ResolvePageId).ToArray();
        foreach (var group in Visible.GroupBy(o => o.SlotId))
        {
            var ids = group.Select(o => o.Id).ToHashSet();
            if (selections.TryGetValue(group.Key, out var current) && ids.Contains(ResolvePageId(current))) continue;
            var remembered = rememberedPages.LastOrDefault(ids.Contains);
            selections[group.Key] = remembered ?? group.First().Id; changed = true;
        }
        return changed;
    }
    public static IReadOnlyList<OperationDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new OperationDefinition("build-map", "build-map", "Build Map", "invader-build.exe"),
        new OperationDefinition("resource-maps", "build-map", "Build Resource Maps", "invader-resource.exe"),
        new OperationDefinition("archive-tags", "archive-tags", "Archive Tags", "invader-archive.exe"),
        new OperationDefinition("import-bitmaps", "asset-interchange", "Import Bitmaps", "invader-bitmap.exe"),
        new OperationDefinition("import-fonts", "asset-interchange", "Import Fonts", "invader-font.exe"),
        new OperationDefinition("import-sounds", "asset-interchange", "Import Sounds", "invader-sound.exe"),
        new OperationDefinition("import-strings", "asset-interchange", "Import Strings", "invader-string.exe"),
        new OperationDefinition("import-models", "asset-interchange", "Import Models", "invader-model.exe"),
        new OperationDefinition("lightmap-meshes", "asset-interchange", "Lightmap Meshes", "invader-lightmap.exe"),
        new OperationDefinition("repair-tags", "tag-handling", "Repair Tags", "invader-bludgeon.exe"),
        new OperationDefinition("convert-tags", "tag-handling", "Convert Tags", "invader-convert.exe"),
        new OperationDefinition("dependencies", "tag-handling", "Find Dependencies", "invader-dependency.exe"),
        new OperationDefinition("tag-collection", "tag-handling", "Create Tag Collection", "invader-collection.exe"),
        new OperationDefinition("extract-tags", "tag-handling", "Extract Tags", "invader-extract.exe"),
        new OperationDefinition("recover-source", "tag-handling", "Recover Source Data", "invader-recover.exe"),
        new OperationDefinition("strip-tags", "tag-handling", "Strip Hidden Data", "invader-strip.exe"),
        new OperationDefinition("compile-scripts", "compile-scripts", "Compile Scripts", "invader-script.exe"),
        new OperationDefinition("compare", "compare", "Compare Tags and Maps", "invader-compare.exe"),
        new OperationDefinition("map-information", "map-tools", "Map Information", "invader-info.exe"),
        new OperationDefinition("map-index", "map-tools", "Export Map Index", "invader-index.exe"),
        new OperationDefinition("map-crc", "map-tools", "Map CRC", "invader-crc.exe"),
        new OperationDefinition("refactor", "refactor", "Refactor Tags", "invader-refactor.exe")
    });
}

public sealed record ToolObservation(string Executable, string FullPath, bool Available, string? Reason);
public sealed record InstallationScan(Guid Id, DateTimeOffset ScannedUtc, string Directory,
    IReadOnlyList<ToolObservation> Tools)
{
    public ToolObservation For(OperationDefinition operation) => Tools.Single(t => t.Executable == operation.Executable);
}

public static class InstallationDiscovery
{
    public static InstallationScan Scan(string directory)
    {
        var tools = Operations.All.Select(operation =>
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(directory, operation.Executable));
                var available = System.IO.Directory.Exists(directory) && File.Exists(fullPath);
                return new ToolObservation(operation.Executable, fullPath, available,
                    available ? null : "Executable was not found in the selected directory.");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new ToolObservation(operation.Executable, "", false, ex.Message);
            }
        }).ToArray();
        return new(Guid.NewGuid(), DateTimeOffset.UtcNow, directory, Array.AsReadOnly(tools));
    }
}

