using System.Diagnostics;
using System.Runtime.InteropServices;
using Rtfc.Identity;

namespace Rtfc.Daemon;

/// <summary>
/// <c>rtfc daemon ensure</c> (spec §3.1): answer on the socket, or start a detached daemon
/// and wait for it to answer. Run from the plugin's SessionStart hook and by <c>rtfc mcp</c>.
/// </summary>
public static class DaemonLauncher
{
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);

    public enum Outcome
    {
        AlreadyRunning,
        Started,
        NoIdentity,
        Failed,
    }

    public static async Task<Outcome> EnsureAsync(RtfcHome home, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(home);
        if (await client.TryStatusAsync(cancellationToken).ConfigureAwait(false) is { } running)
        {
            // A daemon that runs for days outlives an upgrade; the first newer rtfc that finds it replaces it. Never a downgrade.
            if (!IsOlder(running.Version, EntryPoint.Version) || !await StopAsync(client, cancellationToken).ConfigureAwait(false))
            {
                return Outcome.AlreadyRunning;
            }
        }

        if (!IdentityStore.Exists(home))
        {
            return Outcome.NoIdentity;
        }

        Spawn(home);

        var deadline = DateTimeOffset.UtcNow + StartupWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (await client.TryStatusAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return Outcome.Started;
            }
        }

        return Outcome.Failed;
    }

    /// <summary>Whether <paramref name="running"/> is an older release than <paramref name="mine"/>. Build suffixes are ignored; anything unreadable is not older.</summary>
    public static bool IsOlder(string running, string mine) =>
        Version.TryParse(Core(running), out var r) && Version.TryParse(Core(mine), out var m) && r < m;

    private static string Core(string version) => version.Split('-', '+')[0];

    /// <summary>Asks the daemon to stop and waits until it no longer answers. False if it is still there after the wait.</summary>
    public static async Task<bool> StopAsync(DaemonClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or DaemonException)
        {
        }

        var deadline = DateTimeOffset.UtcNow + StartupWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (await client.TryStatusAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts <c>rtfc daemon run</c> with every stdio handle redirected and then closed.
    /// Inheriting them would keep the MCP server's stdout pipe open after it exits, and
    /// Claude Code waits on that pipe. The child moves itself to a new session (<see cref="Detach"/>).
    /// </summary>
    private static void Spawn(RtfcHome home)
    {
        if (OperatingSystem.IsWindows())
        {
            KeepStdioFromChildren();
        }

        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the rtfc executable.");
        var start = new ProcessStartInfo(self)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = home.Root,
        };
        start.ArgumentList.Add("daemon");
        start.ArgumentList.Add("run");
        start.Environment[RtfcHome.EnvironmentVariable] = home.Root;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The daemon process did not start.");
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }

    /// <summary>Called by <c>daemon run</c>: leave the parent's session and process group so a terminal or a parent exiting does not take the daemon with it.</summary>
    public static void Detach()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // CreateNoWindow is as far as we go for now; see AGENTS.md.
        }

        try
        {
            _ = setsid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Redirecting the child's stdio is not enough on Windows: .NET creates every child with
    /// handle inheritance on, so the daemon would also inherit this process's own stdio, which
    /// are the pipes Claude Code gave the SessionStart hook or the MCP server. Held for the
    /// daemon's lifetime, the hook's pipe never closes and the session waits on it. Unix needs
    /// none of this: there the child's 0, 1 and 2 are replaced and every other descriptor is
    /// close-on-exec.
    /// </summary>
    private static void KeepStdioFromChildren()
    {
        foreach (var std in (ReadOnlySpan<int>)[StdInputHandle, StdOutputHandle, StdErrorHandle])
        {
            var handle = GetStdHandle(std);
            if (handle != 0 && handle != -1)
            {
                _ = SetHandleInformation(handle, HandleFlagInherit, 0);
            }
        }
    }

    // A blittable, argument-free signature: DllImport needs no marshalling stub, so this stays AOT-clean without unsafe code.
    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 1;

    // Blittable too: BOOL comes back as int, handles as nint.
    [DllImport("kernel32")]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32")]
    private static extern int SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);
}
