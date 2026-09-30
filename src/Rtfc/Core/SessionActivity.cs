namespace Rtfc.Core;

/// <summary>
/// Whether a Claude Code session is in the middle of a turn (spec §7.3, §10.4). The plugin's hook marks a session busy when a
/// prompt is submitted, a channel event included, and idle when the turn ends or fails, when the session starts or ends, and when
/// Claude Code reports it idle, which is what covers a turn the user interrupted. The daemon holds a push for a busy session and
/// sends it once the session is idle, so a contact's message or a source item never lands in the middle of other work. One small
/// file per session, like the gate, so the hook needs no daemon; a mark older than <see cref="Stale"/> is ignored, so a session
/// killed mid-turn cannot hold pushes forever.
/// </summary>
public static class SessionActivity
{
    public static readonly TimeSpan Stale = TimeSpan.FromHours(3);

    public static void MarkBusy(RtfcHome home, string sessionId)
    {
        if (PathFor(home, sessionId) is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(home.ActivityDirectory);
            File.WriteAllText(path, Timestamps.Format(DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing mark only means a push may land mid-turn, as before; the gate still holds.
        }
    }

    public static void MarkIdle(RtfcHome home, string sessionId)
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

    public static bool IsBusy(RtfcHome home, string sessionId, DateTimeOffset now)
    {
        if (PathFor(home, sessionId) is not { } path)
        {
            return false;
        }

        try
        {
            return File.Exists(path) && now - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) < Stale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes marks left by sessions that ended without saying so. Returns how many went.</summary>
    public static int Prune(RtfcHome home, DateTimeOffset now)
    {
        if (!Directory.Exists(home.ActivityDirectory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(home.ActivityDirectory))
        {
            try
            {
                if (now - new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) >= Stale)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>The same rule as the gate: a session id is used as a file name only if it looks like one.</summary>
    private static string? PathFor(RtfcHome home, string sessionId) =>
        sessionId.Length is > 0 and <= 128 && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Path.Combine(home.ActivityDirectory, sessionId)
            : null;
}
