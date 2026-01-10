# IRCd.LoadTest

A minimal load/soak testing tool for UnrealBG-IRCd.

## Scenarios

- `idle`: connects N clients, registers, then stays mostly idle while periodically sending `PING` to measure latency.
- `churn`: connect/disconnect churn + repeated `JOIN`/`PART` cycles (alias: `joinpart`).
- `chat`: steady `PRIVMSG` traffic at a fixed per-client rate (alias: `flood`).
- `splitheal`: only in `--spawn-network3` mode; repeatedly restarts node B to force S2S split/heal while clients keep chatting.

## Observability sampling

If the server has observability enabled, you can poll:

- `/healthz`
- `/metrics` (Prometheus text)

and emit a single combined `samples.csv` containing both client-side and server-side metrics.

## Usage

Run against a locally running IRCd:

```bash
dotnet run --project IRCd.LoadTest -c Release -- \
  --host 127.0.0.1 --port 6667 \
  --clients 1000 --duration 60 \
  --scenario idle --seed 12345 \
  --ramp-seconds 10

With observability sampling + output artifacts:

```bash
dotnet run --project IRCd.LoadTest -c Release -- \
  --host 127.0.0.1 --port 6667 \
  --obs-url http://127.0.0.1:6060 --sample-seconds 5 \
  --clients 2000 --duration 600 \
  --scenario churn --seed 12345 \
  --ramp-seconds 15 \
  --out .artifacts/soak
```
```

Write a CSV time series:

```bash
dotnet run --project IRCd.LoadTest -c Release -- \
  --host 127.0.0.1 --port 6667 \
  --clients 2000 --duration 120 \
  --scenario chat --mps 2 \
  --ramp-seconds 15 \
  --csv loadtest.csv

## Split/heal (3-node network)

This spawns 3 server processes locally (A-B-C) and forces split/heal by restarting node B:

```bash
dotnet run --project IRCd.LoadTest -c Release -- \
  --spawn-network3 \
  --scenario splitheal \
  --clients 500 --duration 900 \
  --out .artifacts/soak \
  --keep-logs
```
```

## Recommended runs

- 1k idle soak (basic stability)
  - `--clients 1000 --duration 300 --scenario idle --ramp-seconds 10`
- 5k churn (channel/state + reconnect pressure)
  - `--clients 5000 --duration 600 --scenario churn --channels 50 --ramp-seconds 20`
- 10k light chat (throughput + scheduling)
  - `--clients 10000 --duration 300 --scenario chat --mps 0.5 --channels 100 --ramp-seconds 30`

## Ramp-up

- `--ramp-seconds N` spreads the initial connects across N seconds (deterministic).
- `--ramp-jitter-ms M` adds up to M ms of additional per-client delay (still deterministic due to `--seed`).

## Notes

- Results are intended to be repeatable via `--seed` (client nicknames and channel selection are deterministic).
- The tool intentionally avoids TLS/SASL and sticks to the basic `NICK`/`USER` registration flow.
