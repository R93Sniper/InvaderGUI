namespace InvaderGui.Core;

public sealed record TagHierarchyRoot(string Id, string Name, string Path, int Precedence);
public sealed record TagDefinition(TagHierarchyRoot Root, string PhysicalPath);

public sealed record TagHierarchyNode(string Name, string LogicalPath, bool IsFolder,
    IReadOnlyList<TagHierarchyNode> Children, IReadOnlyList<TagDefinition> Definitions)
{
    public bool IsPlannedChange { get; init; }
    public string Details => IsFolder ? LogicalPath : string.Join("\n", Definitions.Select((definition, index) =>
        $"{(index == 0 ? "Owner" : "Shadowed")}: {definition.Root.Name} (priority {definition.Root.Precedence + 1})\n{definition.PhysicalPath}"));
}

public sealed record TagHierarchySnapshot(IReadOnlyList<TagHierarchyRoot> Roots,
    IReadOnlyList<TagHierarchyNode> Nodes, int TagCount, int ShadowedCount, int SkippedLinks)
{
    public IReadOnlyList<string> BlockedPaths { get; init; } = [];
}

/// <summary>Read-only virtual tag tree. Earlier roots own duplicate logical tags.</summary>
public static class TagHierarchy
{
    private static readonly HashSet<string> Groups = new((
        "actor actor_variant antenna model_animations biped bitmap spheroid continuous_damage_effect model_collision_geometry color_table contrail device_control decal ui_widget_definition input_device_defaults device detail_object_collection effect equipment flag fog font material_effects garbage glow grenade_hud_interface hud_message_text hud_number hud_globals item item_collection damage_effect lens_flare lightning device_light_fixture light sound_looping device_machine globals meter light_volume gbxmodel model multiplayer_scenario_description preferences_network_game object particle particle_system physics placeholder point_physics projectile weather_particle_system scenario_structure_bsp scenery shader_transparent_chicago_extended shader_transparent_chicago scenario shader_environment shader_transparent_glass shader sky shader_transparent_meter sound sound_environment shader_model shader_transparent_generic ui_widget_collection shader_transparent_plasma sound_scenery string_list shader_transparent_water tag_collection camera_track dialogue unit_hud_interface unit unicode_string_list virtual_keyboard vehicle weapon wind weapon_hud_interface"
    ).Split(' '), StringComparer.OrdinalIgnoreCase);

    public static bool IsTagFile(string path) => Groups.Contains(System.IO.Path.GetExtension(path).TrimStart('.'));
    public static IReadOnlyList<string> TagGroups { get; } = Groups.OrderBy(group => group, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<TagHierarchyRoot> ResolveRoots(ProjectProfile profile, string? selectedRootId)
    {
        var selected = string.IsNullOrEmpty(selectedRootId) ? profile.Paths.TagRoots.ToArray() :
            profile.Paths.TagRoots.Where(root => root.Id == selectedRootId).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Select a configured tag folder.");
        return selected.Select((root, index) => new TagHierarchyRoot(root.Id, root.Name,
            System.IO.Path.GetFullPath(root.Path, profile.WorkingDirectory), index)).ToArray();
    }

    private sealed class Node(string name, string logicalPath, bool isFolder)
    {
        public string Name { get; } = name;
        public string LogicalPath { get; } = logicalPath;
        public bool IsFolder { get; } = isFolder;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TagDefinition> Definitions { get; } = new();
        public TagHierarchyNode? Freeze(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            var children = new List<TagHierarchyNode>();
            foreach (var child in Children.Values.OrderByDescending(child => child.IsFolder).ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
            {
                var frozen = child.Freeze(cancellationToken);
                if (frozen is null) return null;
                children.Add(frozen);
            }
            return new(Name, LogicalPath, IsFolder, children.ToArray(), Definitions.ToArray());
        }
    }

    public static TagHierarchySnapshot Scan(IReadOnlyList<TagHierarchyRoot> roots) => Scan(roots, CancellationToken.None)!;

    /// <summary>Returns null when superseded/canceled; cancellation never publishes partial results or throws.</summary>
    public static TagHierarchySnapshot? Scan(IReadOnlyList<TagHierarchyRoot> roots, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return null;
        if (roots.Count == 0) throw new ArgumentException("Select a configured tag folder.");
        var tree = new Node("", "", true);
        var blockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tags = 0, shadowed = 0, skippedLinks = 0;
        foreach (var root in roots)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            if (System.IO.Path.GetFullPath(root.Path).Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                .Any(part => string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Repository metadata cannot be used as a tag folder.");
            var pending = new Stack<(string Path, Node Folder)>();
            pending.Push((root.Path, tree));
            while (pending.TryPop(out var directory))
            {
                if (cancellationToken.IsCancellationRequested) return null;
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
                    {
                        if (cancellationToken.IsCancellationRequested) return null;
                        var name = System.IO.Path.GetFileName(entry);
                        // Repository metadata is never traversed or inspected.
                        if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skippedLinks++; blockedPaths.Add(Path.GetRelativePath(root.Path, entry).Replace('/', '\\')); continue;
                        }
                        var isFolder = (attributes & FileAttributes.Directory) != 0;
                        if (!isFolder && !IsTagFile(name))
                        { blockedPaths.Add(Path.GetRelativePath(root.Path, entry).Replace('/', '\\')); continue; }
                        if (!directory.Folder.Children.TryGetValue(name, out var node))
                        {
                            var logicalPath = directory.Folder.LogicalPath.Length == 0 ? name : directory.Folder.LogicalPath + "\\" + name;
                            directory.Folder.Children.Add(name, node = new(name, logicalPath, isFolder));
                        }
                        if (node.IsFolder != isFolder) throw new IOException($"A tag and a folder share the logical path {node.LogicalPath} across roots.");
                        if (isFolder) pending.Push((entry, node));
                        else
                        {
                            if (node.Definitions.Count == 0) tags++; else shadowed++;
                            node.Definitions.Add(new(root, entry));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { throw new IOException($"Could not read tag folder '{directory.Path}': {ex.Message}", ex); }
            }
        }
        var frozenTree = tree.Freeze(cancellationToken);
        return frozenTree is null || cancellationToken.IsCancellationRequested ? null :
            new(roots.ToArray(), frozenTree.Children, tags, shadowed, skippedLinks) { BlockedPaths = blockedPaths.ToArray() };
    }
}
