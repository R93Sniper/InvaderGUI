using System.Text.RegularExpressions;

namespace InvaderGui.Core;

public static class TagHandlingDiagnostics
{
    // Only verified message prefixes, at line boundaries, from these specific tools.
    // Reassemble each stream before matching: process chunks are not complete lines.
    public static string? Summarize(string operation, IEnumerable<OutputChunk> chunks)
    {
        if (operation is not ("repair-tags" or "convert-tags" or "strip-tags" or "dependencies" or "refactor" or "compare" or "extract-tags" or "recover-source")) return null;
        var streams = chunks.GroupBy(c => c.Stream).ToDictionary(g => g.Key, g => string.Concat(g.Select(c => c.Text)));
        var stderr = streams.GetValueOrDefault("stderr", "");
        var stdout = streams.GetValueOrDefault("stdout", "");
        var errors = stderr.Split('\n').Select(l => Regex.Replace(l.TrimEnd('\r'), @"\x1B\[[0-?]*[ -/]*[@-~]", ""))
            .Where(line => operation switch
            {
                "repair-tags" => line.StartsWith("Error: Failed to bludgeon ", StringComparison.Ordinal) || line.StartsWith("Error: Failed to write to ", StringComparison.Ordinal) || line.StartsWith("Failed to open ", StringComparison.Ordinal),
                "convert-tags" => line.StartsWith("Failed to convert ", StringComparison.Ordinal) || line.StartsWith("Failed to write to ", StringComparison.Ordinal) || line.StartsWith("Failed to read ", StringComparison.Ordinal),
                "strip-tags" => line.StartsWith("Error: Failed to strip ", StringComparison.Ordinal) || line.StartsWith("Error: Failed to write to ", StringComparison.Ordinal) || line.StartsWith("Failed to open ", StringComparison.Ordinal),
                "dependencies" => line.StartsWith("Failed to list dependencies", StringComparison.Ordinal),
                "refactor" => line.StartsWith("Error: ", StringComparison.Ordinal) || line.StartsWith("Failed to open ", StringComparison.Ordinal) || line.StartsWith("Cannot safely refactor ", StringComparison.Ordinal),
                "compare" => line.StartsWith("Cannot compare ", StringComparison.Ordinal) || line.StartsWith("Cannot functional compare ", StringComparison.Ordinal),
                "extract-tags" => line.StartsWith("Error while extracting ", StringComparison.Ordinal) || line.StartsWith("ERROR: Failed to extract ", StringComparison.Ordinal) || line.StartsWith("ERROR: Failed to save ", StringComparison.Ordinal),
                "recover-source" => line.StartsWith("Failed to read ", StringComparison.Ordinal) || line.StartsWith("Failed to write to ", StringComparison.Ordinal) || line.StartsWith("Failed to open ", StringComparison.Ordinal),
                _ => false
            }).ToArray();
        if (errors.Length > 0) return "Invader reported tag errors. The process exit code is retained separately.\n" + string.Join("\n", errors);
        if (operation == "compare" && stderr.Split('\n').Any(l => l.StartsWith("Failed to find or read a ", StringComparison.Ordinal)))
            return "Invader could not load one or more resource maps. Some external data may be unavailable; inspect the output before interpreting comparison totals.";
        if (operation == "recover-source" && stderr.Split('\n').Any(l => l.StartsWith("Skipped ", StringComparison.Ordinal)))
            return "Invader skipped some source recoveries. Inspect the recovered/skipped totals; exit success may indicate only partial recovery.";
        if (operation == "convert-tags" && stderr.Split('\n').Any(l => l.StartsWith("Skipping ", StringComparison.Ordinal)))
            return "Invader skipped existing destination tags because Overwrite destination tags was not enabled. Inspect output for the conversion count.";
        if (operation == "strip-tags" && stdout.Split('\n').Any(l => l.StartsWith("Skipped ", StringComparison.Ordinal)))
            return "Invader reported skipped/unchanged tags. Inspect output; the actual exit code is retained.";
        if (operation == "dependencies" && stdout.Split('\n').Any(l => l.TrimEnd('\r').EndsWith(" [BROKEN]", StringComparison.Ordinal)))
            return "Invader reported broken dependency references. Inspect the marked output.";
        return null;
    }
}
