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
newest section below. Releases are tagged `vX.Y.Z`. There is no release workflow yet:
packages are built locally with `dotnet pack`.

## [Unreleased]

Nothing yet. Next is Phase 2, auto-answer: a contact's question answered by a scoped,
read-only, headless Claude run while you are away from the keyboard, plus `remove` and
`block`.

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

[Unreleased]: https://github.com/a7ex-turcan/rtfc/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/a7ex-turcan/rtfc/releases/tag/v0.1.0
