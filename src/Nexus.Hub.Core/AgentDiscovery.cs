using System.Diagnostics;
using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core;

/// <summary>Finds running agents from their registration files.</summary>
public static class AgentDiscovery
{
    public static List<AgentRegistration> Discover(bool removeStale = true)
    {
        var found = new List<AgentRegistration>();
        if (!Directory.Exists(NexusPaths.AgentsDir)) return found;

        foreach (var file in Directory.EnumerateFiles(NexusPaths.AgentsDir, "*.json"))
        {
            AgentRegistration? reg = null;
            try
            {
                reg = JsonSerializer.Deserialize<AgentRegistration>(File.ReadAllText(file), Json.Options);
            }
            catch (Exception ex)
            {
                HubLog.Warn($"Unreadable registration {file}", ex);
            }

            if (reg is null || string.IsNullOrEmpty(reg.PipeName)) continue;

            if (!IsAlive(reg.Host))
            {
                HubLog.Info($"Agent pid {reg.Host.ProcessId} is no longer running; ignoring.");
                if (removeStale)
                {
                    try { File.Delete(file); } catch { /* ignored */ }
                }
                continue;
            }
            found.Add(reg);
        }
        return found.OrderBy(r => r.Host.HostKind).ThenBy(r => r.Host.ProcessId).ToList();
    }

    public static bool IsAlive(HostInfo host)
    {
        try
        {
            using var p = Process.GetProcessById(host.ProcessId);
            if (p.HasExited) return false;
            try
            {
                // Guard against pid reuse: the start time must match the registration.
                var start = p.StartTime.ToUniversalTime();
                if (host.ProcessStartUtc != default && Math.Abs((start - host.ProcessStartUtc).TotalSeconds) > 5)
                    return false;
            }
            catch
            {
                // Access denied reading StartTime: assume alive.
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
