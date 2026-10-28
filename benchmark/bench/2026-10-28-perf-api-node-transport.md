# Node parity transport controls, 2026-10-28

Synthetic TLS qualification only: no production tenant, live SDK parity, latency
comparison or speedup. Both raw fetch and actual SDK GETs use the same observed
fetch boundary and application-owned Undici dispatcher. Certificate validation
stays enabled; the connector routes only synthetic api/auth hostnames to an
isolated ephemeral loopback listener with its own trusted CA.

Pinned SDK `b1bfe76254d8cc3e8e0aaa1a87dd599e7af4efe0` (0.0.18), committed lock,
Undici 7.30.1. Delta, Node 24.21.0, npm 12.2.0. Ran via `ts-heavy-any nice -n 19`.
Each case warmed a read, made 16 concurrent reads, then three sequential reads.

| Lane | Served ALPN | API connects | Concurrent attempts/sends/200s | Gate |
| --- | --- | ---: | --- | --- |
| raw-h1 | http/1.1 | 17 | 16/16/16 | accepted |
| sdk-h1 | http/1.1 | 17 | 16/16/16 | accepted |
| raw-h2 | h2 | 16 | 16/16/16 | accepted |
| sdk-h2 | h2 | 16 | 16/16/16 | accepted |
| raw-h2 | http/1.1 | 17 | 16/16/16 | rejected: protocol mismatch |
| sdk-h2 | http/1.1 | 17 | 16/16/16 | rejected: protocol mismatch |

No new API connections appeared on the sequential reads. Every case rejected
duplicate response region headers and cancelled a stalled token exchange with a
100 ms caller deadline. In every SDK case, one injected 503 followed by 200
produced two attempts and two sends for one operation, which the gate rejected.
The SDK retry policy stayed enabled. Each ordinary case made one token exchange;
all auth requests omitted the pin. Default H2 Agents opened multiple connections
at the burst, so these controls establish reuse, not single-session multiplexing.

Reproduction is committed: stage `benchmark/clients/node/` under the pinned SDK's
`benchmark/bench/node/`, install its unchanged lock with `npm ci --ignore-scripts`,
and run `node control.mjs` through the heavy wrapper. No extra dependency or npm
release is needed. This control does not qualify database setup, signal handling,
measurement windows, production routing, account cleanup or physical erasure.

Artifacts are retained under the delta managed `fix-api-keywatch-timer-3abe240428921dedcdd8d9fd645d2b861e9c2c0be603805186d65566b2ad022e`
checkout, `benchmark/bench/node-parity-control-f/`: exact sources, runtime identity,
JSONL results, empty error log, and SHA-256 manifest. The fixture removes its CA
and server on exit. No production credential or cleanup was required.

| Source | SHA-256 |
| --- | --- |
| control.mjs | `1df67d7a122a8d6cac1ab444b228ab737d8457d9d90473797f071d6086ef71b9` |
| observer.mjs | `deb6d633a9d9b728e3c38f0736c412056367d9ec9f982e6858bf57252c336158` |
| SDK package-lock.json | `8f490c55faaa1d8836677bb5822785c55a433215d51bb6b26674cc6b8835a8c0` |
