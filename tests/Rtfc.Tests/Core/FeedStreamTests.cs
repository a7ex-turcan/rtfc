using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Rtfc.Core;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Hands each run of a streaming feed the lines a test sends it, and lets the test end a run.</summary>
public sealed class ScriptedFeedStreamer : IFeedStreamer
{
    private readonly Channel<Run> _runs = Channel.CreateUnbounded<Run>();

    public sealed class Run(string command)
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly SemaphoreSlim _handled = new(0);
        private string _reason = "ended";

        public string Command => command;
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Sends one line and waits until the node has handled it.</summary>
        public async Task SendAsync(string line)
        {
            _lines.Writer.TryWrite(line);
            Assert.True(await _handled.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "The line was never handled.");
        }

        public void End(string reason)
        {
            _reason = reason;
            _lines.Writer.TryComplete();
        }

        internal async Task<FeedStreamEnd> PlayAsync(Func<string, Task> onLine, CancellationToken cancellationToken)
        {
            using var stop = cancellationToken.Register(() => _lines.Writer.TryComplete());
            await foreach (var line in _lines.Reader.ReadAllAsync(CancellationToken.None))
            {
                await onLine(line);
                _handled.Release();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Stopped.TrySetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new FeedStreamEnd(_reason);
        }
    }

    public Task<FeedStreamEnd> RunAsync(string command, Func<string, Task> onLine, TimeSpan stallAfter, CancellationToken cancellationToken)
    {
        var run = new Run(command);
        _runs.Writer.TryWrite(run);
        return run.PlayAsync(onLine, cancellationToken);
    }

    public async Task<Run> NextRunAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _runs.Reader.ReadAsync(timeout.Token);
    }
}

/// <summary>Streaming feeds (spec §10.1): through the node with a scripted stream, and the real process streamer on each OS.</summary>
public class FeedStreamTests : IAsyncLifetime
{
    private const string Command = "ssh -o BatchMode=yes isos-dell node D:/Tools/teamsfeed/stream.js";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ScriptedFeedRunner _runner = new();
    private readonly ScriptedFeedStreamer _streamer = new();
    private TestNode _alex = null!;
    private string _project = null!;

    public async ValueTask InitializeAsync()
    {
        _alex = await TestNode.StartAsync("alex", "desktop", adapters: [new FeedAdapter(_runner)], streamer: _streamer,
            sources: new SourceSettings(Timeout.InfiniteTimeSpan, TimeSpan.Zero, StreamStall: TimeSpan.FromMinutes(3), StreamBackoff: [TimeSpan.FromMilliseconds(50)]));
        _project = Directory.CreateDirectory(Path.Combine(_alex.Home.Root, "isos-admin")).FullName;
        Directory.CreateDirectory(Path.Combine(_project, ".git"));
        Directory.CreateDirectory(Path.Combine(_project, ".claude"));
    }

    public async ValueTask DisposeAsync() => await _alex.DisposeAsync();

