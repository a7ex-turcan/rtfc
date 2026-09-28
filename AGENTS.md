# AGENTS.md

Guidance for AI agents working in this repository. Tool-agnostic; `CLAUDE.md` points here.

## What this is

**rtfc** (Relay Tool For Contacts, sibling of rtfm and rtfq) lets one person's Claude Code
send messages to another person's Claude Code. Contacts are mutual and explicitly accepted,
incoming messages park in an inbox until a human looks, and a contact can optionally be
answered by a restricted, headless Claude. Jira, Confluence, Bitbucket and GitHub
notifications land in the same inbox, scoped per project.

**The design is `docs/spec.md`, and it is the source of truth.** Read the relevant section
before changing anything; `§` numbers below refer to it. `docs/implementation.md` records
how this code realizes it, what was verified against the real thing, and every departure
from the spec with its reason. If the code has to diverge from the spec, update the spec
(the design) and implementation.md (the reason) in the same change. Don't let them drift.

| Path | Stack | What |
| --- | --- | --- |
| `src/Rtfc/` | C# / .NET 10 | The single `rtfc` executable: daemon, MCP server, status line, management CLI (§3.1) |
| `tests/Rtfc.Tests/` | xUnit v3 | Unit and two-daemon integration tests |
| `plugin/` | JSON + markdown | Claude Code plugin wiring. No logic; it calls `rtfc` from PATH (§9.1) |

## Status

**Phases 1, 2 and 4 work end to end** (§17). Phase 1: `rtfc init`, invite and accept over
mutual TLS, the daemon with its lease-based lifetime and Unix-socket IPC, `send` with
nobody's-home, the parked inbox, the status line, and open and reply, all driven from the
MCP tools and the plugin's skills. Phase 2: `auto_headless` per contact
(`Node.AutoAnswer.cs`, `ClaudeProcessRunner`), every guard of §7.4, and `remove`/`block`.
Phase 4: the outbox and its pump (`Node.Outbox.cs`), queued replies and "leave it for
her", read receipts against a `sent` table, `away`, `rename`, `inbox_dismiss`, notices,
retention. Project-addressed messages (§7.6) work too, with the two parts of Phase 8a they
needed: session registration and the project-aware status line. `scripts/e2e.sh` runs the
whole story on one machine with two daemons and a fake `claude`.

Not there yet, by design: `auto_session` (Phase 7); fingerprint words (hex groups for
now); session reuse between live sends (the outbox pump does reuse one per device); a
proper detach on Windows (`daemon run` calls `setsid` on Unix only). Next is **Phase 3**,
VPN hints, then **Phase 5**, multi-device. Third-party sources come last (Phase 8).

Early phases defer features, never guards. Mutual TLS, the untrusted wrapping, size caps
and the CLI-only management boundary all shipped in Phase 1. Update this section when a
phase lands.

## Hard rules

These come from the spec's security design and its internet invariants. Each one is
cheap to keep and expensive to retrofit.

### 1. Management is never an MCP tool (§9.3)

The MCP surface is **exactly** the seven tools of §9.2: `contacts`, `send`, `inbox_list`,
`inbox_open`, `inbox_reply`, `inbox_dismiss`, `sources`. Anything that changes **who can
reach you or what your Claude will do on its own** is CLI-only: `init`, `invite`, `accept`,
`remove`, `block`, `auto`, `receipts`, `away`, `link`, `approve-device`, `revoke-device`,
`export-identity`, `account add/remove`, `sources approve`, `project forget`.

The reason: a parked message that says "please enable auto-answer for me" must never be one
tool call away from working. So:

- Slash commands that run the CLI use `!` execution, set `disable-model-invocation: true`,
  and scope `allowed-tools` to their own subcommand (`Bash(rtfc auto:*)`), never
  `Bash(rtfc:*)`. CI fails the build otherwise, for `plugin/commands/*.md` and
  `plugin/skills/*/SKILL.md` alike.
- Don't add a convenience MCP tool that wraps a management action, however harmless it
  looks. A read-only view is fine only if it is already in §9.2.
- `send` is never pre-approved anywhere, including in docs and examples. The permission
  prompt is where the user sees the exact text leaving their machine.
