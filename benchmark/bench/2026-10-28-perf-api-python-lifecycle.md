# Python parity lifecycle qualification, 2026-10-28

Offline controls only; no production requests, tenant, latency or speedup claim.
Beta, Python 3.15.2, SDK `49ea36ee9dfc005b1126e4baa68648b0e8fbd221`, unchanged
hash-locked runtime dependencies. Source `f1302f733454f4ad92a83aa2e108b3408e9fb671`.
Ran `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash /tmp/python-parity-qualify-e.sh --repo terrascale --branch fix/api-python-parity`.
Compilation, all eight existing real-SDK transport cases and window/peers/repeated-signal controls passed again.
The window retained both measured 2.5 s operations; elapsed denominator 5.530552345 s.
Four lifecycle cases ran concurrently through the real SDK over HTTPX MockTransport:

| Create outcome / delete | Recorded deletion | Reconcile required | Cleanup accepted |
| --- | --- | --- | --- |
| Created / 204 | logical_accepted | false | true |
| Not attempted / 404 | absent | false | true |
| Create deadline cancels stalled request / 404 | absent | true | false |
| Created / 403 | failed | true | false |

Each delete targets the exact owned name; remote erasure stays unverified. The timed-out create records its uncertain state before awaiting the SDK. A 404 after uncertainty cannot be used as completion proof. Auth is never region-pinned. Mock protocol metadata proves no TLS/ALPN, real sockets, live setup/deletion or regional performance.
Artifacts on beta: managed `fix-api-python-parity-5516b106a3c99d7265691b05e8cf2a126f4caba2f1c18baadc7cc19b8459b5a2`, `benchmark/bench/python-lifecycle-20261028-e/`: exact scripts/source, lock subset, identity, empty source diff, all control JSONL/error logs and SHA-256 manifest. Later changes are documentation only.
