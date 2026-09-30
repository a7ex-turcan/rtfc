using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rtfc.Core.Sources;

/// <summary>
/// Jira Cloud (spec §10.1), read-only. One poll is a JQL search for the subscription's issues that changed since the
/// cursor, then each issue's changelog and comments to say what happened and who did it. Produces <c>assigned</c>,
/// <c>status_changed</c>, <c>comment_on_mine</c> and <c>mentioned</c>; the user's own actions are dropped. Checked against
/// a real site on 2026-09-30: <c>/rest/api/3/search/jql</c> pages by token and the old <c>/search</c> is gone (410).
/// </summary>
public sealed class JiraCloudAdapter(HttpClient http) : ISourceAdapter
{
    public const string TypeName = "jira";

    /// <summary>A new subscription starts from a little before now rather than from the beginning of time.</summary>
    public static readonly TimeSpan FirstPollLookback = TimeSpan.FromMinutes(10);

    /// <summary>After a long absence the daemon catches up this far and no further (spec §10.1).</summary>
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromDays(7);

    /// <summary>Polls overlap by this much, and the cursor's boundary ids cover it, because JQL dates have minute granularity and indexing lags.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private const int PageSize = 50;
    private const int MaxPages = 5;
    private const int CommentsPerIssue = 20;
    private const int SummaryLength = 300;

    public string Type => TypeName;

    public async Task<SourceIdentity> IdentifyAsync(SourceAccount account, CancellationToken cancellationToken)
    {
        var me = await GetAsync(account, "/rest/api/3/myself", cancellationToken).ConfigureAwait(false);
        var accountId = me["accountId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(accountId))
        {
            throw new SourceException("Jira answered without an account id; is the URL a Jira Cloud site?", permanent: true);
        }

        return new SourceIdentity(accountId, me["displayName"]?.GetValue<string>() ?? account.Login, me["timeZone"]?.GetValue<string>());
    }

    public async Task<PollResult> PollAsync(SourceAccount account, JsonObject selector, SourceCursor cursor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var jql = selector["jql"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(jql))
        {
            throw new SourceException("A Jira subscription needs a \"jql\" selector, e.g. \"project = PAY AND assignee = currentUser()\".", permanent: true);
        }

        var me = account.AccountId ?? (await IdentifyAsync(account, cancellationToken).ConfigureAwait(false)).AccountId;
        var since = cursor.Watermark ?? now - FirstPollLookback;
        if (now - since > MaxCatchUp)
        {
            since = now - MaxCatchUp;
        }

        // Relative JQL dates need no time zone; the overlap and the boundary ids make the window safe to widen.
        var minutes = (int)Math.Ceiling((now - since + Overlap).TotalMinutes);
        var query = $"({jql}) AND updated >= \"-{minutes.ToString(CultureInfo.InvariantCulture)}m\" ORDER BY updated ASC";
        var seen = new HashSet<string>(cursor.BoundaryIds, StringComparer.Ordinal);
        var events = new List<SourceEvent>();
        var candidates = new List<(string Id, DateTimeOffset At)>();
        var latest = since;
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = "/rest/api/3/search/jql?jql=" + Uri.EscapeDataString(query)
                + "&fields=summary,status,assignee,reporter,updated&expand=changelog&maxResults=" + PageSize.ToString(CultureInfo.InvariantCulture)
                + (pageToken is null ? "" : "&nextPageToken=" + Uri.EscapeDataString(pageToken));
            var result = await GetAsync(account, url, cancellationToken).ConfigureAwait(false);
            foreach (var issue in result["issues"] as JsonArray ?? [])
            {
                if (issue is not JsonObject i || i["key"]?.GetValue<string>() is not { Length: > 0 } key || i["fields"] is not JsonObject fields)
                {
                    continue;
                }

                var updated = ParseTime(fields["updated"]?.GetValue<string>());
                if (updated is { } u && u > latest)
                {
                    latest = u;
                }

                var title = $"{key} {fields["summary"]?.GetValue<string>() ?? ""}".Trim();
                var link = $"{account.BaseUrl.TrimEnd('/')}/browse/{key}";
                ChangelogEvents(i["changelog"] as JsonObject, key, title, link, me, since, seen, events, candidates);
                var comments = await GetAsync(account,
                    $"/rest/api/3/issue/{Uri.EscapeDataString(key)}/comment?orderBy=-created&maxResults={CommentsPerIssue.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken).ConfigureAwait(false);
                CommentEvents(comments["comments"] as JsonArray, key, title, link, me, since, seen, events, candidates);
            }

            pageToken = result["nextPageToken"]?.GetValue<string>();
            if (result["isLast"]?.GetValue<bool>() ?? pageToken is null)
            {
                break;
            }
        }

        events.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt));
        foreach (var e in events)
        {
            if (e.OccurredAt > latest)
            {
                latest = e.OccurredAt;
            }
        }

