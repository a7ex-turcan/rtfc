namespace Rtfc.Net;

/// <summary>
/// A transport moves bytes to a device and reports reachability, nothing more (spec §8.1).
/// Every stream it yields is wrapped in mutual TLS by <see cref="PeerSession"/>, so a
/// transport never has to be trusted, and adding one (VPN hints, a relay) touches
/// nothing above it.
/// </summary>
public interface ITransport
{
    /// <summary>"tcp", "mdns", "relay": the hint kinds this transport understands.</summary>
    string Kind { get; }

    /// <summary>Starts accepting inbound streams. <paramref name="onInbound"/> owns each stream it is given.</summary>
    Task StartAsync(Func<Stream, Task> onInbound, CancellationToken cancellationToken);

    Task StopAsync();

    /// <summary>The hints this transport can currently reach the device by, in the order to try them.</summary>
    Task<bool> IsReachableAsync(string deviceId, IReadOnlyList<EndpointHint> hints, CancellationToken cancellationToken);

    /// <summary>A raw stream to the device, or null if no hint answered.</summary>
    Task<Stream?> ConnectAsync(string deviceId, IReadOnlyList<EndpointHint> hints, CancellationToken cancellationToken);
}
