using ClaudeSessionsSidekick.Models;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Models;

public class AppSettingsTests
{
    // --- Legacy "resumeDefaultModel" field is silently dropped on load ---
    // The setting shipped briefly in the [Unreleased] line but was removed
    // before 1.0.1 went out — it forced an alias on every resume and ended
    // up downgrading users' intentional Sonnet/Haiku sessions when they
    // picked Opus. Keeping a regression test here so a future read of a
    // stale settings.json doesn't blow up deserialization.

    [Fact]
    public void LegacyResumeDefaultModel_DeserializesWithoutError()
    {
        const string legacyJson = """{"resumeDefaultModel":"opus","preferredShell":"Auto"}""";
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacyJson);
        Assert.NotNull(deserialized);
        Assert.Equal(ShellType.Auto, deserialized!.PreferredShell);
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
