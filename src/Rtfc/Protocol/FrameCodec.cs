using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Rtfc.Protocol;

/// <summary>
/// Frames on the wire are a 4-byte big-endian length followed by UTF-8 JSON (spec §8.2).
/// The length cap bounds what a peer can make us buffer before anything is parsed.
/// </summary>
public static class FrameCodec
{
    public const int HeaderBytes = 4;

    /// <summary>Comfortably above the 64 KB body cap plus envelope, and far below anything worth exhausting memory with.</summary>
    public const int MaxFrameBytes = 256 * 1024;

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new ProtocolException($"A frame of {payload.Length} bytes exceeds the {MaxFrameBytes}-byte limit.");
        }

        var buffer = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderBytes));
        return buffer;
    }

    /// <summary>The next frame's payload, or null when the peer closed the stream cleanly between frames.</summary>
    public static async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryReadFrame(ref buffer, out var payload))
            {
                reader.AdvanceTo(buffer.Start);
                return payload;
            }

            if (result.IsCompleted)
            {
                var leftover = buffer.Length;
                reader.AdvanceTo(buffer.End);
                if (leftover > 0)
                {
                    throw new ProtocolException("The stream ended in the middle of a frame.");
                }

                return null;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> payload)
    {
        payload = default;
        if (buffer.Length < HeaderBytes)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderBytes];
        buffer.Slice(0, HeaderBytes).CopyTo(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new ProtocolException($"A frame announced {length} bytes; the limit is {MaxFrameBytes}.");
        }

        if (buffer.Length < HeaderBytes + length)
        {
            return false;
        }

        var bytes = new byte[length];
        buffer.Slice(HeaderBytes, length).CopyTo(bytes);
        payload = bytes;
        buffer = buffer.Slice(HeaderBytes + length);
        return true;
    }
}
