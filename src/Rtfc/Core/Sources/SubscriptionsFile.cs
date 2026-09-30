using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rtfc.Storage;

namespace Rtfc.Core.Sources;

/// <summary>
/// One entry of a project's <c>.claude/rtfc.local.json</c> (spec §10.2), parsed. <c>Selector</c> is everything in the entry
/// that is not <c>account</c>, <c>type</c>, <c>events</c> or <c>mode</c>: <c>jql</c> for Jira, <c>repo</c> for Bitbucket, and so on.
/// <c>ConfigHash</c> changes with any edit, which is what sends an approved subscription back to pending.
/// </summary>
public sealed record SubscriptionSpec(string Account, string? Type, JsonObject Selector, string[] Events, string Mode, string ConfigHash);

/// <summary>The file as read: its entries, or why it could not be used.</summary>
public sealed record SubscriptionsDocument(IReadOnlyList<SubscriptionSpec> Sources, string? Error);

/// <summary>
/// Reads <c>&lt;project&gt;/.claude/rtfc.local.json</c>. The file is editable by anything that can write to the repo, including
/// Claude, so nothing in it is trusted until <c>rtfc sources approve</c> has seen it: the daemon only records what it says.
/// </summary>
public static class SubscriptionsFile
{
    public const string RelativePath = ".claude/rtfc.local.json";
    private const int MaxEntries = 32;

    public static string PathFor(string projectRoot) => Path.Combine(projectRoot, ".claude", "rtfc.local.json");

    /// <summary>Null when there is no file.</summary>
    public static SubscriptionsDocument? Read(string projectRoot)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            return new SubscriptionsDocument([], $"Could not read {RelativePath}: {ex.Message}");
        }

        return Parse(text);
    }

    public static SubscriptionsDocument Parse(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return new SubscriptionsDocument([], $"{RelativePath} is not valid JSON: {ex.Message}");
        }

        if (root is not JsonObject document)
        {
            return new SubscriptionsDocument([], $"{RelativePath} must be a JSON object with a \"sources\" array.");
        }

        if (document["sources"] is not JsonArray sources)
        {
            return new SubscriptionsDocument([], null);
        }

        if (sources.Count > MaxEntries)
        {
            return new SubscriptionsDocument([], $"{RelativePath} lists {sources.Count} sources; the limit is {MaxEntries}.");
        }

        var specs = new List<SubscriptionSpec>();
        foreach (var entry in sources)
        {
            if (entry is not JsonObject item)
            {
                return new SubscriptionsDocument([], $"Every entry of \"sources\" must be an object.");
            }

            var account = item["account"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(account) || !AccountStore.IsValidName(account))
            {
                return new SubscriptionsDocument([], "Every source needs an \"account\": the name given to `rtfc account add`.");
            }

            var type = item["type"]?.GetValue<string>();
            var mode = item["mode"]?.GetValue<string>() ?? SourceMode.Park;
            if (mode is not (SourceMode.Park or SourceMode.Prepare or SourceMode.Session))
            {
                return new SubscriptionsDocument([], $"Source \"{account}\": mode must be park, prepare or session, not \"{mode}\".");
            }

            string[] events;
            if (item["events"] is JsonArray list)
            {
                events = [.. list.Select(e => e?.GetValue<string>() ?? "").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                if (events.Length == 0 || events.Any(e => !SourceEventType.IsKnown(e)))
                {
                    return new SubscriptionsDocument([], $"Source \"{account}\": events must be a non-empty list from: {string.Join(", ", SourceEventType.All)}.");
                }
            }
            else
            {
                events = SourceEventType.All;
            }

            var selector = new JsonObject();
            foreach (var (key, value) in item.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (key is "account" or "type" or "events" or "mode")
                {
                    continue;
                }

                selector[key] = value?.DeepClone();
            }

            var hash = Hash(account, type, mode, events, selector);
            specs.Add(new SubscriptionSpec(account, type, selector, events, mode, hash));
        }

        return new SubscriptionsDocument(specs, null);
    }

    /// <summary>The poll key of spec §10.1: subscriptions with the same account and selector share one poll across projects.</summary>
    public static string PollKey(string account, JsonObject selector) => $"{account}:{Sha256(Canonical(selector))[..16]}";

    private static string Hash(string account, string? type, string mode, string[] events, JsonObject selector) =>
        Sha256($"{account}\n{type}\n{mode}\n{string.Join(",", events)}\n{Canonical(selector)}");

    /// <summary>JSON with object keys in a fixed order, so the same selector always hashes the same.</summary>
    public static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonValue.Create(p.Key)!.ToJsonString() + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
