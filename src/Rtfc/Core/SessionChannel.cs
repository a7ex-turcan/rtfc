namespace Rtfc.Core;

/// <summary>
/// Pushes a message into a running Claude Code session (spec §7.3, <c>auto_session</c>). The daemon implements it over the
/// session's lease; the session's <c>rtfc mcp</c> turns each event into a channel notification. False when that session holds
/// no lease, that is, it is not open. True means handed to the session, not seen by Claude: Claude Code drops channel events
/// silently when the session was not started with rtfc as a channel.
/// </summary>
public interface ISessionChannel
{
    bool TryPush(string sessionId, SessionEvent sessionEvent);
}

/// <summary>One message for a session: its inbox id, who sent it (the local handle), and the text Claude will see.</summary>
public sealed record SessionEvent(string Id, string From, string Content);