        // Nothing changed: move on anyway, so the window does not grow until the cap. The overlap covers late indexing.
        var watermark = latest > since ? latest : now - TimeSpan.FromMinutes(1);
        var boundary = candidates.Where(c => c.At >= watermark - Overlap).Select(c => c.Id).Distinct(StringComparer.Ordinal).Take(500).ToArray();
        return new PollResult(events, new SourceCursor(watermark, boundary));
    }

    private static void ChangelogEvents(
        JsonObject? changelog, string key, string title, string link, string me, DateTimeOffset since, HashSet<string> seen, List<SourceEvent> events,
        List<(string Id, DateTimeOffset At)> candidates)
    {
        foreach (var history in changelog?["histories"] as JsonArray ?? [])
        {
            if (history is not JsonObject h || ParseTime(h["created"]?.GetValue<string>()) is not { } created || created <= since)
            {
                continue;
            }

            var author = h["author"] as JsonObject;
            var actorId = author?["accountId"]?.GetValue<string>();
            var actor = author?["displayName"]?.GetValue<string>() ?? "Someone";
            var externalId = $"jira:{key}:cl:{h["id"]?.GetValue<string>() ?? created.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            candidates.Add((externalId, created));
            if (actorId == me || !seen.Add(externalId))
            {
                continue;
            }

            foreach (var item in h["items"] as JsonArray ?? [])
            {
                if (item is not JsonObject change)
                {
                    continue;
                }

                var field = change["fieldId"]?.GetValue<string>() ?? change["field"]?.GetValue<string>()?.ToLowerInvariant();
                switch (field)
                {
                    case "assignee" when change["to"]?.GetValue<string>() == me:
                        events.Add(new SourceEvent(externalId, $"jira:{key}", SourceEventType.Assigned, actor, title, $"{actor} assigned it to you", link, created));
                        break;
                    case "status":
                        events.Add(new SourceEvent(externalId, $"jira:{key}", SourceEventType.StatusChanged, actor, title,
                            $"{actor} moved it from \"{change["fromString"]?.GetValue<string>() ?? "?"}\" to \"{change["toString"]?.GetValue<string>() ?? "?"}\"", link, created));
                        break;
                }
            }
        }
    }

    private static void CommentEvents(
        JsonArray? comments, string key, string title, string link, string me, DateTimeOffset since, HashSet<string> seen, List<SourceEvent> events,
        List<(string Id, DateTimeOffset At)> candidates)
    {
        foreach (var comment in comments ?? [])
        {
            if (comment is not JsonObject c || ParseTime(c["created"]?.GetValue<string>()) is not { } created || created <= since)
            {
                continue;
            }

            var author = c["author"] as JsonObject;
            var actorId = author?["accountId"]?.GetValue<string>();
            var actor = author?["displayName"]?.GetValue<string>() ?? "Someone";
            var externalId = $"jira:{key}:c:{c["id"]?.GetValue<string>() ?? created.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            candidates.Add((externalId, created));
            if (actorId == me || !seen.Add(externalId))
            {
                continue;
            }

            var text = Shorten(AdfText(c["body"]));
            var mentioned = Mentions(c["body"], me);
            events.Add(new SourceEvent(externalId, $"jira:{key}", mentioned ? SourceEventType.Mentioned : SourceEventType.CommentOnMine, actor, title,
                mentioned ? $"{actor} mentioned you: {text}" : $"{actor} commented: {text}", link, created));
        }
    }

    /// <summary>The plain text of an Atlassian Document Format body: text nodes, mentions as their display text, paragraphs on their own lines.</summary>
    public static string AdfText(JsonNode? node)
    {
        var text = new StringBuilder();
        Walk(node, text);
        return text.ToString().Trim();

        static void Walk(JsonNode? n, StringBuilder text)
        {
            if (n is JsonArray array)
            {
                foreach (var child in array)
                {
                    Walk(child, text);
                }

                return;
            }

            if (n is not JsonObject o)
            {
                return;
            }

            switch (o["type"]?.GetValue<string>())
            {
                case "text":
                    text.Append(o["text"]?.GetValue<string>());
                    return;
                case "mention":
                case "emoji":
                    text.Append((o["attrs"] as JsonObject)?["text"]?.GetValue<string>());
                    return;
                case "hardBreak":
                    text.Append('\n');
                    return;
                case "inlineCard":
                    text.Append((o["attrs"] as JsonObject)?["url"]?.GetValue<string>());
                    return;
            }

            var before = text.Length;
            Walk(o["content"], text);
            if (o["type"]?.GetValue<string>() is "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote" && text.Length > before)
            {
                text.Append('\n');
            }
        }
    }

    /// <summary>Whether an ADF body mentions the account: a <c>mention</c> node whose <c>attrs.id</c> is the account id.</summary>
    public static bool Mentions(JsonNode? node, string accountId) => node switch
    {
        JsonArray array => array.Any(child => Mentions(child, accountId)),
        JsonObject o => (o["type"]?.GetValue<string>() == "mention" && (o["attrs"] as JsonObject)?["id"]?.GetValue<string>() == accountId) || Mentions(o["content"], accountId),
        _ => false,
    };

    /// <summary>Jira writes offsets without a colon (<c>+0400</c>), which <see cref="DateTimeOffset"/> does not read; everything else is ISO 8601.</summary>
    public static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var text = value;
        if (text.Length > 5 && text[^5] is '+' or '-' && text[^4..].All(char.IsAsciiDigit))
        {
            text = text[..^2] + ":" + text[^2..];
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= SummaryLength ? line : line[..(SummaryLength - 3)] + "...";
    }

    private async Task<JsonObject> GetAsync(SourceAccount account, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, account.BaseUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Login}:{account.Token}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException($"Jira at {account.BaseUrl} did not answer: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceException($"Jira at {account.BaseUrl} timed out.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new SourceException($"Jira refused the token for {account.Name} ({(int)response.StatusCode}). It may have expired; run `rtfc account add {account.Name}` again.", permanent: true);
                case HttpStatusCode.TooManyRequests:
                    throw new SourceException("Jira is rate-limiting; backing off.", retryAfter: response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(2));
                case HttpStatusCode.BadRequest or HttpStatusCode.Gone:
                    throw new SourceException($"Jira rejected the request ({(int)response.StatusCode}): {await ErrorTextAsync(response, cancellationToken).ConfigureAwait(false)}", permanent: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceException($"Jira answered {(int)response.StatusCode}: {await ErrorTextAsync(response, cancellationToken).ConfigureAwait(false)}",
                    retryAfter: response.Headers.RetryAfter?.Delta);
            }

            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonNode.Parse(body) as JsonObject ?? throw new SourceException("Jira answered with something other than a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new SourceException($"Jira answered with invalid JSON: {ex.Message}");
            }
        }
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject o && o["errorMessages"] is JsonArray messages && messages.Count > 0)
            {
                return string.Join("; ", messages.Select(m => m?.GetValue<string>()));
            }

            return body.Length <= 200 ? body : body[..200];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return response.ReasonPhrase ?? "";
        }
    }
}
