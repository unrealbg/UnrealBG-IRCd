# Incident response

This runbook covers common “bad day” scenarios:

- Connect spikes / connection floods
- Message floods (channel spam)
- Suspected oper compromise

## 0) First response (5 minutes)

1. Confirm scope
   - Is it one server or multiple?
   - Is it only on plaintext (`6667`) or also TLS (`6697`)?
   - Is it only a single IP/subnet?

2. Check health and basic load
   - If enabled: `GET /healthz` should be `200`.
   - If enabled: `GET /metrics` and look at:
     - `ircd_connections_active`
     - `ircd_connections_accepted_total` (delta over time)
     - `ircd_flood_kicks_total` (delta over time)
     - `ircd_outbound_queue_depth`

3. Preserve evidence
   - Save recent logs.
   - If you will rotate oper passwords, do it after preserving current state.

## 1) Connect spikes / connection floods

### Immediate containment

1. Tighten connection guard
   - Edit your active config (or temporarily switch to `conf/examples/public.conf`) and ensure:
     - `security { profile = "public"; }`
     - `connectionguard { enabled = true; ... }`

2. Prefer TLS-only during an attack
   - In `listen { ... }`, set:
     - `clientport = 0;` (disable plaintext)
     - keep `enabletls = true; tlsclientport = 6697;`

3. Block obviously abusive IPs
   - Use OperServ (recommended):
     - `/MSG OperServ DLINE <ip-or-mask> <reason>`
     - Example: `/MSG OperServ DLINE 203.0.113.* attack`
   - Or the raw command (if you have oper capability):
     - `/DLINE <mask> <reason>`
   - Remove later:
     - `/MSG OperServ DLINE -<mask>`

4. If you run S2S
   - Confirm `6900/tcp` is allowlisted to peer IPs only.
   - If you suspect the S2S port is being scanned/attacked, temporarily set `serverport = 0` (and reload) or firewall-drop non-peers.

### After containment

- Review logs for lines like:
  - `Client connection rejected from <ip>`
  - `TLS client connection rejected from <ip>`
- Consider enabling connection prechecks (DNSBL/Tor/VPN heuristics) if you have known-good zones.

## 2) Message floods / channel spam

### Immediate containment

1. Ensure rate limiting is enabled
   - `ratelimit { enabled = true; ... disconnect { enabled = true; ... } }`

2. Consider a temporary IP ban for obvious sources
   - `/MSG OperServ DLINE <ip-or-mask> <reason>`

3. Operator actions
   - If a channel is targeted:
     - set channel modes (e.g., moderated / invite-only) as appropriate for your network policy.

### After containment

- Watch the following metrics:
  - `rate(ircd_commands_total[1m])`
  - `rate(ircd_flood_kicks_total[5m])`

## 3) Suspected oper compromise

### Immediate containment

1. Remove the attacker’s access
   - If you can, DLINE the source IP.
   - If you cannot trust the running process, stop the server and proceed offline.

2. Rotate oper passwords (see `key-rotation-oper-passwords.md`)
   - Treat this as urgent.

3. Verify configuration integrity
   - Check `IRCd.Server/confs/opers.conf` and `IRCd.Server/confs/classes.conf` for unexpected changes.
   - Check `IRCd.Server/confs/links.conf` and `listen { serverport = ... }` for unexpected S2S exposure.

### After containment

- Review logs for oper actions if audit logging is enabled.
- Consider requiring hashed oper passwords permanently:
  - `opersecurity { require_hashed_passwords = true; }`

## Optional: fail2ban starter (Linux)

This is intentionally minimal; adjust paths and log formats for your environment.

Example filter (`/etc/fail2ban/filter.d/ircd-conn-guard.conf`):

```
[Definition]
failregex = .*Client connection rejected from <HOST>:.*
            .*TLS client connection rejected from <HOST>:.*
ignoreregex =
```

Example jail (`/etc/fail2ban/jail.d/ircd.conf`):

```
[ircd-conn-guard]
enabled = true
filter = ircd-conn-guard
logpath = /var/log/ircd/ircd.log
maxretry = 5
findtime = 60
bantime = 600
```

Prefer static firewall allowlists for S2S over log-based bans.
