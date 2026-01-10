# Backup/restore: services persistence (NickServ / ChanServ)

This runbook covers backing up and restoring the JSON persistence used by services.

## What is persisted

Configured in `IRCd.Server/confs/services.conf`:

- NickServ accounts (default): `data/nickserv-accounts.json`
- ChanServ registered channels (default): `data/chanserv-channels.json`

The persistence layer performs atomic writes (via `*.tmp` then replace) and can keep rolling backups as `*.bak.N`.

## Backup

### Recommended (cold backup)

1. Stop the IRCd process/service.
2. Copy the following to your backup location:
   - `data/nickserv-accounts.json`
   - `data/chanserv-channels.json`
   - any `data/*.bak.*` files
3. Start the IRCd.

### Online backup (acceptable)

Because writes are atomic, copying the `*.json` files while the server runs is typically safe.

- Also copy any `*.bak.*` files if present.
- If you see a leftover `*.tmp`, include it in your backup as well.

## Restore

1. Stop the IRCd.
2. Restore the JSON files into the configured paths from `services.conf`.
3. If present, remove stale `*.tmp` files unless you intend to restore them as well.
4. Start the IRCd.

## Validation

- Connect and verify:
  - NickServ can identify an existing account.
  - ChanServ shows registered channels as expected.

## Notes

- Paths in `services.conf` may be relative; they resolve under the server content root.
- Keep backups before upgrades.
