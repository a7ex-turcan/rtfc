using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        // Session mode answers in the Claude Code session this runs in (spec §7.3): /rtfc:auto's shell command sees its id.
        string? session = null;
        if (target.mode == "session")
        {
            session = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
            if (string.IsNullOrEmpty(session))
            {
                ctx.Error.WriteLine("rtfc auto: session mode answers in the Claude Code session you set it from. Run /rtfc:auto <contact> session inside that session.");
                return 1;
            }
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
            var result = await client.SetAutoAsync(handle, target.mode, scope, session, ctx.CancellationToken).ConfigureAwait(false);
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
        ctx.Out.WriteLine(target.mode switch
        {
            "off" => $"Messages from {who} park until you look at them.",
            "session" => $"Messages from {who} now come into this Claude Code session as they arrive: Claude gives you the gist and asks Accept or "
                + "Decline, and nothing runs until you accept. The session receives them only while it is open and was started with "
                + "--dangerously-load-development-channels plugin:rtfc@rtfc (rtfc is the archive's marketplace; another name changes the part after @); "
                + $"otherwise they wait in /rtfc:inbox. Turn it off with: {undo}",
            _ => $"Messages from {who} are now answered by a headless, read-only Claude that can see {scope}"
                + (defaulted ? " (the directory Claude is running in; pass --scope to choose another)" : "")
                + $" and nothing else. It never writes, never runs commands, and its answers arrive marked as automatic. Turn it off with: {undo}",
        });
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
            // The hints are what a send will try, and the first thing to look at when someone seems unreachable.
            var devices = string.Join(", ", contact.Devices.Select(d =>
                $"{d.Name} {(d.Online switch { true => "home", false => "away", null => "?" })} ({string.Join(" ", d.Hints)})"));
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

            var from = opened.Kind switch
            {
                "notice" => "rtfc",
                "source" => $"{opened.From} {opened.Entity}",
                _ => $"{opened.From}/{opened.FromDevice}",
            };
            ctx.Out.WriteLine($"From {from} at {Timestamps.Format(opened.ReceivedAt)} ({opened.State}):");
            if (opened.Kind == "source")
            {
                ctx.Out.WriteLine(opened.Title);
                if (opened.Url is { Length: > 0 })
                {
                    ctx.Out.WriteLine(opened.Url);
                }

                foreach (var e in opened.Events ?? [])
                {
                    ctx.Out.WriteLine($"  {Timestamps.Format(e.At)}  {e.Type,-16} {e.Actor}: {e.Summary}");
                }
            }
            else
            {
                ctx.Out.WriteLine(opened.Body);
            }
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
            var from = m.Kind switch
            {
                "notice" => "rtfc",
                "source" => $"{m.From} {m.Entity}",
                _ => $"{m.From}/{m.FromDevice}",
            } + (m.Project is null ? "" : $" → {m.Project}");
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

        // This project's source items (spec §11): reviews and tickets, and subscriptions that wait for approval.
        if (here is not null)
        {
            if (projects[here].Reviews > 0)
            {
                segments.Add($"🔀 {projects[here].Reviews.ToString(CultureInfo.InvariantCulture)}");
            }

            if (projects[here].Tickets > 0)
            {
                segments.Add($"🎫 {projects[here].Tickets.ToString(CultureInfo.InvariantCulture)}");
            }

            if (projects[here].PendingSubscriptions > 0)
            {
                segments.Add($"⚠ rtfc: {projects[here].PendingSubscriptions.ToString(CultureInfo.InvariantCulture)} pending");
            }
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

    /// <summary>
    /// <c>rtfc hook</c>, the plugin's hook for prompts, tool calls and turn ends (spec §7.3): the accept gate. A message rtfc pushed
    /// into a session, recognised by the channel tag Claude Code wraps it in, opens a gate on that session. While it is open, every
    /// tool but AskUserQuestion is denied, whatever the session's permission mode, so Claude can only give the user the gist and
    /// ask; the user's Accept, which Claude Code reports through the PostToolUse event, lifts it, and the turn ending closes it.
    /// Every prompt and tool call of every session passes through here, so for anything else it is quiet and quick, and it never
    /// fails a prompt or a tool call of its own accord.
    /// </summary>
    public static async Task<int> HookAsync(CommandContext ctx, IReadOnlyList<string> args, TextReader stdin)
    {
        if (args.Count > 0)
        {
            ctx.Error.WriteLine("usage: rtfc hook   (reads Claude Code's hook event from stdin)");
            return 2;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await stdin.ReadToEndAsync(ctx.CancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return 0;
        }

        using (document)
        {
            var root = document.RootElement;
            var session = StringProperty(root, "session_id");
            if (session is null || SessionGates.PathFor(ctx.Home, session) is null)
            {
                return 0;
            }

            switch (StringProperty(root, "hook_event_name"))
            {
                case "UserPromptSubmit":
                    if (PushedMessage(StringProperty(root, "prompt")) is { } pushed)
                    {
                        SessionGates.Write(ctx.Home, session, new SessionGate(pushed.Id, pushed.From, GateState.Pending, pushed.Kind));
                    }

                    break;

                case "PreToolUse":
                    if (SessionGates.Read(ctx.Home, session) is { State: not GateState.Accepted } gate && StringProperty(root, "tool_name") != "AskUserQuestion")
                    {
                        ctx.Out.WriteLine(Deny(gate));
                    }

                    break;

                case "PostToolUse":
                    if (StringProperty(root, "tool_name") == "AskUserQuestion" && SessionGates.Read(ctx.Home, session) is { State: GateState.Pending } asked)
                    {
                        switch (Decision(root, asked.Kind))
                        {
                            case Answer.Accept:
                                SessionGates.Write(ctx.Home, session, asked with { State = GateState.Accepted, Asked = true });
                                await NoteDecisionAsync(ctx, asked, GateOutcome.Accepted).ConfigureAwait(false);
                                break;
                            case Answer.Decline:
                                SessionGates.Write(ctx.Home, session, asked with { State = GateState.Declined, Asked = true });
                                await NoteDecisionAsync(ctx, asked, GateOutcome.Declined).ConfigureAwait(false);
                                break;
                            case Answer.Other when asked.Kind == InboxKind.Source && !asked.Asked:
                                // The user picked an action for a source item; the Accept/Decline question comes next.
                                SessionGates.Write(ctx.Home, session, asked with { Asked = true });
                                break;
                        }
                    }

                    break;

                case "Stop":
                    // A source item's turn that ended without a question found nothing to do (spec §10.4): the daemon dismisses it.
                    if (SessionGates.Read(ctx.Home, session) is { Kind: InboxKind.Source, State: GateState.Pending, Asked: false, Id.Length: > 0 } quiet)
                    {
                        await NoteDecisionAsync(ctx, quiet, GateOutcome.NothingToDo).ConfigureAwait(false);
                    }

                    SessionGates.Clear(ctx.Home, session);
                    break;

                case "SessionStart":
                    SessionGates.Clear(ctx.Home, session);
                    break;
            }
        }

        return 0;
    }

    private enum Answer { None, Accept, Decline, Other }

    /// <summary>
    /// The user's answer, read from the answers alone: the echoed options name every choice. "Accept" and "Decline" decide; for a
    /// source item "Nothing to do" declines too, and any other answer is the action they picked, which still needs confirming.
    /// </summary>
    private static Answer Decision(JsonElement root, string kind)
    {
        if (!root.TryGetProperty("tool_response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
        {
            return Answer.None;
        }

        var other = false;
        foreach (var answer in answers.EnumerateObject())
        {
            var value = answer.Value.ValueKind == JsonValueKind.String ? answer.Value.GetString()?.Trim() : null;
            if (string.Equals(value, "Accept", StringComparison.OrdinalIgnoreCase))
            {
                return Answer.Accept;
            }

            if (string.Equals(value, "Decline", StringComparison.OrdinalIgnoreCase)
                || (kind == InboxKind.Source && string.Equals(value, "Nothing to do", StringComparison.OrdinalIgnoreCase)))
            {
                return Answer.Decline;
            }

            other |= !string.IsNullOrEmpty(value);
        }

        return other ? Answer.Other : Answer.None;
    }

    /// <summary>The PreToolUse denial: the documented form, and the older decision/reason pair that some builds still read.</summary>
    private static string Deny(SessionGate gate)
    {
        var what = gate.Kind == InboxKind.Source ? $"the {gate.From} item" : $"{gate.From}'s message";
        var reason = gate.State == GateState.Declined
            ? $"rtfc: the user declined {what}, so tools stay blocked until this turn ends. Tell the user and stop."
            : gate.Kind == InboxKind.Source
                ? $"rtfc: {what} has not been accepted by the user, so every tool but AskUserQuestion is blocked. Give the user the gist and ask them "
                    + "with AskUserQuestion which action to take, with \"Nothing to do\" as an option; when they pick one, ask \"Accept\" or \"Decline\" for it."
                : $"rtfc: {what} has not been accepted by the user, so every tool but AskUserQuestion is blocked. "
                    + "Give the user the gist and ask them with AskUserQuestion, options \"Accept\" and \"Decline\".";
        return new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = reason,
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            },
        }.ToJsonString();
    }

    private static async Task NoteDecisionAsync(CommandContext ctx, SessionGate gate, string outcome)
    {
        if (gate.Id.Length == 0)
        {
            return;
        }

        try
        {
            using var client = new DaemonClient(ctx.Home, TimeSpan.FromSeconds(2));
            await client.GateDecisionAsync(gate.Id, outcome, ctx.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DaemonException or HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            // The gate is what matters; the note on the message is a courtesy.
        }
    }

    /// <summary>
    /// The id and sender of a message rtfc pushed into a session, read from the channel tag Claude Code wraps it in
    /// (<c>&lt;channel source="plugin:rtfc:rtfc" rtfc_id="…" from="…"&gt;</c>); null for any other prompt. The first such tag wins:
    /// the tag's attributes come from rtfc's own event, and a contact's text, which follows it, can only ever imitate a later one.
    /// The sender is cut down to handle characters before anything shows it.
    /// </summary>
    public static (string Id, string From, string Kind)? PushedMessage(string? prompt)
    {
        for (var start = prompt?.IndexOf("<channel ", StringComparison.Ordinal) ?? -1; start >= 0; start = prompt!.IndexOf("<channel ", start + 1, StringComparison.Ordinal))
        {
            var end = prompt!.IndexOf('>', start);
            if (end < 0)
            {
                return null;
            }

            var tag = prompt[start..end];
            if (Attribute(tag, "rtfc_id") is { Length: > 0 } id)
            {
                var kind = Attribute(tag, "rtfc_kind") == InboxKind.Source ? InboxKind.Source : InboxKind.Person;
                var from = new string([.. (Attribute(tag, "from") ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(32)]);
                return (id, from.Length > 0 ? from : kind == InboxKind.Source ? "a source" : "a contact", kind);
            }
        }

        return null;

        static string? Attribute(string tag, string name)
        {
            var marker = $" {name}=\"";
            var start = tag.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var stop = tag.IndexOf('"', start);
            return stop < 0 ? null : tag[start..stop];
        }
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
    /// <summary>
    /// <c>rtfc hints [add &lt;host&gt;… | remove &lt;host&gt;… | auto]</c> (spec §8.4): the hosts this device advertises in invites and in every
    /// hello, which is how a contact beyond the office learns a VPN address or a Tailscale name. Writes config.json, the one
    /// source of truth, and asks a running daemon to re-read it; it never starts one.
    /// </summary>
    public static async Task<int> HintsAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        if (!IdentityStore.Exists(ctx.Home))
        {
            ctx.Error.WriteLine("rtfc hints: no identity yet. Run /rtfc:init first.");
            return 1;
        }

        var config = ConfigFile.Load(ctx.Home);
        var current = config.HintHosts is { Length: > 0 } set ? set : HintHosts.Detect();
        string[]? hosts;
        switch (args.ToArray())
        {
            case []:
                return await ShowHintsAsync(ctx, config, changed: false).ConfigureAwait(false);

            case ["auto"]:
                hosts = null;
                break;

            case ["add", .. var added] when added.Length > 0:
                foreach (var host in added)
                {
                    if (host.Trim().Length == 0 || host.Any(char.IsWhiteSpace) || host.Contains('/'))
                    {
                        ctx.Error.WriteLine($"rtfc hints: '{host}' is not a hostname or an IP address.");
                        return 1;
                    }
                }

                hosts = [.. current.Concat(added.Select(h => h.Trim())).Distinct(StringComparer.OrdinalIgnoreCase)];
                break;

            case ["remove", .. var removed] when removed.Length > 0:
                hosts = [.. current.Where(h => !removed.Contains(h, StringComparer.OrdinalIgnoreCase))];
                if (hosts.Length == 0)
                {
                    ctx.Error.WriteLine("rtfc hints: that would leave nothing to advertise. Add a host first, or use `rtfc hints auto`.");
                    return 1;
                }

                break;

            default:
                ctx.Error.WriteLine("usage: rtfc hints [add <host>... | remove <host>... | auto]");
                return 2;
        }

        config = config with { HintHosts = hosts };
        ConfigFile.Save(ctx.Home, config);
        return await ShowHintsAsync(ctx, config, changed: true).ConfigureAwait(false);
    }

    private static async Task<int> ShowHintsAsync(CommandContext ctx, RtfcConfig config, bool changed)
    {
        using var client = new DaemonClient(ctx.Home);
        string[] advertised;
        string status;
        if (await client.TryStatusAsync(ctx.CancellationToken).ConfigureAwait(false) is not null)
        {
            advertised = changed
                ? await client.ReloadHintsAsync(ctx.CancellationToken).ConfigureAwait(false)
                : (await client.TryStatusAsync(ctx.CancellationToken).ConfigureAwait(false))!.Hints;
            status = changed ? "Applied to the running daemon." : "";
        }
        else
        {
            advertised = [.. HintHosts.Resolve(config).Select(h => EndpointHint.ForTcp(h, config.Port).ToString())];
            status = "The daemon is not running; these apply when it starts.";
        }

        ctx.Out.WriteLine("Advertised in your invites, and to contacts every time you talk:");
        foreach (var hint in advertised)
        {
            ctx.Out.WriteLine($"  {hint}");
        }

        ctx.Out.WriteLine(config.HintHosts is { Length: > 0 }
            ? "Set by hand in config.json; `rtfc hints auto` returns to auto-detection."
            : "Auto-detected; `rtfc hints add <host>` adds a VPN address or a Tailscale name.");
        if (status.Length > 0)
        {
            ctx.Out.WriteLine(status);
        }

        if (changed)
        {
            ctx.Out.WriteLine("Contacts learn these the next time you talk to them; new invites carry them.");
        }

        return 0;
    }

    // ---- sources (spec §10): accounts, subscriptions, forgetting a project. CLI-only, like everything that changes what reaches the user. ----

    /// <summary>
    /// <c>rtfc account add &lt;name&gt; --type jira --url … --login …</c> reads the token with hidden input, checks it against the
    /// service, stores it as a private file and tells the daemon about the account. <c>list</c> and <c>remove</c> are the rest.
    /// </summary>
    public static async Task<int> AccountAsync(CommandContext ctx, IReadOnlyList<string> args, TextReader? stdin)
    {
        var line = new CommandLine(args);
        switch (line.Positionals.ToArray())
        {
            case ["add", var name]:
                return await AddAccountAsync(ctx, line, name, stdin).ConfigureAwait(false);

            case ["remove", var removed]:
                {
                    using var client = await ConnectAsync(ctx).ConfigureAwait(false);
                    if (client is null)
                    {
                        return 1;
                    }

                    var result = await client.RemoveAccountAsync(removed, ctx.CancellationToken).ConfigureAwait(false);
                    var hadToken = Core.Sources.AccountStore.DeleteToken(ctx.Home, removed);
                    if (result.Status != ManagementStatus.Ok && !hadToken)
                    {
                        ctx.Error.WriteLine($"rtfc account: {result.Reason}");
                        return 1;
                    }

                    ctx.Out.WriteLine($"Account {removed} removed, and its token deleted. Subscriptions that used it now show an error until you point them at another account.");
                    return 0;
                }

            case [] or ["list"]:
                {
                    using var client = await ConnectAsync(ctx).ConfigureAwait(false);
                    if (client is null)
                    {
                        return 1;
                    }

                    var accounts = await client.AccountsAsync(ctx.CancellationToken).ConfigureAwait(false);
                    if (accounts.Length == 0)
                    {
                        ctx.Out.WriteLine("No source accounts. `rtfc account add <name> --type jira --url https://<site>.atlassian.net --login <email>` adds one.");
                        return 0;
                    }

                    foreach (var a in accounts)
                    {
                        ctx.Out.WriteLine($"{a.Name,-16} {a.Type,-10} {a.BaseUrl}  {a.Login}  {(a.HasToken ? "token stored" : "NO TOKEN")}  {a.Subscriptions} subscription(s)");
                    }

                    return 0;
                }

            default:
                ctx.Error.WriteLine("usage: rtfc account add <name> --type jira --url <https://site.atlassian.net> --login <email> | list | remove <name>");
                return 2;
        }
    }

    private static async Task<int> AddAccountAsync(CommandContext ctx, CommandLine line, string name, TextReader? stdin)
    {
        var type = line.Value("type");
        var url = line.Value("url");
        var login = line.Value("login");
        if (!Core.Sources.AccountStore.IsValidName(name))
        {
            ctx.Error.WriteLine("rtfc account: the name is letters, digits, '.', '-' or '_', up to 64 characters.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(login))
        {
            ctx.Error.WriteLine("usage: rtfc account add <name> --type jira --url <https://site.atlassian.net> --login <email>");
            return 2;
        }

        Core.Sources.ISourceAdapter? adapter = type switch
        {
            Core.Sources.JiraCloudAdapter.TypeName => new Core.Sources.JiraCloudAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }),
            _ => null,
        };
        if (adapter is null)
        {
            ctx.Error.WriteLine($"rtfc account: type \"{type}\" is not supported yet; jira is. Confluence and Bitbucket follow in later releases.");
            return 2;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            ctx.Error.WriteLine("rtfc account: --url must be an absolute https URL, e.g. https://acme.atlassian.net.");
            return 2;
        }

        // The token is typed, never given on the command line or piped from a slash command (spec §10.5).
        if (stdin is not null || Console.IsInputRedirected)
        {
            ctx.Error.WriteLine("rtfc account: run this in a real terminal. The token is typed with hidden input; it never goes on a command line, through a slash command or through Claude.");
            return 1;
        }

        ctx.Out.Write($"API token for {login} at {uri.Host} (hidden): ");
        var token = ReadHidden();
        ctx.Out.WriteLine();
        if (string.IsNullOrWhiteSpace(token))
        {
            ctx.Error.WriteLine("rtfc account: no token entered.");
            return 1;
        }

        var baseUrl = uri.ToString().TrimEnd('/');
        Core.Sources.SourceIdentity identity;
        try
        {
            identity = await adapter.IdentifyAsync(new Core.Sources.SourceAccount(name, type, baseUrl, login, null, token), ctx.CancellationToken).ConfigureAwait(false);
        }
        catch (Core.Sources.SourceException ex)
        {
            ctx.Error.WriteLine($"rtfc account: the token did not work: {ex.Message}");
            return 1;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        Core.Sources.AccountStore.SaveToken(ctx.Home, name, token);
        var result = await client.AddAccountAsync(new AccountRequest(name, type, baseUrl, login, identity.AccountId), ctx.CancellationToken).ConfigureAwait(false);
        if (result.Status != ManagementStatus.Ok)
        {
            Core.Sources.AccountStore.DeleteToken(ctx.Home, name);
            ctx.Error.WriteLine($"rtfc account: {result.Reason}");
            return 1;
        }

        ctx.Out.WriteLine($"Account {name} ({type}) added: {identity.DisplayName} at {uri.Host}.");
        ctx.Out.WriteLine($"The token is in {Core.Sources.AccountStore.TokenPath(ctx.Home, name)}, readable only by you; rtfc uses it to read, never to write.");
        ctx.Out.WriteLine($"Next: list a subscription under \"sources\" in <project>/.claude/rtfc.local.json with \"account\": \"{name}\", then run /rtfc:sources-approve there.");
        return 0;
    }

    /// <summary>Reads a line without echoing it. Backspace works; nothing else is interpreted.</summary>
    private static string ReadHidden()
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }

    /// <summary><c>rtfc sources [--project &lt;dir&gt;|--all]</c> shows subscriptions; <c>rtfc sources approve</c> lets a project's pending ones poll.</summary>
    public static async Task<int> SourcesAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        var line = new CommandLine(args, "all");
        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        if (line.Positionals is ["approve"])
        {
            var directory = ProjectDirectory(line);
            var result = await client.ApproveSourcesAsync(directory, ctx.CancellationToken).ConfigureAwait(false);
            if (result.Status != ManagementStatus.Ok)
            {
                ctx.Error.WriteLine($"rtfc sources: {result.Reason}");
                return 1;
            }

            ctx.Out.WriteLine(result.Reason == "0"
                ? $"Nothing waits for approval in {result.Handle}. Subscriptions live in .claude/rtfc.local.json there."
                : $"Approved {result.Reason} subscription(s) in {result.Handle}; polling starts now. Items land in this project's inbox and status line.");
            return 0;
        }

        if (line.Positionals.Count > 0)
        {
            ctx.Error.WriteLine("usage: rtfc sources [--project <dir>|--all] | rtfc sources approve [--project <dir>]");
            return 2;
        }

        var views = await client.SourcesAsync(line.Flag("all") ? null : ProjectDirectory(line), ctx.CancellationToken).ConfigureAwait(false);
        if (views.Length == 0)
        {
            ctx.Out.WriteLine("No source subscriptions. Add a \"sources\" array to <project>/.claude/rtfc.local.json and run `rtfc sources approve` there.");
            return 0;
        }

        foreach (var v in views)
        {
            if (v.FileError is not null && v.Account.Length == 0)
            {
                ctx.Out.WriteLine($"{v.Project}: {v.FileError}");
                continue;
            }

            var health = v.LastError is not null ? $"  error: {v.LastError}" : v.SeenUpTo is { } seen ? $"  seen up to {Timestamps.Format(seen)}" : "";
            ctx.Out.WriteLine($"{v.Project}  {v.Account} ({v.Type})  {v.Mode}  {v.Status}  {v.Selector}  events: {string.Join(",", v.Events)}  parked: {v.Parked}{health}");
            if (v.FileError is not null)
            {
                ctx.Out.WriteLine($"  {v.FileError}");
            }
        }

        return 0;
    }

    /// <summary><c>rtfc project forget &lt;dir&gt;</c>: stop polling a project and drop its source items (spec §10.2).</summary>
    public static async Task<int> ProjectAsync(CommandContext ctx, IReadOnlyList<string> args)
    {
        if (args.ToArray() is not ["forget", var directory])
        {
            ctx.Error.WriteLine("usage: rtfc project forget <dir>");
            return 2;
        }

        using var client = await ConnectAsync(ctx).ConfigureAwait(false);
        if (client is null)
        {
            return 1;
        }

        var result = await client.ForgetProjectAsync(ProjectPaths.Full(directory), ctx.CancellationToken).ConfigureAwait(false);
        if (result.Status != ManagementStatus.Ok)
        {
            ctx.Error.WriteLine($"rtfc project: {result.Reason}");
            return 1;
        }

        ctx.Out.WriteLine($"Forgot {result.Handle}: its subscriptions stopped and its source items are gone. Messages addressed to it show in the shared inbox.");
        return 0;
    }

    /// <summary>The project a command means: <c>--project</c>, else the directory Claude runs in, else the working directory.</summary>
    private static string ProjectDirectory(CommandLine line) =>
        ProjectPaths.Full(line.Value("project") ?? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Environment.CurrentDirectory);

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
