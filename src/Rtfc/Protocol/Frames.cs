using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtfc.Protocol;

/// <summary>Everything that travels inside a session (spec §7.1, §8.2). Unknown types are ignored, which is what keeps old daemons talking to new ones.</summary>
public static class FrameType
{
    public const string Hello = "hello";
    public const string InviteAccept = "invite_accept";
    public const string AcceptAck = "accept_ack";
    public const string Message = "message";
    public const string Ack = "ack";
    public const string Error = "error";
    public const string Bye = "bye";
}

public abstract record Frame(string Type);

/// <summary>Exchanged first on every session. Protocol version negotiation lives here.</summary>
public sealed record HelloFrame(int V, long DeviceListVersion) : Frame(FrameType.Hello)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// The only frame a restricted session may send (spec §5.1). Carries the acceptor's person
/// CA in full because a TLS handshake proves the leaf, not the chain the receiver has never
/// seen; the receiver checks that the leaf it saw in the handshake was issued by this CA.
/// </summary>
public sealed record InviteAcceptFrame(string Nonce, string PersonCa, string Handle, string DeviceName, string[] Hints)
    : Frame(FrameType.InviteAccept);

/// <summary>The inviter's side of the same exchange. The acceptor checks the CA against the person id in the token.</summary>
public sealed record AcceptAckFrame(string PersonCa, string Handle, string DeviceName, string[] Hints)
    : Frame(FrameType.AcceptAck);

public sealed record Address(string Person, string Device);

public sealed record MessageBody(string Text);

/// <summary>The envelope of spec §7.1, identical on every transport.</summary>
public sealed record MessageFrame(
    int V,
    string Id,
    Address From,
    Address To,
    long Seq,
    string? Thread,
    string? ReplyTo,
    string Origin,
    int Hop,
    string SentAt,
    MessageBody Body) : Frame(FrameType.Message)
{
    /// <summary>The body cap of spec §7.1, in UTF-8 bytes.</summary>
    public const int MaxBodyBytes = 64 * 1024;
}

public static class AckStatus
{
    public const string Ok = "ok";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";
}

/// <summary>Sent only after the message is committed, so "delivered" means durable (spec §7.2).</summary>
public sealed record AckFrame(string Id, string Status, string? Reason = null) : Frame(FrameType.Ack);

public sealed record ErrorFrame(string Code, string Message) : Frame(FrameType.Error);

public sealed record ByeFrame() : Frame(FrameType.Bye);

/// <summary>A frame whose type this build doesn't know. Ignored by receivers, never sent.</summary>
public sealed record UnknownFrame(string Type) : Frame(Type);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HelloFrame))]
[JsonSerializable(typeof(InviteAcceptFrame))]
[JsonSerializable(typeof(AcceptAckFrame))]
[JsonSerializable(typeof(MessageFrame))]
[JsonSerializable(typeof(AckFrame))]
[JsonSerializable(typeof(ErrorFrame))]
[JsonSerializable(typeof(ByeFrame))]
public sealed partial class ProtocolJson : JsonSerializerContext;

public static class Frames
{
    public static byte[] Serialize(Frame frame) => frame switch
    {
        HelloFrame f => JsonSerializer.SerializeToUtf8Bytes(f, ProtocolJson.Default.HelloFrame),
        InviteAcceptFrame f => JsonSerializer.SerializeToUtf8Bytes(f, ProtocolJson.Default.InviteAcceptFrame),
        AcceptAckFrame f => JsonSerializer.SerializeToUtf8Bytes(f, ProtocolJson.Default.AcceptAckFrame),
        MessageFrame f => JsonSerializer.SerializeToUtf8Bytes(f, ProtocolJson.Default.MessageFrame),
        AckFrame f => JsonSerializer.SerializeToUtf8Bytes(f, ProtocolJson.Default.AckFrame),
        ErrorFrame f => JsonSerializer.SerializeToUtf8Bytes(f, ProtocolJson.Default.ErrorFrame),
        ByeFrame f => JsonSerializer.SerializeToUtf8Bytes(f, ProtocolJson.Default.ByeFrame),
        _ => throw new ArgumentException($"Cannot send a frame of type '{frame.Type}'.", nameof(frame)),
    };

    /// <summary>Parses a frame by its <c>type</c>. Malformed JSON throws; a well-formed frame of an unknown type is returned as <see cref="UnknownFrame"/>.</summary>
    public static Frame Parse(ReadOnlyMemory<byte> json)
    {
        string type;
        using (var document = JsonDocument.Parse(json))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new ProtocolException("A frame must be an object with a string 'type'.");
            }

            type = typeElement.GetString()!;
        }

        Frame? frame = type switch
        {
            FrameType.Hello => JsonSerializer.Deserialize(json.Span, ProtocolJson.Default.HelloFrame),
            FrameType.InviteAccept => JsonSerializer.Deserialize(json.Span, ProtocolJson.Default.InviteAcceptFrame),
            FrameType.AcceptAck => JsonSerializer.Deserialize(json.Span, ProtocolJson.Default.AcceptAckFrame),
            FrameType.Message => JsonSerializer.Deserialize(json.Span, ProtocolJson.Default.MessageFrame),
            FrameType.Ack => JsonSerializer.Deserialize(json.Span, ProtocolJson.Default.AckFrame),
            FrameType.Error => JsonSerializer.Deserialize(json.Span, ProtocolJson.Default.ErrorFrame),
            FrameType.Bye => JsonSerializer.Deserialize(json.Span, ProtocolJson.Default.ByeFrame),
            _ => new UnknownFrame(type),
        };
        return frame ?? throw new ProtocolException($"A '{type}' frame was null.");
    }
}

/// <summary>The peer broke the protocol. The session is closed; nothing is retried.</summary>
public sealed class ProtocolException(string message) : Exception(message);
