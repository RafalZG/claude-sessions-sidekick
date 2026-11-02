namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// The one place token counts get turned into short display strings
/// ("417.5M", "1.1B"), so a new magnitude tier only ever needs adding
/// here. Memory-file sizes keep their own prose format ("~2.1k tokens")
/// in MemoryAuditService.
/// </summary>
public static class TokenFormat
{
    public static string Compact(long tokens)
    {
        // Corrupt or overflowed counts from session files can go negative;
        // show 0 rather than a raw negative long.
        if (tokens < 0)
        {
            return "0";
        }
        return tokens switch
        {
            >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:F1}B",
            >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
            >= 1_000 => $"{tokens / 1_000.0:F1}k",
            _ => tokens.ToString()
        };
    }
}
