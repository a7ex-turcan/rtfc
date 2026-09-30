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

/// <summary>
/// One push for a session: the inbox id, who it is from (a contact's local handle, or the source type for a source item), the
/// text Claude will see, and the kind (<c>person</c> or <c>source</c>), which the plugin's hook uses to pick the gate's shape.
/// </summary>
public sealed record SessionEvent(string Id, string From, string Content, string Kind = "person");