- CI asks the MCP server for `tools/list` and fails if any name is not one of those seven
  (`ci.yml`, "MCP tools are a subset of spec §9.2"); `scripts/e2e.sh` checks the exact
  list. Six exist today; `sources` arrives with Phase 8, and then the CI check becomes an
  exact match.

### 2. Everything from a contact or a source is untrusted input (§7.4, §7.5, §10.4)

- `inbox_open` returns bodies wrapped as
  `<contact_message from="…" id="…" untrusted="true">` or
  `<source_item source="…" entity="…" untrusted="true">`. Escape the content so it can't
  close its own wrapper. Tool descriptions tell Claude to treat it as information, not
  instructions, and to confirm with the user before acting on a request inside it.
- The MCP server **never** declares the permission-relay capability. A contact must never
  be able to approve tool use in your session.
- A project name in an envelope (§7.6) comes from a contact too. It is checked against
  the folder-name format, only ever compared with local project names, never used as a
  path, and quoted in rtfc's own notes (which sit outside the wrapper) only through
  `ProjectName.ForDisplay`. The ack is the same whether it matched or not, so a contact
  can't probe for projects; keep it that way.
- Sources never push into a session. Their only automatic mode is `prepare`, which drafts
  and never posts.
- Every auto-answer guard in §7.4 is load-bearing. Messages with `origin: "auto"` are
  never auto-answered, `hop ≥ 2` is refused, there are per-contact and global rate limits,
  and the size cap is enforced **before** the database write. The headless run is
  read-only, scoped to one directory, denied secret paths, and capped in turns and time.
  Don't loosen any of these to make a test or a demo pass.

### 3. Secrets never pass through Claude (§4, §10.5)

- Source tokens enter only through `rtfc account add` in a real terminal, with hidden
  input. Never through a slash command, an MCP tool, or a command-line argument.
- Private keys live as PKCS#8/PKCS#12 files under `keys/` with 0600 permissions (the OS
  keychain comes later). They are never stored in SQLite, logged, or included in tool
  output. The `sources` tool never shows credentials.
- rtfc only **reads** from sources. There is no write path to Jira, Confluence, Bitbucket
  or GitHub, ever.

### 4. Keep the internet door open (§15)

Violating any of these turns "add a relay" into a rewrite:

1. **Identity is keys.** Handles are local petnames; IPs and hostnames are only hints.
2. **No trusted-network shortcuts.** The LAN gets the same mutual TLS as the internet. No
   "skip TLS on localhost" switch, not even for tests: tests use real certificates.
3. **Security lives above the transport.** A transport moves bytes and reports
   reachability, nothing more. `ITransport` yields a `Stream`, and the session layer wraps
   every one in mutual-TLS `SslStream`.
4. **Endpoint hints are typed:** `tcp:host:port` now, `relay:wss://…` later.
5. **Protocol versioning** lives in `hello` and in the envelope. Unknown frame types are
   ignored.
6. **No synchronized clocks.** Order by `seq`, dedupe by `id`, and let expiries be checked
   only by whoever issued them.
7. **Presence is a transport question.** `nobody_home` means the same thing everywhere.
8. **Servers never store messages.** Durability lives only in the endpoints' inbox and
   outbox.

Key everything by `(person_id, device_id)` from day one, even though Phase 1 has one
device per person.

### 5. "Delivered" means durable (§7.2)

The receiver acks only **after** the SQLite commit. New messages are **never** queued when
nobody's home unless the user asked (`send` with `leave: true`): the sender is told
immediately. Replies, receipts and explicitly left messages go through the outbox, which
is the only place anything waits; a resend with a known `id` gets `ack: duplicate`, which
the pump treats as delivered. Sequence numbers and the target device are assigned when an
entry is delivered, not when it is queued, because they belong to the stream.

### 6. Leave the real `~/.claude/rtfc` alone

Development happens on machines where Claude Code, and eventually a real rtfc, is running.
Every path the code touches (database, keys, socket, status file, log) derives from
`RtfcHome`, whose root is `~/.claude/rtfc` unless the **`RTFC_HOME`** environment variable
says otherwise. The port lives in `config.json`; tests use port 0.

