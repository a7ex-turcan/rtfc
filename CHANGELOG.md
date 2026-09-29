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

Nothing yet. Next is Phase 5, one person on several devices.

## [0.7.1] - 2026-09-29

### Fixed

- A message sent the instant a session opened could fail with `closed_before_ack` when the
  receiver was slow to read the hello: the receiving side lost the first bytes of the frame
  behind it and dropped the session. The hello now goes through the session's own reader,
  so nothing behind it is lost.
- A problem recording the addresses a contact sends in its hello no longer closes the
  session; the message still arrives, and the problem is logged.

## [0.7.0] - 2026-09-29

Phase 3, beyond the office: contacts on a VPN or Tailscale.

### Added

- **`rtfc hints`** (and `/rtfc:hints`) shows the addresses your invites tell people to
  connect to, and `add`, `remove` and `auto` change them: add a VPN address or a Tailscale
  name, drop one nobody can reach, or go back to auto-detection. Changes apply to the running
  daemon at once and go into new invites.
- **Contacts refresh each other's addresses whenever they talk.** Every session's `hello` now
  carries the sender's hints, so a contact who moves to a VPN is reachable the next time
  either side sends, with no new invite.
- `rtfc contacts` shows each device's hints.

### Changed

- **All of a device's addresses are tried at once**, a quarter second apart, and the first to
  answer wins. They used to be tried one after another with a three-second timeout each, so a
  contact's office address timed out before their VPN address was tried, and the "who's home"
  check called them away.
- **Auto-detected hints no longer include link-local addresses** (`169.254.x.x`, one per idle
  adapter on Windows) or loopback, each of which cost contacts a connection attempt.

A 0.6.x daemon ignores the hints in a hello and keeps working; the wire protocol stays at
version 1.

## [0.6.0] - 2026-09-29

Phase 7, experimental: a contact's message comes into your own session, you accept or
decline, and Claude acts.

### Added

- **Session mode for auto-answer.** `/rtfc:auto sasha session`, run inside a Claude Code
  session started with `--dangerously-load-development-channels plugin:rtfc@rtfc`,
  makes Sasha's messages arrive in that session as they come in, even while it is idle.
  Claude gives you the gist and asks **Accept** or **Decline**; on Accept it does what the
  message asks, with your repo, your tools and the session's normal permissions, and answers
  with `inbox_reply`. `--all session` does it for every contact you have. It uses Claude
  Code's channels research preview; a Team or Enterprise organisation has to allow channels.
- **Nothing runs until you accept.** The plugin's new hooks (`rtfc hook`) block every tool
  but the Accept/Decline question from the moment a contact's message lands in the session
  until you accept, in bypass and auto mode too, so a message cannot drive your Claude on
  its own. rtfc still never lets a contact approve anything in your session.
- The same guards as headless auto-answer apply, and a pushed message also waits in your
  inbox, because rtfc cannot tell whether the session received it.
- **Install the plugin once, for every session.** The release archive is now a Claude Code
  marketplace: `claude plugin marketplace add <the extracted folder>` and
  `claude plugin install rtfc@rtfc` replace `--plugin-dir` on every launch, and session mode
  names the plugin as `plugin:rtfc@rtfc`. To upgrade, extract the new release and run
  `claude plugin marketplace update rtfc && claude plugin update rtfc@rtfc`.

## [0.5.2] - 2026-09-28

Includes the fixes of 0.5.1, which was tagged but never published: its release build hit a
flaky test.

### Fixed

- **On Windows, `contacts` no longer fails with a socket error.** Windows takes about two
  seconds to report that a machine refused a connection, which is as long as the "who's
  home" check waits; when the refusal arrived just after the check gave up, the error
  escaped instead of the contact showing as not home.
- **An open session keeps the daemon alive across a restart** (from 0.5.1). A daemon
  restarted by something other than the session, such as `rtfc daemon stop` and a CLI
  command after a config change, used to exit 30 seconds later because the session took no
  lease on it until its next tool call. Now it does within a couple of seconds.
- **Stopping the daemon is prompt** (from 0.5.1). With a session open, it used to keep
  running, and keep its port, for up to 30 seconds after being told to stop.

## [0.5.1] - 2026-09-28

Tagged, never published; these fixes shipped in 0.5.2.

### Fixed

