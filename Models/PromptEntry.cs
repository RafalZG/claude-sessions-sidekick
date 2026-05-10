using System.Text.Json.Serialization;

namespace ClaudeSessionsSidekick.Models;

public class PromptEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "General";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";
}
