using Rtfc.Core;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Pushes wait for the session to be idle (spec §7.3, §10.4): nothing lands in the middle of a turn.</summary>
public class SessionIdleTests : IAsyncLifetime
{
    private const string Session = "5b2c9d1e-7f3a-4e8b-a6c0-d1e2f3a4b5c6";
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TestNode _alex = null!;
    private TestNode _sasha = null!;
    private FakeSourceAdapter _jira = null!;
    private string _project = null!;

    public async ValueTask InitializeAsync()
    {
        _jira = new FakeSourceAdapter();
        _alex = await TestNode.StartAsync("alex", "desktop", adapters: [_jira], sources: new SourceSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero));
        _sasha = await TestNode.StartAsync("sasha", "laptop");
        TestNode.AssertAccepted(await _sasha.Node.AcceptAsync(_alex.Node.CreateInvite().Token, Ct));
        Assert.Equal(ManagementStatus.Ok, _alex.Node.SetAutoMode("sasha", "session", null, Session).Status);

        _project = Directory.CreateDirectory(Path.Combine(_alex.Home.Root, "payments-api")).FullName;
        Directory.CreateDirectory(Path.Combine(_project, ".git"));
        Directory.CreateDirectory(Path.Combine(_project, ".claude"));
        File.WriteAllText(SubscriptionsFile.PathFor(_project), """{"sources":[{"account":"jira-work","jql":"project = PAY","mode":"session"}]}""");
        AccountStore.SaveToken(_alex.Home, "jira-work", "t0ken");
        _alex.Node.AddAccount("jira-work", "jira", "https://acme.atlassian.net", "me@acme.com", "712020:me");
        _alex.Node.ApproveSources(_project);

