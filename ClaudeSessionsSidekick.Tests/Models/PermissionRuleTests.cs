using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Tests.Models;

public class PermissionRuleTests
{
    // ── Parse ──────────────────────────────────────────────────────

    public class ParseTests
    {
        [Theory]
        [InlineData("Bash", "Bash", null)]
        [InlineData("Read", "Read", null)]
        [InlineData("Edit", "Edit", null)]
        public void Parse_BareTool_ReturnsMatchAll(string input, string expectedTool, string? expectedPattern)
        {
            // Arrange & Act
            var rule = PermissionRule.Parse(input, PermissionScope.Allow);

            // Assert
            Assert.NotNull(rule);
            Assert.Equal(expectedTool, rule.Tool);
            Assert.Equal(expectedPattern, rule.Pattern);
            Assert.Equal(PermissionScope.Allow, rule.Scope);
        }

        [Theory]
        [InlineData("Bash(npm install *)", "Bash", "npm install *")]
        [InlineData("Read(src/**/*.cs)", "Read", "src/**/*.cs")]
        [InlineData("Bash(git status)", "Bash", "git status")]
        public void Parse_ToolWithPattern_ExtractsBoth(string input, string expectedTool, string expectedPattern)
        {
            // Arrange & Act
            var rule = PermissionRule.Parse(input, PermissionScope.Deny);

            // Assert
            Assert.NotNull(rule);
            Assert.Equal(expectedTool, rule.Tool);
            Assert.Equal(expectedPattern, rule.Pattern);
            Assert.Equal(PermissionScope.Deny, rule.Scope);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Parse_EmptyOrNull_ReturnsNull(string? input)
        {
            // Arrange & Act
            var rule = PermissionRule.Parse(input!, PermissionScope.Allow);

            // Assert
            Assert.Null(rule);
        }

        [Fact]
        public void Parse_MissingCloseParen_ReturnsNull()
        {
            // Arrange & Act
            var rule = PermissionRule.Parse("Bash(npm install", PermissionScope.Allow);

            // Assert
            Assert.Null(rule);
        }

        [Fact]
        public void Parse_EmptyToolName_ReturnsNull()
        {
            // Arrange & Act
            var rule = PermissionRule.Parse("(some pattern)", PermissionScope.Allow);

            // Assert
            Assert.Null(rule);
        }

        [Fact]
        public void Parse_WhitespaceAroundInput_IsTrimmed()
        {
            // Arrange & Act
            var rule = PermissionRule.Parse("  Bash  ", PermissionScope.Allow);

            // Assert
            Assert.NotNull(rule);
            Assert.Equal("Bash", rule.Tool);
        }
    }

    // ── RuleString ─────────────────────────────────────────────────

    public class RuleStringTests
    {
        [Theory]
        [InlineData("Bash", null, "Bash")]
        [InlineData("Bash", "", "Bash")]
        [InlineData("Bash", "npm *", "Bash(npm *)")]
        [InlineData("Read", "src/**/*.cs", "Read(src/**/*.cs)")]
        public void RuleString_RoundTrips(string tool, string? pattern, string expected)
        {
            // Arrange
            var rule = new PermissionRule { Tool = tool, Pattern = pattern };

            // Act & Assert
            Assert.Equal(expected, rule.RuleString);
        }

        [Fact]
        public void RuleString_ParseRoundTrip()
        {
            // Arrange
            var original = "Bash(dotnet build *)";

            // Act
            var rule = PermissionRule.Parse(original, PermissionScope.Allow);
            var roundTripped = rule!.RuleString;

            // Assert
            Assert.Equal(original, roundTripped);
        }
    }

    // ── SuggestGeneralizedPattern ──────────────────────────────────

    public class SuggestGeneralizedPatternTests
    {
        [Theory]
        [InlineData("npm install foo", "npm *")]
        [InlineData("dotnet build ExergyERP.sln", "dotnet *")]
        [InlineData("git commit -m test", "git *")]
        public void SuggestGeneralized_SimpleCommandWithArgs_ReturnsWildcard(string pattern, string expected)
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = pattern };

