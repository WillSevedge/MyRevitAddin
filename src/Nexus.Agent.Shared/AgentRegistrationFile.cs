using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>Writes/removes %LOCALAPPDATA%\Nexus\agents\{pid}.json so the hub can find us.</summary>
public static class AgentRegistrationFile
{
    public static string Write(AgentRegistration registration)
    {
        Directory.CreateDirectory(NexusPaths.AgentsDir);
        string path = NexusPaths.RegistrationFile(registration.Host.ProcessId);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(registration, Json.Indented));
        Compat.MoveOverwrite(tmp, path);
        return path;
    }

    public static void Delete(int processId)
    {
        try { File.Delete(NexusPaths.RegistrationFile(processId)); } catch { /* ignored */ }
    }
}
