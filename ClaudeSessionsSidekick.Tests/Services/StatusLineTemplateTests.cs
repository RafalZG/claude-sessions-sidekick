using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class StatusLineTemplateTests
{
    private static Dictionary<string, string> FullValues()
    {
        var values = new Dictionary<string, string>
        {
            ["{project}"] = "ExergyERP_Dev",
            ["{branch}"] = "master",
            ["{worktree}"] = "",
            ["{model}"] = "Fable 5",
            ["{ctx}"] = "9",
            ["{cost}"] = "$0.48",
            ["{lines}"] = "+156/-23",
            ["{version}"] = "2.1.90",
            ["{effort}"] = "high",
            ["{5h}"] = "24",
            ["{7d}"] = "41",
        };
        return values;
    }

    [Fact]
    public void Render_DefaultTemplate_OutsideWorktree_ProducesRafalsLine()
    {
        // Arrange — empty {worktree} must vanish without leaving a double space.
        var values = FullValues();

        // Act
        var result = StatusLineTemplate.Render(StatusLineTemplate.DefaultTemplate, values);

        // Assert
        Assert.Equal("ExergyERP_Dev (master) [Fable 5] 9% ctx", result);
    }

    [Fact]
    public void Render_DefaultTemplate_InsideWorktree_ShowsWorktreeName()
    {
        // Arrange
        var values = FullValues();
        values["{worktree}"] = "wt-c3600";

        // Act
        var result = StatusLineTemplate.Render(StatusLineTemplate.DefaultTemplate, values);

        // Assert
        Assert.Equal("ExergyERP_Dev wt-c3600 (master) [Fable 5] 9% ctx", result);
    }

    [Theory]
    [InlineData(0, "32")]
    [InlineData(49, "32")]
    [InlineData(50, "33")]
    [InlineData(79, "33")]
    [InlineData(80, "31")]
    [InlineData(100, "31")]
    public void PercentColor_BoundariesAtWarnAndCritThresholds(double value, string expectedAnsi)
    {
        var color = StatusLineTemplate.PercentColor(value);

        Assert.Equal(expectedAnsi, color.Ansi);
        Assert.False(string.IsNullOrEmpty(color.Hex));
    }

    [Fact]
    public void Render_MissingValue_DropsEmptyBracketPairs()
    {
        // Arrange — a session outside any git repo has no branch; the "( )"
        // shell around it must not survive as empty punctuation.
        var values = FullValues();
        values["{branch}"] = "";

        // Act
        var result = StatusLineTemplate.Render(StatusLineTemplate.DefaultTemplate, values);

        // Assert
        Assert.Equal("ExergyERP_Dev [Fable 5] 9% ctx", result);
    }

    [Fact]
    public void Render_ValueAbsentFromDictionary_TreatedAsEmpty()
    {
        // Arrange
        var values = new Dictionary<string, string>();

        // Act
        var result = StatusLineTemplate.Render("{model} {ctx}%", values);

        // Assert — "%" is literal text and stays; placeholders vanish.
        Assert.Equal("%", result);
    }

    [Fact]
    public void Render_UnknownToken_StaysVisible()
    {
        // Arrange — a typo like {brnach} should show up in the preview
        // instead of silently disappearing.
        var values = FullValues();

        // Act
        var result = StatusLineTemplate.Render("{project} {brnach}", values);

        // Assert
        Assert.Equal("ExergyERP_Dev {brnach}", result);
    }

    [Fact]
    public void Render_CollapsesDoubledSpacesAndTrims()
    {
        // Arrange
        var values = FullValues();
        values["{cost}"] = "";

        // Act
        var result = StatusLineTemplate.Render("  {project}  {cost}  {model} ", values);

        // Assert
        Assert.Equal("ExergyERP_Dev Fable 5", result);
    }

    [Fact]
    public void Render_EmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal("", StatusLineTemplate.Render("", FullValues()));
    }

    [Fact]
    public void SampleValues_CoverEveryPlaceholder()
    {
        // Arrange
        var samples = StatusLineTemplate.SampleValues();

        // Assert — every block button must render to something in the preview.
        foreach (var placeholder in StatusLineTemplate.Placeholders)
        {
            Assert.True(samples.TryGetValue(placeholder.Token, out var value));
            Assert.False(string.IsNullOrEmpty(value));
        }
    }

    [Fact]
    public void Presets_AllRenderNonEmptyWithSampleData()
    {
        // Arrange
        var samples = StatusLineTemplate.SampleValues();

        foreach (var preset in StatusLineTemplate.Presets)
        {
            // Act
            var rendered = StatusLineTemplate.Render(preset.Template, samples);

            // Assert
            Assert.False(string.IsNullOrWhiteSpace(rendered));
            Assert.DoesNotContain("{", rendered);
        }
    }
}
