# Key rotation: oper passwords

This runbook rotates operator passwords safely.

## Preconditions

- You have console access to the server host.
- You can restart or reload the IRCd.

## 1) Generate new hashed passwords

Use the provided tool to generate PBKDF2 hashes.

From the repo root:

- Interactive stdin mode:
  - `dotnet run --project IRCd.Tools.HashPassword -c Release -- --stdin`

Paste the tool output (format: `$pbkdf2$sha256:<iterations>$<salt_b64>$<hash_b64>`) into your oper blocks.

## 2) Update oper accounts

Edit `IRCd.Server/confs/opers.conf`:

- Replace `password = "CHANGEME";` with the new hash string.
- If you rotate multiple operators, do them all in one maintenance window.

## 3) Enforce “hashed only”

Edit `IRCd.Server/confs/opersecurity.conf`:

- Set:

```properties
opersecurity {
    require_hashed_passwords = true;
};
```

This prevents accidental regression back to plaintext passwords.

## 4) Apply the change

- Preferred: reload config via OperServ (requires oper capability `rehash`):

  - `/MSG OperServ REHASH`

- Otherwise: restart the process/service.

## 5) Validate

- Attempt oper-up with a known-good operator (over TLS):
  - `/OPER <name> <password>`
- Confirm old passwords no longer work.

## Rollback

- If you lose oper access:
  - Restore the prior `IRCd.Server/confs/opers.conf` from your backup.
  - Restart the server.

## Notes

- If you suspect compromise, rotate passwords _after_ collecting logs/evidence.
- Prefer unique strong passwords per operator.
