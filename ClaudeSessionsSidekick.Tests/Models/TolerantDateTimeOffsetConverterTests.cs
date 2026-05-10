using System.Text.Json;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Tests.Models;

/// <summary>
/// Tests for the TolerantDateTimeOffsetConverter that caused the production crash
/// on 2026-04-09 when Anthropic changed the resets_at format from ISO string to
/// Unix timestamp. These tests ensure we never regress on format tolerance.
/// </summary>
public class TolerantDateTimeOffsetConverterTests
{
    private static readonly JsonSerializerOptions Options = new();

    private static DateTimeOffset Deserialize(string json)
    {
        return JsonSerializer.Deserialize<Wrapper>(json, Options)!.Value;
    }

    private record Wrapper(
        [property: System.Text.Json.Serialization.JsonConverter(typeof(TolerantDateTimeOffsetConverter))]
        DateTimeOffset Value
    );

    // ── ISO 8601 strings ───────────────────────────────────────────

    [Theory]
    [InlineData("{\"Value\":\"2026-04-09T20:00:00Z\"}", 2026, 4, 9, 20, 0)]
    [InlineData("{\"Value\":\"2026-04-11T10:30:00+02:00\"}", 2026, 4, 11, 10, 30)]
    [InlineData("{\"Value\":\"2026-01-01T00:00:00Z\"}", 2026, 1, 1, 0, 0)]
    public void Read_IsoString_ParsesCorrectly(string json, int y, int m, int d, int h, int min)
    {
        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(y, result.Year);
        Assert.Equal(m, result.Month);
        Assert.Equal(d, result.Day);
        Assert.Equal(h, result.Hour);
        Assert.Equal(min, result.Minute);
    }

    // ── Unix timestamps (seconds) ──────────────────────────────────

    [Fact]
    public void Read_UnixSeconds_ParsesCorrectly()
    {
        // Arrange - 1712678400 = 2024-04-09T20:00:00Z
        var json = "{\"Value\":1712678400}";

        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(2024, result.Year);
        Assert.Equal(4, result.Month);
        Assert.Equal(9, result.Day);
    }

    // ── Unix timestamps (milliseconds) ─────────────────────────────

    [Fact]
    public void Read_UnixMilliseconds_ParsesCorrectly()
    {
        // Arrange - 1712678400000 = same date in ms
        var json = "{\"Value\":1712678400000}";

        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(2024, result.Year);
        Assert.Equal(4, result.Month);
        Assert.Equal(9, result.Day);
    }

    [Fact]
    public void Read_DistinguishesSecondsFromMilliseconds()
    {
        // Arrange
        var secJson = "{\"Value\":1712678400}";
        var msJson = "{\"Value\":1712678400000}";

        // Act
        var fromSec = Deserialize(secJson);
        var fromMs = Deserialize(msJson);

        // Assert - should produce the same datetime
        Assert.Equal(fromSec.Year, fromMs.Year);
        Assert.Equal(fromSec.Month, fromMs.Month);
        Assert.Equal(fromSec.Day, fromMs.Day);
        Assert.Equal(fromSec.Hour, fromMs.Hour);
    }

    // ── Numeric strings ────────────────────────────────────────────

    [Fact]
    public void Read_NumericString_ParsesAsUnixSeconds()
    {
        // Arrange
        var json = "{\"Value\":\"1712678400\"}";

        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(2024, result.Year);
    }

    [Fact]
    public void Read_NumericStringMilliseconds_ParsesCorrectly()
    {
        // Arrange
        var json = "{\"Value\":\"1712678400000\"}";

        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(2024, result.Year);
    }

    // ── Null / zero / negative → MinValue sentinel ─────────────────

    [Fact]
    public void Read_Null_ReturnsMinValue()
    {
        // Arrange
        var json = "{\"Value\":null}";

        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(DateTimeOffset.MinValue, result);
    }

