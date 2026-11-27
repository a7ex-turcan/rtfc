using Microsoft.Extensions.Logging;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>
/// A failing source says so (spec §10.6). A poll's or a stream's error used to show only in the <c>sources</c> view, so a source
/// that was down for a day looked just like a quiet one. Once a poll has been failing for <see cref="SourceSettings.Outage"/>,
/// each project subscribed to it gets one parked notice, and its status line a warning, for as long as the outage lasts. When the
/// source works again, or is no longer polled in that project, the notice is dismissed with a note and the warning goes.
/// </summary>
public sealed partial class Node
{
    /// <summary>
    /// One pass over every active poll, at the end of each tick of the source loop: clears the notices whose outage is over, then
    /// tells the user about each poll that has failed long enough and has not been reported yet. Under the delivery lock, so a
    /// stream that recovers or fails meanwhile is seen either before or after, never halfway.
    /// </summary>
    private void ReportOutages(IReadOnlyList<IGrouping<string, SubscriptionRow>> groups, IReadOnlyDictionary<string, AccountRow> accounts)
    {
        lock (_deliverLock)
        {
            var now = _clock.GetUtcNow();
            var cursors = groups.ToDictionary(g => g.Key, g => _db.GetCursor(g.Key), StringComparer.Ordinal);
            var changed = false;
            foreach (var notice in _db.ListOpenOutageNotices())
            {
                var key = notice.EntityKey!;
                var polledHere = cursors.ContainsKey(key) && groups.Any(g => g.Key == key && g.Any(s => s.ProjectId == notice.ProjectId));
                var note = !polledHere ? "This source is no longer polled in this project, so the notice no longer applies."
                    : cursors[key]?.OutageNoticed is null ? $"It works again as of {Timestamps.Format(now)}."
                    : null;
                if (note is not null)
                {
                    _db.SetMessageNote(notice.Id, note, now);
                    _db.SetMessageState(notice.Id, InboxState.Dismissed, Self.DeviceId, now);
                    changed = true;
                }
            }

            foreach (var group in groups)
            {
                if (cursors[group.Key] is not { LastError: { } error, FailingSince: { } since, OutageNoticed: null } || now - since < _sourceSettings.Outage)
                {
                    continue;
                }

                var account = group.First().Account;
                var text = $"{SourceLabel(account, accounts.GetValueOrDefault(account)?.Type)} has been failing since {Timestamps.Format(since)}, so nothing new "
                    + $"is coming in from it. Its last error: {FeedAdapter.Clean(error, MaxOutageErrorLength)}\n"
                    + "/rtfc:sources (or `rtfc sources`) shows how it is doing now. This notice clears itself once it works again.";
                var told = 0;
                foreach (var subscription in group.DistinctBy(s => s.ProjectId))
                {
                    Notice(text, now, subscription.ProjectId, subscription.Id, about: group.Key);
                    told++;
                }

                _db.SetOutageNoticed(group.Key, now);
                changed = true;
                _logger.LogWarning("{Account}: failing since {Since} ({Error}); told the user in {Count} project(s)", account, Timestamps.Format(since), error, told);
            }

            if (changed)
            {
                WriteStatus();
                InboxChanged?.Invoke();
            }
        }
    }

    /// <summary>A service's error can quote the service, so a notice, outside any wrapper, keeps one short line of it, as the <c>sources</c> view does.</summary>
    private const int MaxOutageErrorLength = 300;

    /// <summary>Per project, the accounts of its active subscriptions whose poll is in a reported outage: the status line's warning.</summary>
    private Dictionary<string, string[]> FailingSources()
    {
        var failing = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var group in _db.ListSubscriptions().Where(s => s.Status is SubscriptionStatus.Active or SubscriptionStatus.Error).GroupBy(PollKeyOf, StringComparer.Ordinal))
        {
            if (_db.GetCursor(group.Key)?.OutageNoticed is null)
            {
                continue;
            }

            foreach (var subscription in group)
            {
                if (!failing.TryGetValue(subscription.ProjectId, out var accounts))
                {
                    failing[subscription.ProjectId] = accounts = new SortedSet<string>(StringComparer.Ordinal);
                }

                accounts.Add(subscription.Account);
            }
        }

        return failing.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// How a notice names a source: by its account and the adapter's type. Both are safe outside a wrapper: an account name is
    /// checked against a small alphabet wherever it comes from (<see cref="AccountStore.IsValidName"/>), and a type is rtfc's own.
    /// </summary>
    private static string SourceLabel(string account, string? type) => type switch
    {
        FeedAdapter.TypeName => $"The {account} feed",
        null => $"The source {account}",
        _ => $"The {account} source ({type})",
    };
}
