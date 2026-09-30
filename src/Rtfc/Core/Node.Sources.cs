using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>How often the source loop wakes, and how long a subscription waits between polls (spec §10.1). Tests shorten both.</summary>
public sealed record SourceSettings(TimeSpan Tick, TimeSpan Interval)
{
    public static readonly SourceSettings Default = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(90));
}

/// <summary>
/// Sources (spec §10): third-party notifications polled into the inbox. Every project's <c>.claude/rtfc.local.json</c>
/// is read on each tick and mirrored into <c>subscriptions</c>, where an entry waits until <c>rtfc sources approve</c>
/// has seen it. Active subscriptions with the same account and selector share one poll, whose events fan out to each
/// of their projects as one coalesced item per entity. The daemon only reads from a source, and nothing a source
/// says leaves the untrusted wrapper.
/// </summary>
public sealed partial class Node
{
    private const int MaxEventsPerItem = 50;
    private const int MaxEntityKeyLength = 200;
    private static readonly TimeSpan PermanentErrorRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TransientErrorRetry = TimeSpan.FromMinutes(5);

    private readonly Channel<bool> _sourcesKick = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, string> _subscriptionFileErrors = new(StringComparer.Ordinal);
    private Task? _sourcesLoop;

    /// <summary>Raised after every tick of the source loop, whether or not anything was polled. Tests wait on it.</summary>
    public event Action? SourcesPolled;

    /// <summary>Asks the loop to run now, after an approval. Cheap and idempotent.</summary>
    public void KickSources() => _sourcesKick.Writer.TryWrite(true);

    /// <summary>An infinite tick means polling is off: only <see cref="PollDueAsync"/> called by hand runs, which is how tests drive it.</summary>
    private void StartSources()
    {
        if (_sourceSettings.Tick != Timeout.InfiniteTimeSpan)
        {
            _sourcesLoop = Task.Run(() => SourcesLoopAsync(_stopping.Token));
        }
    }

