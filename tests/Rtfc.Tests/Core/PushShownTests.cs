using System.Text.Json.Nodes;
using Rtfc.Cli;
using Rtfc.Core;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>
/// Every push waits to be seen (spec §7.3, §10.4). Claude Code drops a push it cannot show without a word, which a session started
/// without the channels flag does to all of them; an unseen push is how such a session is found, and nothing more goes there.
/// </summary>
public class PushShownTests : IAsyncLifetime
{
    private const string Session = "3d7e1f0a-5b6c-4d8e-9f2a-1b3c5d7e9f0a";
    private static readonly TimeSpan Confirm = TimeSpan.FromMilliseconds(200);
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TestNode _alex = null!;
    private TestNode _sasha = null!;
    private FakeSourceAdapter _jira = null!;
    private string _project = null!;

    public async ValueTask InitializeAsync()
    {
        _jira = new FakeSourceAdapter();
        _alex = await TestNode.StartAsync("alex", "desktop", adapters: [_jira], sources: new SourceSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero),
            pushes: new SessionPushSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero, TimeSpan.Zero, Confirm));
        _sasha = await TestNode.StartAsync("sasha", "laptop");
        TestNode.AssertAccepted(await _sasha.Node.AcceptAsync(_alex.Node.CreateInvite().Token, Ct));

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

    private async Task<string> LandAsync(string entity, DateTimeOffset at)
    {
        _jira.Queue(new SourceEvent(entity + at.Ticks, entity, SourceEventType.Assigned, "Dan", entity[5..], "Dan assigned it to you", $"https://acme.atlassian.net/browse/{entity[5..]}", at));
        await _alex.Node.PollDueAsync(Ct);
        return _alex.Node.ListInbox(null).Single(i => i.Entity == entity[5..]).Id;
    }

    private InboxMessage Item(string id) => _alex.Database.GetMessage(id)!;

    private async Task GiveUpAsync()
    {
        await Task.Delay(Confirm * 2, Ct);
        _alex.Node.CheckPushesShown();
    }

    private string Statusline()
    {
        var stdout = new StringWriter();
        Commands.Statusline(new CommandContext(_alex.Home, stdout, new StringWriter(), Ct), new StringReader($$"""{"cwd":{{JsonValue.Create(_project)!.ToJsonString()}}}"""), stdinRedirected: true);
        return stdout.ToString().Trim();
    }

    [Fact]
    public async Task One_push_at_a_time_and_the_next_goes_in_once_the_last_one_showed_up()
    {
        var first = await LandAsync("jira:PAY-1", T0);
        var second = await LandAsync("jira:PAY-2", T0.AddMinutes(1));
        Assert.Equal([first], _alex.Sessions.Pushed.Select(p => p.Event.Id));
        Assert.Contains("Waiting for your Claude Code session in payments-api", Item(second).Note);

        _alex.Node.PushShown(Session, first);
        _alex.Node.FlushHeldPushes();
        Assert.Equal([first, second], _alex.Sessions.Pushed.Select(p => p.Event.Id));

        _alex.Node.PushShown(Session, second);
        await GiveUpAsync();
        Assert.All([first, second], id => Assert.Contains("Sent into your Claude Code session in payments-api", Item(id).Note));
        Assert.DoesNotContain("⚠", Statusline());
    }

    [Fact]
    public async Task A_push_that_never_shows_up_stops_pushes_into_that_session_until_it_restarts()
    {
        var first = await LandAsync("jira:PAY-1", T0);
        var second = await LandAsync("jira:PAY-2", T0.AddMinutes(1));

        await GiveUpAsync();

        Assert.Contains("never showed up there", Item(first).Note);
        Assert.Contains(Node.ChannelsFlag, Item(first).Note);
        Assert.Contains("does not show rtfc's pushes", Item(second).Note);
        Assert.All([first, second], id => Assert.Equal(0, Item(id).AutoAttempts));
        Assert.Equal("🎫 2  ⚠ rtfc: pushes not showing", Statusline());

        // Nothing more goes there, a contact's message included.
        var third = await LandAsync("jira:PAY-3", T0.AddMinutes(2));
        Assert.Contains("does not show rtfc's pushes", Item(third).Note);
        Assert.Equal(ManagementStatus.Ok, _alex.Node.SetAutoMode("sasha", "session", null, Session).Status);
        var message = (await _sasha.Node.SendAsync("alex", "Could you review PR 42?", Ct)).MessageId!;
        Assert.Contains("Not sent into the Claude Code session that answers sasha: it does not show rtfc's pushes", Item(message).Note);
        Assert.Single(_alex.Sessions.Pushed);

        // Restarted with the flag: it is offered everything again, oldest first, and the warning goes.
        _alex.Node.RegisterProject(_project, Session);
        Assert.Equal([first, first], _alex.Sessions.Pushed.Select(p => p.Event.Id));
        Assert.DoesNotContain("⚠", Statusline());
    }

    [Fact]
    public async Task A_busy_session_is_not_held_against_a_push_until_it_is_idle_again()
    {
        var first = await LandAsync("jira:PAY-1", T0);
        SessionActivity.MarkBusy(_alex.Home, Session);

        await GiveUpAsync();
        Assert.Contains("Sent into your Claude Code session", Item(first).Note);

        SessionActivity.MarkIdle(_alex.Home, Session);
        _alex.Node.CheckPushesShown();
        Assert.Contains("Sent into your Claude Code session", Item(first).Note);
        await GiveUpAsync();
        Assert.Contains("never showed up there", Item(first).Note);
    }

    [Fact]
    public async Task The_gate_file_or_a_reported_answer_counts_as_shown_when_the_hook_could_not_say_so()
    {
        var first = await LandAsync("jira:PAY-1", T0);
        SessionGates.Write(_alex.Home, Session, new SessionGate(first, "jira", GateState.Pending, InboxKind.Source));
        await GiveUpAsync();
        Assert.Contains("Sent into your Claude Code session", Item(first).Note);

        var second = await LandAsync("jira:PAY-2", T0.AddMinutes(1));
        Assert.Equal(second, _alex.Sessions.Pushed[^1].Event.Id);
        Assert.True(_alex.Node.RecordGateDecision(second, GateOutcome.Later));
        await GiveUpAsync();
        Assert.Contains("Left for later", Item(second).Note);
        Assert.DoesNotContain("⚠", Statusline());
    }

    [Fact]
    public async Task A_push_given_up_on_that_shows_up_late_counts_again_and_clears_the_warning()
    {
        var first = await LandAsync("jira:PAY-1", T0);
        await GiveUpAsync();
        Assert.Equal(0, Item(first).AutoAttempts);

        _alex.Node.PushShown(Session, first);

        Assert.Contains("showed up there late", Item(first).Note);
        Assert.Equal(1, Item(first).AutoAttempts);
        Assert.DoesNotContain("⚠", Statusline());
        var second = await LandAsync("jira:PAY-2", T0.AddMinutes(1));
        Assert.Equal(second, _alex.Sessions.Pushed[^1].Event.Id);
    }
}
