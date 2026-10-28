# Python parity gates, 2026-10-28

Synthetic control qualification, not a latency or live production result. On beta,
Python 3.15.2, HTTPX 0.28.1 and HTTPcore 1.0.9 ran the real SDK at
`49ea36ee9dfc005b1126e4baa68648b0e8fbd221` using its hash-locked runtime dependencies.
Benchmark source: `e0fe356df`, controls unchanged from `280b6588d`.
Commands ran through `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash
/tmp/python-parity-qualify-d.sh --repo terrascale --branch fix/api-python-parity`.

| Real SDK over HTTPX MockTransport | Attempts / good headers | Logical ops | Gate |
| --- | ---: | ---: | --- |
| Normal | 1 / 1 | 1 | accepted |
| Wrong region, duplicate region, H1 fallback (each) | 1 / 0 | 1 | rejected |
| 503 then 200 | 2 / 1 | 1 | rejected |
| 401 refresh then 200 | 2 / 1 | 1 | rejected; two auth exchanges |
| Truncated 200 body then successful retry | 2 / 2 | 1 | rejected |
| Stalled auth, 100 ms caller deadline | 0 / 0 | 1 | cancelled; auth never pinned |

The window control started three 2.5 s operations, retained two measured latencies,
and extended its 5 s denominator to 5.539300295 s. Malformed peers were refused;
regions were deduplicated. Cleanup completed after SIGINT followed by SIGTERM.
Compilation passed. Protocol metadata is synthetic; TLS, sockets, ALPN, live
setup/deletion, region availability and speed are unqualified by these controls.

Evidence on beta: `~/remote-builds/terrascale/fix-api-python-parity-5516b106a3c99d7265691b05e8cf2a126f4caba2f1c18baadc7cc19b8459b5a2/benchmark/bench/python-control-20261028-d/`.
It retains source, script, identity, lock subset, JSONL and SHA-256 manifest.
