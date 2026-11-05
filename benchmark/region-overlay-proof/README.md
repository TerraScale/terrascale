# Direct regional shape proof (F8)

`run.py` reads `{apiKey, project}` from private stdin. Use a unique Free synthetic
account from `ts-throwaway-account`; the caller deletes that account afterward.
The runner creates a gru database with sjc and fra replicas and deletes it in
`finally`. It uses regional TLS edges, verifies sjc local write routing and fra
live-tail serving, and records each provisional transaction and its settlement.

Run through `ts-heavy-any` on a dev host. After five warmup writes, three measured
repeats each insert five rows in sjc while following the fra tail. The runner
requires provisional delivery before the home-durable write response and a later
matching home-confirmed shape marker. It also overlaps a home write with a local
insert to prove rejection and the home winner on fra. Read retries and all race
attempts remain in the JSON report. Writes are never retried.

Timings use one client's monotonic clock and include its network links to both
regional edges. They measure observed delivery, not an isolated NATS transport
latency or the instant of the home commit. No production link is cut. The
isolated TCP-cut drill is `RegionShapeStreamTests` in the Slow suite; its proxy
cuts only fra's provisional connection while R7 and home page delivery continue.
