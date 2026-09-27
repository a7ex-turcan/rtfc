using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Protocol;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>
/// The outbox (spec §7.2): the one place for anything that must reach a peer later. Replies
/// to people who have gone, receipts, and messages the user explicitly left for someone.
/// One pump delivers it, on an interval and whenever a contact connects to us; entries
/// expire after a week and the human gets a notice. New messages are never queued unless
/// the user asks.
/// </summary>
public sealed partial class Node
{
    private readonly Channel<bool> _pumpKick = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Task? _pump;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>Raised after every pump run, whether or not anything moved. Tests wait on it.</summary>
    public event Action? OutboxFlushed;

    /// <summary>Asks the pump to run now. Cheap and idempotent; called when a peer shows up.</summary>
    public void KickOutbox() => _pumpKick.Writer.TryWrite(true);

    private void StartOutbox() => _pump = Task.Run(() => PumpLoopAsync(_stopping.Token));

    private async Task StopOutboxAsync()
    {
        _pumpKick.Writer.TryComplete();
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task PumpLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await FlushOutboxAsync(cancellationToken).ConfigureAwait(false);
                PruneIfDue();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The outbox pump failed; it will run again");
            }

            OutboxFlushed?.Invoke();

            // Sleep until the next interval, a little jittered so two daemons never fall into step, or until kicked.
            var wait = _options.Outbox.PumpInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)(_options.Outbox.PumpInterval.TotalMilliseconds / 4) + 1));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            try
            {
                if (!await _pumpKick.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    return; // completed: we are stopping
                }

                _pumpKick.Reader.TryRead(out _);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // the interval elapsed
            }
        }
    }

    /// <summary>One pass: expire what is too old, then try to deliver the rest, one session per device.</summary>
    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var pending = _db.ListOutbox(OutboxState.Pending);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var entry in pending.Where(e => e.ExpiresAt <= now))
        {
            Expire(entry, $"nobody was home for {Describe(_options.Outbox.Expiry)}");
        }

        foreach (var group in pending.Where(e => e.ExpiresAt > now).GroupBy(e => e.ToPerson))
        {
            var contact = _db.GetContact(group.Key);
            if (contact is null || contact.Status != ContactStatus.Active)
            {
                foreach (var entry in group)
                {
                    Expire(entry, "they are no longer a contact");
                }

                continue;
            }

            await DeliverGroupAsync(contact, [.. group], cancellationToken).ConfigureAwait(false);
        }

        WriteStatus();
    }

    private async Task DeliverGroupAsync(ContactRow contact, IReadOnlyList<OutboxRow> entries, CancellationToken cancellationToken)
    {
        using var ca = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(contact.PersonCaCert);
        var devices = _db.ListDevices(contact.PersonId).Where(d => d.Status == DeviceStatus.Active).ToList();
        var sessions = new Dictionary<string, PeerSession?>();
        try
        {
            foreach (var entry in entries)
            {
                var targets = entry.ToDevice is null ? devices : devices.Where(d => d.DeviceId == entry.ToDevice).ToList();
                var delivered = false;
                foreach (var device in targets)
                {
                    if (!sessions.TryGetValue(device.DeviceId, out var session))
                    {
                        session = await OpenSessionAsync(contact, ca, device, cancellationToken).ConfigureAwait(false);
                        sessions[device.DeviceId] = session;
                    }

                    if (session is null)
                    {
                        continue;
                    }

                    var outcome = await SendEntryAsync(session, entry, device, cancellationToken).ConfigureAwait(false);
                    if (outcome is null)
                    {
                        MarkDelivered(entry, contact, device);
                        delivered = true;
                        break;
                    }

                    _logger.LogDebug("Outbox {Id} to {Handle}/{Device}: {Outcome}", entry.Id, contact.Handle, device.Name, outcome);
                    if (outcome is not "rejected")
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                        sessions[device.DeviceId] = null;
                    }
                }

                if (!delivered)
                {
                    _db.SetOutboxState(entry.Id, OutboxState.Pending, countAttempt: true);
                }
            }
        }
        finally
        {
            foreach (var session in sessions.Values.OfType<PeerSession>())
            {
                try
                {
                    await session.SendAsync(new ByeFrame(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                }

                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Null on success, "rejected" when the peer refused this entry, otherwise a reason the session is no good.</summary>
    private async Task<string?> SendEntryAsync(PeerSession session, OutboxRow entry, DeviceRow device, CancellationToken cancellationToken)
    {
        Frame frame;
        try
        {
            frame = Frames.Parse(Encoding.UTF8.GetBytes(entry.Envelope));
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException)
        {
            Expire(entry, "the stored envelope could not be read");
            return "rejected";
        }

        // The device and the sequence number belong to the stream, so they are decided now, not when the entry was queued.
        Frame filled = frame switch
        {
            MessageFrame m => m with { To = new Address(entry.ToPerson, device.DeviceId), Seq = _db.NextSeqOut(device.DeviceId) },
            ReceiptFrame r => r with { To = new Address(entry.ToPerson, device.DeviceId) },
            _ => frame,
        };

        try
        {
            await session.SendAsync(filled, cancellationToken).ConfigureAwait(false);
            var reply = await session.ReceiveAsync(cancellationToken).AsTask().WaitAsync(AckTimeout, cancellationToken).ConfigureAwait(false);
            return reply switch
            {
                AckFrame { Status: AckStatus.Ok or AckStatus.Duplicate } => null,
                AckFrame ack => ack.Reason == "unknown_message" ? null : "rejected", // a receipt for something they no longer have is as good as delivered
                null => "closed",
                _ => $"unexpected_{reply.Type}",
            };
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or TimeoutException)
        {
            return ex is TimeoutException ? "no_ack" : "closed";
        }
    }

    private void MarkDelivered(OutboxRow entry, ContactRow contact, DeviceRow device)
    {
        var now = _clock.GetUtcNow();
        _db.SetOutboxState(entry.Id, OutboxState.Delivered, countAttempt: true);
        if (entry.Kind is OutboxKind.Reply or OutboxKind.Message)
        {
            _db.SetSentState(entry.Id, SentState.Delivered, deliveredAt: now, readAt: null);
            if (_db.GetSent(entry.Id)?.ReplyTo is { } original)
            {
                _db.SetMessageNote(original, $"Your reply was delivered to {contact.Handle}/{device.Name} at {Timestamps.Format(now)}.", now);
                InboxChanged?.Invoke();
            }
        }

        _logger.LogInformation("Outbox {Kind} {Id} delivered to {Handle}/{Device} after {Attempts} attempt(s)", entry.Kind, entry.Id, contact.Handle, device.Name, entry.Attempts + 1);
    }

    /// <summary>Gives up on an entry. Replies and messages leave a notice for the human (spec §7.2); receipts go quietly.</summary>
    private void Expire(OutboxRow entry, string reason)
    {
        var now = _clock.GetUtcNow();
        _db.SetOutboxState(entry.Id, OutboxState.Expired, countAttempt: false);
        if (entry.Kind == OutboxKind.Receipt)
        {
            return;
        }

        _db.SetSentState(entry.Id, SentState.Expired, null, null);
        var sent = _db.GetSent(entry.Id);
        var handle = _db.GetContact(entry.ToPerson)?.Handle ?? Ids.Fingerprint(entry.ToPerson);
        var what = entry.Kind == OutboxKind.Reply ? "reply" : "message";
        var text = $"Your {what} to {handle} was not delivered: {reason}. It said:\n{sent?.Body ?? "(unknown)"}";
        Notice(text, now);
        if (sent?.ReplyTo is { } original)
        {
            _db.SetMessageNote(original, $"Your reply to this was not delivered: {reason}.", now);
        }

        _logger.LogInformation("Outbox {Kind} {Id} to {Handle} expired: {Reason}", entry.Kind, entry.Id, handle, reason);
    }

    /// <summary>A parked note from rtfc itself, shown like a message and dismissed like one.</summary>
    private void Notice(string text, DateTimeOffset now)
    {
        _db.InsertMessage(new InboxMessage(
            Ulid.NewUlid(now), Self.DeviceId, Self.PersonId, Self.DeviceId, Seq: 0, ReplyTo: null, MessageOrigin.Human, Hop: 0, Thread: null,
            text, SentAt: now, ReceivedAt: now, UpdatedAt: now, InboxState.Parked, HandledBy: null, HandledAt: null, Kind: InboxKind.Notice));
        WriteStatus();
        InboxChanged?.Invoke();
    }

    /// <summary>Puts a message or reply in the outbox and records it as sent-but-queued.</summary>
    private SendResult Queue(ContactRow contact, string? deviceId, string id, string thread, string? replyTo, int hop, string text, string origin)
    {
        var now = _clock.GetUtcNow();
        var expires = now + _options.Outbox.Expiry;
        var kind = replyTo is null ? OutboxKind.Message : OutboxKind.Reply;
        var envelope = new MessageFrame(
            HelloFrame.CurrentVersion, id, new Address(Self.PersonId, Self.DeviceId), new Address(contact.PersonId, deviceId ?? ""),
            Seq: 0, thread, replyTo, origin, hop, Timestamps.Format(now), new MessageBody(text));

        _db.InsertSent(new SentRow(id, contact.PersonId, deviceId, thread, replyTo, origin, kind, text, now, DeliveredAt: null, ReadAt: null, expires, SentState.Queued));
        _db.InsertOutbox(new OutboxRow(id, contact.PersonId, deviceId, kind, Encoding.UTF8.GetString(Frames.Serialize(envelope)), now, expires, Attempts: 0, OutboxState.Pending));
        _logger.LogInformation("Queued {Kind} {Id} for {Handle}, expires {Expires}", kind, id, contact.Handle, Timestamps.Format(expires));
        KickOutbox();
        WriteStatus();
        return new SendResult(SendStatus.Queued, id, Person: contact.Handle, ExpiresAt: expires);
    }

    /// <summary>Queues a read receipt for the device that sent a message. Delivered by the pump, now if they are home, later if not.</summary>
    private void QueueReceipt(ContactRow contact, InboxMessage message)
    {
        var now = _clock.GetUtcNow();
        var receipt = new ReceiptFrame(
            Ulid.NewUlid(now), message.Id, Timestamps.Format(now), new Address(Self.PersonId, Self.DeviceId), new Address(contact.PersonId, message.FromDevice));
        _db.InsertOutbox(new OutboxRow(
            receipt.Id, contact.PersonId, message.FromDevice, OutboxKind.Receipt, Encoding.UTF8.GetString(Frames.Serialize(receipt)),
            now, now + _options.Outbox.Expiry, Attempts: 0, OutboxState.Pending));
        KickOutbox();
    }

    /// <summary>A receipt from a contact: their device read something we sent. The ack to return.</summary>
    private AckFrame ReceiveReceipt(SessionTrust.Authenticated trust, ReceiptFrame receipt)
    {
        if (receipt.From.Person != trust.PersonId || receipt.From.Device != trust.DeviceId)
        {
            return new AckFrame(receipt.Id, AckStatus.Rejected, "from_mismatch");
        }

        var sent = _db.GetSent(receipt.MessageId);
        if (sent is null || sent.ToPerson != trust.PersonId)
        {
            return new AckFrame(receipt.Id, AckStatus.Rejected, "unknown_message");
        }

        var now = _clock.GetUtcNow();
        if (sent.State != SentState.Read)
        {
            _db.SetSentState(sent.Id, SentState.Read, deliveredAt: sent.DeliveredAt ?? now, readAt: now);
            if (sent.ReplyTo is { } original)
            {
                var handle = _db.GetContact(trust.PersonId)?.Handle ?? Ids.Fingerprint(trust.PersonId);
                _db.SetMessageNote(original, $"{handle} read your reply at {Timestamps.Format(now)}.", now);
                InboxChanged?.Invoke();
            }
        }

        return new AckFrame(receipt.Id, AckStatus.Ok);
    }

    /// <summary>What is still waiting to go out, for the CLI and the contacts view.</summary>
    public OutboxView[] ListOutbox()
    {
        var handles = _db.ListContacts().ToDictionary(c => c.PersonId, c => c.Handle);
        return [.. _db.ListOutbox(OutboxState.Pending).Select(o =>
        {
            var to = handles.GetValueOrDefault(o.ToPerson, Ids.Fingerprint(o.ToPerson));
            if (o.ToDevice is not null)
            {
                to += "/" + DeviceName(o.ToDevice);
            }

            var preview = o.Kind == OutboxKind.Receipt ? "(read receipt)" : Preview(_db.GetSent(o.Id)?.Body ?? "");
            return new OutboxView(o.Id, o.Kind, to, o.State, o.CreatedAt, o.ExpiresAt, o.Attempts, preview);
        })];
    }

    private void PruneIfDue()
    {
        var now = _clock.GetUtcNow();
        if (now - _lastPrune < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastPrune = now;
        var removed = _db.Prune(now - _options.Outbox.Retention, now - TimeSpan.FromDays(1));
        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Count} handled rows older than {Retention}", removed, Describe(_options.Outbox.Retention));
        }
    }

    private static string Describe(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{span.TotalDays:0.#} day(s)",
        { TotalHours: >= 1 } => $"{span.TotalHours:0.#} hour(s)",
        { TotalMinutes: >= 1 } => $"{span.TotalMinutes:0.#} minute(s)",
        _ => $"{span.TotalSeconds:0.#} second(s)",
    };
}