Tests use `TempHome` (a short path under the temp directory, because a Unix socket path is
capped at about a hundred characters on macOS); manual runs set `RTFC_HOME`. Don't read,
write or delete the real `~/.claude/rtfc`, don't start a daemon against it, and don't edit
the user's `~/.claude/settings.json` (for example the status line) unless the user asks.
The installed global tool (`dotnet tool install -g rtfc --add-source ./artifacts`) is fine
to use with `RTFC_HOME` set.

## Engineering rules

- **AOT-ready (§16).** `IsAotCompatible` is on and warnings are errors, so a trim or AOT
  analyzer warning fails the build. The analyzers are not the whole story: a collection
  expression on a `JsonArray` passed them and failed the real publish. Before a release,
  or after touching JSON, `dotnet publish src/Rtfc -c Release -r osx-arm64
  -p:PublishAot=true -o /some/dir` and run `RTFC_BIN=/some/dir/rtfc scripts/e2e.sh`. Use System.Text.Json with source-generated
  `JsonSerializerContext`s, never reflection-based serialization. No EF Core: use
  `Microsoft.Data.Sqlite` with hand-written SQL, or Dapper.AOT. Check that a package is AOT
  compatible before adding it. Don't suppress an AOT warning to get a green build; raise
  it instead.
- **Test guards in the published AOT binary, not only under the JIT.** rtfq's ADR 0001
  records a trimmed reflection walk that turned a guard fail-open while every JIT test
  still passed. Here the guards are the management boundary, the untrusted wrapping and
  the auto-answer limits.
- **The MCP server: prefer rtfq's approach.** The siblings split. rtfm uses the
  `ModelContextProtocol` SDK with `WithToolsFromAssembly`, which relies on reflection and
  isn't AOT-clean. rtfq ships Native AOT and hand-rolls JSON-RPC over `JsonNode` in about
  200 lines (`src/Rtfq.Mcp/McpServer.cs`), because MCP is still moving and tool discovery
  by reflection won't survive trimming. Follow rtfq unless the SDK is shown to work under
  AOT with tools registered explicitly (§16).
- **CLI parsing is hand-rolled, as in both siblings.** A `switch` on the first argument
  dispatches to a command class, and a small args helper does the rest. That settles
  §16's "same library as rtfm's CLI": rtfm uses Spectre.Console only to format output,
  not Spectre.Console.Cli. Don't add Spectre.Console until it's confirmed AOT and trim
  clean. rtfq, the AOT sibling, doesn't use it.
- **No third-party crypto.** Use `System.Security.Cryptography`: ECDSA P-256, `CertificateRequest`,
  and `X509Chain` with `TrustMode = CustomRootTrust`. Never consult the OS trust store.
- **The daemon is the only writer** to SQLite (WAL mode). `rtfc mcp` is stateless and
  forwards to the daemon over the Unix socket. `rtfc statusline` only reads `status.json`,
  because it runs every few seconds: no daemon calls and no database. The daemon writes
  `status.json` atomically (temp file, then rename). `rtfc init` is the one exception: it
  writes the identity and the `self` row before any daemon exists.
- **The IPC is a Kestrel minimal API** on the Unix socket, with the request delegate
  generator on so it survives Native AOT. **Every handler states its return type**
  (`IResult () =>`, `async Task<IResult> (HttpContext) =>`). A handler without one once
  compiled into an endpoint that answered 200 with an empty body, and the analyzers said
  nothing; `DaemonTests` drives every endpoint through the real socket to catch a repeat.
  Poke it by hand with `curl --unix-socket "$RTFC_HOME/rtfcd.sock" http://rtfcd/v1/status`.
- **The daemon is spawned detached** by `rtfc daemon ensure` with all three stdio handles
  redirected and then closed: inheriting them would keep the MCP server's stdout pipe open
  after it exits, and Claude Code waits on that pipe. The child calls `setsid` on Unix so
  a parent exiting doesn't take it along. Logs therefore go to `rtfcd.log`, never to
  stdout or stderr.
- **The plugin uses skills, not `commands/`.** Claude Code has folded commands into skills
  and prefers skills for new work; `plugin/skills/<name>/SKILL.md` becomes `/rtfc:<name>`.
  The frontmatter keys and `!` execution are the same as for commands. Tools from a plugin
  MCP server are named `mcp__plugin_rtfc_rtfc__<tool>`, so skills say "the rtfc `<tool>`
  tool" rather than spelling that out.