- **An open session keeps the daemon alive across a restart.** If the daemon was restarted
  by something other than the session, for example `rtfc daemon stop` and a CLI command to
  pick up a config change, the session did not take a lease on the new daemon until its
  next tool call, so the daemon exited 30 seconds later and contacts saw nobody home. Now
  the session holds a lease on it within a couple of seconds.
- **Stopping the daemon is prompt.** With a session open, it used to keep running, and
  keep its port, for up to 30 seconds after being told to stop.

## [0.5.0] - 2026-09-28

Auto-answer for everyone at once, and without having to name a directory.

### Added

- **`rtfc auto --all`** (and `/rtfc:auto --all`) sets auto-answer for every contact you have
  now, in one go; `--all off` turns it off for everyone. Contacts you accept later still
  park until you turn it on for them.

### Changed

- **`--scope` is optional.** Without it, `rtfc auto <contact> headless` lets the answering
  Claude read the directory Claude is running in, and says which one. It refuses that
  default when it is a drive root, your home folder or above it, or inside `~/.claude`, and
  asks for `--scope` instead.

## [0.4.0] - 2026-09-28

Messages addressed to a project: "send this to sasha, in payments-api".

### Added

- **Messages addressed to a project.** *"Send this to sasha, in payments-api"*: the `send`
  tool takes an optional `project`, the folder name of one of the recipient's projects, and
  the message lands in that project on their side instead of in the shared inbox. Their
  sessions in that project count and list it as usual; their other sessions show a pointer,
  `📨 1 · alex → payments-api`. A name that matches none of their projects lands in the
  shared inbox with a note, and the sender can't tell the difference. Without a project,
  nothing changes.
- **Threads stay in their project.** The answer to a message you sent to a project lands in
  the project you sent it from, and the conversation keeps to those two projects from then
  on. Each side decides this from its own records; no project name is sent back.
- `inbox_list` takes `scope`: `project` (the default) lists the shared inbox and this
  session's project and counts what waits in your other projects; `all` lists everything.
  `rtfc inbox` lists everything and marks each message's project.
- Each session tells the daemon which project it runs in: the git root around the
  directory Claude Code started in.

The wire protocol stays at version 1: the envelope's new `project` field is optional, and a
0.3.x daemon ignores it and delivers to the shared inbox.

## [0.3.3] - 2026-09-28

### Fixed

- **On Windows, the status line shows 📨 again, and what you send keeps its accents.** rtfc
  used the console's legacy code page (437 on an English Windows) on the pipes Claude Code
  talks to it through, so the status line read `?? 1 � alex`, and a message with `é`, `ș`,
  Cyrillic or an emoji was mangled before it left your machine. Pipes are now UTF-8 whatever
  the console's code page.

## [0.3.2] - 2026-09-28

### Fixed

- **On Windows, a Claude Code session no longer hangs when it starts the daemon.** The
  daemon inherited the output pipe of the SessionStart hook that started it and held it
  open, so Claude Code kept waiting on the hook, and whatever you typed first, such as
  `/rtfc:contacts`, sat in the queue for minutes. The same leak would have kept the MCP
  server's output open after it exited.

## [0.3.1] - 2026-09-28

### Fixed

- **The release archive now contains the plugin.** The README told a colleague to load
  `plugin/` from a path that only existed in a clone of the repo. One download is now
  enough: `claude --plugin-dir ~/.local/share/rtfc/plugin`.
- The README's getting-started section defines "home" before using it, adds a sanity
  check after install, moves the firewall prompt to where it happens, and no longer says
  an automatic answer parks when its recipient has gone (it waits in the outbox since
  0.3.0).

## [0.3.0] - 2026-09-27

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

[Unreleased]: https://github.com/a7ex-turcan/rtfc/compare/v0.7.1...HEAD
[0.7.1]: https://github.com/a7ex-turcan/rtfc/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/a7ex-turcan/rtfc/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/a7ex-turcan/rtfc/compare/v0.5.2...v0.6.0
[0.5.2]: https://github.com/a7ex-turcan/rtfc/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/a7ex-turcan/rtfc/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/a7ex-turcan/rtfc/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/a7ex-turcan/rtfc/compare/v0.3.3...v0.4.0
[0.3.3]: https://github.com/a7ex-turcan/rtfc/compare/v0.3.2...v0.3.3
[0.3.2]: https://github.com/a7ex-turcan/rtfc/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/a7ex-turcan/rtfc/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/a7ex-turcan/rtfc/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/a7ex-turcan/rtfc/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/a7ex-turcan/rtfc/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/a7ex-turcan/rtfc/releases/tag/v0.1.0
