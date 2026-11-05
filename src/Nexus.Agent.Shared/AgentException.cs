using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>An expected failure with a structured error code for the hub.</summary>
public sealed class AgentException : Exception
{
    public string Code { get; }

    public AgentException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    public ErrorInfo ToErrorInfo() => new(Code, Message);
}
