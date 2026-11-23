using System.Text.Json.Nodes;
using Rtfc.Core;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Answers with whatever the test set, and records the commands it was asked to run.</summary>
public sealed class ScriptedFeedRunner : IFeedRunner
{
    public FeedRun Next { get; set; } = new(0, """{"v":1,"items":[]}""", "");
    public List<string> Commands { get; } = [];

    public Task<FeedRun> RunAsync(string command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(Next);
    }

    public void Feed(string json) => Next = new FeedRun(0, json, "");
}

/// <summary>The feed adapter of spec §10.1 against scripted output, and its process runner against the real shell of each OS.</summary>
public class FeedAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly SourceAccount Account = new("teams", FeedAdapter.TypeName, "", "", FeedAdapter.TypeName, "ssh work-laptop type feed.json");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Chat = "19:abc@thread.v2";

    private static string Feed(params (string Item, string Entity, (string Id, string At, string Kind, string Actor, string Text)[] Events)[] items) =>
        new JsonObject
        {
            ["v"] = 1,
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Item,
                ["entity"] = i.Entity,
                ["url"] = "https://teams.microsoft.com/l/chat/" + i.Item,
                ["events"] = new JsonArray([.. i.Events.Select(e => (JsonNode)new JsonObject
                {
                    ["id"] = e.Id, ["at"] = e.At, ["kind"] = e.Kind, ["actor"] = e.Actor, ["text"] = e.Text,
                })]),
            })]),
        }.ToJsonString();

    [Fact]
    public async Task The_first_poll_takes_what_is_there_as_seen_and_later_polls_bring_only_what_is_new()
    {
        var runner = new ScriptedFeedRunner();
        var adapter = new FeedAdapter(runner);
        runner.Feed(Feed((Chat, "Chat | Jane Doe", [("m1", "2026-11-23T10:40:00Z", "message", "Jane Doe", "old news")])));

        var first = await adapter.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct);

        Assert.Empty(first.Events);
        Assert.Equal(Now, first.Next.Watermark);
        Assert.Single(first.Next.BoundaryIds);
        Assert.Equal(["ssh work-laptop type feed.json"], runner.Commands);

        runner.Feed(Feed(
            (Chat, "Chat | Jane Doe", [
                ("m1", "2026-11-23T10:40:00Z", "message", "Jane Doe", "old news"),
                ("m2", "2026-11-23T11:42:00Z", "message", "Jane Doe", "Can you look at the release notes?"),
            ]),
            ("19:def@thread.v2", "Ops channel", [("m9", "2026-11-23T11:41:00Z", "mention", "Bob", "@Alex the deploy is stuck")])));

        var second = await adapter.PollAsync(Account, new JsonObject(), first.Next, Now.AddMinutes(2), Ct);

        Assert.Equal(["mentioned", "message"], second.Events.Select(e => e.EventType));
        var mention = second.Events[0];
        Assert.Equal("feed:teams/19:def@thread.v2", mention.EntityKey);
        Assert.Equal("Ops channel", mention.Title);
        Assert.Equal("Bob", mention.Actor);
        Assert.Equal("@Alex the deploy is stuck", mention.Summary);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 11, 41, 0, TimeSpan.Zero), mention.OccurredAt);
        var message = second.Events[1];
        Assert.Equal($"feed:teams/{Chat}", message.EntityKey);
        Assert.StartsWith($"feed:teams/{Chat}:", message.ExternalId);
        Assert.Equal("Chat | Jane Doe", message.Title);
        Assert.Equal($"https://teams.microsoft.com/l/chat/{Chat}", message.Url);
        Assert.Equal("Can you look at the release notes?", message.Summary);
        Assert.Equal(3, second.Next.BoundaryIds.Length);

        // The reader keeps printing the same recent events: nothing twice.
        var third = await adapter.PollAsync(Account, new JsonObject(), second.Next, Now.AddMinutes(4), Ct);
        Assert.Empty(third.Events);
    }

    [Fact]
    public async Task A_feed_that_fails_or_breaks_the_contract_is_an_error_to_retry_soon_and_an_empty_one_is_healthy()
    {
        var runner = new ScriptedFeedRunner();
        var adapter = new FeedAdapter(runner);

        async Task<SourceException> Fails()
        {
            var ex = await Assert.ThrowsAsync<SourceException>(() => adapter.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct));
            Assert.False(ex.Permanent);
            Assert.Equal(FeedAdapter.RetryAfterFailure, ex.RetryAfter);
            return ex;
        }

        runner.Next = new FeedRun(255, "", "ssh: connect to host work-laptop port 22: Connection timed out");
        Assert.Equal("The feed command exited with 255: ssh: connect to host work-laptop port 22: Connection timed out", (await Fails()).Message);
        runner.Feed("not json");
        Assert.Contains("something other than JSON", (await Fails()).Message);
        runner.Feed("""[1, 2]""");
        Assert.Contains("not an object", (await Fails()).Message);
        runner.Feed("""{"v":2,"items":[]}""");
        Assert.Contains("version 1", (await Fails()).Message);
        runner.Feed("""{"v":1}""");
        Assert.Contains("no \"items\"", (await Fails()).Message);

        runner.Feed("﻿{\"v\":1,\"items\":[]}");
        var empty = await adapter.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct);
        Assert.Empty(empty.Events);
        Assert.Equal("0 item(s) with 0 event(s) right now", (await adapter.IdentifyAsync(Account, Ct)).DisplayName);
    }

    [Fact]
    public async Task Ids_outside_the_entity_alphabet_are_hashed_and_whatever_the_feed_says_is_bounded()
    {
        var runner = new ScriptedFeedRunner();
        var adapter = new FeedAdapter(runner);
        runner.Feed("""{"v":1,"items":[]}""");
        var seen = (await adapter.PollAsync(Account, new JsonObject(), SourceCursor.Empty, Now, Ct)).Next;
        var longText = new string('x', 1000);
        runner.Feed($$"""
            {"v":1,"items":[
              {"id":"chat with spaces/and slashes","entity":"Line one\nline\u0007two","url":"javascript:alert(1)",
               "events":[{"id":"e1","at":"not a time","actor":"Eve","text":"{{longText}}"},{"text":"no id, skipped"}]},
              {"entity":"no id, skipped","events":[{"id":"e2","text":"lost"}]},
              {"id":"{{new string('a', 130)}}","events":[{"id":"e3","kind":"reply","text":"a reply"}]}
            ]}
            """);

        var result = await adapter.PollAsync(Account, new JsonObject(), seen, Now.AddMinutes(1), Ct);

        Assert.Equal(2, result.Events.Count);
        Assert.All(result.Events, e => Assert.True(Node.IsValidEntityKey(e.EntityKey), e.EntityKey));
        var spaced = Assert.Single(result.Events, e => e.EventType == SourceEventType.Message);
        Assert.Matches("^feed:teams/h[0-9a-f]{16}$", spaced.EntityKey);
        Assert.Equal("Line one linetwo", spaced.Title);
        Assert.Equal("", spaced.Url);
        Assert.Equal(300, spaced.Summary.Length);
        Assert.EndsWith("...", spaced.Summary);
        Assert.Equal(Now.AddMinutes(1), spaced.OccurredAt);
        var reply = Assert.Single(result.Events, e => e.EventType == SourceEventType.ReplyToMe);
        Assert.Matches("^feed:teams/h[0-9a-f]{16}$", reply.EntityKey);
        Assert.Equal("Someone", reply.Actor);
    }

    [Fact]
    public async Task The_command_runs_in_the_shell_with_a_time_limit_and_a_cap_on_what_it_prints()
    {
        using var temp = new TempHome();
        var file = Path.Combine(temp.Home.Root, "feed.json");
        var json = """{"v":1,"items":[]}""" + new string(' ', 4096);
        File.WriteAllText(file, json);
        var windows = OperatingSystem.IsWindows();
        var runner = new ProcessFeedRunner();

        var read = await runner.RunAsync(windows ? $"type \"{file}\"" : $"cat '{file}'", Ct);
        Assert.Equal(0, read.ExitCode);
        Assert.Equal(json, read.Output);

        var failed = await runner.RunAsync(windows ? "echo first line 1>&2 & echo second line 1>&2 & exit /b 3" : "echo first line >&2; echo second line >&2; exit 3", Ct);
        Assert.Equal(3, failed.ExitCode);
        Assert.Equal("first line", failed.Error);

        var slow = new ProcessFeedRunner(TimeSpan.FromSeconds(1), ProcessFeedRunner.DefaultMaxOutputBytes);
        var timedOut = await Assert.ThrowsAsync<SourceException>(() => slow.RunAsync(windows ? "ping -n 6 127.0.0.1 >nul" : "sleep 5", Ct));
        Assert.Contains("did not finish within 1 seconds", timedOut.Message);

        var small = new ProcessFeedRunner(ProcessFeedRunner.DefaultTimeout, 2048);
        var tooMuch = await Assert.ThrowsAsync<SourceException>(() => small.RunAsync(windows ? $"type \"{file}\"" : $"cat '{file}'", Ct));
        Assert.Contains("more than 2 KiB", tooMuch.Message);
    }
}