        _alex.Sessions.Open.Add(Session);
        _alex.Node.RegisterProject(_project, Session);
    }

    public async ValueTask DisposeAsync()
    {
        await _alex.DisposeAsync();
        await _sasha.DisposeAsync();
    }

    [Fact]
    public async Task A_source_item_for_a_busy_session_waits_and_goes_in_when_the_turn_ends()
    {
        SessionActivity.MarkBusy(_alex.Home, Session);
        _jira.Queue(Event("a", "jira:PAY-1", T0));
        await _alex.Node.PollDueAsync(Ct);

        Assert.Empty(_alex.Sessions.Pushed);
        Assert.Contains("Waiting for your Claude Code session in payments-api to finish", Assert.Single(_alex.Node.ListInbox(InboxState.Parked)).Note);

        _alex.Node.FlushHeldPushes();
        Assert.Empty(_alex.Sessions.Pushed);

        SessionActivity.MarkIdle(_alex.Home, Session);
        _alex.Node.FlushHeldPushes();

        var (session, pushed) = Assert.Single(_alex.Sessions.Pushed);
        Assert.Equal((Session, InboxKind.Source), (session, pushed.Kind));
        Assert.Contains("Sent into your Claude Code session in payments-api", Assert.Single(_alex.Node.ListInbox(InboxState.Parked)).Note);
    }

    [Fact]
    public async Task A_contacts_message_for_a_busy_session_waits_too_and_counts_toward_the_caps_at_once()
    {
        SessionActivity.MarkBusy(_alex.Home, Session);

        var sent = await _sasha.Node.SendAsync("alex", "Could you review PR 42?", Ct);

        Assert.Empty(_alex.Sessions.Pushed);
        Assert.Contains("Waiting for the Claude Code session that answers sasha to finish", Assert.Single(_alex.Node.ListInbox(InboxState.Parked)).Note);
        Assert.Equal(1, _alex.Database.GetMessage(sent.MessageId!)!.AutoAttempts);

        SessionActivity.MarkIdle(_alex.Home, Session);
        _alex.Node.FlushHeldPushes();

        Assert.Equal(sent.MessageId, Assert.Single(_alex.Sessions.Pushed).Event.Id);
        Assert.Contains("Sent into the Claude Code session that answers sasha", Assert.Single(_alex.Node.ListInbox(InboxState.Parked)).Note);
        Assert.Equal(1, _alex.Database.GetMessage(sent.MessageId!)!.AutoAttempts);
    }

    [Fact]
    public async Task Held_items_go_in_one_turn_at_a_time_and_one_handled_meanwhile_is_skipped()
    {
        SessionActivity.MarkBusy(_alex.Home, Session);
        _jira.Queue(Event("a", "jira:PAY-1", T0), Event("b", "jira:PAY-2", T0.AddMinutes(1)), Event("c", "jira:PAY-3", T0.AddMinutes(2)));
        await _alex.Node.PollDueAsync(Ct);
        var ids = _alex.Node.ListInbox(InboxState.Parked).ToDictionary(i => i.Entity!, i => i.Id);
        Assert.True(_alex.Node.Dismiss(ids["PAY-2"]));

        SessionActivity.MarkIdle(_alex.Home, Session);
        _alex.Node.FlushHeldPushes();
        Assert.Equal([ids["PAY-1"]], _alex.Sessions.Pushed.Select(p => p.Event.Id));

        // The first one's turn: the hook marks the session busy, then idle again when it ends.
        SessionActivity.MarkBusy(_alex.Home, Session);
        _alex.Node.FlushHeldPushes();
        Assert.Single(_alex.Sessions.Pushed);
        SessionActivity.MarkIdle(_alex.Home, Session);
        _alex.Node.FlushHeldPushes();

        Assert.Equal([ids["PAY-1"], ids["PAY-3"]], _alex.Sessions.Pushed.Select(p => p.Event.Id));
    }

    [Fact]
    public async Task A_session_that_closes_while_busy_leaves_its_items_in_the_inbox_saying_so()
    {
        SessionActivity.MarkBusy(_alex.Home, Session);
        _jira.Queue(Event("a", "jira:PAY-1", T0));
        await _alex.Node.PollDueAsync(Ct);

        _alex.Sessions.Open.Remove(Session);
        _alex.Node.SessionClosed(Session);
        SessionActivity.MarkIdle(_alex.Home, Session);
        _alex.Node.FlushHeldPushes();

        Assert.Empty(_alex.Sessions.Pushed);
        Assert.Contains("it closed while it was busy", Assert.Single(_alex.Node.ListInbox(InboxState.Parked)).Note);
    }

    [Fact]
    public async Task Right_after_a_push_the_session_counts_as_busy_so_two_items_do_not_arrive_together()
    {
        await using var alex = await TestNode.StartAsync("alex2", "desktop", adapters: [_jira],
            sources: new SourceSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero), pushes: new SessionPushSettings(Timeout.InfiniteTimeSpan, TimeSpan.FromHours(1)));
        var project = Directory.CreateDirectory(Path.Combine(alex.Home.Root, "billing")).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        File.WriteAllText(SubscriptionsFile.PathFor(project), """{"sources":[{"account":"jira-work","jql":"project = BIL","mode":"session"}]}""");
        AccountStore.SaveToken(alex.Home, "jira-work", "t0ken");
        alex.Node.AddAccount("jira-work", "jira", "https://acme.atlassian.net", "me@acme.com", "712020:me");
        alex.Node.ApproveSources(project);
        alex.Sessions.Open.Add(Session);
        alex.Node.RegisterProject(project, Session);

        _jira.Queue(Event("x", "jira:BIL-1", T0), Event("y", "jira:BIL-2", T0.AddMinutes(1)));
        await alex.Node.PollDueAsync(Ct);

        Assert.Single(alex.Sessions.Pushed);
        Assert.Single(alex.Node.ListInbox(InboxState.Parked), i => i.Note!.Contains("Waiting for your Claude Code session in billing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_busy_mark_goes_stale_and_is_pruned()
    {
        SessionActivity.MarkBusy(_alex.Home, Session);
        Assert.True(SessionActivity.IsBusy(_alex.Home, Session, DateTimeOffset.UtcNow));
        Assert.False(SessionActivity.IsBusy(_alex.Home, Session, DateTimeOffset.UtcNow + SessionActivity.Stale + TimeSpan.FromMinutes(1)));
        Assert.False(SessionActivity.IsBusy(_alex.Home, "../escape", DateTimeOffset.UtcNow));

        Assert.Equal(0, SessionActivity.Prune(_alex.Home, DateTimeOffset.UtcNow));
        Assert.Equal(1, SessionActivity.Prune(_alex.Home, DateTimeOffset.UtcNow + SessionActivity.Stale));
        Assert.False(SessionActivity.IsBusy(_alex.Home, Session, DateTimeOffset.UtcNow));
    }

    private static SourceEvent Event(string id, string entity, DateTimeOffset at) =>
        new(id, entity, SourceEventType.Assigned, "Dan", entity[5..], "Dan assigned it to you", $"https://acme.atlassian.net/browse/{entity[5..]}", at);
}
