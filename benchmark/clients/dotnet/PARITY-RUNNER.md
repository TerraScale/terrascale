# Authenticated .NET parity runner

```
TS_PEERS_FILE=<ts-orch peers> api-bench parity --credentials PATH --out PATH
```

The runner builds on `ParityAttempts`, `ParityPeers`, `ParityWindow` and
`ParityDocument` ([core](PARITY-CORE.md), [window](PARITY-WINDOW.md)) and the
pinned public SDK ([pin](SDK-PIN.md)). The existing `startup` and `run` commands
and the workload preflight's closed `SDK_BLOCKED` result are unchanged.

## Before any network use

- Regions come only from `TS_PEERS_FILE`; every discovered region is measured and
  gru, sjc and fra must be present.
- Credentials must be a regular, non-symlinked file without group or other
  permissions, holding exactly the snake_case members `api_key` and `project`.
  Refusals never echo file content. The link check precedes the read, so a link
  swapped in between is not detected.
- The output is created new with mode 0600; an existing file or link is refused.

## Lifecycle

The `owned` row (`api-perf-<lowercase ULID>`, the deliberate public recovery
identity) is written before the token prewarm and create. Setup and cleanup use
`https://api.terrascale.tech` pinned to the alphabetically first region; the
auth client is separate and never pinned. Create uses
`Project(project).CreateDatabaseAsync(name, allRegions: true)`. Create,
collection and the exact 256 and 4096 B seeds share one 60 s deadline; each
seed's version is the write reply's.

Cleanup always deletes the exact owned name on its own 30 s deadline, after
failures, deadlines and signals. It reports `logical_accepted`, `absent` or
`failed` with `remote_erasure: unverified`. An uncertain create followed by
`absent` still sets `reconcile_required`. Main's SIGINT/SIGTERM registrations
stay active through cleanup and mark every signal handled. The first signal
cancels the run.

## Cells

Each region runs three repeats, with lane order reversed on even repeats. Each
repeat covers 256 and 4096 B at c1 and c16 for raw-h1, raw-h2 and the SDK. Every
cell gets a fresh `TerraBaseHttp.CreateHandler` pool with a counting
`ConnectCallback`, which uses the default socket connect and leaves the
redirect, cookie, pool, connect-timeout, TLS and proxy settings unchanged.

- Raw lanes send `RequestVersionExact` HTTP/1.1 or HTTP/2. The SDK keeps its
  H2-or-lower default, retries and provider refresh. Its observed protocol is
  reported, not forced.
- Before readiness, the token is prewarmed so it stays fresh through readiness,
  warmup, window and drain. Readiness lasts up to 60 s and is reported separately.
- Next come 2 s of warmup, a 5 s window and a 30 s drain. Starts inside the
  window keep their full latency. P50/P99 are labeled
  `approximate_reservoir_16384`.
- A cell is rejected by any extra send (retry), wrong target, pin, region or
  protocol, or a non-200 status. It is also rejected by a body whose id, version,
  state or document differs, a token exchange inside the cell, any pinned auth
  request, an interrupt or a drain timeout. The run stops after emitting the
  first rejected cell. The whole run is limited to 30 minutes.
- Unknown values are `null`: connects without a counted handler, and
  `wire_replays` always.

## Limits

- `DelegatingHandler` sees every SDK send, but `SocketsHttpHandler` internal
  replays are invisible. No zero-wire-retry claim is made.
- Connects count successful TCP connects only. TLS handshakes and auth-host
  connections are not counted.
- Offline controls use fake handlers that echo or override the response version.
  They test the version policy sent and the protocol gate, not TLS or ALPN
  negotiation.
- Signal controls call the handler with constructed `PosixSignalContext`s, so
  no real signal is delivered to the test host.
- The token lifetime must exceed about 157 s (60 s refresh margin plus 97 s
  horizon). Otherwise every cell is rejected with `auth`.
- No live run, latency, throughput, regional result or SDK overhead is
  established here.