/// <summary>A feed through the node: one item per feed item, no filtering by event type, the account's name as the source.</summary>
public class FeedSourceTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ScriptedFeedRunner _runner = new();
    private TestNode _alex = null!;
    private string _project = null!;

    public async ValueTask InitializeAsync()
    {
        _alex = await TestNode.StartAsync("alex", "desktop", adapters: [new FeedAdapter(_runner)], sources: new SourceSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero));
        _project = Directory.CreateDirectory(Path.Combine(_alex.Home.Root, "isos-admin")).FullName;
        Directory.CreateDirectory(Path.Combine(_project, ".git"));
        Directory.CreateDirectory(Path.Combine(_project, ".claude"));
    }

    public async ValueTask DisposeAsync() => await _alex.DisposeAsync();

    [Fact]
    public async Task A_feed_account_needs_no_site_and_its_events_land_whatever_the_subscription_lists()
    {
        AccountStore.SaveToken(_alex.Home, "teams", "ssh work-laptop type feed.json");
        Assert.Equal(ManagementStatus.Ok, _alex.Node.AddAccount("teams", FeedAdapter.TypeName, "", "", FeedAdapter.TypeName).Status);
        var account = Assert.Single(_alex.Node.ListAccounts());
        Assert.Equal("", account.BaseUrl);
        Assert.True(account.HasToken);

        File.WriteAllText(Path.Combine(_project, ".claude", "rtfc.local.json"), """{"sources":[{"account":"teams"}]}""");
        _alex.Node.ApproveSources(_project);
        Assert.Equal(["message", "mentioned", "reply_to_me"], Assert.Single(_alex.Node.SourceViews(_project)).Events);
        _runner.Feed("""{"v":1,"items":[{"id":"19:abc@thread.v2","entity":"Chat | Jane Doe","events":[{"id":"m1","text":"old"}]}]}""");
        await _alex.Node.PollDueAsync(Ct);
        Assert.Empty(_alex.Node.ListInbox(InboxState.Parked));

        _runner.Feed("""{"v":1,"items":[{"id":"19:abc@thread.v2","entity":"Chat | Jane Doe","events":[{"id":"m1","text":"old"},{"id":"m2","actor":"Jane Doe","text":"New: can you look?"}]}]}""");
        await _alex.Node.PollDueAsync(Ct);

        Assert.Equal(["ssh work-laptop type feed.json", "ssh work-laptop type feed.json"], _runner.Commands);
        var item = Assert.Single(_alex.Node.ListInbox(InboxState.Parked));
        Assert.Equal("teams", item.From);
        Assert.Equal("isos-admin", item.Project);
        Assert.Equal(1, StatusFile.Read(_alex.Home.StatusPath)!.Projects.Single().Value.Feeds);
        var opened = _alex.Node.Open(item.Id)!;
        Assert.Equal("teams", opened.Source);
        Assert.Equal("19:abc@thread.v2", opened.Entity);
        Assert.Equal(["message"], opened.Events!.Select(e => e.Type));
        Assert.Null(Assert.Single(_alex.Node.SourceViews(_project)).LastError);

        // A failing command is shown on the subscription, and the next good poll clears it.
        _runner.Next = new FeedRun(255, "", "ssh: Could not resolve hostname work-laptop");
        await _alex.Node.PollDueAsync(Ct);
        Assert.Contains("Could not resolve hostname", Assert.Single(_alex.Node.SourceViews(_project)).LastError);
    }
}