    private async Task StopSourcesAsync()
    {
        _sourcesKick.Writer.TryComplete();
        if (_sourcesLoop is not null)
        {
            try
            {
                await _sourcesLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SourcesLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollDueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The source loop failed; it will run again");
            }

            SourcesPolled?.Invoke();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_sourceSettings.Tick);
            try
            {
                if (!await _sourcesKick.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    return;
                }

                _sourcesKick.Reader.TryRead(out _);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // the tick elapsed
            }
        }
    }

    /// <summary>One tick: mirror every project's subscriptions file, then poll whatever is due.</summary>
    internal async Task PollDueAsync(CancellationToken cancellationToken)
    {
        foreach (var project in _db.ListProjects())
        {
            SyncSubscriptions(project);
        }

        var now = _clock.GetUtcNow();
        var active = _db.ListSubscriptions().Where(s => s.Status is SubscriptionStatus.Active or SubscriptionStatus.Error).ToList();
        var accounts = _db.ListAccounts().ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (var group in active.GroupBy(PollKeyOf, StringComparer.Ordinal))
        {
            var cursorRow = _db.GetCursor(group.Key);
            if (cursorRow?.NextPollAt is { } next && next > now)
            {
                continue;
            }

            var first = group.First();
            if (!accounts.TryGetValue(first.Account, out var account))
            {
                Fail(group, cursorRow, $"The account \"{first.Account}\" does not exist; `rtfc account add {first.Account}` creates it.", permanent: true, now);
                continue;
            }

            if (!_adapters.TryGetValue(account.Type, out var adapter))
            {
                Fail(group, cursorRow, $"No adapter for {account.Type} yet.", permanent: true, now);
                continue;
            }

            var token = AccountStore.LoadToken(_home, account.Name);
            if (token is null)
            {
                Fail(group, cursorRow, $"No token for the account \"{account.Name}\"; `rtfc account add {account.Name}` stores one.", permanent: true, now);
                continue;
            }

            var selector = JsonNode.Parse(first.Selector) as JsonObject ?? new JsonObject();
            var cursor = ParseCursor(cursorRow);
            try
            {
                var result = await adapter.PollAsync(
                    new SourceAccount(account.Name, account.Type, account.BaseUrl ?? "", account.Login ?? "", account.AccountId, token), selector, cursor, now, cancellationToken)
                    .ConfigureAwait(false);
                var delivered = 0;
                foreach (var subscription in group)
                {
                    delivered += Deliver(subscription, result.Events, now);
                    if (subscription.Status == SubscriptionStatus.Error)
                    {
                        _db.SetSubscriptionStatus(subscription.Id, SubscriptionStatus.Active);
                    }
                }

                _db.UpsertCursor(new SourceCursorRow(group.Key, FormatCursor(result.Next), BoundaryJson(result.Next), now + NextInterval(result.RetryAfter), LastError: null));
                if (delivered > 0)
                {
                    _logger.LogInformation("{Account}: {Count} source event(s) landed in {Projects} project(s)", account.Name, delivered, group.Count());
                }
                else if (cursorRow is null || cursorRow.LastError is not null)
                {
                    // The first poll, or the first good one after an error: worth a line, so a quiet subscription is known to work.
                    _logger.LogInformation("{Account}: polled, nothing new; seen up to {Watermark}", account.Name, FormatCursor(result.Next));
                }
            }
            catch (SourceException ex)
            {
                Fail(group, cursorRow, ex.Message, ex.Permanent, now, ex.RetryAfter);
                _logger.LogWarning("Polling {Account} failed: {Reason}", account.Name, ex.Message);
            }
        }
    }

    private void Fail(IEnumerable<SubscriptionRow> group, SourceCursorRow? cursor, string reason, bool permanent, DateTimeOffset now, TimeSpan? retryAfter = null)
    {
        var key = PollKeyOf(group.First());
        _db.UpsertCursor(new SourceCursorRow(key, cursor?.Cursor ?? "", cursor?.BoundaryIds ?? "[]",
            now + (retryAfter ?? (permanent ? PermanentErrorRetry : TransientErrorRetry)), reason));
        if (permanent)
        {
            foreach (var subscription in group)
            {
                _db.SetSubscriptionStatus(subscription.Id, SubscriptionStatus.Error);
            }
        }
    }

    /// <summary>Stores the events a subscription asked for, one item per entity, appending to what the item already knows (spec §10.3).</summary>
    private int Deliver(SubscriptionRow subscription, IReadOnlyList<SourceEvent> events, DateTimeOffset now)
    {
        var wanted = (JsonSerializer.Deserialize(subscription.Events, SourceJson.Default.StringArray) ?? []).ToHashSet(StringComparer.Ordinal);
        var delivered = 0;
        foreach (var entity in events.Where(e => wanted.Contains(e.EventType) && IsValidEntityKey(e.EntityKey)).GroupBy(e => e.EntityKey, StringComparer.Ordinal))
        {
            var incoming = entity.OrderBy(e => e.OccurredAt).ToList();
            var (_, _, added) = _db.UpsertSourceItem(subscription.ProjectId, subscription.Id, entity.Key, Self.DeviceId, now, stored =>
            {
                var known = stored is { } json ? JsonSerializer.Deserialize(json, SourceJson.Default.SourceEventArray) ?? [] : [];
                var knownIds = known.Select(e => e.ExternalId).ToHashSet(StringComparer.Ordinal);
                var fresh = incoming.Where(e => !knownIds.Contains(e.ExternalId)).ToList();
                if (fresh.Count == 0)
                {
                    return null;
                }

                var history = known.Concat(fresh).TakeLast(MaxEventsPerItem).ToArray();
                var latest = fresh[^1];
                return new SourceMerge(latest.Title, latest.Url, latest.Summary, JsonSerializer.Serialize(history, SourceJson.Default.SourceEventArray), fresh.Count);
            });
            delivered += added;
        }

        if (delivered > 0)
        {
            WriteStatus();
            InboxChanged?.Invoke();
        }

        return delivered;
    }

    /// <summary>Mirrors a project's <c>.claude/rtfc.local.json</c> into <c>subscriptions</c>: new or edited entries wait for approval, removed ones go.</summary>
    internal void SyncSubscriptions(ProjectRow project)
    {
        var existing = _db.ListSubscriptions(project.Id);
        var document = SubscriptionsFile.Read(project.RootPath);
        if (document is null)
        {
            _subscriptionFileErrors.Remove(project.Id);
            foreach (var row in existing)
            {
                _db.DeleteSubscription(row.Id);
            }

            if (existing.Count > 0)
            {
                WriteStatus();
            }

            return;
        }

        if (document.Error is { } error)
        {
            // Keep what was approved; a half-edited file must not silently switch anything off.
            if (_subscriptionFileErrors.GetValueOrDefault(project.Id) != error)
            {
                _subscriptionFileErrors[project.Id] = error;
                _logger.LogWarning("{Project}: {Error}", project.Name, error);
            }

            return;
        }

        _subscriptionFileErrors.Remove(project.Id);
        var now = _clock.GetUtcNow();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var spec in document.Sources)
        {
            if (existing.FirstOrDefault(s => s.ConfigHash == spec.ConfigHash && !keep.Contains(s.Id)) is { } match)
            {
                keep.Add(match.Id);
                continue;
            }

            var row = new SubscriptionRow(
                Ulid.NewUlid(now), project.Id, spec.Account, SubscriptionsFile.Canonical(spec.Selector),
                JsonSerializer.Serialize(spec.Events, SourceJson.Default.StringArray), spec.Mode, SubscriptionStatus.PendingApproval, spec.ConfigHash);
            _db.UpsertSubscription(row);
            keep.Add(row.Id);
            changed = true;
            _logger.LogInformation("{Project}: a {Account} subscription waits for `rtfc sources approve`", project.Name, spec.Account);
        }

        foreach (var stale in existing.Where(s => !keep.Contains(s.Id)))
        {
            _db.DeleteSubscription(stale.Id);
            changed = true;
        }

        if (changed)
        {
            WriteStatus();
        }
    }

    /// <summary>Activates a project's pending subscriptions (spec §10.2). CLI-only: this is what lets a source reach the user.</summary>
    public ManagementResult ApproveSources(string directory)
    {
        var project = RegisterProject(directory);
        SyncSubscriptions(project);
        if (_subscriptionFileErrors.TryGetValue(project.Id, out var error))
        {
            return new ManagementResult(ManagementStatus.Invalid, project.Name, error);
        }

        var pending = _db.ListSubscriptions(project.Id).Where(s => s.Status == SubscriptionStatus.PendingApproval).ToList();
        foreach (var subscription in pending)
        {
            _db.SetSubscriptionStatus(subscription.Id, SubscriptionStatus.Active);
            _logger.LogInformation("{Project}: {Account} subscription approved", project.Name, subscription.Account);
        }

        WriteStatus();
        KickSources();
        return new ManagementResult(ManagementStatus.Ok, project.Name, pending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Stops polling a project and removes its source items (spec §10.2). Messages addressed to it fall back to the shared inbox.</summary>
    public ManagementResult ForgetProject(string directory)
    {
        var project = _db.GetProjectByRoot(ProjectPaths.Key(ProjectPaths.Root(directory)));
        if (project is null)
        {
            return new ManagementResult(ManagementStatus.Invalid, Reason: "No project is registered at that path.");
        }

        var keys = _db.ListSubscriptions(project.Id).Select(PollKeyOf).ToHashSet(StringComparer.Ordinal);
        _db.DeleteProject(project.Id);
        foreach (var key in keys.Where(k => !_db.ListSubscriptions().Any(s => PollKeyOf(s) == k)))
        {
            _db.DeleteCursor(key);
        }

        _subscriptionFileErrors.Remove(project.Id);
        _logger.LogInformation("Project {Name} forgotten", project.Name);
        WriteStatus();
        InboxChanged?.Invoke();
        return new ManagementResult(ManagementStatus.Ok, project.Name);
    }

    /// <summary>The subscriptions of one project, or of all of them, with their health (the <c>sources</c> tool, spec §9.2). Never a token.</summary>
    public SourceView[] SourceViews(string? directory)
    {
        var projects = _db.ListProjects().ToDictionary(p => p.Id, StringComparer.Ordinal);
        string? here = null;
        if (directory is not null)
        {
            var project = RegisterProject(directory);
            SyncSubscriptions(project);
            here = project.Id;
        }

        var accounts = _db.ListAccounts().ToDictionary(a => a.Name, StringComparer.Ordinal);
        var views = new List<SourceView>();
        foreach (var subscription in _db.ListSubscriptions(here))
        {
            var cursor = _db.GetCursor(PollKeyOf(subscription));
            var account = accounts.GetValueOrDefault(subscription.Account);
            views.Add(new SourceView(
                projects.GetValueOrDefault(subscription.ProjectId)?.Name ?? "?", subscription.Account, account?.Type ?? "?",
                subscription.Selector, JsonSerializer.Deserialize(subscription.Events, SourceJson.Default.StringArray) ?? [], subscription.Mode, subscription.Status,
                _db.ListMessages(null).Count(m => m.Kind == InboxKind.Source && m.SubscriptionId == subscription.Id && NeedsAttention(m)),
                Timestamps.ParseOrNull(cursor?.Cursor is { Length: > 0 } c ? c : null), cursor?.NextPollAt, cursor?.LastError,
                here is not null && _subscriptionFileErrors.TryGetValue(here, out var fileError) ? fileError : null));
        }

        if (here is not null && views.Count == 0 && _subscriptionFileErrors.TryGetValue(here, out var onlyError))
        {
            views.Add(new SourceView(projects[here].Name, "", "", "{}", [], "", SubscriptionStatus.Error, 0, null, null, null, onlyError));
        }

        return [.. views];
    }

    // ---- accounts (spec §10.5): the row here, the token in a file the CLI wrote ----

    public ManagementResult AddAccount(string name, string type, string baseUrl, string login, string? accountId)
    {
        if (!AccountStore.IsValidName(name))
        {
            return new ManagementResult(ManagementStatus.Invalid, Reason: "An account name is letters, digits, '.', '-' or '_', up to 64 characters.");
        }

        if (!_adapters.ContainsKey(type))
        {
            return new ManagementResult(ManagementStatus.Invalid, Reason: $"Unknown account type \"{type}\"; supported: {string.Join(", ", _adapters.Keys.Order(StringComparer.Ordinal))}.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ManagementResult(ManagementStatus.Invalid, Reason: "The URL must be absolute and https.");
        }

        _db.UpsertAccount(new AccountRow(name, type, uri.ToString().TrimEnd('/'), login, accountId, _clock.GetUtcNow()));
        _logger.LogInformation("Account {Name} ({Type}) at {Url} added", name, type, uri.Host);
        KickSources();
        return new ManagementResult(ManagementStatus.Ok, name);
    }

    public ManagementResult RemoveAccount(string name)
    {
        if (!_db.DeleteAccount(name))
        {
            return new ManagementResult(ManagementStatus.Invalid, Reason: $"No account named \"{name}\".");
        }

        _logger.LogInformation("Account {Name} removed", name);
        WriteStatus();
        return new ManagementResult(ManagementStatus.Ok, name);
    }

    public AccountView[] ListAccounts()
    {
        var subscriptions = _db.ListSubscriptions();
        return [.. _db.ListAccounts().Select(a => new AccountView(
            a.Name, a.Type, a.BaseUrl ?? "", a.Login ?? "", a.AccountId, a.CreatedAt, subscriptions.Count(s => s.Account == a.Name),
            AccountStore.LoadToken(_home, a.Name) is not null))];
    }

    // ---- helpers ----

    private static string PollKeyOf(SubscriptionRow s) => SubscriptionsFile.PollKey(s.Account, JsonNode.Parse(s.Selector) as JsonObject ?? new JsonObject());

    private static SourceCursor ParseCursor(SourceCursorRow? row) => row is null
        ? SourceCursor.Empty
        : new SourceCursor(Timestamps.ParseOrNull(row.Cursor is { Length: > 0 } c ? c : null), JsonSerializer.Deserialize(row.BoundaryIds, SourceJson.Default.StringArray) ?? []);

    private static string FormatCursor(SourceCursor cursor) => cursor.Watermark is { } w ? Timestamps.Format(w) : "";

    private static string BoundaryJson(SourceCursor cursor) => JsonSerializer.Serialize(cursor.BoundaryIds, SourceJson.Default.StringArray);

    private TimeSpan NextInterval(TimeSpan? retryAfter)
    {
        var interval = _sourceSettings.Interval;
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)(interval.TotalMilliseconds / 3) + 1));
        var wait = interval + jitter;
        return retryAfter is { } after && after > wait ? after : wait;
    }

    /// <summary>An entity key is <c>type:id</c> in a small alphabet; it is shown outside the wrapper, so it is checked like a project name.</summary>
    internal static bool IsValidEntityKey(string key) =>
        key.Length is > 2 and <= MaxEntityKeyLength
        && key.IndexOf(':') is > 0 and var colon && colon < key.Length - 1
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.' or '/' or '#' or '@');

    /// <summary>The source type of an item, from its entity key: <c>jira:PAY-1</c> is <c>jira</c>.</summary>
    internal static string SourceTypeOf(string? entityKey) => entityKey is { } key && key.IndexOf(':') is > 0 and var i ? key[..i] : "source";

    /// <summary>The part of an entity key people recognize: <c>PAY-1</c> of <c>jira:PAY-1</c>.</summary>
    internal static string EntityIdOf(string? entityKey) => entityKey is { } key && key.IndexOf(':') is > 0 and var i ? key[(i + 1)..] : entityKey ?? "";
}
