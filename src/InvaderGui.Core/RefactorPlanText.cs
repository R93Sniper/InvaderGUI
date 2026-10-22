using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace InvaderGui.Core;

/// <summary>A command list parser, never a shell interpreter.</summary>
public static class RefactorPlanText
{
    public static string Serialize(RefactorPlanDocument document)
    {
        document = RefactorPlanFiles.Validate(document);
        var text = new StringBuilder();
        foreach (var command in document.Queue)
        {
            text.Append("invader-refactor");
            int remaining = 0;
            foreach (var argument in RefactorPlanner.Arguments(command, []))
            {
                if (argument.Any(c => c is '\r' or '\n'))
                    throw new ArgumentException("Text plans cannot contain line breaks inside command arguments.");
                if (remaining > 0)
                { text.Append(" \"").Append(argument.Replace("\"", "\"\"" )).Append('"'); remaining--; }
                else
                {
                    text.Append(' ').Append(argument);
                    remaining = argument is "--unsafe" ? 0 : argument is "--mode" or "--single-tag" ? 1 : 2;
                }
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    public static RefactorPlanDocument Parse(string text, RefactorProfileBinding? binding)
    {
        if (binding is null) throw new ArgumentException("Select a profile and tag folders before loading a text command list.");
        var commands = new List<RefactorCommand>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith("::") || line.Equals("rem", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("echo ", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("pause", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Equals("@echo off", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var tokens = Tokens(line);
                if (tokens.Count == 0 || !tokens[0].Equals("invader-refactor", StringComparison.OrdinalIgnoreCase) && !tokens[0].Equals("invader-refactor.exe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Expected an invader-refactor command or a comment.");
                var command = new RefactorCommand();
                bool mode = false, scope = false;
                for (int i = 1; i < tokens.Count; i++)
                {
                    string Next() => ++i < tokens.Count ? tokens[i] : throw new ArgumentException("Missing option argument.");
                    switch (tokens[i])
                    {
                        case "-M": case "--mode":
                            if (mode) throw new ArgumentException("Mode is specified more than once.");
                            mode = true; command = command with { Mode = Next() }; break;
                        case "-T": case "--tag": command = command with { TagMappings = command.TagMappings.Add(new(Next(), Next())) }; break;
                        case "-r": case "--recursive":
                            if (command.RecursiveMapping is not null) throw new ArgumentException("Only one recursive mapping is supported per command.");
                            command = command with { RecursiveMapping = new(Next(), Next()) }; break;
                        case "-g": case "--groups": command = command with { GroupMappings = command.GroupMappings.Add(new(Next(), Next())) }; break;
                        case "-R": case "--replace-string": command = command with { ReplaceStrings = command.ReplaceStrings.Add(new(Next(), Next())) }; break;
                        case "-s": case "--single-tag":
                            if (scope) throw new ArgumentException("Only one single-tag scope is supported.");
                            scope = true; command = command with { SingleTagScope = Next() }; break;
                        case "-U": case "--unsafe": command = command with { Unsafe = true }; break;
                        default: throw new ArgumentException($"Unsupported option: {tokens[i]}. Tag folders come from the window selection.");
                    }
                }
                commands.Add(RefactorPlanner.Normalize(command));
            }
            catch (ArgumentException ex) { throw new JsonException($"Line {number}: {ex.Message}", ex); }
        }
        return RefactorPlanFiles.Validate(new() { ProfileBinding = binding, Queue = commands.ToImmutableArray() });
    }

    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>(); var token = new StringBuilder(); bool quoted = false, started = false;
        for (int index = 0; index < line.Length; index++)
        {
            var c = line[index];
            if (c == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { token.Append('"'); index++; }
            else if (c == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            { if (started) { tokens.Add(token.ToString()); token.Clear(); started = false; } }
            else
            {
                if (!quoted && "&|<>^%".Contains(c)) throw new ArgumentException("Shell operators are not supported in command lists.");
                token.Append(c); started = true;
            }
        }
        if (quoted) throw new ArgumentException("Unclosed quote.");
        if (started) tokens.Add(token.ToString());
        return tokens;
    }
}
