using System.IO;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

[Collection("SessionNotesService")]
public class SessionNotesServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public SessionNotesServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NotesTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "session-notes.json");
        SessionNotesService.UseStoragePathForTesting(_path);
    }

    public void Dispose()
    {
        // Reset back to production path so leftover state can't leak into
        // sibling test classes that touch SessionNotesService.
        SessionNotesService.UseStoragePathForTesting(null);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetNote_NoNoteSet_ReturnsNull()
    {
        // Act
        var note = SessionNotesService.GetNote("missing-session");

        // Assert
        Assert.Null(note);
    }

    [Fact]
    public void SetNote_ThenGet_ReturnsStoredText()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "fix didn't work, retry monday");

        // Act
        var note = SessionNotesService.GetNote("s1");

        // Assert
        Assert.Equal("fix didn't work, retry monday", note);
    }

    [Fact]
    public void SetNote_TrimsWhitespace()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "   trimmed   ");

        // Act
        var note = SessionNotesService.GetNote("s1");

        // Assert
        Assert.Equal("trimmed", note);
    }

    [Fact]
    public void SetNote_EmptyString_ClearsNote()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "old note");
        SessionNotesService.SetNote("s1", "");

        // Act
        var note = SessionNotesService.GetNote("s1");

        // Assert
        Assert.Null(note);
    }

    [Fact]
    public void SetNote_WhitespaceOnly_ClearsNote()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "old note");
        SessionNotesService.SetNote("s1", "   ");

        // Act
        var note = SessionNotesService.GetNote("s1");

        // Assert
        Assert.Null(note);
    }

    [Fact]
    public void SetNote_Null_ClearsNote()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "old note");
        SessionNotesService.SetNote("s1", null);

        // Act
        var note = SessionNotesService.GetNote("s1");

        // Assert
        Assert.Null(note);
    }

    [Fact]
    public void SetNote_OverwritesExisting()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "first version");

        // Act
        SessionNotesService.SetNote("s1", "second version");

        // Assert
        Assert.Equal("second version", SessionNotesService.GetNote("s1"));
    }

    [Fact]
    public void SetNote_PersistsToDisk()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "persisted");

        // Act — force a fresh load by re-pointing to the same path
        SessionNotesService.UseStoragePathForTesting(_path);

        // Assert
        Assert.Equal("persisted", SessionNotesService.GetNote("s1"));
    }

    [Fact]
    public void SetNote_MultipleSessions_KeptIndependent()
    {
        // Arrange
        SessionNotesService.SetNote("s1", "note one");
        SessionNotesService.SetNote("s2", "note two");

        // Act + Assert
        Assert.Equal("note one", SessionNotesService.GetNote("s1"));
        Assert.Equal("note two", SessionNotesService.GetNote("s2"));
    }

    [Fact]
    public void SetNote_PreservesMultilineContent()
    {
        // Arrange
        var multiline = "line one\nline two\nline three";

        // Act
        SessionNotesService.SetNote("s1", multiline);

        // Assert
        Assert.Equal(multiline, SessionNotesService.GetNote("s1"));
    }
}
