# Upgrade process

This runbook upgrades the server with a clear rollback path.

## 1) Pre-flight

- Take a backup (see `backup-restore-services.md`).
- Confirm you know where your active config file is (default is `IRCd.Server/confs/ircd.conf`, selected via `Irc:ConfigFile`). All modular `*.conf` files are under `IRCd.Server/confs/`.
- If you run multiple servers (S2S), plan a rolling upgrade (one node at a time).

## 2) Build and stage

- Build the server:
  - `dotnet build -c Release`

- Stage configuration changes separately from binaries.
  - Avoid changing both config and binary in one step unless required.

## 3) Apply

### Single node

1. Stop the process/service.
2. Deploy new binaries.
3. Start the process/service.
4. Validate:
   - If enabled: `/healthz` is `200`.
   - Users can connect/register.
   - If enabled: `/metrics` updates.

### Multi-node (S2S)

1. Upgrade leaf nodes first, hubs last.
2. After each node upgrade, verify:
   - It reconnects to its peers (if outbound links configured).
   - Users on other nodes can still see/join channels across the network.

## 4) Post-upgrade checks

- Watch for spikes in:
  - `ircd_connections_active`
  - `ircd_outbound_queue_depth`
  - `ircd_flood_kicks_total`

## Rollback

- Stop the process/service.
- Restore prior binaries.
- Restore prior config if it was changed.
- Start and validate again.

## Notes

- Prefer enabling `opersecurity.require_hashed_passwords=true` on production during an upgrade window.
