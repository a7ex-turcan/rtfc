using System.Text.Json.Nodes;
using Rtfc.Core.Sources;

namespace Rtfc.Tests.Core;

/// <summary>
/// Opt-in: the Bitbucket adapter against a real repository, read-only. Skipped unless <c>RTFC_LIVE_BITBUCKET</c> names
/// <c>workspace/slug</c> and <c>BB_TOKEN</c> holds <c>email:token</c> (a scoped API token). The token is never printed.
/// </summary>
public class BitbucketCloudLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_real_repository_answers_identify_and_a_week_long_poll_with_well_formed_events()
    {
        var repo = Environment.GetEnvironmentVariable("RTFC_LIVE_BITBUCKET");
        var credential = Environment.GetEnvironmentVariable("BB_TOKEN");
        Assert.SkipWhen(string.IsNullOrEmpty(repo) || !repo.Contains('/') || string.IsNullOrEmpty(credential) || !credential.Contains(':'),
            "Set RTFC_LIVE_BITBUCKET=workspace/slug and BB_TOKEN=email:token (a scoped Atlassian API token for Bitbucket) to run this against a real repository.");

        var colon = credential!.IndexOf(':');
        var account = new SourceAccount("live", BitbucketCloudAdapter.TypeName, BitbucketCloudAdapter.ApiBase, credential[..colon], null, credential[(colon + 1)..]);
        var adapter = new BitbucketCloudAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

        var identity = await adapter.IdentifyAsync(account, Ct);
        Assert.NotEmpty(identity.AccountId);

        var now = DateTimeOffset.UtcNow;
        var result = await adapter.PollAsync(account with { AccountId = identity.AccountId }, new JsonObject { ["repo"] = repo }, new SourceCursor(now - TimeSpan.FromDays(7), []), now, Ct);

        Assert.NotNull(result.Next.Watermark);
        Assert.All(result.Events, e =>
        {
            Assert.StartsWith($"bitbucket:{repo}#", e.EntityKey);
            Assert.True(SourceEventType.IsKnown(e.EventType), e.EventType);
            Assert.StartsWith("https://bitbucket.org/", e.Url);
            Assert.NotEmpty(e.Title);
            Assert.NotEmpty(e.Summary);
            Assert.NotEqual(identity.DisplayName, e.Actor);
        });
        Assert.Equal(result.Events.Count, result.Events.Select(e => e.ExternalId).Distinct().Count());
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{identity.DisplayName}: {result.Events.Count} event(s) in a week across {result.Events.Select(e => e.EntityKey).Distinct().Count()} pull request(s); "
            + $"types: {string.Join(", ", result.Events.GroupBy(e => e.EventType).Select(g => $"{g.Key}={g.Count()}"))}");
    }
}
