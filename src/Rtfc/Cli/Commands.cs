using System.Globalization;
using Rtfc.Core;
using Rtfc.Daemon;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Storage;

namespace Rtfc.Cli;

/// <summary>Everything a command needs to talk to the user. Results go to <c>Out</c>, problems to <c>Error</c>.</summary>
public sealed record CommandContext(RtfcHome Home, TextWriter Out, TextWriter Error, CancellationToken CancellationToken);

/// <summary>
/// The management CLI (spec §9.3). These change who can reach you and are deliberately
/// not MCP tools: a slash command runs them only when the user types it.
/// </summary>
public static class Commands
{
    // ---- init ----

    public static int Init(CommandContext ctx, IReadOnlyList<string> args)
    {
        var line = new CommandLine(args);
        if (IdentityStore.Exists(ctx.Home))
        {
            ctx.Error.WriteLine($"rtfc init: an identity already exists in {ctx.Home.KeysDirectory}.");
            return 1;
        }

        var handle = InviteToken.SanitizeHandle(line.Value("handle") ?? Environment.UserName, "p_00000000");
        var device = InviteToken.SanitizeHandle(line.Value("device") ?? Environment.MachineName.ToLowerInvariant(), "d_00000000");
        var config = new RtfcConfig(line.IntValue("port") ?? TcpTransport.DefaultPort, line.Values("hint-host") is { Count: > 0 } hosts ? [.. hosts] : null);

        ctx.Home.EnsureCreated();
        ConfigFile.Save(ctx.Home, config);
        using var self = IdentityStore.Create(ctx.Home, handle, device, DateTimeOffset.UtcNow);
        using (var db = Database.Open(ctx.Home.DatabasePath))
        {
            db.SaveSelf(new SelfRow(self.PersonId, self.Handle, self.PersonCa.RawData, self.DeviceId, self.DeviceName, self.DeviceCertificate.RawData, 1));
        }

        var hints = HintHosts.Resolve(config).Select(h => EndpointHint.ForTcp(h, config.Port).ToString());
        ctx.Out.WriteLine($"Created an identity for {self.Handle} on {self.DeviceName}.");
        ctx.Out.WriteLine($"  person       {self.PersonId}");
        ctx.Out.WriteLine($"  fingerprint  {Ids.Fingerprint(self.PersonId)}");
        ctx.Out.WriteLine($"  device       {self.DeviceId}");
        ctx.Out.WriteLine($"  keys         {ctx.Home.KeysDirectory}");
        ctx.Out.WriteLine($"  hints        {string.Join(", ", hints)}");
        ctx.Out.WriteLine();
        ctx.Out.WriteLine("Next: /rtfc:invite to invite someone, or /rtfc:accept <token> to accept an invite.");
        return 0;
    }

    // ---- invite / accept ----

