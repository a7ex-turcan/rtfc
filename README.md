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

**Pre-alpha: nothing works yet.** The repository is scaffolded and Phase 1 (the MVP) is
next. The design is in [`docs/spec.md`](docs/spec.md).

## Building

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/Rtfc -- --version
```

## Layout

| Path | What |
| --- | --- |
| `src/Rtfc/` | The single `rtfc` executable: daemon, MCP server, status line, management CLI |
| `tests/Rtfc.Tests/` | xUnit v3 tests |
| `plugin/` | The Claude Code plugin: JSON and markdown that call `rtfc` from PATH |
| `docs/spec.md` | The design spec |

## License

MIT. See [`LICENSE`](LICENSE).
