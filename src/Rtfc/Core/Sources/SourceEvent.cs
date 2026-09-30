using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Rtfc.Core.Sources;

/// <summary>The normalized event vocabulary of spec §10.3. Each adapter documents which of these it produces.</summary>
public static class SourceEventType
{
    public const string Assigned = "assigned";
    public const string Mentioned = "mentioned";
    public const string ReviewRequested = "review_requested";
    public const string CommentOnMine = "comment_on_mine";
    public const string ReplyToMe = "reply_to_me";
    public const string StatusChanged = "status_changed";
    public const string Approved = "approved";
    public const string ChangesRequested = "changes_requested";
    public const string Merged = "merged";
    public const string BuildFailedOnMine = "build_failed_on_mine";

    public static readonly string[] All =
    [
        Assigned, Mentioned, ReviewRequested, CommentOnMine, ReplyToMe, StatusChanged, Approved, ChangesRequested, Merged, BuildFailedOnMine,
    ];

    public static bool IsKnown(string type) => All.Contains(type, StringComparer.Ordinal);
}

/// <summary>
/// One thing that happened at a source (spec §10.1). <c>ExternalId</c> is stable per event and dedupes across polls;
/// <c>EntityKey</c> (<c>jira:PAY-123</c>) is the ticket, pull request or page it belongs to, which is the inbox item.
/// Everything a person typed at the source (actor, title, summary) is untrusted and only ever shown inside the wrapper.
/// </summary>
public sealed record SourceEvent(
    string ExternalId,
    string EntityKey,
    string EventType,
    string Actor,
    string Title,
    string Summary,
    string Url,
    DateTimeOffset OccurredAt);

/// <summary>Where a poll left off: the last instant seen, and the ids already reported near it, so the overlap between polls reports nothing twice.</summary>
public sealed record SourceCursor(DateTimeOffset? Watermark, string[] BoundaryIds)
{
    public static readonly SourceCursor Empty = new(null, []);
}

public sealed record PollResult(IReadOnlyList<SourceEvent> Events, SourceCursor Next, TimeSpan? RetryAfter = null);

/// <summary>An account with its token in hand, for one call. Built by the daemon from the row and the token file; never stored or logged.</summary>
public sealed record SourceAccount(string Name, string Type, string BaseUrl, string Login, string? AccountId, string Token);

/// <summary>What a service says the token's user is.</summary>
public sealed record SourceIdentity(string AccountId, string DisplayName, string? TimeZone);

/// <summary>
/// A service said no, or not now. <c>Permanent</c> is a refused token or a broken selector, which polling again would
/// not fix; anything else is retried after <c>RetryAfter</c> or the back-off.
/// </summary>
public sealed class SourceException(string message, bool permanent = false, TimeSpan? retryAfter = null) : Exception(message)
{
    public bool Permanent { get; } = permanent;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>What one adapter needs from the daemon: read the service, report what changed, and say who the token belongs to.</summary>
public interface ISourceAdapter
{
    /// <summary><c>jira</c>, <c>confluence</c>, <c>bitbucket</c>, <c>github</c>: the account type it serves.</summary>
    string Type { get; }

    /// <summary>The token's user, checked when the account is added and used to recognize the user's own actions.</summary>
    Task<SourceIdentity> IdentifyAsync(SourceAccount account, CancellationToken cancellationToken);

    /// <summary>Everything that happened since <paramref name="cursor"/> for the subscription's <paramref name="selector"/>, oldest first, and the cursor to continue from.</summary>
    Task<PollResult> PollAsync(SourceAccount account, JsonObject selector, SourceCursor cursor, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Event histories in <c>inbox.events</c> and the boundary ids of a cursor. camelCase, because people will look at the column.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SourceEvent[]))]
[JsonSerializable(typeof(string[]))]
public sealed partial class SourceJson : JsonSerializerContext;
