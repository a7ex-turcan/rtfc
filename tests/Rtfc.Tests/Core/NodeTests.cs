using Rtfc.Core;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Two daemons' worth of state in one process, talking over real TLS on loopback (spec §16).</summary>
public class NodeTests : IAsyncLifetime
{
    private TestNode _alex = null!;
    private TestNode _sasha = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _alex = await TestNode.StartAsync("alex", "desktop");
        _sasha = await TestNode.StartAsync("sasha", "laptop");
    }

    public async ValueTask DisposeAsync()
    {
        await _alex.DisposeAsync();
        await _sasha.DisposeAsync();
    }

    private async Task BecomeContactsAsync()
    {
        var invite = _alex.Node.CreateInvite();
        var accepted = await _sasha.Node.AcceptAsync(invite.Token, Ct);
        TestNode.AssertAccepted(accepted);
    }

    [Fact]
    public async Task Invite_and_accept_make_both_sides_active_contacts_in_park_mode()
    {
        var invite = _alex.Node.CreateInvite();
        Assert.StartsWith("rtfc1_", invite.Token);
        Assert.Contains($"tcp:127.0.0.1:{_alex.Transport.Port}", invite.Hints);

        var accepted = await _sasha.Node.AcceptAsync(invite.Token, Ct);

        TestNode.AssertAccepted(accepted);
        Assert.Equal("alex", accepted.Handle);
        Assert.Equal(_alex.Node.Self.PersonId, accepted.PersonId);

        var sashasView = Assert.Single(await _sasha.Node.ContactsAsync(probe: true, Ct));
        Assert.Equal(ContactStatus.Active, sashasView.Status);
        Assert.Equal(InboundMode.Park, sashasView.InboundMode);
        var alexDevice = Assert.Single(sashasView.Devices);
        Assert.Equal("desktop", alexDevice.Name);
        Assert.True(alexDevice.Online);

        var alexsView = Assert.Single(await _alex.Node.ContactsAsync(probe: false, Ct));
        Assert.Equal("sasha", alexsView.Handle);
        Assert.Equal(_sasha.Node.Self.PersonId, alexsView.PersonId);
        Assert.Equal("laptop", Assert.Single(alexsView.Devices).Name);
    }

    [Fact]
    public async Task A_token_works_once()
    {
        var invite = _alex.Node.CreateInvite();
        TestNode.AssertAccepted(await _sasha.Node.AcceptAsync(invite.Token, Ct));

        await using var third = await TestNode.StartAsync("mallory", "phone");
        var second = await third.Node.AcceptAsync(invite.Token, Ct);

        Assert.Equal(AcceptStatus.Rejected, second.Status);
        Assert.Contains("already used", second.Reason);
        Assert.Single(await _alex.Node.ContactsAsync(probe: false, Ct));
    }

    [Fact]
    public async Task Accepting_needs_the_inviter_to_be_home_and_keeps_the_token_valid()
    {
        var invite = _alex.Node.CreateInvite();
        await _alex.Node.StopAsync();

        var result = await _sasha.Node.AcceptAsync(invite.Token, Ct);

        Assert.Equal(AcceptStatus.NobodyHome, result.Status);
        Assert.Empty(await _sasha.Node.ContactsAsync(probe: false, Ct));
    }

    [Fact]
    public async Task Garbage_and_own_tokens_are_invalid()
    {
        Assert.Equal(AcceptStatus.Invalid, (await _sasha.Node.AcceptAsync("hello", Ct)).Status);
        Assert.Equal(AcceptStatus.Invalid, (await _sasha.Node.AcceptAsync("rtfc1_!!!", Ct)).Status);
        Assert.Equal(AcceptStatus.Invalid, (await _alex.Node.AcceptAsync(_alex.Node.CreateInvite().Token, Ct)).Status);
    }

    [Fact]
    public async Task A_message_is_delivered_parked_and_shown_in_the_status_file()
    {
        await BecomeContactsAsync();
        var changed = new TaskCompletionSource();
        _alex.Node.InboxChanged += () => changed.TrySetResult();

        var result = await _sasha.Node.SendAsync("alex", "How does your retry policy handle poison messages?", Ct);

        Assert.Equal(SendStatus.Delivered, result.Status);
        Assert.Equal(["alex/desktop"], result.To!);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var parked = Assert.Single(_alex.Node.ListInbox(InboxState.Parked));
        Assert.Equal(result.MessageId, parked.Id);
        Assert.Equal("sasha", parked.From);
        Assert.Equal("laptop", parked.FromDevice);
        Assert.StartsWith("How does your retry policy", parked.Preview);

        var status = StatusFile.Read(_alex.Home.StatusPath)!;
        Assert.Equal(1, status.Global.Parked);
        Assert.Equal(["sasha"], status.Global.From);
        Assert.False(status.Away);
    }

    [Fact]
    public async Task Opening_marks_a_message_read_and_replying_marks_it_answered()
    {
        await BecomeContactsAsync();
        var sent = await _sasha.Node.SendAsync("alex", "Ping?", Ct);

        var opened = _alex.Node.Open(sent.MessageId!)!;
        Assert.Equal("Ping?", opened.Body);
        Assert.Equal(InboxState.Read, opened.State);
        Assert.Equal(sent.MessageId, opened.Thread);
        Assert.Equal(0, StatusFile.Read(_alex.Home.StatusPath)!.Global.Parked);

        var reply = await _alex.Node.ReplyAsync(sent.MessageId!, "Pong.", Ct);
        Assert.Equal(SendStatus.Delivered, reply.Status);
        Assert.Equal(InboxState.Answered, Assert.Single(_alex.Node.ListInbox(null)).State);

        var received = Assert.Single(_sasha.Node.ListInbox(InboxState.Parked));
        Assert.Equal(sent.MessageId, received.ReplyTo);
        var full = _sasha.Node.Open(received.Id)!;
        Assert.Equal(1, full.Hop);
        Assert.Equal(sent.MessageId, full.Thread);
        Assert.Equal("Pong.", full.Body);
    }

    [Fact]
    public async Task Nobody_home_is_reported_immediately_and_nothing_is_queued()
    {
        await BecomeContactsAsync();
        await _alex.Node.StopAsync();

        var result = await _sasha.Node.SendAsync("alex", "Anyone there?", Ct);

        Assert.Equal(SendStatus.NobodyHome, result.Status);
        Assert.Equal("alex", result.Person);
        Assert.Empty(_alex.Node.ListInbox(null));
    }

    [Fact]
    public async Task A_message_can_be_addressed_to_one_device()
    {
        await BecomeContactsAsync();

        Assert.Equal(SendStatus.Delivered, (await _sasha.Node.SendAsync("alex/desktop", "Desktop only.", Ct)).Status);
        Assert.Equal(SendStatus.Rejected, (await _sasha.Node.SendAsync("alex/toaster", "?", Ct)).Status);
        Assert.Equal("unknown_device", (await _sasha.Node.SendAsync("alex/toaster", "?", Ct)).Reason);
    }

    [Fact]
    public async Task No_contact_no_message()
    {
        var result = await _sasha.Node.SendAsync("alex", "We have never met.", Ct);

        Assert.Equal(SendStatus.Rejected, result.Status);
        Assert.Equal("not_a_contact", result.Reason);
    }

    [Fact]
    public async Task A_removed_contact_can_still_reach_the_port_but_not_the_inbox()
    {
        // Removal is local (spec §5.2). Sasha still pins Alex, so her side of the handshake looks
        // fine to her; Alex no longer pins Sasha, so on his side the session is restricted and the
        // message is refused before it touches the inbox.
        await BecomeContactsAsync();
        var sasha = _alex.Database.GetContact(_sasha.Node.Self.PersonId)!;
        _alex.Database.UpsertContact(sasha with { Status = ContactStatus.Removed });

        var result = await _sasha.Node.SendAsync("alex", "Still there?", Ct);

        Assert.Equal(SendStatus.Failed, result.Status);
        Assert.StartsWith("not_a_contact", result.Reason);
        Assert.Empty(_alex.Node.ListInbox(null));
    }

    [Fact]
    public async Task Oversized_and_empty_messages_are_rejected_locally()
    {
        await BecomeContactsAsync();

        Assert.Equal("empty_message", (await _sasha.Node.SendAsync("alex", "   ", Ct)).Reason);
        Assert.Equal("body_too_large", (await _sasha.Node.SendAsync("alex", new string('x', 65 * 1024), Ct)).Reason);
        Assert.Empty(_alex.Node.ListInbox(null));
    }

    [Fact]
    public async Task Contacts_learn_each_others_current_hints_whenever_they_talk()
    {
        await BecomeContactsAsync();
        string[] AlexAsSashaSeesHim() => [.. Assert.Single(_sasha.Database.ListDevices(_alex.Node.Self.PersonId)).Endpoints];
        Assert.Equal([$"tcp:127.0.0.1:{_alex.Transport.Port}"], AlexAsSashaSeesHim());

        // Alex adds a VPN address; the hello of the next message he sends tells Sasha, with no new invite.
        _alex.Node.SetHintHosts(["127.0.0.1", "100.101.5.7"]);
        Assert.Equal(SendStatus.Delivered, (await _alex.Node.SendAsync("sasha", "I am on the VPN now.", Ct)).Status);
        Assert.Equal([$"tcp:127.0.0.1:{_alex.Transport.Port}", $"tcp:100.101.5.7:{_alex.Transport.Port}"], AlexAsSashaSeesHim());

        // And the other way round, from the hello Sasha sends when she connects to him.
        _sasha.Node.SetHintHosts(["127.0.0.1", "sasha.tailnet.ts.net"]);
        Assert.Equal(SendStatus.Delivered, (await _sasha.Node.SendAsync("alex", "Me too.", Ct)).Status);
        Assert.Equal(
            [$"tcp:127.0.0.1:{_sasha.Transport.Port}", $"tcp:sasha.tailnet.ts.net:{_sasha.Transport.Port}"],
            Assert.Single(_alex.Database.ListDevices(_sasha.Node.Self.PersonId)).Endpoints);
    }
}
