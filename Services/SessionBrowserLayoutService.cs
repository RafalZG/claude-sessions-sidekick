using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// One column's persisted layout. Columns are identified by Tag (set in
/// XAML) so adding/removing columns doesn't break older saved layouts —
/// unknown tags are ignored on restore, missing ones keep their default.
/// </summary>
public sealed class ColumnLayout
{
    public string Tag { get; set; } = "";
    public double WidthValue { get; set; }
    public string WidthUnit { get; set; } = "Pixel";
    public int DisplayIndex { get; set; }
}

public sealed class WindowGeometry
{
    public double Width { get; set; }
    public double Height { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    /// <summary>"Normal" or "Maximized". Minimised state isn't persisted —
    /// nobody wants the window to come back hidden.</summary>
    public string State { get; set; } = "Normal";
}

public sealed class SessionBrowserLayout
{
    public List<ColumnLayout> Columns { get; set; } = [];
    public WindowGeometry? Window { get; set; }
}

/// <summary>
/// Persists Session Browser column widths and display order across
/// open/close cycles, so resizing/reordering the grid survives a window
/// reopen instead of resetting to XAML defaults.
/// </summary>
public static class SessionBrowserLayoutService
{
    private static string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "session-browser-layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static SessionBrowserLayout? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<SessionBrowserLayout>(json);
        }
        catch
        {
            // Corrupt file → fall back to defaults rather than crash the
            // window. Next save overwrites with a clean layout.
            return null;
        }
    }

    public static void Save(SessionBrowserLayout layout)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(layout, JsonOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        catch
        {
            // Non-critical — losing a layout save isn't worth bubbling up.
        }
    }

    /// <summary>Test helper: redirect storage to a temp path. Pass null to reset.</summary>
    internal static void UseStoragePathForTesting(string? path)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeSessionsSidekick", "session-browser-layout.json");
    }
}
