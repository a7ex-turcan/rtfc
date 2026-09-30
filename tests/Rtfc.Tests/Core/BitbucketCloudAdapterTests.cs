using System.Net;
using System.Text.Json.Nodes;
using Rtfc.Core.Sources;

namespace Rtfc.Tests.Core;

/// <summary>The Bitbucket Cloud adapter against scripted answers shaped like the real ones seen on 2026-09-30.</summary>
public class BitbucketCloudAdapterTests
{
    private const string Me = "712020:me";
    private const string MyUuid = "{7c894f3f-0000-0000-0000-000000000001}";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Since = new(2026, 9, 30, 11, 30, 0, TimeSpan.Zero);
    private static readonly SourceAccount Account = new("bb-work", "bitbucket", "https://api.bitbucket.org", "me@acme.com", Me, "t0ken");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Activity_on_my_request_and_a_new_request_to_review_become_events_and_my_own_actions_do_not()
    {
        var handler = new ScriptedHttpHandler();
        handler.Reply("/2.0/user", User(Me, MyUuid, "Me"));
        handler.Reply("/pullrequests?state=", Page(
            PullRequest(42, "Retry policy", "OPEN", User(Me, MyUuid, "Me"), "2026-09-20T08:00:00.000000+00:00", "2026-09-30T11:50:00.000000+00:00"),
            PullRequest(43, "Timeouts", "OPEN", User("712020:dan", "{dan}", "Dan"), "2026-09-30T11:41:00.000000+00:00", "2026-09-30T11:41:00.000000+00:00", reviewers: [User(Me, MyUuid, "Me")]),
            PullRequest(44, "Not mine", "OPEN", User("712020:dan", "{dan}", "Dan"), "2026-09-30T11:42:00.000000+00:00", "2026-09-30T11:42:00.000000+00:00")));
        handler.Reply("/pullrequests/42/activity", Page(
            CommentEntry(1, User("712020:eve", "{eve}", "Eve"), "2026-09-30T11:45:00.000000+00:00", "Looks fine"),
            CommentEntry(2, User("712020:eve", "{eve}", "Eve"), "2026-09-30T11:46:00.000000+00:00", "Sure", parent: 9),
            CommentEntry(3, User(Me, MyUuid, "Me"), "2026-09-30T11:47:00.000000+00:00", "mine"),
            CommentEntry(4, User("712020:eve", "{eve}", "Eve"), "2026-09-30T11:20:00.000000+00:00", "too old"),
            new JsonObject { ["approval"] = new JsonObject { ["date"] = "2026-09-30T11:48:00.000000+00:00", ["user"] = User("712020:dan", "{dan}", "Dan") } },
            new JsonObject { ["changes_requested"] = new JsonObject { ["date"] = "2026-09-30T11:49:00.000000+00:00", ["user"] = User("712020:eve", "{eve}", "Eve") } },
            new JsonObject { ["update"] = new JsonObject { ["state"] = "MERGED", ["date"] = "2026-09-30T11:50:00.000000+00:00", ["author"] = User("712020:dan", "{dan}", "Dan") } },
            new JsonObject { ["update"] = new JsonObject { ["state"] = "OPEN", ["date"] = "2026-09-30T11:44:00.000000+00:00", ["author"] = User("712020:dan", "{dan}", "Dan") } }));
        handler.Reply("/pullrequests/42/comments/9", new JsonObject { ["id"] = 9, ["user"] = User(Me, MyUuid, "Me") });
        handler.Reply("/pullrequests/42/statuses", Page(new JsonObject
        {
            ["key"] = "ci",
            ["name"] = "Pipeline #12",
            ["state"] = "FAILED",
            ["description"] = "2 tests failed",
            ["updated_on"] = "2026-09-30T11:43:00.000000+00:00",
        }));
        handler.Reply("/pullrequests/43/activity", Page(
            CommentEntry(5, User("712020:dan", "{dan}", "Dan"), "2026-09-30T11:41:30.000000+00:00", $"@{{{Me}}} please look"),
            CommentEntry(6, User("712020:dan", "{dan}", "Dan"), "2026-09-30T11:41:40.000000+00:00", "just a note")));
        var adapter = new BitbucketCloudAdapter(new HttpClient(handler));

        var result = await adapter.PollAsync(Account, new JsonObject { ["repo"] = "acme/payments-api" }, new SourceCursor(Since, []), Now, Ct);

        Assert.Equal(
            ["review_requested", "mentioned", "build_failed_on_mine", "comment_on_mine", "reply_to_me", "approved", "changes_requested", "merged"],
            result.Events.Select(e => e.EventType));
        Assert.Equal("bitbucket:acme/payments-api#43", result.Events[0].EntityKey);
        Assert.Equal("Dan asked you to review it", result.Events[0].Summary);
        Assert.Equal("payments-api#43 Timeouts", result.Events[0].Title);
        Assert.Equal("https://bitbucket.org/acme/payments-api/pull-requests/43", result.Events[0].Url);
        Assert.Equal("Dan mentioned you: @{712020:me} please look", result.Events[1].Summary);
        Assert.Equal("Pipeline #12 failed: 2 tests failed", result.Events[2].Summary);
        Assert.Equal("Eve commented: Looks fine", result.Events[3].Summary);
        Assert.Equal("Eve replied to your comment: Sure", result.Events[4].Summary);
        Assert.Equal("Dan approved it", result.Events[5].Summary);
        Assert.Equal("Eve requested changes", result.Events[6].Summary);
        Assert.Equal("Dan merged it", result.Events[7].Summary);
        Assert.All(result.Events.Skip(2), e => Assert.Equal("bitbucket:acme/payments-api#42", e.EntityKey));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 11, 50, 0, TimeSpan.Zero), result.Next.Watermark);
        Assert.DoesNotContain(handler.Requests, r => r.Contains("/pullrequests/44/", StringComparison.Ordinal));

        var list = handler.Requests.Single(r => r.Contains("/pullrequests?state=", StringComparison.Ordinal));
        Assert.Contains("state=OPEN&state=MERGED&state=DECLINED&state=SUPERSEDED", list);
        Assert.Contains(Uri.EscapeDataString("updated_on >= 2026-09-30T11:25:00+00:00"), list);
    }

    [Fact]
    public async Task Pages_follow_the_next_link_and_boundary_ids_stop_repeats()
    {
        var handler = new ScriptedHttpHandler();
        handler.Reply("/2.0/user", User(Me, MyUuid, "Me"));
        var first = Page(PullRequest(1, "One", "OPEN", User(Me, MyUuid, "Me"), "2026-09-01T00:00:00+00:00", "2026-09-30T11:40:00+00:00"));
        first["next"] = "https://api.bitbucket.org/2.0/repositories/acme/payments-api/pullrequests?page=2";
        handler.Reply("/pullrequests?state=", first);
        handler.Reply("pullrequests?page=2", Page(PullRequest(2, "Two", "OPEN", User(Me, MyUuid, "Me"), "2026-09-01T00:00:00+00:00", "2026-09-30T11:45:00+00:00")));
        handler.Reply("/pullrequests/1/activity", Page(CommentEntry(10, User("712020:eve", "{eve}", "Eve"), "2026-09-30T11:40:00+00:00", "a")));
        handler.Reply("/pullrequests/2/activity", Page(CommentEntry(11, User("712020:eve", "{eve}", "Eve"), "2026-09-30T11:45:00+00:00", "b")));
        handler.Reply("/statuses", Page());
        var adapter = new BitbucketCloudAdapter(new HttpClient(handler));

        var result = await adapter.PollAsync(Account, new JsonObject { ["repo"] = "acme/payments-api" },
            new SourceCursor(Since, ["bitbucket:acme/payments-api#1:c:10"]), Now, Ct);

        Assert.Equal(["bitbucket:acme/payments-api#2:c:11"], result.Events.Select(e => e.ExternalId));
        Assert.Contains("bitbucket:acme/payments-api#1:c:10", result.Next.BoundaryIds);
    }

    [Fact]
    public async Task The_repository_selector_is_checked_and_errors_are_told_apart()
    {
        var adapter = new BitbucketCloudAdapter(new HttpClient(new ScriptedHttpHandler()));
        Assert.True((await Assert.ThrowsAsync<SourceException>(() => adapter.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct))).Permanent);
        Assert.True((await Assert.ThrowsAsync<SourceException>(() => adapter.PollAsync(Account, new JsonObject { ["repo"] = "no slash" }, SourceCursor.Empty, Now, Ct))).Permanent);
        Assert.Equal(("acme", "payments-api"), BitbucketCloudAdapter.Repository(new JsonObject { ["repo"] = "acme/payments-api" }));
        Assert.Equal(("acme", "payments-api"), BitbucketCloudAdapter.Repository(new JsonObject { ["repo"] = "payments-api", ["workspace"] = "acme" }));

        var refused = new BitbucketCloudAdapter(new HttpClient(new ScriptedHttpHandler { Status = HttpStatusCode.Unauthorized }));
        var ex = await Assert.ThrowsAsync<SourceException>(() => refused.PollAsync(Account, new JsonObject { ["repo"] = "acme/x" }, SourceCursor.Empty, Now, Ct));
        Assert.True(ex.Permanent);
        Assert.Contains("scoped API token", ex.Message);
        var missing = new BitbucketCloudAdapter(new HttpClient(new ScriptedHttpHandler { Status = HttpStatusCode.NotFound }));
        Assert.True((await Assert.ThrowsAsync<SourceException>(() => missing.PollAsync(Account, new JsonObject { ["repo"] = "acme/x" }, SourceCursor.Empty, Now, Ct))).Permanent);
        var limited = new BitbucketCloudAdapter(new HttpClient(new ScriptedHttpHandler { Status = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 45 }));
        Assert.Equal(TimeSpan.FromSeconds(45), (await Assert.ThrowsAsync<SourceException>(() => limited.PollAsync(Account, new JsonObject { ["repo"] = "acme/x" }, SourceCursor.Empty, Now, Ct))).RetryAfter);
    }

    [Fact]
    public async Task Identify_returns_the_atlassian_account_id_and_both_bitbucket_hosts_mean_the_api()
    {
        var handler = new ScriptedHttpHandler();
        handler.Reply("/2.0/user", User(Me, MyUuid, "Alex"));
        var adapter = new BitbucketCloudAdapter(new HttpClient(handler));

        var identity = await adapter.IdentifyAsync(Account with { BaseUrl = "https://bitbucket.org" }, Ct);

        Assert.Equal(new SourceIdentity(Me, "Alex", null), identity);
        Assert.StartsWith("https://api.bitbucket.org/2.0/user", handler.Requests.Single());
    }

    // ---- builders shaped like Bitbucket's JSON ----

    private static JsonObject User(string accountId, string uuid, string name) => new() { ["account_id"] = accountId, ["uuid"] = uuid, ["display_name"] = name, ["nickname"] = name };

    private static JsonObject Page(params JsonObject[] values) => new() { ["values"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()), ["pagelen"] = 50, ["size"] = values.Length };

    private static JsonObject PullRequest(int id, string title, string state, JsonObject author, string created, string updated, JsonObject[]? reviewers = null) => new()
    {
        ["id"] = id,
        ["title"] = title,
        ["state"] = state,
        ["author"] = author,
        ["created_on"] = created,
        ["updated_on"] = updated,
        ["participants"] = new JsonArray((reviewers ?? []).Select(r => (JsonNode)new JsonObject { ["role"] = "REVIEWER", ["approved"] = false, ["user"] = r.DeepClone() }).ToArray()),
        ["links"] = new JsonObject { ["html"] = new JsonObject { ["href"] = $"https://bitbucket.org/acme/payments-api/pull-requests/{id}" } },
    };

    private static JsonObject CommentEntry(long id, JsonObject user, string created, string raw, long? parent = null)
    {
        var comment = new JsonObject
        {
            ["id"] = id,
            ["user"] = user,
            ["created_on"] = created,
            ["content"] = new JsonObject { ["raw"] = raw, ["html"] = $"<p>{raw}</p>" },
            ["deleted"] = false,
        };
        if (parent is { } p)
        {
            comment["parent"] = new JsonObject { ["id"] = p };
        }

        return new JsonObject { ["comment"] = comment, ["pull_request"] = new JsonObject { ["id"] = 42 } };
    }
}
