using System.Buffers.Binary;
using System.Text.Json;

namespace Nexus.Contracts;

/// <summary>
/// Wire format: 4-byte little-endian length, then that many bytes of UTF-8 JSON
/// (one <see cref="Envelope"/>).
/// </summary>
public static class Frames
{
    public static async Task WriteAsync(Stream stream, Envelope envelope, CancellationToken ct = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope, Json.Options);
        if (body.Length > Protocol.MaxFrameBytes)
            throw new InvalidOperationException($"Message is {body.Length:N0} bytes; the limit is {Protocol.MaxFrameBytes:N0}.");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns null when the other side closed the pipe cleanly.</summary>
    public static async Task<Envelope?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > Protocol.MaxFrameBytes)
            throw new InvalidDataException($"Invalid frame length {length}.");

        byte[] body = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, body, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Pipe closed in the middle of a message.");

        return JsonSerializer.Deserialize<Envelope>(body, Json.Options)
               ?? throw new InvalidDataException("Empty message.");
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer, read, buffer.Length - read, ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("Pipe closed in the middle of a message.");
            }
            read += n;
        }
        return true;
    }
}