    [Fact]
    public void Read_Zero_ReturnsMinValue()
    {
        // Arrange
        var json = "{\"Value\":0}";

        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(DateTimeOffset.MinValue, result);
    }

    [Fact]
    public void Read_Negative_ReturnsMinValue()
    {
        // Arrange
        var json = "{\"Value\":-1}";

        // Act
        var result = Deserialize(json);

        // Assert
        Assert.Equal(DateTimeOffset.MinValue, result);
    }

    // ── Error cases ────────────────────────────────────────────────

    [Fact]
    public void Read_GarbageString_Throws()
    {
        // Arrange
        var json = "{\"Value\":\"not-a-date\"}";

        // Act & Assert
        Assert.Throws<JsonException>(() => Deserialize(json));
    }

    [Fact]
    public void Read_BooleanToken_Throws()
    {
        // Arrange
        var json = "{\"Value\":true}";

        // Act & Assert
        Assert.Throws<JsonException>(() => Deserialize(json));
    }

    // ── Write (round-trip) ─────────────────────────────────────────

    [Fact]
    public void Write_ProducesIso8601()
    {
        // Arrange
        var dto = new DateTimeOffset(2026, 4, 9, 20, 0, 0, TimeSpan.Zero);
        var wrapper = new Wrapper(dto);

        // Act
        var json = JsonSerializer.Serialize(wrapper, Options);

        // Assert
        Assert.Contains("2026-04-09T20:00:00", json);
    }

    // ── Full UsageData deserialization ──────────────────────────────

    [Fact]
    public void UsageData_WithIsoResetTime_Deserializes()
    {
        // Arrange
        var json = """
        {
            "five_hour": { "utilization": 0.15, "resets_at": "2026-04-11T15:00:00Z" },
            "seven_day": { "utilization": 0.42, "resets_at": "2026-04-14T00:00:00Z" }
        }
        """;

        // Act
        var data = JsonSerializer.Deserialize<UsageData>(json);

        // Assert
        Assert.NotNull(data?.FiveHour);
        Assert.Equal(0.15, data.FiveHour.Utilization);
        Assert.Equal(2026, data.FiveHour.ResetsAt.Year);
        Assert.NotNull(data.SevenDay);
        Assert.Equal(0.42, data.SevenDay.Utilization);
    }

    [Fact]
    public void UsageData_WithUnixTimestamp_Deserializes()
    {
        // Arrange - the format that caused the 2026-04-09 production crash
        var json = """
        {
            "five_hour": { "utilization": 0.0, "resets_at": 1712678400 },
            "seven_day": { "utilization": 0.15, "resets_at": 1713139200 }
        }
        """;

        // Act
        var data = JsonSerializer.Deserialize<UsageData>(json);

        // Assert
        Assert.NotNull(data?.FiveHour);
        Assert.NotEqual(DateTimeOffset.MinValue, data.FiveHour.ResetsAt);
        Assert.NotNull(data.SevenDay);
    }

    [Fact]
    public void UsageData_WithNullResetTime_UsesMinValueSentinel()
    {
        // Arrange
        var json = """
        {
            "five_hour": { "utilization": 0.0, "resets_at": null }
        }
        """;

        // Act
        var data = JsonSerializer.Deserialize<UsageData>(json);

        // Assert
        Assert.NotNull(data?.FiveHour);
        Assert.Equal(DateTimeOffset.MinValue, data.FiveHour.ResetsAt);
    }

    [Fact]
    public void UsageData_WithMissingWindows_LeavesNull()
    {
        // Arrange
        var json = """
        {
            "five_hour": { "utilization": 0.5, "resets_at": "2026-04-11T15:00:00Z" }
        }
        """;

        // Act
        var data = JsonSerializer.Deserialize<UsageData>(json);

        // Assert
        Assert.NotNull(data?.FiveHour);
        Assert.Null(data.SevenDay);
        Assert.Null(data.SevenDayOpus);
    }
}
