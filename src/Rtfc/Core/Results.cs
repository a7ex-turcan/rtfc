using System.Text.Json.Serialization;

namespace Rtfc.Core;

/// <summary>
/// What the daemon reports back over IPC and what the MCP tools hand to Claude. Shapes
/// follow spec §7.2 and §9.2; nulls are omitted, so each status carries only its own fields.
/// </summary>
public static class SendStatus
{
    public const string Delivered = "delivered";
    public const string Partial = "partial";
    public const string NobodyHome = "nobody_home";
    public const string DeviceOffline = "device_offline";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
}

public sealed record SendResult(
    string Status,
    string? MessageId = null,
    string[]? To = null,
    string[]? Unreachable = null,
    string? Person = null,
    string? Requested = null,
    string[]? Online = null,
    string? Reason = null)
{
    public static SendResult Rejected(string reason) => new(SendStatus.Rejected, Reason: reason);
}

public sealed record DeviceView(string Name, string DeviceId, string Status, bool? Online, string[] Hints);

public sealed record ContactView(
    string Handle,
    string PersonId,
    string Fingerprint,
    string Status,
    string InboundMode,
    DateTimeOffset? AcceptedAt,
    DeviceView[] Devices);

public sealed record InboxSummary(
    string Id,
    string From,
    string FromDevice,
    string State,
    string Preview,
    DateTimeOffset ReceivedAt,
    string? ReplyTo,
    string Origin);

public sealed record InboxOpened(
    string Id,
    string From,
    string FromDevice,
    string FromPerson,
    string State,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? SentAt,
    string? Thread,
    string? ReplyTo,
    string Origin,
    int Hop,
    string Body);

public sealed record InviteResult(string Token, DateTimeOffset ExpiresAt, string[] Hints);

public static class AcceptStatus
{
    public const string Accepted = "accepted";
    public const string NobodyHome = "nobody_home";
    public const string Invalid = "invalid";
    public const string Rejected = "rejected";
    public const string AlreadyContact = "already_contact";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
}

public sealed record AcceptResult(
    string Status,
    string? Handle = null,
    string? PersonId = null,
    string? Fingerprint = null,
    string? Reason = null);

public sealed record StatusGlobal(int Parked, string[] From);

public sealed record ProjectStatus(int Reviews, int Tickets, int PendingSubscriptions);

/// <summary>The contents of <c>status.json</c> (spec §11).</summary>
public sealed record StatusSnapshot(StatusGlobal Global, Dictionary<string, ProjectStatus> Projects, bool Away);

/// <summary>The payload behind <c>rtfc1_</c> (spec §5.1): a fingerprint and hints, never certificates, so it stays short enough to paste.</summary>
public sealed record InviteTokenPayload(int V, string Person, string Handle, string[] Hints, string Nonce, DateTimeOffset ExpiresAt);

/// <summary>Per-device settings in <c>config.json</c>.</summary>
public sealed record RtfcConfig(int Port, string[]? HintHosts)
{
    public static RtfcConfig Default => new(Net.TcpTransport.DefaultPort, null);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(ContactView))]
[JsonSerializable(typeof(ContactView[]))]
[JsonSerializable(typeof(InboxSummary))]
[JsonSerializable(typeof(InboxSummary[]))]
[JsonSerializable(typeof(InboxOpened))]
[JsonSerializable(typeof(InviteResult))]
[JsonSerializable(typeof(AcceptResult))]
[JsonSerializable(typeof(StatusSnapshot))]
[JsonSerializable(typeof(InviteTokenPayload))]
[JsonSerializable(typeof(RtfcConfig))]
public sealed partial class CoreJson : JsonSerializerContext;
