using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSessionsSidekick.Models;

public class UsageData
{
    [JsonPropertyName("five_hour")]
    public UsageWindow? FiveHour { get; set; }

    [JsonPropertyName("seven_day")]
    public UsageWindow? SevenDay { get; set; }

    [JsonPropertyName("seven_day_oauth_apps")]
    public UsageWindow? SevenDayOAuthApps { get; set; }

    [JsonPropertyName("seven_day_opus")]
    public UsageWindow? SevenDayOpus { get; set; }
}

public class UsageWindow
{
    [JsonPropertyName("utilization")]
    public double Utilization { get; set; }

    [JsonPropertyName("resets_at")]
    [JsonConverter(typeof(TolerantDateTimeOffsetConverter))]
    public DateTimeOffset ResetsAt { get; set; }
}

/// <summary>
/// Accepts any of the date formats the Anthropic usage API has been seen to use:
/// ISO 8601 string ("2026-04-09T20:00:00Z"), Unix timestamp in seconds (integer or
/// numeric string), and Unix timestamp in milliseconds. Falls back to DateTimeOffset
/// default parser if it's none of the above. Throws on completely unknown shapes so
/// the calling code can log the raw response and surface the issue.
/// </summary>
public class TolerantDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var num = reader.GetInt64();
            return FromUnixNumeric(num);
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }
            // Numeric string (e.g. "1712678400") - try Unix seconds/ms
            if (long.TryParse(s, out var num))
            {
                return FromUnixNumeric(num);
            }
            throw new JsonException($"Unrecognized date string '{s}'");
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return DateTimeOffset.MinValue;
        }

        throw new JsonException($"Unexpected token {reader.TokenType} for DateTimeOffset");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset FromUnixNumeric(long num)
    {
        // Treat 0 or negative as "absent" - otherwise we'd map to 1970-01-01 and the
        // UI would show "reset passed 55 years ago".
        if (num <= 0)
        {
            return DateTimeOffset.MinValue;
        }
        // > 1e12 means milliseconds since epoch, otherwise seconds
        return num > 1_000_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(num)
            : DateTimeOffset.FromUnixTimeSeconds(num);
    }
}
