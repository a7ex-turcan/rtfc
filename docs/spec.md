# rtfc — Relay Tool For Contacts · Design Spec

**Status:** draft v0.6 · **Date:** 2026-09-27 · **Stack:** .NET 10 / C# · **Scope:** LAN first, designed so internet reachability is an added transport, not a rewrite.

This is the design: what rtfc is, so that it could be built again from this document. How this code builds it, what was verified against the real thing, where it departed from earlier drafts and why, and the schema history are in [`implementation.md`](implementation.md).

---

## 1. What this is

**rtfc** (Relay Tool For Contacts; sibling of rtfm and rtfq) is a Claude Code plugin that lets one person's Claude Code send messages to another person's Claude Code, replacing the current routine of copy-pasting Claude output between terminals.

The core idea is the **contact**: a mutual, explicitly accepted relationship between two people. No accepted contact means no message. Incoming messages are **parked** by default: they show in the status bar and wait for a human. Per contact, a user can opt into **auto-answer**, where their Claude replies on its own within a restricted scope.

### Goals

- Invite/accept contacts; remove and block without the other side's cooperation.
- Device-aware identity: "Alex from the laptop" and "Alex from the desktop" are one contact with two endpoints.
- Send a message from a normal Claude Code conversation ("send this to Sasha").
- Parked inbox with a status-bar indicator; messages survive relaunches.
- "Nobody's home": if the recipient has no device online, the sender is told immediately.
- Optional per-contact auto-answer with a restricted profile and loop protection.
- **Sources:** notifications from Jira, Confluence, Bitbucket, and GitHub land in the same inbox, subscribed per project (§10). They come last in the phasing (§17): the primary value is agent-to-agent communication.
- LAN-only in v1, with an architecture that extends to the internet (overlay VPN, then a relay) without touching identity, storage, or the message model.

### Non-goals (for now)

Group messaging, file transfer, mobile clients, message storage on any server, "last seen" timestamps, writing back to third-party systems (rtfc reads notifications; acting on them happens in your session with existing tools), public webhook endpoints.

---

## 2. Concepts

| Term | Meaning |
|---|---|
| **Person** | A human identity, defined by a long-lived *person CA* (a small self-signed certificate authority). Contacts are people. |
| **Device** | A machine a person uses (laptop, desktop). Has its own *device certificate*, issued by the person CA. Devices are the network endpoints. |
| **Session** | A running Claude Code instance on a device. Sessions are local and never visible to contacts. |
| **Contact** | A person you've mutually accepted. Addressed as `sasha` (the person) or `sasha/laptop` (one device). |
| **Handle** | A local, editable nickname for a contact (a "petname"). Never used for identity. |
| **Home** | A person is home if at least one of their devices is reachable, meaning Claude Code is open on it. |
| **Park** | Default inbound mode: store the message, show it in the status bar, wait for the user. |
| **Auto-answer** | Opt-in inbound mode per contact: a restricted Claude answers and replies automatically. |
| **Outbox** | A local queue on the sender's device for things that must reach a peer later (replies, receipts, sync). |
| **Source** | A third-party system that produces notifications for you (Jira, Confluence, Bitbucket, GitHub). One-way trust: you authenticate to it; it never connects to you. |
| **Account** | Named credentials for one source instance (`jira-work`, `github-personal`). Managed by CLI, stored by the daemon, never in project files. |
| **Project** | A working directory (git root) that sessions run in. Subscriptions, source items and messages addressed to it (§7.6) belong to a project; a contact names it by its folder. |
| **Subscription** | Project-scoped config: an account, a selector (repo, JQL, space), and event filters. |

---

## 3. Architecture

```
 Alex's desktop                                          Sasha's laptop
┌─────────────────────────────────┐                     ┌─────────────────────────────────┐
│ Claude Code session A           │                     │ Claude Code session             │
│   ├─ MCP server (stdio, thin) ──┐                     │   ├─ MCP server ───────┐        │
│   └─ statusline cmd             │                     │   └─ statusline cmd    │        │
│ Claude Code session B           │                     │                        │        │
│   └─ MCP server ────────────────┤ local IPC           │              local IPC │        │
│                                 ▼                     │                        ▼        │
│  rtfcd  (one per device)                              │  rtfcd                          │
│   ├─ SQLite (inbox, outbox,     │   Transport: LAN    │   ├─ SQLite                     │
│   │   contacts, devices)        │ ◄─────────────────► │   ├─ keys                       │
│   ├─ keys                       │  mutually authed,   │   ├─ discovery                  │
│   ├─ discovery (mDNS)           │  encrypted session  │   └─ transports[]               │
│   ├─ transports[]               │                     │                                 │
│   └─ auto-answer runner         │                     │                                 │
└─────────────────────────────────┘                     └─────────────────────────────────┘
```

This keeps the napkin sketch's shape: each side owns its inbound queue, and the sender writes directly into the recipient's queue. There's no server in the middle on the LAN.

### 3.1 Components

Everything ships as **one executable, `rtfc`**, with several modes:

| Mode | Role |
|---|---|
| `rtfc daemon` (**rtfcd**) | Per-device daemon. Owns everything stateful: keys and certificates, SQLite, the peer listener, transports, the outbox pump, the source poller, and the auto-answer runner. |
| `rtfc mcp` | Per-session stdio MCP server, launched by Claude Code from the plugin's `.mcp.json`. Stateless; forwards messaging calls to the daemon. |
| `rtfc statusline` | Prints the status-bar segment from a small status file (§11). |
| `rtfc <command>` | Management CLI: `init`, `invite`, `accept`, `auto`, `remove`, `link`, … Invoked by slash commands (§9.3) or by hand. |

