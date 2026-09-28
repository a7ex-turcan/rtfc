using System.Diagnostics;

namespace Rtfc.Tests;

/// <summary>
/// The real <c>rtfc</c> executable from the test output, started the way Claude Code starts it
/// for a hook, the MCP server or the status line: every stdio handle a pipe and no window. On
/// Windows that gives it a console of its own in the system's code page, not the test's.
/// </summary>
internal static class RtfcProcess
{
    public static Process Start(RtfcHome home, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "rtfc.exe" : "rtfc"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment[RtfcHome.EnvironmentVariable] = home.Root;
        return Process.Start(start) ?? throw new InvalidOperationException("rtfc did not start.");
    }
}
