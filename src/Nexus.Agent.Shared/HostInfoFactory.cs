using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Nexus.Contracts;

namespace Nexus.Agent;

public static class HostInfoFactory
{
    /// <summary>Fills the process/machine fields common to every host.</summary>
    public static HostInfo Create(string hostKind, string product, string version, string build, AgentLog log)
    {
        DateTime start;
        try { start = Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { start = DateTime.UtcNow; }

        return new HostInfo
        {
            HostKind = hostKind,
            Product = product,
            Version = version,
            Build = build,
            ProcessId = Compat.ProcessId,
            ProcessStartUtc = start,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            AgentVersion = typeof(HostInfoFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            Runtime = RuntimeInformation.FrameworkDescription,
            LogFile = log.FilePath,
        };
    }
}