**Daemon lifetime.** `rtfc daemon ensure` (run from the plugin's SessionStart hook, and again by `rtfc mcp` on startup) starts the daemon detached if its socket doesn't answer. Each running `rtfc mcp` holds an open IPC connection as a lease. When no leases remain for a grace period (e.g. 30 s), the daemon exits. This is what makes "home" mean "Claude Code is open on that machine", and it survives crashed sessions without depending on an end-of-session hook. There's exactly one listener and one queue per device, no matter how many sessions are open.

**Local IPC.** A minimal API on Kestrel listening on a Unix domain socket at `~/.claude/rtfc/rtfcd.sock` (the whole directory moves with the `RTFC_HOME` environment variable, which is how tests and side-by-side runs stay apart). .NET supports Unix sockets on Windows 10+ too, so one mechanism covers every OS. It's debuggable with `curl --unix-socket`. The socket lives in the user's profile directory, with 0600 permissions on Unix.

**Why a separate daemon instead of doing it all in the MCP server:** several sessions share one device identity, one inbox, and one port. The IPC boundary also means a non-.NET piece (e.g. a TypeScript channel shim, §12) can talk to the same daemon.

---

## 4. Identity and keys

Identity is keys, never network addresses. It's built from ordinary X.509 certificates, so .NET's built-in TLS and certificate APIs do the heavy lifting and no third-party crypto is needed.

- **Person CA.** A self-signed X.509 certificate with an ECDSA P-256 key, `basicConstraints: CA, pathLen 0`, and long validity (e.g. 20 years). The **person ID** (`p_…`) is the SHA-256 of its SubjectPublicKeyInfo. Created once by `rtfc init`.
- **Device certificate.** A leaf certificate with its own ECDSA P-256 key, **issued by the person CA**. It carries the device ID (`d_…`, SHA-256 of the device SPKI) and the device name in a subject or extension field, with both client and server authentication usages. Validity is e.g. 2 years; renewing means re-issuing from the person CA.
- **Why P-256 rather than Ed25519:** ECDSA P-256 certificates work in TLS on every OS's native TLS stack, which `SslStream` uses. Ed25519 certificates don't work consistently across them.
- **Device list.** A signed, versioned document (canonical JSON plus an ECDSA signature by the person CA key) listing active and revoked device IDs. Peers fetch it on connect whenever its version is newer than what they hold. Revocation uses this list, not X.509 CRL/OCSP machinery.
- **Storage.** Private keys are stored as PKCS#8/PKCS#12 files in `~/.claude/rtfc/keys/` with 0600 permissions (the user-profile ACL on Windows), or in the OS keychain later. They're never stored in SQLite. Phase 1 keeps the person CA key on the first device. `rtfc export-identity` produces an encrypted PKCS#12 backup.
- **Implementation note (verify):** on macOS, `SslStream` has known quirks with certificates whose private keys are ephemeral in-memory objects. Load device credentials from a PKCS#12 file instead.

Everything in the data model is keyed by `(person_id, device_id)` from day one, even while Phase 1 supports only one device per person.

---

## 5. Contact lifecycle

### 5.1 Invite and accept

Invites are exchanged out-of-band, over any chat, and completed peer-to-peer. There's no public directory, so nobody can spam invites.

```
Alex                                        Sasha
 │ rtfc invite  (via /rtfc:invite)            │
 │  → token = "rtfc1_" + base64url({          │
 │      v, person_id, handle_hint,            │
 │      endpoint_hints[], nonce, expires_at })│
 │  (nonce stored in `invites`)               │
 │ ── token via Telegram / Slack / etc ─────► │
 │                                            │ rtfc accept <token>  (via /rtfc:accept)
 │ ◄────── TCP connect to endpoint_hints ──── │
 │ ◄═══════ mutual TLS handshake ═══════════► │ (proves each side's device leaf, not its chain)
 │  Sasha's leaf chains to no CA Alex knows   │
 │  → connection is RESTRICTED:               │
 │  only `invite_accept` is allowed           │
 │ ◄── invite_accept { nonce, person_ca, … } ─│
 │  verify Sasha's leaf was issued by that CA │
 │  verify nonce unused + unexpired           │
 │  pin Sasha's person CA, mark nonce used    │
 │ ─── accept_ack { person_ca, … } ─────────► │ Sasha checks: hash(person_ca) == token.person_id
 │                                            │ and Alex's leaf was issued by it, then pins it
 │ both sides: contact status = active        │
 │ both UIs show fingerprint words for optional in-person verification
```

- The token carries only the person ID fingerprint, not certificates, so it stays short enough to paste. The device leaf arrives in the TLS handshake and the person CA in the `invite_accept` / `accept_ack` frame; each side checks that the leaf it saw was issued by the CA it was sent (see implementation.md for why the CA does not travel in the TLS chain).
- Tokens are single-use, expire after 24 h, and are checked only by the issuer, so there's no dependency on synchronized clocks.
- Accepting requires the inviter to be home. If they aren't, `accept` returns `nobody_home` and the token stays valid.
- Accepting creates the contact with `inbound_mode = park`. Accepting never implies auto-answer.

### 5.2 Remove and block

- **Remove:** local and immediate. The contact's status becomes `removed`, and connections from their devices are rejected at the handshake. A courtesy `bye` is sent if they're online, but it isn't required.
- **Block:** remove, plus refuse future invites carrying that person key.

---

## 6. Devices (multi-device, Phase 5)

### 6.1 Linking a new device

1. On the new device: `/rtfc:link` generates a device key and certificate signing request, then shows a short code encoding its `tcp:` hint and fingerprint.
2. On an existing device: `/rtfc:approve-device <code>` connects to the new device, verifies the fingerprint, issues its device certificate from the person CA, bumps the device list version, and sends the new device the contact list.
3. Contacts learn about the new device the next time they connect (device list version check). No re-invites are needed.

### 6.2 Revocation

`/rtfc:revoke-device laptop` from any other device publishes a new device list marking it revoked. Peers reject its handshakes as soon as they see the new list: immediately if online, otherwise on next connect. Revoked devices are removed as fan-out targets.

### 6.3 Own-device sync

These travel between a person's own devices through the outbox:

- **Contact records.** Last-writer-wins per record, using a `rev` counter.
- **Handled state.** If Sasha's message was answered on the desktop, the laptop marks it handled when it next connects.
- **Auto-owner.** At most one device per contact may auto-answer. Enabling auto on one device disables it on the others. This avoids needing a distributed lease.

---

## 7. Messaging

### 7.1 Envelope

Frames travel inside an authenticated, encrypted session (§8). The envelope is plain JSON inside that session and identical on every transport.

```json
{
  "v": 1,
  "type": "message",
  "id": "01J8ZQ4Y7K3M9V2T6H0XWBNC5R",
  "from": { "person": "p_3fa9…", "device": "d_77c1…" },
  "to":   { "person": "p_b204…", "device": "d_19ae…" },
  "seq": 42,
  "thread": "01J8ZQ3…",
  "replyTo": null,
  "origin": "human",
  "hop": 0,
  "sentAt": "2026-09-10T14:03:11Z",
  "body": { "text": "How does your retry policy handle poison messages?" }
}
```

- `id` is a ULID. It's the same logical ID across fan-out copies and is used for dedupe.
- `seq` is per (sender device → recipient device) stream and is used for display order and gap detection.
- `origin` is `human` or `auto`, and `hop` is 0 for new messages and parent + 1 for replies. Both exist for loop protection (§7.4).
- `sentAt` is informational only. Nothing depends on clocks agreeing.
- The body is capped at 64 KB of text in v1.
- `project` is optional and absent unless the user named one: which of the recipient's projects the message is about (§7.6). Older daemons ignore it.

Other frame types: `ack`, `receipt` (read), `handled`, `device_list_req` / `device_list`, `contact_sync`, `invite_accept`, `bye`. Unknown types are ignored, which gives forward compatibility.

### 7.2 Sending and "nobody's home"

`send(to, text)` runs this flow in the daemon:

1. Resolve `to` → person → target devices: all active devices, or one if addressed as `sasha/laptop`.
2. Ask the transports which targets are reachable (§8.2).
3. For each reachable device: open or reuse a session, send the envelope, and wait for an `ack` (timeout ~5 s).
4. The receiver acks **only after the message is committed to SQLite**, so "delivered" means durable.
5. Return a structured result that the sender's Claude turns into plain language:

```json
{ "status": "delivered",      "to": ["sasha/laptop"] }
{ "status": "partial",        "to": ["sasha/desktop"], "unreachable": ["sasha/laptop"] }
{ "status": "nobody_home",    "person": "sasha" }
{ "status": "device_offline", "requested": "sasha/laptop", "online": ["sasha/desktop"] }
{ "status": "rejected",       "reason": "not_a_contact" }
```

New messages are **never queued** when nobody's home. The user can choose to put one in their own outbox explicitly ("leave it for her").

**Retries are idempotent.** A resend with a known `id` gets `ack: duplicate`.

**Replies are the exception.** Because parked messages may be answered hours later, `inbox_reply` falls back to the **local outbox** when the original sender isn't home. The outbox pump delivers it the next time both sides are online. Outbox entries expire after 7 days, and the user is told if a reply expired undelivered.

### 7.3 Inbound modes

Set per contact with `/rtfc:auto <contact> off|headless|session [--scope <dir>]`.

**`park` (default).** The message is stored with state `parked`, and the status file is updated. The user pulls it in with `/rtfc:inbox` or by asking Claude ("what did Sasha send?"). Opening it sets `read` and sends a read receipt, if enabled for that contact.

**`auto_headless` (recommended auto mode).** The daemon answers each message in a fresh, isolated, non-interactive Claude Code run:

- Run `claude -p` with its working directory set to the configured scope directory.
- Restrict tools with an allowlist of read-only tools (e.g. Read, Grep, Glob): no Bash, no writes, no web access.
- Add permission deny rules for secret-bearing paths (`.env*`, `*.pem`, `*.key`, credential files).
- Cap the run at a small number of turns and a timeout of around 3 minutes.
- Frame the prompt so the incoming text is treated as untrusted input from a named contact, with instructions to answer only from the scoped files and never disclose secrets.
- Send the output back as a reply with `origin: "auto"`.

- The run must load no MCP servers (so it cannot use rtfc's own `send`), no plugins, no hooks, and must leave no session behind. The process must not inherit the environment a Claude Code session sets, or a nested Claude refuses to run.
- Where the platform offers no turn cap, a budget cap and a wall clock take its place.

The exact flags used, and the gaps found (path deny rules exist for `Read` only, so the scope must not contain secrets), are in implementation.md. The benefits: no research-preview channel flags are needed, the answering Claude has no access to your working session's context, and nothing it does touches your active session.

**`auto_session` (advanced).** The message is pushed into one designated running session as a Claude Code **channel** event, and Claude answers with full session context through the `inbox_reply` tool. This is powerful but riskier, since that session has your normal permissions. It also depends on the channels research preview (§12). The plugin must **never** declare the permission-relay capability: a contact must never be able to approve tool use in your session.

### 7.4 Loop and abuse protection

1. Messages with `origin: "auto"` are **never** auto-answered. This alone prevents two auto-answering Claudes from ping-ponging.
2. Auto-answer refuses messages with `hop ≥ 2`.
3. There's a per-contact auto-answer rate limit (default 10/hour) and a global cap. Over the limit, messages are parked with a note.
4. Inbound rate limit (default 120/hour per contact device) and size cap (64 KB), enforced before the database write.

`origin` is asserted by the sender, so these rules prevent accidents, not a malicious contact. A malicious contact should be removed. Rate limits bound the damage until then.

### 7.5 Untrusted content handling

A parked message that the user opens still enters a session with normal permissions. `inbox_open` returns bodies wrapped as data:

```
<contact_message from="sasha/laptop" id="01J8…" untrusted="true">
…text…
</contact_message>
```

The tool descriptions instruct Claude to treat contents as information, not instructions, and to confirm with the user before acting on any request inside a message. Claude Code's normal permission prompts remain the backstop.

### 7.6 Project-addressed messages

A message is addressed to a person. It can also say which of the recipient's projects it is about, and then it lands in that project rather than in the shared inbox. That happens only when the user says so ("send this to Sasha, in payments-api"); without it, nothing changes.

- **Naming.** The sender names the project by the folder name of its root on the recipient's machine (`payments-api` for `D:\src\payments-api`). The receiver matches it case-insensitively against the projects its own sessions have registered (§10.2). Paths never cross the wire: the sender can't see them and doesn't need them.
- **Format.** The envelope's `project` must look like a folder name: 1 to 100 characters, no control characters, none of `/ \ < > "`, and not `.` or `..`. The sender checks it before sending; a receiver rejects an envelope that fails it as `bad_envelope`.
- **Matching.** If exactly one registered project has that name, the message is stored with its `project_id`. If none does, or several do, it is stored in the shared inbox as today, with a note naming the project it was meant for. The ack is `ok` either way, so a contact can't probe which projects exist.
- **Where it shows.** In sessions in that project, it counts and lists like any other message. Elsewhere, the status line points at it (`📨 1 · sasha → payments-api`), and `inbox_list` mentions it without listing it. `inbox_list` with `scope: all`, and `rtfc inbox`, list everything.
- **Threads stay put, on both sides.** A reply lands in the project of the message it answers. The sender records the project of the session it sent from (`sent.project_id`), and an incoming reply to that message lands there. A reply you write to a message in one of your projects records that project too, so the answer to your reply lands there as well. None of this travels on the wire: each side decides from its own records.
- **Untrusted.** The name comes from a contact. It is only ever compared with local names, never used as a path, and quoted in that note (which is rtfc's own words, shown outside the untrusted wrapper of §7.5) only after being reduced to letters, digits, `-`, `_` and `.`, as a suggested handle is.
- Auto-answer is unaffected: its scope is still set per contact (§7.3).

---

## 8. Transport and session layer

### 8.1 Layering

```
  MCP tools / slash commands
          │
  Messaging (envelopes, acks, outbox, inbox)        ← identical everywhere
          │
  Session: mutual TLS (SslStream) + framing          ← identical everywhere
          │
  Transport: a byte pipe to a device                ← swappable
     ├─ tcp     (direct TCP to host:port hints)                Phase 1 (LAN), 3 (VPN)
     ├─ mdns    (discovery that produces tcp hints)            later
     └─ relay   (WebSocket to a rendezvous relay, as a Stream) Phase 6
```

The rule that keeps the internet option open: **all security lives above the transport.** A transport only has to move bytes to a device and report reachability.

```csharp
public interface ITransport
{
    string Kind { get; }  // "tcp", "mdns", "relay"
    Task StartAsync(Func<Stream, Task> onInbound, CancellationToken ct);
    Task StopAsync();
    Task<bool> IsReachableAsync(string deviceId, IReadOnlyList<EndpointHint> hints, CancellationToken ct);
    Task<Stream?> ConnectAsync(string deviceId, IReadOnlyList<EndpointHint> hints, CancellationToken ct);  // null: no hint answered
}
// A transport yields a raw Stream. The session layer wraps every Stream,
// on every transport, in SslStream with mutual TLS (§8.2).
```

### 8.2 Session handshake

Every Stream is wrapped in `SslStream` with **mutual TLS**: TLS 1.3 where the OS supports it, TLS 1.2 at minimum. Both sides present their device certificate chain (device cert + person CA). The server requires a client certificate.

Validation ignores the OS trust store entirely. The `RemoteCertificateValidationCallback` builds the chain with `X509ChainPolicy.TrustMode = CustomRootTrust`, where the custom trust store is your own person CA plus the CAs of your active contacts. A connection is accepted as **authenticated** only if:

- the chain ends at your own CA or an active contact's CA, and
- the leaf's device ID isn't revoked in the latest known device list for that person.

A chain that is self-consistent but ends at an unknown CA is accepted only as **restricted**. The only frame allowed on a restricted connection is `invite_accept` (§5.1); anything else closes it. Everything else fails the TLS handshake.

After the handshake, frames are 4-byte length-prefixed UTF-8 JSON with a maximum frame size, handled with `System.IO.Pipelines`. Both sides exchange `hello {v, device_list_version}` first; protocol version negotiation lives here.

There is **no "trusted LAN" shortcut**. The LAN gets the same authentication and encryption as the internet will. Because TLS runs over any `Stream`, a future relay can carry the same TLS session end to end without being able to read it.

### 8.3 LAN transport (Phase 1)

- **No discovery in Phase 1.** On a wired office LAN where machines already find each other by hostname (SMB works), invite tokens and device records carry `tcp:<hostname>:<port>` hints. The default port is fixed (e.g. 47821) and configurable.
- **Reachability:** a short TCP connect probe to the hints.
- **mDNS later:** a `mdns` component that advertises `_rtfc._tcp` and produces `tcp:` hints. .NET mDNS libraries are the least mature part of the stack, so evaluate options such as the `Makaretu.Dns.Multicast` forks for maintenance before choosing. Discovery results are only hints; the TLS handshake is the only proof of identity.
- **First run:** expect an OS firewall prompt the first time `rtfcd` listens.

### 8.4 Endpoint hints

Devices carry a list of typed hints, and invite tokens include them:

```json
["lan:mdns", "tcp:alex-desktop.local:47821", "tcp:100.101.5.7:47821", "relay:wss://relay.example/v1"]
```

Adding a new way to reach someone means adding a hint type and a transport, and nothing else.

---

## 9. Claude-facing surface

### 9.1 Plugin layout

A Claude Code plugin is a folder of JSON and markdown that points at executables. All logic lives in the `rtfc` binary; the plugin is only wiring:

```
rtfc/                                  (plugin, distributed via a marketplace repo)
├── .claude-plugin/plugin.json         name "rtfc", version, description
├── .mcp.json                          { "mcpServers": { "rtfc": { "command": "rtfc", "args": ["mcp"] } } }
├── skills/<name>/SKILL.md             invite, accept, inbox, auto, remove, …
└── hooks/hooks.json                   SessionStart → "rtfc daemon ensure"
```

- The plugin calls `rtfc` from PATH, so the repo contains no binaries. See §16 for how `rtfc` gets installed.
- Skills are namespaced automatically: `skills/invite/SKILL.md` becomes `/rtfc:invite`.
- The status line isn't a plugin component; it's configured once in the user's settings (§11).

### 9.2 MCP tools (messaging only)

These are the only things Claude can do on its own. Claude Code exposes them as MCP tools of the `rtfc` server under a prefix it chooses, so skills and docs name them as "the rtfc `<name>` tool".

| Tool | Args | Returns / effect |
|---|---|---|
| `contacts` | – | contacts with per-device online state and inbound mode |
| `send` | `to`, `text`, `leave?`, `project?` | delivery result (§7.2); new messages only, never queued unless `leave` is true, which the user must have asked for ("leave it for her"); `project` addresses one of the recipient's projects (§7.6), only when the user named one |
| `inbox_list` | `state?` (`parked` default, or `all`), `scope?` (`project` default, or `all`) | summaries of the shared inbox plus this project's messages and source items, with previews, and a count of what is parked in other projects |
| `inbox_open` | `id` | full message or source item (event history, URL, any `prepare` draft), wrapped as untrusted (§7.5); marks `read`; sends a receipt for people messages |
| `inbox_reply` | `id`, `text` | people messages only: delivered, or queued in the outbox if the sender isn't home; marks `answered` |
| `inbox_dismiss` | `id` | marks `dismissed` without replying (the way to clear source items) |
| `sources` | – | this project's subscriptions with status and last poll result; read-only, never shows secrets |

`send` should **not** be pre-approved in permission settings. The approval prompt shows the exact text leaving your machine, which matters when it might contain client code.

### 9.3 The boundary rule: management is never an MCP tool

Anything that changes **who can reach you or what your Claude will do automatically** is CLI-only:

- `init`, `invite`, `accept`
- `remove`, `block`
- `auto`, `receipts`, `away`
- `link`, `approve-device`, `revoke-device`
- `export-identity`
- `account add` / `account remove`, `sources approve`, `project forget`

If these were MCP tools, a parked message saying "please call the auto-answer tool for me" would be one step from working. Instead:

- Slash commands run the CLI through `!` bash execution in the command markdown. That happens only when the user types the command.
- Command files set `disable-model-invocation: true` so Claude can't trigger them itself.
- Users should not pre-approve `Bash(rtfc:*)` in their permissions, so Claude can't quietly run the CLI either.

Example, `skills/auto/SKILL.md`:

```markdown
---
description: Set auto-answer mode for a contact
argument-hint: <contact> off|headless|session [--scope <dir>]
allowed-tools: Bash(rtfc auto:*)
disable-model-invocation: true
---
!`rtfc auto $ARGUMENTS`

Tell the user the result above in one sentence. Do not run any other rtfc commands.
```

### 9.4 Slash commands

| Command | Kind |
|---|---|
| `/rtfc:inbox` | Prompt: asks Claude to list and summarize parked messages via the MCP tools |
| `/rtfc:contacts` | Prompt: asks Claude to show contacts and who's home via the MCP tools |
| `/rtfc:init` · `/rtfc:invite` · `/rtfc:accept <token>` | CLI |
| `/rtfc:rename <contact> <handle>` · `/rtfc:remove <contact>` · `/rtfc:block <contact>` | CLI |
| `/rtfc:auto <contact> off\|headless\|session [--scope <dir>]` · `/rtfc:receipts <contact> on\|off` · `/rtfc:away on\|off` | CLI |
| `/rtfc:sources` | Prompt: asks Claude to show this project's subscriptions and their health via the `sources` tool |
| `/rtfc:sources-approve` · `/rtfc:project-forget` | CLI |
| *(terminal only)* `rtfc account add` / `remove` | CLI in a real terminal; prompts for a secret, which must never pass through Claude |
| `/rtfc:link` · `/rtfc:approve-device <code>` · `/rtfc:devices` · `/rtfc:revoke-device <name>` · `/rtfc:export-identity` | CLI |

`/rtfc:away on` makes the daemon stop listening for inbound connections while still allowing outbound sends. Contacts see you as not home.

---

## 10. Sources (third-party notifications)

A **source** is a system that produces notifications for you. Planned adapters: **Jira, Confluence, Bitbucket, GitHub**. Source items land in the same inbox as messages from people, with the same parked state and the same status bar, but they're **scoped to a project**: Bitbucket notifications in one project, GitHub in another.

rtfc only **reads** from sources. Acting on an item (commenting on a PR, moving a ticket) happens in your session with tools you already have, such as the Atlassian MCP server or `gh`, and with your approval. rtfc is an event inbox, never a Jira or Bitbucket client.

### 10.1 Ingestion: polling first

Webhooks don't fit v1, for three reasons:

- Cloud services can't reach a laptop on a LAN, so webhooks need a public tunnel or the relay.
- A webhook that arrives while nobody's home is lost unless a server stores it, which breaks invariant 8 (§15).
- Polling needs neither: **the third party is the durable queue.**

How polling works:

- **Cursor per subscription.** A watermark (last `updated` time or the service's own cursor) plus the external IDs seen at the boundary, for dedupe. After a relaunch, the daemon polls from the cursor and catches up. Catch-up is capped (e.g. 7 days); older items are summarized as a single "N older updates" item.
- **Intervals.** Default 60–120 s per subscription, with jitter. Back off on errors, and honor the service's rate-limit and `Retry-After` responses.
- **Shared polls.** Subscriptions with the same account and selector across projects share one poll, and the results fan out to each project.
- **Later: webhook as a poke.** A webhook through the relay (§15, Stage B) can trigger an immediate poll. The relay still stores nothing. A missed poke costs only latency, because the next scheduled poll catches up.

```csharp
public interface ISourceAdapter
{
    string Type { get; }  // "jira", "confluence", "bitbucket", "github"
    Task<PollResult> PollAsync(Account account, Subscription sub, SourceCursor cursor, CancellationToken ct);
}

public sealed record PollResult(IReadOnlyList<SourceEvent> Events, SourceCursor Next, TimeSpan? RetryAfter);

public sealed record SourceEvent(
    string ExternalId,   // stable per event, for dedupe
    string EntityKey,    // "jira:PAY-123", "bitbucket:acme/payments-api#42"
    string EventType,    // normalized vocabulary, §10.3
    string Actor, string Title, string Summary, string Url,
    DateTimeOffset OccurredAt);
```

Each adapter maps its service's API to the normalized event vocabulary. Verify the endpoints when implementing:

| Adapter | Approach |
|---|---|
| **GitHub** | The user's notifications feed (repo-filterable) plus review requests |
| **Bitbucket** | Pull requests where I'm a reviewer, comments on my PRs, build status on my PRs. Cloud and Data Center APIs differ; pick one first. |
| **Jira** | JQL search with `updated >= cursor`, then the changelog and comments to classify what happened |
| **Confluence** | CQL for mentions, comments on my pages, and watched pages |

### 10.2 Project scoping

- **Session registration.** On start, `rtfc mcp` sends the session's directory to the daemon: `CLAUDE_PROJECT_DIR`, which Claude Code sets to the directory the session started in, else its own working directory. The daemon resolves the project root (the git root, else that directory) and records it in `projects`, keyed by its normalized path and named after its folder. This part shipped early, with project-addressed messages (§7.6).
- **Config.** `<project>/.claude/rtfc.local.json` is gitignored, following the same convention as Claude Code's `settings.local.json`. The daemon watches it with a debounced file watcher (the same approach as rtfm's) and reloads it.

```json
{
  "sources": [
    { "account": "bitbucket-work", "type": "bitbucket", "repo": "acme/payments-api",
      "events": ["review_requested", "comment_on_mine", "build_failed_on_mine"] },
    { "account": "jira-work", "type": "jira",
      "jql": "project = PAY AND (assignee = currentUser() OR watcher = currentUser())",
      "events": ["assigned", "mentioned", "status_changed"],
      "mode": "prepare" }
  ]
}
```

- **Approval.** New or changed subscriptions start as `pending_approval`. They activate only after `rtfc sources approve` (CLI), and the status line shows `⚠ rtfc: 2 pending` until then. The file is editable by anything that can write to the repo, including Claude, while changing what reaches your Claude is a management action (§9.3).
- **Accounts.** A subscription can only reference an account that already exists. It can never contain credentials.
- **Polling scope.** While the daemon runs, it polls the active subscriptions of every known project, not just open ones. Items are tagged with their project and are waiting when you open it. `rtfc project forget <path>` stops polling a project and removes its items.
- **Messages from people stay global:** `project = NULL`, visible in every project, unless the sender addressed one of your projects (§7.6).

### 10.3 Noise control

- **Normalized event vocabulary:** `assigned`, `mentioned`, `review_requested`, `comment_on_mine`, `reply_to_me`, `status_changed`, `approved`, `changes_requested`, `merged`, `build_failed_on_mine`. Each adapter documents which of these it produces.
- **Involvement by default.** Only events that involve you are kept. Anything broader has to be listed explicitly.
- **Your own actions are suppressed.** Your own comments and transitions never notify you.
- **Coalescing per entity.** There is one inbox item per ticket, PR, or page, identified by `entity_key`, which reuses the `thread` concept. New events append to that item's history, and an item that was already read goes back to `parked`. The status line counts entities, not events: five updates to PAY-123 are one line, "PAY-123 · 5 updates".

### 10.4 Inbound modes for sources

| Mode | Behavior |
|---|---|
| `park` (default) | Item is stored and shown in the status bar; you pull it in with `/rtfc:inbox`. |
| `prepare` | The auto mode for sources. A headless, read-only Claude run (same restrictions as `auto_headless`, §7.3) with the **project directory** as scope drafts something useful: review notes for a PR, a summary of a ticket thread, a suggested reply. The draft is attached to the item and the item is parked. **It never posts anything.** |

There's no push-into-session mode for sources. The claude-pr-channel project describes the risk directly: anyone who can comment on a PR is effectively talking to your agent.

Every `prepare` run uses Claude usage, so it's rate-limited per project (e.g. 20/hour) and capped per day. Items over the limit are parked without a draft.

Source content (ticket descriptions, comments, page text, PR descriptions) is untrusted, wrapped the same way as contact messages (§7.5):

```
<source_item source="jira" entity="PAY-123" untrusted="true">…</source_item>
```

### 10.5 Accounts and credentials

- **Adding an account:** `rtfc account add <name> --type jira --url https://acme.atlassian.net` prompts for the token with hidden input.
- **Run this in a real terminal, not via a slash command.** A slash command's `!` execution isn't interactive, and more importantly, the token must never pass through Claude's context.
- **Storage:** the OS keychain where available, otherwise the keys directory with 0600 permissions. Tokens are device-local and aren't synced between your own devices in v1.
- **Scopes:** use the narrowest the service offers, read-only where possible. Some Atlassian tokens carry the user's full permissions. That's another reason rtfc never writes to sources: a token that's only ever used for reading limits the damage if it leaks.
- **Multiple devices:** each device polls its own projects. Handled state syncs between your devices by `(source, entity_key)` so the same PR doesn't nag you twice (§6.3).

---

## 11. Status bar

- The daemon writes `~/.claude/rtfc/status.json` atomically (write temp file, then rename) whenever the inbox changes. Counts are split into global (the shared inbox) and per project (messages addressed to a project, §7.6, and source items), keyed by the project's normalized root path:
  `{ "global": { "parked": 1, "from": ["Sasha"], "pending": 2 }, "projects": { "/src/payments-api": { "name": "payments-api", "parked": 1, "from": ["Alex"], "reviews": 2, "tickets": 3, "pendingSubscriptions": 0 } }, "away": false }` (`pending` counts the outbox; the status line shows it as `📤 2`, and `away` as `💤 away`)
- Claude Code passes session information, including the current working directory, to the status line command on stdin. `rtfc statusline` uses it to pick the right project and prints e.g. `📨 2 · Sasha, Alex  🔀 2  🎫 3`, or nothing when there's nothing to show. The current project's messages count with the shared inbox; another project's appear as a pointer, `📨 1 · Alex → payments-api`. Reading a file keeps it fast, since status lines run often.
- Configure it once in `~/.claude/settings.json`:
  `{ "statusLine": { "type": "command", "command": "rtfc statusline", "refreshInterval": 5 } }`
- Claude Code has **one** `statusLine` setting per user, so ship this as a composable segment: users with an existing status line call `rtfc statusline` from their own script. Offer a standalone config for everyone else.
- Set `refreshInterval` (in seconds; minimum 1) so the counter updates while the session is idle. Otherwise the status line only refreshes on session events.

---

## 12. Claude Code platform constraints

- **Channels** (needed only for `auto_session`) are a research preview. A channel is an MCP server declaring the `claude/channel` capability and emitting `notifications/claude/channel` events (with `content` and `meta`) over stdio. Custom channels aren't on the approved allowlist during the preview and need `--dangerously-load-development-channels`. Team/Enterprise organizations must enable channels explicitly; Pro/Max users without an organization aren't gated. Events that arrive while Claude is busy are delivered together on the next turn. Docs: https://code.claude.com/docs/en/channels and https://code.claude.com/docs/en/channels-reference
- **Status line:** event-driven with a 300 ms debounce, plus optional `refreshInterval`. Docs: https://code.claude.com/docs/en/statusline
- **Headless (`claude -p`):** tools that need terminal input are unavailable in this mode, which is desirable for auto-answer.
- **Channels from C#:** the channel examples are TypeScript. Before Phase 7, check whether the C# MCP SDK can declare the `claude/channel` experimental capability and send the custom notification. If it can't, a small TypeScript channel shim talking to `rtfcd` over the same Unix socket covers `auto_session`, and nothing else changes.
- **Plugins:** the manifest lives in `.claude-plugin/plugin.json`; commands, hooks, and `.mcp.json` sit at the plugin root; `${CLAUDE_PLUGIN_ROOT}` is available for intra-plugin paths. Docs: https://code.claude.com/docs/en/plugins-reference
- Nothing before Phase 7 depends on channels. Park and headless auto-answer **don't need them at all**.

---

## 13. Storage (SQLite, WAL mode, single writer = daemon)

Location: `~/.claude/rtfc/rtfc.db`. Private keys are not stored here (§4).

```sql
CREATE TABLE self (
  person_id           TEXT PRIMARY KEY,
  handle              TEXT NOT NULL,
  person_ca_cert      BLOB NOT NULL,              -- DER; private key lives in keys/
  device_id           TEXT NOT NULL,
  device_name         TEXT NOT NULL,
  device_cert         BLOB NOT NULL,              -- DER
  device_list_version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE contacts (
  person_id           TEXT PRIMARY KEY,
  handle              TEXT NOT NULL,              -- local petname
  person_ca_cert      BLOB NOT NULL,              -- DER; pinned trust anchor
  status              TEXT NOT NULL,              -- active | removed | blocked
  accepted_at         TEXT,
  inbound_mode        TEXT NOT NULL DEFAULT 'park', -- park | auto_headless | auto_session
  auto_scope          TEXT,
  auto_owner_device   TEXT,                       -- which of MY devices auto-answers
  read_receipts       INTEGER NOT NULL DEFAULT 1,
  device_list_version INTEGER NOT NULL DEFAULT 0,
  rev                 INTEGER NOT NULL DEFAULT 0  -- own-device sync, LWW
);

CREATE TABLE devices (                            -- contacts' devices AND my own
  device_id   TEXT PRIMARY KEY,
  person_id   TEXT NOT NULL,
  name        TEXT NOT NULL,
  cert        BLOB NOT NULL,                      -- DER device cert, issued by the person CA
  status      TEXT NOT NULL,                      -- active | revoked
  endpoints   TEXT NOT NULL DEFAULT '[]'          -- JSON EndpointHint[]
);

CREATE TABLE invites (
  nonce       TEXT PRIMARY KEY,
  created_at  TEXT NOT NULL,
  expires_at  TEXT NOT NULL,
  used_by     TEXT,
  used_at     TEXT
);

CREATE TABLE inbox (
  id          TEXT NOT NULL,
  to_device   TEXT NOT NULL,
  kind        TEXT NOT NULL,                      -- person | source | notice (a note from rtfc itself, e.g. an expired reply)
  project_id  TEXT,                               -- NULL = the shared inbox; set for source items and project-addressed messages (§7.6)
  -- person messages
  from_person TEXT,
  from_device TEXT,
  seq         INTEGER,
  reply_to    TEXT,
  origin      TEXT,                               -- human | auto
  hop         INTEGER,
  -- source items (one row per entity, coalesced)
  subscription_id TEXT,
  entity_key  TEXT,                               -- "jira:PAY-123"
  url         TEXT,
  events      TEXT,                               -- JSON history of SourceEvent, newest last
  draft       TEXT,                               -- output of a `prepare` run, or the auto-answer that was sent
  note        TEXT,                               -- a line for the human: why not auto-answered, what became of the reply
  auto_attempts INTEGER NOT NULL DEFAULT 0,       -- auto-answer runs started for this message
  -- common
  thread      TEXT,
  title       TEXT,
  body        TEXT NOT NULL,                      -- message text, or latest event summary
  sent_at     TEXT,
  received_at TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  state       TEXT NOT NULL,  -- parked | read | answered | dismissed | auto_running | auto_done | auto_failed
  handled_by  TEXT,
  handled_at  TEXT,
  PRIMARY KEY (id, to_device)
);
CREATE UNIQUE INDEX inbox_entity ON inbox (project_id, entity_key) WHERE kind = 'source';

CREATE TABLE accounts (                           -- secrets live in the keychain / keys dir
  name        TEXT PRIMARY KEY,
  type        TEXT NOT NULL,                      -- jira | confluence | bitbucket | github
  base_url    TEXT,
  created_at  TEXT NOT NULL
);

CREATE TABLE projects (
  id          TEXT PRIMARY KEY,
  root_path   TEXT NOT NULL UNIQUE,               -- normalized path key (lower case on Windows)
  name        TEXT
);

CREATE TABLE subscriptions (
  id          TEXT PRIMARY KEY,
  project_id  TEXT NOT NULL,
  account     TEXT NOT NULL,
  selector    TEXT NOT NULL,                      -- JSON: repo / jql / space …
  events      TEXT NOT NULL,                      -- JSON array
  mode        TEXT NOT NULL DEFAULT 'park',       -- park | prepare
  status      TEXT NOT NULL,                      -- pending_approval | active | disabled | error
  config_hash TEXT NOT NULL                       -- detects edits that need re-approval
);

CREATE TABLE source_cursors (                     -- one per (account, selector); shared across projects
  poll_key    TEXT PRIMARY KEY,
  cursor      TEXT NOT NULL,
  boundary_ids TEXT NOT NULL DEFAULT '[]',
  next_poll_at TEXT,
  last_error  TEXT
);

CREATE TABLE outbox (
  id          TEXT PRIMARY KEY,
  to_person   TEXT NOT NULL,
  to_device   TEXT,                               -- NULL = any active device
  kind        TEXT NOT NULL,                      -- reply | message | receipt | handled | contact_sync | device_list
  envelope    TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  expires_at  TEXT NOT NULL,
  attempts    INTEGER NOT NULL DEFAULT 0,
  state       TEXT NOT NULL                       -- pending | delivered | expired
);

CREATE TABLE sent (                               -- what left this device: receipts and expiries need something to update
  id          TEXT PRIMARY KEY,
  to_person   TEXT NOT NULL,
  to_device   TEXT,                               -- NULL = whichever device took it
  thread      TEXT,
  reply_to    TEXT,                               -- the inbox message this answered, if any
  origin      TEXT NOT NULL,                      -- human | auto
  kind        TEXT NOT NULL,                      -- message | reply
  body        TEXT NOT NULL,
  sent_at     TEXT NOT NULL,
  delivered_at TEXT,
  read_at     TEXT,
  expires_at  TEXT,                               -- while queued in the outbox
  state       TEXT NOT NULL,                      -- queued | delivered | read | expired
  project_id  TEXT                                -- where a reply to this lands (§7.6); NULL = the shared inbox
);

CREATE TABLE seq_out (to_device   TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
CREATE TABLE seq_in  (from_device TEXT PRIMARY KEY, max_seq  INTEGER NOT NULL);
```

A `meta` table holds `schema_version` and device-local flags such as `away`; the daemon migrates older files on open (history in implementation.md).

**Relaunch behavior:**

- Parked messages reappear in the status bar.
- `auto_running` rows are re-queued; the answer is regenerated once and the attempt is recorded (`auto_attempts`; two interrupted attempts make it `auto_failed`).
- The outbox pump resumes, and handshakes trigger it whenever a peer comes online.

**Retention:** prune `answered`, `dismissed` and `auto_done` messages after 30 days, and finished outbox entries after a day. Expire outbox entries after 7 days, leaving a local notice (an inbox row of kind `notice`); receipts expire silently. The pump runs every 30 seconds with jitter and whenever a contact connects, and delivers a batch to one device over one session.

---

## 14. Threat model

| Threat | Mitigation |
|---|---|
| A contact prompt-injects your Claude | Park by default; untrusted wrapping (§7.5); auto-answer only headless, read-only, scoped, with secret-path deny rules; no permission relay, ever |
| Someone on the LAN impersonates a contact | Mutual TLS pinned to contacts' person CAs; hostnames and discovery treated as hints only |
| Passive sniffing on the network | TLS on every transport |
| Invite token leaks | Single-use, 24 h expiry, inviter must be online, fingerprint words shown on both sides |
| Stolen device | Revoke from another device; device list version propagates on next connect; keys at rest 0600 or keychain |
| Auto-answer loops, spam | `origin: auto` never auto-answered, hop cap, per-contact rate limits, size caps |
| Replay | Dedupe by `id` and `seq`; TLS protects against replay within a session |
| You leak client code in outgoing messages | `send` requires approval, and the prompt shows the exact outgoing text |
| An injected message tries to change rtfc settings (e.g. enable auto-answer for the attacker) | Management is CLI-only, never an MCP tool; slash commands use `disable-model-invocation`; don't pre-approve `Bash(rtfc:*)` (§9.3) |
| Malicious text in a ticket, PR comment, or page | Park by default; untrusted wrapping; source auto mode is `prepare` only (headless, read-only, never posts); no push into sessions |
| A source token is stolen or misused | Keychain storage, narrowest scopes, device-local, added only in a real terminal, never seen by Claude; rtfc never writes to sources |
| Subscriptions are changed by a script, a teammate's tooling, or an injected Claude | Changes stay `pending_approval` until `rtfc sources approve` |
| Runaway usage from `prepare` runs | Per-project rate limits and a daily cap |
| Presence reveals when you're working | Visible only to accepted contacts; `/rtfc:away`; no "last seen" |

---

## 15. Keeping the door open to the internet

Invariants that v1 must respect. Violating any of them is what would turn internet support into a rewrite:

1. **Identity is keys.** Handles are local nicknames; IPs and hostnames are only hints.
2. **No trusted-network shortcuts.** Every connection is mutually authenticated and encrypted, including on the LAN.
3. **Security above the transport.** The same mutual-TLS handshake runs over a LAN socket, a VPN address, or a relay pipe, since every transport yields a `Stream`.
4. **Typed endpoint hints** in device records and invite tokens.
5. **Protocol versioning** in `hello` and the envelope from day one; unknown frame types are ignored.
6. **No synchronized clocks.** Order by `seq`, dedupe by `id`; expiries are evaluated by whoever issued them.
7. **Presence is a transport question.** `nobody_home` means the same thing on every transport.
8. **Servers never store messages.** Durability lives only on the endpoints (inbox, outbox), which a future relay inherits for free.

### Evolution path

**Stage A: overlay networks (Tailscale, WireGuard, ZeroTier). Almost no code.** Add `tcp:` hints with VPN addresses or MagicDNS names. Multicast mDNS generally doesn't cross these networks, so rely on the manual hints. Because of invariants 1–3, nothing else changes.

**Stage B: relay transport.** Each device holds one outbound WSS connection to a relay and authenticates with its device key. When A wants B, the relay pairs the two connections into a pipe and forwards **opaque TLS bytes**. It can't read them and doesn't store them. Presence is simply "B is connected to the relay". The friends' relay can run self-hosted on a homelab. The same relay can accept webhooks from sources and forward them to your devices as polling pokes (§10.1), without storing anything.

The main open design choice for the relay is how it limits who can request a pipe to whom:

- (a) **Relay forwards any request from a registered device; the recipient's daemon rejects non-contacts at the handshake.** The relay learns only "A tried B". Recommended for self-hosted relays.
- (b) **Recipients upload their allowlist to the relay, which enforces it.** Less abuse, but the relay operator learns the social graph.

**Stage C (optional): direct upgrade.** Use the relay for signaling and attempt NAT hole punching to get a direct pipe, falling back to the relay pipe.

### Build vs. adopt

**libp2p** bundles key-based peer IDs, discovery, encryption, relaying, and hole punching. The JavaScript implementation is mature; the .NET port is much less so. On this stack the better path is to build the relay yourself: it's a small ASP.NET Core app (a WebSocket endpoint that authenticates devices and pipes pairs of connections together) that fits in a Docker container on a homelab. Revisit hole punching (Stage C) only if relay traffic ever becomes a problem.

---

## 16. Stack

**.NET 10 / C#**, the same as rtfm and rtfq.

| Concern | Choice |
|---|---|
| MCP server | Hand-rolled JSON-RPC 2.0 over stdio and `JsonNode`, as in rtfq: AOT-clean, and small enough to own |
| Daemon host | `Microsoft.Extensions.Hosting` worker service |
| Local IPC | Kestrel minimal API on a Unix domain socket (`WebApplication.CreateSlimBuilder` for AOT) |
| Peer protocol | `TcpListener`/`TcpClient` + `SslStream` (mutual TLS), framing with `System.IO.Pipelines` |
| Certificates & crypto | `System.Security.Cryptography`: `ECDsa`, `CertificateRequest`, `X509Chain` with custom root trust. No third-party crypto. |
| Storage | `Microsoft.Data.Sqlite` (WAL) + hand-written SQL or Dapper.AOT. No EF Core: its Native AOT support is limited, and six tables don't need it. |
| JSON | `System.Text.Json` with source-generated contexts (required for AOT) |
| CLI | Hand-rolled dispatch, as in rtfm and rtfq |
| Auto-answer runner | `System.Diagnostics.Process` running `claude -p`, with timeout and output capture |
| mDNS (later) | Evaluate `Makaretu.Dns.Multicast` forks or similar |
| Tests | xUnit. Integration tests run two daemons in one test process with temp home directories and localhost ports, covering invite, send, nobody's-home, and the reply outbox. |

**Stay AOT-ready from day one.** Set `<IsAotCompatible>true</IsAotCompatible>` so the trimming and AOT analyzers flag reflection-heavy dependencies early. Confirm the MCP C# SDK's AOT status before committing to it for `rtfc mcp`.

**Distribution:**

- **GitHub Releases only.** A `vX.Y.Z` tag builds Native AOT binaries per platform (linux-x64, osx-arm64, win-x64), runs the end-to-end story against them, and publishes an archive per platform plus the dotnet-tool package for people who have the SDK anyway. Colleagues download an archive and put it on `PATH`; no .NET install is needed. Not on NuGet.
- **The plugin:** loaded with `claude --plugin-dir` from the repo for now; a marketplace entry (markdown and JSON only) can come later.

---

## 17. Phasing

Ordered by the primary value, agent-to-agent communication. Two Claudes talk as early as possible, then auto-answer arrives (the feature that makes rtfc more than chat), then reach grows beyond the office. Sources are a separate pipeline that shares only the inbox and the status bar, so they come last.

The order is cheap to change because the invariants (§15) and the `(person_id, device_id)` data model are in place from Phase 1, so no phase reworks an earlier one. **Early phases defer features, never guards.** Mutual TLS, the untrusted wrapping (§7.5), the frame and body size caps, and the CLI-only management boundary (§9.3) all ship in Phase 1.

Which phase shipped in which release is in `CHANGELOG.md`; Phase 4 was pulled ahead of 3 because replies that wait for the sender are worth more than VPN hints. Project-addressed messages (§7.6) came next, and brought two parts of 8a forward with them: session registration and the project-aware status line.

| Phase | Scope | Done when |
|---|---|---|
| **0: Spike** | ~~Two office laptops: raw TCP connect, mDNS browse, firewall behavior~~ Skipped: wired, same switch, SMB between machines already works | n/a. Only remaining first-run item: OS firewall prompt when `rtfcd` first listens |
| **1: Thin slice** | Two people, one device each (data model already person/device). `rtfc` binary + plugin wiring, configurable rtfc home, `init`, invite/accept over mutual TLS, `tcp:` hostname hints (no mDNS), daemon lifetime + SQLite, `send` with nobody's-home, park inbox, status bar, `contacts`, `inbox_list`, `inbox_open`, and `inbox_reply` while the sender is home (otherwise it reports `nobody_home`, like `send`) | Alex and Sasha exchange a question and answer without copy-paste, and a parked message survives a relaunch |
| **2: Auto-answer** | `auto_headless` with scope, deny rules, loop and rate guards. `remove` and `block` arrive here too: until now the worst a contact can do is park a message, but once your Claude answers on its own you need to be able to cut someone off | Sasha's question gets answered by Alex's scoped Claude while Alex is away from the keyboard |
| **3: Beyond the office** | Overlay networks (§15, Stage A): several `tcp:` hints per device, including VPN addresses and MagicDNS names, a way to choose which hints this device advertises, and invite tokens that carry them. Almost no code | Works with someone at home over Tailscale or similar, with no change to identity, storage, or tools |
| **4: Async** | Reply outbox with its pump and 7-day expiry notice (§7.2), read receipts, `away`, `rename`, `inbox_dismiss`, retention pruning | A reply written hours later reaches a sender who has since closed Claude Code, as soon as they're next home |
| **5: Multi-device** | Link/approve, device lists, fan-out, handled sync, revocation, contact sync, auto-owner rule | Alex's laptop and desktop act as one contact; revoking one works |
| **6: Relay** | Own ASP.NET Core relay transport (§15, Stage B), including the choice of who may request a pipe to whom | Works with someone at home with no VPN, and with no change to identity, storage, or tools |
| **7: Session auto** | `auto_session` via channels (research preview); C# SDK capability check or TypeScript shim (§12). Late on purpose: the preview may have settled by then | Opt-in; documented risks |
| **8: Sources** | 8a: pipeline (accounts, `rtfc.local.json`, approval, cursors, coalescing, `project forget`; project registration and the project-aware status line shipped early, with §7.6) with **one adapter: Bitbucket review requests**. 8b: Jira, then GitHub and Confluence. 8c: `prepare` mode. 8d: webhook pokes through the relay (§10.1; needs Phase 6) | A Bitbucket review request shows up as 🔀 in the right project's status bar within 2 minutes, appears exactly once, and survives a relaunch |

---

## 18. Open questions

1. **Daemon lifetime.** Session-bound (current design: home = Claude Code open) or a login item that is always home? More pressing since Phase 4: the outbox is delivered by the daemon, so a queued reply leaves only while both sides have a session open at the same time. The login item would let auto-answer work with no session open, but changes what "home" means.
2. **Sibling-device delivery.** Should a message that landed on the desktop be offered to the laptop when it comes online, or does an inbox live where it landed? Currently only handled state syncs.
3. **Attachments.** Diffs and files are the obvious next ask. What size limit, and are they ever allowed into auto-answer?
4. **Groups.** Is "ask the team" a list of contacts with fan-out, or a first-class group concept?
5. **Read receipts default.** On (current; off per contact with `rtfc receipts <contact> off`) or off?
6. **Handle collisions.** How to display two contacts who both chose "alex" as their suggested handle.
7. **Daemon lifetime matters more with sources.** Polling only happens while some session is open, so items catch up late (not lost) after the laptop has been closed. A login-item daemon would poll all day. Is that wanted?
8. **Bitbucket flavor.** Cloud or Data Center first? The APIs differ.
9. ~~**Project-addressed messages.**~~ Resolved on 2026-09-28: yes, when the sender names the project explicitly, and a message for a project that doesn't exist lands in the shared inbox. See §7.6.
10. **Account sync.** Should accounts (without secrets) sync between your own devices, so a new device only needs tokens re-entered?
