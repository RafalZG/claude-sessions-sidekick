using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

public static class SessionSharingService
{
    private static readonly string ClaudeProjectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    /// <summary>
    /// Exports a session JSONL + companion dir to a ZIP file.
    /// Writes to a temp file first and moves on success to avoid leaving
    /// partial archives on disk if export fails.
    /// </summary>
    public static void Export(string jsonlPath, string zipPath)
    {
        var sessionId = Path.GetFileNameWithoutExtension(jsonlPath);
        var parentDir = Path.GetDirectoryName(jsonlPath) ?? "";
        var tempZipPath = zipPath + ".tmp";

        try
        {
            using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                // Add the JSONL file
                zip.CreateEntryFromFile(jsonlPath, $"{sessionId}.jsonl");

                // Add companion directory if exists (subagents, tool-results)
                var companionDir = Path.Combine(parentDir, sessionId);
                if (Directory.Exists(companionDir))
                {
                    foreach (var file in Directory.GetFiles(companionDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(parentDir, file);
                        zip.CreateEntryFromFile(file, relativePath);
                    }
                }

                // Read source cwd from JSONL (parse first line as JSON)
                var sourceCwd = ExtractCwd(jsonlPath);
                var projectDirName = Path.GetFileName(parentDir);

                var metaEntry = zip.CreateEntry("_session_meta.txt");
                using var writer = new StreamWriter(metaEntry.Open());
                writer.WriteLine($"sessionId={sessionId}");
                writer.WriteLine($"sourceCwd={sourceCwd}");
                writer.WriteLine($"projectDirName={projectDirName}");
                writer.WriteLine($"exportDate={DateTimeOffset.Now:o}");
            }

            // Atomic move - overwrite if exists
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            File.Move(tempZipPath, zipPath);
        }
        catch
        {
            // Clean up partial temp file on any failure
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* ignore */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Imports a session ZIP into the target project path.
    /// Rewrites cwd fields in JSONL to match the target path.
    /// </summary>
    public static (bool success, string message) Import(string zipPath, string targetCwd)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var metaEntry = zip.GetEntry("_session_meta.txt");
            if (metaEntry == null)
            {
                return (false, "Invalid session archive - missing metadata");
            }

            string sourceCwd;
            string sessionId;
            using (var reader = new StreamReader(metaEntry.Open()))
            {
                var meta = new Dictionary<string, string>();
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        meta[parts[0]] = parts[1];
                    }
                }

                sourceCwd = meta.GetValueOrDefault("sourceCwd", "");
                sessionId = meta.GetValueOrDefault("sessionId", "");
            }

            if (string.IsNullOrEmpty(sessionId))
            {
                return (false, "Invalid session archive - missing session ID");
            }

            // Determine target project directory
            var targetDirName = EncodeDirName(targetCwd);
            var targetDir = Path.Combine(ClaudeProjectsDir, targetDirName);
            var targetDirFull = Path.GetFullPath(targetDir);
            Directory.CreateDirectory(targetDir);

            // Check if session already exists
            var targetJsonl = Path.Combine(targetDir, $"{sessionId}.jsonl");
            if (File.Exists(targetJsonl))
            {
                return (false, $"Session {sessionId[..8]} already exists in the target project");
            }

            // Extract and rewrite JSONL
            var jsonlEntry = zip.GetEntry($"{sessionId}.jsonl");
            if (jsonlEntry == null)
            {
                return (false, "Invalid session archive - missing JSONL file");
            }

            using (var entryStream = jsonlEntry.Open())
            using (var reader = new StreamReader(entryStream))
            using (var writer = new StreamWriter(targetJsonl, false, Encoding.UTF8))
            {
                string? jsonlLine;
                while ((jsonlLine = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(jsonlLine))
                    {
                        continue;
                    }

                    var rewritten = RewritePaths(jsonlLine, sourceCwd, targetCwd);
                    writer.WriteLine(rewritten);
                }
            }

            // Extract companion files (subagents, tool-results) with zip-slip protection
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == $"{sessionId}.jsonl" ||
                    entry.FullName == "_session_meta.txt" ||
                    string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var targetPath = Path.Combine(targetDir, entry.FullName);
                var targetPathFull = Path.GetFullPath(targetPath);

                // Zip-slip protection: verify the resolved path stays inside targetDir
                if (!targetPathFull.StartsWith(targetDirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"Rejected zip entry outside target dir: {entry.FullName}");
                    continue;
                }

