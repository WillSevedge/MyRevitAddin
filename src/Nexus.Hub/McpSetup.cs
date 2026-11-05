using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>
/// Connects AI assistants to Nexus: adds the "nexus" MCP server (nexus-mcp.exe) to Claude Desktop's configuration,
/// keeping every other server there (pyRevit's, for example) and saving a backup first.
/// </summary>
internal static class McpSetup
{
    /// <summary>nexus-mcp.exe: installed with the hub, or next to this hub.</summary>
    public static string? ServerPath()
    {
        foreach (var dir in new[] { NexusPaths.HubInstallDir, AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "" })
        {
            string exe = Path.Combine(dir, "nexus-mcp.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Claude Desktop's config files on this PC (the standard install and the Microsoft Store one).</summary>
    public static List<string> ClaudeDesktopConfigs()
    {
        var found = new List<string>();
        string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (Directory.Exists(roaming)) found.Add(Path.Combine(roaming, "claude_desktop_config.json"));
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (var pkg in Directory.EnumerateDirectories(packages, "Claude_*"))
                {
                    string dir = Path.Combine(pkg, "LocalCache", "Roaming", "Claude");
                    if (Directory.Exists(dir)) found.Add(Path.Combine(dir, "claude_desktop_config.json"));
                }
        }
        catch { /* no access */ }
        if (found.Count == 0)
        {
            // Claude Desktop installed but never configured: its standard place.
            Directory.CreateDirectory(roaming);
            found.Add(Path.Combine(roaming, "claude_desktop_config.json"));
        }
        return found;
    }

    /// <summary>Adds or updates "nexus" under mcpServers. Returns the files changed.</summary>
    public static List<string> ConnectClaudeDesktop(string serverPath)
    {
        var changed = new List<string>();
        foreach (var file in ClaudeDesktopConfigs())
        {
            JsonObject root;
            if (File.Exists(file))
            {
                string text = File.ReadAllText(file);
                root = (string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject)
                       ?? throw new InvalidOperationException($"{file} is not a JSON object; fix or remove it first.");
                File.Copy(file, file + ".nexus-backup", overwrite: true);
            }
            else root = new JsonObject();
            if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
            servers["nexus"] = new JsonObject { ["command"] = serverPath, ["args"] = new JsonArray() };
            File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            HubLog.Info($"Added the Nexus MCP server to {file}");
            changed.Add(file);
        }
        return changed;
    }

    /// <summary>The Claude Code command that adds the server (for the clipboard).</summary>
    public static string ClaudeCodeCommand(string serverPath) => $"claude mcp add nexus --scope user -- \"{serverPath}\"";
}
