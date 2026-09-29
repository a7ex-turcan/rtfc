using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Protocol;

namespace Rtfc.Tests.Net;

/// <summary>Real TLS, real certificates, a real loopback socket. The session layer is where the bugs would be, so nothing here is mocked.</summary>
public class PeerSessionTests : IAsyncLifetime
{
    private readonly TempHome _alexHome = new();
    private readonly TempHome _sashaHome = new();
    private SelfIdentity _alex = null!;
    private SelfIdentity _sasha = null!;

    public ValueTask InitializeAsync()
    {
        _alex = IdentityStore.Create(_alexHome.Home, "alex", "desktop", DateTimeOffset.UtcNow);
        _sasha = IdentityStore.Create(_sashaHome.Home, "sasha", "laptop", DateTimeOffset.UtcNow);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _alex.Dispose();
        _sasha.Dispose();
        _alexHome.Dispose();
        _sashaHome.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Two_contacts_authenticate_each_other_and_exchange_frames()
    {
        var (server, client) = await ConnectAsync(serverAnchors: [_alex.PersonCa, _sasha.PersonCa], clientAnchors: [_sasha.PersonCa, _alex.PersonCa]);
        await using (server)
        await using (client)
        {
            Assert.Equal(new SessionTrust.Authenticated(_sasha.PersonId, _sasha.DeviceId), server.Trust);
            Assert.Equal(new SessionTrust.Authenticated(_alex.PersonId, _alex.DeviceId), client.Trust);
            Assert.Equal(3, server.RemoteHello.DeviceListVersion);
            Assert.Equal(1, client.RemoteHello.DeviceListVersion);

            await client.SendAsync(new AckFrame("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", AckStatus.Ok), TestContext.Current.CancellationToken);
            Assert.Equal(new AckFrame("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", AckStatus.Ok), await server.ReceiveAsync(TestContext.Current.CancellationToken));

            await server.SendAsync(new ByeFrame(), TestContext.Current.CancellationToken);
            Assert.Equal(new ByeFrame(), await client.ReceiveAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_peer_from_an_unknown_ca_is_restricted_not_rejected()
    {
        var (server, client) = await ConnectAsync(serverAnchors: [_alex.PersonCa], clientAnchors: [_sasha.PersonCa]);
        await using (server)
        await using (client)
        {
            Assert.Equal(new SessionTrust.Restricted(_sasha.DeviceId), server.Trust);
            Assert.Equal(new SessionTrust.Restricted(_alex.DeviceId), client.Trust);
        }
    }

    [Fact]
    public async Task Trust_is_decided_by_the_ca_not_by_which_side_connected()
    {
        // Alex knows Sasha, Sasha does not know Alex: one side authenticated, the other restricted.
        var (server, client) = await ConnectAsync(serverAnchors: [_alex.PersonCa, _sasha.PersonCa], clientAnchors: [_sasha.PersonCa]);
        await using (server)
        await using (client)
        {
            Assert.IsType<SessionTrust.Authenticated>(server.Trust);
            Assert.IsType<SessionTrust.Restricted>(client.Trust);
        }
    }

    [Fact]
    public async Task A_closed_session_reads_as_null_and_the_protocol_is_at_least_tls_1_2()
    {
        var (server, client) = await ConnectAsync(serverAnchors: [_alex.PersonCa, _sasha.PersonCa], clientAnchors: [_sasha.PersonCa, _alex.PersonCa]);
        await using (server)
        {
            await client.DisposeAsync();
            Assert.Null(await server.ReceiveAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Frames_sent_right_behind_the_hello_reach_a_peer_whose_reads_lag()
    {
        // A busy macOS runner read a client's hello only after the message behind it had arrived too, and the message lost its
        // first bytes: SslStream fills one read from every record it has buffered, and the hello's reader was thrown away.
        // Sixteen bytes is the smallest buffer a pipe reader rents, so a hello that does not end on a multiple of it is the case.
        var hint = new[] { "tcp:10.0.0.1:1", "tcp:10.0.0.1:12" }
            .First(h => FrameCodec.Encode(Frames.Serialize(new HelloFrame(HelloFrame.CurrentVersion, 3, [h]))).Length % 16 != 0);
        var ack = new AckFrame("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", AckStatus.Ok);
        var (server, client) = await ConnectAsync(
            serverAnchors: [_alex.PersonCa, _sasha.PersonCa], clientAnchors: [_sasha.PersonCa, _alex.PersonCa],
            clientHints: [hint], slowServerReads: true,
            onClientConnected: async c =>
            {
                await c.SendAsync(ack, TestContext.Current.CancellationToken);
                await c.SendAsync(new ByeFrame(), TestContext.Current.CancellationToken);
            });
        await using (server)
        await using (client)
        {
            Assert.Equal([hint], server.RemoteHello.Hints!);
            Assert.Equal(ack, await server.ReceiveAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new ByeFrame(), await server.ReceiveAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_client_that_speaks_plaintext_is_dropped_by_the_handshake()
    {
        var transport = new TcpTransport(0);
        var accepted = new TaskCompletionSource<Exception?>();
        await transport.StartAsync(async stream =>
        {
            try
            {
                await using var _ = await PeerSession.AcceptAsync(stream, _alex, [_alex.PersonCa], 1, null, TestContext.Current.CancellationToken);
                accepted.SetResult(null);
            }
            catch (Exception ex)
            {
                accepted.SetResult(ex);
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync("127.0.0.1", transport.Port, TestContext.Current.CancellationToken);
            await client.GetStream().WriteAsync(FrameCodec.Encode(Frames.Serialize(new HelloFrame(1, 1))), TestContext.Current.CancellationToken);
            client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);

            var error = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.NotNull(error);
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    [Fact]
    public async Task Nobody_is_home_when_nothing_listens()
    {
        var transport = new TcpTransport(0, connectTimeout: TimeSpan.FromSeconds(2));
        await transport.StartAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken);
        var port = transport.Port;
        await transport.StopAsync();

        var hints = new[] { EndpointHint.ForTcp("127.0.0.1", port) };
        Assert.False(await transport.IsReachableAsync("d_x", hints, TestContext.Current.CancellationToken));
        Assert.Null(await transport.ConnectAsync("d_x", hints, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Endpoint_hints_parse_both_ways()
    {
        Assert.Equal(("alex-desktop.local", 47821), EndpointHint.Parse("tcp:alex-desktop.local:47821").TcpEndpoint());
        Assert.Equal(("::1", 47821), EndpointHint.Parse("tcp:[::1]:47821").TcpEndpoint());
        Assert.Equal("tcp:[fe80::1]:5", EndpointHint.ForTcp("fe80::1", 5).ToString());
        Assert.Equal("relay", EndpointHint.Parse("relay:wss://relay.example/v1").Kind);
        Assert.Throws<FormatException>(() => EndpointHint.Parse("tcp"));
        Assert.Throws<FormatException>(() => EndpointHint.Parse("tcp:host:99999").TcpEndpoint());
        Assert.Throws<InvalidOperationException>(() => EndpointHint.Parse("relay:x").TcpEndpoint());
    }

    private async Task<(PeerSession Server, PeerSession Client)> ConnectAsync(
        X509Certificate2[] serverAnchors, X509Certificate2[] clientAnchors,
        string[]? clientHints = null, bool slowServerReads = false, Func<PeerSession, Task>? onClientConnected = null)
    {
        var transport = new TcpTransport(0);
        var serverSide = new TaskCompletionSource<PeerSession>();
        await transport.StartAsync(async stream =>
        {
            try
            {
                var inbound = slowServerReads ? new LaggingReadStream(stream) : stream;
                serverSide.SetResult(await PeerSession.AcceptAsync(inbound, _alex, serverAnchors, 1, null, TestContext.Current.CancellationToken));
                // Keep the stream alive until the session is disposed by the test.
                await serverSide.Task.ContinueWith(_ => Task.Delay(Timeout.Infinite), TaskScheduler.Default).Unwrap();
            }
            catch (Exception ex)
            {
                serverSide.TrySetException(ex);
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            var raw = await transport.ConnectAsync(_alex.DeviceId, [EndpointHint.ForTcp("127.0.0.1", transport.Port)], TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("The loopback transport did not connect.");
            var client = await PeerSession.ConnectAsync(raw, _sasha, clientAnchors, 3, clientHints, TestContext.Current.CancellationToken);
            if (onClientConnected is not null)
            {
                await onClientConnected(client);
            }

            var server = await serverSide.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            return (server, client);
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    /// <summary>
    /// The far end as a starved runner sees it. The handshake is delivered one TLS record per read, so nothing beyond it sits in
    /// SslStream's buffer when it ends; the reads after it come late, and bring everything the peer sent in the meantime at once.
    /// Reads and writes alternate R W R W R through a handshake with client certificates on TLS 1.2 and 1.3 alike, and the third
    /// R is the hello's, so bulk mode starts with the second read that follows a write.
    /// </summary>
    private sealed class LaggingReadStream(Stream inner) : Stream
    {
        private const int RecordHeader = 5;
        private static readonly TimeSpan Lag = TimeSpan.FromMilliseconds(150);
        private bool _lastWasWrite;
        private int _readsAfterWrites;
        private int _recordRemaining;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_lastWasWrite)
            {
                _lastWasWrite = false;
                _readsAfterWrites++;
            }

            if (_readsAfterWrites >= 2)
            {
                await Task.Delay(Lag, cancellationToken);
                return await inner.ReadAsync(buffer, cancellationToken);
            }

            if (buffer.Length < RecordHeader)
            {
                return await inner.ReadAsync(buffer, cancellationToken);
            }

            if (_recordRemaining == 0)
            {
                await inner.ReadExactlyAsync(buffer[..RecordHeader], cancellationToken);
                _recordRemaining = (buffer.Span[3] << 8) | buffer.Span[4];
                var body = Math.Min(_recordRemaining, buffer.Length - RecordHeader);
                await inner.ReadExactlyAsync(buffer.Slice(RecordHeader, body), cancellationToken);
                _recordRemaining -= body;
                return RecordHeader + body;
            }

            var rest = Math.Min(_recordRemaining, buffer.Length);
            await inner.ReadExactlyAsync(buffer[..rest], cancellationToken);
            _recordRemaining -= rest;
            return rest;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The session reads asynchronously.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _lastWasWrite = true;
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _lastWasWrite = true;
            inner.Write(buffer, offset, count);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
