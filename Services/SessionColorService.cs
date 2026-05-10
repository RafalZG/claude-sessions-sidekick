using System.IO;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

public static class SessionColorService
{
    public static readonly string[] AvailableColors =
        ["Red", "Orange", "Yellow", "Green", "Blue", "Purple"];

    public static readonly Dictionary<string, string> ColorHexMap = new()
    {
        ["Red"] = "#E05050",
        ["Orange"] = "#E8A030",
        ["Yellow"] = "#D0D040",
        ["Green"] = "#50B050",
        ["Blue"] = "#5090E0",
        ["Purple"] = "#A060D0"
    };

    private static readonly string ColorsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "session-colors.json");

    private static readonly object _lock = new();
    private static Dictionary<string, string>? _cache;

    private static Dictionary<string, string> LoadUnsafe()
    {
        if (_cache != null)
        {
            return _cache;
        }

        if (!File.Exists(ColorsPath))
        {
            _cache = [];
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(ColorsPath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch
        {
            _cache = [];
        }

        return _cache;
    }

    public static string? GetColor(string sessionId)
    {
        lock (_lock)
        {
            return LoadUnsafe().GetValueOrDefault(sessionId);
        }
    }

    public static void SetColor(string sessionId, string? color)
    {
        lock (_lock)
        {
            var colors = LoadUnsafe();
            if (color == null)
            {
                colors.Remove(sessionId);
            }
            else
            {
                colors[sessionId] = color;
            }
            SaveUnsafe();
        }
    }

    private static void SaveUnsafe()
    {
        try
        {
            var dir = Path.GetDirectoryName(ColorsPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            // Atomic write: write to temp then move
            var tempPath = ColorsPath + ".tmp";
            var json = JsonSerializer.Serialize(_cache ?? []);
            File.WriteAllText(tempPath, json);

            if (File.Exists(ColorsPath))
            {
                File.Delete(ColorsPath);
            }
            File.Move(tempPath, ColorsPath);
        }
        catch
        {
            // Non-critical
        }
    }
}
