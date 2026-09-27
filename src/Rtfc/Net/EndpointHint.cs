using System.Globalization;

namespace Rtfc.Net;

/// <summary>
/// A typed way to reach a device (spec §8.4): <c>tcp:host:port</c> today, <c>relay:wss://…</c>
/// later. Hints are hints; only the TLS handshake proves who answered.
/// </summary>
public sealed record EndpointHint(string Kind, string Value)
{
    public const string Tcp = "tcp";

    public static EndpointHint Parse(string hint)
    {
        var colon = hint.IndexOf(':');
        if (colon <= 0 || colon == hint.Length - 1)
        {
            throw new FormatException($"'{hint}' is not a '<kind>:<value>' endpoint hint.");
        }

        return new EndpointHint(hint[..colon], hint[(colon + 1)..]);
    }

    public static EndpointHint ForTcp(string host, int port) =>
        new(Tcp, host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}");

    /// <summary>Splits a <c>tcp</c> hint into host and port. IPv6 literals are bracketed.</summary>
    public (string Host, int Port) TcpEndpoint()
    {
        if (Kind != Tcp)
        {
            throw new InvalidOperationException($"'{this}' is not a tcp hint.");
        }

        string host;
        string portText;
        if (Value.StartsWith('['))
        {
            var close = Value.IndexOf(']');
            if (close < 0 || close + 1 >= Value.Length || Value[close + 1] != ':')
            {
                throw new FormatException($"'{this}' is not a '[ipv6]:port' hint.");
            }

            host = Value[1..close];
            portText = Value[(close + 2)..];
        }
        else
        {
            var colon = Value.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new FormatException($"'{this}' has no port.");
            }

            host = Value[..colon];
            portText = Value[(colon + 1)..];
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new FormatException($"'{this}' has an invalid port.");
        }

        return (host, port);
    }

    public override string ToString() => $"{Kind}:{Value}";
}
