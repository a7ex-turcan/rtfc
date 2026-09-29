using System.Text.Json;

namespace Rtfc.Core;

public static class GateState
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
}

/// <summary>A contact's message pushed into one Claude Code session, and what the user has said about it so far (spec §7.3).</summary>
public sealed record SessionGate(string Id, string From, string State);

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
