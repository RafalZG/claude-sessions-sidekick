using System;
using System.IO;
using System.Linq;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class MemoryAuditServiceTests
{
    // ── EstimateTokens ──────────────────────────────────────────────
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]      // ceiling
    [InlineData(4000, 1000)]
    [InlineData(-10, 0)]    // guard negatives
    public void EstimateTokens_RoughBytesOverFour(long bytes, int expected)
    {
        Assert.Equal(expected, MemoryAuditService.EstimateTokens(bytes));
    }

    // ── ShouldSuggest ───────────────────────────────────────────────
    private static MemoryAuditResult AuditOf(int totalTokens) => new()
    {
        Files = [new MemoryAuditFile { Path = "x", DisplayName = "x", EstimatedTokens = totalTokens }],
    };

    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldSuggest_Disabled_False()
    {
        var s = new AppSettings { EnableMemoryReviewSuggestions = false, MemoryReviewThresholdTokens = 1000 };
        Assert.False(MemoryAuditService.ShouldSuggest(s, AuditOf(50_000), Now));
    }

    [Fact]
    public void ShouldSuggest_BelowThreshold_False()
    {
        var s = new AppSettings { MemoryReviewThresholdTokens = 25_000, LastMemoryReviewSuggestionUtc = null };
        Assert.False(MemoryAuditService.ShouldSuggest(s, AuditOf(10_000), Now));
    }

    [Fact]
    public void ShouldSuggest_WithinInterval_False()
    {
        var s = new AppSettings
        {
            MemoryReviewThresholdTokens = 1000,
            MemoryReviewIntervalDays = 7,
            LastMemoryReviewSuggestionUtc = Now.AddDays(-3),
        };
        Assert.False(MemoryAuditService.ShouldSuggest(s, AuditOf(50_000), Now));
    }

    [Fact]
    public void ShouldSuggest_IntervalElapsedAndOverThreshold_True()
    {
        var s = new AppSettings
        {
            MemoryReviewThresholdTokens = 25_000,
            MemoryReviewIntervalDays = 7,
            LastMemoryReviewSuggestionUtc = Now.AddDays(-8),
        };
        Assert.True(MemoryAuditService.ShouldSuggest(s, AuditOf(30_000), Now));
    }

    [Fact]
    public void ShouldSuggest_NeverSuggestedAndOverThreshold_True()
    {
        var s = new AppSettings { MemoryReviewThresholdTokens = 25_000, LastMemoryReviewSuggestionUtc = null };
        Assert.True(MemoryAuditService.ShouldSuggest(s, AuditOf(25_000), Now));
    }

    // ── BuildReviewPrompt ───────────────────────────────────────────
    [Fact]
    public void BuildReviewPrompt_IncludesStepsAndLargestFiles()
    {
        var audit = new MemoryAuditResult
        {
            Files =
            [
                new MemoryAuditFile { Path = @"C:\a\AGENTS.md", DisplayName = "AGENTS.md", EstimatedTokens = 9000 },
                new MemoryAuditFile { Path = @"C:\a\MEMORY.md", DisplayName = "MEMORY.md", EstimatedTokens = 6300 },
            ],
        };

        var prompt = MemoryAuditService.BuildReviewPrompt(audit);

        Assert.Contains("Consolidate", prompt);
        Assert.Contains("Keep it lean", prompt);
        Assert.Contains(@"C:\a\AGENTS.md", prompt);          // largest first
        Assert.Contains("15.3k tokens", prompt);              // 9000 + 6300 total
    }

    [Fact]
    public void BuildReviewPrompt_NoFiles_StillHasInstructions()
    {
        var prompt = MemoryAuditService.BuildReviewPrompt(new MemoryAuditResult());
        Assert.Contains("Review and consolidate", prompt);
        Assert.DoesNotContain("Largest:", prompt);
    }

    // ── BuildAudit (project files) ──────────────────────────────────
    [Fact]
    public void BuildAudit_PicksUpProjectClaudeMdAndRules()
    {
        using var temp = new TempDir();
        var projectDir = temp.Path;
        File.WriteAllText(Path.Combine(projectDir, "CLAUDE.md"), new string('x', 400)); // ~100 tokens
        var rulesDir = Path.Combine(projectDir, ".claude", "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "style.md"), new string('y', 800));    // ~200 tokens

        var entry = new QuickLaunchEntry { Name = "Proj", FolderPath = projectDir };
        var audit = MemoryAuditService.BuildAudit([entry]);

        var claude = audit.Files.SingleOrDefault(f => f.Path == Path.Combine(projectDir, "CLAUDE.md"));
        var rule = audit.Files.SingleOrDefault(f => f.Path == Path.Combine(rulesDir, "style.md"));

        Assert.NotNull(claude);
        Assert.Equal(MemoryFileCategory.ProjectInstructions, claude!.Category);
        Assert.Equal(100, claude.EstimatedTokens);

        Assert.NotNull(rule);
        Assert.Equal(MemoryFileCategory.Rules, rule!.Category);
        Assert.Equal(200, rule.EstimatedTokens);
    }

    [Fact]
    public void BuildAudit_MissingProjectFolder_Skipped()
    {
        var entry = new QuickLaunchEntry { Name = "Gone", FolderPath = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid()) };
        // Should not throw; result simply won't contain that project's files.
        var audit = MemoryAuditService.BuildAudit([entry]);
        Assert.DoesNotContain(audit.Files, f => f.Path.Contains("no-such-"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sidekick-audit-test-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
