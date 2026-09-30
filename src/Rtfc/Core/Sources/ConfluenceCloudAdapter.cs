using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rtfc.Core.Sources;

/// <summary>
/// Confluence Cloud (spec §10.1), read-only. One poll is a CQL search for comments by other people since the cursor, expanded
/// with their author, their page and its creator, their parent comments and their body; a comment becomes <c>mentioned</c>
/// when its body mentions the user, <c>reply_to_me</c> when a parent comment is the user's, and <c>comment_on_mine</c> when it
/// sits on a page the user created or watches. Items coalesce per page. Checked against a real site on 2026-09-30: v1
/// <c>/wiki/rest/api/search?cql=</c> with those expansions, <c>mention = currentUser()</c>, <c>watcher = currentUser()</c>,
/// <c>now("-Nm")</c> and the <c>_links.next</c> cursor; v2 has no search.
/// </summary>
public sealed partial class ConfluenceCloudAdapter(HttpClient http) : ISourceAdapter
{
    public const string TypeName = "confluence";

    public static readonly TimeSpan FirstPollLookback = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromDays(7);
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private const int PageSize = 25;
    private const int MaxPages = 5;
    private const int MaxWatched = 200;
    private const int SummaryLength = 300;

    public string Type => TypeName;

    public async Task<SourceIdentity> IdentifyAsync(SourceAccount account, CancellationToken cancellationToken)
    {
        var me = await GetAsync(account, "/wiki/rest/api/user/current", cancellationToken).ConfigureAwait(false);
        var accountId = me["accountId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(accountId))
        {
            throw new SourceException("Confluence answered without an account id; is the URL a Confluence Cloud site?", permanent: true);
        }

        return new SourceIdentity(accountId, me["displayName"]?.GetValue<string>() ?? account.Login, TimeZone: null);
    }

    public async Task<PollResult> PollAsync(SourceAccount account, JsonObject selector, SourceCursor cursor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var me = account.AccountId ?? (await IdentifyAsync(account, cancellationToken).ConfigureAwait(false)).AccountId;
        var since = cursor.Watermark ?? now - FirstPollLookback;
        if (now - since > MaxCatchUp)
        {
            since = now - MaxCatchUp;
        }

        var scope = Scope(selector);
        var minutes = (int)Math.Ceiling((now - since + Overlap).TotalMinutes);
        var cql = $"type = comment AND creator != currentUser() AND lastmodified >= now(\"-{minutes.ToString(CultureInfo.InvariantCulture)}m\"){scope} ORDER BY lastmodified ASC";
        var watched = await WatchedPagesAsync(account, scope, cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<string>(cursor.BoundaryIds, StringComparer.Ordinal);
        var events = new List<SourceEvent>();
        var candidates = new List<(string Id, DateTimeOffset At)>();
        var latest = since;
        var next = "/wiki/rest/api/search?cql=" + Uri.EscapeDataString(cql) + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture)
            + "&expand=" + Uri.EscapeDataString("content.history.createdBy,content.container.history.createdBy,content.ancestors.history.createdBy,content.body.storage");
        for (var page = 0; page < MaxPages && next is not null; page++)
        {
            var result = await GetAsync(account, next, cancellationToken).ConfigureAwait(false);
            foreach (var entry in result["results"] as JsonArray ?? [])
            {
                if (entry is not JsonObject r || r["content"] is not JsonObject c || c["id"]?.GetValue<string>() is not { Length: > 0 } commentId)
                {
                    continue;
                }

                var at = ParseTime(r["lastModified"]?.GetValue<string>());
                if (at is null || at <= since)
                {
                    continue;
                }

                if (at > latest)
                {
                    latest = at.Value;
                }

                var container = c["container"] as JsonObject;
                var pageId = container?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(pageId))
                {
                    continue;
                }

                var externalId = $"confluence:{pageId}:c:{commentId}";
                candidates.Add((externalId, at.Value));
                var author = (c["history"] as JsonObject)?["createdBy"] as JsonObject;
                if (author?["accountId"]?.GetValue<string>() == me || !seen.Add(externalId))
                {
                    continue;
                }

                var actor = author?["displayName"]?.GetValue<string>() ?? "Someone";
                var pageTitle = container?["title"]?.GetValue<string>() ?? r["title"]?.GetValue<string>() ?? pageId;
                var body = ((c["body"] as JsonObject)?["storage"] as JsonObject)?["value"]?.GetValue<string>() ?? "";
                var pageCreator = ((container?["history"] as JsonObject)?["createdBy"] as JsonObject)?["accountId"]?.GetValue<string>();
                var repliesToMe = (c["ancestors"] as JsonArray ?? []).OfType<JsonObject>()
                    .Any(a => ((a["history"] as JsonObject)?["createdBy"] as JsonObject)?["accountId"]?.GetValue<string>() == me);
                var excerpt = Excerpt(r["excerpt"]?.GetValue<string>() ?? "");
                var link = account.BaseUrl.TrimEnd('/') + "/wiki" + (r["url"]?.GetValue<string>() ?? $"/pages/{pageId}");

                string type, summary;
                if (Mentions(body, me))
                {
                    (type, summary) = (SourceEventType.Mentioned, $"{actor} mentioned you in a comment on \"{pageTitle}\": {excerpt}");
                }
                else if (repliesToMe)
                {
                    (type, summary) = (SourceEventType.ReplyToMe, $"{actor} replied to your comment on \"{pageTitle}\": {excerpt}");
                }
                else if (pageCreator == me || watched.Contains(pageId))
                {
                    (type, summary) = (SourceEventType.CommentOnMine, $"{actor} commented on \"{pageTitle}\": {excerpt}");
                }
                else
                {
                    continue;
                }

                events.Add(new SourceEvent(externalId, $"confluence:{pageId}", type, actor, pageTitle, summary, link, at.Value));
            }

            var more = (result["_links"] as JsonObject)?["next"]?.GetValue<string>();
            next = string.IsNullOrEmpty(more) ? null : (more.StartsWith("/wiki", StringComparison.Ordinal) ? more : "/wiki" + more);
        }

        events.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt));
        var watermark = latest > since ? latest : now - TimeSpan.FromMinutes(1);
        var boundary = candidates.Where(c => c.At >= watermark - Overlap).Select(c => c.Id).Distinct(StringComparer.Ordinal).Take(500).ToArray();
        return new PollResult(events, new SourceCursor(watermark, boundary));
    }

    /// <summary>The subscription's <c>space</c> (a key) and <c>cql</c> (any extra clause), as an AND suffix.</summary>
    private static string Scope(JsonObject selector)
    {
        var scope = "";
        if (selector["space"]?.GetValue<string>() is { Length: > 0 } space)
        {
            if (!space.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '~' or '.'))
            {
                throw new SourceException($"The Confluence space key \"{space}\" has characters a key cannot have.", permanent: true);
            }

            scope += $" AND space = \"{space}\"";
        }

        if (selector["cql"]?.GetValue<string>() is { Length: > 0 } extra)
        {
            scope += $" AND ({extra})";
        }

        return scope;
    }

    /// <summary>The pages the user watches within the scope, so comments on them count as theirs. Capped; a bigger watch list means the rest are missed.</summary>
    private async Task<HashSet<string>> WatchedPagesAsync(SourceAccount account, string scope, CancellationToken cancellationToken)
    {
        var watched = new HashSet<string>(StringComparer.Ordinal);
        var cql = $"type IN (page, blogpost) AND watcher = currentUser(){scope}";
        var next = "/wiki/rest/api/search?cql=" + Uri.EscapeDataString(cql) + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture);
        while (next is not null && watched.Count < MaxWatched)
        {
            var result = await GetAsync(account, next, cancellationToken).ConfigureAwait(false);
            foreach (var entry in result["results"] as JsonArray ?? [])
            {
                if ((entry as JsonObject)?["content"] is JsonObject c && c["id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    watched.Add(id);
                }
            }

            var more = (result["_links"] as JsonObject)?["next"]?.GetValue<string>();
            next = string.IsNullOrEmpty(more) ? null : (more.StartsWith("/wiki", StringComparison.Ordinal) ? more : "/wiki" + more);
        }

        return watched;
    }

    /// <summary>A mention in storage format is <c>&lt;ri:user ri:account-id="…"/&gt;</c>.</summary>
    public static bool Mentions(string storageBody, string accountId) =>
        storageBody.Contains($"ri:account-id=\"{accountId}\"", StringComparison.Ordinal);

    /// <summary>The search excerpt without its highlight markers and its line breaks, cut to a summary's length.</summary>
    public static string Excerpt(string excerpt)
    {
        var text = HighlightMarkers().Replace(excerpt, "");
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= SummaryLength ? text : text[..(SummaryLength - 3)] + "...";
    }

    public static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

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
            throw new SourceException($"Confluence at {account.BaseUrl} did not answer: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceException($"Confluence at {account.BaseUrl} timed out.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new SourceException($"Confluence refused the token for {account.Name} ({(int)response.StatusCode}). It may have expired; run `rtfc account add {account.Name}` again.", permanent: true);
                case HttpStatusCode.TooManyRequests:
                    throw new SourceException("Confluence is rate-limiting; backing off.", retryAfter: response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(2));
                case HttpStatusCode.BadRequest or HttpStatusCode.Gone:
                    throw new SourceException($"Confluence rejected the request ({(int)response.StatusCode}): {await ErrorTextAsync(response, cancellationToken).ConfigureAwait(false)}", permanent: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceException($"Confluence answered {(int)response.StatusCode}: {await ErrorTextAsync(response, cancellationToken).ConfigureAwait(false)}",
                    retryAfter: response.Headers.RetryAfter?.Delta);
            }

            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonNode.Parse(body) as JsonObject ?? throw new SourceException("Confluence answered with something other than a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new SourceException($"Confluence answered with invalid JSON: {ex.Message}");
            }
        }
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject o && o["message"]?.GetValue<string>() is { Length: > 0 } message)
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

    [GeneratedRegex("@@@(end)?hl@@@")]
    private static partial Regex HighlightMarkers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
