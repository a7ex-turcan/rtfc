using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rtfc.Core.Sources;

/// <summary>
/// Bitbucket Cloud (spec §10.1), read-only, one repository per subscription: the user-wide pull-request endpoints were removed
/// in 2025 and 2026. One poll lists the repository's pull requests updated since the cursor, keeps the ones the user authored
/// or reviews, and reads their activity: a new request where the user is a reviewer is <c>review_requested</c>; on the user's
/// own requests, other people's comments, approvals, requested changes, a merge, a decline and failed builds become
/// <c>comment_on_mine</c>, <c>approved</c>, <c>changes_requested</c>, <c>merged</c>, <c>status_changed</c> and
/// <c>build_failed_on_mine</c>; replies to the user's comments are <c>reply_to_me</c> and comments naming them <c>mentioned</c>.
/// Checked against a real workspace on 2026-09-30: the repository list, <c>q=updated_on &gt;= …</c>, the activity shapes
/// (<c>comment</c>, <c>approval</c>, <c>update</c>), comments with <c>q</c> and <c>sort</c>, and statuses. Needs a scoped
/// API token; the mention markup inside a comment was not seen in the wild and is matched permissively.
/// </summary>
public sealed class BitbucketCloudAdapter(HttpClient http) : ISourceAdapter
{
    public const string TypeName = "bitbucket";
    public const string ApiBase = "https://api.bitbucket.org";

    public static readonly TimeSpan FirstPollLookback = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromDays(7);
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private const int PageSize = 50;
    private const int MaxPages = 5;
    private const int SummaryLength = 300;

    private readonly ConcurrentDictionary<string, string> _uuids = new(StringComparer.Ordinal);

    public string Type => TypeName;

    public async Task<SourceIdentity> IdentifyAsync(SourceAccount account, CancellationToken cancellationToken)
    {
        var me = await GetAsync(account, "/2.0/user", cancellationToken).ConfigureAwait(false);
        var accountId = me["account_id"]?.GetValue<string>();
        var uuid = me["uuid"]?.GetValue<string>();
        if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(uuid))
        {
            throw new SourceException("Bitbucket answered without an account id; is the token a scoped Atlassian API token for the Bitbucket app?", permanent: true);
        }

