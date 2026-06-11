using ClaudeSessionsSidekick.Models;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Models;

public class AppSettingsTests
{
    // --- ResumeDefaultModel serialization ---
    // Mirrors the QuickLaunchEntry.ModelOverride coverage in
    // ClaudeLauncherServiceTests — same alias semantics, same JSON contract,
    // same need to keep the alias forward-compatible (opus → 4.7 → 4.8 → ...
    // without code changes).

    [Fact]
    public void ResumeDefaultModel_DefaultsToNull()
    {
        Assert.Null(new AppSettings().ResumeDefaultModel);
    }

    [Fact]
    public void ResumeDefaultModel_RoundTripsViaJson()
    {
        var settings = new AppSettings { ResumeDefaultModel = "opus" };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Contains("\"resumeDefaultModel\":\"opus\"", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.Equal("opus", deserialized!.ResumeDefaultModel);
    }

    [Fact]
    public void ResumeDefaultModel_MissingFieldDeserializesToNull()
    {
        // Older settings.json files written before the field existed must still
        // round-trip cleanly — no exception, ResumeDefaultModel just stays null.
        const string legacyJson = """{"preferredShell":"Auto"}""";
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacyJson);
        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.ResumeDefaultModel);
    }

    // --- ResumeEffortLevel serialization ---

    [Fact]
    public void ResumeEffortLevel_DefaultsToNull()
    {
        Assert.Null(new AppSettings().ResumeEffortLevel);
    }

    [Fact]
    public void ResumeEffortLevel_RoundTripsViaJson()
    {
        var settings = new AppSettings { ResumeEffortLevel = "xhigh" };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Contains("\"resumeEffortLevel\":\"xhigh\"", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.Equal("xhigh", deserialized!.ResumeEffortLevel);
    }

    [Fact]
    public void ResumeEffortLevel_MissingFieldDeserializesToNull()
    {
        const string legacyJson = """{"resumeDefaultModel":"opus"}""";
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacyJson);
        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.ResumeEffortLevel);
    }
}
