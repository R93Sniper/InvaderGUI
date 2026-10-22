using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvaderGui.Core;

public sealed record RefactorCompatibility([property: JsonRequired] string ExpectedVersion);
public sealed record RefactorPlanRoot([property: JsonRequired] string RootId, [property: JsonRequired] string Path, [property: JsonRequired] int Precedence)
{
    public string? Name { get; init; }
}
public sealed record RefactorProfileBinding
{
    [JsonRequired] public Guid ProfileId { get; init; }
    [JsonRequired] public string ProfileNameAtSave { get; init; } = "";
    [JsonRequired] public string WorkingDirectory { get; init; } = "";
    [JsonRequired] public ImmutableArray<RefactorPlanRoot> TagRoots { get; init; } = [];
}
public sealed record RefactorPlanDocument
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    [JsonRequired] public RefactorCompatibility InvaderCompatibility { get; init; } = new(RefactorPlanFiles.ExpectedVersion);
    [JsonRequired] public RefactorProfileBinding ProfileBinding { get; init; } = new();
    [JsonRequired] public ImmutableArray<RefactorCommand> Queue { get; init; } = [];
}
public sealed record RefactorPlanRead(RefactorPlanDocument Document, string ContentHash);

/// <summary>Strict versioned data, with no executable text or scripting interpreter.</summary>
public static class RefactorPlanFiles
{
    public const string ExpectedVersion = "0.55.0.r4134.33518109";
    public const string Extension = ".txt";
    private static readonly JsonSerializerOptions Options = new(JsonFiles.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    public static string FilePath(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (absolute.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Repository metadata cannot be used for a Refactor plan or its bound folders.");
        return absolute;
    }
    private static string BoundPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new JsonException("Plan working directory and tag roots must be absolute paths.");
        return Path.TrimEndingDirectorySeparator(FilePath(path));
    }

    public static RefactorPlanDocument Validate(RefactorPlanDocument document)
    {
        if (document is null || document.SchemaVersion != 1) throw new JsonException("Unsupported Refactor plan schema. Expected schemaVersion 1.");
        if (document.InvaderCompatibility?.ExpectedVersion != ExpectedVersion) throw new JsonException($"This plan must target Invader {ExpectedVersion}.");
        var binding = document.ProfileBinding;
        if (binding is null || binding.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(binding.ProfileNameAtSave) || binding.TagRoots.IsDefaultOrEmpty)
            throw new JsonException("The plan needs a profile identity, profile name, working directory and ordered tag roots.");
        var rootIds = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<RefactorPlanRoot>();
        foreach (var root in binding.TagRoots)
        {
            if (root is null || string.IsNullOrWhiteSpace(root.RootId) || !rootIds.Add(root.RootId) || root.Precedence != roots.Count)
                throw new JsonException("Tag roots need unique identities and precedence matching their array order (starting at zero).");
            roots.Add(root with { Path = BoundPath(root.Path), Name = string.IsNullOrWhiteSpace(root.Name) ? root.RootId : root.Name });
        }
        if (document.Queue.IsDefault) throw new JsonException("The pending queue must be an array.");
        var ids = new HashSet<Guid>(); var commands = new List<RefactorCommand>();
        foreach (var command in document.Queue)
        {
            if (command is null || command.Id == Guid.Empty || !ids.Add(command.Id) || command.Mode is null ||
                command.TagMappings.IsDefault || command.GroupMappings.IsDefault || command.ReplaceStrings.IsDefault ||
                command.TagMappings.Any(pair => pair is null || pair.Source is null || pair.Destination is null) ||
                command.GroupMappings.Any(pair => pair is null || pair.SourceGroup is null || pair.DestinationGroup is null) ||
                command.ReplaceStrings.Any(pair => pair is null || pair.Find is null || pair.Replace is null) ||
                command.RecursiveMapping is { } recursive && (recursive.SourceDirectory is null || recursive.DestinationDirectory is null))
                throw new JsonException("Every queue entry needs a unique nonempty ID and complete structured parameters.");
            try
            {
                var normalized = RefactorPlanner.Normalize(command);
                if (normalized.Mode != "no-move" && normalized.TagMappings.Any(pair => !Path.GetExtension(pair.Source).Equals(Path.GetExtension(pair.Destination), StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Move/Copy tag mappings must preserve group extensions.");
                if (normalized.RecursiveMapping is { } folder && (folder.SourceDirectory.Equals(folder.DestinationDirectory, StringComparison.OrdinalIgnoreCase) ||
                    folder.DestinationDirectory.StartsWith(folder.SourceDirectory + "\\", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Recursive mappings cannot target their source or a descendant.");
                commands.Add(normalized);
            }
            catch (ArgumentException ex) { throw new JsonException($"Command {commands.Count + 1}: {ex.Message}", ex); }
        }
        return document with
        {
            ProfileBinding = binding with { WorkingDirectory = BoundPath(binding.WorkingDirectory), TagRoots = roots.ToImmutableArray() },
            Queue = commands.ToImmutableArray()
        };
    }

    public static string Serialize(RefactorPlanDocument document) => JsonSerializer.Serialize(Validate(document), Options);

    public static RefactorPlanDocument Parse(string json)
    {
        using var syntax = JsonDocument.Parse(json, new() { MaxDepth = 64 });
        RejectDuplicateProperties(syntax.RootElement);
        return Validate(JsonSerializer.Deserialize<RefactorPlanDocument>(json, Options) ?? throw new JsonException("The Refactor plan cannot be null."));
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"Duplicate JSON property: {property.Name}");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    public static RefactorPlanRead Read(string path, RefactorProfileBinding? textBinding = null)
    {
        var bytes = File.ReadAllBytes(FilePath(path));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        return new(text.TrimStart().StartsWith('{') ? Parse(text) : RefactorPlanText.Parse(text, textBinding), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static string Save(string path, RefactorPlanDocument document, string? expectedContentHash = null)
    {
        path = FilePath(path);
        var bytes = new UTF8Encoding(false).GetBytes(Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? Serialize(document) : RefactorPlanText.Serialize(document));
        if (expectedContentHash is not null && (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expectedContentHash))
            throw new IOException("The plan file changed or was removed outside the application. Load it again or use Save as to choose a file explicitly.");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
