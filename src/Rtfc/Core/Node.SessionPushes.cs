using Microsoft.Extensions.Logging;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>
/// How often held pushes are checked, how long a session counts as busy right after a push, and how long after a session opens
/// the first push may go in: Claude Code drops a channel event sent before the session's channel is live. <c>Confirm</c> is how
/// long a push may go unseen while its session sits idle before the session counts as not showing pushes at all; null or infinite
/// turns confirmation off. Tests shorten or switch off all four.
/// </summary>
public sealed record SessionPushSettings(TimeSpan Check, TimeSpan Settle, TimeSpan OpenGrace, TimeSpan? Confirm = null)
{
    public static readonly SessionPushSettings Default = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2));
}

/// <summary>
/// Pushes into a session only while it is idle (spec §7.3, §10.4). A contact's message or a source item for a session that is in
/// the middle of a turn waits here, and goes in, one at a time, once the plugin's hook has marked the turn over. Right after a push
/// the session counts as busy for a moment, until Claude Code has turned the push into a prompt and the hook has seen it, so two
/// items that arrive together go in one turn after the other. Everything held is parked in the inbox as well, with a note, so
/// nothing is lost when the daemon restarts or the session closes.
///
/// Claude Code drops a push it cannot show without a word, which a session started without the channels flag does to every one.
/// So each push waits to be seen: the hook reports it when it becomes a prompt, and until then nothing more goes to that session.
/// A push still unseen after <see cref="SessionPushSettings.Confirm"/> of the session sitting idle means the session does not show
/// pushes. The item gives its attempt back and says why, nothing more is sent there, the status line warns, and the session is
/// offered everything again when it restarts.
/// </summary>
public sealed partial class Node
{
    private enum PushOutcome { Sent, Held, NotOpen, NotShown }

    private sealed record HeldPush(string MessageId, SessionEvent Event, Func<DateTimeOffset, string> SentNote);

    /// <summary>A push sent into a session and not yet seen there. <c>Since</c> moves on while the session is busy.</summary>
    private sealed record UnseenPush(SessionEvent Event, DateTimeOffset SentAt, DateTimeOffset Since);

    private readonly Lock _pushLock = new();
    private readonly Dictionary<string, List<HeldPush>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastPush = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnseenPush> _unseen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _notShowing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _givenUp = new(StringComparer.Ordinal);
    private Task? _pushLoop;

    private TimeSpan? ConfirmWindow => _pushSettings.Confirm is { } confirm && confirm != Timeout.InfiniteTimeSpan ? confirm : null;

    /// <summary>The flag a session needs for rtfc's pushes to show, as the notes quote it.</summary>
    public const string ChannelsFlag = "--dangerously-load-development-channels plugin:rtfc@rtfc";

