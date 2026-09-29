using System.Net;
using System.Net.Sockets;

namespace Rtfc.Net;

/// <summary>
/// Direct TCP to <c>tcp:host:port</c> hints (spec §8.3). No discovery: on the office LAN
/// hostnames already resolve, and Phase 3 adds VPN addresses as more hints of the same kind.
/// </summary>
public sealed class TcpTransport(int port, TimeSpan? connectTimeout = null) : ITransport
{
    public const int DefaultPort = 47821;

    private readonly TimeSpan _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    private TcpListener? _listener;
    private CancellationTokenSource? _stopping;
    private Task? _acceptLoop;

    public string Kind => EndpointHint.Tcp;

    /// <summary>The port actually bound. Differs from the requested one only when that was 0, which tests use.</summary>
    public int Port { get; private set; } = port;

    public bool IsListening => _listener is not null;

    public Task StartAsync(Func<Stream, Task> onInbound, CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The transport is already listening.");
        }

        var listener = Socket.OSSupportsIPv6 ? new TcpListener(IPAddress.IPv6Any, Port) : new TcpListener(IPAddress.Any, Port);
        if (Socket.OSSupportsIPv6)
        {
            listener.Server.DualMode = true;
        }

        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(listener, onInbound, _stopping.Token);
        return Task.CompletedTask;
    }

    private static async Task AcceptLoopAsync(TcpListener listener, Func<Stream, Task> onInbound, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            client.NoDelay = true;
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await onInbound(client.GetStream()).ConfigureAwait(false);
                    }
                    catch (Exception) when (!stopping.IsCancellationRequested)
                    {
                        // The handler is responsible for logging; a misbehaving peer must not stop the loop.
                    }
                }
            }, CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null)
        {
            return;
        }

        _stopping?.Cancel();
        listener.Stop();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        _stopping?.Dispose();
        _stopping = null;
        _acceptLoop = null;
    }

    public async Task<bool> IsReachableAsync(string deviceId, IReadOnlyList<EndpointHint> hints, CancellationToken cancellationToken)
    {
        var stream = await ConnectAsync(deviceId, hints, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return false;
        }

        await stream.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Tries every <c>tcp</c> hint at once, started a quarter second apart in the order given, and takes the first that connects
    /// (spec §8.3). A device beyond the office lists its LAN address and its VPN address; trying them one after another would cost
    /// a whole connect timeout per unreachable hint, and the "who's home" probe would call the device away. The stagger lets a
    /// reachable first hint win on its own, without a burst of connections. Null when no hint answered; the caller's cancellation
    /// is reported as cancellation.
    /// </summary>
    public async Task<Stream?> ConnectAsync(string deviceId, IReadOnlyList<EndpointHint> hints, CancellationToken cancellationToken)
    {
        var endpoints = new List<(string Host, int Port)>();
        foreach (var hint in hints)
        {
            if (hint.Kind != EndpointHint.Tcp)
            {
                continue;
            }

            try
            {
                endpoints.Add(hint.TcpEndpoint());
            }
            catch (FormatException)
            {
                // A hint we cannot read is a hint we cannot use.
            }
        }

        if (endpoints.Count == 0)
        {
            return null;
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = new HashSet<Task<TcpClient?>>();
        for (var i = 0; i < endpoints.Count; i++)
        {
            attempts.Add(AttemptAsync(endpoints[i], Stagger * i, race.Token));
        }

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);
            var client = await finished.ConfigureAwait(false);
            if (client is null)
            {
                continue;
            }

            await race.CancelAsync().ConfigureAwait(false);
            foreach (var late in attempts)
            {
                _ = DisposeLateAsync(late);
            }

            return new OwningNetworkStream(client);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>One hint: waits its turn, then connects with its own timeout. Null when it did not connect, whatever the reason.</summary>
    private async Task<TcpClient?> AttemptAsync((string Host, int Port) endpoint, TimeSpan delay, CancellationToken race)
    {
        TcpClient? client = null;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, race).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(race);
            timeout.CancelAfter(_connectTimeout);
            client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
            if (race.IsCancellationRequested)
            {
                client.Dispose();
                return null;
            }

            client.NoDelay = true;
            return client;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            client?.Dispose();
            return null;
        }
    }

    /// <summary>A loser that connects after the winner was chosen has nobody to hand its socket to.</summary>
    private static async Task DisposeLateAsync(Task<TcpClient?> attempt)
    {
        try
        {
            (await attempt.ConfigureAwait(false))?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>A network stream that disposes its client with it, so a caller holding only the stream leaks nothing.</summary>
    private sealed class OwningNetworkStream(TcpClient client) : NetworkStream(client.Client, ownsSocket: true)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                client.Dispose();
            }
        }
    }
}
