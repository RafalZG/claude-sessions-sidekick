using System.IO;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Where the search hit was found in the session.
/// Used in tooltips so the user can tell at a glance whether a session
/// genuinely discussed the topic or just mentioned the term in passing
/// (e.g. inside a pasted error log or a list of test names).
/// </summary>
public enum SessionContentMatchSource
{
    User,
    Assistant,
    CustomTitle
}

public sealed record SessionContentMatch(SessionContentMatchSource Source, string Excerpt);

/// <summary>
/// Streaming full-text search across session JSONL files.
/// Caller passes pre-filtered (project + days) list to keep the scan small.
/// Returns a dictionary keyed by session id with the first match found in
/// real user/assistant text content (skipping tool_result, tool_use, thinking,
/// signatures, and other JSON noise that would otherwise produce false positives).
/// </summary>
public static class SessionContentSearchService
{
    private const int ExcerptRadius = 60;

    public static async Task<Dictionary<string, SessionContentMatch>> SearchAsync(
        IReadOnlyList<(string SessionId, string FilePath)> sessions,
        string query,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, SessionContentMatch>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return results;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sessionId, path) = sessions[i];

            try
            {
                var match = await FindFirstMatchAsync(path, query, cancellationToken).ConfigureAwait(false);
                if (match != null)
                {
                    results[sessionId] = match;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // File may be locked, deleted, or permission-denied — skip silently.
            }

            progress?.Report(i + 1);
        }

        return results;
    }

    internal static async Task<SessionContentMatch?> FindFirstMatchAsync(
        string path, string query, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            // Cheap reject: if the raw line doesn't contain the query at all,
            // skip the JSON parse entirely.
            if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var match = TryExtractMatch(line, query);
            if (match != null)
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses a JSONL line and, if the query appears inside real user/assistant
    /// text (or a /rename custom title), returns the match with a short excerpt
    /// centered on the hit. Returns null when the only matches are in
    /// tool_result, tool_use, thinking, signatures, etc.
    /// </summary>
    internal static SessionContentMatch? TryExtractMatch(string jsonLine, string query)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                return null;
            }
            var type = typeProp.GetString();

            if (type == "custom-title" &&
                root.TryGetProperty("customTitle", out var titleProp))
            {
                var title = titleProp.GetString();
                if (title != null && IndexOfQuery(title, query) >= 0)
                {
                    return new SessionContentMatch(
                        SessionContentMatchSource.CustomTitle,
                        BuildExcerpt(title, query));
                }
                return null;
            }

            if (type != "user" && type != "assistant")
            {
                return null;
            }

            if (!root.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("content", out var content))
            {
                return null;
            }

            var source = type == "user" ? SessionContentMatchSource.User : SessionContentMatchSource.Assistant;

            if (content.ValueKind == JsonValueKind.String)
            {
                var s = content.GetString();
                if (s != null && IndexOfQuery(s, query) >= 0)
                {
                    return new SessionContentMatch(source, BuildExcerpt(s, query));
                }
                return null;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var itemType)) continue;
                if (itemType.GetString() != "text") continue;
                if (!item.TryGetProperty("text", out var textProp)) continue;
                var text = textProp.GetString();
                if (text != null && IndexOfQuery(text, query) >= 0)
                {
                    return new SessionContentMatch(source, BuildExcerpt(text, query));
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int IndexOfQuery(string haystack, string query)
    {
        return haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildExcerpt(string text, string query)
    {
        var idx = IndexOfQuery(text, query);
        if (idx < 0)
        {
            return text;
        }
        var start = Math.Max(0, idx - ExcerptRadius);
        var end = Math.Min(text.Length, idx + query.Length + ExcerptRadius);
        var excerpt = text.Substring(start, end - start);
        // Collapse newlines so the tooltip stays compact.
        excerpt = excerpt.Replace('\r', ' ').Replace('\n', ' ');
        if (start > 0)
        {
            excerpt = "..." + excerpt;
        }
        if (end < text.Length)
        {
            excerpt += "...";
        }
        return excerpt;
    }
}
