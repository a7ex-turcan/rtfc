using Microsoft.Extensions.Logging;
using Rtfc.Core.Sources;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>
/// Streaming feeds (spec §10.1): a feed account added with <c>--stream</c> is not polled. Its command keeps running, one
/// process per account and selector, shared by every subscription on it like a poll is, and each line it prints is a whole
/// feed document, read the moment it arrives exactly as a poll's result would be. The source loop's tick starts a stream that
/// an approved subscription needs and stops one nothing needs any more. A stream that ends or stalls is an error on its
/// subscriptions and is started again after a pause that grows while it keeps failing.
/// </summary>
public sealed partial class Node
{
    /// <summary>A run longer than this was healthy, so the next failure starts the back-off from the beginning.</summary>
    private static readonly TimeSpan HealthyStream = TimeSpan.FromMinutes(2);

    private readonly Lock _streamsLock = new();
    private readonly Dictionary<string, FeedStreamRun> _streams = new(StringComparer.Ordinal);

    /// <summary>Delivery from the poll and from every stream, one at a time, so an item's history is merged in order.</summary>
    private readonly Lock _deliverLock = new();

    private sealed record FeedStreamRun(string Account, string Command, CancellationTokenSource Stop, Task Task);

    /// <summary>The poll keys that run as streams right now. Tests look.</summary>
    internal string[] RunningStreams()
    {
        lock (_streamsLock)
        {
            return [.. _streams.Keys];
        }
    }

    /// <summary>Starts the streams that active subscriptions need and stops the rest, including one whose command changed.</summary>
    private void ReconcileStreams(IEnumerable<IGrouping<string, SubscriptionRow>> groups, IReadOnlyDictionary<string, AccountRow> accounts, DateTimeOffset now)
    {
        var wanted = new Dictionary<string, (string Account, string Command)>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (!accounts.TryGetValue(group.First().Account, out var account) || !IsStream(account))
            {
                continue;
            }

            if (AccountStore.LoadToken(_home, account.Name) is not { } command)
            {
                Fail(group, _db.GetCursor(group.Key), $"No command for the feed account \"{account.Name}\"; `rtfc account add {account.Name} --type feed --stream` stores one.", permanent: true, now);
                continue;
            }

            wanted[group.Key] = (account.Name, command);
        }

        lock (_streamsLock)
        {
            foreach (var (key, run) in _streams.ToList())
            {
                if (!wanted.TryGetValue(key, out var want) || want.Command != run.Command)
                {
                    _logger.LogInformation("{Account}: feed stream stopped", run.Account);
                    Cancel(run.Stop);
                    _streams.Remove(key);
                }
            }

            foreach (var (key, want) in wanted)
            {
                if (!_streams.ContainsKey(key))
                {
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    _streams[key] = new FeedStreamRun(want.Account, want.Command, stop, Task.Run(async () =>
                    {
                        try
                        {
                            await RunStreamAsync(key, want.Account, want.Command, stop.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            stop.Dispose();
                        }
                    }));
                }
            }
        }
    }

    private async Task StopStreamsAsync()
    {
        FeedStreamRun[] runs;
        lock (_streamsLock)
        {
            runs = [.. _streams.Values];
            _streams.Clear();
        }

        foreach (var run in runs)
        {
            Cancel(run.Stop);
        }

        try
        {
            await Task.WhenAll(runs.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    private async Task RunStreamAsync(string key, string account, string command, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = _clock.GetUtcNow();
            FeedStreamEnd end;
            try
            {
                _logger.LogInformation("{Account}: feed stream started", account);
                end = await _streamer.RunAsync(command, line => HandleStreamLine(key, account, line), _sourceSettings.Stall, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                end = new FeedStreamEnd($"The feed stream failed: {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (_clock.GetUtcNow() - started > HealthyStream)
            {
                failures = 0;
            }

            var backoff = _sourceSettings.Backoff;
            var wait = backoff[Math.Min(failures, backoff.Length - 1)];
            failures++;
            RecordStreamError(key, $"{end.Reason} Starting it again in {Words(wait)}.");
            _logger.LogWarning("{Account}: {Reason} Starting it again in {Wait}", account, end.Reason, wait);
            try
            {
                await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One line of a stream: a whole feed document, diffed against the cursor and delivered like a poll's result.</summary>
    private Task HandleStreamLine(string key, string account, string line)
    {
        try
        {
            var now = _clock.GetUtcNow();
            var subscriptions = _db.ListSubscriptions()
                .Where(s => s.Status is SubscriptionStatus.Active or SubscriptionStatus.Error && PollKeyOf(s) == key).ToList();
            if (subscriptions.Count == 0)
            {
                return Task.CompletedTask;
            }

            lock (_deliverLock)
            {
                var cursorRow = _db.GetCursor(key);
                PollResult result;
                try
                {
                    result = FeedAdapter.Read(account, line, ParseCursor(cursorRow), now);
                }
                catch (SourceException ex)
                {
                    RecordStreamError(key, ex.Message);
                    return Task.CompletedTask;
                }

                var delivered = 0;
                foreach (var subscription in subscriptions)
                {
                    delivered += Deliver(subscription, result.Events, now, filtered: false);
                    if (subscription.Status == SubscriptionStatus.Error)
                    {
                        _db.SetSubscriptionStatus(subscription.Id, SubscriptionStatus.Active);
                    }
                }

                _db.UpsertCursor(new SourceCursorRow(key, FormatCursor(result.Next), BoundaryJson(result.Next), NextPollAt: null, LastError: null));
                if (delivered > 0)
                {
                    _logger.LogInformation("{Account}: {Count} feed event(s) landed from the stream", account, delivered);
                }
                else if (cursorRow is null || cursorRow.LastError is not null)
                {
                    _logger.LogInformation("{Account}: the feed stream is reading; nothing new", account);
                }
            }
        }
        catch (Exception ex)
        {
            // A line that breaks something here must not end the stream; the next one may well be fine.
            _logger.LogError(ex, "{Account}: a feed stream line could not be handled", account);
        }

        return Task.CompletedTask;
    }

    /// <summary>A stream's trouble, shown on its subscriptions like a failed poll's; what it has seen is kept.</summary>
    private void RecordStreamError(string key, string reason)
    {
        lock (_deliverLock)
        {
            var cursor = _db.GetCursor(key);
            _db.UpsertCursor(new SourceCursorRow(key, cursor?.Cursor ?? "", cursor?.BoundaryIds ?? "[]", NextPollAt: null, reason));
        }
    }

    private static bool IsStream(AccountRow account) => account.Stream && account.Type == FeedAdapter.TypeName;

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string Words(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{span.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} minute(s)"
        : $"{span.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} second(s)";
}
