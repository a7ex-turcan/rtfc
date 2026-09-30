using System.Text.Json;

namespace Rtfc.Core;

public static class GateState
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
}

/// <summary>
/// What the hook tells the daemon about a pushed item: the user's answer, or that the turn ended with nothing to ask. For a source
/// item, <c>NoAction</c> is the user choosing "Nothing to do", <c>Later</c> is "Later", and <c>Dismissed</c> and <c>Kept</c> are
/// the answer to "Dismiss or Keep?" once an accepted action is done (spec §10.4).
/// </summary>
public static class GateOutcome
{
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string NothingToDo = "nothing_to_do";
    public const string NoAction = "no_action";
    public const string Later = "later";
    public const string Dismissed = "dismissed";
    public const string Kept = "kept";
}

/// <summary>
/// A contact's message or a source item pushed into one Claude Code session, and what the user has said about it so far (spec
/// §7.3, §10.4). <c>Asked</c> is set once Claude asked a source item's action question, so a turn that ends without asking is
/// known to have found nothing to do.
/// </summary>
public sealed record SessionGate(string Id, string From, string State, string Kind = "person", bool Asked = false);

/// <summary>
/// The accept gate's state, one small file per session under <c>gates/</c>. The plugin's hook reads it on every tool call of
/// every session, so it is a file and not a daemon call: no socket, no daemon needed, nothing lost when the daemon restarts.
/// The turn ending clears it, and so does the session starting again.
/// </summary>
public static class SessionGates
{
    // A gate file that exists but cannot be read still gates: fail closed.
    private static readonly SessionGate Unreadable = new("", "a contact", GateState.Pending);

    public static SessionGate? Read(RtfcHome home, string sessionId)
    {
        var path = PathFor(home, sessionId);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), CoreJson.Default.SessionGate) ?? Unreadable;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Unreadable;
        }
    }

    public static void Write(RtfcHome home, string sessionId, SessionGate gate)
    {
        if (PathFor(home, sessionId) is not { } path)
        {
            return;
        }

        Directory.CreateDirectory(home.GatesDirectory);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(gate, CoreJson.Default.SessionGate));
        File.Move(temp, path, overwrite: true);
    }

    public static void Clear(RtfcHome home, string sessionId)
    {
        if (PathFor(home, sessionId) is not { } path)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Session ids come from Claude Code and look like GUIDs; anything else is refused rather than used as a file name.</summary>
    public static string? PathFor(RtfcHome home, string sessionId) =>
        sessionId.Length is > 0 and <= 128 && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Path.Combine(home.GatesDirectory, sessionId + ".json")
            : null;
}
