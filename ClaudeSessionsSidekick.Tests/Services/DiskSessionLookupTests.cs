using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class DiskSessionLookupTests
{
    [Fact]
    public void ExtractNames_FindsSlugAndCustomTitle()
    {
        // Arrange
        var text = """
            {"type":"user","slug":"keen-mixing-origami","message":"hello"}
            {"type":"custom-title","title":"My renamed session"}
            {"type":"assistant","slug":"keen-mixing-origami"}
            """;

        // Act
        var names = DiskSessionLookup.ExtractNames(text);

        // Assert
        Assert.Equal("keen-mixing-origami", names.Slug);
        Assert.Equal("My renamed session", names.CustomTitle);
    }

    [Fact]
    public void ExtractNames_LastValueWins_AfterRename()
    {
        // Arrange — the session was renamed mid-way; the newest records carry
        // the current values.
        var text = """
            {"type":"custom-title","title":"Old name"}
            {"slug":"old-slug"}
            {"slug":"new-slug"}
            {"type":"custom-title","title":"New name"}
            """;

        // Act
        var names = DiskSessionLookup.ExtractNames(text);

        // Assert
        Assert.Equal("new-slug", names.Slug);
        Assert.Equal("New name", names.CustomTitle);
    }

    [Fact]
    public void ExtractNames_NothingThere_ReturnsNulls()
    {
        // Arrange
        var text = @"{""type"":""user"",""message"":""hi""}";

        // Act
        var names = DiskSessionLookup.ExtractNames(text);

        // Assert
        Assert.Null(names.Slug);
        Assert.Null(names.CustomTitle);
    }

    // ── ExtractPrompts — a prompt as the label of last resort ──────

    [Fact]
    public void ExtractPrompts_LatestMeaningfulPromptWins()
    {
        // Arrange — two real prompts; the newer one describes the session now.
        var text = """
            {"type":"user","message":{"role":"user","content":"napraw zaokrąglanie na fakturach"}}
            {"type":"assistant","message":{"role":"assistant","content":"ok"}}
            {"type":"user","message":{"role":"user","content":"dodaj jeszcze walidację NIP w tym samym miejscu"}}
            """;

        // Act
        var prompts = DiskSessionLookup.ExtractPrompts(text);

        // Assert
        Assert.Equal("napraw zaokrąglanie na fakturach", prompts.FirstPrompt);
        Assert.Equal("dodaj jeszcze walidację NIP w tym samym miejscu", prompts.LastMeaningfulPrompt);
    }

    [Fact]
    public void ExtractPrompts_OnlyShortAcknowledgements_FallsBackToTheFirstOne()
    {
        // Arrange — nothing long enough to be meaningful; a short prompt
        // still beats showing a bare id.
        var text = """
            {"type":"user","message":{"role":"user","content":"ok"}}
            {"type":"user","message":{"role":"user","content":"tak"}}
            """;

        // Act
        var prompts = DiskSessionLookup.ExtractPrompts(text);

        // Assert
        Assert.Equal("ok", prompts.FirstPrompt);
        Assert.Null(prompts.LastMeaningfulPrompt);
    }

    [Fact]
    public void ExtractPrompts_SkipsHarnessToolResultsAndBrokenLines()
    {
        // Arrange — harness noise, a tool_result round, half a line cut at
        // the chunk boundary, and one real (pasted) prompt.
        var text = """
            {"type":"user","message":{"role":"user","content":"<local-command-caveat>Caveat: The messages below were generated...</local-command-caveat>"}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"a very long tool result that is not a prompt"}]}}
            ssage":{"role":"user","content":"half a line from the chunk boundary"}}
            {"type":"user","message":{"role":"user","content":"<pasted_content id=\"x\">https://example.com/bug/42?x=1</pasted_content>"}}
            """;

        // Act
        var prompts = DiskSessionLookup.ExtractPrompts(text);

        // Assert
        Assert.Equal("https://example.com/bug/42?x=1", prompts.LastMeaningfulPrompt);
    }

    [Fact]
    public void ExtractPrompts_NothingUsable_ReturnsNull()
    {
        // Arrange
        var text = """
            {"type":"summary","summary":"whatever"}
            {"type":"user","message":{"role":"user","content":"<system-reminder>context</system-reminder>"}}
            """;

        // Act
        var prompts = DiskSessionLookup.ExtractPrompts(text);

        // Assert
        Assert.Null(prompts.FirstPrompt);
        Assert.Null(prompts.LastMeaningfulPrompt);
    }

    [Fact]
    public void ReadNamesFromFile_ReturnsPromptLabel_WhenNoSlugOrRename()
    {
        // Arrange — a transcript with no slug and no custom title, like the
        // "098b5b60" session from the Reopen window.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".jsonl");
        var line =
            """{"type":"user","message":{"role":"user","content":"research on serial numbers in the API"}}""";
        File.WriteAllText(path, line + "\n");
        try
        {
            // Act
            var names = DiskSessionLookup.ReadNamesFromFile(path);

            // Assert
            Assert.Null(names.Slug);
            Assert.Null(names.CustomTitle);
            Assert.Equal("research on serial numbers in the API", names.FirstPrompt);
            Assert.Equal("research on serial numbers in the API", names.LastMeaningfulPrompt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractNames_DecodedControlCharacters_AreSanitized()
    {
        // Arrange — a title whose JSON escapes decode into a real newline and
        // an ANSI escape; it must come out as one clean line, or a crafted
        // session file could forge log lines and window labels.
        var text = @"{""type"":""custom-title"",""title"":""ok\nFAKE\u001b[2Jline""}";

        // Act
        var names = DiskSessionLookup.ExtractNames(text);

        // Assert
        Assert.NotNull(names.CustomTitle);
        Assert.DoesNotContain(names.CustomTitle!, c => char.IsControl(c));
        Assert.StartsWith("ok FAKE", names.CustomTitle);
    }

    [Fact]
    public void ExtractNames_DecodesEscapedQuotesAndUnicode()
    {
        // Arrange — a rename with quotes and a Polish character written the
        // way the JSONL stores them.
        var text = @"{""type"":""custom-title"",""title"":""My \""big\"" fix żab""}";

        // Act
        var names = DiskSessionLookup.ExtractNames(text);

        // Assert
        Assert.Equal("My \"big\" fix żab", names.CustomTitle);
    }

    [Fact]
    public void ReadNamesFromFile_MidSizeFile_FindsRenameAtTheEnd()
    {
        // Arrange — a ~200 KB file (bigger than one 128 KB chunk, smaller than
        // two) whose rename sits in the FINAL bytes; the tail must be read.
        var path = Path.Combine(Path.GetTempPath(), $"sidekick-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var filler = @"{""type"":""assistant"",""message"":""" + new string('x', 1000) + @"""}";
            var lines = Enumerable.Repeat(filler, 200)
                .Append(@"{""slug"":""late-slug""}")
                .Append(@"{""type"":""user"",""message"":{""role"":""user"",""content"":""dorzuć do tego jeszcze walidację numerów""}}")
                .Append(@"{""type"":""custom-title"",""title"":""Late rename""}");
            File.WriteAllLines(path, lines);

            // Act
            var names = DiskSessionLookup.ReadNamesFromFile(path);

            // Assert — the rename, the slug AND the prompt all sit past the
            // head chunk; the real two-chunk read must surface them.
            Assert.Equal("late-slug", names.Slug);
            Assert.Equal("Late rename", names.CustomTitle);
            Assert.Equal("dorzuć do tego jeszcze walidację numerów", names.LastMeaningfulPrompt);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
