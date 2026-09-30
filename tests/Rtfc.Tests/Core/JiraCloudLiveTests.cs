using System.Text.Json.Nodes;
using Rtfc.Core.Sources;

namespace Rtfc.Tests.Core;

/// <summary>
/// Opt-in: the adapter against a real Jira Cloud site, read-only. Skipped unless <c>RTFC_LIVE_JIRA</c> holds the site URL and
/// <c>JIRA_CRED</c> holds <c>email:token</c>. Nothing here prints the token.
/// </summary>
public class JiraCloudLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_real_site_answers_identify_and_a_two_day_poll_with_well_formed_events()
    {
        var site = Environment.GetEnvironmentVariable("RTFC_LIVE_JIRA");
        var credential = Environment.GetEnvironmentVariable("JIRA_CRED");
        Assert.SkipWhen(string.IsNullOrEmpty(site) || string.IsNullOrEmpty(credential) || !credential.Contains(':'),
            "Set RTFC_LIVE_JIRA=https://<site>.atlassian.net and JIRA_CRED=email:token to run this against a real Jira Cloud site.");

        var colon = credential!.IndexOf(':');
        var account = new SourceAccount("live", JiraCloudAdapter.TypeName, site!, credential[..colon], null, credential[(colon + 1)..]);
        var adapter = new JiraCloudAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

        var identity = await adapter.IdentifyAsync(account, Ct);
        Assert.NotEmpty(identity.AccountId);
        Assert.NotEmpty(identity.DisplayName);

        var now = DateTimeOffset.UtcNow;
        var selector = new JsonObject { ["jql"] = "assignee = currentUser() OR reporter = currentUser() OR watcher = currentUser()" };
        var result = await adapter.PollAsync(account with { AccountId = identity.AccountId }, selector, new SourceCursor(now - TimeSpan.FromDays(2), []), now, Ct);

        Assert.NotNull(result.Next.Watermark);
        Assert.All(result.Events, e =>
        {
            Assert.StartsWith("jira:", e.EntityKey);
            Assert.True(SourceEventType.IsKnown(e.EventType), e.EventType);
            Assert.StartsWith(site!.TrimEnd('/'), e.Url);
            Assert.NotEmpty(e.Title);
            Assert.NotEmpty(e.Summary);
            Assert.NotEqual(identity.DisplayName, e.Actor);
            Assert.InRange(e.OccurredAt, now - TimeSpan.FromDays(2) - TimeSpan.FromMinutes(1), now + TimeSpan.FromMinutes(5));
        });
        Assert.Equal(result.Events.Count, result.Events.Select(e => e.ExternalId).Distinct().Count());
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{identity.DisplayName}: {result.Events.Count} event(s) in two days across {result.Events.Select(e => e.EntityKey).Distinct().Count()} ticket(s); "
            + $"types: {string.Join(", ", result.Events.GroupBy(e => e.EventType).Select(g => $"{g.Key}={g.Count()}"))}; watermark {result.Next.Watermark:O}");
    }
}
