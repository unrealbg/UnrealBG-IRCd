# Client compatibility

This repo contains a snapshot-based compatibility regression suite focused on IRC **numerics** and common command outputs.

It is meant to answer:

- “Did we accidentally change a numeric / message format?”
- “Do WHO/WHOIS/MODE/INVITE/JOIN/PRIVMSG outputs still look the same?”

It is **not** a full RFC compliance document.

## Golden snapshots

- Test driver: [IRCd.Tests/CompatibilityGoldenTests.cs](../IRCd.Tests/CompatibilityGoldenTests.cs)
- Snapshot files: [IRCd.Tests/Goldens/compat/](../IRCd.Tests/Goldens/compat/)

Normal CI/dev behavior:

- `dotnet test` compares outputs to snapshots.
- Any change in output fails the test, so numerics changes are always intentional.

To create/update snapshots locally:

- PowerShell:
  - `$env:IRCD_UPDATE_GOLDENS=1; dotnet test`

This will rewrite snapshot files under `IRCd.Tests/Goldens/compat/`.

## What’s covered (current)

The goldens currently exercise (at least):

- Registration gating (`451`), missing-parameter errors (`461`)
- `WHO` / `WHOIS` edge cases (invalid targets, target limits, away line)
- Channel `MODE` query and list modes:
  - `MODE #chan` -> `324`
  - `+b` list (`367/368`) and list-full (`478`)
  - `+e` list (`348/349`)
  - `+I` list (`346/347`)
- `INVITE` success and delivery, plus common failure numerics
- `JOIN` failures: illegal name (`479`), full (`471`), invite-only (`473`), key-required (`475`), banned (`474`)
- `PRIVMSG` channel policies (`404` variants), ban/exception behavior
- `TOPIC` query/set rules (membership/op-only topic)
- `CAP` negotiation and basic SASL PLAIN flows (`900/903/904/905/906`)
- Basic `MOTD`, `LUSERS`, `NAMES` outputs

## Manual scripts

For quick spot-checking against a live server (especially after refactors), see:

- [docs/client-scripts/](client-scripts/)

These are intentionally raw IRC command sequences with “expected output” notes.

## Manually validated clients

Fill this in as you validate against real clients. Don’t rely on this table unless it is populated with your own results.

| Client | Version | TLS | SASL | Notes | Validated by | Date |
|---|---:|:---:|:---:|---|---|---|
| (add) |  |  |  |  |  |  |

## Notes / common expectations

- Max client line length is enforced (see `transport.client_max_line_chars`).
- Default channel modes include `+nt`.
- Ban policy: a matching `+b` blocks both JOIN and speaking; `+e` exempts both.

## Known quirks / behavior notes

- Secret channel visibility: `WHO`/`WHOIS` hide secret channels from non-members.
- List modes: `MODE #chan +b/+e/+I` require channel operator privileges.
- SASL: `AUTHENTICATE` requires `CAP REQ :sasl` first, otherwise `904` is returned.

If you discover a client-specific quirk:

1. Add a small, focused golden case that reproduces it.
2. Regenerate snapshots with `IRCD_UPDATE_GOLDENS=1`.
3. Document the quirk in this file.
