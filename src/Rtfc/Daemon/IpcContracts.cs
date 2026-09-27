using System.Text.Json.Serialization;
using Rtfc.Core;

namespace Rtfc.Daemon;

/// <summary>
/// The local API on the Unix socket (spec §3.1). JSON over HTTP so it can be poked with
/// <c>curl --unix-socket ~/.claude/rtfc/rtfcd.sock http://rtfcd/v1/status</c>.
/// </summary>
public static class IpcRoutes
{
    public const string Status = "/v1/status";
    public const string Lease = "/v1/lease";
    public const string Contacts = "/v1/contacts";
    public const string Send = "/v1/send";
    public const string Inbox = "/v1/inbox";
    public const string Invite = "/v1/invite";
    public const string Accept = "/v1/accept";
    public const string Outbox = "/v1/outbox";
    public const string Away = "/v1/away";
    public const string Shutdown = "/v1/shutdown";

    public static string InboxOpen(string id) => $"{Inbox}/{Uri.EscapeDataString(id)}/open";
    public static string InboxReply(string id) => $"{Inbox}/{Uri.EscapeDataString(id)}/reply";
    public static string InboxDismiss(string id) => $"{Inbox}/{Uri.EscapeDataString(id)}/dismiss";

    // Management (spec §9.3). On the user-only socket, reached by the CLI; never an MCP tool.
    public static string ContactAuto(string handle) => $"{Contacts}/{Uri.EscapeDataString(handle)}/auto";
    public static string ContactRemove(string handle) => $"{Contacts}/{Uri.EscapeDataString(handle)}/remove";
    public static string ContactBlock(string handle) => $"{Contacts}/{Uri.EscapeDataString(handle)}/block";
    public static string ContactRename(string handle) => $"{Contacts}/{Uri.EscapeDataString(handle)}/rename";
    public static string ContactReceipts(string handle) => $"{Contacts}/{Uri.EscapeDataString(handle)}/receipts";
}

public sealed record DaemonStatus(
    string Version,
    int Pid,
    string PersonId,
    string Handle,
    string DeviceId,
    string DeviceName,
    int Port,
    string[] Hints,
    int Leases,
    bool IdleExit,
    bool Away = false);

public sealed record SendRequest(string To, string Text, bool Leave = false);

public sealed record ToggleRequest(bool On);

public sealed record RenameRequest(string Handle);

public sealed record ReplyRequest(string Text);

public sealed record AcceptRequest(string Token);

public sealed record AutoRequest(string Mode, string? Scope);

public sealed record IpcError(string Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(DaemonStatus))]
[JsonSerializable(typeof(SendRequest))]
[JsonSerializable(typeof(ReplyRequest))]
[JsonSerializable(typeof(AcceptRequest))]
[JsonSerializable(typeof(AutoRequest))]
[JsonSerializable(typeof(ToggleRequest))]
[JsonSerializable(typeof(RenameRequest))]
[JsonSerializable(typeof(ManagementResult))]
[JsonSerializable(typeof(OutboxView[]))]
[JsonSerializable(typeof(IpcError))]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(ContactView[]))]
[JsonSerializable(typeof(InboxSummary[]))]
[JsonSerializable(typeof(InboxOpened))]
[JsonSerializable(typeof(InviteResult))]
[JsonSerializable(typeof(AcceptResult))]
public sealed partial class IpcJson : JsonSerializerContext;
