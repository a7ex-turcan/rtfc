using System.Text.Json.Nodes;
using Rtfc.Cli;
using Rtfc.Core;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Records what it is asked and answers with whatever the test queued. The type is jira so the account type check passes.</summary>
public sealed class FakeSourceAdapter : ISourceAdapter
{
    private readonly Lock _lock = new();
    private readonly List<SourceEvent> _queued = [];

    public string Type => JiraCloudAdapter.TypeName;
    public int Polls { get; private set; }
    public SourceException? Fail { get; set; }
    public JsonObject? LastSelector { get; private set; }
    public SourceCursor? LastCursor { get; private set; }

    public void Queue(params SourceEvent[] events)
    {
        lock (_lock)
        {
            _queued.AddRange(events);
        }
    }

    public Task<SourceIdentity> IdentifyAsync(SourceAccount account, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceIdentity("712020:me", "Me", "UTC"));

    public Task<PollResult> PollAsync(SourceAccount account, JsonObject selector, SourceCursor cursor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Polls++;
            LastSelector = selector;
            LastCursor = cursor;
            if (Fail is { } failure)
            {
                throw failure;
            }

            var events = _queued.ToArray();
            _queued.Clear();
            return Task.FromResult(new PollResult(events, new SourceCursor(now, [.. events.Select(e => e.ExternalId)])));
        }
    }
}

