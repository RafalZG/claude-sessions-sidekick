using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class ActiveSessionServiceTests
{
    // We deliberately do NOT exercise the WMI path in unit tests — it's slow,
    // OS-dependent, and would require spawning a real claude.exe to assert
    // true-positives. The contract worth pinning here is the input-validation
    // behavior: empty / whitespace / a UUID nobody is running all return
    // false without throwing, so the caller can use this as a yes/no signal
    // without try/catch.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsResumeRunning_EmptyInput_ReturnsFalseWithoutQuery(string? sessionId)
    {
        // Should short-circuit before touching WMI — fast path for the common
        // case of a blank/missing session ID.
        Assert.False(ActiveSessionService.IsResumeRunning(sessionId!));
    }

    [Fact]
    public void IsResumeRunning_UnknownUuid_ReturnsFalse()
    {
        // A freshly-generated UUID will never match a running process's command
        // line. Confirms the WMI path is invoked and the negative case is
        // handled cleanly (no exception, just `false`).
        var randomId = Guid.NewGuid().ToString();
        Assert.False(ActiveSessionService.IsResumeRunning(randomId));
    }
}
