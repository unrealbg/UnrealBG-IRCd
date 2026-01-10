# Operator runbooks

This folder is intended to be “grab-and-go” operational guidance.

- Deployment baseline and example configs:
  - `conf/examples/public.conf` (internet-facing)
  - `conf/examples/lan.conf` (trusted LAN)
- Runbooks:
  - `incident-response.md`
  - `key-rotation-oper-passwords.md`
  - `backup-restore-services.md`
  - `upgrade-process.md`

## Baseline security posture (quick checklist)

- Prefer TLS-only client ingress (`6697`) and disable plaintext (`6667`) unless you explicitly want it.
- Keep S2S (`serverport`, default `6900`) closed unless you run a multi-server network; if you do, firewall it to an allowlist of peer IPs.
- Use the `public` security profile on Internet-facing servers.
- Require hashed oper passwords (`opersecurity { require_hashed_passwords = true; }`).
- Keep operational HTTP endpoints bound to localhost (`observability { bind = "127.0.0.1"; }`).

## Firewall baseline

Minimum recommended inbound rules:

- `6667/tcp`: optional (plaintext IRC). If enabled, allow from the Internet.
- `6697/tcp`: recommended (TLS IRC). Allow from the Internet.
- `6900/tcp`: S2S. **Do not** expose publicly. Allow only from peer server IPs (or keep disabled).
- `6060/tcp`: observability HTTP (`/healthz`, `/metrics`). Bind to `127.0.0.1` and/or firewall to operators only.

Outbound:

- DNS (`53/udp,tcp`) if you enable connection prechecks (DNSBL/Tor/VPN zones).

## Metrics and alerting

When `observability.enabled=true`, scrape:

- `GET /healthz`
- `GET /metrics` (Prometheus text)

Recommended alerts (tune thresholds per network size):

- `ircd_connections_active` sudden spike (possible connect flood).
- `rate(ircd_connections_accepted_total[1m])` unusually high.
- `rate(ircd_flood_kicks_total[5m])` > 0 for sustained periods.
- `ircd_outbound_queue_depth` rising steadily or near capacity.
- `ircd_outbound_queue_dropped_total` or `ircd_outbound_queue_overflow_disconnects_total` > 0.

## Optional: fail2ban

If you use fail2ban, prefer banning on stable log lines like:

- `Client connection rejected from <ip>` (connection guard throttle)
- `TLS client connection rejected from <ip>`
- `TLS handshake timed out from <endpoint>` / `TLS handshake failed from <endpoint>`

See `incident-response.md` for a minimal example filter/jail snippet.
