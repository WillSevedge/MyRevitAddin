using System.Text;
using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>
/// Minimal thread-safe file logger. Never throws: logging must not be able to
/// take the host down.
/// </summary>
public sealed class AgentLog
{
    private readonly object _gate = new();

    public string FilePath { get; }

    public AgentLog(string name)
    {
        string file = $"{name}-{DateTime.Now:yyyyMMdd}-{Compat.ProcessId}.log";
        FilePath = Path.Combine(NexusPaths.LogsDir, file);
        try { Directory.CreateDirectory(NexusPaths.LogsDir); } catch { /* ignored */ }
    }

    public void Info(string message) => Write("INFO ", message, null);
    public void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(' ').Append(level)
              .Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
              .Append(message);
            if (ex is not null) sb.AppendLine().Append(ex);
            sb.AppendLine();
            lock (_gate)
                File.AppendAllText(FilePath, sb.ToString());
        }
        catch
        {
            // Swallow: a full disk or locked file must never crash the host.
        }
    }
}
