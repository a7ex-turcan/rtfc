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

    public async Task<Stream?> ConnectAsync(string deviceId, IReadOnlyList<EndpointHint> hints, CancellationToken cancellationToken)
    {
        foreach (var hint in hints)
        {
            if (hint.Kind != EndpointHint.Tcp)
            {
                continue;
            }

            (string host, int port) endpoint;
            try
            {
                endpoint = hint.TcpEndpoint();
            }
            catch (FormatException)
            {
                continue;
            }

            var client = new TcpClient();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_connectTimeout);
                await client.ConnectAsync(endpoint.host, endpoint.port, timeout.Token).ConfigureAwait(false);
                client.NoDelay = true;
                return new OwningNetworkStream(client);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        return null;
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
