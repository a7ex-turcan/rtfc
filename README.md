# rtfc — Relay Tool For Contacts

Send a message from your Claude Code to someone else's Claude Code, instead of
copy-pasting Claude output between terminals and chat windows.

- **Contacts are mutual.** You invite, they accept. No accepted contact, no message.
- **Messages park.** Incoming messages wait in an inbox and show in your status bar
  until you look. They survive relaunches.
- **"Nobody's home" is immediate.** If none of the recipient's devices is online, you
  are told straight away.
- **Auto-answer is opt-in, per contact.** A separate, read-only, scoped Claude run
  answers for you. It never touches your working session.
- **Sources.** Jira, Confluence, Bitbucket and GitHub notifications land in the same
  inbox, scoped to the project they belong to.

LAN first, with mutual TLS on every connection, designed so that reaching someone over
a VPN or a relay is a new transport rather than a rewrite.

## Status

**Phase 1 works: two people on one LAN exchange messages between their Claude Code
sessions.** Invite and accept, send, a parked inbox with a status-line counter, open and
reply. Next is auto-answer. The design and the phasing are in [`docs/spec.md`](docs/spec.md).

## Try it

You need the .NET 10 SDK and Claude Code on both machines.

```bash
# 1. Build and install the `rtfc` command
dotnet pack src/Rtfc -c Release -o artifacts
dotnet tool install -g rtfc --add-source ./artifacts

# 2. Create your identity (once per machine)
rtfc init                       # or: rtfc init --handle alex --device laptop --port 47821

# 3. Load the plugin in Claude Code
claude --plugin-dir /path/to/rtfc/plugin
```

Then, inside Claude Code:

| You | Your contact |
| --- | --- |
| `/rtfc:invite` and send the token over any chat | `/rtfc:accept rtfc1_…` |
| "send this to sasha" (the `send` tool asks you to approve the exact text) | The status line shows `📨 1 · alex`; `/rtfc:inbox` shows it |
| `/rtfc:contacts` shows who is home | "reply that we use a dead-letter queue" |

Messages are delivered only while the other person has Claude Code open; otherwise you
are told nobody is home and nothing is queued. The first time the daemon listens, the OS
firewall will ask.

**Status line.** Add to `~/.claude/settings.json`:

```json
{ "statusLine": { "type": "command", "command": "rtfc statusline", "refreshInterval": 5 } }
```

If you already have a status line script, call `rtfc statusline` from it.

**Reaching each other.** Invite tokens carry `tcp:<hostname>:<port>` hints built from
the machine's hostname and its IPv4 addresses. If your hostnames don't resolve, pass the
right ones at init time (`--hint-host 10.0.0.5`) or edit `~/.claude/rtfc/config.json`.

## Building

```bash
dotnet build
dotnet test
scripts/e2e.sh        # two daemons on this machine, the whole story, about a minute
```

## Layout

| Path | What |
| --- | --- |
| `src/Rtfc/` | The single `rtfc` executable: daemon, MCP server, status line, management CLI |
| `tests/Rtfc.Tests/` | xUnit v3 tests, including two-node integration tests over real TLS |
| `plugin/` | The Claude Code plugin: JSON and markdown that call `rtfc` from PATH |
| `docs/spec.md` | The design spec |

## License

MIT. See [`LICENSE`](LICENSE).
