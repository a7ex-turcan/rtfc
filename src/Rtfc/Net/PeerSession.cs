using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Rtfc.Identity;
using Rtfc.Protocol;

namespace Rtfc.Net;

/// <summary>What the handshake proved about the peer.</summary>
public abstract record SessionTrust
{
    /// <summary>The leaf chains to our own CA or an active contact's. Every frame is allowed.</summary>
    public sealed record Authenticated(string PersonId, string DeviceId) : SessionTrust;

    /// <summary>The leaf is proven, but its CA is unknown to us. Only <c>invite_accept</c> is allowed (spec §5.1).</summary>
    public sealed record Restricted(string DeviceId) : SessionTrust;
}

/// <summary>
/// Mutual TLS over any transport stream, then length-prefixed JSON frames (spec §8.2).
/// Validation ignores the OS trust store entirely: the only anchors are the person CAs
/// the caller pins. There is no "trusted LAN" mode and no plaintext mode, so a future
/// relay carries the same session end to end without being able to read it.
/// </summary>
public sealed class PeerSession : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly SslStream _tls;
    private readonly PipeReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    private PeerSession(SslStream tls, X509Certificate2 remoteCertificate, SessionTrust trust, HelloFrame remoteHello)
    {
        _tls = tls;
        _reader = PipeReader.Create(tls, new StreamPipeReaderOptions(leaveOpen: true));
        RemoteCertificate = remoteCertificate;
        Trust = trust;
        RemoteHello = remoteHello;
    }

    public X509Certificate2 RemoteCertificate { get; }
    public SessionTrust Trust { get; }
    public HelloFrame RemoteHello { get; }
    public string RemoteDeviceId => Trust switch
    {
        SessionTrust.Authenticated a => a.DeviceId,
        SessionTrust.Restricted r => r.DeviceId,
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The server side: wraps an inbound transport stream. Fails unless the peer presents a certificate.</summary>
    public static Task<PeerSession> AcceptAsync(
        Stream transport, SelfIdentity self, IReadOnlyCollection<X509Certificate2> anchors, long deviceListVersion, IReadOnlyList<string>? hints, CancellationToken cancellationToken)
    {
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(self.DeviceCertificate, additionalCertificates: null, offline: true),
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.None, // the OS picks: TLS 1.3 where it can, never below 1.2 (checked after)
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = AcceptAnyPresentedCertificate,
        };
        return HandshakeAsync(transport, anchors, deviceListVersion, hints, tls => tls.AuthenticateAsServerAsync(options, cancellationToken), cancellationToken);
    }

    /// <summary>The client side: wraps an outbound transport stream and offers our device certificate.</summary>
    public static Task<PeerSession> ConnectAsync(
        Stream transport, SelfIdentity self, IReadOnlyCollection<X509Certificate2> anchors, long deviceListVersion, IReadOnlyList<string>? hints, CancellationToken cancellationToken)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "rtfc", // names are hints; the callback never looks at this
            ClientCertificates = [self.DeviceCertificate],
            // Chosen explicitly so the OS cannot filter it out for not matching the server's issuer list.
            LocalCertificateSelectionCallback = (_, _, _, _, _) => self.DeviceCertificate,
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = AcceptAnyPresentedCertificate,
        };
        return HandshakeAsync(transport, anchors, deviceListVersion, hints, tls => tls.AuthenticateAsClientAsync(options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The handshake proves possession of the leaf's key. Whether that leaf belongs to
    /// anyone we know is decided afterwards, against our own anchors, because a leaf from
    /// an unknown CA is exactly what an invite acceptance looks like.
    /// </summary>
    private static bool AcceptAnyPresentedCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null;

    private static async Task<PeerSession> HandshakeAsync(
        Stream transport,
        IReadOnlyCollection<X509Certificate2> anchors,
        long deviceListVersion,
        IReadOnlyList<string>? hints,
        Func<SslStream, Task> authenticate,
        CancellationToken cancellationToken)
    {
        var tls = new SslStream(transport, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandshakeTimeout);

            await authenticate(tls).WaitAsync(timeout.Token).ConfigureAwait(false);

            if (tls.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
            {
                throw new AuthenticationException($"The peer negotiated {tls.SslProtocol}; TLS 1.2 is the minimum.");
            }

            var remote = tls.RemoteCertificate ?? throw new AuthenticationException("The peer presented no certificate.");
            var leaf = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
            var trust = Classify(leaf, anchors);

            var hello = new HelloFrame(HelloFrame.CurrentVersion, deviceListVersion, hints is { Count: > 0 } ? [.. hints] : null);
            await tls.WriteAsync(FrameCodec.Encode(Frames.Serialize(hello)), timeout.Token).ConfigureAwait(false);

            var session = new PeerSession(tls, leaf, trust, await ReadHelloAsync(tls, timeout.Token).ConfigureAwait(false));
            return session;
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<HelloFrame> ReadHelloAsync(SslStream tls, CancellationToken cancellationToken)
    {
        // A reader just for the hello: it must not buffer past it, or the session's reader loses frames.
        var reader = PipeReader.Create(tls, new StreamPipeReaderOptions(leaveOpen: true, bufferSize: FrameCodec.HeaderBytes, minimumReadSize: 1));
        try
        {
            var payload = await FrameCodec.ReadAsync(reader, cancellationToken).ConfigureAwait(false)
                ?? throw new ProtocolException("The peer closed the session before saying hello.");
            if (Frames.Parse(payload) is not HelloFrame hello)
            {
                throw new ProtocolException("The first frame must be a hello.");
            }

            if (hello.V != HelloFrame.CurrentVersion)
            {
                throw new ProtocolException($"The peer speaks protocol version {hello.V}; this build speaks {HelloFrame.CurrentVersion}.");
            }

            return hello;
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static SessionTrust Classify(X509Certificate2 leaf, IReadOnlyCollection<X509Certificate2> anchors)
    {
        var deviceId = Ids.Device(leaf);
        var issuer = Certificates.FindIssuer(leaf, anchors);
        return issuer is null
            ? new SessionTrust.Restricted(deviceId)
            : new SessionTrust.Authenticated(Ids.Person(issuer), deviceId);
    }

    public async ValueTask SendAsync(Frame frame, CancellationToken cancellationToken)
    {
        var bytes = FrameCodec.Encode(Frames.Serialize(frame));
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _tls.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _tls.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The next frame, or null when the peer closed the session cleanly.</summary>
    public async ValueTask<Frame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var payload = await FrameCodec.ReadAsync(_reader, cancellationToken).ConfigureAwait(false);
        return payload is null ? null : Frames.Parse(payload.Value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _reader.CompleteAsync().ConfigureAwait(false);
        await _tls.DisposeAsync().ConfigureAwait(false);
        RemoteCertificate.Dispose();
        _writeLock.Dispose();
    }
}
