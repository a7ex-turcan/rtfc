using Rtfc.Core;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Phase 4 (spec §7.2, §7.3, §9.4): what happens when the other person is not there.</summary>
public class OutboxTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TestNode _alex = null!;
    private TestNode _sasha = null!;

    public async ValueTask InitializeAsync()
    {
        _alex = await TestNode.StartAsync("alex", "desktop");
        _sasha = await TestNode.StartAsync("sasha", "laptop");
        Assert.Equal(AcceptStatus.Accepted, (await _sasha.Node.AcceptAsync(_alex.Node.CreateInvite().Token, Ct)).Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _alex.DisposeAsync();
        await _sasha.DisposeAsync();
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Wait;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task A_reply_to_someone_who_left_waits_in_the_outbox_and_arrives_when_they_return()
    {
        var question = await _sasha.Node.SendAsync("alex", "How does the retry policy handle poison messages?", Ct);
        await _sasha.Node.SetAwayAsync(true, Ct);

        var reply = await _alex.Node.ReplyAsync(question.MessageId!, "Dead-letter queue after 5 attempts.", Ct);

        Assert.Equal(SendStatus.Queued, reply.Status);
        Assert.Equal("sasha", reply.Person);
        Assert.True(reply.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30));
        var mine = Assert.Single(_alex.Node.ListInbox(null));
        Assert.Equal(InboxState.Answered, mine.State);
        Assert.Equal(SentState.Queued, mine.ReplyState);
        Assert.Contains("waits in the outbox", mine.Note);
        Assert.Equal(1, _alex.Node.Status().Global.Pending);
        var pending = Assert.Single(_alex.Node.ListOutbox());
        Assert.Equal(OutboxKind.Reply, pending.Kind);
        Assert.Equal("sasha", pending.To);
        Assert.Empty(_sasha.Node.ListInbox(null));

        await _sasha.Node.SetAwayAsync(false, Ct);
        _alex.Node.KickOutbox();
        await UntilAsync(() => _sasha.Node.ListInbox(InboxState.Parked).Length == 1, "the reply to arrive");

        var arrived = _sasha.Node.Open(Assert.Single(_sasha.Node.ListInbox(InboxState.Parked)).Id)!;
        Assert.Equal("Dead-letter queue after 5 attempts.", arrived.Body);
        Assert.Equal(question.MessageId, arrived.ReplyTo);
        Assert.Equal(1, arrived.Hop);
        await UntilAsync(() => _alex.Node.Status().Global.Pending == 0, "the outbox to empty");
        var delivered = Assert.Single(_alex.Node.Open(question.MessageId!)!.YourReplies!);
        Assert.Contains(delivered.State, new[] { SentState.Delivered, SentState.Read });
        Assert.NotNull(delivered.DeliveredAt);
        Assert.Contains("delivered to sasha/laptop", _alex.Node.Open(question.MessageId!)!.Note!);
    }

    [Fact]
    public async Task An_undelivered_reply_expires_with_a_notice_for_the_human()
    {
        // A fresh pair: this Alex expires outbox entries after a second, and Sasha must know this Alex, not the one from InitializeAsync.
        await _alex.DisposeAsync();
        await _sasha.DisposeAsync();
        _alex = await TestNode.StartAsync("alex", "desktop", outbox: new OutboxSettings(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300), TimeSpan.FromDays(30)));
        _sasha = await TestNode.StartAsync("sasha", "laptop");
        Assert.Equal(AcceptStatus.Accepted, (await _sasha.Node.AcceptAsync(_alex.Node.CreateInvite().Token, Ct)).Status);
        var question = await _sasha.Node.SendAsync("alex", "Ping?", Ct);
        await _sasha.Node.SetAwayAsync(true, Ct);

        var reply = await _alex.Node.ReplyAsync(question.MessageId!, "Pong, eventually.", Ct);
        Assert.Equal(SendStatus.Queued, reply.Status);

        await UntilAsync(() => _alex.Node.ListInbox(InboxState.Parked).Any(m => m.Kind == InboxKind.Notice), "the expiry notice");

        var notice = Assert.Single(_alex.Node.ListInbox(InboxState.Parked));
        Assert.Equal("rtfc", notice.From);
        Assert.Contains("was not delivered", notice.Preview);
        var full = _alex.Node.Open(notice.Id)!;
        Assert.Contains("Pong, eventually.", full.Body);
        Assert.Equal(0, _alex.Node.Status().Global.Pending);
        Assert.Equal(SentState.Expired, Assert.Single(_alex.Node.Open(question.MessageId!)!.YourReplies!).State);
        Assert.Contains("not delivered", _alex.Node.Open(question.MessageId!)!.Note);
        Assert.Empty(_alex.Node.ListOutbox());

        Assert.True(_alex.Node.Dismiss(notice.Id));
        Assert.Empty(_alex.Node.ListInbox(InboxState.Parked));
        Assert.False(_alex.Node.Dismiss("01J8ZQ4Y7K3M9V2T6H0XWBNC5R"));
    }

    [Fact]
    public async Task A_new_message_is_queued_only_when_the_user_asks()
    {
        await _sasha.Node.SetAwayAsync(true, Ct);

        var plain = await _alex.Node.SendAsync("sasha", "Are you there?", leave: false, Ct);
        Assert.Equal(SendStatus.NobodyHome, plain.Status);
        Assert.Empty(_alex.Node.ListOutbox());

        var left = await _alex.Node.SendAsync("sasha", "Leaving this for you.", leave: true, Ct);
        Assert.Equal(SendStatus.Queued, left.Status);
        Assert.Equal(OutboxKind.Message, Assert.Single(_alex.Node.ListOutbox()).Kind);

        await _sasha.Node.SetAwayAsync(false, Ct);
        _alex.Node.KickOutbox();
        await UntilAsync(() => _sasha.Node.ListInbox(InboxState.Parked).Length == 1, "the left message to arrive");
        Assert.Equal("Leaving this for you.", _sasha.Node.Open(_sasha.Node.ListInbox(InboxState.Parked)[0].Id)!.Body);
        Assert.Empty(_alex.Node.ListInbox(null)); // nothing of this shows in alex's inbox: it was his own message
    }

    [Fact]
    public async Task Opening_a_message_tells_the_sender_it_was_read()
    {
        var sent = await _sasha.Node.SendAsync("alex", "Did you see this?", Ct);
        Assert.Equal(SentState.Delivered, _sasha.Database.GetSent(sent.MessageId!)!.State);

        _alex.Node.Open(sent.MessageId!);

        await UntilAsync(() => _sasha.Database.GetSent(sent.MessageId!)!.State == SentState.Read, "the receipt");
        Assert.NotNull(_sasha.Database.GetSent(sent.MessageId!)!.ReadAt);
        Assert.Empty(_alex.Node.ListOutbox());

        // The same for a reply: sasha reads alex's answer, and alex's copy of the question says so.
        var reply = await _alex.Node.ReplyAsync(sent.MessageId!, "Yes.", Ct);
        Assert.Equal(SendStatus.Delivered, reply.Status);
        _sasha.Node.Open(reply.MessageId!);
        await UntilAsync(() => _alex.Node.Open(sent.MessageId!)!.YourReplies![0].State == SentState.Read, "the reply receipt");
        Assert.Contains("sasha read your reply", _alex.Node.Open(sent.MessageId!)!.Note);
        Assert.Equal(SentState.Read, Assert.Single(_alex.Node.ListInbox(null)).ReplyState);
    }

    [Fact]
    public async Task Receipts_can_be_switched_off_per_contact()
    {
        Assert.Equal(ManagementStatus.Ok, _alex.Node.SetReceipts("sasha", on: false).Status);
        Assert.False(Assert.Single(await _alex.Node.ContactsAsync(false, Ct)).ReadReceipts);
        Assert.Equal(ManagementStatus.NotAContact, _alex.Node.SetReceipts("nobody", on: true).Status);

        var sent = await _sasha.Node.SendAsync("alex", "Did you see this?", Ct);
        _alex.Node.Open(sent.MessageId!);

        var flushed = new TaskCompletionSource();
        _alex.Node.OutboxFlushed += () => flushed.TrySetResult();
        _alex.Node.KickOutbox();
        await flushed.Task.WaitAsync(Wait, Ct);
        Assert.Equal(SentState.Delivered, _sasha.Database.GetSent(sent.MessageId!)!.State);
        Assert.Empty(_alex.Node.ListOutbox());
    }

    [Fact]
    public async Task Away_stops_inbound_and_keeps_outbound()
    {
        Assert.False(_alex.Node.IsAway);
        await _alex.Node.SetAwayAsync(true, Ct);
        Assert.True(_alex.Node.IsAway);
        Assert.True(_alex.Node.Status().Away);

        Assert.Equal(SendStatus.NobodyHome, (await _sasha.Node.SendAsync("alex", "Hello?", Ct)).Status);
        Assert.False(Assert.Single(Assert.Single(await _sasha.Node.ContactsAsync(probe: true, Ct)).Devices).Online);
        Assert.Equal(SendStatus.Delivered, (await _alex.Node.SendAsync("sasha", "I can still talk.", Ct)).Status);

        await _alex.Node.SetAwayAsync(false, Ct);
        Assert.False(_alex.Node.Status().Away);
        Assert.Equal(SendStatus.Delivered, (await _sasha.Node.SendAsync("alex", "Welcome back.", Ct)).Status);
        Assert.Equal(2, _sasha.Node.ListInbox(null).Length + _alex.Node.ListInbox(null).Length);
    }

    [Fact]
    public async Task Away_survives_a_restart_of_the_node()
    {
        await _alex.Node.SetAwayAsync(true, Ct);
        Assert.Equal("1", _alex.Database.GetMeta("away"));
        // A node started over this database would not listen: the flag lives in the database, not in memory.
        Assert.True(_alex.Node.IsAway);
    }

    [Fact]
    public async Task Rename_changes_the_local_petname_only()
    {
        await using var third = await TestNode.StartAsync("mallory", "phone");
        Assert.Equal(AcceptStatus.Accepted, (await third.Node.AcceptAsync(_alex.Node.CreateInvite().Token, Ct)).Status);

        Assert.Equal(ManagementStatus.Ok, _alex.Node.Rename("sasha", "Sasha K").Status);
        Assert.Contains((await _alex.Node.ContactsAsync(false, Ct)), c => c.Handle == "Sasha-K");
        Assert.Equal(SendStatus.Delivered, (await _alex.Node.SendAsync("sasha-k", "New name, same person.", Ct)).Status);
        Assert.Equal("alex", Assert.Single(await _sasha.Node.ContactsAsync(false, Ct)).Handle);

        Assert.Equal(ManagementStatus.Invalid, _alex.Node.Rename("Sasha-K", "mallory").Status);
        Assert.Equal(ManagementStatus.NotAContact, _alex.Node.Rename("nobody", "x").Status);
    }
}
