# Node SDK parity runner transport control, 2026-10-28

This qualifies benchmark counters against synthetic TLS endpoints. It is not an
authenticated TerraBase run, regional proof, SDK latency result or speedup.

Source: ts-js `b1bfe76254d8cc3e8e0aaa1a87dd599e7af4efe0` (0.0.18),
committed lock with Undici 7.30.1. Node v24.21.0, npm 12.2.0, host delta.
The control ran through `ts-heavy-any nice -n 19` with a self-signed CA trusted by
the connector, certificate hostname verification enabled, synthetic API-key
exchange, actual SDK GETs, and native-fetch controls. Only synthetic api/auth
hostnames are routed to an ephemeral loopback TLS server. No production tenant
or credential is used. The benchmark's shipped Agent has no connection cap.

After a warm read, each row made 16 concurrent operations and three sequential
reads. No additional API connections appeared during the sequential reads.

| Lane | Served ALPN | API connects including readiness | Concurrent attempts/sends/200s | Outcome |
| --- | --- | ---: | --- | --- |
| raw-h1 | http/1.1 | 17 | 16/16/16 | accepted |
| sdk-h1 | http/1.1 | 17 | 16/16/16 | accepted |
| raw-h2 | h2 | 16 | 16/16/16 | accepted |
| sdk-h2 | h2 | 16 | 16/16/16 | accepted |
| raw-h2 | http/1.1 | 17 | 16/16/16 | rejected: protocol mismatch |
| sdk-h2 | http/1.1 | 17 | 16/16/16 | rejected: protocol mismatch |

All six cases rejected duplicate `x-terrabase-region` values. Each SDK case
injected one 503 then 200: the observer saw two fetch attempts and two wire sends
for one operation, and the actual cell gate rejected it. The SDK retry policy
stayed enabled. Every auth request omitted the region pin, and only one token
exchange occurred per case. Peer-file discovery deduplicated regions and
rejected a malformed row. These controls do not test live setup or erasure.

The first control failed because its synthetic token omitted required `scope`.
A second launch used an incorrect local checkout path. The next control exposed
that the default H2 Agent opens multiple connections at the initial concurrent
burst; the incorrect one-connection assertion was replaced by observed counts
and the check for no extra connections after the burst. Six final cases passed.
No failed run was treated as parity evidence.

Retained artifact directory:
`/home/mariogk/remote-builds/terrascale/fix-api-server-attribution-9cfecace35564b2c364249fe56d09f0d7490a057d9e28909ddde9d4731fb4a62/benchmark/bench/node-parity-control-d`.
It contains the exact runner/control scripts, six JSONL rows, empty error log,
source/runtime identity and SHA-256 manifest. Run `node node-control.mjs` with
`node-parity.mjs` adjacent under the pinned SDK's `benchmark/bench/`, after
`npm ci --ignore-scripts`. It creates and removes its own ephemeral TLS fixture.

Live SDK/HTTP ratios and three-repeat gru/sjc/fra measurements remain pending;
prior synthetic-account erasure reconciliation must finish before a new tenant.

Control SHA-256: `067f32d054f824749efdfe054a218864ef22cec485a307eb2891099953b43b2e`.
Runner SHA-256: `21b42f6c0125202cc4d6ed59b8dc163f255230f76d5e0b0aac2cabb6251ef235`.
SDK lock SHA-256: `8f490c55faaa1d8836677bb5822785c55a433215d51bb6b26674cc6b8835a8c0`.
