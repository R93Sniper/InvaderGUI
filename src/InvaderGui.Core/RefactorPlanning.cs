using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace InvaderGui.Core;

public sealed record RefactorTagMapping(string Source, string Destination);
public sealed record RefactorFolderMapping(string SourceDirectory, string DestinationDirectory);
public sealed record RefactorGroupMapping(string SourceGroup, string DestinationGroup);
public sealed record RefactorStringReplacement(string Find, string Replace);

/// <summary>One immutable Invader invocation, never a collection of separate processes.</summary>
public sealed record RefactorCommand
{
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired] public string Mode { get; init; } = "move";
    [JsonRequired] public ImmutableArray<RefactorTagMapping> TagMappings { get; init; } = [];
    [JsonRequired] public RefactorFolderMapping? RecursiveMapping { get; init; }
    [JsonRequired] public ImmutableArray<RefactorGroupMapping> GroupMappings { get; init; } = [];
    [JsonRequired] public ImmutableArray<RefactorStringReplacement> ReplaceStrings { get; init; } = [];
    [JsonRequired] public string? SingleTagScope { get; init; }
    [JsonRequired] public bool Unsafe { get; init; }
    [JsonIgnore] public string Summary => (Mode switch { "copy" => "Copy", "no-move" => "References only", _ => "Move" }) + " · " +
        (RecursiveMapping is not null ? $"{RecursiveMapping.SourceDirectory} → {RecursiveMapping.DestinationDirectory}" :
        TagMappings.Length > 0 ? string.Join("; ", TagMappings.Select(pair => $"{pair.Source} → {pair.Destination}")) :
        GroupMappings.Length > 0 ? string.Join("; ", GroupMappings.Select(pair => $"{pair.SourceGroup} → {pair.DestinationGroup}")) : "Path string replacements") +
        (ReplaceStrings.Length == 0 ? "" : $" · {ReplaceStrings.Length} replacement(s)");
}

public sealed record RefactorCommandResult(RefactorCommand Command, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, int AffectedTags);
public sealed record RefactorReplay(TagHierarchySnapshot Planned, IReadOnlyList<RefactorCommandResult> Entries)
{
    public bool IsValid => Entries.All(entry => entry.Errors.Count == 0);
}

public static class RefactorPlanner
{
    public static IEnumerable<TagHierarchyNode> Flatten(IEnumerable<TagHierarchyNode> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    public static string LogicalPath(string value, bool tag)
    {
        var path = value.Replace('/', '\\');
        if (!tag) path = path.TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('\\') || path.Split('\\').Any(part =>
            part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) ||
            IsDeviceName(part)))
            throw new ArgumentException($"Enter a valid tag-folder-relative {(tag ? "tag path" : "folder path")}: {value}");
        if (path.Split('\\').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Repository metadata is not a tag path.");
        if (tag)
        {
            if (!TagHierarchy.IsTagFile(path)) throw new ArgumentException($"Include a supported tag extension: {value}");
            path = Stem(path) + Path.GetExtension(path).ToLowerInvariant();
        }
        return path;
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
    }
    private static string Stem(string tag) => tag[..tag.LastIndexOf('.')];
    private static string Group(string value)
    {
        var group = value.Trim().ToLowerInvariant();
        if (!TagHierarchy.IsTagFile("tag." + group) || group.Contains('.') || group.Contains('\\') || group.Contains('/'))
            throw new ArgumentException($"Choose a supported tag group: {value}");
        return group;
    }

    public static RefactorCommand Normalize(RefactorCommand command)
    {
        if (command.Mode is not ("move" or "copy" or "no-move")) throw new ArgumentException("Select Move, Copy, or References only.");
        if (command.RecursiveMapping is not null && (command.TagMappings.Length > 0 || command.GroupMappings.Length > 0 || command.Mode == "no-move"))
            throw new ArgumentException("Recursive folders require Move or Copy and cannot be combined with individual tags or groups.");
        if (command.GroupMappings.Length > 0 && command.Mode != "no-move") throw new ArgumentException("Group mappings require References only.");
        if (command.Unsafe && command.Mode != "no-move") throw new ArgumentException("Allow missing destinations applies only to References only.");
        if (command.TagMappings.Length == 0 && command.GroupMappings.Length == 0 && command.RecursiveMapping is null && command.ReplaceStrings.Length == 0)
            throw new ArgumentException("Add a mapping or a string replacement.");
        foreach (var replacement in command.ReplaceStrings)
            if (string.IsNullOrEmpty(replacement.Find)) throw new ArgumentException("Find text cannot be empty.");
        return command with
        {
            TagMappings = command.TagMappings.Select(pair => new RefactorTagMapping(LogicalPath(pair.Source, true), LogicalPath(pair.Destination, true))).ToImmutableArray(),
            RecursiveMapping = command.RecursiveMapping is not { } recursive ? null : new(LogicalPath(recursive.SourceDirectory, false), LogicalPath(recursive.DestinationDirectory, false)),
            GroupMappings = command.GroupMappings.Select(pair => new RefactorGroupMapping(Group(pair.SourceGroup), Group(pair.DestinationGroup))).ToImmutableArray(),
            ReplaceStrings = command.ReplaceStrings.Select(pair => new RefactorStringReplacement(pair.Find.Replace('/', '\\'), pair.Replace.Replace('/', '\\'))).ToImmutableArray(),
            SingleTagScope = command.SingleTagScope is null ? null : LogicalPath(command.SingleTagScope, true)
        };
    }

