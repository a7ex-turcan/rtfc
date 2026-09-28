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
    public const string Queued = "queued";
}

public sealed record SendResult(
    string Status,
    string? MessageId = null,
    string[]? To = null,
    string[]? Unreachable = null,
    string? Person = null,
    string? Requested = null,
    string[]? Online = null,
    string? Reason = null,
    DateTimeOffset? ExpiresAt = null,
    string? Project = null)
{
    public static SendResult Rejected(string reason) => new(SendStatus.Rejected, Reason: reason);
}

/// <summary>
/// How to send (spec §7.2, §7.6). <see cref="Project"/> names one of the recipient's projects; <see cref="FromDirectory"/> is
/// the sending session's directory, which decides where an answer lands on this side.
/// </summary>
public sealed record SendOptions(bool Leave = false, string? Project = null, string? FromDirectory = null);

public sealed record DeviceView(string Name, string DeviceId, string Status, bool? Online, string[] Hints);

public sealed record ContactView(
    string Handle,
    string PersonId,
    string Fingerprint,
    string Status,
    string InboundMode,
    string? AutoScope,
    bool ReadReceipts,
    int Pending,
    DateTimeOffset? AcceptedAt,
    DeviceView[] Devices);

/// <summary>Something you sent, as far as this device knows: queued, delivered, read, or expired.</summary>
public sealed record SentSummary(
    string Id,
    string State,
    string Origin,
    DateTimeOffset SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? ExpiresAt);

public sealed record OutboxView(
    string Id,
    string Kind,
    string To,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int Attempts,
    string Preview);

public sealed record InboxSummary(
    string Id,
    string From,
    string FromDevice,
    string State,
    string Preview,
    DateTimeOffset ReceivedAt,
    string? ReplyTo,
    string Origin,
    string? Note = null,
    string? ReplyState = null,
    string Kind = "person",
    string? Project = null);

/// <summary>What <c>inbox_list</c> shows a session (spec §7.6): its messages, and a count of what is parked in other projects.</summary>
public sealed record InboxListing(InboxSummary[] Messages, ProjectCount[] Elsewhere);

public sealed record ProjectCount(string Project, int Parked, string[] From);

/// <summary>A registered project, as the session that registered it sees it.</summary>
public sealed record ProjectView(string Name, string Root);

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
    string Body,
    string? Note = null,
    string? Draft = null,
    SentSummary[]? YourReplies = null,
    string Kind = "person",
    string? Project = null);

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

public sealed record StatusGlobal(int Parked, string[] From, int Pending = 0);

/// <summary>One project's line in <c>status.json</c>: messages addressed to it (spec §7.6) and, from Phase 8, its source items.</summary>
public sealed record ProjectStatus(string Name, int Parked, string[] From, int Reviews = 0, int Tickets = 0, int PendingSubscriptions = 0);

/// <summary>The contents of <c>status.json</c> (spec §11). <c>Projects</c> is keyed by the project's normalized root (<see cref="ProjectPaths.Key"/>).</summary>
public sealed record StatusSnapshot(StatusGlobal Global, Dictionary<string, ProjectStatus> Projects, bool Away);

/// <summary>The payload behind <c>rtfc1_</c> (spec §5.1): a fingerprint and hints, never certificates, so it stays short enough to paste.</summary>
public sealed record InviteTokenPayload(int V, string Person, string Handle, string[] Hints, string Nonce, DateTimeOffset ExpiresAt);

public static class ManagementStatus
{
    public const string Ok = "ok";
    public const string NotAContact = "not_a_contact";
    public const string Invalid = "invalid";
}

/// <summary>The outcome of a CLI-only management action (spec §9.3).</summary>
public sealed record ManagementResult(string Status, string? Handle = null, string? Reason = null);

/// <summary>The caps of spec §7.4. Counted from the database, so they survive a daemon restart.</summary>
public sealed record AutoAnswerConfig(
    int PerContactPerHour = 10,
    int GlobalPerHour = 30,
    int InboundPerDevicePerHour = 120,
    int TimeoutSeconds = 180,
    double MaxBudgetUsd = 0.5);

/// <summary>The outbox (spec §7.2) and retention (spec §13), in units people write in config files.</summary>
public sealed record OutboxConfig(int ExpiryHours = 168, int PumpIntervalSeconds = 30, int RetentionDays = 30)
{
    public OutboxSettings ToSettings() => new(TimeSpan.FromHours(ExpiryHours), TimeSpan.FromSeconds(PumpIntervalSeconds), TimeSpan.FromDays(RetentionDays));
}

/// <summary>The same, as the node consumes it.</summary>
public sealed record OutboxSettings(TimeSpan Expiry, TimeSpan PumpInterval, TimeSpan Retention);

/// <summary>Per-device settings in <c>config.json</c>. <c>ClaudePath</c> defaults to <c>claude</c> on PATH.</summary>
public sealed record RtfcConfig(int Port, string[]? HintHosts, string? ClaudePath = null, AutoAnswerConfig? AutoAnswer = null, OutboxConfig? Outbox = null)
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
[JsonSerializable(typeof(InboxListing))]
[JsonSerializable(typeof(InboxOpened))]
[JsonSerializable(typeof(OutboxView[]))]
[JsonSerializable(typeof(InviteResult))]
[JsonSerializable(typeof(AcceptResult))]
[JsonSerializable(typeof(ManagementResult))]
[JsonSerializable(typeof(StatusSnapshot))]
[JsonSerializable(typeof(InviteTokenPayload))]
[JsonSerializable(typeof(RtfcConfig))]
public sealed partial class CoreJson : JsonSerializerContext;
