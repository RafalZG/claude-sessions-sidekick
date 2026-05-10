using System.IO;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

// SessionBrowserLayoutService keeps its storage path in a static field, so
// running these tests in parallel with each other would scramble paths.
// xUnit serialises tests within a Collection.
[Collection("SessionBrowserLayoutService")]
public class SessionBrowserLayoutServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public SessionBrowserLayoutServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LayoutTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "session-browser-layout.json");
        SessionBrowserLayoutService.UseStoragePathForTesting(_path);
    }

    public void Dispose()
    {
        SessionBrowserLayoutService.UseStoragePathForTesting(null);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        // Act
        var result = SessionBrowserLayoutService.Load();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsColumns()
    {
        // Arrange
        var layout = new SessionBrowserLayout
        {
            Columns =
            [
                new ColumnLayout { Tag = "project", WidthValue = 150, WidthUnit = "Pixel", DisplayIndex = 1 },
                new ColumnLayout { Tag = "topic",   WidthValue = 0,   WidthUnit = "Star",  DisplayIndex = 2 },
                new ColumnLayout { Tag = "model",   WidthValue = 80,  WidthUnit = "Pixel", DisplayIndex = 4 }
            ]
        };

        // Act
        SessionBrowserLayoutService.Save(layout);
        var loaded = SessionBrowserLayoutService.Load();

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Columns.Count);
        var project = loaded.Columns.First(c => c.Tag == "project");
        Assert.Equal(150, project.WidthValue);
        Assert.Equal("Pixel", project.WidthUnit);
        Assert.Equal(1, project.DisplayIndex);

        var topic = loaded.Columns.First(c => c.Tag == "topic");
        Assert.Equal("Star", topic.WidthUnit);
    }

    [Fact]
    public void Save_OverwritesExisting()
    {
        // Arrange
        SessionBrowserLayoutService.Save(new SessionBrowserLayout
        {
            Columns = [new ColumnLayout { Tag = "project", WidthValue = 100, WidthUnit = "Pixel" }]
        });

        // Act
        SessionBrowserLayoutService.Save(new SessionBrowserLayout
        {
            Columns = [new ColumnLayout { Tag = "project", WidthValue = 250, WidthUnit = "Pixel" }]
        });
        var loaded = SessionBrowserLayoutService.Load();

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(250, loaded!.Columns.Single().WidthValue);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        // Arrange
        File.WriteAllText(_path, "{ this is not valid json");

        // Act
        var result = SessionBrowserLayoutService.Load();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Save_CreatesParentDirectory()
    {
        // Arrange — point at a path whose parent doesn't exist yet
        var nested = Path.Combine(_tempDir, "subdir", "layout.json");
        SessionBrowserLayoutService.UseStoragePathForTesting(nested);

        // Act
        SessionBrowserLayoutService.Save(new SessionBrowserLayout
        {
            Columns = [new ColumnLayout { Tag = "project", WidthValue = 100, WidthUnit = "Pixel" }]
        });

        // Assert
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void EmptyLayout_RoundTripsCleanly()
    {
        // Act
        SessionBrowserLayoutService.Save(new SessionBrowserLayout());
        var loaded = SessionBrowserLayoutService.Load();

        // Assert
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Columns);
    }
}
