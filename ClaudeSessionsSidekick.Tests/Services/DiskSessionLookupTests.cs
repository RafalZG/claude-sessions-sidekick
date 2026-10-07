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
                .Append(@"{""type"":""custom-title"",""title"":""Late rename""}");
            File.WriteAllLines(path, lines);

            // Act
            var names = DiskSessionLookup.ReadNamesFromFile(path);

            // Assert
            Assert.Equal("late-slug", names.Slug);
            Assert.Equal("Late rename", names.CustomTitle);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
