using System.IO;
using System.Text.Json;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

public static class PromptService
{
    private static readonly string PromptsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "prompts.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static List<PromptEntry>? _cache;

    public static List<PromptEntry> Load()
    {
        if (_cache != null)
        {
            return _cache;
        }

        if (!File.Exists(PromptsPath))
        {
            _cache = [];
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(PromptsPath);
            _cache = JsonSerializer.Deserialize<List<PromptEntry>>(json) ?? [];
        }
        catch
        {
            _cache = [];
        }

        return _cache;
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(PromptsPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_cache ?? [], JsonOptions);
            File.WriteAllText(PromptsPath, json);
        }
        catch
        {
            // Non-critical
        }
    }

    public static void Add(PromptEntry entry)
    {
        Load().Add(entry);
        Save();
    }

    public static void Update(PromptEntry entry)
    {
        var list = Load();
        var idx = list.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0)
        {
            list[idx] = entry;
            Save();
        }
    }

    public static void Remove(string id)
    {
        var list = Load();
        list.RemoveAll(e => e.Id == id);
        Save();
    }

    public static List<string> GetCategories()
    {
        return Load()
            .Select(e => e.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
    }

    public static void InvalidateCache()
    {
        _cache = null;
    }
}
