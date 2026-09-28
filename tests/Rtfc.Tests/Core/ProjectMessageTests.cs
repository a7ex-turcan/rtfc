using Rtfc.Core;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Messages addressed to one of the recipient's projects (spec §7.6), between two nodes over real TLS.</summary>
public class ProjectMessageTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TestNode _alex = null!;
    private TestNode _sasha = null!;

    public async ValueTask InitializeAsync()
    {
        _alex = await TestNode.StartAsync("alex", "desktop");
        _sasha = await TestNode.StartAsync("sasha", "laptop");
        var invite = _alex.Node.CreateInvite();
        Assert.Equal(AcceptStatus.Accepted, (await _sasha.Node.AcceptAsync(invite.Token, Ct)).Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _alex.DisposeAsync();
        await _sasha.DisposeAsync();
    }

    /// <summary>A git repository under the node's home; a session in <c>src</c> registers the repository's root.</summary>
    private static string Repository(TestNode node, params string[] path)
    {
        var root = Path.Combine([node.Home.Root, .. path]);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var session = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        node.Node.RegisterProject(session);
        return session;
    }

    [Fact]
    public async Task A_message_for_a_project_lands_there_and_other_projects_point_at_it()
    {
        var payments = Repository(_alex, "payments-api");
        var billing = Repository(_alex, "billing");

        var sent = await _sasha.Node.SendAsync("alex", "The retry policy in PaymentClient looks off.", new SendOptions(Project: "Payments-API"), Ct);

        Assert.Equal(SendStatus.Delivered, sent.Status);
        Assert.Equal("Payments-API", sent.Project);

        var here = _alex.Node.ListInbox(InboxState.Parked, payments, allProjects: false);
        Assert.Equal("payments-api", Assert.Single(here.Messages).Project);
        Assert.Empty(here.Elsewhere);

        var there = _alex.Node.ListInbox(InboxState.Parked, billing, allProjects: false);
        Assert.Empty(there.Messages);
        var pointer = Assert.Single(there.Elsewhere);
        Assert.Equal(("payments-api", 1, "sasha"), (pointer.Project, pointer.Parked, Assert.Single(pointer.From)));

        Assert.Single(_alex.Node.ListInbox(InboxState.Parked));

        var status = StatusFile.Read(_alex.Home.StatusPath)!;
        Assert.Equal(0, status.Global.Parked);
        var project = status.Projects[ProjectPaths.Key(Path.GetDirectoryName(payments)!)];
        Assert.Equal(("payments-api", 1, "sasha"), (project.Name, project.Parked, Assert.Single(project.From)));

        Assert.Equal("payments-api", _alex.Node.Open(sent.MessageId!)!.Project);
    }

    [Fact]
    public async Task A_project_that_is_not_there_lands_in_the_shared_inbox_with_a_note_and_the_same_result()
    {
        Repository(_alex, "work", "api");
        Repository(_alex, "home", "api");

        var missing = await _sasha.Node.SendAsync("alex", "For the payments repo.", new SendOptions(Project: "payments-api"), Ct);
        var ambiguous = await _sasha.Node.SendAsync("alex", "For the api repo.", new SendOptions(Project: "api"), Ct);
        var plain = await _sasha.Node.SendAsync("alex", "Nothing to do with a project.", Ct);

        Assert.All([missing, ambiguous, plain], r => Assert.Equal(SendStatus.Delivered, r.Status));
        Assert.All(_alex.Database.ListMessages(null), m => Assert.Null(m.ProjectId));
        Assert.Equal(3, StatusFile.Read(_alex.Home.StatusPath)!.Global.Parked);
        Assert.Equal("Sent for the project \"payments-api\", which does not exist here, so it is in the shared inbox.", _alex.Node.Open(missing.MessageId!)!.Note);
        Assert.Equal("Sent for the project \"api\", which names 2 projects here, so it is in the shared inbox.", _alex.Node.Open(ambiguous.MessageId!)!.Note);
        Assert.Null(_alex.Node.Open(plain.MessageId!)!.Note);
    }

    [Fact]
    public async Task A_thread_stays_in_its_project_on_both_sides()
    {
        var payments = Repository(_alex, "payments-api");
        var portal = Repository(_sasha, "client-portal");

        var question = await _sasha.Node.SendAsync("alex", "Which retry policy do you use?", new SendOptions(Project: "payments-api", FromDirectory: portal), Ct);
        Assert.Equal(SendStatus.Delivered, (await _alex.Node.ReplyAsync(question.MessageId!, "Exponential, five attempts.", Ct)).Status);

        // The answer lands in the project sasha asked from, though nothing on the wire said so.
        var answer = Assert.Single(_sasha.Node.ListInbox(InboxState.Parked, portal, allProjects: false).Messages);
        Assert.Equal("client-portal", answer.Project);
        Assert.Equal(0, StatusFile.Read(_sasha.Home.StatusPath)!.Global.Parked);

        // And her follow-up lands back in alex's payments-api.
        var followUp = await _sasha.Node.ReplyAsync(answer.Id, "Thanks, and the dead-letter queue?", Ct);
        Assert.Equal(SendStatus.Delivered, followUp.Status);
        Assert.Equal("payments-api", _alex.Node.Open(followUp.MessageId!)!.Project);
        Assert.Contains(_alex.Node.ListInbox(null, payments, allProjects: false).Messages, m => m.Id == followUp.MessageId);
    }

    [Fact]
    public async Task Replies_to_ordinary_messages_stay_in_the_shared_inbox()
    {
        Repository(_alex, "payments-api");
        var portal = Repository(_sasha, "client-portal");

        var question = await _sasha.Node.SendAsync("alex", "Lunch?", new SendOptions(FromDirectory: portal), Ct);
        await _alex.Node.ReplyAsync(question.MessageId!, "Yes.", Ct);

        Assert.Null(Assert.Single(_sasha.Node.ListInbox(InboxState.Parked)).Project);
        Assert.Null(Assert.Single(_alex.Database.ListMessages(null)).ProjectId);
    }

    [Theory]
    [InlineData("../payments-api")]
    [InlineData("src/payments-api")]
    [InlineData("payments\\api")]
    [InlineData("<payments>")]
    [InlineData("pay\"ments")]
    [InlineData("pay\nments")]
    [InlineData(".")]
    public async Task A_name_that_is_not_a_folder_name_is_refused_before_anything_leaves(string project)
    {
        var result = await _sasha.Node.SendAsync("alex", "Hello.", new SendOptions(Project: project), Ct);

        Assert.Equal((SendStatus.Rejected, "invalid_project"), (result.Status, result.Reason));
        Assert.Empty(_alex.Database.ListMessages(null));
    }

    [Fact]
    public async Task A_message_left_for_later_keeps_its_project()
    {
        var payments = Repository(_alex, "payments-api");
        await _alex.Node.SetAwayAsync(true, Ct);

        var left = await _sasha.Node.SendAsync("alex", "For when you are back.", new SendOptions(Leave: true, Project: "payments-api"), Ct);
        Assert.Equal(SendStatus.Queued, left.Status);

        await _alex.Node.SetAwayAsync(false, Ct);
        _sasha.Node.KickOutbox();
        var deadline = DateTimeOffset.UtcNow + Wait;
        while (_alex.Node.ListInbox(InboxState.Parked).Length == 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The left message did not arrive.");
            await Task.Delay(50, Ct);
        }

        Assert.Equal("payments-api", Assert.Single(_alex.Node.ListInbox(InboxState.Parked, payments, allProjects: false).Messages).Project);
    }
}
