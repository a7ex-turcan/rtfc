using System.Globalization;
using System.Text.Json;
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

    // ---- auto / remove / block (spec §7.3, §5.2) ----

    /// <summary>
    /// <c>rtfc auto &lt;contact&gt;|--all off|headless|session [--scope &lt;dir&gt;]</c> (spec §7.3). <c>--all</c> covers every active contact
    /// now; contacts accepted later still park (spec §5.1). Without <c>--scope</c>, headless reads the directory Claude runs in.
    /// </summary>
    public static async Task<int> AutoAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        var line = new CommandLine(args, "all");
        var all = line.Flag("all");
        (string? handle, string mode) target = (all, line.Positionals) switch
        {
            (true, [var m]) => (null, m),
            (false, [var h, var m]) => (h, m),
            _ => (null, ""),
        };
        if (target.mode.Length == 0)
        {
            ctx.Error.WriteLine("usage: rtfc auto <contact>|--all off|headless|session [--scope <dir>]");
            return 2;
        }

        var scope = line.Value("scope") is { } given ? ProjectPaths.Full(given) : null;
        var defaulted = false;
        if (target.mode == "headless" && scope is null)
        {
            scope = AutoScope.Default(
                Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"), Environment.CurrentDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out var refusal);
            if (scope is null)
            {
                ctx.Error.WriteLine($"rtfc auto: {refusal}");
                return 1;
            }

            defaulted = true;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        string[] handles = target.handle is { } one
            ? [one]
            : [.. (await client.ContactsAsync(probe: false, ctx.CancellationToken).ConfigureAwait(false)).Where(c => c.Status == ContactStatus.Active).Select(c => c.Handle)];
        if (handles.Length == 0)
        {
            ctx.Out.WriteLine("No active contacts yet, so nothing changed.");
            return 0;
        }

        var changed = new List<string>();
        foreach (var handle in handles)
        {
            var result = await client.SetAutoAsync(handle, target.mode, scope, ctx.CancellationToken).ConfigureAwait(false);
            if (result.Status != ManagementStatus.Ok)
            {
                ctx.Error.WriteLine($"rtfc auto: {result.Reason}");
                continue;
            }

            changed.Add(result.Handle!);
        }

        if (changed.Count == 0)
        {
            return 1;
        }

        var who = string.Join(", ", changed);
        var undo = all ? "rtfc auto --all off" : $"rtfc auto {changed[0]} off";
        ctx.Out.WriteLine(target.mode == "off"
            ? $"Messages from {who} park until you look at them."
            : $"Messages from {who} are now answered by a headless, read-only Claude that can see {scope}"
              + (defaulted ? " (the directory Claude is running in; pass --scope to choose another)" : "")
              + $" and nothing else. It never writes, never runs commands, and its answers arrive marked as automatic. Turn it off with: {undo}");
        if (all && target.mode != "off")
        {
            ctx.Out.WriteLine("Contacts you accept later still park until you turn this on for them.");
        }

        return changed.Count == handles.Length ? 0 : 1;
    }

    public static async Task<int> RemoveAsync(CommandContext ctx, IReadOnlyList<string> args, bool block)
    {
        var verb = block ? "block" : "remove";
        if (args is not [var handle])
        {
            ctx.Error.WriteLine($"usage: rtfc {verb} <contact>");
            return 2;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var result = block
            ? await client.BlockAsync(handle, ctx.CancellationToken).ConfigureAwait(false)
            : await client.RemoveAsync(handle, ctx.CancellationToken).ConfigureAwait(false);
        if (result.Status != ManagementStatus.Ok)
        {
            ctx.Error.WriteLine($"rtfc {verb}: {result.Reason}");
            return 1;
        }

        ctx.Out.WriteLine(block
            ? $"{result.Handle} is blocked: their devices are refused, and invites to or from them are refused too."
            : $"{result.Handle} is removed: their devices are refused from now on. A new invite, either way, makes you contacts again.");
        return 0;
    }

    // ---- away / rename / receipts (spec §9.4) ----

    public static async Task<int> AwayAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        if (args is not [("on" or "off") and var state])
        {
            ctx.Error.WriteLine("usage: rtfc away on|off");
            return 2;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        await client.AwayAsync(state == "on", ctx.CancellationToken).ConfigureAwait(false);
        ctx.Out.WriteLine(state == "on"
            ? "Away: nothing listens, so contacts see nobody home. You can still send, and your outbox still delivers."
            : "Back: listening again.");
        return 0;
    }

    public static async Task<int> RenameAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        if (args is not [var handle, var newHandle])
        {
            ctx.Error.WriteLine("usage: rtfc rename <contact> <new-handle>");
            return 2;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var result = await client.RenameAsync(handle, newHandle, ctx.CancellationToken).ConfigureAwait(false);
        if (result.Status != ManagementStatus.Ok)
        {
            ctx.Error.WriteLine($"rtfc rename: {result.Reason}");
            return 1;
        }

        ctx.Out.WriteLine($"{handle} is now {result.Handle} to you. Only you see this name.");
        return 0;
    }

    public static async Task<int> ReceiptsAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        if (args is not [var handle, ("on" or "off") and var state])
        {
            ctx.Error.WriteLine("usage: rtfc receipts <contact> on|off");
            return 2;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var result = await client.ReceiptsAsync(handle, state == "on", ctx.CancellationToken).ConfigureAwait(false);
        if (result.Status != ManagementStatus.Ok)
        {
            ctx.Error.WriteLine($"rtfc receipts: {result.Reason}");
            return 1;
        }

        ctx.Out.WriteLine(state == "on"
            ? $"{result.Handle} is told when you read their messages."
            : $"{result.Handle} is not told when you read their messages.");
        return 0;
    }

    public static async Task<int> OutboxAsync(CommandContext ctx)
    {
        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var entries = await client.OutboxAsync(ctx.CancellationToken).ConfigureAwait(false);
        if (entries.Length == 0)
        {
            ctx.Out.WriteLine("The outbox is empty.");
            return 0;
        }

        foreach (var e in entries)
        {
            ctx.Out.WriteLine($"{e.Id}  {e.Kind,-8} to {e.To,-16} tried {e.Attempts,3}x, until {Timestamps.Format(e.ExpiresAt)}: {e.Preview}");
        }

        return 0;
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
            var mode = contact.AutoScope is null ? contact.InboundMode : $"{contact.InboundMode} ({contact.AutoScope})";
            var extras = (contact.ReadReceipts ? "" : "  no receipts") + (contact.Pending > 0 ? $"  {contact.Pending} in outbox" : "");
            ctx.Out.WriteLine($"{contact.Handle,-16} {contact.Status,-8} {mode,-14} {devices}{extras}");
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

            var from = opened.Kind == "notice" ? "rtfc" : $"{opened.From}/{opened.FromDevice}";
            ctx.Out.WriteLine($"From {from} at {Timestamps.Format(opened.ReceivedAt)} ({opened.State}):");
            ctx.Out.WriteLine(opened.Body);
            if (opened.Note is not null)
            {
                ctx.Out.WriteLine($"Note: {opened.Note}");
            }

            foreach (var reply in opened.YourReplies ?? [])
            {
                ctx.Out.WriteLine($"Your reply {reply.Id}: {reply.State}{(reply.ReadAt is null ? "" : $", read {Timestamps.Format(reply.ReadAt.Value)}")}");
            }

            return 0;
        }

        if (line.Positionals is ["dismiss", var dismissId])
        {
            if (!await client.DismissAsync(dismissId, ctx.CancellationToken).ConfigureAwait(false))
            {
                ctx.Error.WriteLine($"rtfc inbox: no message '{dismissId}'.");
                return 1;
            }

            ctx.Out.WriteLine("Dismissed.");
            return 0;
        }

        // The CLI is not in a session, so it lists every project's messages and says which project each is in.
        var messages = (await client.InboxAsync(line.Flag("all") ? "all" : "parked", ctx.CancellationToken).ConfigureAwait(false)).Messages;
        if (messages.Length == 0)
        {
            ctx.Out.WriteLine(line.Flag("all") ? "The inbox is empty." : "Nothing parked.");
            return 0;
        }

        foreach (var m in messages)
        {
            var from = (m.Kind == "notice" ? "rtfc" : $"{m.From}/{m.FromDevice}") + (m.Project is null ? "" : $" → {m.Project}");
            var tail = (m.Note is null ? "" : $"  [{m.Note}]") + (m.ReplyState is null ? "" : $"  (your reply: {m.ReplyState})");
            ctx.Out.WriteLine($"{m.Id}  {m.State,-11} {from}: {m.Preview}{tail}");
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

    /// <summary>
    /// The status line segment (spec §11). The shared inbox counts together with the session's own project; another project's
    /// messages show as a pointer, <c>📨 1 · alex → payments-api</c>. Reads only <c>status.json</c>: it runs every few seconds.
    /// </summary>
    public static int Statusline(CommandContext ctx, TextReader stdin, bool stdinRedirected)
    {
        // Claude Code passes session JSON on stdin, with the directory the session runs in.
        var directory = stdinRedirected ? SessionDirectory(stdin.ReadToEnd()) : null;

        var status = StatusFile.Read(ctx.Home.StatusPath);
        if (status is null)
        {
            return 0;
        }

        var projects = status.Projects ?? [];
        var here = directory is null ? null : projects.Keys.Where(root => ProjectPaths.Contains(root, directory)).MaxBy(root => root.Length);

        var segments = new List<string>();
        if (status.Away)
        {
            segments.Add("💤 away");
        }

        var parked = status.Global.Parked + (here is null ? 0 : projects[here].Parked);
        if (parked > 0)
        {
            segments.Add($"📨 {parked.ToString(CultureInfo.InvariantCulture)}{Senders([.. status.Global.From, .. here is null ? [] : projects[here].From ?? []])}");
        }

        foreach (var (_, project) in projects.Where(p => p.Key != here && p.Value.Parked > 0).OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            segments.Add($"📨 {project.Parked.ToString(CultureInfo.InvariantCulture)}{Senders(project.From ?? [])} → {project.Name}");
        }

        if (status.Global.Pending > 0)
        {
            segments.Add($"📤 {status.Global.Pending.ToString(CultureInfo.InvariantCulture)}");
        }

        if (segments.Count > 0)
        {
            ctx.Out.WriteLine(string.Join("  ", segments));
        }

        return 0;
    }

    /// <summary>The key (<see cref="ProjectPaths.Key"/>) of the session's directory in Claude Code's status line JSON: <c>workspace.project_dir</c>, else <c>cwd</c>.</summary>
    private static string? SessionDirectory(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? directory = null;
            if (root.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.Object
                && workspace.TryGetProperty("project_dir", out var projectDir) && projectDir.ValueKind == JsonValueKind.String)
            {
                directory = projectDir.GetString();
            }

            if (string.IsNullOrEmpty(directory) && root.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
            {
                directory = cwd.GetString();
            }

            return string.IsNullOrEmpty(directory) ? null : ProjectPaths.Key(directory);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Senders(IEnumerable<string> from) =>
        from.Distinct().ToArray() is { Length: > 0 } names ? " · " + string.Join(", ", names) : "";

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
