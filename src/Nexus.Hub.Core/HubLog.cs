using Nexus.Contracts;

namespace Nexus.Hub.Core;

/// <summary>Hub-side log file plus an in-memory event for the UI.</summary>
public static class HubLog
{
    private static readonly object Gate = new();
    public static string FilePath { get; } = Path.Combine(NexusPaths.LogsDir, $"hub-{DateTime.Now:yyyyMMdd}.log");

    public static event Action<string>? Message;

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {level} {message}" + (ex is null ? "" : $" ({ex.GetType().Name}: {ex.Message})");
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(NexusPaths.LogsDir);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}{(ex is null ? "" : ex + Environment.NewLine)}");
            }
        }
        catch { /* ignored */ }
        try { Message?.Invoke(line); } catch { /* ignored */ }
    }
}
