using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class StatusLineScriptGeneratorTests
{
    [Fact]
    public void Generate_BakesTemplateIntoSingleQuotedString()
    {
        // Act
        var script = StatusLineScriptGenerator.Generate("{project} ({branch}) [{model}] {ctx}% ctx");

        // Assert
        Assert.Contains("$line = '{project} ({branch}) [{model}] {ctx}% ctx'", script);
    }

    [Fact]
    public void Generate_EscapesSingleQuotes_SoTemplateTextCannotRunAsCode()
    {
        // Arrange — a template trying to break out of the single-quoted string.
        var hostile = "'; Remove-Item -Recurse C:\\ #";

        // Act
        var script = StatusLineScriptGenerator.Generate(hostile);

        // Assert — the quote is doubled, so the payload stays inert string data.
        Assert.Contains("$line = '''; Remove-Item -Recurse C:\\ #'", script);
    }

    [Fact]
    public void Generate_ReplacesEveryKnownPlaceholder()
    {
        // Act
        var script = StatusLineScriptGenerator.Generate("x");

        // Assert — the Replace chain must cover the full token list, otherwise
        // a block would render literally in the terminal.
        foreach (var placeholder in StatusLineTemplate.Placeholders)
        {
            Assert.Contains($".Replace('{placeholder.Token}',", script);
        }
    }

    [Fact]
    public void Generate_EmitsColorWiring()
    {
        // The coloring block could be deleted wholesale and every substitution
        // test would still pass — pin its presence and the injected thresholds.
        var script = StatusLineScriptGenerator.Generate("{project} {ctx}");

        Assert.Contains("$esc = [char]27", script);
        Assert.Contains("function Add-Color", script);
        Assert.Contains("function Get-PctColor", script);
        Assert.Contains($"-ge {StatusLineTemplate.PercentCritThreshold}", script);
        Assert.Contains($"-ge {StatusLineTemplate.PercentWarnThreshold}", script);
        foreach (var (token, color) in StatusLineTemplate.BlockColors)
        {
            Assert.Contains($"'{color.Ansi}'", script);
        }
    }

    [Fact]
    public void Generate_StripsControlCharsFromFreeTextValues_BeforeColoring()
    {
        var script = StatusLineScriptGenerator.Generate("{project}");

        // The strip must sit before the Add-Color calls — stripping after
        // coloring would eat the ESC the coloring just inserted.
        var stripIndex = script.IndexOf("$model = $model -replace '[\\x00-\\x1F\\x7F]', ''", StringComparison.Ordinal);
        var colorIndex = script.IndexOf("Add-Color $model", StringComparison.Ordinal);
        Assert.True(stripIndex >= 0);
        Assert.True(colorIndex > stripIndex);
    }

    [Fact]
    public void Generate_FlattensLineBreaks()
    {
        // Act
        var script = StatusLineScriptGenerator.Generate("a\r\nb\nc");

        // Assert
        Assert.Contains("$line = 'a b c'", script);
    }

    [Fact]
    public void Generate_TooLongTemplate_Throws()
    {
        // Arrange
        var tooLong = new string('x', StatusLineTemplate.MaxTemplateLength + 1);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => StatusLineScriptGenerator.Generate(tooLong));
    }

    [Fact]
    public void Generate_StaysPowerShell51Compatible()
    {
        // Assert — the script targets stock Windows PowerShell 5.1; the
        // null-coalescing and ternary operators only exist in PowerShell 7.
        var script = StatusLineScriptGenerator.Generate("{project}");
        Assert.DoesNotContain("??", script);
        Assert.Contains("exit 0", script);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("it's", "it''s")]
    [InlineData("''", "''''")]
    public void EscapeForSingleQuotedPs_DoublesQuotes(string input, string expected)
    {
        Assert.Equal(expected, StatusLineScriptGenerator.EscapeForSingleQuotedPs(input));
    }

    [Theory]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201A')]
    [InlineData('\u201B')]
    public void EscapeForSingleQuotedPs_DoublesSmartQuotes_WhichPowerShellTreatsAsDelimiters(char smartQuote)
    {
        // PowerShell's tokenizer accepts the whole smart-quote family as a
        // single-quoted string delimiter, so an undoubled one would terminate
        // the baked template string and run what follows as code.
        var escaped = StatusLineScriptGenerator.EscapeForSingleQuotedPs($"a{smartQuote}b");

        Assert.Equal($"a{smartQuote}{smartQuote}b", escaped);
    }

    [Fact]
    public void Generate_StripsControlCharacters_IncludingC1Range()
    {
        // A C1 byte in a BOM-less ANSI-decoded file can turn into a smart
        // quote at script-read time; none may survive into the baked string.
        var hostile = "a\u0092b\u0001c\u007Fd";

        var script = StatusLineScriptGenerator.Generate(hostile);

        Assert.Contains("$line = 'abcd'", script);
    }

    [Fact]
    public void Generate_SmartQuoteInTemplate_CannotTerminateBakedString()
    {
        var script = StatusLineScriptGenerator.Generate("don\u2019t");

        Assert.Contains("$line = 'don\u2019\u2019t'", script);
    }
}
