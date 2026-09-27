using System.Reflection;

namespace Rtfc;

/// <summary>
/// Dispatches to a mode of the single <c>rtfc</c> executable (spec §3.1). Each mode lands
/// with its phase; until then it says so and fails, rather than pretending to work.
/// </summary>
/// <remarks>
/// Everything is written to <paramref name="stderr"/> except version output: in
/// <c>rtfc mcp</c> stdout is the MCP protocol, and in <c>rtfc statusline</c> it is what
/// the user sees in their status bar.
/// </remarks>
internal static class EntryPoint
{
    internal const int Ok = 0;
    internal const int NotImplemented = 1;
    internal const int Usage = 2;

    private static readonly string[] Modes = ["daemon", "mcp", "statusline"];

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is ["--version" or "version"])
        {
            stdout.WriteLine(Version);
            return Ok;
        }

        if (args is [var mode, ..] && Modes.Contains(mode))
        {
            stderr.WriteLine($"rtfc {mode}: not implemented yet.");
            return NotImplemented;
        }

        stderr.WriteLine($"""
            rtfc {Version}
            usage: rtfc <mode> [args]

            modes:
              daemon       per-device daemon (rtfcd)
              mcp          stdio MCP server, launched by Claude Code
              statusline   print the status-bar segment
              --version    print the version
            """);
        return Usage;
    }

    private static string Version
    {
        get
        {
            var informational = typeof(EntryPoint).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";

            // The SDK appends "+<commit>" when built from a git checkout.
            return informational.Split('+')[0];
        }
    }
}
