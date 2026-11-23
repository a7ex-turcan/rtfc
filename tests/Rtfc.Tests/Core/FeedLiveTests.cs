using System.Text.Json.Nodes;
using Rtfc.Core;
using Rtfc.Core.Sources;

namespace Rtfc.Tests.Core;

/// <summary>
/// Opt-in: the feed adapter against a real feed command, through the real shell. Skipped unless <c>RTFC_LIVE_FEED</c> holds the
/// command. Only counts are printed, never what the feed says.
/// </summary>
public class FeedLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_real_feed_answers_identify_and_a_poll_with_well_formed_events()
    {
        var command = Environment.GetEnvironmentVariable("RTFC_LIVE_FEED");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(command), "Set RTFC_LIVE_FEED to a feed command, e.g. `ssh host type feed.json`, to run this against a real feed.");

        var account = new SourceAccount("live", FeedAdapter.TypeName, "", "", FeedAdapter.TypeName, command!);
        var adapter = new FeedAdapter(new ProcessFeedRunner(TimeSpan.FromSeconds(60), ProcessFeedRunner.DefaultMaxOutputBytes));

        var identity = await adapter.IdentifyAsync(account, Ct);

        // A cursor that has seen nothing yet but is past its first poll: everything the feed holds counts as new.
        var now = DateTimeOffset.UtcNow;
        var result = await adapter.PollAsync(account, new JsonObject(), new SourceCursor(now, []), now, Ct);
        Assert.All(result.Events, e =>
        {
            Assert.True(Node.IsValidEntityKey(e.EntityKey), e.EntityKey);
            Assert.StartsWith("feed:live/", e.EntityKey);
            Assert.True(SourceEventType.IsKnown(e.EventType), e.EventType);
            Assert.NotEmpty(e.Title);
        });
        Assert.Equal(result.Events.Count, result.Events.Select(e => e.ExternalId).Distinct().Count());
        Assert.Equal(result.Events.Count, result.Next.BoundaryIds.Length);

        var again = await adapter.PollAsync(account, new JsonObject(), result.Next, now.AddMinutes(1), Ct);
        Assert.All(again.Events, e => Assert.DoesNotContain(e.ExternalId, result.Events.Select(r => r.ExternalId)));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"identify: {identity.DisplayName}; poll: {result.Events.Count} event(s) across {result.Events.Select(e => e.EntityKey).Distinct().Count()} item(s); "
            + $"types: {string.Join(", ", result.Events.GroupBy(e => e.EventType).Select(g => $"{g.Key}={g.Count()}"))}");
    }
}