- **stdout is reserved.** In `rtfc mcp` it is the MCP protocol; in `rtfc statusline` it is
  what the user sees. Logs and errors go to stderr. `EntryPoint` already works this way,
  and its tests hold the line. Redirected stdio is UTF-8 whatever the console's code page
  (`Program.cs`); don't read or write `Console.In`/`Console.Out` directly, take the reader
  and writers `EntryPoint` is given.
- **Machine-readable output uses the invariant culture.** This machine's locale uses a
  decimal comma (`dotnet` itself prints `4,54 sec`). Format numbers with `InvariantCulture`
  and timestamps as ISO 8601 in anything a program or Claude parses: envelopes, tool
  results, `status.json`, SQLite text columns.
- **Schema changes are migrations.** `schema.sql` creates the current shape for new files;
  `Database.Migrate` brings older files up, one `if (SchemaVersion < n)` block per version,
  and `CurrentSchemaVersion` moves with it. Update the `CREATE TABLE` in `schema.sql` and
  the spec's §13 in the same commit, and test the migration by dropping the new columns
  (`DatabaseTests`).
- **Package versions live in `Directory.Packages.props`** (central package management).
  Shared build settings live in `Directory.Build.props`.
- **Style** comes from `.editorconfig`: file-scoped namespaces, `_camelCase` private fields,
  and LF line endings (`.gitattributes` enforces them on Windows too). CI runs
  `dotnet format --verify-no-changes`.

## Testing

- The tests use **xUnit v3 on Microsoft.Testing.Platform**, opted in through `global.json`.
  With the .NET 10 SDK, `dotnet test` refuses to run xUnit v3 without that opt-in, so
  don't remove it.
- Integration tests run **two nodes in one test process** (`TestNode`) with temp home
  directories and loopback ports (§16), covering invite, send, nobody's home, open and
  reply. They use real TLS, real certificates and real SQLite files. Don't mock the session
  layer, because that is where the bugs will be. `DaemonTests` hosts the real daemon on a
  real socket.
- `scripts/e2e.sh` is the manual smoke test: two homes under `$TMPDIR`, two detached
  daemons, `rtfc mcp` driven with raw JSON-RPC, and the idle exit. Run it after any change
  to the daemon, the MCP server or the CLI; CI can't, because it takes a minute and spawns
  processes. For the last mile, `RTFC_HOME=<that home> claude --plugin-dir ./plugin -p "…"
  --allowedTools mcp__plugin_rtfc_rtfc__contacts,mcp__plugin_rtfc_rtfc__inbox_list` runs a
  headless session against it; `send` stays unapproved on purpose.
- CI runs on Linux, macOS and Windows, because the risky parts are platform-specific: Unix
  sockets on Windows, `SslStream` key handling on macOS, and file permissions on keys.
- Name tests as sentences: `Unimplemented_modes_fail_without_touching_stdout`.

## Verify, don't assume

The spec marks several facts as unverified. Check each one against the real thing (docs, a
spike, a test) before building on it, then record what you found here or in the spec.

Checked 2026-09-27 against Claude Code 2.1.283 (`claude --help`) and the docs:

- **Plugin commands are legacy**, so the plugin uses skills (see the engineering rules).
- **Headless flags (§7.3), as used by `ClaudeProcessRunner`:** `-p --output-format json
  --restricted --strict-mcp-config --no-session-persistence --disable-slash-commands
  --tools/--allowedTools Read,Grep,Glob --disallowedTools … --permission-mode default
  --max-budget-usd --settings {deny rules} --system-prompt`, prompt on stdin. **There is
  no `--max-turns`** in this build; the caps are the budget and a wall-clock kill.
  `--restricted` ignores user/project/local settings (so no plugins, no hooks) and confines
  file tools to the working directory; `--strict-mcp-config` keeps every MCP server out,
  including rtfc's own `send`. **The child must not inherit `CLAUDE*` environment
  variables**: a daemon started from a session has a dozen of them and a nested Claude
  refuses to run under them, so the runner strips them.
- **Permission deny rules exist for `Read` only.** `Grep` and `Glob` have no path rules,
  so the secret-path denies in `ClaudeProcessRunner.DenyRules` are a second line behind
  "choose a scope without secrets", not the first. Documented for users in the README.

