using System.IO;
using System.Text.RegularExpressions;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Builds a session object straight from its file on disk, for a running
/// Claude process the watcher doesn't track — a session idle for longer than
/// the watcher looks back (MonitorMike's case: a window untouched since
/// yesterday still deserves the right "Copy Last Reply"). The process's
/// <c>--resume</c> ID names the exact file; without it the newest session
/// file of the process's working directory is taken. The slug and custom
/// title are read from the file so window-title matching still works.
/// </summary>
public static class DiskSessionLookup
{
    // How much of the file to read when looking for the slug/title. The slug
    // appears on many records, so the first and last chunk together cover
    // both small and huge session files.
    private const int ChunkBytes = 128 * 1024;

    // The captures allow JSON escape pairs (\" \\ \uXXXX) but exclude raw
    // newlines: a valid JSON string never holds one, and this also stops a
    // capture from running across the seam where the head and tail chunks of
    // a big file are glued together. The title pattern assumes Claude Code
    // writes "type" before "title" on the record line — if that order ever
    // changes, title extraction silently stops (heuristic, verified live).
    private static readonly Regex SlugRegex = new(
        @"""slug""\s*:\s*""((?:[^""\\\n]|\\.)+)""", RegexOptions.Compiled);

    private static readonly Regex CustomTitleRegex = new(
        @"""type""\s*:\s*""custom-title""[^\n]*?""title""\s*:\s*""((?:[^""\\\n]|\\.)+)""", RegexOptions.Compiled);

