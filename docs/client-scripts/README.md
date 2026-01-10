# Manual client scripts

These scripts are meant for **quick manual verification** against a running UnrealBG-IRCd after refactors.

They are intentionally “raw IRC”: copy/paste the `C:` lines into your client (or pipe them via a TCP tool), and compare what the server sends (`S:` expectations).

If you changed any numeric formatting or line shapes and tests fail, update snapshots intentionally:

- PowerShell: `$env:IRCD_UPDATE_GOLDENS=1; dotnet test`

## How to run

### Generic (netcat / ncat)

You can use any TCP client that lets you type raw IRC lines.

- Connect to your server (example): `ncat 127.0.0.1 6667`
- Then paste the `C:` lines from a script (without the `C:` prefix).

Note: Some tools send `\n` only; IRC expects `\r\n`. If you see odd behavior, use a client that sends CRLF (most IRC clients do).

### HexChat / irssi / weechat

These scripts are compatible with any client that supports a “raw” command entry:

- HexChat: `/QUOTE <line>`
- irssi: `/QUOTE <line>`
- weechat: `/quote <line>`

## Scripts

### 01 - TOPIC basics (query + set)

File: `topic-basics.txt`

Expected: `331` when unset; a `TOPIC` broadcast when set.

### 02 - MODE list modes (+b/+e/+I) permissions

File: `mode-list-permissions.txt`

Expected: `442` when not on channel; `482` when not op; `367/368`, `348/349`, `346/347` when op.

### 03 - WHOIS channel visibility (secret channels)

File: `whois-secret-visibility.txt`

Expected: secret channels are omitted from `319` unless you are in them.

### 04 - CAP + SASL PLAIN flows

File: `sasl-plain.txt`

Expected: `CAP * LS` shows `sasl=PLAIN` (or `sasl=PLAIN,EXTERNAL` if enabled), `AUTHENTICATE +`, then `900` + `903` on success.
