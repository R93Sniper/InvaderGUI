using System.Text;

namespace InvaderGui.Core;

/// <summary>Labels complete lines instead of arbitrary pipe reads. Raw chunks remain unchanged.</summary>
public sealed class OutputFormatter
{
    private sealed record PendingLine(long Order, StringBuilder Text);
    private readonly StringBuilder complete = new();
    private readonly Dictionary<string, PendingLine> pending = new();
    private long order;

    public void Append(string stream, string text)
    {
        if (stream == "workflow" && text.Length > 0)
        {
            foreach (var (pendingStream, line) in pending.OrderBy(p => p.Value.Order))
                complete.Append('[').Append(pendingStream).Append("] ").Append(line.Text).AppendLine();
            pending.Clear();
        }
        int offset = 0;
        while (offset < text.Length)
        {
            if (!pending.TryGetValue(stream, out var line))
            {
                line = new(++order, new()); pending.Add(stream, line);
            }
            var newline = text.IndexOf('\n', offset);
            if (newline < 0) { line.Text.Append(text, offset, text.Length - offset); break; }
            line.Text.Append(text, offset, newline - offset + 1);
            complete.Append('[').Append(stream).Append("] ").Append(line.Text);
            pending.Remove(stream); offset = newline + 1;
        }
    }

    public override string ToString()
    {
        var display = new StringBuilder(complete.ToString());
        foreach (var (stream, line) in pending.OrderBy(p => p.Value.Order))
        {
            if (display.Length > 0 && display[^1] != '\n') display.AppendLine();
            display.Append('[').Append(stream).Append("] ").Append(line.Text);
        }
        return display.ToString();
    }
}