Everything settled by running it, with dates and how, is the "Verified against the real
thing" table in `docs/implementation.md`. Read it before re-verifying anything.

Still open:

- Source adapter endpoints (§10.1), and Bitbucket Cloud vs Data Center (§18.8).

## Open decisions

Don't settle these silently in code. Raise them.

- **Everything in §18**, such as daemon lifetime, sibling-device delivery and the read
  receipts default.

## Commands

```bash
dotnet build                              # build everything (rtfc.slnx)
dotnet test                               # run the tests
dotnet format --verify-no-changes         # the formatting check CI runs
dotnet run --project src/Rtfc -- --help   # the CLI: init, invite, accept, contacts, inbox, daemon, mcp, statusline
scripts/e2e.sh                            # two daemons on this machine, the whole story
dotnet pack src/Rtfc -c Release -o artifacts && dotnet tool install -g rtfc --add-source ./artifacts   # put `rtfc` on PATH
claude plugin validate plugin             # validate the plugin manifest
RTFC_HOME=/some/temp/home claude --plugin-dir ./plugin   # try the plugin in a session without touching the real home
```

## Commits, changelog and versions

Write an imperative, sentence-case subject that says what changed, with no type prefix,
for example "Tell the sender at once when nobody is home". Make one logical change per
commit. When code changes the design, commit the spec edit in the same commit.

`CHANGELOG.md` follows Keep a Changelog. Every user-visible change (a tool, a command, a
flag, a status, wire behaviour, a fixed bug someone could have hit) gets a line under
`[Unreleased]` in the same commit, written for the person using rtfc, not for the
reviewer. Internal refactors don't.

A release is a commit that moves the version in all three places, and CI fails if they
disagree: `<Version>` in `src/Rtfc/Rtfc.csproj`, `version` in
`plugin/.claude-plugin/plugin.json`, and a new `## [x.y.z] - date` section at the top of
the changelog with the `[Unreleased]` items moved into it and the link references at the
bottom updated. Semver: patch for fixes, minor for additive features, major for a breaking
change to the CLI, the MCP tools or the wire protocol; before 1.0, minor may break. Tag it
`vX.Y.Z` after merging: the tag push runs `.github/workflows/release.yml`, which refuses a
tag that doesn't match the declared version, publishes Native AOT binaries per platform,
runs `scripts/e2e.sh` against them, and creates the GitHub Release. GitHub Releases only:
no NuGet and no plugin marketplace, by the owner's decision (September 2026). Don't bump
the version for ordinary commits.

## Where things are

When the spec says "same as rtfm", look at the siblings, which are public:
[a7ex-turcan/rtfm](https://github.com/a7ex-turcan/rtfm) and
[a7ex-turcan/rtfq](https://github.com/a7ex-turcan/rtfq). Clone them somewhere temporary,
not into this repo. The debounced file watcher that §10.2 points at is rtfm's
`src/Rtfm.Core/Watch/FolderWatcher.cs`.

| Path | What |
| --- | --- |
| `docs/spec.md` | The design and the source of truth: architecture, protocol, schema, threat model, phasing |
| `docs/implementation.md` | How the code realizes the spec, verified facts, departures and why, schema history, known gaps |
| `README.md` | Getting started and what users need to know; keep it true when behaviour changes |
| `CHANGELOG.md` | Per-version history and the versioning rules |
| `plugin/` | Plugin wiring: manifest, `.mcp.json`, the SessionStart hook, and one skill per slash command. §16 says it ships from a separate marketplace repo; it lives here until there is something to ship |
| `scripts/e2e.sh` | The manual smoke test |
| `src/Rtfc/` | `Identity/` keys and certificates · `Storage/` SQLite · `Protocol/` frames · `Net/` transport and TLS sessions · `Core/` the node · `Daemon/` IPC host, client, launcher · `Mcp/` the stdio server · `Cli/` the commands |
| `.github/workflows/ci.yml` | Build and test on three OSes, the formatting check, and plugin JSON, version and boundary checks |
| `.github/workflows/release.yml` | On a `vX.Y.Z` tag: version gate, Native AOT binaries, e2e against them, the GitHub Release |
| `global.json` | Pins the SDK and opts `dotnet test` into Microsoft.Testing.Platform |
