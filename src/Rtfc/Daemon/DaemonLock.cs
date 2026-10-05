using System.Globalization;
using System.Text;

namespace Rtfc.Daemon;

/// <summary>
/// One daemon per home (spec §3.1): an exclusive lock on <c>rtfcd.lock</c>, taken before the daemon touches its socket and held until
/// it has removed it again. The operating system releases it when the process ends, crash or not. Only the holder deletes or creates
/// the socket, so a second daemon, from a login item, a session's start or an upgrade, can never take the socket from a running one,
/// at start or on the way out. The TCP port cannot be the lock: an away daemon does not listen, and tests use port 0.
/// </summary>
public sealed class DaemonLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly RtfcHome _home;

    private DaemonLock(FileStream stream, RtfcHome home)
    {
        _stream = stream;
        _home = home;
    }

    /// <summary>The lock, or null while another daemon holds it.</summary>
    public static DaemonLock? TryAcquire(RtfcHome home)
    {
        FileStream stream;
        try
        {
            // FileShare.None is an exclusive share mode on Windows and an exclusive flock on Unix.
            stream = new FileStream(home.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            // The pid for a person reading the home; nothing decides anything on it.
            File.WriteAllText(home.PidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new DaemonLock(stream, home);
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the lock: a daemon that is leaving releases it in a moment. Gives up at once when another
    /// daemon answers, since that one is staying. Null when this process is not to be the daemon.
    /// </summary>
    public static async Task<DaemonLock?> AcquireAsync(RtfcHome home, TimeSpan wait, Func<CancellationToken, Task<bool>> anotherAnswers, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            if (TryAcquire(home) is { } held)
            {
                return held;
            }

            if (DateTimeOffset.UtcNow >= deadline || await anotherAnswers(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_home.PidPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _stream.Dispose();
    }
}
