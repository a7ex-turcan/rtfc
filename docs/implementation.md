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
| §7.3 inbound modes | park in `Node.Receive`; `auto_headless` in `Node.AutoAnswer.cs` with `Core/ClaudeRunner.cs`; receipts in `Node.Open`, `QueueReceipt`, `ReceiveReceipt` |
| §7.4 loop and abuse protection | `Node.AutoAnswer.SkipReason`; the inbound rate limit and size checks in `Node.Receive` |
| §7.5 untrusted content | `Mcp/Tools.Wrap`; `Node.AutoAnswer.UntrustedPrompt` |
| §8 transport and session | `Net/ITransport.cs`, `TcpTransport.cs`, `PeerSession.cs`, `EndpointHint.cs` |
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
dotnet-tool package for people with the SDK.

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

## Schema history

`schema.sql` creates the current shape; `Database.Migrate` brings older files up, one
block per version.

| Version | Release | Change |
| --- | --- | --- |
| 1 | 0.1.0 | The spec's §13 schema verbatim, plus a `meta` table |
| 2 | 0.2.0 | `inbox.auto_note`, `inbox.auto_attempts` for auto-answer |
| 3 | 0.3.0 | `auto_note` renamed to `note` (it serves every kind of message); the `sent` table; the `notice` inbox kind |

## Known gaps

- **The outbox is delivered only while the daemon runs**, that is, while a session is
  open. The login-item daemon of spec §18.1 would change that.
- **Windows detach.** `daemon run` does not leave its parent's process group on Windows;
  whether Claude Code takes it down with the MCP server is unknown.
- **The e2e story does not run on Windows** (bash, Unix socket paths); the Windows binary
  is covered by the unit tests.
- **`Grep` in auto-answer** can search files `Read` is denied.
- **Fingerprint words** do not exist yet.
- **One device per person** until Phase 5; `Receive` refuses messages from a device it
  does not know.
- **Sessions are not reused across live sends.**
