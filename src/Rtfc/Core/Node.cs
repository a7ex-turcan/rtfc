using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Protocol;
using Rtfc.Storage;

namespace Rtfc.Core;

public sealed record NodeOptions(IReadOnlyList<string> HintHosts, AutoAnswerConfig AutoAnswer, OutboxSettings Outbox, SourceSettings? Sources = null);

/// <summary>
/// Everything stateful on one device, minus the IPC surface (spec §3.1): the contact
/// lifecycle, sending with nobody's-home, and the parked inbox. The daemon hosts one of
/// these; the tests host two in one process.
/// </summary>
public sealed partial class Node : IAsyncDisposable
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly RtfcHome _home;
    private readonly Database _db;
    private readonly ITransport _transport;
    private readonly NodeOptions _options;
    private readonly IClaudeRunner _claude;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ISessionChannel? _sessions;
    private readonly SourceSettings _sourceSettings;
    private readonly Dictionary<string, Sources.ISourceAdapter> _adapters;
    private IReadOnlyList<string> _hintHosts;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>One client for every source adapter: the daemon polls a handful of sites, never many.</summary>
    private static readonly HttpClient SourceHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Node(
        RtfcHome home, SelfIdentity self, Database db, ITransport transport, NodeOptions options, IClaudeRunner claude, TimeProvider clock, ILogger logger,
        ISessionChannel? sessions = null, IReadOnlyList<Sources.ISourceAdapter>? adapters = null)
    {
        _home = home;
        Self = self;
        _db = db;
        _transport = transport;
        _options = options;
        _claude = claude;
        _clock = clock;
        _logger = logger;
        _sessions = sessions;
        _sourceSettings = options.Sources ?? SourceSettings.Default;
        _adapters = (adapters ?? [new Sources.JiraCloudAdapter(SourceHttp), new Sources.ConfluenceCloudAdapter(SourceHttp), new Sources.BitbucketCloudAdapter(SourceHttp)])
            .ToDictionary(a => a.Type, StringComparer.Ordinal);
        _hintHosts = options.HintHosts;

        _db.SaveSelf(new SelfRow(self.PersonId, self.Handle, self.PersonCa.RawData, self.DeviceId, self.DeviceName, self.DeviceCertificate.RawData, DeviceListVersion));
    }

    public SelfIdentity Self { get; }

    public ITransport Transport => _transport;

    public long DeviceListVersion => 1;

    /// <summary>Raised after the inbox changed and <c>status.json</c> was rewritten.</summary>
    public event Action? InboxChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsAway)
        {
            await _transport.StartAsync(HandleInboundAsync, cancellationToken).ConfigureAwait(false);
        }

        StartAutoAnswering();
        StartOutbox();
        StartSources();
        WriteStatus();
        _logger.LogInformation("rtfcd listening as {Handle}/{Device} ({Person}) on port {Port}", Self.Handle, Self.DeviceName, Ids.Fingerprint(Self.PersonId), (_transport as TcpTransport)?.Port);
    }

    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _transport.StopAsync().ConfigureAwait(false);
        await StopAutoAnsweringAsync().ConfigureAwait(false);
        await StopOutboxAsync().ConfigureAwait(false);
        await StopSourcesAsync().ConfigureAwait(false);
    }

    /// <summary>Away (spec §9.4): nothing listens, so contacts see nobody home, while everything outbound still works.</summary>
    public bool IsAway => _db.GetMeta("away") == "1";

    public async Task<ManagementResult> SetAwayAsync(bool away, CancellationToken cancellationToken)
    {
        if (away != IsAway)
        {
            _db.SetMeta("away", away ? "1" : "0");
            if (away)
            {
                await _transport.StopAsync().ConfigureAwait(false);
            }
            else
            {
                await _transport.StartAsync(HandleInboundAsync, _stopping.Token).ConfigureAwait(false);
                KickOutbox();
            }

            _logger.LogInformation("Away {State}", away ? "on" : "off");
            WriteStatus();
        }

        return new ManagementResult(ManagementStatus.Ok);
    }

    /// <summary>The <c>tcp:</c> hints this device advertises in invite tokens, accept frames and every hello (spec §8.4).</summary>
    public string[] AdvertisedHints()
    {
        var port = (_transport as TcpTransport)?.Port ?? TcpTransport.DefaultPort;
        return [.. _hintHosts.Select(host => EndpointHint.ForTcp(host, port).ToString())];
    }

    /// <summary>Changes what this device advertises from now on, without a restart: <c>rtfc hints</c> wrote config.json and asked.</summary>
    public void SetHintHosts(IReadOnlyList<string> hosts)
    {
        _hintHosts = [.. hosts];
        _logger.LogInformation("Advertising {Hints}", string.Join(", ", AdvertisedHints()));
    }

    private const int MaxLearnedHints = 16;

    /// <summary>
    /// A contact's device says where it can be reached in every hello (spec §8.4), and its record follows, so hints refresh
    /// whenever we talk instead of only on a new invite. The claim is about the sender's own device and arrives on an
    /// authenticated session, but it is still input: shape-checked and capped.
    /// </summary>
    private void LearnHints(SessionTrust.Authenticated trust, string[]? hints)
    {
        if (hints is not { Length: > 0 and <= MaxLearnedHints } || trust.PersonId == Self.PersonId)
        {
            return;
        }

        try
        {
            var usable = hints.Where(h => h.Length <= 128 && ParseHints([h]).Count == 1).Distinct(StringComparer.Ordinal).ToArray();
            var device = _db.GetDevice(trust.DeviceId);
            if (usable.Length == 0 || device is null || device.PersonId != trust.PersonId || device.Status != DeviceStatus.Active || device.Endpoints.SequenceEqual(usable))
            {
                return;
            }

            _db.UpsertDevice(device with { Endpoints = usable });
            _logger.LogInformation("{Person}/{Device} is now reachable at {Hints}", Ids.Fingerprint(trust.PersonId), device.Name, string.Join(", ", usable));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Learning is a courtesy on the way into a session; the session, and the message behind it, must not pay for a failure here.
            _logger.LogWarning(ex, "Could not record the hints {Person} sent in its hello", Ids.Fingerprint(trust.PersonId));
        }
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

        // An active contact's token is still worth presenting: removal and block are local (spec §5.2), so
        // only the inviter knows whether we are still contacts, and a fresh exchange refreshes their hints.
        var wasActive = existing?.Status == ContactStatus.Active;

        var hints = ParseHints(payload.Hints);
        var stream = await _transport.ConnectAsync("", hints, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return new AcceptResult(AcceptStatus.NobodyHome, Reason: "The inviter has no device online. The token stays valid.");
        }

        try
        {
            await using var session = await PeerSession.ConnectAsync(stream, Self, Anchors(), DeviceListVersion, AdvertisedHints(), cancellationToken).ConfigureAwait(false);
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
                        return new AcceptResult(
                            wasActive ? AcceptStatus.AlreadyContact : AcceptStatus.Accepted, contact.Handle, contact.PersonId, Ids.Fingerprint(contact.PersonId));
                    }

                case ErrorFrame { Code: "blocked" } error:
                    return new AcceptResult(AcceptStatus.Blocked, Reason: error.Message);

                case ErrorFrame { Code: "invite_used" or "invite_unknown" or "invite_expired" } when wasActive:
                    // The token is spent, but we are contacts already; nothing to fix.
                    return new AcceptResult(AcceptStatus.AlreadyContact, existing!.Handle, existing.PersonId, Ids.Fingerprint(existing.PersonId));

                case ErrorFrame error:
                    return new AcceptResult(AcceptStatus.Rejected, Reason: error.Message);

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

    public Task<SendResult> SendAsync(string to, string text, CancellationToken cancellationToken) =>
        SendAsync(to, text, new SendOptions(), cancellationToken);

    public Task<SendResult> SendAsync(string to, string text, bool leave, CancellationToken cancellationToken) =>
        SendAsync(to, text, new SendOptions(Leave: leave), cancellationToken);

    /// <summary>
    /// Sends to a contact now. With <see cref="SendOptions.Leave"/>, a message nobody is home for waits in the outbox instead
    /// (spec §7.2, "leave it for her"). With <see cref="SendOptions.Project"/>, it is addressed to one of their projects (spec §7.6).
    /// </summary>
    public async Task<SendResult> SendAsync(string to, string text, SendOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SendResult.Rejected("empty_message");
        }

        if (Encoding.UTF8.GetByteCount(text) > MessageFrame.MaxBodyBytes)
        {
            return SendResult.Rejected("body_too_large");
        }

        var project = string.IsNullOrWhiteSpace(options.Project) ? null : options.Project.Trim();
        if (project is not null && !ProjectName.IsValid(project))
        {
            return SendResult.Rejected("invalid_project");
        }

        var slash = to.IndexOf('/');
        var handle = slash < 0 ? to : to[..slash];
        var deviceName = slash < 0 ? null : to[(slash + 1)..];

        var contact = _db.FindContactByHandle(handle.Trim());
        if (contact is null || contact.Status != ContactStatus.Active)
        {
            return SendResult.Rejected("not_a_contact");
        }

        // An answer to a message addressed to a project lands in the project it was sent from (spec §7.6), decided here, not on the wire.
        var replyProject = project is not null && options.FromDirectory is { } from ? RegisterProject(from).Id : null;
        var id = Ulid.NewUlid(_clock.GetUtcNow());
        var outgoing = new Outgoing(id, Thread: id, ReplyTo: null, Hop: 0, text, MessageOrigin.Human, project, replyProject);
        var result = await DeliverAsync(contact, deviceName, outgoing, cancellationToken).ConfigureAwait(false);
        if (result.Status == SendStatus.NobodyHome && options.Leave)
        {
            result = Queue(contact, deviceId: null, outgoing);
        }

        return project is null ? result : result with { Project = project };
    }

    /// <summary>
    /// A message or reply on its way out. <see cref="Project"/> travels in the envelope; <see cref="ReplyProject"/> stays here, in
    /// <c>sent.project_id</c>, and is where an answer to it will land (spec §7.6).
    /// </summary>
    private sealed record Outgoing(string Id, string Thread, string? ReplyTo, int Hop, string Text, string Origin, string? Project = null, string? ReplyProject = null);

    private async Task<SendResult> DeliverAsync(ContactRow contact, string? deviceName, Outgoing message, CancellationToken cancellationToken)
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
            var outcome = await DeliverToDeviceAsync(contact, ca, device, message, cancellationToken).ConfigureAwait(false);
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

        if (delivered.Count > 0)
        {
            var now = _clock.GetUtcNow();
            _db.InsertSent(new SentRow(
                message.Id, contact.PersonId, deviceName is null ? null : targets[0].DeviceId, message.Thread, message.ReplyTo, message.Origin,
                message.ReplyTo is null ? SentKind.Message : SentKind.Reply, message.Text, now, DeliveredAt: now, ReadAt: null, ExpiresAt: null,
                SentState.Delivered, message.ReplyProject));
        }

        if (delivered.Count == targets.Count)
        {
            return new SendResult(SendStatus.Delivered, message.Id, To: [.. delivered]);
        }

        if (delivered.Count > 0)
        {
            return new SendResult(SendStatus.Partial, message.Id, To: [.. delivered], Unreachable: [.. unreachable]);
        }

        if (failure is not null)
        {
            return new SendResult(SendStatus.Failed, message.Id, Reason: failure);
        }

        if (deviceName is not null)
        {
            var others = devices.Where(d => !targets.Contains(d)).ToList();
            var online = await ProbeAsync(others, cancellationToken).ConfigureAwait(false);
            return new SendResult(SendStatus.DeviceOffline, Requested: $"{contact.Handle}/{deviceName}",
                Online: [.. online.Where(kv => kv.Value).Select(kv => $"{contact.Handle}/{kv.Key.Name}")]);
        }

        return new SendResult(SendStatus.NobodyHome, message.Id, Person: contact.Handle);
    }

    /// <summary>An authenticated session to one of a contact's devices, or null when it is unreachable or someone else answered.</summary>
    private async Task<PeerSession?> OpenSessionAsync(ContactRow contact, X509Certificate2 ca, DeviceRow device, CancellationToken cancellationToken)
    {
        var stream = await _transport.ConnectAsync(device.DeviceId, ParseHints(device.Endpoints), cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        try
        {
            var session = await PeerSession.ConnectAsync(stream, Self, [ca], DeviceListVersion, AdvertisedHints(), cancellationToken).ConfigureAwait(false);
            if (session.Trust is SessionTrust.Authenticated trust && trust.PersonId == contact.PersonId && trust.DeviceId == device.DeviceId)
            {
                LearnHints(trust, session.RemoteHello.Hints);
                return session;
            }

            _logger.LogWarning("The device at {Hints} is not {Handle}/{Device}", string.Join(", ", device.Endpoints), contact.Handle, device.Name);
            await session.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ProtocolException or TimeoutException)
        {
            _logger.LogDebug(ex, "Handshake with {Handle}/{Device} failed", contact.Handle, device.Name);
            return null;
        }
    }

    /// <summary>Null on success, "unreachable" when no hint answered, otherwise a reason.</summary>
    private async Task<string?> DeliverToDeviceAsync(ContactRow contact, X509Certificate2 ca, DeviceRow device, Outgoing message, CancellationToken cancellationToken)
    {
        var session = await OpenSessionAsync(contact, ca, device, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return "unreachable";
        }

        try
        {
            await using var _ = session;
            var seq = _db.NextSeqOut(device.DeviceId);
            var envelope = new MessageFrame(
                HelloFrame.CurrentVersion, message.Id,
                new Address(Self.PersonId, Self.DeviceId), new Address(contact.PersonId, device.DeviceId),
                seq, message.Thread, message.ReplyTo, message.Origin, message.Hop, Timestamps.Format(_clock.GetUtcNow()), new MessageBody(message.Text), message.Project);
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
            session = await PeerSession.AcceptAsync(stream, Self, Anchors(), DeviceListVersion, AdvertisedHints(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ProtocolException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "An inbound connection did not complete the handshake");
            return;
        }

        await using (session)
        {
            if (session.Trust is SessionTrust.Authenticated caller)
            {
                LearnHints(caller, session.RemoteHello.Hints);
                // Someone we know is home: whatever waits for them can go now (spec §13, "handshakes trigger it").
                KickOutbox();
            }

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
                        case ReceiptFrame receipt:
                            await session.SendAsync(ReceiveReceipt(trust, receipt), cancellationToken).ConfigureAwait(false);
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

        if (message.Origin is not (MessageOrigin.Human or MessageOrigin.Auto) || message.Hop < 0
            || (message.Project is not null && !ProjectName.IsValid(message.Project)))
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

        var contact = _db.GetContact(trust.PersonId);
        if (contact is null || contact.Status != ContactStatus.Active)
        {
            return new AckFrame(message.Id, AckStatus.Rejected, "not_a_contact");
        }

        var now = _clock.GetUtcNow();
        if (_db.CountReceivedFrom(trust.DeviceId, now - RateWindow) >= _options.AutoAnswer.InboundPerDevicePerHour)
        {
            // Enforced before the write (spec §7.4): a flood never reaches the database.
            return new AckFrame(message.Id, AckStatus.Rejected, "rate_limited");
        }

        // The ack below is the same wherever the message lands, so a contact can't probe which projects exist (spec §7.6).
        var (projectId, note) = Route(trust.PersonId, message);
        var row = new InboxMessage(
            message.Id, Self.DeviceId, message.From.Person, message.From.Device, message.Seq, message.ReplyTo, message.Origin,
            message.Hop, message.Thread ?? message.Id, message.Body.Text, TryParseTime(message.SentAt), now, now,
            InboxState.Parked, HandledBy: null, HandledAt: null, Note: note, ProjectId: projectId);
        if (!_db.InsertMessage(row))
        {
            return new AckFrame(message.Id, AckStatus.Duplicate);
        }

        _db.RecordSeqIn(message.From.Device, message.Seq);
        ConsiderAutoAnswer(contact, row);
        WriteStatus();
        InboxChanged?.Invoke();
        return new AckFrame(message.Id, AckStatus.Ok);
    }

    // ---- inbox (spec §9.2) ----

    /// <summary>The states that wait for a human: parked, and auto-answers that failed.</summary>
    private static bool NeedsAttention(InboxMessage m) => m.State is InboxState.Parked or InboxState.AutoFailed;

    /// <summary>Every message in <paramref name="state"/>, whichever project it is in. What the CLI shows.</summary>
    public InboxSummary[] ListInbox(string? state) => ListInbox(state, directory: null, allProjects: true).Messages;

    /// <summary>
    /// The inbox as a session sees it (spec §7.6, §9.2): with <paramref name="allProjects"/> or no <paramref name="directory"/>,
    /// everything; otherwise the shared inbox and this directory's project, plus a count of what is parked in the others.
    /// </summary>
    public InboxListing ListInbox(string? state, string? directory, bool allProjects)
    {
        var handles = _db.ListContacts().ToDictionary(c => c.PersonId, c => c.Handle);
        var projects = _db.ListProjects().ToDictionary(p => p.Id);
        var here = allProjects || directory is null ? null : RegisterProject(directory).Id;
        bool Visible(InboxMessage m) => here is null || ProjectIdOf(m, projects) is null || ProjectIdOf(m, projects) == here;

        var messages = state == InboxState.Parked ? _db.ListMessages(null).Where(NeedsAttention) : _db.ListMessages(state);
        var summaries = messages.Where(Visible).Select(m => new InboxSummary(
            m.Id, FromLabel(m, handles), m.Kind == InboxKind.Person ? DeviceName(m.FromDevice) : "", m.State,
            Preview(m.Kind == InboxKind.Source ? $"{m.Title}: {m.Body}" : m.Body), m.ReceivedAt, m.ReplyTo, m.Origin, m.Note,
            m.Kind == InboxKind.Person ? _db.ListSentReplies(m.Id).LastOrDefault()?.State : null, m.Kind,
            ProjectIdOf(m, projects) is { } p ? projects[p].Name : null,
            m.Kind == InboxKind.Source ? m.Title : null, m.Kind == InboxKind.Source ? EntityIdOf(m.EntityKey) : null, m.Kind == InboxKind.Source ? m.Url : null,
            m.Kind == InboxKind.Source ? EventHistory(m).Length : 0));

        var elsewhere = here is null
            ? []
            : _db.ListMessages(null).Where(m => NeedsAttention(m) && !Visible(m)).GroupBy(m => ProjectIdOf(m, projects)!)
                .Select(g => new ProjectCount(projects[g.Key].Name, g.Count(), [.. g.Select(m => FromLabel(m, handles)).Distinct()]));
        return new InboxListing([.. summaries], [.. elsewhere]);
    }

    /// <summary>A message's project, or null for the shared inbox. A project that is no longer known falls back to the shared inbox.</summary>
    private static string? ProjectIdOf(InboxMessage m, Dictionary<string, ProjectRow> projects) =>
        m.ProjectId is { } id && projects.ContainsKey(id) ? id : null;

    private static string FromLabel(InboxMessage m, Dictionary<string, string> handles) => m.Kind switch
    {
        InboxKind.Notice => "rtfc",
        InboxKind.Source => SourceTypeOf(m.EntityKey),
        _ => handles.GetValueOrDefault(m.FromPerson, Ids.Fingerprint(m.FromPerson)),
    };

    /// <summary>A source item's stored history, oldest first; empty for anything else.</summary>
    private static Sources.SourceEvent[] EventHistory(InboxMessage m) =>
        m.Kind == InboxKind.Source && m.Events is { } json ? System.Text.Json.JsonSerializer.Deserialize(json, Sources.SourceJson.Default.SourceEventArray) ?? [] : [];

    /// <summary>Marks a message dismissed without answering it (spec §9.2). False when there is no such message.</summary>
    public bool Dismiss(string id)
    {
        if (_db.GetMessage(id) is null)
        {
            return false;
        }

        _db.SetMessageState(id, InboxState.Dismissed, Self.DeviceId, _clock.GetUtcNow());
        WriteStatus();
        InboxChanged?.Invoke();
        return true;
    }

    /// <summary>Marks a parked message read and, if the contact gets receipts from us, queues one (spec §7.3).</summary>
    public InboxOpened? Open(string id)
    {
        var message = _db.GetMessage(id);
        if (message is null)
        {
            return null;
        }

        var contact = _db.GetContact(message.FromPerson);
        if (message.State == InboxState.Parked)
        {
            _db.SetMessageState(id, InboxState.Read, handledBy: null, _clock.GetUtcNow());
            message = message with { State = InboxState.Read };
            if (message.Kind == InboxKind.Person && contact is { Status: ContactStatus.Active, ReadReceipts: true })
            {
                QueueReceipt(contact, message);
            }

            WriteStatus();
            InboxChanged?.Invoke();
        }

        var from = message.Kind switch
        {
            InboxKind.Notice => "rtfc",
            InboxKind.Source => SourceTypeOf(message.EntityKey),
            _ => contact?.Handle ?? Ids.Fingerprint(message.FromPerson),
        };
        var replies = message.Kind == InboxKind.Person
            ? _db.ListSentReplies(message.Id).Select(r => new SentSummary(r.Id, r.State, r.Origin, r.SentAt, r.DeliveredAt, r.ReadAt, r.ExpiresAt)).ToArray()
            : [];
        var isSource = message.Kind == InboxKind.Source;
        return new InboxOpened(
            message.Id, from, message.Kind == InboxKind.Person ? DeviceName(message.FromDevice) : "", message.FromPerson, message.State,
            message.ReceivedAt, message.SentAt, message.Thread, message.ReplyTo, message.Origin, message.Hop, message.Body, message.Note,
            message.Draft, replies.Length == 0 ? null : replies, message.Kind,
            message.ProjectId is { } project ? _db.GetProject(project)?.Name : null,
            isSource ? message.Title : null, isSource ? EntityIdOf(message.EntityKey) : null, isSource ? message.Url : null, isSource ? from : null,
            isSource ? [.. EventHistory(message).Select(e => new SourceEventView(e.OccurredAt, e.EventType, e.Actor, e.Summary))] : null);
    }

    /// <summary>Replies to the person who sent a message: delivered now, or queued in the outbox if nobody is home (spec §7.2).</summary>
    public async Task<SendResult> ReplyAsync(string id, string text, CancellationToken cancellationToken)
    {
        var original = _db.GetMessage(id);
        if (original is null)
        {
            return SendResult.Rejected("unknown_message");
        }

        if (original.Kind != InboxKind.Person)
        {
            // rtfc never writes to a source (spec §10): acting on an item happens with the session's own tools.
            return SendResult.Rejected(original.Kind == InboxKind.Source ? "source_item" : "not_a_message");
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

        // A reply to a message in one of our projects records that project, so the answer to the reply lands there too (spec §7.6).
        var reply = new Outgoing(
            Ulid.NewUlid(_clock.GetUtcNow()), original.Thread ?? original.Id, ReplyTo: id, original.Hop + 1, text, MessageOrigin.Human, ReplyProject: original.ProjectId);
        var result = await DeliverAsync(contact, deviceName: null, reply, cancellationToken).ConfigureAwait(false);
        if (result.Status == SendStatus.NobodyHome)
        {
            result = Queue(contact, deviceId: null, reply);
            _db.SetMessageNote(id, $"Your reply waits in the outbox until {contact.Handle} is next home (until {Timestamps.Format(result.ExpiresAt!.Value)}).", _clock.GetUtcNow());
        }

        if (result.Status is SendStatus.Delivered or SendStatus.Partial or SendStatus.Queued)
        {
            _db.SetMessageState(id, InboxState.Answered, Self.DeviceId, _clock.GetUtcNow());
            WriteStatus();
            InboxChanged?.Invoke();
        }

        return result;
    }

    // ---- management (spec §5.2, §7.3): reached only through the CLI, never through an MCP tool ----

    /// <summary>
    /// Sets a contact's inbound mode (spec §7.3). Headless auto-answer needs an existing scope directory; session mode needs the
    /// id of the Claude Code session that will answer, which is the session the command was run from.
    /// </summary>
    public ManagementResult SetAutoMode(string handle, string mode, string? scope, string? session = null)
    {
        var contact = _db.FindContactByHandle(handle.Trim());
        if (contact is null || contact.Status != ContactStatus.Active)
        {
            return new ManagementResult(ManagementStatus.NotAContact, Reason: $"'{handle}' is not an active contact.");
        }

        switch (mode)
        {
            case "off":
                _db.UpsertContact(contact with { InboundMode = InboundMode.Park, AutoScope = null, AutoOwnerDevice = null, AutoSession = null, Rev = contact.Rev + 1 });
                _logger.LogInformation("Auto-answer off for {Handle}", contact.Handle);
                return new ManagementResult(ManagementStatus.Ok, contact.Handle);

            case "headless":
                if (string.IsNullOrWhiteSpace(scope))
                {
                    return new ManagementResult(ManagementStatus.Invalid, contact.Handle, "Headless auto-answer needs --scope <dir>: the only directory the answering Claude may read.");
                }

                var full = Path.GetFullPath(scope);
                if (!Directory.Exists(full))
                {
                    return new ManagementResult(ManagementStatus.Invalid, contact.Handle, $"'{full}' is not a directory.");
                }

                _db.UpsertContact(contact with { InboundMode = InboundMode.AutoHeadless, AutoScope = full, AutoOwnerDevice = Self.DeviceId, AutoSession = null, Rev = contact.Rev + 1 });
                _logger.LogInformation("Auto-answer headless for {Handle}, scope {Scope}", contact.Handle, full);
                return new ManagementResult(ManagementStatus.Ok, contact.Handle);

            case "session":
                if (string.IsNullOrWhiteSpace(session))
                {
                    return new ManagementResult(ManagementStatus.Invalid, contact.Handle,
                        "Session auto-answer is set from inside the Claude Code session that should answer: run /rtfc:auto there.");
                }

                _db.UpsertContact(contact with { InboundMode = InboundMode.AutoSession, AutoScope = null, AutoOwnerDevice = Self.DeviceId, AutoSession = session.Trim(), Rev = contact.Rev + 1 });
                _logger.LogInformation("Auto-answer in session {Session} for {Handle}", session.Trim(), contact.Handle);
                return new ManagementResult(ManagementStatus.Ok, contact.Handle);

            default:
                return new ManagementResult(ManagementStatus.Invalid, contact.Handle, $"Unknown mode '{mode}'. Use off, headless or session.");
        }
    }

    /// <summary>Changes the local petname (spec §2). Identity is unaffected; the other side never learns.</summary>
    public ManagementResult Rename(string handle, string newHandle)
    {
        var contact = _db.FindContactByHandle(handle.Trim());
        if (contact is null)
        {
            return new ManagementResult(ManagementStatus.NotAContact, Reason: $"'{handle}' is not a contact.");
        }

        var sanitized = InviteToken.SanitizeHandle(newHandle, contact.PersonId);
        var taken = _db.FindContactByHandle(sanitized);
        if (taken is not null && taken.PersonId != contact.PersonId)
        {
            return new ManagementResult(ManagementStatus.Invalid, contact.Handle, $"'{sanitized}' already names another contact.");
        }

        _db.UpsertContact(contact with { Handle = sanitized, Rev = contact.Rev + 1 });
        return new ManagementResult(ManagementStatus.Ok, sanitized);
    }

    /// <summary>Whether we tell this contact when we read their messages (spec §7.3).</summary>
    public ManagementResult SetReceipts(string handle, bool on)
    {
        var contact = _db.FindContactByHandle(handle.Trim());
        if (contact is null || contact.Status != ContactStatus.Active)
        {
            return new ManagementResult(ManagementStatus.NotAContact, Reason: $"'{handle}' is not an active contact.");
        }

        _db.UpsertContact(contact with { ReadReceipts = on, Rev = contact.Rev + 1 });
        return new ManagementResult(ManagementStatus.Ok, contact.Handle);
    }

    /// <summary>Removal is local and immediate (spec §5.2): the contact's CA leaves the trust store, so their next handshake is restricted.</summary>
    public ManagementResult Remove(string handle) => SetStatus(handle, ContactStatus.Removed);

    /// <summary>Block is remove plus refusing every future invite exchange with that person key.</summary>
    public ManagementResult Block(string handle) => SetStatus(handle, ContactStatus.Blocked);

    private ManagementResult SetStatus(string handle, string status)
    {
        var contact = _db.FindContactByHandle(handle.Trim());
        if (contact is null || (contact.Status != ContactStatus.Active && status == ContactStatus.Removed))
        {
            return new ManagementResult(ManagementStatus.NotAContact, Reason: $"'{handle}' is not an active contact.");
        }

        _db.UpsertContact(contact with { Status = status, InboundMode = InboundMode.Park, AutoScope = null, AutoOwnerDevice = null, AutoSession = null, Rev = contact.Rev + 1 });
        _logger.LogInformation("Contact {Handle} is now {Status}", contact.Handle, status);
        return new ManagementResult(ManagementStatus.Ok, contact.Handle);
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
                contact.Handle, contact.PersonId, Ids.Fingerprint(contact.PersonId), contact.Status, contact.InboundMode, contact.AutoScope,
                contact.ReadReceipts, _db.CountOutbox(OutboxState.Pending, contact.PersonId), contact.AcceptedAt,
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

    /// <summary>The shared inbox under <c>global</c>, and messages addressed to a project under that project's root key (spec §11).</summary>
    public StatusSnapshot Status()
    {
        var parked = _db.ListMessages(null).Where(m => NeedsAttention(m) && m.Kind != InboxKind.Source).ToList();
        var handles = _db.ListContacts().ToDictionary(c => c.PersonId, c => c.Handle);
        var projects = _db.ListProjects().ToDictionary(p => p.Id);
        var shared = parked.Where(m => ProjectIdOf(m, projects) is null).ToList();
        var pending = _db.ListSubscriptions().Where(s => s.Status == SubscriptionStatus.PendingApproval).GroupBy(s => s.ProjectId).ToDictionary(g => g.Key, g => g.Count());
        var perProject = new Dictionary<string, ProjectStatus>();
        foreach (var project in projects.Values)
        {
            var messages = parked.Where(m => ProjectIdOf(m, projects) == project.Id).ToList();
            var tickets = _db.CountSourceItems(project.Id, "jira:");
            var reviews = _db.CountSourceItems(project.Id, "bitbucket:") + _db.CountSourceItems(project.Id, "github:");
            var pages = _db.CountSourceItems(project.Id, "confluence:");
            var waiting = pending.GetValueOrDefault(project.Id);
            if (messages.Count > 0 || tickets > 0 || reviews > 0 || pages > 0 || waiting > 0)
            {
                perProject[project.RootPath] = new ProjectStatus(project.Name, messages.Count, [.. messages.Select(m => FromLabel(m, handles)).Distinct()], reviews, tickets, waiting, pages);
            }
        }

        return new StatusSnapshot(
            new StatusGlobal(shared.Count, [.. shared.Select(m => FromLabel(m, handles)).Distinct()], _db.CountOutbox(OutboxState.Pending)), perProject, IsAway);
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
