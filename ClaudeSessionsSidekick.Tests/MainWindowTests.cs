using System.Text.Json;
using ClaudeSessionsSidekick;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Tests;

public class MainWindowTests
{
    [Fact]
    public void ShouldShowSessionSection_NoDataYet_StaysVisible()
    {
        // Act
        var visible = MainWindow.ShouldShowSessionSection(null);

        // Assert — before the first fetch the section carries error messages.
        Assert.True(visible);
    }

    [Fact]
    public void ShouldShowSessionSection_PlanWithFiveHourWindow_IsVisible()
    {
        // Arrange
        var data = new UsageData
        {
            FiveHour = new UsageWindow { Utilization = 42 },
            SevenDay = new UsageWindow { Utilization = 57 }
        };

        // Act
        var visible = MainWindow.ShouldShowSessionSection(data);

        // Assert
        Assert.True(visible);
    }

    [Fact]
    public void ShouldShowSessionSection_UltraPlanWithoutFiveHourWindow_IsHidden()
    {
        // Arrange — Ultra (formerly Max 20x) lost the 5-hour limit in late 2026
        // and the usage API stops returning the window.
        var data = new UsageData
        {
            SevenDay = new UsageWindow { Utilization = 57 }
        };

        // Act
        var visible = MainWindow.ShouldShowSessionSection(data);

        // Assert
        Assert.False(visible);
    }

    [Fact]
    public void UsageData_ResponseWithoutFiveHour_DeserializesWithNullWindow()
    {
        // Arrange — the exact shape an Ultra account gets back.
        var json = """
            { "seven_day": { "utilization": 57.0, "resets_at": "2026-11-05T09:00:00Z" } }
            """;

        // Act
        var data = JsonSerializer.Deserialize<UsageData>(json);

        // Assert
        Assert.NotNull(data);
        Assert.Null(data!.FiveHour);
        Assert.NotNull(data.SevenDay);
        Assert.Equal(57.0, data.SevenDay!.Utilization);
    }
}
