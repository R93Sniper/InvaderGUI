using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace InvaderGui.Core;

public static class RepairWorkflow
{
    public static IReadOnlyList<string> AvailabilityErrors(RepairTagsState state, InstallationScan? scan)
    {
        var required = new List<string>();
        if (state.RepairMode != "skip") required.Add("repair-tags");
        if (state.ModelReferences != "unchanged") required.Add("refactor");
        if (state.StripHiddenData) required.Add("strip-tags");
        return required.Select(id => Operations.All.Single(o => o.Id == id))
            .Where(op => scan?.For(op).Available != true).Select(op => $"Unavailable: {op.Executable}. Use Invader to select its installation.").ToArray();
    }

    public static TaskLaunchSnapshot Create(RepairTagsState state, ProjectProfile profile, InstallationScan scan, string logsDirectory)
    {
        var check = TagHandlingBuilder.BuildRepair(state, profile);
        if (check.Invocation is not { } invocation) throw new ArgumentException(string.Join("\n", check.Errors));
        var unavailable = AvailabilityErrors(state, scan);
        if (unavailable.Count > 0) throw new FileNotFoundException(string.Join("\n", unavailable));
        var steps = new List<TaskLaunchStep>();
        void Add(string id, string name, IEnumerable<string> arguments)
        {
            var observation = scan.For(Operations.All.Single(o => o.Id == id));
            steps.Add(new(id, name, observation.FullPath, arguments.ToImmutableArray()));
        }
        if (state.RepairMode != "skip") Add("repair-tags", state.RepairMode == "detect" ? "Detect tag issues (Bludgeon)" : "Repair tags (Bludgeon)", invocation.Arguments);
        var root = invocation.EffectiveProfile.Paths.TagRoots.Single().Path;
        if (state.ModelReferences != "unchanged")
        {
            var from = state.ModelReferences == "model-to-gbxmodel" ? "model" : "gbxmodel";
            var to = from == "model" ? "gbxmodel" : "model";
            var arguments = new[] { "--groups", from, to, "--mode", "no-move", "--tags", root };
            var name = $"Change {from} references to {to}";
            if (!state.Batch)
            {
                var logical = state.FilesystemPath
                    ? TagHandlingBuilder.LogicalFromFile(Path.GetFullPath(state.Input, profile.WorkingDirectory), new[] { root })
                    : TagHandlingBuilder.NormalizeLogical(state.Input);
                Add("refactor", name, arguments.Concat(new[] { "--single-tag", logical }));
            }
            else if (state.Excludes.Count == 0 && state.Includes.Contains("*")) Add("refactor", name + " (all tags)", arguments);
            else
            {
                // Refactor has no wildcard filters. Freeze matching tag identities at launch and
                // invoke its supported single-tag scope separately, preserving the selected scope.
                var targets = MatchingTags(root, state.Includes, state.Excludes).ToArray();
                if (targets.Length == 0) throw new ArgumentException("No tags match the reference-normalization batch patterns.");
                foreach (var tag in targets) Add("refactor", name + ": " + tag, arguments.Concat(new[] { "--single-tag", tag }));
            }
        }
        if (state.StripHiddenData)
        {
            var strip = new StripTagsState
            {
                SelectedTagRootId = state.SelectedTagRootId, Input = state.Input, FilesystemPath = state.FilesystemPath,
                Batch = state.Batch, Includes = state.Includes.ToList(), Excludes = state.Excludes.ToList()
            };
            var cleanup = TagHandlingBuilder.BuildStrip(strip, profile);
            if (cleanup.Invocation is null) throw new ArgumentException(string.Join("\n", cleanup.Errors));
            Add("strip-tags", "Strip hidden data", cleanup.Invocation.Arguments);
        }
        var first = steps[0];
        var parent = new OperationDefinition("repair-tags", "tag-handling", "Repair Tags", Path.GetFileName(first.ExecutablePath));
        return TaskLaunchSnapshot.Create(parent, scan, invocation.EffectiveProfile, first.Arguments,
            invocation.Summary + "; steps: " + string.Join(" → ", steps.Select(s => s.Name)), logsDirectory, options: invocation.Options)
            with { Steps = steps.ToImmutableArray() };
    }

    public static bool Matches(string tag, string pattern)
    {
        var expression = "\\A" + Regex.Escape(pattern.Replace('/', '\\')).Replace("\\*", ".*").Replace("\\?", ".") + "\\z";
        return Regex.IsMatch(tag.Replace('/', '\\'), expression, RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private static readonly HashSet<string> TagGroups = new((
        "actor actor_variant antenna model_animations biped bitmap spheroid continuous_damage_effect model_collision_geometry color_table contrail device_control decal ui_widget_definition input_device_defaults device detail_object_collection effect equipment flag fog font material_effects garbage glow grenade_hud_interface hud_message_text hud_number hud_globals item item_collection damage_effect lens_flare lightning device_light_fixture light sound_looping device_machine globals meter light_volume gbxmodel model multiplayer_scenario_description preferences_network_game object particle particle_system physics placeholder point_physics projectile weather_particle_system scenario_structure_bsp scenery shader_transparent_chicago_extended shader_transparent_chicago scenario shader_environment shader_transparent_glass shader sky shader_transparent_meter sound sound_environment shader_model shader_transparent_generic ui_widget_collection shader_transparent_plasma sound_scenery string_list shader_transparent_water tag_collection camera_track dialogue unit_hud_interface unit unicode_string_list virtual_keyboard vehicle weapon wind weapon_hud_interface"
        ).Split(' '), StringComparer.Ordinal);

    private static IEnumerable<string> MatchingTags(string root, IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        var pending = new Stack<string>(); pending.Push(root); var matches = new List<string>();
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) != ".git" && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = Path.GetExtension(file);
                if (extension.Length < 2 || !TagGroups.Contains(extension[1..])) continue;
                var relative = Path.GetRelativePath(root, file).Replace('/', '\\');
                if (!excludes.Any(pattern => Matches(relative, pattern)) && (includes.Count == 0 || includes.Any(pattern => Matches(relative, pattern)))) matches.Add(relative);
            }
        }
        return matches.OrderBy(p => p, StringComparer.Ordinal);
    }
}
