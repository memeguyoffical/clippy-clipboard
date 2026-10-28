using System.Text;

namespace Clippy.App.Services;

/// <summary>
/// Minimal append-only log. Never write clipboard contents here: only counts,
/// strategies and error codes.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clippy",
        "logs");

    private static readonly string LogPath = Path.Combine(LogDirectory, "Clippy.log");

    internal static void Info(string message) => Write("INFO", message);

    internal static void Warn(string message) => Write("WARN", message);

    internal static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RollIfLarge();
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private static void RollIfLarge()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < 1_000_000)
        {
            return;
        }

        var previous = LogPath + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(LogPath, previous);
    }
}
