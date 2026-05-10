using System.IO;

namespace ClaudeSessionsSidekick.Services;

public static class AppLogger
{
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick");

    public static readonly string LogPath = Path.Combine(LogDirectory, "app.log");

    private static readonly object _lock = new();
    private const long MaxLogSize = 1_000_000; // 1 MB - rotate when exceeded

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex != null ? $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (dir != null)
                {
                    Directory.CreateDirectory(dir);
                }

                // Rotate if too large
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogSize)
                {
                    var backupPath = LogPath + ".old";
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                    File.Move(LogPath, backupPath);
                }

                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app
        }
    }
}
