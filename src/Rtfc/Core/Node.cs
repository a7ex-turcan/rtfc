using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Protocol;
using Rtfc.Storage;

namespace Rtfc.Core;

public sealed record NodeOptions(IReadOnlyList<string> HintHosts);

/// <summary>
/// Everything stateful on one device, minus the IPC surface (spec §3.1): the contact
/// lifecycle, sending with nobody's-home, and the parked inbox. The daemon hosts one of
/// these; the tests host two in one process.
/// </summary>
public sealed class Node : IAsyncDisposable
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly RtfcHome _home;
    private readonly Database _db;
    private readonly ITransport _transport;
    private readonly NodeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();

    public Node(RtfcHome home, SelfIdentity self, Database db, ITransport transport, NodeOptions options, TimeProvider clock, ILogger logger)
    {
        _home = home;
        Self = self;
        _db = db;
        _transport = transport;
        _options = options;
        _clock = clock;
        _logger = logger;

        _db.SaveSelf(new SelfRow(self.PersonId, self.Handle, self.PersonCa.RawData, self.DeviceId, self.DeviceName, self.DeviceCertificate.RawData, DeviceListVersion));
    }

    public SelfIdentity Self { get; }

    public long DeviceListVersion => 1;

    /// <summary>Raised after the inbox changed and <c>status.json</c> was rewritten.</summary>
    public event Action? InboxChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _transport.StartAsync(HandleInboundAsync, cancellationToken).ConfigureAwait(false);
        WriteStatus();
        _logger.LogInformation("rtfcd listening as {Handle}/{Device} ({Person}) on port {Port}", Self.Handle, Self.DeviceName, Ids.Fingerprint(Self.PersonId), (_transport as TcpTransport)?.Port);
    }

    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _transport.StopAsync().ConfigureAwait(false);
    }

    /// <summary>The <c>tcp:</c> hints this device advertises in invite tokens and accept frames (spec §8.4).</summary>
    public string[] AdvertisedHints()
    {
        var port = (_transport as TcpTransport)?.Port ?? TcpTransport.DefaultPort;
        return [.. _options.HintHosts.Select(host => EndpointHint.ForTcp(host, port).ToString())];
    }

    // ---- invite and accept (spec §5.1) ----

    public InviteResult CreateInvite()
    {
        var now = _clock.GetUtcNow();
        var nonce = InviteToken.NewNonce();
        var expires = now + InviteToken.Lifetime;
        _db.CreateInvite(nonce, now, expires);

        var hints = AdvertisedHints();
        var token = InviteToken.Encode(new InviteTokenPayload(1, Self.PersonId, Self.Handle, hints, nonce, expires));
        return new InviteResult(token, expires, hints);
    }

    public async Task<AcceptResult> AcceptAsync(string token, CancellationToken cancellationToken)
    {
        if (!InviteToken.TryDecode(token, out var payload))
        {
            return new AcceptResult(AcceptStatus.Invalid, Reason: "The token is not an rtfc invite.");
        }

        if (payload.Person == Self.PersonId)
        {
            return new AcceptResult(AcceptStatus.Invalid, Reason: "That is your own invite.");
        }

        var existing = _db.GetContact(payload.Person);
        if (existing?.Status == ContactStatus.Blocked)
        {
            return new AcceptResult(AcceptStatus.Blocked, existing.Handle, existing.PersonId, Ids.Fingerprint(existing.PersonId));
        }

        if (existing?.Status == ContactStatus.Active)
        {
            return new AcceptResult(AcceptStatus.AlreadyContact, existing.Handle, existing.PersonId, Ids.Fingerprint(existing.PersonId));
        }

        var hints = ParseHints(payload.Hints);
        var stream = await _transport.ConnectAsync("", hints, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return new AcceptResult(AcceptStatus.NobodyHome, Reason: "The inviter has no device online. The token stays valid.");
        }

        try
        {
            await using var session = await PeerSession.ConnectAsync(stream, Self, Anchors(), DeviceListVersion, cancellationToken).ConfigureAwait(false);
            await session.SendAsync(
                new InviteAcceptFrame(payload.Nonce, Base64(Self.PersonCa), Self.Handle, Self.DeviceName, AdvertisedHints()),
                cancellationToken).ConfigureAwait(false);

            var reply = await session.ReceiveAsync(cancellationToken).AsTask().WaitAsync(AckTimeout, cancellationToken).ConfigureAwait(false);
            switch (reply)
            {
                case AcceptAckFrame ack:
                    using (var ca = ParseCertificate(ack.PersonCa))
                    {
                        if (ca is null || Ids.Person(ca) != payload.Person)
                        {
                            return new AcceptResult(AcceptStatus.Failed, Reason: "The inviter's certificate does not match the token.");
                        }

                        if (!Certificates.IsIssuedBy(session.RemoteCertificate, ca))
                        {
                            return new AcceptResult(AcceptStatus.Failed, Reason: "The device that answered was not issued by the inviter's CA.");
                        }

                        var contact = Pin(ca, ack.Handle, session.RemoteCertificate, ack.DeviceName, ack.Hints.Length > 0 ? ack.Hints : payload.Hints);
                        await session.SendAsync(new ByeFrame(), cancellationToken).ConfigureAwait(false);
                        return new AcceptResult(AcceptStatus.Accepted, contact.Handle, contact.PersonId, Ids.Fingerprint(contact.PersonId));
                    }

                case ErrorFrame error:
                    return new AcceptResult(
                        error.Code == "blocked" ? AcceptStatus.Blocked : AcceptStatus.Rejected, Reason: error.Message);

                case null:
                    return new AcceptResult(AcceptStatus.Failed, Reason: "The inviter closed the connection.");

                default:
                    return new AcceptResult(AcceptStatus.Failed, Reason: $"Unexpected '{reply.Type}' frame from the inviter.");
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ProtocolException or TimeoutException)
        {
            _logger.LogWarning(ex, "Accepting an invite from {Person} failed", Ids.Fingerprint(payload.Person));
            return new AcceptResult(AcceptStatus.Failed, Reason: ex.Message);
        }
    }

    private async Task HandleInviteAcceptAsync(PeerSession session, InviteAcceptFrame frame, CancellationToken cancellationToken)
    {
        using var ca = ParseCertificate(frame.PersonCa);
        if (ca is null || !Certificates.IsIssuedBy(session.RemoteCertificate, ca))
        {
            await session.SendAsync(new ErrorFrame("bad_certificate", "The device certificate was not issued by the CA you sent."), cancellationToken).ConfigureAwait(false);
            return;
        }

        var personId = Ids.Person(ca);
        if (_db.GetContact(personId)?.Status == ContactStatus.Blocked)
        {
            await session.SendAsync(new ErrorFrame("blocked", "This invite cannot be accepted."), cancellationToken).ConfigureAwait(false);
            return;
        }

        var use = _db.TryUseInvite(frame.Nonce, personId, _clock.GetUtcNow());
        if (use != InviteUse.Used)
        {
            var (code, message) = use switch
            {
                InviteUse.AlreadyUsed => ("invite_used", "This invite was already used."),
                InviteUse.Expired => ("invite_expired", "This invite has expired."),
                _ => ("invite_unknown", "This invite is not one I issued."),
            };
            await session.SendAsync(new ErrorFrame(code, message), cancellationToken).ConfigureAwait(false);
            return;
        }

        var contact = Pin(ca, frame.Handle, session.RemoteCertificate, frame.DeviceName, frame.Hints);
        _logger.LogInformation("Accepted contact {Handle} ({Person}) from device {Device}", contact.Handle, Ids.Fingerprint(personId), frame.DeviceName);
        await session.SendAsync(new AcceptAckFrame(Base64(Self.PersonCa), Self.Handle, Self.DeviceName, AdvertisedHints()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a person as an active contact with their device, in park mode. Accepting never implies auto-answer (spec §5.1).</summary>
    private ContactRow Pin(X509Certificate2 personCa, string suggestedHandle, X509Certificate2 deviceCertificate, string deviceName, string[] hints)
    {
        var personId = Ids.Person(personCa);
        var existing = _db.GetContact(personId);
        var handle = existing?.Handle ?? UniqueHandle(InviteToken.SanitizeHandle(suggestedHandle, personId), personId);

        var contact = new ContactRow(
            personId, handle, personCa.RawData, ContactStatus.Active, _clock.GetUtcNow(), InboundMode.Park,
            AutoScope: null, AutoOwnerDevice: null, ReadReceipts: true, DeviceListVersion: 0, Rev: (existing?.Rev ?? 0) + 1);
        _db.UpsertContact(contact);
        _db.UpsertDevice(new DeviceRow(
            Ids.Device(deviceCertificate), personId, InviteToken.SanitizeHandle(deviceName, Ids.Device(deviceCertificate)),
            deviceCertificate.RawData, DeviceStatus.Active, hints));
        return contact;
    }

    private string UniqueHandle(string handle, string personId)
    {
        var taken = _db.FindContactByHandle(handle);
        if (taken is null || taken.PersonId == personId)
        {
            return handle;
        }

        return $"{handle}-{personId[Ids.PersonPrefix.Length..][..4]}";
    }

    // ---- sending (spec §7.2) ----

    public Task<SendResult> SendAsync(string to, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(SendResult.Rejected("empty_message"));
        }

        if (Encoding.UTF8.GetByteCount(text) > MessageFrame.MaxBodyBytes)
        {
            return Task.FromResult(SendResult.Rejected("body_too_large"));
        }

        var slash = to.IndexOf('/');
        var handle = slash < 0 ? to : to[..slash];
        var deviceName = slash < 0 ? null : to[(slash + 1)..];

        var contact = _db.FindContactByHandle(handle.Trim());
        if (contact is null || contact.Status != ContactStatus.Active)
        {
            return Task.FromResult(SendResult.Rejected("not_a_contact"));
        }

        var id = Ulid.NewUlid(_clock.GetUtcNow());
        return DeliverAsync(contact, deviceName, id, thread: id, replyTo: null, hop: 0, text, cancellationToken);
    }

    private async Task<SendResult> DeliverAsync(
        ContactRow contact, string? deviceName, string id, string thread, string? replyTo, int hop, string text, CancellationToken cancellationToken)
    {
        var devices = _db.ListDevices(contact.PersonId).Where(d => d.Status == DeviceStatus.Active).ToList();
        var targets = deviceName is null ? devices : devices.Where(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            return SendResult.Rejected(deviceName is null ? "no_devices" : "unknown_device");
        }

        using var ca = X509CertificateLoader.LoadCertificate(contact.PersonCaCert);
        var delivered = new List<string>();
        var unreachable = new List<string>();
        string? failure = null;

        foreach (var device in targets)
        {
            var label = $"{contact.Handle}/{device.Name}";
            var outcome = await DeliverToDeviceAsync(contact, ca, device, id, thread, replyTo, hop, text, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case null:
                    delivered.Add(label);
                    break;
                case "unreachable":
                    unreachable.Add(label);
                    break;
                default:
                    unreachable.Add(label);
                    failure = outcome;
                    break;
            }
        }

        if (delivered.Count == targets.Count)
        {
            return new SendResult(SendStatus.Delivered, id, To: [.. delivered]);
        }

        if (delivered.Count > 0)
        {
            return new SendResult(SendStatus.Partial, id, To: [.. delivered], Unreachable: [.. unreachable]);
        }

        if (failure is not null)
        {
            return new SendResult(SendStatus.Failed, id, Reason: failure);
        }

        if (deviceName is not null)
        {
            var others = devices.Where(d => !targets.Contains(d)).ToList();
            var online = await ProbeAsync(others, cancellationToken).ConfigureAwait(false);
            return new SendResult(SendStatus.DeviceOffline, Requested: $"{contact.Handle}/{deviceName}",
                Online: [.. online.Where(kv => kv.Value).Select(kv => $"{contact.Handle}/{kv.Key.Name}")]);
        }

        return new SendResult(SendStatus.NobodyHome, Person: contact.Handle);
    }

    /// <summary>Null on success, "unreachable" when no hint answered, otherwise a reason.</summary>
    private async Task<string?> DeliverToDeviceAsync(
        ContactRow contact, X509Certificate2 ca, DeviceRow device, string id, string thread, string? replyTo, int hop, string text, CancellationToken cancellationToken)
    {
        var stream = await _transport.ConnectAsync(device.DeviceId, ParseHints(device.Endpoints), cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return "unreachable";
        }

        try
        {
            await using var session = await PeerSession.ConnectAsync(stream, Self, [ca], DeviceListVersion, cancellationToken).ConfigureAwait(false);
            if (session.Trust is not SessionTrust.Authenticated { } trust || trust.PersonId != contact.PersonId || trust.DeviceId != device.DeviceId)
            {
                return "wrong_device_answered";
            }

            var seq = _db.NextSeqOut(device.DeviceId);
            var envelope = new MessageFrame(
                HelloFrame.CurrentVersion, id,
                new Address(Self.PersonId, Self.DeviceId), new Address(contact.PersonId, device.DeviceId),
                seq, thread, replyTo, MessageOrigin.Human, hop, Timestamps.Format(_clock.GetUtcNow()), new MessageBody(text));
            await session.SendAsync(envelope, cancellationToken).ConfigureAwait(false);

            var reply = await session.ReceiveAsync(cancellationToken).AsTask().WaitAsync(AckTimeout, cancellationToken).ConfigureAwait(false);
            await session.SendAsync(new ByeFrame(), cancellationToken).ConfigureAwait(false);
            return reply switch
            {
                AckFrame { Status: AckStatus.Ok or AckStatus.Duplicate } => null,
                AckFrame ack => $"rejected:{ack.Reason ?? ack.Status}",
                ErrorFrame error => $"{error.Code}: {error.Message}",
                null => "closed_before_ack",
                _ => $"unexpected_{reply.Type}",
            };
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ProtocolException or TimeoutException)
        {
            _logger.LogWarning(ex, "Delivery to {Handle}/{Device} failed", contact.Handle, device.Name);
            return ex is TimeoutException ? "no_ack" : "unreachable";
        }
    }

    // ---- inbound (spec §8.2) ----

    private async Task HandleInboundAsync(Stream stream)
    {
        var cancellationToken = _stopping.Token;
        PeerSession session;
        try
        {
            session = await PeerSession.AcceptAsync(stream, Self, Anchors(), DeviceListVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ProtocolException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "An inbound connection did not complete the handshake");
            return;
        }

        await using (session)
        {
            try
            {
                while (await session.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
                {
                    if (frame is ByeFrame)
                    {
                        return;
                    }

                    if (frame is InviteAcceptFrame invite)
                    {
                        await HandleInviteAcceptAsync(session, invite, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (session.Trust is not SessionTrust.Authenticated trust)
                    {
                        // A restricted session may only accept an invite (spec §5.1). Anything else closes it.
                        await session.SendAsync(new ErrorFrame("not_a_contact", "We are not contacts."), cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    switch (frame)
                    {
                        case MessageFrame message:
                            await session.SendAsync(Receive(trust, message), cancellationToken).ConfigureAwait(false);
                            break;
                        case UnknownFrame unknown:
                            _logger.LogDebug("Ignoring a '{Type}' frame from {Person}", unknown.Type, Ids.Fingerprint(trust.PersonId));
                            break;
                        default:
                            _logger.LogDebug("Ignoring a '{Type}' frame from {Person}", frame.Type, Ids.Fingerprint(trust.PersonId));
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "A session from {Device} ended", session.RemoteDeviceId);
            }
        }
    }

    /// <summary>Stores a message and returns the ack to send. The ack is built only after the commit (spec §7.2).</summary>
    private AckFrame Receive(SessionTrust.Authenticated trust, MessageFrame message)
    {
        if (!Ulid.IsValid(message.Id))
        {
            return new AckFrame(message.Id, AckStatus.Rejected, "bad_id");
        }

        if (message.From.Person != trust.PersonId || message.From.Device != trust.DeviceId)
        {
            return new AckFrame(message.Id, AckStatus.Rejected, "from_mismatch");
        }

        if (message.To.Person != Self.PersonId || message.To.Device != Self.DeviceId)
        {
            return new AckFrame(message.Id, AckStatus.Rejected, "wrong_recipient");
        }

        if (message.Origin is not (MessageOrigin.Human or MessageOrigin.Auto) || message.Hop < 0)
        {
            return new AckFrame(message.Id, AckStatus.Rejected, "bad_envelope");
        }

        if (string.IsNullOrEmpty(message.Body?.Text) || Encoding.UTF8.GetByteCount(message.Body.Text) > MessageFrame.MaxBodyBytes)
        {
            return new AckFrame(message.Id, AckStatus.Rejected, "bad_body");
        }

        if (_db.GetDevice(trust.DeviceId) is not { Status: DeviceStatus.Active })
        {
            // Phase 5 learns new devices from signed device lists. Until then, only the device that accepted is known.
            return new AckFrame(message.Id, AckStatus.Rejected, "unknown_device");
        }

        var now = _clock.GetUtcNow();
        var stored = _db.InsertMessage(new InboxMessage(
            message.Id, Self.DeviceId, message.From.Person, message.From.Device, message.Seq, message.ReplyTo, message.Origin,
            message.Hop, message.Thread ?? message.Id, message.Body.Text, TryParseTime(message.SentAt), now, now,
            InboxState.Parked, HandledBy: null, HandledAt: null));
        if (!stored)
        {
            return new AckFrame(message.Id, AckStatus.Duplicate);
        }

        _db.RecordSeqIn(message.From.Device, message.Seq);
        WriteStatus();
        InboxChanged?.Invoke();
        return new AckFrame(message.Id, AckStatus.Ok);
    }

    // ---- inbox (spec §9.2) ----

    public InboxSummary[] ListInbox(string? state)
    {
        var handles = _db.ListContacts().ToDictionary(c => c.PersonId, c => c.Handle);
        return [.. _db.ListMessages(state).Select(m => new InboxSummary(
            m.Id, handles.GetValueOrDefault(m.FromPerson, Ids.Fingerprint(m.FromPerson)), DeviceName(m.FromDevice), m.State,
            Preview(m.Body), m.ReceivedAt, m.ReplyTo, m.Origin))];
    }

    /// <summary>Marks a parked message read. Receipts arrive with Phase 4.</summary>
    public InboxOpened? Open(string id)
    {
        var message = _db.GetMessage(id);
        if (message is null)
        {
            return null;
        }

        if (message.State == InboxState.Parked)
        {
            _db.SetMessageState(id, InboxState.Read, handledBy: null, _clock.GetUtcNow());
            message = message with { State = InboxState.Read };
            WriteStatus();
            InboxChanged?.Invoke();
        }

        var from = _db.GetContact(message.FromPerson)?.Handle ?? Ids.Fingerprint(message.FromPerson);
        return new InboxOpened(
            message.Id, from, DeviceName(message.FromDevice), message.FromPerson, message.State, message.ReceivedAt, message.SentAt,
            message.Thread, message.ReplyTo, message.Origin, message.Hop, message.Body);
    }

    /// <summary>Replies to the person who sent a message. Until the outbox lands (Phase 4), the sender must be home.</summary>
    public async Task<SendResult> ReplyAsync(string id, string text, CancellationToken cancellationToken)
    {
        var original = _db.GetMessage(id);
        if (original is null)
        {
            return SendResult.Rejected("unknown_message");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return SendResult.Rejected("empty_message");
        }

        if (Encoding.UTF8.GetByteCount(text) > MessageFrame.MaxBodyBytes)
        {
            return SendResult.Rejected("body_too_large");
        }

        var contact = _db.GetContact(original.FromPerson);
        if (contact is null || contact.Status != ContactStatus.Active)
        {
            return SendResult.Rejected("not_a_contact");
        }

        var replyId = Ulid.NewUlid(_clock.GetUtcNow());
        var result = await DeliverAsync(contact, deviceName: null, replyId, original.Thread ?? original.Id, replyTo: id, original.Hop + 1, text, cancellationToken).ConfigureAwait(false);
        if (result.Status is SendStatus.Delivered or SendStatus.Partial)
        {
            _db.SetMessageState(id, InboxState.Answered, Self.DeviceId, _clock.GetUtcNow());
            WriteStatus();
            InboxChanged?.Invoke();
        }

        return result;
    }

    // ---- contacts (spec §9.2) ----

    public async Task<ContactView[]> ContactsAsync(bool probe, CancellationToken cancellationToken)
    {
        var views = new List<ContactView>();
        foreach (var contact in _db.ListContacts())
        {
            var devices = _db.ListDevices(contact.PersonId);
            var online = probe && contact.Status == ContactStatus.Active
                ? await ProbeAsync(devices, cancellationToken).ConfigureAwait(false)
                : [];
            views.Add(new ContactView(
                contact.Handle, contact.PersonId, Ids.Fingerprint(contact.PersonId), contact.Status, contact.InboundMode, contact.AcceptedAt,
                [.. devices.Select(d => new DeviceView(d.Name, d.DeviceId, d.Status, online.TryGetValue(d, out var o) ? o : null, [.. d.Endpoints]))]));
        }

        return [.. views];
    }

    private async Task<Dictionary<DeviceRow, bool>> ProbeAsync(IReadOnlyList<DeviceRow> devices, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var probes = devices.Select(async d =>
        {
            try
            {
                return (d, await _transport.IsReachableAsync(d.DeviceId, ParseHints(d.Endpoints), timeout.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                return (d, false);
            }
        });
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        return results.ToDictionary(r => r.Item1, r => r.Item2);
    }

    // ---- status (spec §11) ----

    public StatusSnapshot Status()
    {
        var parked = _db.ListMessages(InboxState.Parked);
        var handles = _db.ListContacts().ToDictionary(c => c.PersonId, c => c.Handle);
        var from = parked.Select(m => handles.GetValueOrDefault(m.FromPerson, Ids.Fingerprint(m.FromPerson))).Distinct().ToArray();
        return new StatusSnapshot(new StatusGlobal(parked.Count, from), [], Away: false);
    }

    private void WriteStatus()
    {
        try
        {
            StatusFile.Write(_home.StatusPath, Status());
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not write {Path}", _home.StatusPath);
        }
    }

    // ---- helpers ----

    /// <summary>Our own CA plus every active contact's: the complete trust store for a handshake (spec §8.2).</summary>
    private List<X509Certificate2> Anchors() =>
    [
        Self.PersonCa,
        .. _db.ListContacts().Where(c => c.Status == ContactStatus.Active).Select(c => X509CertificateLoader.LoadCertificate(c.PersonCaCert)),
    ];

    private string DeviceName(string deviceId) => _db.GetDevice(deviceId)?.Name ?? deviceId[Ids.DevicePrefix.Length..][..8];

    private static string Preview(string body)
    {
        var line = body.ReplaceLineEndings(" ").Trim();
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    private static List<EndpointHint> ParseHints(IEnumerable<string> hints)
    {
        var parsed = new List<EndpointHint>();
        foreach (var hint in hints)
        {
            try
            {
                parsed.Add(EndpointHint.Parse(hint));
            }
            catch (FormatException)
            {
                // A hint we cannot read is a hint we cannot use.
            }
        }

        return parsed;
    }

    private static string Base64(X509Certificate2 certificate) => Convert.ToBase64String(certificate.RawData);

    private static X509Certificate2? ParseCertificate(string base64)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryParseTime(string? value)
    {
        try
        {
            return Timestamps.ParseOrNull(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _db.Dispose();
        Self.Dispose();
    }
}