        _uuids[account.Name] = uuid;
        return new SourceIdentity(accountId, me["display_name"]?.GetValue<string>() ?? account.Login, TimeZone: null);
    }

    public async Task<PollResult> PollAsync(SourceAccount account, JsonObject selector, SourceCursor cursor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (workspace, slug) = Repository(selector);
        var me = account.AccountId;
        if (me is null || !_uuids.TryGetValue(account.Name, out var uuid))
        {
            var identity = await IdentifyAsync(account, cancellationToken).ConfigureAwait(false);
            me = identity.AccountId;
            uuid = _uuids[account.Name];
        }

        var since = cursor.Watermark ?? now - FirstPollLookback;
        if (now - since > MaxCatchUp)
        {
            since = now - MaxCatchUp;
        }

        var window = (since - Overlap).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);
        var seen = new HashSet<string>(cursor.BoundaryIds, StringComparer.Ordinal);
        var events = new List<SourceEvent>();
        var candidates = new List<(string Id, DateTimeOffset At)>();
        var latest = since;
        var repo = $"/2.0/repositories/{Uri.EscapeDataString(workspace)}/{Uri.EscapeDataString(slug)}";
        var next = $"{repo}/pullrequests?state=OPEN&state=MERGED&state=DECLINED&state=SUPERSEDED&sort=updated_on&pagelen={PageSize.ToString(CultureInfo.InvariantCulture)}"
            + "&q=" + Uri.EscapeDataString($"updated_on >= {window}")
            + "&fields=" + Uri.EscapeDataString("values.id,values.title,values.state,values.author,values.participants,values.created_on,values.updated_on,values.links.html.href,next");
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var page = 0; page < MaxPages && next is not null; page++)
        {
            var result = await GetAsync(account, next, cancellationToken).ConfigureAwait(false);
            foreach (var entry in result["values"] as JsonArray ?? [])
            {
                if (entry is not JsonObject pr || pr["id"]?.GetValue<int>() is not { } id)
                {
                    continue;
                }

                var updated = ParseTime(pr["updated_on"]?.GetValue<string>());
                if (updated is { } u && u > latest)
                {
                    latest = u;
                }

                var author = pr["author"] as JsonObject;
                var authorMe = IsMe(author, me, uuid);
                var reviewerMe = (pr["participants"] as JsonArray ?? []).OfType<JsonObject>()
                    .Any(p => p["role"]?.GetValue<string>() == "REVIEWER" && IsMe(p["user"] as JsonObject, me, uuid));
                if (!authorMe && !reviewerMe)
                {
                    continue;
                }

                var key = $"bitbucket:{workspace}/{slug}#{id.ToString(CultureInfo.InvariantCulture)}";
                var title = $"{slug}#{id.ToString(CultureInfo.InvariantCulture)} {pr["title"]?.GetValue<string>() ?? ""}".Trim();
                var link = ((pr["links"] as JsonObject)?["html"] as JsonObject)?["href"]?.GetValue<string>() ?? $"https://bitbucket.org/{workspace}/{slug}/pull-requests/{id}";
                var created = ParseTime(pr["created_on"]?.GetValue<string>());
                if (reviewerMe && !authorMe && created is { } c && c > since)
                {
                    var externalId = $"{key}:new";
                    candidates.Add((externalId, c));
                    if (seen.Add(externalId))
                    {
                        var who = author?["display_name"]?.GetValue<string>() ?? "Someone";
                        events.Add(new SourceEvent(externalId, key, SourceEventType.ReviewRequested, who, title, $"{who} asked you to review it", link, c));
                    }
                }

                var prPath = $"{repo}/pullrequests/{id.ToString(CultureInfo.InvariantCulture)}";
                await ActivityEventsAsync(account, prPath, key, title, link, me, uuid, authorMe, since, seen, events, candidates, parents, cancellationToken).ConfigureAwait(false);
                if (authorMe)
                {
                    await FailedBuildsAsync(account, prPath, key, title, link, window, since, seen, events, candidates, cancellationToken).ConfigureAwait(false);
                }
            }

            next = result["next"]?.GetValue<string>() is { Length: > 0 } more ? more : null;
        }

        events.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt));
        foreach (var e in events)
        {
            if (e.OccurredAt > latest)
            {
                latest = e.OccurredAt;
            }
        }

        var watermark = latest > since ? latest : now - TimeSpan.FromMinutes(1);
        var boundary = candidates.Where(c => c.At >= watermark - Overlap).Select(c => c.Id).Distinct(StringComparer.Ordinal).Take(500).ToArray();
        return new PollResult(events, new SourceCursor(watermark, boundary));
    }

    private async Task ActivityEventsAsync(
        SourceAccount account, string prPath, string key, string title, string link, string me, string uuid, bool authorMe, DateTimeOffset since,
        HashSet<string> seen, List<SourceEvent> events, List<(string Id, DateTimeOffset At)> candidates, Dictionary<string, string?> parents, CancellationToken cancellationToken)
    {
        var activity = await GetAsync(account, $"{prPath}/activity?pagelen={PageSize.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        foreach (var entry in (activity["values"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (entry["comment"] is JsonObject comment)
            {
                var at = ParseTime(comment["created_on"]?.GetValue<string>());
                var user = comment["user"] as JsonObject;
                if (at is null || at <= since || comment["deleted"]?.GetValue<bool>() == true)
                {
                    continue;
                }

                var externalId = $"{key}:c:{comment["id"]?.GetValue<long>().ToString(CultureInfo.InvariantCulture) ?? at.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
                candidates.Add((externalId, at.Value));
                if (IsMe(user, me, uuid) || !seen.Add(externalId))
                {
                    continue;
                }

                var actor = user?["display_name"]?.GetValue<string>() ?? "Someone";
                var content = comment["content"] as JsonObject;
                var text = Shorten(content?["raw"]?.GetValue<string>() ?? "");
                var parentId = (comment["parent"] as JsonObject)?["id"]?.GetValue<long>().ToString(CultureInfo.InvariantCulture);
                string? type = null, summary = null;
                if (Mentions(content, me, uuid))
                {
                    (type, summary) = (SourceEventType.Mentioned, $"{actor} mentioned you: {text}");
                }
                else if (parentId is not null && await ParentIsMineAsync(account, prPath, parentId, me, uuid, parents, cancellationToken).ConfigureAwait(false))
                {
                    (type, summary) = (SourceEventType.ReplyToMe, $"{actor} replied to your comment: {text}");
                }
                else if (authorMe)
                {
                    (type, summary) = (SourceEventType.CommentOnMine, $"{actor} commented: {text}");
                }

                if (type is not null)
                {
                    events.Add(new SourceEvent(externalId, key, type, actor, title, summary!, link, at.Value));
                }

                continue;
            }

            if (entry["approval"] is JsonObject approval)
            {
                AddReaction(approval, "date", SourceEventType.Approved, "approved it", $"{key}:a");
                continue;
            }

            if ((entry["changes_requested"] ?? entry["changes_request"]) is JsonObject changes)
            {
                AddReaction(changes, "date", SourceEventType.ChangesRequested, "requested changes", $"{key}:r");
                continue;
            }

            if (entry["update"] is JsonObject update && authorMe)
            {
                var at = ParseTime(update["date"]?.GetValue<string>());
                var user = update["author"] as JsonObject;
                var state = update["state"]?.GetValue<string>();
                if (at is null || at <= since || IsMe(user, me, uuid) || state is not ("MERGED" or "DECLINED"))
                {
                    continue;
                }

                var externalId = $"{key}:u:{at.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
                candidates.Add((externalId, at.Value));
                if (seen.Add(externalId))
                {
                    var actor = user?["display_name"]?.GetValue<string>() ?? "Someone";
                    events.Add(state == "MERGED"
                        ? new SourceEvent(externalId, key, SourceEventType.Merged, actor, title, $"{actor} merged it", link, at.Value)
                        : new SourceEvent(externalId, key, SourceEventType.StatusChanged, actor, title, $"{actor} declined it", link, at.Value));
                }
            }
        }

        void AddReaction(JsonObject reaction, string dateField, string type, string verb, string idPrefix)
        {
            var at = ParseTime(reaction[dateField]?.GetValue<string>());
            var user = reaction["user"] as JsonObject;
            if (at is null || at <= since || IsMe(user, me, uuid) || !authorMe)
            {
                return;
            }

            var actor = user?["display_name"]?.GetValue<string>() ?? "Someone";
            var externalId = $"{idPrefix}:{user?["uuid"]?.GetValue<string>()?.Trim('{', '}') ?? actor}:{at.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            candidates.Add((externalId, at.Value));
            if (seen.Add(externalId))
            {
                events.Add(new SourceEvent(externalId, key, type, actor, title, $"{actor} {verb}", link, at.Value));
            }
        }
    }

    private async Task FailedBuildsAsync(
        SourceAccount account, string prPath, string key, string title, string link, string window, DateTimeOffset since,
        HashSet<string> seen, List<SourceEvent> events, List<(string Id, DateTimeOffset At)> candidates, CancellationToken cancellationToken)
    {
        var statuses = await GetAsync(account, $"{prPath}/statuses?pagelen=20&q=" + Uri.EscapeDataString($"state=\"FAILED\" AND updated_on >= {window}"), cancellationToken).ConfigureAwait(false);
        foreach (var status in (statuses["values"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var at = ParseTime(status["updated_on"]?.GetValue<string>() ?? status["created_on"]?.GetValue<string>());
            if (at is null || at <= since)
            {
                continue;
            }

            var name = status["name"]?.GetValue<string>() ?? status["key"]?.GetValue<string>() ?? "a build";
            var externalId = $"{key}:s:{status["key"]?.GetValue<string>() ?? name}:{at.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            candidates.Add((externalId, at.Value));
            if (seen.Add(externalId))
            {
                events.Add(new SourceEvent(externalId, key, SourceEventType.BuildFailedOnMine, name, title, $"{name} failed" + (status["description"]?.GetValue<string>() is { Length: > 0 } d ? $": {Shorten(d)}" : ""), link, at.Value));
            }
        }
    }

    private async Task<bool> ParentIsMineAsync(SourceAccount account, string prPath, string parentId, string me, string uuid, Dictionary<string, string?> parents, CancellationToken cancellationToken)
    {
        if (!parents.TryGetValue(parentId, out var parentUuid))
        {
            var parent = await GetAsync(account, $"{prPath}/comments/{Uri.EscapeDataString(parentId)}", cancellationToken).ConfigureAwait(false);
            var user = parent["user"] as JsonObject;
            parentUuid = IsMe(user, me, uuid) ? uuid : user?["uuid"]?.GetValue<string>();
            parents[parentId] = parentUuid;
        }

        return parentUuid == uuid;
    }

    /// <summary>The subscription's <c>repo</c>, as <c>workspace/slug</c>, or a slug with a separate <c>workspace</c>.</summary>
    public static (string Workspace, string Slug) Repository(JsonObject selector)
    {
        var repo = selector["repo"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new SourceException("A Bitbucket subscription needs a \"repo\" selector, e.g. \"acme/payments-api\".", permanent: true);
        }

        var parts = repo.Split('/', 2);
        var workspace = parts.Length == 2 ? parts[0] : selector["workspace"]?.GetValue<string>();
        var slug = parts.Length == 2 ? parts[1] : parts[0];
        if (string.IsNullOrWhiteSpace(workspace) || !IsSlug(workspace) || !IsSlug(slug))
        {
            throw new SourceException($"The Bitbucket repository \"{repo}\" must be workspace/slug in letters, digits, '-', '_' and '.'.", permanent: true);
        }

        return (workspace, slug);
    }

    private static bool IsSlug(string value) => value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>A user object is the account when its Atlassian account id or its Bitbucket uuid matches.</summary>
    private static bool IsMe(JsonObject? user, string accountId, string uuid) =>
        user is not null && (user["account_id"]?.GetValue<string>() == accountId || user["uuid"]?.GetValue<string>() == uuid);

    /// <summary>Permissive, because the markup was not seen live: the raw text names the account id or uuid, or the html carries it.</summary>
    public static bool Mentions(JsonObject? content, string accountId, string uuid)
    {
        var raw = content?["raw"]?.GetValue<string>() ?? "";
        var html = content?["html"]?.GetValue<string>() ?? "";
        var bare = uuid.Trim('{', '}');
        return raw.Contains(accountId, StringComparison.Ordinal) || raw.Contains(bare, StringComparison.OrdinalIgnoreCase)
            || html.Contains(accountId, StringComparison.Ordinal) || html.Contains(bare, StringComparison.OrdinalIgnoreCase);
    }

    public static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= SummaryLength ? line : line[..(SummaryLength - 3)] + "...";
    }

    private async Task<JsonObject> GetAsync(SourceAccount account, string pathOrUrl, CancellationToken cancellationToken)
    {
        var url = pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : Base(account.BaseUrl) + pathOrUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Login}:{account.Token}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException($"Bitbucket did not answer: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceException("Bitbucket timed out.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new SourceException($"Bitbucket refused the token for {account.Name} ({(int)response.StatusCode}). It must be a scoped API token for the Bitbucket app with read scopes; run `rtfc account add {account.Name}` again.", permanent: true);
                case HttpStatusCode.TooManyRequests:
                    throw new SourceException("Bitbucket is rate-limiting; backing off.", retryAfter: response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                case HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Gone:
                    throw new SourceException($"Bitbucket rejected the request ({(int)response.StatusCode}): {await ErrorTextAsync(response, cancellationToken).ConfigureAwait(false)}", permanent: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceException($"Bitbucket answered {(int)response.StatusCode}: {await ErrorTextAsync(response, cancellationToken).ConfigureAwait(false)}", retryAfter: response.Headers.RetryAfter?.Delta);
            }

            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonNode.Parse(body) as JsonObject ?? throw new SourceException("Bitbucket answered with something other than a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new SourceException($"Bitbucket answered with invalid JSON: {ex.Message}");
            }
        }
    }

    /// <summary><c>https://bitbucket.org</c> and <c>https://api.bitbucket.org</c> both mean the Cloud API.</summary>
    private static string Base(string configured)
    {
        var trimmed = configured.TrimEnd('/');
        return trimmed.EndsWith("bitbucket.org", StringComparison.OrdinalIgnoreCase) && !trimmed.Contains("api.", StringComparison.OrdinalIgnoreCase) ? ApiBase : trimmed;
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject o && (o["error"] as JsonObject)?["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return message;
            }

            return body.Length <= 200 ? body : body[..200];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return response.ReasonPhrase ?? "";
        }
    }
}
