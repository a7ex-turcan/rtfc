using System.Text.Json.Nodes;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

public class SubscriptionsFileTests
{
    [Fact]
    public void Entries_are_parsed_into_account_selector_events_and_mode()
    {
        var document = SubscriptionsFile.Parse("""
            {
              "sources": [
                { "account": "jira-work", "type": "jira", "jql": "project = PAY AND assignee = currentUser()", "events": ["mentioned", "assigned"] },
                { "account": "bb-work", "repo": "acme/payments-api", "mode": "session" }
              ]
            }
            """);

        Assert.Null(document.Error);
        Assert.Equal(2, document.Sources.Count);
        var jira = document.Sources[0];
        Assert.Equal("jira-work", jira.Account);
        Assert.Equal("jira", jira.Type);
        Assert.Equal(["assigned", "mentioned"], jira.Events);
        Assert.Equal(SourceMode.Park, jira.Mode);
        Assert.Equal("project = PAY AND assignee = currentUser()", jira.Selector["jql"]!.GetValue<string>());
        Assert.Null(jira.Selector["account"]);
        var bitbucket = document.Sources[1];
        Assert.Equal(SourceEventType.All, bitbucket.Events);
        Assert.Equal(SourceMode.Session, bitbucket.Mode);
        Assert.Equal("acme/payments-api", bitbucket.Selector["repo"]!.GetValue<string>());
    }

    [Fact]
    public void The_hash_follows_every_edit_and_ignores_key_order()
    {
        var a = SubscriptionsFile.Parse("""{"sources":[{"account":"j","jql":"x","events":["assigned","mentioned"]}]}""").Sources[0];
        var reordered = SubscriptionsFile.Parse("""{"sources":[{"events":["mentioned","assigned"],"jql":"x","account":"j"}]}""").Sources[0];
        var edited = SubscriptionsFile.Parse("""{"sources":[{"account":"j","jql":"y","events":["assigned","mentioned"]}]}""").Sources[0];
        var otherMode = SubscriptionsFile.Parse("""{"sources":[{"account":"j","jql":"x","events":["assigned","mentioned"],"mode":"session"}]}""").Sources[0];

        Assert.Equal(a.ConfigHash, reordered.ConfigHash);
        Assert.NotEqual(a.ConfigHash, edited.ConfigHash);
        Assert.NotEqual(a.ConfigHash, otherMode.ConfigHash);
        Assert.Equal(SubscriptionsFile.PollKey("j", a.Selector), SubscriptionsFile.PollKey("j", otherMode.Selector));
        Assert.NotEqual(SubscriptionsFile.PollKey("j", a.Selector), SubscriptionsFile.PollKey("j", edited.Selector));
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("""{"sources":[{"jql":"x"}]}""", "needs an \"account\"")]
    [InlineData("""{"sources":[{"account":"j","mode":"push"}]}""", "mode must be")]
    [InlineData("""{"sources":[{"account":"j","events":["tweeted"]}]}""", "events must be")]
    [InlineData("""{"sources":[{"account":"j","events":[]}]}""", "events must be")]
    [InlineData("""{"sources":["j"]}""", "must be an object")]
    public void A_broken_file_is_reported_not_guessed_at(string text, string error)
    {
        var document = SubscriptionsFile.Parse(text);

        Assert.NotNull(document.Error);
        Assert.Contains(error, document.Error);
        Assert.Empty(document.Sources);
    }

    [Fact]
    public void A_file_without_sources_means_none()
    {
        var document = SubscriptionsFile.Parse("""{"other": 1}""");

        Assert.Null(document.Error);
        Assert.Empty(document.Sources);
    }

    [Fact]
    public void Canonical_json_sorts_object_keys_at_every_level()
    {
        var node = JsonNode.Parse("""{"b":[{"z":1,"a":"x"}],"a":null}""");

        Assert.Equal("""{"a":null,"b":[{"a":"x","z":1}]}""", SubscriptionsFile.Canonical(node));
    }
}
