using Microsoft.Extensions.Logging;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>How often held pushes are checked, and how long a session counts as busy right after a push. Tests shorten both.</summary>
public sealed record SessionPushSettings(TimeSpan Check, TimeSpan Settle)
{
    public static readonly SessionPushSettings Default = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
}

/// <summary>
/// Pushes into a session only while it is idle (spec §7.3, §10.4). A contact's message or a source item for a session that is in
/// the middle of a turn waits here, and goes in, one at a time, once the plugin's hook has marked the turn over. Right after a push
/// the session counts as busy for a moment, until Claude Code has turned the push into a prompt and the hook has seen it, so two
/// items that arrive together go in one turn after the other. Everything held is parked in the inbox as well, with a note, so
/// nothing is lost when the daemon restarts or the session closes.
/// </summary>
public sealed partial class Node
{
    private enum PushOutcome { Sent, Held, NotOpen }

    private sealed record HeldPush(string MessageId, SessionEvent Event, Func<DateTimeOffset, string> SentNote);

    private readonly Lock _pushLock = new();
    private readonly Dictionary<string, List<HeldPush>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastPush = new(StringComparer.Ordinal);
    private Task? _pushLoop;

    /// <summary>
    /// Sends the push now if the session is idle and nothing of its waits ahead, else holds it. <paramref name="record"/> writes the
    /// item's note for the outcome and runs under the lock, so a held item's note is written before a flush can send it.
    /// </summary>
    private PushOutcome PushOrHold(string session, SessionEvent sessionEvent, Func<DateTimeOffset, string> sentNote, Action<PushOutcome> record)
    {
        if (_sessions is null)
        {
            record(PushOutcome.NotOpen);
            return PushOutcome.NotOpen;
        }

        lock (_pushLock)
        {
            var now = _clock.GetUtcNow();
            PushOutcome outcome;
            if (!_sessions.IsOpen(session))
            {
                outcome = PushOutcome.NotOpen;
            }
            else if (!(_held.TryGetValue(session, out var waiting) && waiting.Count > 0) && IsIdle(session, now) && _sessions.TryPush(session, sessionEvent))
            {
                _lastPush[session] = now;
                outcome = PushOutcome.Sent;
            }
            else
            {
                if (!_held.TryGetValue(session, out var list))
                {
                    _held[session] = list = [];
                }

                list.Add(new HeldPush(sessionEvent.Id, sessionEvent, sentNote));
                outcome = PushOutcome.Held;
            }

            record(outcome);
            return outcome;
        }
    }

    private bool IsIdle(string session, DateTimeOffset now) =>
        !SessionActivity.IsBusy(_home, session, now)
        && !(_lastPush.TryGetValue(session, out var last) && now - last < _pushSettings.Settle);

    /// <summary>Sends the next held push of every session that is idle now. The loop calls it; tests call it by hand.</summary>
    internal void FlushHeldPushes()
    {
        var sent = new List<HeldPush>();
        var dropped = new List<HeldPush>();
        var now = _clock.GetUtcNow();
        lock (_pushLock)
        {
            foreach (var (session, list) in _held.ToList())
            {
                if (_sessions is null || !_sessions.IsOpen(session))
                {
                    dropped.AddRange(list);
                    _held.Remove(session);
                    continue;
                }

                if (!IsIdle(session, now))
                {
                    continue;
                }

                while (list.Count > 0)
                {
                    var next = list[0];
                    list.RemoveAt(0);
                    if (_db.GetMessage(next.MessageId) is not { State: InboxState.Parked })
                    {
                        continue; // answered, dismissed or gone while it waited
                    }

                    if (_sessions.TryPush(session, next.Event))
                    {
                        _lastPush[session] = now;
                        sent.Add(next);
                    }
                    else
                    {
                        dropped.Add(next);
                    }

                    break;
                }

                if (list.Count == 0)
                {
                    _held.Remove(session);
                }
            }
        }

        foreach (var push in sent)
        {
            _db.SetMessageNote(push.MessageId, push.SentNote(now), now);
            _logger.LogInformation("Held push {Id} sent now that its session is idle", push.MessageId);
        }

        foreach (var push in dropped)
        {
            _db.SetMessageNote(push.MessageId, "Not sent into a session: it closed while it was busy. It waits here.", now);
        }

        if (sent.Count > 0 || dropped.Count > 0)
        {
            WriteStatus();
            InboxChanged?.Invoke();
        }
    }

    private void StartSessionPushes()
    {
        if (_pushSettings.Check != Timeout.InfiniteTimeSpan)
        {
            _pushLoop = Task.Run(() => SessionPushLoopAsync(_stopping.Token));
        }
    }

    private async Task StopSessionPushesAsync()
    {
        if (_pushLoop is not null)
        {
            try
            {
                await _pushLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SessionPushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pushSettings.Check, cancellationToken).ConfigureAwait(false);
                bool any;
                lock (_pushLock)
                {
                    any = _held.Count > 0;
                }

                if (any)
                {
                    FlushHeldPushes();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending held pushes failed; it will run again");
            }
        }
    }

    /// <summary>A session's lease ended: what waited for it stays in the inbox, and says so.</summary>
    private void DropHeldPushes(string session)
    {
        List<HeldPush>? list;
        lock (_pushLock)
        {
            if (!_held.Remove(session, out list))
            {
                return;
            }

            _lastPush.Remove(session);
        }

        var now = _clock.GetUtcNow();
        foreach (var push in list)
        {
            _db.SetMessageNote(push.MessageId, "Not sent into a session: it closed while it was busy. It waits here.", now);
        }

        WriteStatus();
        InboxChanged?.Invoke();
    }
}
