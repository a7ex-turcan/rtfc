using System.Reflection;
using Rtfc.Cli;
using Rtfc.Mcp;

namespace Rtfc;

/// <summary>
/// Dispatches to a mode of the single <c>rtfc</c> executable (spec §3.1), by hand, as
/// rtfm and rtfq do.
/// </summary>
/// <remarks>
/// Only results are written to <paramref name="stdout"/>: in <c>rtfc mcp</c> it is the MCP
/// protocol, and in <c>rtfc statusline</c> it is what the user sees in their status bar.
/// Diagnostics go to <paramref name="stderr"/>.
/// </remarks>
public static class EntryPoint
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, RtfcHome? home = null, TextReader? stdin = null) =>
        RunAsync(args, stdout, stderr, home, stdin).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, RtfcHome? home = null, TextReader? stdin = null)
    {
        if (args is ["--version" or "version"])
        {
            stdout.WriteLine(Version);
            return Ok;
        }

        if (args is [] or ["--help" or "-h" or "help"])
        {
            stderr.WriteLine(UsageText);
            return Usage;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var ctx = new CommandContext(home ?? RtfcHome.Resolve(), stdout, stderr, cancellation.Token);
        var rest = args[1..];
        try
        {
            switch (args[0])
            {
                case "init":
                    return Commands.Init(ctx, rest);
                case "invite":
                    return await Commands.InviteAsync(ctx).ConfigureAwait(false);
                case "accept":
                    return await Commands.AcceptAsync(ctx, rest).ConfigureAwait(false);
                case "auto":
                    return await Commands.AutoAsync(ctx, rest).ConfigureAwait(false);
                case "remove":
                    return await Commands.RemoveAsync(ctx, rest, block: false).ConfigureAwait(false);
                case "block":
                    return await Commands.RemoveAsync(ctx, rest, block: true).ConfigureAwait(false);
                case "away":
                    return await Commands.AwayAsync(ctx, rest).ConfigureAwait(false);
                case "rename":
                    return await Commands.RenameAsync(ctx, rest).ConfigureAwait(false);
                case "receipts":
                    return await Commands.ReceiptsAsync(ctx, rest).ConfigureAwait(false);
                case "outbox":
                    return await Commands.OutboxAsync(ctx).ConfigureAwait(false);
                case "contacts":
                    return await Commands.ContactsAsync(ctx).ConfigureAwait(false);
                case "inbox":
                    return await Commands.InboxAsync(ctx, rest).ConfigureAwait(false);
                case "daemon":
                    return await Commands.DaemonAsync(ctx, rest).ConfigureAwait(false);
                case "hook":
                    return await Commands.HookAsync(ctx, rest, stdin ?? Console.In).ConfigureAwait(false);
                case "statusline":
                    return Commands.Statusline(ctx, stdin ?? Console.In, Console.IsInputRedirected);
                case "mcp":
                    await new McpServer(ctx.Home, stdin ?? Console.In, stdout, stderr).RunAsync(ctx.CancellationToken).ConfigureAwait(false);
                    return Ok;
                default:
                    stderr.WriteLine($"rtfc: unknown command '{args[0]}'.");
                    stderr.WriteLine(UsageText);
                    return Usage;
            }
        }
        catch (CommandLineException ex)
        {
            stderr.WriteLine($"rtfc {args[0]}: {ex.Message}");
            return Usage;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return Failure;
        }
        catch (Exception ex) when (ex is Daemon.DaemonException or IOException or InvalidOperationException)
        {
            stderr.WriteLine($"rtfc {args[0]}: {ex.Message}");
            return Failure;
        }
    }

    private static string UsageText => $"""
        rtfc {Version}
        usage: rtfc <command> [args]

        set-up (run these yourself, they are never tools):
          init [--handle h] [--device d] [--port p] [--hint-host host]...
          invite                      print a single-use token to send to someone
          accept <token>              accept someone's invite (they must be home)
          auto <contact>|--all off|headless [--scope <dir>]
                                      let a read-only headless Claude answer them from one directory
                                      (default: where Claude runs; --all: every contact you have now)
          remove <contact>            stop talking to someone; block <contact> also refuses future invites
          rename <contact> <handle>   what you call them; only you see it
          receipts <contact> on|off   tell them when you read their messages (default on)
          away on|off                 stop listening; contacts see nobody home, you can still send

        look:
          contacts                    contacts and whether they are home
          inbox [--all] | inbox open <id> | inbox dismiss <id>
          outbox                      what waits to be delivered

        plumbing:
          daemon run [--stay] | ensure | status | stop
          mcp                         stdio MCP server, launched by Claude Code
          statusline                  the status-bar segment
          hook                        the plugin's hook, fed Claude Code's event JSON: the accept gate for a contact's message
          --version
        """;

    public static string Version
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
