namespace Rtfc.Storage;

public static class ContactStatus
{
    public const string Active = "active";
    public const string Removed = "removed";
    public const string Blocked = "blocked";
}

public static class InboundMode
{
    public const string Park = "park";
    public const string AutoHeadless = "auto_headless";
    public const string AutoSession = "auto_session";
}

public static class DeviceStatus
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public static class InboxKind
{
    public const string Person = "person";
    public const string Source = "source";
    public const string Notice = "notice";
}

public static class OutboxKind
{
    public const string Reply = "reply";
    public const string Message = "message";
    public const string Receipt = "receipt";
}

public static class OutboxState
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Expired = "expired";
}

public static class SentKind
{
    public const string Message = "message";
    public const string Reply = "reply";
}

public static class SentState
{
    public const string Queued = "queued";
    public const string Delivered = "delivered";
    public const string Read = "read";
    public const string Expired = "expired";
}

public static class InboxState
{
    public const string Parked = "parked";
    public const string Read = "read";
    public const string Answered = "answered";
    public const string Dismissed = "dismissed";
    public const string AutoRunning = "auto_running";
    public const string AutoDone = "auto_done";
    public const string AutoFailed = "auto_failed";
}

public static class MessageOrigin
{
    public const string Human = "human";
    public const string Auto = "auto";
}

public sealed record SelfRow(
    string PersonId,
    string Handle,
    byte[] PersonCaCert,
    string DeviceId,
    string DeviceName,
    byte[] DeviceCert,
    long DeviceListVersion);

public sealed record ContactRow(
    string PersonId,
    string Handle,
    byte[] PersonCaCert,
    string Status,
    DateTimeOffset? AcceptedAt,
    string InboundMode,
    string? AutoScope,
    string? AutoOwnerDevice,
    bool ReadReceipts,
    long DeviceListVersion,
    long Rev);

public sealed record DeviceRow(
    string DeviceId,
    string PersonId,
    string Name,
    byte[] Cert,
    string Status,
    IReadOnlyList<string> Endpoints);

public sealed record InviteRow(
    string Nonce,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? UsedBy,
    DateTimeOffset? UsedAt);

public enum InviteUse
{
    Used,
    Unknown,
    AlreadyUsed,
    Expired,
}

/// <summary>A message from a person, as stored. Source items (spec §10) get their own row shape when they land.</summary>
public sealed record InboxMessage(
    string Id,
    string ToDevice,
    string FromPerson,
    string FromDevice,
    long Seq,
    string? ReplyTo,
    string Origin,
    int Hop,
    string? Thread,
    string Body,
    DateTimeOffset? SentAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset UpdatedAt,
    string State,
    string? HandledBy,
    DateTimeOffset? HandledAt,
    string? Draft = null,
    string? Note = null,
    int AutoAttempts = 0,
    string Kind = InboxKind.Person);

/// <summary>Something that must reach a peer later (spec §7.2). The envelope is the frame as it will be sent, minus the sequence number and device, which are filled at delivery.</summary>
public sealed record OutboxRow(
    string Id,
    string ToPerson,
    string? ToDevice,
    string Kind,
    string Envelope,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int Attempts,
    string State);

/// <summary>A message that left this device, so a receipt or an expiry has something to update.</summary>
public sealed record SentRow(
    string Id,
    string ToPerson,
    string? ToDevice,
    string? Thread,
    string? ReplyTo,
    string Origin,
    string Kind,
    string Body,
    DateTimeOffset SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? ExpiresAt,
    string State);
