# rtfc — Relay Tool For Contacts

Send a message from your Claude Code to someone else's Claude Code, instead of
copy-pasting Claude output between terminals and chat windows.

- **Contacts are mutual.** You invite, they accept. No accepted contact, no message.
- **Messages park.** Incoming messages wait in an inbox and show in your status bar
  until you look. They survive relaunches.
- **"Nobody's home" is immediate.** If none of the recipient's devices is online, you
  are told straight away. Nothing is queued behind your back.
- **Auto-answer is opt-in, per contact** (coming in Phase 2). A separate, read-only,
  scoped Claude run answers for you. It never touches your working session.
- **Sources** (coming last): Jira, Confluence, Bitbucket and GitHub notifications in the
  same inbox, scoped to the project they belong to.

LAN first, with mutual TLS on every connection, designed so that reaching someone over
a VPN or a relay is a new transport rather than a rewrite. Sibling of
[rtfm](https://github.com/a7ex-turcan/rtfm) and [rtfq](https://github.com/a7ex-turcan/rtfq).

**Status: 0.1.0, Phase 1.** Two people on one LAN exchange messages between their Claude
Code sessions. See [`CHANGELOG.md`](CHANGELOG.md) for what is in and what is not, and
[`docs/spec.md`](docs/spec.md) for the design.

---

## Getting started

### What you need

On **both** machines:

- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- [Claude Code](https://claude.com/claude-code) with plugin support (2.1 or later).
- A network path between them: the same office LAN, where either hostnames resolve or
  you know the IP addresses. TCP port **47821** must be reachable (configurable).

### 1. Install the `rtfc` command

There are no published packages yet, so build it from the repo:

```bash
git clone https://github.com/a7ex-turcan/rtfc.git
cd rtfc
dotnet pack src/Rtfc -c Release -o artifacts
dotnet tool install -g rtfc --add-source ./artifacts
rtfc --version
```

`dotnet tool install` puts `rtfc` in `~/.dotnet/tools`; make sure that directory is on
your `PATH` (the installer tells you if it isn't). The plugin calls `rtfc` from `PATH`, so
this matters.

To upgrade later: pull, `dotnet pack` again, then `dotnet tool uninstall -g rtfc` and
install again.

### 2. Create your identity

Once per machine:

```bash
rtfc init
# or, explicitly:
rtfc init --handle alex --device laptop --port 47821 --hint-host alex-laptop.local
```

This creates `~/.claude/rtfc/` with:

| File | What |
| --- | --- |
| `keys/person.p12` | Your **person CA**: who you are. Long-lived. |
| `keys/device.p12` | This device's certificate, issued by your person CA. |
| `rtfc.db` | Contacts, devices, the inbox (SQLite). |
| `config.json` | The port and the hosts advertised in your invites. |

Both key files are readable only by you. **Back up `keys/`**: if you lose it you are a
new person and every contact has to re-invite you.

- `--handle` is the name you suggest to contacts. They store it as a local nickname they
  can change; it is never your identity.
- `--device` names this machine (`laptop`, `desktop`). Defaults to the hostname.
- `--hint-host` sets the hostnames or IPs your invites tell people to connect to. By
  default it is your hostname plus every non-loopback IPv4 address. If the other person
  can't reach any of those, set the right one here or in `config.json` (see below).

### 3. Load the plugin in Claude Code

```bash
claude --plugin-dir /path/to/rtfc/plugin
```

(A marketplace entry comes later; for now the plugin is loaded per session from the repo.)

The plugin adds:

- An MCP server, `rtfc mcp`, with five tools: `contacts`, `send`, `inbox_list`,
  `inbox_open`, `inbox_reply`.
- A SessionStart hook, `rtfc daemon ensure`, that starts the daemon when a session opens.
- Slash commands: `/rtfc:init`, `/rtfc:invite`, `/rtfc:accept <token>`, `/rtfc:contacts`,
  `/rtfc:inbox`.

### 4. Show parked messages in the status line

Add to `~/.claude/settings.json`:

```json
{ "statusLine": { "type": "command", "command": "rtfc statusline", "refreshInterval": 5 } }
```

You'll see `📨 1 · sasha` when something is waiting, and nothing otherwise. If you already
have a status line script, call `rtfc statusline` from it and append its output;
`refreshInterval` (seconds) keeps the counter current while you're idle.

### 5. Become contacts

| You | Your contact |
| --- | --- |
| `/rtfc:invite` prints a token like `rtfc1_eyJ2…`. Send it over any chat. **Keep Claude Code open**: accepting needs you home. | `/rtfc:accept rtfc1_eyJ2…` |
| Both of you see the same fingerprint, e.g. `1d36 19af 68e1 8af1`. Compare it in person if you want to be sure it's really them. | |

Tokens are single-use and expire after 24 hours. If the other person accepts while you
have no session open, they're told nobody's home and the token stays valid for later.

### 6. Talk

- **Send:** just ask. *"Send this to sasha: how does your retry policy handle poison
  messages?"* Claude calls the `send` tool, and Claude Code shows you the exact text before
  it leaves your machine. **Don't pre-approve `send`**: that prompt is where you catch
  client code going out. `sasha/laptop` addresses one device.
- **Receive:** the status line shows `📨 1 · sasha`. `/rtfc:inbox` lists what's parked,
  and Claude can open one and answer it. *"Reply that we use a dead-letter queue after
  five attempts."*
- **Who's home:** `/rtfc:contacts`.

Messages are delivered only while the other person has Claude Code open. If they don't,
you're told `nobody_home` right away and nothing is queued. In this release the same
applies to replies: answer while the sender is home, or send it later.

---

## Important to know

### How it works, briefly

- **The daemon.** One `rtfcd` per machine owns your keys, the database, the listener on
  port 47821 and a local API on `~/.claude/rtfc/rtfcd.sock`. Every open Claude Code
  session holds a lease on it; thirty seconds after the last one closes, the daemon exits.
  That's what "home" means: Claude Code is open on that machine.
- **Identity is keys, not addresses.** Contacts are pinned by their person CA. Hostnames
  and IPs are only hints for where to try; the TLS handshake is the only proof of who
  answered.
- **Every connection is mutual TLS**, including on the LAN. The OS trust store is never
  consulted. Someone on your network who reaches the port gets a session that can do
  exactly one thing: present an invite token you issued.
- **Delivered means durable.** The receiver acknowledges only after the message is in its
  database.
- **Messages from contacts are data, not instructions.** Claude sees them wrapped as
  `<contact_message untrusted="true">` and is told to confirm with you before doing
  anything a message asks. Claude Code's normal permission prompts remain the backstop.
- **Changing who can reach you is never a tool.** Invite, accept, and later block and
  auto-answer, are CLI commands that only run when you type the slash command. Don't
  pre-approve `Bash(rtfc:*)` in your permissions, or Claude could run them for you.

### The daemon, by hand

```bash
rtfc daemon status          # running? which port, how many leases
rtfc daemon stop            # stop it (it restarts on the next session)
rtfc daemon run --stay      # run in the foreground, never idle-exit; logs to the terminal too
tail -f ~/.claude/rtfc/rtfcd.log
curl --unix-socket ~/.claude/rtfc/rtfcd.sock http://rtfcd/v1/status
```

The first time the daemon listens, macOS and Windows show a firewall prompt. Allow it, or
nobody can reach you.

### Configuration

`~/.claude/rtfc/config.json`:

```json
{
  "port": 47821,
  "hintHosts": ["alex-laptop.local", "10.0.0.5"]
}
```

`hintHosts` is what your future invites and accepts advertise. Changing either value
takes effect when the daemon restarts (`rtfc daemon stop`). Contacts you already have keep
the hints they learned when you became contacts; in this release the way to refresh them
is a new invite.

`RTFC_HOME` moves the whole directory somewhere else. Tests and the e2e script use it so
they never touch your real one.

### Troubleshooting

| Symptom | Look at |
| --- | --- |
| `nobody_home` but they say they're online | Do they have a Claude Code session open *with the plugin loaded*? `rtfc daemon status` on their side. Can you reach their port: `nc -vz their-host 47821`? Firewall prompt dismissed? |
| `/rtfc:accept` says nobody's home | The inviter needs a session open. Check the hints in their token reach you: the invite output lists them. If hostnames don't resolve on your LAN, they should `rtfc init --hint-host <ip>` (or edit `config.json`) and invite again. |
| `rtfc: no identity yet` | `rtfc init` on this machine. |
| The plugin's tools say the daemon could not be started | `~/.claude/rtfc/rtfcd.log`. Common cause: `rtfc` isn't on the `PATH` Claude Code sees. |
| Port already in use | Another rtfcd, or something else on 47821: `rtfc daemon status`, or change `port` in `config.json` and re-invite. |
| The status line never changes | `refreshInterval` set? `rtfc statusline` prints nothing when nothing is parked; try `cat ~/.claude/rtfc/status.json`. |
| A message shows an odd `</contact_message​>` inside | Someone tried to close the untrusted wrapper from inside a message. It was defused; treat the message with suspicion. |

### Limitations in 0.1.0

- One device per person. Multi-device comes in Phase 5.
- Replies need the sender to be home. The outbox that delivers them later is Phase 4.
- No read receipts, no `away`, no `remove` or `block` yet (Phase 2 brings the last two,
  with auto-answer). Until then, removing a contact means editing the database.
- LAN only, or any network where the hosts in your hints are reachable. Tailscale-style
  overlays are Phase 3 and need no code beyond hints.
- On Windows the daemon is started without a proper detach; if it dies with your session,
  `rtfc daemon run --stay` in a separate terminal is the workaround.
- Fingerprints are hex groups, not words.

### Roadmap

| Phase | What |
| --- | --- |
| 1 ✅ | Two people exchange messages |
| 2 | Auto-answer: a scoped, read-only, headless Claude answers a contact for you. `remove`, `block`. |
| 3 | Beyond the office: VPN and Tailscale addresses as hints |
| 4 | Async: the reply outbox, read receipts, `away`, `rename`, dismiss, retention |
| 5 | Multi-device: one person, several machines |
| 6 | A self-hosted relay, for people with no shared network |
| 7 | Auto-answer inside a live session, via Claude Code channels |
| 8 | Sources: Jira, Confluence, Bitbucket, GitHub |

---

## Development

```bash
dotnet build
dotnet test                       # unit + integration tests (real TLS on loopback, real daemon on a real socket)
dotnet format --verify-no-changes # what CI checks
scripts/e2e.sh                    # two daemons on this machine, the whole Phase 1 story, ~1 minute
claude plugin validate plugin
```

CI runs the build and tests on Linux, macOS and Windows, checks formatting, validates the
plugin's JSON, checks the version is declared consistently, and fails if a skill that runs
`rtfc` can be invoked by the model or holds a blanket `Bash(rtfc:*)` permission.

| Path | What |
| --- | --- |
| `src/Rtfc/` | The single `rtfc` executable: `Identity/` keys and certificates · `Storage/` SQLite · `Protocol/` frames · `Net/` transport and TLS · `Core/` the node · `Daemon/` IPC · `Mcp/` the stdio server · `Cli/` the commands |
| `tests/Rtfc.Tests/` | xUnit v3 |
| `plugin/` | The Claude Code plugin: manifest, `.mcp.json`, the hook, one skill per slash command |
| `docs/spec.md` | The design spec, the source of truth |
| `AGENTS.md` | Guidance for AI agents working in this repo; `CLAUDE.md` points at it |
| `CHANGELOG.md` | What changed, per version |

## License

MIT. See [`LICENSE`](LICENSE).
