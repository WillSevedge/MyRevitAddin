using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>Talks to the background hub (the Nexus tray app) from a host.</summary>
public static class HubControl
{
    /// <summary>
    /// Brings the hub window forward, starting the hub first if it is not running.
    /// Returns null on success, else a message for the user.
    /// With <paramref name="readerId"/> (e.g. "revit.fabrication.parts") the hub opens that view.
    /// </summary>
    public static string? Show(AgentLog log, string? readerId = null)
    {
        try
        {
            // We are the foreground app (the user just clicked in the host); let the hub take focus.
            AllowSetForegroundWindow(AsfwAny);
            if (Send(readerId is null ? "show" : "dataset " + readerId)) return null;

            if (!File.Exists(NexusPaths.HubExe))
                return "The Nexus hub is not installed.\n\nBuild the solution in Visual Studio; the build installs the hub to:\n" + NexusPaths.HubInstallDir;

            var start = new ProcessStartInfo(NexusPaths.HubExe) { UseShellExecute = true, WorkingDirectory = NexusPaths.HubInstallDir };
            if (readerId is not null) start.Arguments = "--dataset " + readerId;
            Process.Start(start);
            log.Info("Started the hub: " + NexusPaths.HubExe);
            return null;
        }
        catch (Exception ex)
        {
            log.Error("Could not show the hub.", ex);
            return "Could not open the Nexus hub: " + ex.Message;
        }
    }

    /// <summary>True when a running hub accepted the command.</summary>
    public static bool Send(string command, int timeoutMs = 500)
    {
        try
        {
            using var pipe = Compat.CreateUserOnlyClient(Protocol.HubControlPipe, PipeDirection.Out);
            pipe.Connect(timeoutMs);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
