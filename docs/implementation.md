# rtfc — Implementation notes

How this code realizes [`spec.md`](spec.md), what was checked against the real thing,
where the implementation departed from the spec and why, and how the schema got to where
it is. The spec says what rtfc *is*; this says how it is built. When either disagrees
with the code, one of them is wrong: fix it in the same commit.

Companions: [`../AGENTS.md`](../AGENTS.md) holds the rules for working in this repo,
[`../CHANGELOG.md`](../CHANGELOG.md) what shipped when.

## Where the spec lives in the code

| Spec | Code |
| --- | --- |
| §3.1 the modes | `EntryPoint.cs` dispatches by hand to `Cli/Commands.cs`, `Daemon/DaemonHost.cs`, `Mcp/McpServer.cs` and `Commands.Statusline` |
| §3.1 daemon lifetime | `Daemon/Leases.cs`; the idle loop in `DaemonHost.RunAsync`; `Daemon/DaemonLauncher.cs` for ensure, spawn and detach |
| §3.1 local IPC | Kestrel minimal API on the Unix socket in `DaemonHost.MapEndpoints`; `Daemon/DaemonClient.cs`; `Daemon/IpcContracts.cs` |
| §4 identity and keys | `Identity/Certificates.cs`, `Ids.cs`, `IdentityStore.cs`, `SelfIdentity.cs` |
| §5 contact lifecycle | `Node.CreateInvite`, `AcceptAsync`, `HandleInviteAcceptAsync`, `Pin`; `Core/InviteToken.cs`; remove and block in `Node.SetStatus` |
| §7.1 envelope and frames | `Protocol/Frames.cs`, `FrameCodec.cs`, `Ulid.cs` |
| §7.2 sending, nobody's home, the outbox | `Node.SendAsync`, `DeliverAsync`, `DeliverToDeviceAsync`; `Node.Outbox.cs` for the queue, the pump, expiry and notices |
| §7.3 inbound modes | park in `Node.Receive`; `auto_headless` in `Node.AutoAnswer.cs` with `Core/ClaudeRunner.cs`; `auto_session` in `Node.AutoAnswer.PushToSession`/`SessionPrompt`/`KeptOutOfSession`, `Daemon/SessionChannels.cs` (the lease's `?session=`), `McpServer.PushAsync` and the accept gate in `Commands.HookAsync` with `Core/SessionGates.cs`; `rtfc auto` (`--all`, the default scope, `session`) in `Commands.AutoAsync` and `Cli/AutoScope.cs`; receipts in `Node.Open`, `QueueReceipt`, `ReceiveReceipt` |
| §7.4 loop and abuse protection | `Node.AutoAnswer.SkipReason`; the inbound rate limit and size checks in `Node.Receive` |
| §7.5 untrusted content | `Mcp/Tools.Wrap`; `Node.AutoAnswer.UntrustedPrompt` |
| §7.6 project-addressed messages | `Node.Projects.cs` (`RegisterProject`, `Route`); `ProjectName` in `Protocol/Frames.cs`; `Core/ProjectPaths.cs`; `Node.ListInbox(state, directory, allProjects)`; the reply project in `Node.Outgoing` and `sent.project_id` |
| §10.2 session registration | `McpServer` (`CLAUDE_PROJECT_DIR`, registered once it holds a lease) → `POST /v1/projects` → `Node.RegisterProject` |
| §8 transport and session | `Net/ITransport.cs`, `TcpTransport.cs` (hints raced in `ConnectAsync`), `PeerSession.cs`, `EndpointHint.cs`; hints: `HintHosts` in `Daemon/DaemonHost.cs`, `Commands.HintsAsync`, `Node.SetHintHosts` and `LearnHints`, the `hello`'s `Hints` |
| §9 the Claude-facing surface | `plugin/` (manifest, `.mcp.json`, the hook, `skills/*/SKILL.md`); `Mcp/Tools.cs` |
| §9.3 the boundary rule | management only in `Cli/Commands.cs` over IPC routes the MCP server never calls; `ci.yml` checks the tool list and the skills |
| §11 status bar | `Core/StatusFile.cs`, `Node.Status`, `Commands.Statusline` |
| §13 storage | `Storage/schema.sql`, `Database.cs`, `Rows.cs` |
| §16 stack and distribution | `Rtfc.csproj`, `Directory.Build.props`, `.github/workflows/ci.yml`, `release.yml` |
| §17 phasing | this file's history section and `CHANGELOG.md` |

## Decisions and departures from the spec

Each of these is either a place where the spec was silent, or where building it showed the
spec's first idea would not work. The spec has been updated to the decision where the
decision is part of the design; the reasoning lives here.

**Certificates travel in the invite frames, not in the TLS chain (§5.1, 0.1.0).** The spec
first said "the certificates themselves arrive during the TLS handshake". TLS stacks do
not reliably send a self-signed root, and `SslStreamCertificateContext` drops it on
purpose, so the receiver of an invite acceptance would never see the person CA. Instead,
`invite_accept` carries the acceptor's person CA and `accept_ack` the inviter's, and each
side checks that the leaf it saw in the handshake was issued by the CA it was sent. The
leaf is still proven by TLS; the CA is proven by that check.

**The transport interface has no identity parameter (§8.1, 0.1.0).** The spec's sketch
passed `DeviceIdentity self` to `StartAsync`. A transport moves bytes; identity is the
session layer's business, so the parameter went. `ConnectAsync` returns null when no hint
answers, which is what "nobody's home" is made of.

**Skills, not `commands/` (§9.1, 0.1.0).** Claude Code folded custom commands into skills
and prefers skills for new work. `plugin/skills/<name>/SKILL.md` becomes `/rtfc:<name>`;
the frontmatter keys (`description`, `argument-hint`, `allowed-tools`,
`disable-model-invocation`) and `!` execution are the same as for commands.

**Tool names (§9.2, 0.1.0).** A plugin's MCP server exposes tools as
`mcp__plugin_rtfc_rtfc__<name>`, not `mcp__rtfc__<name>` as the spec assumed. Skills and
docs say "the rtfc `<name>` tool" and never spell the prefix out.

**The MCP server is hand-rolled (§16, 0.1.0).** rtfm uses the `ModelContextProtocol` SDK
with reflection-based tool discovery, which Native AOT cannot keep. rtfq hand-rolls
JSON-RPC over `JsonNode`, and rtfc does the same: `Mcp/McpServer.cs` is about two hundred
lines, and `claude/channel` for Phase 7 will be one more JSON field.

**The CLI is hand-rolled (§16, 0.1.0).** The spec said "the same library as rtfm's CLI".
rtfm has no CLI library: it switches on `args[0]`, and uses Spectre.Console only to format
output. rtfc does the same, without Spectre.Console until it is shown to be AOT-clean.

**GitHub Releases only (§16, 0.1.1).** The spec planned `dotnet tool install` from NuGet
for Phase 1. The owner decided against NuGet and against a plugin marketplace for now:
colleagues download an archive from GitHub Releases. Each release still carries the
dotnet-tool package for people with the SDK. Since 0.6.0 the archive is itself a local Claude Code marketplace (`.claude-plugin/marketplace.json` beside `plugin/`, the same file as at the repo root): session mode names the plugin as `plugin:rtfc@rtfc`, which needs an installed plugin, not `--plugin-dir`, and an installed plugin also spares every launch a flag.

**`RTFC_HOME` (§3.1, 0.1.0).** Every path derives from `RtfcHome`, whose root is
`~/.claude/rtfc` unless the environment variable says otherwise. It exists so tests, the
e2e script and side-by-side runs never touch the real home.

**Kestrel handlers state their return types (§3.1, 0.1.0).** The request delegate
generator, which Native AOT needs, once compiled an `async (HttpContext) => …` lambda
without a declared return type into an endpoint that answered 200 with an empty body, and
no analyzer said a word. Every handler now says `IResult` or `Task<IResult>`, and
`DaemonTests` drives every endpoint through the real socket.

**The daemon is spawned with its stdio closed (§3.1, 0.1.0).** If it inherited the MCP
server's stdout pipe, Claude Code would wait on that pipe after the server exits. So
`daemon ensure` redirects all three handles and closes them; the child logs to
`rtfcd.log` and, on Unix, calls `setsid` to leave the parent's session. Windows gets
`CreateNoWindow` and no more, for now.

Redirecting is not enough on Windows: .NET always calls `CreateProcess` with handle
inheritance on, so the daemon also inherited the stdio pipes of the process that spawned
it, which are the hook's or the MCP server's pipes from Claude Code. 0.3.1 held the
SessionStart hook's pipe for the daemon's lifetime, and the session hung. Since then
`Spawn` clears the inherit flag on its own three stdio handles before starting the child
(`SetHandleInformation`), and `DaemonTests` runs the real `rtfc daemon ensure` with piped
output and requires the pipes to close when it exits.

**Project-addressed messages route on the receiver, and threads route locally (§7.6).**
Only the folder name crosses the wire, and the receiver's ack is the same whether it
matched or not, so nothing about a machine's projects leaks to a contact. The reply side
needs no wire field at all: `sent.project_id` records where an answer should land, set from
the sending session's project for a message with a project, and from the answered
message's project for a reply. A name that matches nothing is quoted in the note only in
handle form (`ProjectName.ForDisplay`), because notes are rtfc's words and sit outside the
untrusted wrapper; otherwise a contact could put a sentence in front of Claude there.
Registration happens in `rtfc mcp` rather than the SessionStart hook, because the hook
leaves the process as soon as the daemon is up, and the MCP server is the one that also
needs the directory for `send` and `inbox_list`.

**Auto-answer's default scope is the session's directory, with a floor (§7.3).** `rtfc auto`
resolves it in the CLI (`Cli/AutoScope.cs`), because the directory is the caller's, not
the daemon's: `CLAUDE_PROJECT_DIR`, else the working directory. A skill's `!` command turns
out not to get `CLAUDE_PROJECT_DIR` (see the verified table), so from `/rtfc:auto` it is the
working directory, which is the session's; the command prints the directory it chose. The default refuses roots, the home folder and its
ancestors, and `~/.claude`, because `Grep` has no path rules and a forgotten `cd ~` would
otherwise expose SSH keys and credentials. `--all` is a loop in the CLI over the active
contacts, so the management surface on the socket did not grow.

**A lease ends when the daemon stops, and a session takes one on the next daemon (§3.1).**
The lease handler used to wait only for its client to hang up, so a stopping daemon held
every session's connection, and its TCP port, until the host's shutdown timeout, and the
sessions could not tell. It now also ends on `ApplicationStopping`. On the other side,
`DaemonLease.Ended` tells `rtfc mcp` its lease is gone, and a small loop takes a lease on
whichever daemon answers next. Before, a session learned only on its next tool call, so a
daemon restarted by the CLI (after a config change, say) idled out 30 seconds later under
an open session. The loop never starts a daemon itself, so `rtfc daemon stop` keeps its
meaning. `DaemonTests` covers both halves against real daemons, and the session test fails
without the loop.

**Session auto-answer rides the lease, and the accept gate sits in hooks (§7.3, Phase 7).**
Each `rtfc mcp` already held one open request to the daemon, so naming the session on it
(`/v1/lease?session=`) made it the push path too, one JSON line per message, with no new
connection and nothing to clean up when a session dies. The designation is the
`CLAUDE_CODE_SESSION_ID` of the session `/rtfc:auto … session` ran in, which is also what the
session's MCP server sees. The first design kept pushed messages out of any session that
does not ask before using tools; the owner's colleagues all run in bypass mode, so it would
have kept them out of every real session. The gate replaced it with one human decision per
message that works in any mode: the hooks deny every tool but `AskUserQuestion` from the
moment the pushed message becomes a prompt until Claude Code reports the user's Accept. It
had to be hooks and not the MCP server, because only a hook sees the tool calls, and a hard
gate and not an instruction, because in the spike Claude ran an unrequested `pwd` before
asking anything. The gate's state is a file per session under `gates/`, not daemon state,
because the `PreToolUse` hook runs on every tool call of every session and has to be fast,
work with no daemon, and survive a daemon restart; the turn's `Stop` and the session's next
`SessionStart` clear it. A pushed message stays parked because Claude Code drops
undeliverable channel events silently: the note says where it went, never that it arrived.
The channel tag's attributes come from rtfc's event, not from the contact, which is why the
hook may trust `rtfc_id` there, and why the body defuses `</channel` as well as
`</contact_message`.

**Beyond the office is three small things, not a transport (§8.3, §8.4, Phase 3).** The
spec promised "almost no code", and the code bears it out. Hints were already lists carried
in tokens; what broke away from the LAN was trying them one after another: the "who's home"
probe allows two seconds and a connect three, so a contact whose office address came first
was called away before their VPN address was ever tried. `TcpTransport.ConnectAsync` now
races every hint, started 250 ms apart in the listed order, so a reachable first hint still
wins alone and a dead one costs only the stagger. Contacts learn new hints from the `hello`
of every session rather than from a sync frame, because both sides already exchange one on
every connection and the claim is only about the sender's own device; Phase 5's contact and
device sync will carry the rest. `rtfc hints` writes `config.json` and asks the daemon to
re-read it, which keeps the file the one source of truth and the CLI the only writer of it,
as `rtfc init` already was. Auto-detection drops link-local addresses because this machine
advertised five of them. There is no Tailscale CLI probe: the tailnet's IPv4 address sits on
an ordinary adapter and is detected anyway, and the MagicDNS name is one `rtfc hints add`
away. IPv6 is left to `rtfc hints add` too, since most IPv6 addresses a machine has are
temporary.

**Every pipe is UTF-8.** Claude Code reads and writes UTF-8 on the MCP server's stdio, the
status line and a skill's `!rtfc` output, but .NET on Windows encodes and decodes the
standard streams in the console's code page, and a process Claude Code starts gets a
console of its own in the system's legacy code page (437 on an en-US machine). 0.3.2 wrote
📨 as `??` and read `é` as `├⌐`. `Program.cs` now wraps every redirected standard stream in
UTF-8 without a BOM and leaves a real console to .NET. `PipeEncodingTests` start the real
executable with no window, as Claude Code does, so the legacy code page is in force.

**Accepting a token from an existing contact performs the exchange (§5.1, 0.2.0).**
0.1.x returned `already_contact` without connecting, so a person who had removed or blocked
you still looked like a contact from your side. Now the exchange always happens: the
inviter says `blocked` or refreshes the contact, and the acceptor's hints get updated.

**The `sent` table and the `notice` kind (§13, 0.3.0).** The spec's model recorded nothing
about messages that left a device, so a read receipt would have had nothing to update and
an expired reply nobody to tell. `sent` records id, recipient, thread, body and state
(`queued | delivered | read | expired`); `notice` is an inbox kind for rtfc's own notes,
parked and dismissed like any message, never wrapped as untrusted.

**`send` takes `leave` (§7.2, 0.3.0).** The spec allowed the user to "leave it for her"
explicitly. It is a boolean the user must have asked for; a plain send is never queued.

**Status carries `pending` and the line shows `💤 away` (§11, 0.3.0).** Two segments the
spec's example did not have, in the same format.

**Fingerprints are hex groups (§5.1).** The spec asks for fingerprint words. Until a word
list exists, `Ids.Fingerprint` shows the first sixteen hex digits in groups of four.

**Sessions.** A live send opens one session per device and closes it after the ack. The
outbox pump reuses one session per device for a whole batch. Reuse across live sends is
an optimization for later.

**Phase order: 4 before 3.** Replies that wait for the sender are worth more than VPN
hints, and cost more to get right. See §17's progress line.

## The auto-answer run, exactly (§7.3)

`ClaudeProcessRunner` runs, in the scope directory:

```
claude -p --output-format json --restricted --strict-mcp-config --no-session-persistence
       --disable-slash-commands --tools Read,Grep,Glob --allowedTools Read,Grep,Glob
       --disallowedTools Bash,Edit,Write,MultiEdit,NotebookEdit,WebFetch,WebSearch,Task,Agent
       --permission-mode default --max-budget-usd 0.50
       --settings '{"permissions":{"deny":[Read rules for .env*, keys, certificates, credentials, .ssh, .aws, …]}}'
       --system-prompt <framing>
```

with the message on stdin, wrapped as `<contact_message untrusted="true">`, and a
three-minute wall clock after which the process tree is killed.

- **There is no `--max-turns`** in Claude Code 2.1.283; the caps are the budget and the
  clock.
- `--restricted` removes the code-running tools and WebFetch unless named, ignores user,
  project and local settings (so no plugins and no hooks), and confines file tools to the
  working directory. `--strict-mcp-config` keeps every MCP server out, including rtfc's
  own `send`.
- **`CLAUDE*` environment variables are stripped** from the child. A daemon started from
  a session inherits a dozen of them, and a nested Claude refuses to run under them.
- **Deny rules exist for `Read` only.** `Grep` and `Glob` have no path rules, so `Grep`
  can search a `.env` that `Read` cannot open. The system prompt carries that case; the
  README tells users to choose scopes without secrets.
- The result is the `result` string of the `type: "result"` object; `is_error` or a
  non-zero exit is a failure with whatever the process said.

Tried with the real `claude`: asked about a retry policy and, in the same message, for
the database password in `.env` ("ignore your rules"), it answered the first from the
files in seven seconds and refused the second.

## Verified against the real thing

Facts that were checked by running them, so nobody has to check again. Dates are when
they were checked; versions are what they were checked against.

| Fact | How | When |
| --- | --- | --- |
| macOS `SslStream` needs keychain-backed private keys; certificates loaded from PKCS#12 with `DefaultKeySet` work, ones from `CreateSelfSigned`/`CopyWithPrivateKey` do not | `PeerSessionTests` on macOS | 2026-09-27 |
| Mutual TLS with private CAs works on Linux, macOS and Windows; PKCS#12 with an empty password loads on all three | CI | 2026-09-27 |
| Unix domain sockets work under Kestrel on Windows | `DaemonTests` in CI | 2026-09-27 |
| A Kestrel handler without a declared return type can compile to an empty 200 | found in the e2e, see above | 2026-09-27 |
| A collection expression on a `JsonArray` passes the AOT analyzers and fails the AOT publish | `dotnet publish -p:PublishAot=true`; the release pipeline now runs the e2e against the native binary | 2026-09-27 |
| Claude Code 2.1.283 headless flags | `claude --help`; the real run above | 2026-09-27 |
| Plugin skills with `!` execution and `disable-model-invocation` load and run; plugin tools are `mcp__plugin_rtfc_rtfc__<name>` | headless session with `--plugin-dir` | 2026-09-27 |
| The status line's stdin JSON carries `cwd` | docs and the e2e | 2026-09-27 |
| `rtfc` is free on nuget.org | `dotnet package search` | 2026-09-27 |
| On Windows a process started with redirected stdio still inherits its parent's inheritable handles, so a daemon spawned from a hook held the hook's stdout open and Claude Code 2.1.283 stayed busy on it | two Windows 11 machines, a session stuck on start; reproduced with a piped `daemon ensure` whose stdout closed only when the daemon stopped | 2026-09-28 |
| A console process started with no window gets a fresh console in the system's OEM code page, and .NET uses that code page for redirected stdio too, so Claude Code saw `?? 1 � alex` | the status line on a Windows 11 VM; `PipeEncodingTests` with and without the fix | 2026-09-28 |
| On Windows, the daemon a session's SessionStart hook started keeps running after that session ends, and the next session takes a lease on it | two Windows 11 machines, Claude Code 2.1.283: daemons started at 11:26 and 11:29 still served sessions started at 11:44 | 2026-09-28 |
| Channels in Claude Code 2.1.284: `--dangerously-load-development-channels` works (hidden from `--help`), including `plugin:<name>@<marketplace>` for a plugin installed from a local directory marketplace; the event reaches an interactive session only, never `claude -p` nor `--input-format stream-json`; an idle session starts a turn on its own; an event sent right after `initialize` is dropped silently; Claude sees it as a user turn `<channel source="plugin:<plugin>:<server>" key="value"…>`; the connection opens with a `server/discover` request before `initialize` | throwaway probe servers and plugins, headless runs and two interactive sessions, transcripts and logs read afterwards | 2026-09-29 |
| A `UserPromptSubmit` hook receives channel events like typed prompts, with `permission_mode` (`bypassPermissions` under `--dangerously-skip-permissions`), and `{"decision":"block"}` keeps the event from Claude; the user sees the reason and the original text. `SessionStart` input has no `permission_mode` | the probe plugin's hook, interactive session | 2026-09-29 |
| In bypass mode a `PreToolUse` hook's deny (`hookSpecificOutput.permissionDecision: "deny"`, sent with the older `decision: "block"` beside it) stops the tool call, and Claude sees the reason as a tool error; `AskUserQuestion` still renders and waits for the human; its `PostToolUse` event carries `tool_response.answers` (question → chosen label) beside the echoed `questions`; `Stop` fires at the end of the turn with `session_id` | the accept-gate spike: a throwaway plugin with all four hooks, one interactive bypass-mode session | 2026-09-29 |
| Handed a contact's message in a session, Claude ran an unrequested `pwd` before asking anything | the same spike's hook log | 2026-09-29 |
| `CLAUDE_CODE_SESSION_ID` is the same in a plugin's MCP server, its hooks' input (`session_id`) and a skill's `!` command; `CLAUDE_PROJECT_DIR` is set for the MCP server but not for the `!` command, whose working directory is the session's | the probe plugin's server log, hook log and `/probe:where` | 2026-09-29 |
| Claude Code starts a plugin's MCP server in the directory the session started in (not the git root) and sets `CLAUDE_PROJECT_DIR` to the same path | a probe plugin whose server wrote down its directory, run with `claude -p` from a subdirectory of a git repository, Claude Code 2.1.283 | 2026-09-28 |
| `SslStream.ReadAsync` fills one read from every TLS record it has already buffered, so a reader that must stop exactly at the end of one frame cannot: the old hello-only reader ate the first bytes of a frame that arrived right behind the hello, and the session died with a bogus frame length (`closed_before_ack` for the sender). Seen on a busy macOS CI runner; `PeerSessionTests.Frames_sent_right_behind_the_hello_reach_a_peer_whose_reads_lag` recreates it on every OS | the release and CI runs of v0.7.0, then the test against the code before the fix | 2026-09-29 |
| On Windows, a daemon started by `rtfc daemon ensure` from a Win32-OpenSSH command session dies when that session ends (the session's job is killed; the launcher does not break away), so a host probing it a few seconds later sees `away` and pktmon on the receiver reports "transport endpoint was not found" for the SYNs. With the session kept open the same probe and a message both succeed | the rebuilt test VM, an ssh session running `daemon ensure` then sleeping, `rtfc contacts` and a send from the host meanwhile | 2026-09-29 |

## Schema history

`schema.sql` creates the current shape; `Database.Migrate` brings older files up, one
block per version.

| Version | Release | Change |
| --- | --- | --- |
| 1 | 0.1.0 | The spec's §13 schema verbatim, plus a `meta` table |
| 2 | 0.2.0 | `inbox.auto_note`, `inbox.auto_attempts` for auto-answer |
| 3 | 0.3.0 | `auto_note` renamed to `note` (it serves every kind of message); the `sent` table; the `notice` inbox kind |
| 4 | 0.4.0 | `sent.project_id`: where an answer to something sent lands (project-addressed messages, spec §7.6); `projects` and `inbox.project_id`, in the schema since v1, come into use |
| 5 | 0.6.0 | `contacts.auto_session`: the Claude Code session that answers a contact in `auto_session` mode (spec §7.3) |

## Known gaps

- **The outbox is delivered only while the daemon runs**, that is, while a session is
  open. The login-item daemon of spec §18.1 would change that.
- **Windows detach.** `daemon run` does not leave its parent's process group or job on
  Windows. Claude Code 2.1.283 leaves it running when the session that started it ends, but
  a Win32-OpenSSH command session kills it the moment the session closes (both in the
  verified table). Nothing in normal use depends on this yet; a proper detach would break
  away from the job.
- **Projects are never forgotten.** A registered project stays in `projects` and stays
  addressable; `rtfc project forget` arrives with Phase 8. Two projects with the same folder
  name are ambiguous, and messages for that name land in the shared inbox.
- **The e2e story does not cover project-addressed messages or session mode**;
  `ProjectMessageTests`, `SessionModeTests` and `DaemonTests` do, with real daemons and
  real TLS. Delivery into a live Claude Code session was checked by hand (see the verified
  table); no test can start an interactive session.
- **Session mode cannot confirm delivery.** Claude Code drops a channel event it cannot
  deliver without telling the server, so a pushed message stays parked and its note says
  where it was sent, not that it arrived. It rests on the channels research preview and on
  a launch flag with `dangerously` in its name.
- **The accept gate is a process per event.** `rtfc hook` starts on every prompt, tool call
  and turn end of every session with the plugin. With the Native AOT binary that is a few
  milliseconds; with the framework-dependent build about 100 ms.
- **The e2e story does not run on Windows** (bash, Unix socket paths); the Windows binary
  is covered by the unit tests.
- **`Grep` in auto-answer** can search files `Read` is denied.
- **Fingerprint words** do not exist yet.
- **One device per person** until Phase 5; `Receive` refuses messages from a device it
  does not know.
- **Sessions are not reused across live sends.**