    private static string Document(params string[] messages) =>
        new JsonObject
        {
            ["v"] = 1,
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = "teams:chat:19:abc@thread.v2",
                ["entity"] = "Chat | Jane Doe",
                ["events"] = new JsonArray([.. messages.Select(m => (JsonNode)new JsonObject { ["id"] = $"teams:msg:{m}", ["actor"] = "Jane Doe", ["text"] = m })]),
            }),
        }.ToJsonString();

    private string[] EventsOfTheItem() =>
        [.. _alex.Node.Open(Assert.Single(_alex.Node.ListInbox(null)).Id)!.Events!.Select(e => e.Summary)];

    [Fact]
    public async Task A_streaming_feed_is_never_polled_and_every_line_is_read_like_a_poll_as_it_arrives()
    {
        var notAFeed = _alex.Node.AddAccount("jira-work", "jira", "https://acme.atlassian.net", "me@acme.com", "712020:me", stream: true);
        Assert.Equal(ManagementStatus.Invalid, notAFeed.Status);
        Assert.Equal("Only a feed account can stream.", notAFeed.Reason);
        AccountStore.SaveToken(_alex.Home, "teams", Command);
        Assert.Equal(ManagementStatus.Ok, _alex.Node.AddAccount("teams", FeedAdapter.TypeName, "", "", FeedAdapter.TypeName, stream: true).Status);
        Assert.True(Assert.Single(_alex.Node.ListAccounts()).Stream);
        File.WriteAllText(Path.Combine(_project, ".claude", "rtfc.local.json"), """{"sources":[{"account":"teams","mode":"park"}]}""");
        _alex.Node.ApproveSources(_project);

        await _alex.Node.PollDueAsync(Ct);
        var run = await _streamer.NextRunAsync();
        Assert.Equal(Command, run.Command);
        Assert.Single(_alex.Node.RunningStreams());

        // The first line ever only takes note; the next one brings what is new.
        await run.SendAsync(Document("old"));
        Assert.Empty(_alex.Node.ListInbox(null));
        await run.SendAsync(Document("old", "Can you look?"));
        Assert.Equal(["Can you look?"], EventsOfTheItem());
        var view = Assert.Single(_alex.Node.SourceViews(_project));
        Assert.True(view.Stream);
        Assert.Null(view.LastError);
        Assert.NotNull(view.SeenUpTo);

        // A heartbeat repeats the document: nothing twice. The producer's own trouble shows, and the next good line clears it.
        await run.SendAsync(Document("old", "Can you look?"));
        await run.SendAsync("""{"v":1,"error":"teamsfeed: the Teams tab is gone"}""");
        Assert.Equal("The feed reports: teamsfeed: the Teams tab is gone", Assert.Single(_alex.Node.SourceViews(_project)).LastError);
        await run.SendAsync("not json at all");
        Assert.Contains("something other than JSON", Assert.Single(_alex.Node.SourceViews(_project)).LastError);
        await run.SendAsync(Document("old", "Can you look?"));
        Assert.Null(Assert.Single(_alex.Node.SourceViews(_project)).LastError);

        // A stream that ends is an error and starts again; the restart reads on from what was seen, so nothing is lost or repeated.
        run.End("The feed stream ended with exit 255: Connection reset by peer");
        var again = await _streamer.NextRunAsync();
        Assert.Contains("ended with exit 255: Connection reset by peer", Assert.Single(_alex.Node.SourceViews(_project)).LastError);
        Assert.Contains("Starting it again", Assert.Single(_alex.Node.SourceViews(_project)).LastError);
        await again.SendAsync(Document("old", "Can you look?", "Missed while it was down"));
        Assert.Equal(["Can you look?", "Missed while it was down"], EventsOfTheItem());
        Assert.Empty(_runner.Commands);

        // Nothing needs it any more: it stops.
        File.WriteAllText(Path.Combine(_project, ".claude", "rtfc.local.json"), """{"sources":[]}""");
        await _alex.Node.PollDueAsync(Ct);
        await again.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(_alex.Node.RunningStreams());
    }

    [Fact]
    public void A_feed_that_reports_its_own_error_fails_the_read_with_that_reason()
    {
        var ex = Assert.Throws<SourceException>(() => FeedAdapter.Read("teams", "{\"v\":1,\"error\":\"teamsfeed: feed.json is\\u0007 stale\\nsecond line\"}", SourceCursor.Empty, DateTimeOffset.UtcNow));
        Assert.Equal("The feed reports: teamsfeed: feed.json is stale", ex.Message);
        Assert.Equal(FeedAdapter.RetryAfterFailure, ex.RetryAfter);
    }

    [Fact]
    public async Task Lines_are_split_on_lf_or_crlf_with_the_last_one_unterminated_and_an_overlong_one_flagged()
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFFone\r\ntwo\n" + new string('x', 50) + "\nthree");
        var lines = new List<(string, bool)>();

        await ProcessFeedStreamer.ReadLinesAsync(new MemoryStream(bytes), 20, (line, overlong) =>
        {
            lines.Add((line, overlong));
            return Task.CompletedTask;
        }, Ct);

        Assert.Equal([("one", false), ("two", false), ("", true), ("three", false)], lines);
    }

    [Fact]
    public async Task The_real_streamer_hands_over_lines_as_they_come_and_says_how_the_command_ended()
    {
        var windows = OperatingSystem.IsWindows();
        var streamer = new ProcessFeedStreamer();
        var lines = new List<string>();

        var end = await streamer.RunAsync(
            windows
                ? """echo {"v":1,"items":[]}& ping -n 2 127.0.0.1 >nul & echo {"v":1,"error":"x"}& echo first 1>&2 & echo last words 1>&2 & exit /b 2"""
                : """echo '{"v":1,"items":[]}'; sleep 1; echo '{"v":1,"error":"x"}'; echo first >&2; echo last words >&2; exit 2""",
            line =>
            {
                lines.Add(line.Trim());
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(["""{"v":1,"items":[]}""", """{"v":1,"error":"x"}"""], lines);
        Assert.Equal("The feed stream ended with exit 2: last words", end.Reason);
    }

    [Fact]
    public async Task The_real_streamer_stops_a_stalled_command_a_too_long_line_and_on_cancellation()
    {
        var windows = OperatingSystem.IsWindows();
        using var temp = new TempHome();
        var file = Path.Combine(temp.Home.Root, "long.txt");
        File.WriteAllText(file, new string('x', 5000) + "\n");
        var seen = 0;
        Task Count(string _)
        {
            seen++;
            return Task.CompletedTask;
        }

        var stalled = await new ProcessFeedStreamer().RunAsync(windows ? "echo {}& ping -n 30 127.0.0.1 >nul" : "echo '{}'; sleep 30", Count, TimeSpan.FromSeconds(1), Ct);
        Assert.Equal("The feed stream printed nothing for 1 second(s), so it was stopped.", stalled.Reason);
        Assert.Equal(1, seen);

        var tooLong = await new ProcessFeedStreamer(2048).RunAsync(windows ? $"type \"{file}\"& ping -n 30 127.0.0.1 >nul" : $"cat '{file}'; sleep 30", Count, TimeSpan.FromSeconds(20), Ct);
        Assert.Equal("The feed stream printed a line longer than 2 KiB, so it was stopped.", tooLong.Reason);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessFeedStreamer().RunAsync(
            windows ? "echo {}& ping -n 30 127.0.0.1 >nul" : "echo '{}'; sleep 30",
            async _ => await cancel.CancelAsync(), TimeSpan.FromMinutes(1), cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Cancelling took {watch.Elapsed}.");
    }
}
