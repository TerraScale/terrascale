# Python parity runner transport control, 2026-10-28

No latency, authenticated SDK or regional-production qualification is claimed.
Beta, runner `308f035570a3e3f192a84dde71570754ae9247db`, SDK 0.0.5 source
`49ea36ee9dfc005b1126e4baa68648b0e8fbd221`; imported Python source SHA-256
`250804eaf414a42d3bf60e29e1c516a7b4be52757d35b28bfaa9375aba22c01b`.
Through `ts-heavy-any nice -n 19`, compilation and status/region/protocol/duplicate-header,
document and pin-isolation checks passed. Each pool then sent two unpinned, anonymous
GETs to the public `/v1/regions`, with normal TLS/proxy defaults and the HTTPcore trace.

| Runner pool | Responses | Negotiated protocol | TCP connection attempts |
| --- | ---: | --- | ---: |
| raw-h1 | 2 | HTTP/1.1 | 1 |
| raw-h2 | 2 | HTTP/2 | 1 |
| SDK settings | 2 | HTTP/2 | 1 |

The SDK-settings row exercises the runner's pool, not an SDK data/auth operation.
No tenant was created. Run a stopped during dependency installation: beta uses Python
3.15 while the full CI lock includes backports-asyncio-runner restricted below 3.11.
Run b used eleven runtime entries with their original hashes from the same SDK lock.
Both artifacts remain under beta's managed `fix-api-python-parity-5516b106a3c99d7265691b05e8cf2a126f4caba2f1c18baadc7cc19b8459b5a2/benchmark/bench/python-control-20261028-{a,b}/`.
Run b retains the exact control source, runtime lock, dependency log and JSONL results.
