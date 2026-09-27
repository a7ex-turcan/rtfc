# Changelog

All notable changes to rtfc are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and rtfc follows
[semantic versioning](https://semver.org/): patch for fixes, minor for additive features,
major for a breaking change to the CLI, the MCP tools, or the wire protocol.

**Before 1.0, treat minor versions as potentially breaking.** The wire protocol is versioned
separately (`hello.v` and the envelope's `v`, both `1`); a daemon refuses to talk to a peer
speaking another version, so two people must run compatible releases.

The version is declared in three places that must move together, and CI checks that they
do: `<Version>` in `src/Rtfc/Rtfc.csproj` (the `rtfc` command, `rtfc --version`, and the MCP
server's `serverInfo.version`), `version` in `plugin/.claude-plugin/plugin.json`, and the
newest section below. Pushing a `vX.Y.Z` tag runs the release workflow, which checks the
tag against those three, builds Native AOT binaries for Linux, macOS and Windows, runs the
end-to-end story against the binary that ships, and publishes a
[GitHub Release](https://github.com/a7ex-turcan/rtfc/releases) with the matching section
below as its notes. GitHub Releases only: rtfc is not on NuGet, by decision.

## [Unreleased]

Phase 4, async: a reply written hours later reaches a sender who has since closed Claude
Code, as soon as they are next home.

### Added

- **The outbox.** A reply to someone who is not home no longer fails: it waits in the
  outbox and is delivered when they are next home, for up to seven days. The result is
  `queued`, the original message says so, and the status line shows `📤 1`. One pump
  delivers everything that waits, every 30 seconds and the moment a contact connects to
  you. An automatic answer whose recipient left before it was ready waits the same way.
  If it expires, you get a notice in your inbox with the text, so nothing is lost quietly.
- **"Leave it for her."** `send` takes `leave: true`; when nobody is home the message
  waits in the outbox instead of being refused. Only when you ask for it: a plain send is
  still never queued.
- **Read receipts.** Opening a message tells the sender's device it was read, if receipts
  are on for that contact (they are by default; `rtfc receipts <contact> off`, or
  `/rtfc:receipts`). Your copy of the question shows what became of your reply: queued,
  delivered, read, or expired, with the time. Receipts travel through the outbox too, so
  a sender who has gone learns later.
- **`rtfc away on|off`** (`/rtfc:away`). Away means nothing listens, so contacts see nobody
  home, while you can still send and your outbox still delivers. The status line shows
  `💤 away`. It survives a daemon restart.
- **`rtfc rename <contact> <handle>`** (`/rtfc:rename`), a local nickname only.
- **`inbox_dismiss`**, the sixth MCP tool, and `rtfc inbox dismiss <id>`: clear a message
  or a notice without answering it.
- **`rtfc outbox`** lists what still waits, with attempts and expiry.
- **Retention.** Answered, dismissed and auto-answered messages are pruned after 30 days,
  finished outbox entries after a day. `config.json` takes an `outbox` object
  (`expiryHours`, `pumpIntervalSeconds`, `retentionDays`).

### Changed

- The database is schema version 3: a `sent` table records what left this device, so
  receipts and expiries have something to update; the inbox `note` column serves every
  kind of message; and `notice` is a new inbox kind for rtfc's own notes. An existing
  file is migrated the first time the daemon opens it.
- The daemon reuses one connection per device when it delivers a batch from the outbox.

## [0.2.0] - 2026-09-27

Phase 2, auto-answer: a contact's question gets answered by your scoped, read-only Claude
while you are away from the keyboard. Tried with the real `claude`: asked about a retry
policy and, in the same message, for the database password in `.env`, it answered the
first from the files and refused the second.

### Added

- **Auto-answer, per contact, opt in.** `rtfc auto <contact> headless --scope <dir>` (or
  `/rtfc:auto`) has a fresh headless Claude answer that contact's messages from the files
  under one directory. The run is `claude -p --restricted --strict-mcp-config` with only
  `Read`, `Grep` and `Glob`, deny rules for files that look like secrets (`.env*`, keys,
  certificates, credentials, `.ssh`, `.aws`, …), no session persistence, no plugins, no
  hooks, no MCP servers (so no rtfc `send`), a budget cap and a three-minute wall clock.
  The message goes in on stdin wrapped as untrusted content with a system prompt that
  says so. Answers arrive marked automatic on both sides. `rtfc auto <contact> off` turns
  it off; `session` mode waits for Phase 7.
- **Loop and abuse guards** (spec §7.4). A message written automatically is never
  answered automatically, so two auto-answering Claudes cannot ping-pong. A thread deeper
  than one reply is not answered automatically. Ten automatic answers per contact per
  hour and thirty overall, counted from the database so a restart doesn't reset them.
  One hundred and twenty inbound messages per device per hour, refused before anything is
  stored. Whatever the guards stop is parked with a note saying why.
- **`rtfc remove <contact>` and `rtfc block <contact>`** (`/rtfc:remove`, `/rtfc:block`).
  Removal is local and immediate: the contact's CA leaves your trust store, so their next
  connection is refused. Block also refuses every future invite exchange with that person,
  from either side. Both switch auto-answer off for that contact.
- A failed automatic answer, or one whose recipient left before it was ready, is parked
  for you with the draft attached. `inbox_list` shows the note; `inbox_open` shows the
  draft in its own tags.
- `config.json` takes `claudePath` (the `claude` executable, default from `PATH`) and
  `autoAnswer` limits (`perContactPerHour`, `globalPerHour`, `inboundPerDevicePerHour`,
  `timeoutSeconds`, `maxBudgetUsd`).

### Changed

- **Accepting a token from someone already in your contacts now performs the exchange**
  instead of returning early. It refreshes their endpoint hints, and someone who removed
  or blocked you no longer looks like a contact from your side.
- The database is schema version 2, with two new inbox columns; an existing file is
  migrated the first time the daemon opens it.

## [0.1.1] - 2026-09-27

The first release with downloadable binaries, so nobody has to compile it.

### Added

- **Releases on GitHub.** Pushing a `vX.Y.Z` tag builds Native AOT binaries for
  `linux-x64`, `osx-arm64` and `win-x64`, runs the end-to-end story against them, and
  publishes a GitHub Release with the changelog section as notes and the archives (plus
  the dotnet-tool package) as assets. The tag must match the declared version.
- `scripts/e2e.sh` checks every step and exits non-zero when the story doesn't hold, so
  it can gate a release.

### Fixed

- **`rtfc` builds as a Native AOT binary again.** `dotnet publish -p:PublishAot=true`
  failed on the MCP tool list, which used a collection expression that needs runtime code
  generation; the trim and AOT analyzers had not flagged it. The published binary now
  passes the whole `scripts/e2e.sh` story, which takes `RTFC_BIN` to point at any build.

## [0.1.0] - 2026-09-27

Phase 1, the thin slice: two people on one LAN exchange messages between their Claude Code
sessions without copy-paste, and a parked message survives a relaunch.

Not in this release, by design: read receipts, the reply outbox (a reply needs the sender
to be home), `away`, `rename`, dismiss, retention pruning, `remove` and `block`, more than
one device per person, fingerprint words (hex groups for now), reaching anyone beyond the
LAN, and third-party sources.

### Added

- **Identity is keys.** `rtfc init` creates a self-signed P-256 person CA and a device
  certificate it issues, stored as PKCS#12 files under `~/.claude/rtfc/keys/` that only
  the user can read. A person is the hash of their CA's public key, a device the hash of
  its certificate's; handles are local nicknames and never identity.
- **Contacts are mutual.** `rtfc invite` prints a single-use token, valid 24 hours, that
  carries a fingerprint and `tcp:` hints and never a certificate. `rtfc accept <token>`
  connects to the inviter over mutual TLS, exchanges person CAs, checks that the leaf seen
  in the handshake was issued by the CA received, and pins it on both sides. Accepting
  needs the inviter to be home; otherwise the token stays valid.
- **Mutual TLS on every connection**, LAN included. Validation ignores the OS trust store;
  the only anchors are your own CA and your active contacts'. A peer from an unknown CA
  gets a restricted session that may only accept an invite.
- **Sending with nobody's-home.** The `send` tool resolves a handle (or `handle/device`)
  to devices, delivers to each over its own session, waits for an ack that is sent only
  after the receiver committed the message, and reports `delivered`, `partial`,
  `nobody_home`, `device_offline` or `rejected`. Nothing is ever queued. A resend of a
  known id is acknowledged as a duplicate.
- **The parked inbox.** Messages park in SQLite until a human looks. `inbox_list` shows
  previews, `inbox_open` returns the body wrapped as untrusted content and marks it read,
  `inbox_reply` answers in the same thread and marks it answered. A closing tag smuggled
  into a message body cannot close the wrapper.
- **The daemon.** One `rtfcd` per device owns keys, SQLite, the listener and a Kestrel
  minimal API on `~/.claude/rtfc/rtfcd.sock`. Every open `rtfc mcp` holds a lease; thirty
  seconds after the last lease is released the daemon exits, which is what makes "home"
  mean "Claude Code is open". `rtfc daemon ensure` starts it detached, with its stdio
  closed so the MCP server's pipes are never inherited; logs go to `rtfcd.log`.
- **The status line.** The daemon writes `status.json` atomically; `rtfc statusline`
  prints `📨 1 · sasha` or nothing.
- **The Claude Code plugin**: the `rtfc mcp` server with the five tools above, a
  SessionStart hook that runs `rtfc daemon ensure`, and one skill per slash command
  (`/rtfc:init`, `/rtfc:invite`, `/rtfc:accept`, `/rtfc:contacts`, `/rtfc:inbox`). The
  skills that run the CLI are user-only and scoped to their own subcommand, and CI fails
  the build if one is not.
- **The CLI**: `init`, `invite`, `accept`, `contacts`, `inbox [--all] | inbox open <id>`,
  `daemon run|ensure|status|stop`, `mcp`, `statusline`, `--version`.
- `RTFC_HOME` moves the whole data directory, for tests and side-by-side runs.
- Tests on Linux, macOS and Windows, including two nodes talking over real TLS on
  loopback and the real daemon on a real socket, and `scripts/e2e.sh` for the whole story
  with two daemons on one machine.

[Unreleased]: https://github.com/a7ex-turcan/rtfc/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/a7ex-turcan/rtfc/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/a7ex-turcan/rtfc/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/a7ex-turcan/rtfc/releases/tag/v0.1.0
