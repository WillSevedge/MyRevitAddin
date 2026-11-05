using System.Text.Json;

namespace Nexus.Contracts;

/// <summary>
/// Every frame on the pipe is one JSON-serialized envelope. A request and its
/// response share the same <see cref="Id"/>.
/// </summary>
public sealed class Envelope
{
    public int V { get; set; } = Protocol.Version;
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public JsonElement? Payload { get; set; }
    public ErrorInfo? Error { get; set; }

    public static Envelope Request<T>(string type, T payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = type,
        Payload = JsonSerializer.SerializeToElement(payload, Json.Options),
    };

    public static Envelope Result<T>(string id, T payload) => new()
    {
        Id = id,
        Type = MessageTypes.Result,
        Payload = JsonSerializer.SerializeToElement(payload, Json.Options),
    };

    public static Envelope Failure(string id, ErrorInfo error) => new()
    {
        Id = id,
        Type = MessageTypes.Error,
        Error = error,
    };

    public T? PayloadAs<T>() =>
        Payload is { } p ? p.Deserialize<T>(Json.Options) : default;
}

public sealed class ErrorInfo
{
    public string Code { get; set; } = ErrorCodes.InternalError;
    public string Message { get; set; } = "";
    public string? Detail { get; set; }

    public ErrorInfo() { }
    public ErrorInfo(string code, string message, string? detail = null)
    {
        Code = code;
        Message = message;
        Detail = detail;
    }

    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>Empty payload for requests that take no arguments.</summary>
public sealed class Empty
{
    public static readonly Empty Instance = new();
}
