using System.Net;
using System.Text.Json.Nodes;
using Rtfc.Core.Sources;

namespace Rtfc.Tests.Core;

/// <summary>The Confluence Cloud adapter against scripted answers shaped like the real ones seen on 2026-09-30.</summary>
public class ConfluenceCloudAdapterTests
{
    private const string Me = "712020:me";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SourceAccount Account = new("confluence-work", "confluence", "https://acme.atlassian.net", "me@acme.com", Me, "t0ken");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Comments_become_mentions_replies_and_comments_on_my_pages_and_the_rest_are_dropped()
    {
        var handler = new ScriptedHttpHandler();
        handler.Reply(Uri.EscapeDataString("watcher = currentUser"), Search(Page("900", "Watched page")));
        handler.Reply(Uri.EscapeDataString("type = comment"), Search(
            Comment("101", "712020:dan", "Dan", "2026-09-30T11:40:00.000Z", page: ("500", "My design", Me), body: $"<p><ac:link><ri:user ri:account-id=\"{Me}\" /></ac:link> look</p>", excerpt: "@@@hl@@@Alex@@@endhl@@@ look"),
            Comment("102", "712020:eve", "Eve", "2026-09-30T11:41:00.000Z", page: ("600", "Their page", "712020:owner"), body: "<p>reply</p>", excerpt: "reply", ancestors: [("100", Me)]),
            Comment("103", "712020:dan", "Dan", "2026-09-30T11:42:00.000Z", page: ("500", "My design", Me), body: "<p>plain</p>", excerpt: "plain\n  text"),
            Comment("104", "712020:dan", "Dan", "2026-09-30T11:43:00.000Z", page: ("900", "Watched page", "712020:owner"), body: "<p>on watched</p>", excerpt: "on watched"),
            Comment("105", "712020:dan", "Dan", "2026-09-30T11:44:00.000Z", page: ("700", "Unrelated", "712020:owner"), body: "<p>nothing to do with me</p>", excerpt: "x"),
            Comment("106", Me, "Me", "2026-09-30T11:45:00.000Z", page: ("500", "My design", Me), body: "<p>mine</p>", excerpt: "mine")));
        var adapter = new ConfluenceCloudAdapter(new HttpClient(handler));
        var since = new DateTimeOffset(2026, 9, 30, 11, 30, 0, TimeSpan.Zero);

        var result = await adapter.PollAsync(Account, new JsonObject(), new SourceCursor(since, []), Now, Ct);

        Assert.Equal(["mentioned", "reply_to_me", "comment_on_mine", "comment_on_mine"], result.Events.Select(e => e.EventType));
        Assert.Equal(["confluence:500", "confluence:600", "confluence:500", "confluence:900"], result.Events.Select(e => e.EntityKey));
        Assert.Equal("Dan mentioned you in a comment on \"My design\": Alex look", result.Events[0].Summary);
        Assert.Equal("Eve replied to your comment on \"Their page\": reply", result.Events[1].Summary);
        Assert.Equal("Dan commented on \"My design\": plain text", result.Events[2].Summary);
        Assert.Equal("Dan commented on \"Watched page\": on watched", result.Events[3].Summary);
        Assert.Equal("https://acme.atlassian.net/wiki/spaces/PR/pages/500/My+design?focusedCommentId=101", result.Events[0].Url);
        Assert.Equal("My design", result.Events[0].Title);
        Assert.Equal("confluence:500:c:101", result.Events[0].ExternalId);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 11, 45, 0, TimeSpan.Zero), result.Next.Watermark);
        Assert.Contains("confluence:700:c:105", result.Next.BoundaryIds);

        var query = handler.Requests.Single(r => r.Contains(Uri.EscapeDataString("type = comment"), StringComparison.Ordinal));
        Assert.Contains(Uri.EscapeDataString("type = comment AND creator != currentUser() AND lastmodified >= now(\"-35m\") ORDER BY lastmodified ASC"), query);
        Assert.Contains("content.ancestors.history.createdBy", Uri.UnescapeDataString(query));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task The_space_and_an_extra_cql_clause_scope_both_queries_and_a_bad_space_key_is_refused()
    {
        var handler = new ScriptedHttpHandler();
        handler.Reply(Uri.EscapeDataString("watcher = currentUser"), Search());
        handler.Reply(Uri.EscapeDataString("type = comment"), Search());
        var adapter = new ConfluenceCloudAdapter(new HttpClient(handler));

        await adapter.PollAsync(Account, new JsonObject { ["space"] = "PR", ["cql"] = "label = adr" }, SourceCursor.Empty, Now, Ct);

        Assert.All(handler.Requests, r => Assert.Contains(Uri.EscapeDataString(" AND space = \"PR\" AND (label = adr)"), r));
        var bad = await Assert.ThrowsAsync<SourceException>(() => adapter.PollAsync(Account, new JsonObject { ["space"] = "PR\" OR x" }, SourceCursor.Empty, Now, Ct));
        Assert.True(bad.Permanent);
    }

    [Fact]
    public async Task Pages_follow_the_next_link_and_the_boundary_stops_repeats()
    {
        var handler = new ScriptedHttpHandler();
        handler.Reply(Uri.EscapeDataString("watcher = currentUser"), Search());
        var first = Search(Comment("101", "712020:dan", "Dan", "2026-09-30T11:40:00.000Z", page: ("500", "My design", Me), body: "", excerpt: "a"));
        first["_links"] = new JsonObject { ["next"] = "/rest/api/search?next=true&cursor=abc" };
        handler.Reply(Uri.EscapeDataString("type = comment"), first, when: url => !url.Contains("cursor=abc", StringComparison.Ordinal));
        handler.Reply("cursor=abc", Search(Comment("102", "712020:dan", "Dan", "2026-09-30T11:50:00.000Z", page: ("500", "My design", Me), body: "", excerpt: "b")));
        var adapter = new ConfluenceCloudAdapter(new HttpClient(handler));

        var result = await adapter.PollAsync(Account, new JsonObject(), new SourceCursor(Now - TimeSpan.FromHours(1), ["confluence:500:c:101"]), Now, Ct);

        Assert.Equal(["confluence:500:c:102"], result.Events.Select(e => e.ExternalId));
        Assert.Contains("/wiki/rest/api/search?next=true&cursor=abc", handler.Requests[^1]);
    }

    [Fact]
    public async Task Errors_are_told_apart_and_identify_returns_the_account_id()
    {
        var refused = new ConfluenceCloudAdapter(new HttpClient(new ScriptedHttpHandler { Status = HttpStatusCode.Forbidden }));
        Assert.True((await Assert.ThrowsAsync<SourceException>(() => refused.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct))).Permanent);
        var limited = new ConfluenceCloudAdapter(new HttpClient(new ScriptedHttpHandler { Status = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 30 }));
        Assert.Equal(TimeSpan.FromSeconds(30), (await Assert.ThrowsAsync<SourceException>(() => limited.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct))).RetryAfter);

        var handler = new ScriptedHttpHandler();
        handler.Reply("/wiki/rest/api/user/current", new JsonObject { ["accountId"] = Me, ["displayName"] = "Alex" });
        var identity = await new ConfluenceCloudAdapter(new HttpClient(handler)).IdentifyAsync(Account, Ct);
        Assert.Equal(new SourceIdentity(Me, "Alex", null), identity);
    }

    [Fact]
    public void Excerpts_lose_their_highlight_markers_and_mentions_are_found_in_storage_format()
    {
        Assert.Equal("Alex can you look at this?", ConfluenceCloudAdapter.Excerpt("@@@hl@@@Alex@@@endhl@@@ can you\n look at   this?"));
        Assert.True(ConfluenceCloudAdapter.Mentions($"<p><ac:link><ri:user ri:account-id=\"{Me}\" /></ac:link></p>", Me));
        Assert.False(ConfluenceCloudAdapter.Mentions("<p><ac:link><ri:user ri:account-id=\"712020:other\" /></ac:link></p>", Me));
    }

    // ---- builders shaped like Confluence's JSON ----

    private static JsonObject Search(params JsonObject[] results) => new()
    {
        ["results"] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()),
        ["totalSize"] = results.Length,
        ["_links"] = new JsonObject(),
    };

    private static JsonObject Page(string id, string title) => new()
    {
        ["content"] = new JsonObject { ["id"] = id, ["type"] = "page", ["title"] = title },
        ["title"] = title,
        ["url"] = $"/spaces/PR/pages/{id}",
        ["lastModified"] = "2026-09-01T00:00:00.000Z",
    };

    private static JsonObject Comment(
        string id, string authorId, string author, string modified, (string Id, string Title, string CreatedBy) page, string body, string excerpt,
        (string Id, string CreatedBy)[]? ancestors = null) => new()
        {
            ["content"] = new JsonObject
            {
                ["id"] = id,
                ["type"] = "comment",
                ["title"] = "Re: " + page.Title,
                ["history"] = new JsonObject { ["createdBy"] = new JsonObject { ["accountId"] = authorId, ["displayName"] = author } },
                ["container"] = new JsonObject
                {
                    ["id"] = page.Id,
                    ["type"] = "page",
                    ["title"] = page.Title,
                    ["history"] = new JsonObject { ["createdBy"] = new JsonObject { ["accountId"] = page.CreatedBy, ["displayName"] = "Owner" } },
                },
                ["ancestors"] = new JsonArray((ancestors ?? []).Select(a => (JsonNode)new JsonObject
                {
                    ["id"] = a.Id,
                    ["type"] = "comment",
                    ["history"] = new JsonObject { ["createdBy"] = new JsonObject { ["accountId"] = a.CreatedBy } },
                }).ToArray()),
                ["body"] = new JsonObject { ["storage"] = new JsonObject { ["value"] = body, ["representation"] = "storage" } },
            },
            ["title"] = "Re: " + page.Title,
            ["excerpt"] = excerpt,
            ["url"] = $"/spaces/PR/pages/{page.Id}/{Uri.EscapeDataString(page.Title).Replace("%20", "+")}?focusedCommentId={id}",
            ["lastModified"] = modified,
        };
}
