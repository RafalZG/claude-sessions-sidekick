using System.IO;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

public static class FavoritesService
{
    private static readonly string FavoritesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "favorites.json");

    private static HashSet<string>? _cache;

    public static HashSet<string> Load()
    {
        if (_cache != null)
        {
            return _cache;
        }

        if (!File.Exists(FavoritesPath))
        {
            _cache = [];
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(FavoritesPath);
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            _cache = [.. list];
        }
        catch
        {
            _cache = [];
        }

        return _cache;
    }

    public static void Toggle(string sessionId)
    {
        var favorites = Load();
        if (!favorites.Remove(sessionId))
        {
            favorites.Add(sessionId);
        }
        Save();
    }

    public static bool IsFavorite(string sessionId)
    {
        return Load().Contains(sessionId);
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FavoritesPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_cache?.ToList() ?? []);
            File.WriteAllText(FavoritesPath, json);
        }
        catch
        {
            // Non-critical
        }
    }
}
