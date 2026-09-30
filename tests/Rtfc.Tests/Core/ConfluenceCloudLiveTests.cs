using System.Text.Json.Nodes;
using Rtfc.Core.Sources;

namespace Rtfc.Tests.Core;

/// <summary>Opt-in: the Confluence adapter against the real site, read-only. Same switches as the Jira live test; the token is never printed.</summary>
public class ConfluenceCloudLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_real_site_answers_identify_and_a_week_long_poll_with_well_formed_events()
    {
        var site = Environment.GetEnvironmentVariable("RTFC_LIVE_JIRA");
        var credential = Environment.GetEnvironmentVariable("JIRA_CRED");
        Assert.SkipWhen(string.IsNullOrEmpty(site) || string.IsNullOrEmpty(credential) || !credential.Contains(':'),
            "Set RTFC_LIVE_JIRA=https://<site>.atlassian.net and JIRA_CRED=email:token to run this against a real Atlassian Cloud site.");

        var colon = credential!.IndexOf(':');
        var account = new SourceAccount("live", ConfluenceCloudAdapter.TypeName, site!, credential[..colon], null, credential[(colon + 1)..]);
        var adapter = new ConfluenceCloudAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

        var identity = await adapter.IdentifyAsync(account, Ct);
        Assert.NotEmpty(identity.AccountId);

        var now = DateTimeOffset.UtcNow;
        var result = await adapter.PollAsync(account with { AccountId = identity.AccountId }, new JsonObject(), new SourceCursor(now - TimeSpan.FromDays(7), []), now, Ct);

        Assert.NotNull(result.Next.Watermark);
        Assert.All(result.Events, e =>
        {
            Assert.StartsWith("confluence:", e.EntityKey);
            Assert.Contains(e.EventType, new[] { SourceEventType.Mentioned, SourceEventType.ReplyToMe, SourceEventType.CommentOnMine });
            Assert.StartsWith(site!.TrimEnd('/') + "/wiki/", e.Url);
            Assert.NotEmpty(e.Title);
            Assert.NotEqual(identity.DisplayName, e.Actor);
        });
        Assert.Equal(result.Events.Count, result.Events.Select(e => e.ExternalId).Distinct().Count());
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{identity.DisplayName}: {result.Events.Count} event(s) in a week across {result.Events.Select(e => e.EntityKey).Distinct().Count()} page(s); "
            + $"types: {string.Join(", ", result.Events.GroupBy(e => e.EventType).Select(g => $"{g.Key}={g.Count()}"))}");
    }
}