    /// <summary>What a session that opens is offered: items and messages from no further back than this.</summary>
    private static readonly TimeSpan OfferWindow = TimeSpan.FromDays(7);

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
            else if (_notShowing.ContainsKey(session))
            {
                outcome = PushOutcome.NotShown;
            }
            else if (!(_held.TryGetValue(session, out var waiting) && waiting.Count > 0) && IsIdle(session, now) && _sessions.TryPush(session, sessionEvent))
            {
                Sent(session, sessionEvent, now);
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
        && !_unseen.ContainsKey(session)
        && !(_lastPush.TryGetValue(session, out var last) && now - last < _pushSettings.Settle)
        && !(_notBefore.TryGetValue(session, out var notBefore) && now < notBefore);

    /// <summary>Under the lock: a push went into the session, and waits to be seen there.</summary>
    private void Sent(string session, SessionEvent sessionEvent, DateTimeOffset now)
    {
        _lastPush[session] = now;
        if (ConfirmWindow is not null)
        {
            _unseen[session] = new UnseenPush(sessionEvent, now, now);
        }
    }

    /// <summary>Where a push went, in the words of its notes.</summary>
    private string SessionLabel(string session, SessionEvent pushed) =>
        pushed.Kind == InboxKind.Source
            ? $"your Claude Code session in {(_openSessions.TryGetValue(session, out var open) && _db.GetProject(open.ProjectId) is { } project ? project.Name : "its project")}"
            : $"the Claude Code session that answers {pushed.From}";

    private string NotShownNote(string session, SessionEvent pushed) =>
        $"Not sent into {SessionLabel(session, pushed)}: it does not show rtfc's pushes, most likely because it was started without {ChannelsFlag}. Restart it with that flag and this is offered again. It waits here.";

    /// <summary>
    /// From the plugin's hook (spec §7.3): a pushed item became a prompt in the session, so the session shows rtfc's pushes. A
    /// push already given up on came late after all: its note says so and its attempt counts again.
    /// </summary>
    public void PushShown(string session, string id)
    {
        bool late, wasNotShowing;
        lock (_pushLock)
        {
            if (_unseen.TryGetValue(session, out var unseen) && unseen.Event.Id == id)
            {
                _unseen.Remove(session);
            }

            late = _givenUp.Remove(id);
            wasNotShowing = _notShowing.Remove(session);
        }

        var now = _clock.GetUtcNow();
        if (late && _db.GetMessage(id) is { State: InboxState.Parked })
        {
            _db.CountAttempt(id);
            _db.SetMessageNote(id, $"Sent into your Claude Code session; it showed up there late, at {Timestamps.Format(now)}.", now);
        }

        if (wasNotShowing)
        {
            _logger.LogInformation("Session {Session} shows rtfc's pushes again", session);
        }

        if (late || wasNotShowing)
        {
            WriteStatus();
            InboxChanged?.Invoke();
        }
    }

    /// <summary>A decision the hook reported about an item is proof enough that it showed up.</summary>
    private void ShownSomewhere(string id)
    {
        string? session;
        lock (_pushLock)
        {
            session = _unseen.FirstOrDefault(u => u.Value.Event.Id == id).Key;
        }

        if (session is not null)
        {
            PushShown(session, id);
        }
    }

    /// <summary>
    /// Gives up on pushes that were never seen. A session that is busy is not counted against: Claude Code may hold the push until
    /// the turn ends. The gate file is checked too, in case the hook could not reach the daemon. The loop calls it; tests by hand.
    /// </summary>
    internal void CheckPushesShown()
    {
        if (ConfirmWindow is not { } confirm)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var lost = new List<(string Session, UnseenPush Push)>();
        var notSent = new List<(string Session, HeldPush Push)>();
        lock (_pushLock)
        {
            foreach (var (session, unseen) in _unseen.ToList())
            {
                if (_sessions is null || !_sessions.IsOpen(session) || SessionGates.Read(_home, session)?.Id == unseen.Event.Id)
                {
                    _unseen.Remove(session);
                }
                else if (SessionActivity.IsBusy(_home, session, now))
                {
                    _unseen[session] = unseen with { Since = now };
                }
                else if (now - unseen.Since >= confirm)
                {
                    _unseen.Remove(session);
                    _notShowing[session] = now;
                    _givenUp.Add(unseen.Event.Id);
                    lost.Add((session, unseen));
                    if (_held.Remove(session, out var held))
                    {
                        notSent.AddRange(held.Select(h => (session, h)));
                    }
                }
            }
        }

        foreach (var (session, unseen) in lost)
        {
            var label = SessionLabel(session, unseen.Event);
            _db.UncountAttempt(unseen.Event.Id);
            _db.SetMessageNote(unseen.Event.Id,
                $"Sent into {label} at {Timestamps.Format(unseen.SentAt)}, but it never showed up there. Claude Code drops rtfc's pushes in a session started without {ChannelsFlag}, so nothing more is sent there until it restarts with that flag; then this is offered again. It waits here.",
                now);
            _logger.LogWarning("A push into {Label} ({Session}) never showed up there; nothing more goes to that session until it restarts", label, session);
        }

        foreach (var (session, push) in notSent)
        {
            _db.UncountAttempt(push.MessageId);
            _db.SetMessageNote(push.MessageId, NotShownNote(session, push.Event), now);
        }

        if (lost.Count > 0)
        {
            WriteStatus();
            InboxChanged?.Invoke();
        }
    }

    /// <summary>The projects whose open session does not show pushes, for the status line.</summary>
    private HashSet<string> ProjectsNotShowingPushes()
    {
        lock (_pushLock)
        {
            return [.. _notShowing.Keys.Select(s => _openSessions.TryGetValue(s, out var open) ? open.ProjectId : null).OfType<string>()];
        }
    }

    /// <summary>
    /// A session registered again: a new Claude Code process for the same session, or the same one after a blip. What it was sent
    /// and never showed is offered again, and it gets a fresh chance to show pushes.
    /// </summary>
    private void SessionRestarted(string session)
    {
        UnseenPush? unseen;
        lock (_pushLock)
        {
            _notShowing.Remove(session);
            _unseen.Remove(session, out unseen);
        }

        if (unseen is not null)
        {
            _db.UncountAttempt(unseen.Event.Id);
            _db.SetMessageNote(unseen.Event.Id,
                $"Sent into {SessionLabel(session, unseen.Event)} at {Timestamps.Format(unseen.SentAt)}, which restarted before it showed up there. It is offered again.",
                _clock.GetUtcNow());
        }
    }

    /// <summary>
    /// A session opened (issue #2): offer it what never reached a session while none was there, oldest first, through the same
    /// guards, caps, idle hold and gate as a live push. Source items of its project in <c>session</c> mode, and messages from
    /// contacts that designate this session. The first goes in only after the open grace, once the session's channel is live.
    /// </summary>
    private void OfferWaiting(string session, ProjectRow project)
    {
        lock (_pushLock)
        {
            _notBefore[session] = _clock.GetUtcNow() + _pushSettings.OpenGrace;
        }

        var contacts = _db.ListContacts().Where(c => c.Status == ContactStatus.Active).ToDictionary(c => c.PersonId, StringComparer.Ordinal);
        var subscriptions = new Dictionary<string, SubscriptionRow?>(StringComparer.Ordinal);
        var offered = 0;
        foreach (var item in _db.ListNeverPushed(_clock.GetUtcNow() - OfferWindow))
        {
            if (item.Kind == InboxKind.Source)
            {
                if (item.ProjectId != project.Id || item.SubscriptionId is not { } subscriptionId)
                {
                    continue;
                }

                if (!subscriptions.TryGetValue(subscriptionId, out var subscription))
                {
                    subscriptions[subscriptionId] = subscription = _db.GetSubscription(subscriptionId);
                }

                if (subscription is { Mode: SourceMode.Session, Status: SubscriptionStatus.Active or SubscriptionStatus.Error })
                {
                    PushSourceItem(subscription, item);
                    offered++;
                }
            }
            else if (contacts.TryGetValue(item.FromPerson, out var contact) && contact.InboundMode == InboundMode.AutoSession && contact.AutoSession == session)
            {
                PushToSession(contact, item);
                offered++;
            }
        }

        if (offered > 0)
        {
            _logger.LogInformation("Offering {Count} item(s) that waited for a session to the session in {Project}", offered, project.Name);
            WriteStatus();
            InboxChanged?.Invoke();
        }
    }

    /// <summary>Sends the next held push of every session that is idle now. The loop calls it; tests call it by hand.</summary>
    internal void FlushHeldPushes()
    {
        var sent = new List<HeldPush>();
        var dropped = new List<HeldPush>();
        var notShown = new List<(string Session, HeldPush Push)>();
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

                if (_notShowing.ContainsKey(session))
                {
                    notShown.AddRange(list.Select(h => (session, h)));
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
                        Sent(session, next.Event, now);
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
            _db.UncountAttempt(push.MessageId);
            _db.SetMessageNote(push.MessageId, "Not sent into a session: it closed while it was busy. It waits here.", now);
        }

        foreach (var (session, push) in notShown)
        {
            _db.UncountAttempt(push.MessageId);
            _db.SetMessageNote(push.MessageId, NotShownNote(session, push.Event), now);
        }

        if (sent.Count > 0 || dropped.Count > 0 || notShown.Count > 0)
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
                bool held, unseen;
                lock (_pushLock)
                {
                    held = _held.Count > 0;
                    unseen = _unseen.Count > 0;
                }

                if (unseen)
                {
                    CheckPushesShown();
                }

                if (held)
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
        bool wasNotShowing;
        lock (_pushLock)
        {
            _notBefore.Remove(session);
            _unseen.Remove(session);
            wasNotShowing = _notShowing.Remove(session);
            if (!_held.Remove(session, out list))
            {
                list = null;
            }

            _lastPush.Remove(session);
        }

        if (list is null)
        {
            if (wasNotShowing)
            {
                WriteStatus();
            }

            return;
        }

        var now = _clock.GetUtcNow();
        foreach (var push in list)
        {
            // It never went in: the attempt it counted goes back, so the next session that opens is offered it.
            _db.UncountAttempt(push.MessageId);
            _db.SetMessageNote(push.MessageId, "Not sent into a session: it closed while it was busy. It waits here.", now);
        }

        WriteStatus();
        InboxChanged?.Invoke();
    }
}
