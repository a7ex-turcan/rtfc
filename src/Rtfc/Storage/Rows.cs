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

/// <summary>A subscription's life (spec §10.2): it polls only while active, and an edit sends it back to pending.</summary>
public static class SubscriptionStatus
{
    public const string PendingApproval = "pending_approval";
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Error = "error";
}

/// <summary>What happens to a source item when it lands (spec §10.4).</summary>
public static class SourceMode
{
    public const string Park = "park";
    public const string Prepare = "prepare";
    public const string Session = "session";
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

    /// <summary>The origin of a source item: a third party's notification, not a person's message.</summary>
    public const string Source = "source";
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
    long Rev,
    string? AutoSession = null);

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

/// <summary>
/// One inbox row: a message from a person, a notice from rtfc, or a source item (spec §10), which is one row per ticket,
/// pull request or page with its event history appended. A source item fills the person columns with empty strings and
/// <see cref="MessageOrigin.Source"/>, and carries its own fields at the end.
/// </summary>
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
    string Kind = InboxKind.Person,
    string? ProjectId = null,
    string? SubscriptionId = null,
    string? EntityKey = null,
    string? Url = null,
    string? Title = null,
    string? Events = null);

/// <summary>What a source item becomes after new events are merged into it: the caller computes it from the stored history, under the lock.</summary>
public sealed record SourceMerge(string Title, string? Url, string Body, string Events, int Added);

/// <summary>A third-party account (spec §10.5). The token is a file under <c>keys/accounts/</c>, never a column.</summary>
public sealed record AccountRow(string Name, string Type, string? BaseUrl, string? Login, string? AccountId, DateTimeOffset CreatedAt);

/// <summary>One entry of a project's <c>.claude/rtfc.local.json</c>, as approved or waiting (spec §10.2). <c>Selector</c> and <c>Events</c> are JSON.</summary>
public sealed record SubscriptionRow(string Id, string ProjectId, string Account, string Selector, string Events, string Mode, string Status, string ConfigHash);

/// <summary>Where one poll left off (spec §10.1): shared by every subscription with the same account and selector.</summary>
public sealed record SourceCursorRow(string PollKey, string Cursor, string BoundaryIds, DateTimeOffset? NextPollAt, string? LastError);

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

/// <summary>A message that left this device, so a receipt or an expiry has something to update, and a reply knows which project it belongs in (spec §7.6).</summary>
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
    string State,
    string? ProjectId = null);

/// <summary>A project a session has run in (spec §10.2): its root, keyed by the normalized path, and the folder name contacts address it by.</summary>
public sealed record ProjectRow(string Id, string RootPath, string Name);
