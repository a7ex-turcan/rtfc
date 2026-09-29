using System.Collections.Concurrent;
using System.Threading.Channels;
using Rtfc.Core;

namespace Rtfc.Daemon;

/// <summary>
/// The sessions that can take a push (spec §7.3, <c>auto_session</c>). An <c>rtfc mcp</c> that names its Claude Code session when it
/// takes a lease gets a queue here for as long as the lease lasts, and the lease response carries the queue to it, one JSON line
/// per event. A session without a live lease is not open, and a push to it reports false.
/// </summary>
public sealed class SessionChannels : ISessionChannel
{
    private readonly ConcurrentDictionary<string, Channel<SessionEvent>> _sessions = new(StringComparer.Ordinal);

    /// <summary>Opens the queue for a session's lease. A newer lease for the same session replaces an older one.</summary>
    public ChannelReader<SessionEvent> Open(string sessionId)
    {
        var channel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
        if (_sessions.TryGetValue(sessionId, out var previous))
        {
            previous.Writer.TryComplete();
        }

        _sessions[sessionId] = channel;
        return channel.Reader;
    }

    /// <summary>Closes the queue when its lease ends, unless a newer lease has taken the session over.</summary>
    public void Close(string sessionId, ChannelReader<SessionEvent> reader)
    {
        if (_sessions.TryGetValue(sessionId, out var channel) && channel.Reader == reader)
        {
            _sessions.TryRemove(KeyValuePair.Create(sessionId, channel));
            channel.Writer.TryComplete();
        }
    }

    public bool TryPush(string sessionId, SessionEvent sessionEvent) =>
        _sessions.TryGetValue(sessionId, out var channel) && channel.Writer.TryWrite(sessionEvent);
}