                var entryDir = Path.GetDirectoryName(targetPath);
                if (entryDir != null)
                {
                    Directory.CreateDirectory(entryDir);
                }
                entry.ExtractToFile(targetPath, overwrite: false);
            }

            return (true, $"Session imported successfully. Resume with:\nclaude --resume {sessionId}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Session import failed", ex);
            return (false, $"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites absolute paths in a JSONL line from source to target cwd.
    /// Uses sentinel-based replacement to avoid double-rewriting and handles
    /// case-insensitive Windows path matching.
    /// </summary>
    private static string RewritePaths(string line, string sourceCwd, string targetCwd)
    {
        if (string.IsNullOrEmpty(sourceCwd))
        {
            return line;
        }

        // Build path variants for source (all lowercase for case-insensitive match)
        var sourceForward = sourceCwd.Replace('\\', '/');
        var sourceBackslash = sourceCwd.Replace('/', '\\');
        var sourceDoubleBackslash = sourceBackslash.Replace("\\", "\\\\");

        // Corresponding target variants
        var targetForward = targetCwd.Replace('\\', '/');
        var targetBackslash = targetCwd.Replace('/', '\\');
        var targetDoubleBackslash = targetBackslash.Replace("\\", "\\\\");

        // Sentinel-based replacement: replace each variant with a unique placeholder first,
        // then substitute sentinels with the target. This prevents later passes from
        // corrupting already-rewritten sections.
        const string sentinelDouble = "\u0001DOUBLE\u0001";
        const string sentinelForward = "\u0001FORWARD\u0001";
        const string sentinelBackslash = "\u0001BACKSLASH\u0001";

        // Order matters: double-backslash (most escaped) before backslash
        var result = ReplaceCaseInsensitive(line, sourceDoubleBackslash, sentinelDouble);
        result = ReplaceCaseInsensitive(result, sourceForward, sentinelForward);
        result = ReplaceCaseInsensitive(result, sourceBackslash, sentinelBackslash);

        // Now substitute sentinels with actual target values
        result = result.Replace(sentinelDouble, targetDoubleBackslash);
        result = result.Replace(sentinelForward, targetForward);
        result = result.Replace(sentinelBackslash, targetBackslash);

        return result;
    }

    /// <summary>
    /// Case-insensitive string.Replace (for Windows path matching).
    /// </summary>
    private static string ReplaceCaseInsensitive(string input, string search, string replacement)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(search))
        {
            return input;
        }

        var sb = new StringBuilder();
        int lastIndex = 0;
        int index;

        while ((index = input.IndexOf(search, lastIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            sb.Append(input, lastIndex, index - lastIndex);
            sb.Append(replacement);
            lastIndex = index + search.Length;
        }

        if (lastIndex < input.Length)
        {
            sb.Append(input, lastIndex, input.Length - lastIndex);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads the "cwd" value from the first JSON object in a JSONL session file.
    /// Uses proper JSON parsing to avoid matching nested strings.
    /// </summary>
    private static string ExtractCwd(string jsonlPath)
    {
        using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("cwd", out var cwdProp) &&
                    cwdProp.ValueKind == JsonValueKind.String)
                {
                    return cwdProp.GetString() ?? "";
                }
            }
            catch (JsonException)
            {
                // Malformed line, try next
            }
        }

        return "";
    }

    /// <summary>
    /// Encodes a path as a Claude project directory name.
    /// Claude Code replaces \ / and _ with dashes.
    /// e.g. "D:\Projects\MyApp_Main" → "D--Projects-MyApp-Main"
    /// Only supports drive-letter paths (C:\, D:\, etc) - UNC paths not supported.
    /// </summary>
    private static string EncodeDirName(string path)
    {
        path = Path.GetFullPath(path).TrimEnd('\\', '/');

        // Must be a drive-letter path
        if (path.Length < 3 || path[1] != ':' || !(path[2] == '\\' || path[2] == '/'))
        {
            throw new ArgumentException($"Only local drive paths are supported (got: {path})");
        }

        var drive = path[0];
        var rest = path[3..]; // skip "D:\"
        var encoded = rest
            .Replace('\\', '-')
            .Replace('/', '-')
            .Replace('_', '-');

        return $"{drive}--{encoded}";
    }
}