    public static IReadOnlyList<string> Arguments(RefactorCommand entry, IReadOnlyList<TagHierarchyRoot> roots)
    {
        var command = Normalize(entry);
        var args = new List<string> { "--mode", command.Mode };
        foreach (var root in roots) args.AddRange(["--tags", root.Path]);
        foreach (var pair in command.TagMappings) args.AddRange(["--tag", pair.Source, pair.Destination]);
        if (command.RecursiveMapping is { } recursive) args.AddRange(["--recursive", recursive.SourceDirectory, recursive.DestinationDirectory]);
        foreach (var pair in command.GroupMappings) args.AddRange(["--groups", pair.SourceGroup, pair.DestinationGroup]);
        foreach (var pair in command.ReplaceStrings) args.AddRange(["--replace-string", pair.Find, pair.Replace]);
        if (command.SingleTagScope is not null) args.AddRange(["--single-tag", command.SingleTagScope]);
        if (command.Unsafe) args.Add("--unsafe");
        return args;
    }

    public static RefactorReplay Replay(TagHierarchySnapshot current, IEnumerable<RefactorCommand> commands)
    {
        var original = Flatten(current.Nodes).ToArray();
        var files = original.Where(node => !node.IsFolder).ToDictionary(node => node.LogicalPath, node => node.Definitions.ToList(), StringComparer.OrdinalIgnoreCase);
        var folders = original.Where(node => node.IsFolder).Select(node => node.LogicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RefactorCommandResult>();
        foreach (var input in commands)
        {
            var errors = new List<string>(); var warnings = new List<string>();
            var next = files.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            var nextFolders = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int affected = 0;
            try
            {
                var command = Normalize(input);
                if (command.SingleTagScope is not null && !files.ContainsKey(command.SingleTagScope))
                    throw new ArgumentException($"Reference scope tag does not exist: {command.SingleTagScope}");
                var mappings = command.TagMappings.Where(pair => !pair.Source.Equals(pair.Destination, StringComparison.OrdinalIgnoreCase)).ToList();
                if (command.RecursiveMapping is { } recursive)
                {
                    if (recursive.SourceDirectory.Equals(recursive.DestinationDirectory, StringComparison.OrdinalIgnoreCase) ||
                        recursive.DestinationDirectory.StartsWith(recursive.SourceDirectory + "\\", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("A folder cannot be moved or copied into itself or one of its descendants.");
                    mappings.AddRange(files.Keys.Where(path => path.StartsWith(recursive.SourceDirectory + "\\", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => new RefactorTagMapping(path, recursive.DestinationDirectory + path[recursive.SourceDirectory.Length..])));
                    if (mappings.Count == 0) throw new ArgumentException($"No tags were found in {recursive.SourceDirectory}.");
                }
                foreach (var group in command.GroupMappings)
                    mappings.AddRange(files.Keys.Where(path => Path.GetExtension(path).Equals("." + group.DestinationGroup, StringComparison.OrdinalIgnoreCase))
                        .Select(path => new RefactorTagMapping(Stem(path) + "." + group.SourceGroup, path)));
                if (mappings.Count == 0 && command.ReplaceStrings.Length > 0)
                    mappings.AddRange(files.Keys.Where(path => command.ReplaceStrings.Any(pair => Stem(path).Contains(pair.Find, StringComparison.Ordinal)))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => new RefactorTagMapping(path, path)));
                var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mapping in mappings)
                {
                    var destinationStem = Stem(mapping.Destination);
                    foreach (var replacement in command.ReplaceStrings)
                        destinationStem = destinationStem.Replace(replacement.Find, replacement.Replace, StringComparison.Ordinal);
                    var destination = LogicalPath(destinationStem + Path.GetExtension(mapping.Destination), true);
                    if (mapping.Source.Equals(destination, StringComparison.OrdinalIgnoreCase)) continue;
                    if (command.Mode == "no-move")
                    {
                        if (!command.Unsafe && !files.ContainsKey(destination)) errors.Add($"Reference destination does not exist: {destination}");
                        affected++;
                        continue;
                    }
                    if (!Path.GetExtension(mapping.Source).Equals(Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Moving or copying a tag must preserve its tag group extension.");
                    if (!files.TryGetValue(mapping.Source, out var definitions)) throw new ArgumentException($"Source tag does not exist before this command: {mapping.Source}");
                    if (command.Mode == "move" && !sources.Add(mapping.Source)) throw new ArgumentException($"The same tag cannot be moved twice in one command: {mapping.Source}");
                    if (next.ContainsKey(destination) || nextFolders.Contains(destination)) throw new ArgumentException($"Destination already exists: {destination}");
                    if (current.BlockedPaths.Any(path => path.Equals(destination, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"An existing non-tag file or linked item blocks the destination: {destination}");
                    foreach (var parent in Parents(destination))
                        if (next.ContainsKey(parent)) throw new ArgumentException($"A tag blocks the destination folder: {parent}");
                    var owner = definitions[0];
                    var aliases = definitions.Where(definition => definition.PhysicalPath.Equals(owner.PhysicalPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                    next[destination] = aliases.Select(definition => new TagDefinition(definition.Root,
                        Path.Combine(definition.Root.Path, destination.Replace('\\', Path.DirectorySeparatorChar)))).ToList();
                    if (command.Mode == "move")
                    {
                        next[mapping.Source].RemoveAll(definition => definition.PhysicalPath.Equals(owner.PhysicalPath, StringComparison.OrdinalIgnoreCase));
                        if (next[mapping.Source].Count == 0) next.Remove(mapping.Source);
                        else warnings.Add($"Moving {mapping.Source} exposes a shadowed definition from {next[mapping.Source][0].Root.Name}.");
                        sourceParents.UnionWith(Parents(mapping.Source));
                    }
                    nextFolders.UnionWith(Parents(destination)); modified.Add(destination); affected++;
                }
                if (affected == 0) throw new ArgumentException("This command produces no path or reference changes.");
                if (command.Mode == "copy") warnings.Add("Copy updates references within the copied set; it does not rewrite all original references.");
                if (command.Mode == "no-move") warnings.Add("References only: files stay in place. Reference contents are not simulated by the tree.");
                foreach (var parent in sourceParents.OrderByDescending(path => path.Length))
                    if (!next.Keys.Any(path => path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase)))
                        nextFolders.RemoveWhere(path => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase));
            }
            catch (ArgumentException ex) { errors.Add(ex.Message); }
            if (errors.Count == 0) { files = next; folders = nextFolders; changed.UnionWith(modified); }
            results.Add(new(input, errors, warnings.Distinct().ToArray(), affected));
        }
        var nodes = BuildTree(files, folders, changed);
        return new(new(current.Roots, nodes, files.Count, files.Values.Sum(definitions => Math.Max(0, definitions.Count - 1)), current.SkippedLinks) { BlockedPaths = current.BlockedPaths }, results);
    }

    private static IEnumerable<string> Parents(string path)
    {
        var index = path.LastIndexOf('\\');
        while (index >= 0) { yield return path[..index]; index = path.LastIndexOf('\\', index - 1); }
    }

    private sealed class TreeNode(string path, bool folder)
    {
        public string Path { get; } = path;
        public bool Folder { get; } = folder;
        public List<TagDefinition> Definitions { get; set; } = [];
        public Dictionary<string, TreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TagHierarchyNode Freeze(HashSet<string> changed) => new(Path[(Path.LastIndexOf('\\') + 1)..], Path, Folder,
            Children.Values.OrderByDescending(node => node.Folder).ThenBy(node => node.Path, StringComparer.OrdinalIgnoreCase).Select(node => node.Freeze(changed)).ToArray(), Definitions.ToArray())
            { IsPlannedChange = changed.Contains(Path) };
    }

    private static IReadOnlyList<TagHierarchyNode> BuildTree(Dictionary<string, List<TagDefinition>> files, HashSet<string> folders, HashSet<string> changed)
    {
        var root = new TreeNode("", true);
        var highlights = changed.Concat(changed.SelectMany(Parents)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in folders.Concat(files.Keys).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var node = root; var prefix = "";
            foreach (var part in path.Split('\\'))
            {
                prefix = prefix.Length == 0 ? part : prefix + "\\" + part;
                if (!node.Children.TryGetValue(part, out var child)) node.Children[part] = child = new(prefix, !files.ContainsKey(prefix));
                node = child;
            }
            if (files.TryGetValue(path, out var definitions)) node.Definitions = definitions;
        }
        return root.Children.Values.OrderByDescending(node => node.Folder).ThenBy(node => node.Path, StringComparer.OrdinalIgnoreCase).Select(node => node.Freeze(highlights)).ToArray();
    }
}
