using System.Text.Json;

namespace Nexus.Contracts;

/// <summary>
/// A command an agent offers to AI assistants (through Nexus.Mcp): what it does and the JSON schema of its arguments.
/// </summary>
public sealed class ToolDescriptor
{
    /// <summary>Snake case, unique within the agent, e.g. "query_entities". Nexus.Mcp prefixes the program ("autocad_").</summary>
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What the tool does and when to use it, written for the AI.</summary>
    public string Description { get; set; } = "";
    /// <summary>JSON schema of the arguments (an object schema).</summary>
    public JsonElement InputSchema { get; set; }
    /// <summary>Only reads: the assistant may call it without asking.</summary>
    public bool ReadOnly { get; set; }
    /// <summary>Removes or overwrites things (the assistant should ask first).</summary>
    public bool Destructive { get; set; }
}

public sealed class ListToolsResponse
{
    public List<ToolDescriptor> Tools { get; set; } = new();
}

public sealed class CallToolRequest
{
    public string Tool { get; set; } = "";
    /// <summary>A <see cref="DocumentInfo.Id"/>, a document title, or empty for the active document.</summary>
    public string? Document { get; set; }
    public JsonElement? Arguments { get; set; }
}

public sealed class CallToolResult
{
    /// <summary>The answer as JSON (lists of entities, handles created...).</summary>
    public JsonElement? Data { get; set; }
    /// <summary>A short line for people and the AI ("Created 3 lines on A-WALL").</summary>
    public string? Summary { get; set; }
    /// <summary>A PNG picture (base64), e.g. of the current view.</summary>
    public string? ImagePng { get; set; }
    /// <summary>The tool ran but could not do what was asked (bad handle, unknown layer...): the summary says why.</summary>
    public bool IsError { get; set; }
}
