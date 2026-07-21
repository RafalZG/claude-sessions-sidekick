using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Extracts Claude's latest reply from a session JSONL so it can be copied to the
/// clipboard clean — the JSONL holds the ORIGINAL authored text, free of the
/// console's width-based hard wrapping and leading indentation that make a manual
/// terminal copy ugly (GitHub issue #2).
/// </summary>
public static class LatestReplyService
{
    /// <summary>
    /// Returns the text of the last assistant message that contains any text, with its
    /// text blocks joined by blank lines. Null if the file has no such message.
    /// Scans from the end so large session files stay cheap.
    /// </summary>
    public static string? ExtractLatestAssistantText(string jsonlPath)
    {
        if (string.IsNullOrEmpty(jsonlPath) || !File.Exists(jsonlPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(jsonlPath);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"LatestReply: read failed for {jsonlPath}: {ex.Message}");
            return null;
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var text = TryExtractAssistantText(lines[i]);
            if (text != null)
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>
    /// If the line is an assistant message with text content, returns the joined text;
    /// otherwise null. Public for unit testing the parse in isolation.
    /// </summary>
    public static string? TryExtractAssistantText(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant")
            {
                return null;
            }
            if (!root.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "text") continue;
                if (!block.TryGetProperty("text", out var txt)) continue;

                var s = txt.GetString();
                if (string.IsNullOrEmpty(s)) continue;

                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(s);
            }

            var result = sb.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        catch
        {
            // Malformed line — skip.
            return null;
        }
    }
}