            // Act
            var result = rule.SuggestGeneralizedPattern();

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("npm *")]
        [InlineData("git *")]
        [InlineData("*")]
        public void SuggestGeneralized_AlreadyWildcarded_ReturnsNull(string pattern)
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = pattern };

            // Act
            var result = rule.SuggestGeneralizedPattern();

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void SuggestGeneralized_NonBashTool_ReturnsNull()
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Read", Pattern = "src/**/*.cs" };

            // Act
            var result = rule.SuggestGeneralizedPattern();

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void SuggestGeneralized_NullPattern_ReturnsNull()
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = null };

            // Act & Assert
            Assert.Null(rule.SuggestGeneralizedPattern());
        }

        [Fact]
        public void SuggestGeneralized_SingleWord_ReturnsNull()
        {
            // Arrange - no space = no args = nothing to generalize
            var rule = new PermissionRule { Tool = "Bash", Pattern = "ls" };

            // Act & Assert
            Assert.Null(rule.SuggestGeneralizedPattern());
        }

        [Theory]
        [InlineData("\"C:\\Program Files\\node.exe\" install")]
        [InlineData("'some-tool' args")]
        [InlineData("/usr/bin/node script.js")]
        [InlineData("\\\\server\\share\\tool.exe args")]
        public void SuggestGeneralized_PathsAndQuotes_ReturnsNull(string pattern)
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = pattern };

            // Act & Assert
            Assert.Null(rule.SuggestGeneralizedPattern());
        }

        [Theory]
        [InlineData("C:\\Windows\\cmd.exe /c dir")]
        [InlineData("KEY=value command")]
        public void SuggestGeneralized_ShellMetacharsInFirstToken_ReturnsNull(string pattern)
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = pattern };

            // Act & Assert
            Assert.Null(rule.SuggestGeneralizedPattern());
        }

        [Theory]
        [InlineData("cmd1 | cmd2", "cmd1 *")]
        [InlineData("cmd1 & cmd2", "cmd1 *")]
        [InlineData("cmd1 ; cmd2", "cmd1 *")]
        public void SuggestGeneralized_MetacharsInArgs_StillGeneralizes(string pattern, string expected)
        {
            // Arrange - metachars after the first token are in the args, not the command.
            // Generalizing "cmd1 | cmd2" to "cmd1 *" is valid: it widens the allowed args.
            var rule = new PermissionRule { Tool = "Bash", Pattern = pattern };

            // Act & Assert
            Assert.Equal(expected, rule.SuggestGeneralizedPattern());
        }
    }

    // ── IsCoveredBy ────────────────────────────────────────────────

    public class IsCoveredByTests
    {
        [Fact]
        public void IsCoveredBy_BareToolCoversSpecific()
        {
            // Arrange
            var broad = new PermissionRule { Tool = "Bash", Pattern = null, Scope = PermissionScope.Allow };
            var narrow = new PermissionRule { Tool = "Bash", Pattern = "npm install", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.True(narrow.IsCoveredBy(broad));
        }

        [Fact]
        public void IsCoveredBy_StarPatternCoversSpecific()
        {
            // Arrange
            var broad = new PermissionRule { Tool = "Bash", Pattern = "*", Scope = PermissionScope.Allow };
            var narrow = new PermissionRule { Tool = "Bash", Pattern = "npm install", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.True(narrow.IsCoveredBy(broad));
        }

        [Fact]
        public void IsCoveredBy_PrefixWildcardCoversSubcommand()
        {
            // Arrange
            var broad = new PermissionRule { Tool = "Bash", Pattern = "npm *", Scope = PermissionScope.Allow };
            var narrow = new PermissionRule { Tool = "Bash", Pattern = "npm install foo", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.True(narrow.IsCoveredBy(broad));
        }

        [Fact]
        public void IsCoveredBy_PrefixWildcardCoversExactCommand()
        {
            // Arrange - "npm *" covers "npm" (the command itself)
            var broad = new PermissionRule { Tool = "Bash", Pattern = "npm *", Scope = PermissionScope.Allow };
            var narrow = new PermissionRule { Tool = "Bash", Pattern = "npm", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.True(narrow.IsCoveredBy(broad));
        }

        [Fact]
        public void IsCoveredBy_DifferentTool_NotCovered()
        {
            // Arrange
            var broad = new PermissionRule { Tool = "Read", Pattern = null, Scope = PermissionScope.Allow };
            var narrow = new PermissionRule { Tool = "Bash", Pattern = "npm install", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.False(narrow.IsCoveredBy(broad));
        }

        [Fact]
        public void IsCoveredBy_DifferentScope_NotCovered()
        {
            // Arrange
            var broad = new PermissionRule { Tool = "Bash", Pattern = null, Scope = PermissionScope.Allow };
            var narrow = new PermissionRule { Tool = "Bash", Pattern = "npm install", Scope = PermissionScope.Deny };

            // Act & Assert
            Assert.False(narrow.IsCoveredBy(broad));
        }

        [Fact]
        public void IsCoveredBy_SameRule_NotCovered()
        {
            // Arrange - rule can't cover itself
            var rule = new PermissionRule { Tool = "Bash", Pattern = "npm *", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.False(rule.IsCoveredBy(rule));
        }

        [Fact]
        public void IsCoveredBy_IdenticalPatterns_NotCovered()
        {
            // Arrange - duplicates, not redundancy
            var a = new PermissionRule { Tool = "Bash", Pattern = "npm install", Scope = PermissionScope.Allow };
            var b = new PermissionRule { Tool = "Bash", Pattern = "npm install", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.False(a.IsCoveredBy(b));
        }

        [Fact]
        public void IsCoveredBy_BareToolDoesNotCoverItself()
        {
            // Arrange
            var a = new PermissionRule { Tool = "Bash", Pattern = null, Scope = PermissionScope.Allow };
            var b = new PermissionRule { Tool = "Bash", Pattern = null, Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.False(a.IsCoveredBy(b));
        }

        [Fact]
        public void IsCoveredBy_UnrelatedPrefixes_NotCovered()
        {
            // Arrange - "npm *" does NOT cover "node something"
            var broad = new PermissionRule { Tool = "Bash", Pattern = "npm *", Scope = PermissionScope.Allow };
            var narrow = new PermissionRule { Tool = "Bash", Pattern = "node script.js", Scope = PermissionScope.Allow };

            // Act & Assert
            Assert.False(narrow.IsCoveredBy(broad));
        }
    }
}
