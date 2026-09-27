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
        if (await client.TryStatusAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return Outcome.AlreadyRunning;
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

    /// <summary>
    /// Starts <c>rtfc daemon run</c> with every stdio handle redirected and then closed.
    /// Inheriting them would keep the MCP server's stdout pipe open after it exits, and
    /// Claude Code waits on that pipe. The child moves itself to a new session (<see cref="Detach"/>).
    /// </summary>
    private static void Spawn(RtfcHome home)
    {
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

    // A blittable, argument-free signature: DllImport needs no marshalling stub, so this stays AOT-clean without unsafe code.
    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();
}
