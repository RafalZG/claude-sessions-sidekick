using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class PermissionWatcherServiceTests
{
    // ── SplitCompoundCommand ───────────────────────────────────────

    public class SplitCompoundCommandTests
    {
        [Fact]
        public void Split_SimpleCommand_ReturnsSinglePart()
        {
            // Act
            var parts = PermissionWatcherService.SplitCompoundCommand("grep -r pattern src/");

            // Assert
            Assert.Single(parts);
            Assert.Equal("grep -r pattern src/", parts[0]);
        }

        [Fact]
        public void Split_AndOperator_ReturnsBothParts()
        {
            // Act
            var parts = PermissionWatcherService.SplitCompoundCommand("cd /path && grep -r pattern");

            // Assert
            Assert.Equal(2, parts.Count);
            Assert.Equal("cd /path", parts[0]);
            Assert.Equal("grep -r pattern", parts[1]);
        }

        [Fact]
        public void Split_OrOperator_ReturnsBothParts()
        {
            // Act
            var parts = PermissionWatcherService.SplitCompoundCommand("test -f file || echo missing");

            // Assert
            Assert.Equal(2, parts.Count);
            Assert.Equal("test -f file", parts[0]);
            Assert.Equal("echo missing", parts[1]);
        }

        [Fact]
        public void Split_Semicolon_ReturnsBothParts()
        {
            // Act
            var parts = PermissionWatcherService.SplitCompoundCommand("cd /path; ls -la");

            // Assert
            Assert.Equal(2, parts.Count);
            Assert.Equal("cd /path", parts[0]);
            Assert.Equal("ls -la", parts[1]);
        }

        [Fact]
        public void Split_ThreeParts_ReturnsAll()
        {
            // Act
            var parts = PermissionWatcherService.SplitCompoundCommand("cd /path && npm install && npm test");

            // Assert
            Assert.Equal(3, parts.Count);
            Assert.Equal("cd /path", parts[0]);
            Assert.Equal("npm install", parts[1]);
            Assert.Equal("npm test", parts[2]);
        }

        [Fact]
        public void Split_EmptyParts_AreSkipped()
        {
            // Act
            var parts = PermissionWatcherService.SplitCompoundCommand("cd /path &&  && grep foo");

            // Assert
            Assert.Equal(2, parts.Count);
            Assert.Equal("cd /path", parts[0]);
            Assert.Equal("grep foo", parts[1]);
        }
    }

    // ── GetSuggestions ─────────────────────────────────────────────

    public class GetSuggestionsTests
    {
        [Fact]
        public void GetSuggestions_CompoundCdAndGrep_SuggestsBothWildcards()
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = "cd /some/path && grep -r pattern src/", Scope = PermissionScope.Allow };

            // Act
            var suggestions = PermissionWatcherService.GetSuggestions(rule);

            // Assert
            Assert.Contains("Bash(cd *)", suggestions);
            Assert.Contains("Bash(grep *)", suggestions);
        }

        [Fact]
        public void GetSuggestions_CompoundCdAndNpm_SuggestsBothWildcards()
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = "cd /project && npm run build", Scope = PermissionScope.Allow };

            // Act
            var suggestions = PermissionWatcherService.GetSuggestions(rule);

            // Assert
            Assert.Contains("Bash(cd *)", suggestions);
            Assert.Contains("Bash(npm *)", suggestions);
        }

        [Fact]
        public void GetSuggestions_SimpleSpecificCommand_SuggestsWildcard()
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = "npm install lodash", Scope = PermissionScope.Allow };

            // Act
            var suggestions = PermissionWatcherService.GetSuggestions(rule);

            // Assert
            Assert.Single(suggestions);
            Assert.Equal("Bash(npm *)", suggestions[0]);
        }

        [Fact]
        public void GetSuggestions_AlreadyWildcarded_ReturnsEmpty()
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Bash", Pattern = "npm *", Scope = PermissionScope.Allow };

            // Act
            var suggestions = PermissionWatcherService.GetSuggestions(rule);

            // Assert
            Assert.Empty(suggestions);
        }

        [Fact]
        public void GetSuggestions_NonBashTool_ReturnsEmpty()
        {
            // Arrange
            var rule = new PermissionRule { Tool = "Read", Pattern = "src/**/*.cs", Scope = PermissionScope.Allow };

            // Act
            var suggestions = PermissionWatcherService.GetSuggestions(rule);

            // Assert
            Assert.Empty(suggestions);
        }

        [Fact]
        public void GetSuggestions_SingleWordCommand_ReturnsEmpty()
        {
            // Arrange - "ls" alone can't be generalized further
            var rule = new PermissionRule { Tool = "Bash", Pattern = "ls", Scope = PermissionScope.Allow };

            // Act
            var suggestions = PermissionWatcherService.GetSuggestions(rule);

            // Assert
            Assert.Empty(suggestions);
        }

        [Fact]
        public void GetSuggestions_CompoundWithAbsolutePathCd_SuggestsOnlyGeneralizable()
        {
            // Arrange - cd with absolute path can still generalize to "cd *"
            var rule = new PermissionRule { Tool = "Bash", Pattern = "cd /c/Users/me/project && dotnet build MyApp.sln", Scope = PermissionScope.Allow };

            // Act
            var suggestions = PermissionWatcherService.GetSuggestions(rule);

            // Assert
            Assert.Contains("Bash(cd *)", suggestions);
            Assert.Contains("Bash(dotnet *)", suggestions);
        }
    }
}
