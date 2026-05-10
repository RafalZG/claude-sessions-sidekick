using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class PermissionServiceTests
{
    // ── Presets validation ──────────────────────────────────────────

    [Fact]
    public void Presets_AllPresetsHaveNameAndDescription()
    {
        // Assert
        foreach (var preset in PermissionService.Presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name), "Preset has empty name");
            Assert.False(string.IsNullOrWhiteSpace(preset.Description), "Preset has empty description");
        }
    }

    [Fact]
    public void Presets_AllRuleStringsParse()
    {
        // Arrange & Act & Assert
        foreach (var preset in PermissionService.Presets)
        {
            foreach (var ruleStr in preset.AllowRules)
            {
                var rule = PermissionRule.Parse(ruleStr, PermissionScope.Allow);
                Assert.NotNull(rule);
            }

            foreach (var ruleStr in preset.DenyRules)
            {
                var rule = PermissionRule.Parse(ruleStr, PermissionScope.Deny);
                Assert.NotNull(rule);
            }
        }
    }

    [Fact]
    public void Presets_AllRulesRoundTrip()
    {
        // Assert - every rule string should survive Parse → RuleString round-trip
        foreach (var preset in PermissionService.Presets)
        {
            foreach (var original in preset.AllowRules)
            {
                var rule = PermissionRule.Parse(original, PermissionScope.Allow)!;
                Assert.Equal(original, rule.RuleString);
            }

            foreach (var original in preset.DenyRules)
            {
                var rule = PermissionRule.Parse(original, PermissionScope.Deny)!;
                Assert.Equal(original, rule.RuleString);
            }
        }
    }

    [Fact]
    public void Presets_HaveUniqueNames()
    {
        // Arrange
        var names = PermissionService.Presets.Select(p => p.Name).ToList();

        // Assert
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Presets_EachHasAtLeastOneRule()
    {
        // Assert
        foreach (var preset in PermissionService.Presets)
        {
            var totalRules = preset.AllowRules.Length + preset.DenyRules.Length;
            Assert.True(totalRules > 0, $"Preset '{preset.Name}' has no rules");
        }
    }

    [Fact]
    public void Presets_BlockDestructiveGit_HasOnlyDenyRules()
    {
        // Arrange
        var preset = PermissionService.Presets.First(p => p.Name == "Block Destructive Git");

        // Assert
        Assert.Empty(preset.AllowRules);
        Assert.NotEmpty(preset.DenyRules);
        Assert.All(preset.DenyRules, r => Assert.StartsWith("Bash(git", r));
    }

    [Fact]
    public void Presets_BlockSecretFileReads_HasOnlyDenyRules()
    {
        // Arrange
        var preset = PermissionService.Presets.First(p => p.Name == "Block Secret File Reads");

        // Assert
        Assert.Empty(preset.AllowRules);
        Assert.NotEmpty(preset.DenyRules);
        Assert.All(preset.DenyRules, r => Assert.StartsWith("Read(", r));
    }

    // ── PermissionFile with temp files ─────────────────────────────

    public class PermissionFileTests : IDisposable
    {
        private readonly string _tempDir;

        public PermissionFileTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"ClaudeWidgetTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void Load_NonExistentFile_CreatesEmptyRules()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "missing.json");

            // Act
            var pf = PermissionFile.Load(path, "Test");

            // Assert
            Assert.Empty(pf.Rules);
            Assert.Empty(pf.AdditionalDirectories);
        }

        [Fact]
        public void Load_EmptyFile_CreatesEmptyRules()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "empty.json");
            File.WriteAllText(path, "");

            // Act
            var pf = PermissionFile.Load(path, "Test");

            // Assert
            Assert.Empty(pf.Rules);
        }

        [Fact]
        public void Load_ParsesAllowDenyAskRules()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "settings.json");
            File.WriteAllText(path, """
            {
                "permissions": {
                    "allow": ["Read", "Bash(npm *)"],
                    "deny": ["Bash(rm -rf *)"],
                    "ask": ["Write"]
                }
            }
            """);

            // Act
            var pf = PermissionFile.Load(path, "Test");

            // Assert
            Assert.Equal(4, pf.Rules.Count);
            Assert.Equal(2, pf.Rules.Count(r => r.Scope == PermissionScope.Allow));
            Assert.Single(pf.Rules, r => r.Scope == PermissionScope.Deny);
            Assert.Single(pf.Rules, r => r.Scope == PermissionScope.Ask);
        }

        [Fact]
        public void Load_ParsesAdditionalDirectories()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "settings.json");
            File.WriteAllText(path, """
            {
                "permissions": {
                    "allow": [],
                    "additionalDirectories": ["C:\\Projects\\Foo", "D:\\Other"]
                }
            }
            """);

            // Act
            var pf = PermissionFile.Load(path, "Test");

            // Assert
            Assert.Equal(2, pf.AdditionalDirectories.Count);
            Assert.Contains("C:\\Projects\\Foo", pf.AdditionalDirectories);
        }

        [Fact]
        public void Save_RoundTrips()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "settings.json");
            File.WriteAllText(path, """{"permissions": {"allow": ["Read", "Bash(npm *)"]}}""");
            var pf = PermissionFile.Load(path, "Test");

            // Act - add a rule and save
            pf.Rules.Add(new PermissionRule { Tool = "Edit", Pattern = null, Scope = PermissionScope.Allow });
            pf.Save();

            // Assert - reload should have 3 allow rules
            var reloaded = PermissionFile.Load(path, "Test2");
            Assert.Equal(3, reloaded.Rules.Count);
            Assert.All(reloaded.Rules, r => Assert.Equal(PermissionScope.Allow, r.Scope));
        }

        [Fact]
        public void Save_PreservesNonPermissionKeys()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "settings.json");
            File.WriteAllText(path, """
            {
                "model": "claude-opus-4-6",
                "permissions": { "allow": ["Read"] },
                "env": { "FOO": "bar" }
            }
            """);
            var pf = PermissionFile.Load(path, "Test");

            // Act
            pf.Save();

            // Assert
            var raw = File.ReadAllText(path);
            Assert.Contains("\"model\"", raw);
            Assert.Contains("claude-opus-4-6", raw);
            Assert.Contains("\"env\"", raw);
            Assert.Contains("FOO", raw);
        }

        [Fact]
        public void Save_CreatesBakFile()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "settings.json");
            File.WriteAllText(path, """{"permissions": {"allow": ["Read"]}}""");
            var pf = PermissionFile.Load(path, "Test");

            // Act
            pf.Save();

            // Assert
            Assert.True(File.Exists(path + ".bak"));
        }

        [Fact]
        public void Save_OmitsEmptyAdditionalDirectories()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "settings.json");
            File.WriteAllText(path, """
            {
                "permissions": {
                    "allow": ["Read"],
                    "additionalDirectories": ["C:\\Old"]
                }
            }
            """);
            var pf = PermissionFile.Load(path, "Test");
            pf.AdditionalDirectories.Clear();

            // Act
            pf.Save();

            // Assert
            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("additionalDirectories", raw);
        }
    }
}