/// <summary>One node, one project with a subscriptions file, a fake Jira: the pipeline of spec §10 without the network.</summary>
public class SourcesTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TestNode _alex = null!;
    private FakeSourceAdapter _jira = null!;
    private string _project = null!;

    public async ValueTask InitializeAsync()
    {
        _jira = new FakeSourceAdapter();
        // Polling is off (an infinite tick); tests drive PollDueAsync by hand. A zero interval makes every manual poll due.
        _alex = await TestNode.StartAsync("alex", "desktop", adapters: [_jira], sources: new SourceSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero));
        _project = Directory.CreateDirectory(Path.Combine(_alex.Home.Root, "payments-api")).FullName;
        Directory.CreateDirectory(Path.Combine(_project, ".git"));
        AccountStore.SaveToken(_alex.Home, "jira-work", "t0ken");
        Assert.Equal(ManagementStatus.Ok, _alex.Node.AddAccount("jira-work", "jira", "https://acme.atlassian.net", "me@acme.com", "712020:me").Status);
    }

    public async ValueTask DisposeAsync() => await _alex.DisposeAsync();

    [Fact]
    public async Task A_subscription_waits_for_approval_and_then_polls_into_one_item_per_ticket()
    {
        WriteSubscriptions("""{"sources":[{"account":"jira-work","type":"jira","jql":"project = PAY","events":["assigned","status_changed","mentioned","comment_on_mine"]}]}""");
        _alex.Node.RegisterProject(_project);
        await _alex.Node.PollDueAsync(Ct);

        var pending = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Equal(SubscriptionStatus.PendingApproval, pending.Status);
        Assert.Equal("jira-work", pending.Account);
        Assert.Equal("""{"jql":"project = PAY"}""", pending.Selector);
        Assert.Equal(0, _jira.Polls);
        Assert.Equal(1, StatusFile.Read(_alex.Home.StatusPath)!.Projects.Single().Value.PendingSubscriptions);

        var approved = _alex.Node.ApproveSources(_project);
        Assert.Equal(ManagementStatus.Ok, approved.Status);
        Assert.Equal("1", approved.Reason);
        Assert.Equal(SubscriptionStatus.Active, Assert.Single(_alex.Node.SourceViews(_project)).Status);

        _jira.Queue(
            Event("jira:PAY-1:cl:1", "jira:PAY-1", SourceEventType.StatusChanged, "Dan", "PAY-1 Retry policy", "Dan moved it from \"To Do\" to \"In Progress\"", T0.AddMinutes(1)),
            Event("jira:PAY-1:c:2", "jira:PAY-1", SourceEventType.CommentOnMine, "Eve", "PAY-1 Retry policy", "Eve commented: looks fine", T0.AddMinutes(2)),
            Event("jira:PAY-2:cl:3", "jira:PAY-2", SourceEventType.Assigned, "Dan", "PAY-2 Timeouts", "Dan assigned it to you", T0.AddMinutes(3)));
        await _alex.Node.PollDueAsync(Ct);

        Assert.Equal(1, _jira.Polls);
        Assert.Equal("project = PAY", _jira.LastSelector!["jql"]!.GetValue<string>());
        var items = _alex.Node.ListInbox(InboxState.Parked).OrderBy(i => i.Entity).ToArray();
        Assert.Equal(["PAY-1", "PAY-2"], items.Select(i => i.Entity));
        Assert.All(items, i => Assert.Equal(InboxKind.Source, i.Kind));
        Assert.All(items, i => Assert.Equal("jira", i.From));
        Assert.All(items, i => Assert.Equal("payments-api", i.Project));
        Assert.Equal(2, items[0].Events);
        Assert.Equal("PAY-1 Retry policy: Eve commented: looks fine", items[0].Preview);

        var status = StatusFile.Read(_alex.Home.StatusPath)!;
        var project = status.Projects.Single().Value;
        Assert.Equal(2, project.Tickets);
        Assert.Equal(0, project.Reviews);
        Assert.Equal(0, project.Parked);
        Assert.Equal(0, status.Global.Parked);

        // Opening an item reads it, which takes it off the status line like a message.
        var opened = _alex.Node.Open(items[0].Id)!;
        Assert.Equal("jira", opened.Source);
        Assert.Equal("PAY-1", opened.Entity);
        Assert.Equal("https://acme.atlassian.net/browse/PAY-1", opened.Url);
        Assert.Equal(["status_changed", "comment_on_mine"], opened.Events!.Select(e => e.Type));
        Assert.Equal(InboxState.Read, opened.State);
        Assert.Equal(1, StatusFile.Read(_alex.Home.StatusPath)!.Projects.Single().Value.Tickets);

        // A dismissed ticket that moves again is news: it comes back with its history intact.
        Assert.True(_alex.Node.Dismiss(items[0].Id));
        _jira.Queue(Event("jira:PAY-1:cl:4", "jira:PAY-1", SourceEventType.StatusChanged, "Dan", "PAY-1 Retry policy", "Dan moved it from \"In Progress\" to \"Done\"", T0.AddMinutes(9)));
        await _alex.Node.PollDueAsync(Ct);
        var back = Assert.Single(_alex.Node.ListInbox(InboxState.Parked), i => i.Entity == "PAY-1");
        Assert.Equal(items[0].Id, back.Id);
        Assert.Equal(3, back.Events);

        // The same event twice is one event.
        _jira.Queue(Event("jira:PAY-1:cl:4", "jira:PAY-1", SourceEventType.StatusChanged, "Dan", "PAY-1 Retry policy", "again", T0.AddMinutes(9)));
        await _alex.Node.PollDueAsync(Ct);
        Assert.Equal(3, Assert.Single(_alex.Node.ListInbox(InboxState.Parked), i => i.Entity == "PAY-1").Events);

        // rtfc never writes to a source.
        Assert.Equal("source_item", (await _alex.Node.ReplyAsync(back.Id, "thanks", Ct)).Reason);
    }

    [Fact]
    public async Task Only_the_events_a_subscription_asked_for_land_and_two_projects_share_one_poll()
    {
        WriteSubscriptions("""{"sources":[{"account":"jira-work","jql":"project = PAY","events":["assigned"]}]}""");
        var other = Directory.CreateDirectory(Path.Combine(_alex.Home.Root, "billing")).FullName;
        Directory.CreateDirectory(Path.Combine(other, ".git"));
        Directory.CreateDirectory(Path.Combine(other, ".claude"));
        File.WriteAllText(Path.Combine(other, ".claude", "rtfc.local.json"), """{"sources":[{"account":"jira-work","jql":"project = PAY","events":["status_changed"]}]}""");
        _alex.Node.ApproveSources(_project);
        _alex.Node.ApproveSources(other);

        _jira.Queue(
            Event("a", "jira:PAY-1", SourceEventType.Assigned, "Dan", "PAY-1", "assigned", T0),
            Event("b", "jira:PAY-1", SourceEventType.StatusChanged, "Dan", "PAY-1", "moved", T0.AddMinutes(1)),
            Event("c", "jira:PAY-1", SourceEventType.Mentioned, "Dan", "PAY-1", "mentioned", T0.AddMinutes(2)));
        await _alex.Node.PollDueAsync(Ct);

        Assert.Equal(1, _jira.Polls);
        var paymentsItem = Assert.Single(_alex.Node.ListInbox(InboxState.Parked), i => i.Project == "payments-api");
        Assert.Equal(["assigned"], _alex.Node.Open(paymentsItem.Id)!.Events!.Select(e => e.Type));
        var billingItem = Assert.Single(_alex.Node.ListInbox(InboxState.Parked), i => i.Project == "billing");
        Assert.Equal(["status_changed"], _alex.Node.Open(billingItem.Id)!.Events!.Select(e => e.Type));
    }

    [Fact]
    public async Task An_edited_entry_goes_back_to_pending_and_a_removed_one_goes_away()
    {
        WriteSubscriptions("""{"sources":[{"account":"jira-work","jql":"project = PAY"}]}""");
        Assert.Equal("1", _alex.Node.ApproveSources(_project).Reason);
        var original = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Equal(SubscriptionStatus.Active, original.Status);

        WriteSubscriptions("""{"sources":[{"account":"jira-work","jql":"project = PAY AND assignee = currentUser()"}]}""");
        await _alex.Node.PollDueAsync(Ct);
        var edited = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Equal(SubscriptionStatus.PendingApproval, edited.Status);
        Assert.Contains("currentUser()", edited.Selector);
        Assert.Equal(0, _jira.Polls);

        // A broken file changes nothing already approved, and says what is wrong.
        Assert.Equal("1", _alex.Node.ApproveSources(_project).Reason);
        WriteSubscriptions("""{"sources":[{"jql":"oops"}]}""");
        await _alex.Node.PollDueAsync(Ct);
        var kept = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Equal(SubscriptionStatus.Active, kept.Status);
        Assert.Contains("needs an \"account\"", kept.FileError);
        Assert.Equal(ManagementStatus.Invalid, _alex.Node.ApproveSources(_project).Status);

        File.Delete(SubscriptionsFile.PathFor(_project));
        await _alex.Node.PollDueAsync(Ct);
        Assert.Empty(_alex.Node.SourceViews(_project));
    }

    [Fact]
    public async Task A_failing_poll_is_recorded_and_a_refused_token_stops_the_subscription_until_it_works_again()
    {
        WriteSubscriptions("""{"sources":[{"account":"jira-work","jql":"project = PAY"}]}""");
        _alex.Node.ApproveSources(_project);

        _jira.Fail = new SourceException("Jira is having a moment");
        await _alex.Node.PollDueAsync(Ct);
        var transient = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Equal(SubscriptionStatus.Active, transient.Status);
        Assert.Equal("Jira is having a moment", transient.LastError);
        Assert.True(transient.NextPollAt > DateTimeOffset.UtcNow.AddMinutes(4));

        // Not due yet: the back-off holds.
        await _alex.Node.PollDueAsync(Ct);
        Assert.Equal(1, _jira.Polls);

        _alex.Database.UpsertCursor(_alex.Database.GetCursor(SubscriptionsFile.PollKey("jira-work", new JsonObject { ["jql"] = "project = PAY" }))! with { NextPollAt = null });
        _jira.Fail = new SourceException("Jira refused the token", permanent: true);
        await _alex.Node.PollDueAsync(Ct);
        var refused = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Equal(SubscriptionStatus.Error, refused.Status);
        Assert.Equal("Jira refused the token", refused.LastError);

        _alex.Database.UpsertCursor(_alex.Database.GetCursor(SubscriptionsFile.PollKey("jira-work", new JsonObject { ["jql"] = "project = PAY" }))! with { NextPollAt = null });
        _jira.Fail = null;
        await _alex.Node.PollDueAsync(Ct);
        var recovered = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Equal(SubscriptionStatus.Active, recovered.Status);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task Without_a_token_or_an_account_nothing_is_polled_and_the_reason_is_shown()
    {
        WriteSubscriptions("""{"sources":[{"account":"jira-work","jql":"project = PAY"},{"account":"ghost","jql":"project = X"}]}""");
        _alex.Node.ApproveSources(_project);
        AccountStore.DeleteToken(_alex.Home, "jira-work");
        await _alex.Node.PollDueAsync(Ct);

        Assert.Equal(0, _jira.Polls);
        var views = _alex.Node.SourceViews(_project).OrderBy(v => v.Account).ToArray();
        Assert.Contains("does not exist", views[0].LastError);
        Assert.Contains("rtfc account add jira-work", views[1].LastError);
        Assert.All(views, v => Assert.Equal(SubscriptionStatus.Error, v.Status));
    }

    [Fact]
    public async Task Forgetting_a_project_stops_its_polling_and_drops_its_items()
    {
        WriteSubscriptions("""{"sources":[{"account":"jira-work","jql":"project = PAY"}]}""");
        _alex.Node.ApproveSources(_project);
        _jira.Queue(Event("a", "jira:PAY-1", SourceEventType.Assigned, "Dan", "PAY-1", "assigned", T0));
        await _alex.Node.PollDueAsync(Ct);
        Assert.Single(_alex.Node.ListInbox(InboxState.Parked));

        Assert.Equal(ManagementStatus.Ok, _alex.Node.ForgetProject(Path.Combine(_project, "src")).Status);

        Assert.Empty(_alex.Node.ListInbox(null));
        Assert.Empty(_alex.Node.SourceViews(null));
        Assert.Empty(StatusFile.Read(_alex.Home.StatusPath)!.Projects);
        Assert.Equal(ManagementStatus.Invalid, _alex.Node.ForgetProject(_project).Status);
    }

    [Fact]
    public async Task The_status_line_shows_tickets_and_pending_subscriptions_for_the_project_it_runs_in()
    {
        WriteSubscriptions("""{"sources":[{"account":"jira-work","jql":"project = PAY"}]}""");
        _alex.Node.RegisterProject(_project);
        await _alex.Node.PollDueAsync(Ct);
        Assert.Equal("⚠ rtfc: 1 pending", Statusline(_project));
        Assert.Equal("", Statusline(_alex.Home.Root));

        _alex.Node.ApproveSources(_project);
        _jira.Queue(
            Event("a", "jira:PAY-1", SourceEventType.Assigned, "Dan", "PAY-1", "assigned", T0),
            Event("b", "jira:PAY-2", SourceEventType.Mentioned, "Dan", "PAY-2", "mentioned", T0));
        await _alex.Node.PollDueAsync(Ct);

        Assert.Equal("🎫 2", Statusline(Path.Combine(_project, "src")));
        Assert.Equal("", Statusline(_alex.Home.Root));
    }

    private string Statusline(string directory)
    {
        var stdout = new StringWriter();
        Commands.Statusline(new CommandContext(_alex.Home, stdout, new StringWriter(), Ct), new StringReader($$"""{"cwd":{{JsonValue.Create(directory)!.ToJsonString()}}}"""), stdinRedirected: true);
        return stdout.ToString().Trim();
    }

    private void WriteSubscriptions(string json)
    {
        Directory.CreateDirectory(Path.Combine(_project, ".claude"));
        File.WriteAllText(SubscriptionsFile.PathFor(_project), json);
    }

    private static SourceEvent Event(string id, string entity, string type, string actor, string title, string summary, DateTimeOffset at) =>
        new(id, entity, type, actor, title, summary, $"https://acme.atlassian.net/browse/{entity[5..]}", at);
}