    /// <summary>
    /// Builds the session for a process whose pid file names it exactly but
    /// the watcher doesn't track it (idle for too long). The pid file's live
    /// name travels along so window-title matching still works.
    /// </summary>
    public static SessionTokenData? ForPidInfo(ClaudeSessionPidInfo info)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(info.Cwd))
            {
                return null;
            }

            var projectDir = Path.Combine(
                ClaudeConfigService.ClaudeProjectsRoot
                , SessionRestoreService.EncodeProjectDirName(info.Cwd));
            var filePath = Path.Combine(projectDir, info.SessionId + ".jsonl");
            if (!File.Exists(filePath))
            {
                return null;
            }

            var file = new FileInfo(filePath);
            var names = ReadNamesFromFile(filePath);
            var projectName = Path.GetFileName(info.Cwd.TrimEnd('\\', '/'));
            var session = new SessionTokenData
            {
                SessionId = info.SessionId,
                FilePath = file.FullName,
                Cwd = info.Cwd,
                ProjectName = string.IsNullOrEmpty(projectName) ? info.Cwd : projectName,
                Slug = names.Slug,
                // Only a name the user typed counts; Claude's own "derived"
                // label (like "dev-63") would hide the slug and the prompt.
                CustomName = (info.HasUserName ? info.Name : null) ?? names.CustomTitle,
                FirstMessage = names.FirstPrompt,
                LastMeaningfulPrompt = names.LastMeaningfulPrompt,
                LastSeen = file.LastWriteTimeUtc,
            };
            return session;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"DiskSessionLookup: pid-info lookup failed: {ex.Message}");
            return null;
        }
    }

    public static SessionTokenData? ForProcess(FocusedClaudeProcess proc)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(proc.WorkingDirectory))
            {
                return null;
            }

            var projectDir = Path.Combine(
                ClaudeConfigService.ClaudeProjectsRoot
                , SessionRestoreService.EncodeProjectDirName(proc.WorkingDirectory));
            if (!Directory.Exists(projectDir))
            {
                return null;
            }

            var file = FindSessionFile(projectDir, proc.CommandLine);
            if (file == null)
            {
                return null;
            }

            var names = ReadNamesFromFile(file.FullName);
            var projectName = Path.GetFileName(proc.WorkingDirectory.TrimEnd('\\', '/'));
            var session = new SessionTokenData
            {
                SessionId = Path.GetFileNameWithoutExtension(file.Name),
                FilePath = file.FullName,
                Cwd = proc.WorkingDirectory,
                ProjectName = projectName,
                Slug = names.Slug,
                CustomName = names.CustomTitle,
                FirstMessage = names.FirstPrompt,
                LastMeaningfulPrompt = names.LastMeaningfulPrompt,
                LastSeen = file.LastWriteTimeUtc,
            };
            return session;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"DiskSessionLookup: failed: {ex.Message}");
            return null;
        }
    }

    private static FileInfo? FindSessionFile(string projectDir, string commandLine)
    {
        var resumeId = FocusedSessionResolver.ExtractResumeSessionId(commandLine);
        if (resumeId != null)
        {
            var exact = Path.Combine(projectDir, resumeId + ".jsonl");
            if (File.Exists(exact))
            {
                var exactInfo = new FileInfo(exact);
                return exactInfo;
            }
        }

        var newest = Directory.GetFiles(projectDir, "*.jsonl")
            .Select(f => new FileInfo(f))
            .Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f.Name), out _))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest;
    }

    internal static (string? Slug, string? CustomTitle, string? FirstPrompt, string? LastMeaningfulPrompt) ReadNamesFromFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var headLength = (int)Math.Min(stream.Length, ChunkBytes);
            var head = new byte[headLength];
            stream.ReadExactly(head, 0, headLength);

            // Always read the end when the file is longer than one chunk —
            // the latest rename/slug lives there ("last value wins").
            var tail = Array.Empty<byte>();
            if (stream.Length > ChunkBytes)
            {
                var tailLength = (int)Math.Min(ChunkBytes, stream.Length - headLength);
                stream.Seek(-tailLength, SeekOrigin.End);
                tail = new byte[tailLength];
                stream.ReadExactly(tail, 0, tail.Length);
            }

            var text = System.Text.Encoding.UTF8.GetString(head)
                + "\n"
                + System.Text.Encoding.UTF8.GetString(tail);
            var names = ExtractNames(text);
            var prompts = ExtractPrompts(text);
            return (names.Slug, names.CustomTitle, prompts.FirstPrompt, prompts.LastMeaningfulPrompt);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    /// <summary>Digs the prompts out of the same head+tail chunks, for a
    /// session with no rename and no slug (otherwise the UI shows a bare id).
    /// Returns the first prompt and the LATEST prompt long enough to mean
    /// something — the same two fields the watcher keeps, so the Topic ladder
    /// works identically. Lines cut in half at a chunk boundary simply fail
    /// to parse and are skipped.</summary>
    internal static (string? FirstPrompt, string? LastMeaningfulPrompt) ExtractPrompts(string text)
    {
        string? firstPrompt = null;
        string? lastMeaningful = null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user")
                {
                    continue;
                }
                if (!SessionWatcherService.IsUserTypedMessage(root))
                {
                    continue;
                }

                var message = SessionWatcherService.ExtractMessageText(root);
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                firstPrompt ??= message;
                if (message.Trim().Length >= SessionWatcherService.MinMeaningfulPromptLength)
                {
                    lastMeaningful = message;
                }
            }
            catch
            {
                // Broken or half-a-line JSON — skip it.
            }
        }

        return (firstPrompt, lastMeaningful);
    }

    /// <summary>Takes the LAST slug and custom title found — later records
    /// carry the current values when the session was renamed. Both come back
    /// cleaned the same way as pid-file names (one line, no control
    /// characters, capped) — decoded escapes could otherwise smuggle real
    /// newlines or ANSI codes into logs and windows.</summary>
    internal static (string? Slug, string? CustomTitle) ExtractNames(string text)
    {
        string? slug = null;
        foreach (Match m in SlugRegex.Matches(text))
        {
            slug = DecodeJsonString(m.Groups[1].Value);
        }

        string? customTitle = null;
        foreach (Match m in CustomTitleRegex.Matches(text))
        {
            customTitle = DecodeJsonString(m.Groups[1].Value);
        }

        slug = ClaudeSessionPidFileService.SanitizeName(slug);
        customTitle = ClaudeSessionPidFileService.SanitizeName(customTitle);
        return (slug, customTitle);
    }

    /// <summary>Turns the raw captured value back into the real string —
    /// a title like <c>My \"big\" fix</c> must compare against window titles
    /// with its quotes and unicode escapes decoded.</summary>
    private static string DecodeJsonString(string raw)
    {
        if (raw.IndexOf('\\') < 0)
        {
            return raw;
        }

        try
        {
            var decoded = System.Text.Json.JsonSerializer.Deserialize<string>("\"" + raw + "\"");
            return decoded ?? raw;
        }
        catch
        {
            return raw;
        }
    }
}