    public static async Task<int> InviteAsync(CommandContext ctx)
    {
        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var invite = await client.InviteAsync(ctx.CancellationToken).ConfigureAwait(false);
        ctx.Out.WriteLine("Invite token, single use, valid for 24 hours:");
        ctx.Out.WriteLine();
        ctx.Out.WriteLine(invite.Token);
        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"Reachable at {string.Join(", ", invite.Hints)}.");
        ctx.Out.WriteLine("Send it over any chat. Keep Claude Code open until they accept: accepting needs you to be home.");
        return 0;
    }

    public static async Task<int> AcceptAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        var token = string.Join("", args).Trim();
        if (token.Length == 0)
        {
            ctx.Error.WriteLine("usage: rtfc accept <token>");
            return 2;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var result = await client.AcceptAsync(token, ctx.CancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case AcceptStatus.Accepted:
                ctx.Out.WriteLine($"You and {result.Handle} are now contacts. Messages from them will park in your inbox.");
                ctx.Out.WriteLine($"  fingerprint  {result.Fingerprint}   (compare in person if you want to be sure)");
                return 0;
            case AcceptStatus.NobodyHome:
                ctx.Out.WriteLine("Nobody's home: the inviter has no device online. The token stays valid; try again when they have Claude Code open.");
                return 1;
            case AcceptStatus.AlreadyContact:
                ctx.Out.WriteLine($"{result.Handle} is already a contact.");
                return 0;
            default:
                ctx.Error.WriteLine($"rtfc accept: {result.Status}: {result.Reason}");
                return 1;
        }
    }

    // ---- contacts / inbox (read-only conveniences; the MCP tools are the real surface) ----

    public static async Task<int> ContactsAsync(CommandContext ctx)
    {
        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var contacts = await client.ContactsAsync(probe: true, ctx.CancellationToken).ConfigureAwait(false);
        if (contacts.Length == 0)
        {
            ctx.Out.WriteLine("No contacts yet. /rtfc:invite to invite someone, or /rtfc:accept <token>.");
            return 0;
        }

        foreach (var contact in contacts)
        {
            var devices = string.Join(", ", contact.Devices.Select(d => $"{d.Name} {(d.Online switch { true => "home", false => "away", null => "?" })}"));
            ctx.Out.WriteLine($"{contact.Handle,-16} {contact.Status,-8} {contact.InboundMode,-14} {devices}");
        }

        return 0;
    }

    public static async Task<int> InboxAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        var line = new CommandLine(args, "all");
        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        if (line.Positionals is ["open", var id])
        {
            var opened = await client.OpenAsync(id, ctx.CancellationToken).ConfigureAwait(false);
            if (opened is null)
            {
                ctx.Error.WriteLine($"rtfc inbox: no message '{id}'.");
                return 1;
            }

            ctx.Out.WriteLine($"From {opened.From}/{opened.FromDevice} at {Timestamps.Format(opened.ReceivedAt)} ({opened.State}):");
            ctx.Out.WriteLine(opened.Body);
            return 0;
        }

        var messages = await client.InboxAsync(line.Flag("all") ? "all" : "parked", ctx.CancellationToken).ConfigureAwait(false);
        if (messages.Length == 0)
        {
            ctx.Out.WriteLine(line.Flag("all") ? "The inbox is empty." : "Nothing parked.");
            return 0;
        }

        foreach (var m in messages)
        {
            ctx.Out.WriteLine($"{m.Id}  {m.State,-8} {m.From}/{m.FromDevice}: {m.Preview}");
        }

        return 0;
    }

    // ---- daemon ----

    public static async Task<int> DaemonAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        var line = new CommandLine(args, "stay");
        switch (line.Positionals.FirstOrDefault())
        {
            case "run":
                DaemonLauncher.Detach();
                return await DaemonHost.RunAsync(
                    ctx.Home,
                    new DaemonOptions(IdleExit: !line.Flag("stay"), LogToConsole: !Console.IsErrorRedirected),
                    ctx.CancellationToken).ConfigureAwait(false);

            case "ensure":
                switch (await DaemonLauncher.EnsureAsync(ctx.Home, ctx.CancellationToken).ConfigureAwait(false))
                {
                    case DaemonLauncher.Outcome.AlreadyRunning:
                    case DaemonLauncher.Outcome.Started:
                        return 0;
                    case DaemonLauncher.Outcome.NoIdentity:
                        // Quiet by design: this runs from the SessionStart hook on machines that may never use rtfc.
                        return 0;
                    default:
                        ctx.Error.WriteLine($"rtfc daemon ensure: the daemon did not start. See {ctx.Home.LogPath}.");
                        return 1;
                }

            case "status":
                using (var client = new DaemonClient(ctx.Home))
                {
                    var status = await client.TryStatusAsync(ctx.CancellationToken).ConfigureAwait(false);
                    if (status is null)
                    {
                        ctx.Out.WriteLine("rtfcd is not running.");
                        return 1;
                    }

                    ctx.Out.WriteLine($"rtfcd {status.Version} pid {status.Pid}: {status.Handle}/{status.DeviceName}, port {status.Port}, {status.Leases} lease(s){(status.IdleExit ? "" : ", staying")}");
                    ctx.Out.WriteLine($"  hints  {string.Join(", ", status.Hints)}");
                    ctx.Out.WriteLine($"  socket {ctx.Home.SocketPath}");
                    return 0;
                }

            case "stop":
                using (var client = new DaemonClient(ctx.Home))
                {
                    if (await client.TryStatusAsync(ctx.CancellationToken).ConfigureAwait(false) is null)
                    {
                        ctx.Out.WriteLine("rtfcd is not running.");
                        return 0;
                    }

                    await client.ShutdownAsync(ctx.CancellationToken).ConfigureAwait(false);
                    ctx.Out.WriteLine("Stopping rtfcd.");
                    return 0;
                }

            default:
                ctx.Error.WriteLine("usage: rtfc daemon run [--stay] | ensure | status | stop");
                return 2;
        }
    }

    // ---- statusline (spec §11) ----

    public static int Statusline(CommandContext ctx, TextReader stdin, bool stdinRedirected)
    {
        if (stdinRedirected)
        {
            // Claude Code passes session JSON (cwd and more). Phase 8 uses it to pick the project.
            _ = stdin.ReadToEnd();
        }

        var status = StatusFile.Read(ctx.Home.StatusPath);
        if (status is null || status.Global.Parked == 0)
        {
            return 0;
        }

        var from = status.Global.From.Length == 0 ? "" : " · " + string.Join(", ", status.Global.From);
        ctx.Out.WriteLine($"📨 {status.Global.Parked.ToString(CultureInfo.InvariantCulture)}{from}");
        return 0;
    }

    // ---- helpers ----

    /// <summary>A client to a running daemon, starting one if needed. Null, with the reason on stderr, when that fails.</summary>
    private static async Task<DaemonClient?> ConnectAsync(CommandContext ctx)
    {
        var outcome = await DaemonLauncher.EnsureAsync(ctx.Home, ctx.CancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case DaemonLauncher.Outcome.AlreadyRunning:
            case DaemonLauncher.Outcome.Started:
                return new DaemonClient(ctx.Home);
            case DaemonLauncher.Outcome.NoIdentity:
                ctx.Error.WriteLine("rtfc: no identity yet. Run /rtfc:init first.");
                return null;
            default:
                ctx.Error.WriteLine($"rtfc: the daemon did not start. See {ctx.Home.LogPath}.");
                return null;
        }
    }
}
