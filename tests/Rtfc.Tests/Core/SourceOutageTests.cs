using System.Text.Json.Nodes;
using Rtfc.Cli;
using Rtfc.Core;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>
/// A failing source says so (spec §10.6): one parked notice per project per outage and a status line warning, once a poll or a
/// stream has failed for long enough, both gone when it works again. Asked for after two feeds were dead for a day and only the
/// <c>sources</c> view showed it.
/// </summary>
public class SourceOutageTests : IAsyncDisposable
{
    private const string Command = "ssh -o BatchMode=yes isos-dell node D:/Tools/outlookfeed/stream.js";
    private static readonly string Good = """{"v":1,"items":[]}""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ScriptedFeedRunner _runner = new();
    private readonly ScriptedFeedStreamer _streamer = new();
    private TestNode _alex = null!;
    private string _project = null!;

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _alex.DisposeAsync();
    }

    private async Task StartAsync(TimeSpan outageAfter)
    {
        _alex = await TestNode.StartAsync("alex", "desktop", adapters: [new FeedAdapter(_runner)], streamer: _streamer,
            sources: new SourceSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero, StreamBackoff: [TimeSpan.FromMilliseconds(50)], OutageAfter: outageAfter));
        _project = NewProject("isos-admin");
    }

    private string NewProject(string name)
    {
        var project = Directory.CreateDirectory(Path.Combine(_alex.Home.Root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        return project;
    }

    private void Subscribe(string project, string account, bool stream = false)
    {
        AccountStore.SaveToken(_alex.Home, account, stream ? Command : "ssh isos-dell type feed.json");
        Assert.Equal(ManagementStatus.Ok, _alex.Node.AddAccount(account, FeedAdapter.TypeName, "", "", FeedAdapter.TypeName, stream).Status);
        File.WriteAllText(Path.Combine(project, ".claude", "rtfc.local.json"), $$"""{"sources":[{"account":"{{account}}"}]}""");
        _alex.Node.ApproveSources(project);
    }

    /// <summary>A failed poll waits two minutes; a test does not.</summary>
    private void MakeDue(string account) =>
        _alex.Database.UpsertCursor(_alex.Database.GetCursor(SubscriptionsFile.PollKey(account, new JsonObject()))! with { NextPollAt = null }, DateTimeOffset.UtcNow);

    private InboxSummary[] Notices(string? state = null) => [.. _alex.Node.ListInbox(state).Where(m => m.Kind == InboxKind.Notice)];

    private string Statusline(string directory)
    {
        var stdout = new StringWriter();
        Commands.Statusline(new CommandContext(_alex.Home, stdout, new StringWriter(), Ct), new StringReader($$"""{"cwd":{{JsonValue.Create(directory)!.ToJsonString()}}}"""), stdinRedirected: true);
        return stdout.ToString().Trim();
    }

    [Fact]
    public async Task A_polled_source_failing_long_enough_parks_one_notice_per_outage_and_clears_it_when_it_works_again()
    {
        await StartAsync(TimeSpan.Zero);
        Subscribe(_project, "outlook");
        await _alex.Node.PollDueAsync(Ct);
        Assert.Empty(Notices());
        Assert.Equal("", Statusline(_project));

        // It fails: the user is told, in this project, once, by rtfc, with the reason; the status line names it.
        _runner.Next = new FeedRun(255, "", "ssh: connect to host isos-dell port 22: Connection timed out");
        MakeDue("outlook");
        await _alex.Node.PollDueAsync(Ct);
        var notice = Assert.Single(Notices(InboxState.Parked));
        Assert.Equal("rtfc", notice.From);
        Assert.Equal("isos-admin", notice.Project);
        var body = _alex.Node.Open(notice.Id)!.Body;
        Assert.StartsWith("The outlook feed has been failing since ", body);
        Assert.Contains("Its last error: The feed command exited with 255", body);
        Assert.Contains("Connection timed out", body);
        Assert.Contains("clears itself once it works again", body);
        Assert.Equal("⚠ rtfc: outlook failing", Statusline(_project));
        Assert.NotNull(Assert.Single(_alex.Node.SourceViews(_project)).FailingSince);

        // Still failing: no second notice, and the warning stays although the notice was read.
        MakeDue("outlook");
        await _alex.Node.PollDueAsync(Ct);
        Assert.Single(Notices());
        Assert.Equal(["outlook"], StatusFile.Read(_alex.Home.StatusPath)!.Projects.Single().Value.Failing!);

        // It works again: the notice is dismissed with a note saying so, and the warning goes.
        _runner.Feed(Good);
        MakeDue("outlook");
        await _alex.Node.PollDueAsync(Ct);
        var cleared = Assert.Single(Notices(InboxState.Dismissed));
        Assert.StartsWith("It works again as of ", cleared.Note);
        Assert.Equal("", Statusline(_project));
        Assert.Null(Assert.Single(_alex.Node.SourceViews(_project)).FailingSince);

        // A new outage is a new notice.
        _runner.Next = new FeedRun(1, "", "feed.json is missing");
        MakeDue("outlook");
        await _alex.Node.PollDueAsync(Ct);
        Assert.Single(Notices(InboxState.Parked));
        Assert.Equal(2, Notices().Length);
        Assert.Equal("📨 1 · rtfc  ⚠ rtfc: outlook failing", Statusline(_project));
    }

    [Fact]
    public async Task A_short_failure_tells_nobody_and_shows_only_in_the_sources_view()
    {
        await StartAsync(TimeSpan.FromMinutes(10));
        Subscribe(_project, "outlook");
        await _alex.Node.PollDueAsync(Ct);

        _runner.Next = new FeedRun(255, "", "ssh: Connection reset by peer");
        MakeDue("outlook");
        await _alex.Node.PollDueAsync(Ct);

        Assert.Empty(Notices());
        Assert.Equal("", Statusline(_project));
        var view = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.Contains("Connection reset by peer", view.LastError);
        Assert.NotNull(view.FailingSince);
    }

    [Fact]
    public async Task A_stream_that_keeps_reporting_its_own_error_is_an_outage_until_a_good_line_comes()
    {
        await StartAsync(TimeSpan.Zero);
        Subscribe(_project, "outlook", stream: true);
        await _alex.Node.PollDueAsync(Ct);
        var run = await _streamer.NextRunAsync();
        await run.SendAsync(Good);

        // What the Dell's readers did for a day: an error document every 30 s.
        await run.SendAsync("""{"v":1,"error":"outlookfeed: feed is 12 minutes old"}""");
        await run.SendAsync("""{"v":1,"error":"outlookfeed: feed is 13 minutes old"}""");
        await _alex.Node.PollDueAsync(Ct);
        var notice = Assert.Single(Notices(InboxState.Parked));
        Assert.Contains("Its last error: The feed reports: outlookfeed: feed is 13 minutes old", _alex.Node.Open(notice.Id)!.Body);
        await run.SendAsync("""{"v":1,"error":"outlookfeed: feed is 14 minutes old"}""");
        await _alex.Node.PollDueAsync(Ct);
        Assert.Single(Notices());
        Assert.Equal("⚠ rtfc: outlook failing", Statusline(_project));

        await run.SendAsync(Good);
        await _alex.Node.PollDueAsync(Ct);
        Assert.StartsWith("It works again", Assert.Single(Notices(InboxState.Dismissed)).Note);
        Assert.Equal("", Statusline(_project));
    }

    [Fact]
    public async Task Every_project_on_a_failing_poll_is_told_and_one_that_stops_polling_it_has_its_notice_cleared_or_removed()
    {
        await StartAsync(TimeSpan.Zero);
        var other = NewProject("payments-api");
        Subscribe(_project, "outlook");
        Subscribe(other, "outlook");
        await _alex.Node.PollDueAsync(Ct);

        _runner.Next = new FeedRun(255, "", "ssh: Could not resolve hostname isos-dell");
        MakeDue("outlook");
        await _alex.Node.PollDueAsync(Ct);
        Assert.Equal(["isos-admin", "payments-api"], Notices(InboxState.Parked).Select(n => n.Project).Order(StringComparer.Ordinal));
        // Each project's notice is its own; another project's shows as a pointer, as its messages do.
        Assert.Equal("📨 1 · rtfc  📨 1 · rtfc → isos-admin  ⚠ rtfc: outlook failing", Statusline(other));

        // One project drops the subscription while the source is still down: its notice no longer applies.
        File.WriteAllText(Path.Combine(other, ".claude", "rtfc.local.json"), """{"sources":[]}""");
        await _alex.Node.PollDueAsync(Ct);
        var dropped = Assert.Single(Notices(InboxState.Dismissed));
        Assert.Equal("payments-api", dropped.Project);
        Assert.Contains("no longer polled in this project", dropped.Note);
        Assert.Equal("📨 1 · rtfc → isos-admin", Statusline(other));
        Assert.Equal("isos-admin", Assert.Single(Notices(InboxState.Parked)).Project);

        // Forgetting a project takes its notices with its items.
        _alex.Node.ForgetProject(_project);
        Assert.DoesNotContain(Notices(), n => n.Project == "isos-admin" || n.Project is null);
    }
}
