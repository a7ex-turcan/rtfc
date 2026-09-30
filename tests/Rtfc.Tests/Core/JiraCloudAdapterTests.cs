using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Rtfc.Core.Sources;

namespace Rtfc.Tests.Core;

/// <summary>The Jira Cloud adapter against a scripted HTTP handler shaped like the real answers seen on 2026-09-30.</summary>
public class JiraCloudAdapterTests
{
    private const string Me = "712020:me";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SourceAccount Account = new("jira-work", "jira", "https://acme.atlassian.net", "me@acme.com", Me, "t0ken");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_poll_turns_changelog_and_comments_into_events_and_drops_the_users_own()
    {
        var handler = new ScriptedHandler();
        handler.Reply("/rest/api/3/search/jql", Search(
            Issue("PAY-1", "Retry policy", "2026-09-30T11:50:00.000+0000",
                History("10", "712020:dan", "Dan", "2026-09-30T11:40:00.000+0000", ("status", "To Do", "In Progress", null)),
                History("11", "712020:dan", "Dan", "2026-09-30T11:45:00.000+0000", ("assignee", null, "Me", Me)),
                History("12", Me, "Me", "2026-09-30T11:46:00.000+0000", ("status", "In Progress", "Done", null))),
            Issue("PAY-2", "Old one", "2026-09-30T11:00:00.000+0000",
                History("20", "712020:dan", "Dan", "2026-09-29T09:00:00.000+0000", ("status", "A", "B", null)))));
        handler.Reply("/rest/api/3/issue/PAY-1/comment", Comments(
            Comment("100", "712020:dan", "Dan", "2026-09-30T11:48:00.000+0000", Paragraph(Mention(Me, "@Me"), Text(" can you look at this?"))),
            Comment("101", "712020:eve", "Eve", "2026-09-30T11:49:00.000+0000", Paragraph(Text("Plain comment"))),
            Comment("102", Me, "Me", "2026-09-30T11:49:30.000+0000", Paragraph(Text("mine")))));
        handler.Reply("/rest/api/3/issue/PAY-2/comment", Comments());
        var adapter = new JiraCloudAdapter(new HttpClient(handler));
        var since = new DateTimeOffset(2026, 9, 30, 11, 30, 0, TimeSpan.Zero);

        var result = await adapter.PollAsync(Account, Selector("project = PAY"), new SourceCursor(since, []), Now, Ct);

        Assert.Equal(["status_changed", "assigned", "mentioned", "comment_on_mine"], result.Events.Select(e => e.EventType));
        Assert.All(result.Events, e => Assert.Equal("jira:PAY-1", e.EntityKey));
        Assert.All(result.Events, e => Assert.Equal("PAY-1 Retry policy", e.Title));
        Assert.All(result.Events, e => Assert.Equal("https://acme.atlassian.net/browse/PAY-1", e.Url));
        Assert.Equal("Dan moved it from \"To Do\" to \"In Progress\"", result.Events[0].Summary);
        Assert.Equal("Dan assigned it to you", result.Events[1].Summary);
        Assert.Equal("Dan mentioned you: @Me can you look at this?", result.Events[2].Summary);
        Assert.Equal("Eve commented: Plain comment", result.Events[3].Summary);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 11, 50, 0, TimeSpan.Zero), result.Next.Watermark);
        Assert.Contains("jira:PAY-1:c:101", result.Next.BoundaryIds);

        var query = handler.Requests.Single(r => r.Contains("/search/jql"));
        Assert.Contains(Uri.EscapeDataString("(project = PAY) AND updated >= \"-35m\" ORDER BY updated ASC"), query);
        Assert.Contains("expand=changelog", query);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me@acme.com:t0ken")), handler.Authorization);
    }

    [Fact]
    public async Task Events_already_reported_at_the_boundary_are_not_reported_twice_and_stay_on_the_boundary_while_in_the_overlap()
    {
        var handler = new ScriptedHandler();
        handler.Reply("/rest/api/3/search/jql", Search(Issue("PAY-1", "Retry policy", "2026-09-30T11:50:00.000+0000",
            History("10", "712020:dan", "Dan", "2026-09-30T11:40:00.000+0000", ("status", "To Do", "In Progress", null)),
            History("11", "712020:dan", "Dan", "2026-09-30T11:48:00.000+0000", ("status", "In Progress", "Review", null)))));
        handler.Reply("/rest/api/3/issue/PAY-1/comment", Comments());
        var adapter = new JiraCloudAdapter(new HttpClient(handler));

        var result = await adapter.PollAsync(Account, Selector("project = PAY"),
            new SourceCursor(new DateTimeOffset(2026, 9, 30, 11, 30, 0, TimeSpan.Zero), ["jira:PAY-1:cl:10", "jira:PAY-1:cl:11"]), Now, Ct);

        Assert.Empty(result.Events);
        // The watermark is 11:50 and polls overlap by five minutes, so 11:48 must stay on the boundary and 11:40 may go.
        Assert.Equal(["jira:PAY-1:cl:11"], result.Next.BoundaryIds);
    }

    [Fact]
    public async Task A_first_poll_looks_back_ten_minutes_and_an_empty_one_still_moves_the_cursor()
    {
        var handler = new ScriptedHandler();
        handler.Reply("/rest/api/3/search/jql", Search());
        var adapter = new JiraCloudAdapter(new HttpClient(handler));

        var result = await adapter.PollAsync(Account, Selector("project = PAY"), SourceCursor.Empty, Now, Ct);

        Assert.Empty(result.Events);
        Assert.Equal(Now - TimeSpan.FromMinutes(1), result.Next.Watermark);
        Assert.Contains(Uri.EscapeDataString("updated >= \"-15m\""), handler.Requests.Single());
    }

    [Fact]
    public async Task Pages_are_followed_by_token()
    {
        var handler = new ScriptedHandler();
        var first = Search(Issue("PAY-1", "One", "2026-09-30T11:50:00.000+0000"));
        first["isLast"] = false;
        first["nextPageToken"] = "page2";
        handler.Reply("/rest/api/3/search/jql", first, when: url => !url.Contains("nextPageToken"));
        handler.Reply("/rest/api/3/search/jql", Search(Issue("PAY-2", "Two", "2026-09-30T11:55:00.000+0000")), when: url => url.Contains("nextPageToken=page2"));
        handler.Reply("/rest/api/3/issue/PAY-1/comment", Comments(Comment("1", "712020:dan", "Dan", "2026-09-30T11:50:00.000+0000", Paragraph(Text("a")))));
        handler.Reply("/rest/api/3/issue/PAY-2/comment", Comments(Comment("2", "712020:dan", "Dan", "2026-09-30T11:55:00.000+0000", Paragraph(Text("b")))));
        var adapter = new JiraCloudAdapter(new HttpClient(handler));

        var result = await adapter.PollAsync(Account, Selector("project = PAY"), new SourceCursor(Now - TimeSpan.FromHours(1), []), Now, Ct);

        Assert.Equal(["jira:PAY-1", "jira:PAY-2"], result.Events.Select(e => e.EntityKey));
    }

    [Fact]
    public async Task A_refused_token_is_permanent_and_rate_limiting_says_when_to_come_back()
    {
        var refused = new JiraCloudAdapter(new HttpClient(new ScriptedHandler { Status = HttpStatusCode.Unauthorized }));
        var ex = await Assert.ThrowsAsync<SourceException>(() => refused.PollAsync(Account, Selector("x"), SourceCursor.Empty, Now, Ct));
        Assert.True(ex.Permanent);
        Assert.Contains("rtfc account add jira-work", ex.Message);

        var limited = new JiraCloudAdapter(new HttpClient(new ScriptedHandler { Status = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 90 }));
        var wait = await Assert.ThrowsAsync<SourceException>(() => limited.PollAsync(Account, Selector("x"), SourceCursor.Empty, Now, Ct));
        Assert.False(wait.Permanent);
        Assert.Equal(TimeSpan.FromSeconds(90), wait.RetryAfter);

        var gone = new JiraCloudAdapter(new HttpClient(new ScriptedHandler { Status = HttpStatusCode.Gone }));
        Assert.True((await Assert.ThrowsAsync<SourceException>(() => gone.PollAsync(Account, Selector("x"), SourceCursor.Empty, Now, Ct))).Permanent);

        var noJql = new JiraCloudAdapter(new HttpClient(new ScriptedHandler()));
        Assert.True((await Assert.ThrowsAsync<SourceException>(() => noJql.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct))).Permanent);
    }

    [Fact]
    public async Task Identify_returns_the_account_id_the_adapters_compare_against()
    {
        var handler = new ScriptedHandler();
        handler.Reply("/rest/api/3/myself", new JsonObject { ["accountId"] = Me, ["displayName"] = "Alex", ["timeZone"] = "Asia/Dubai" });
        var adapter = new JiraCloudAdapter(new HttpClient(handler));

        var identity = await adapter.IdentifyAsync(Account, Ct);

        Assert.Equal(new SourceIdentity(Me, "Alex", "Asia/Dubai"), identity);
    }

    [Fact]
    public void Jira_timestamps_and_document_bodies_are_read_the_way_jira_writes_them()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 13, 12, 19, 629, TimeSpan.FromHours(4)), JiraCloudAdapter.ParseTime("2026-09-30T13:12:19.629+0400"));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero), JiraCloudAdapter.ParseTime("2026-09-30T09:00:00Z"));
        Assert.Null(JiraCloudAdapter.ParseTime("yesterday"));
        Assert.Null(JiraCloudAdapter.ParseTime(null));

        var body = new JsonObject
        {
            ["type"] = "doc",
            ["content"] = new JsonArray(
                Paragraph(Text("First "), Mention("712020:x", "@Sam"), Text(" line")),
                Paragraph(Text("Second"))),
        };
        Assert.Equal("First @Sam line\nSecond", JiraCloudAdapter.AdfText(body));
        Assert.True(JiraCloudAdapter.Mentions(body, "712020:x"));
        Assert.False(JiraCloudAdapter.Mentions(body, Me));
    }

    // ---- builders shaped like Jira's JSON ----

    private static JsonObject Selector(string jql) => new() { ["jql"] = jql };

    private static JsonObject Search(params JsonObject[] issues) => new() { ["issues"] = new JsonArray(issues.Select(i => (JsonNode)i).ToArray()), ["isLast"] = true };

    private static JsonObject Issue(string key, string summary, string updated, params JsonObject[] histories) => new()
    {
        ["key"] = key,
        ["fields"] = new JsonObject { ["summary"] = summary, ["updated"] = updated, ["status"] = new JsonObject { ["name"] = "x" } },
        ["changelog"] = new JsonObject { ["histories"] = new JsonArray(histories.Select(h => (JsonNode)h).ToArray()) },
    };

    private static JsonObject History(string id, string authorId, string author, string created, params (string Field, string? From, string? To, string? ToId)[] items) => new()
    {
        ["id"] = id,
        ["author"] = new JsonObject { ["accountId"] = authorId, ["displayName"] = author },
        ["created"] = created,
        ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["field"] = i.Field, ["fieldId"] = i.Field, ["fromString"] = i.From, ["toString"] = i.To, ["to"] = i.ToId }).ToArray()),
    };

    private static JsonObject Comments(params JsonObject[] comments) => new() { ["comments"] = new JsonArray(comments.Select(c => (JsonNode)c).ToArray()), ["total"] = comments.Length };

    private static JsonObject Comment(string id, string authorId, string author, string created, JsonObject paragraph) => new()
    {
        ["id"] = id,
        ["author"] = new JsonObject { ["accountId"] = authorId, ["displayName"] = author },
        ["created"] = created,
        ["body"] = new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = new JsonArray(paragraph) },
    };

    private static JsonObject Paragraph(params JsonObject[] inline) => new() { ["type"] = "paragraph", ["content"] = new JsonArray(inline.Select(i => (JsonNode)i).ToArray()) };

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Mention(string id, string text) => new() { ["type"] = "mention", ["attrs"] = new JsonObject { ["id"] = id, ["text"] = text } };

    /// <summary>Answers scripted JSON per path, records the requests, and can play a broken service.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly List<(string Path, Func<string, bool>? When, JsonObject Body)> _replies = [];

        public List<string> Requests { get; } = [];
        public string? Authorization { get; private set; }
        public HttpStatusCode? Status { get; init; }
        public int? RetryAfterSeconds { get; init; }

        public void Reply(string path, JsonObject body, Func<string, bool>? when = null) => _replies.Add((path, when, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            Requests.Add(url);
            Authorization = request.Headers.Authorization?.ToString();
            if (Status is { } status)
            {
                var failure = new HttpResponseMessage(status) { Content = new StringContent("""{"errorMessages":["nope"]}""", Encoding.UTF8, "application/json") };
                if (RetryAfterSeconds is { } seconds)
                {
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                }

                return Task.FromResult(failure);
            }

            var reply = _replies.FirstOrDefault(r => url.Contains(r.Path, StringComparison.Ordinal) && (r.When?.Invoke(url) ?? true));
            if (reply.Body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.Body.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}
