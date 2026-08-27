# SE-11 S6 multi-region visibility — tracer receipt

Scenario `s6-multi-region` of the SE-11 end-to-end client live-update tracer
(ticket #13). Correctness evidence only — no performance claim. Loopback
hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal `backend-operator` / X-TB-Tenant `tenant-a`) only; no
secrets; no live systems; the engine and hosts are throwaway processes
under the run's temp directory.

- TerraBaseV2 worktree: branch `fleet/se11-2`
- Sibling SDK checkouts (read-only, never edited or pushed):
  TerraScale.TypeScript @ `cde691866e04882998a29e6ecd2c661802d48459`, TerraScale.DotNet @ `21d21aa9a5f3d9231f005a4b4eb5fb6bd986abe7`
- Reproduce: `python3 scripts/se11/run.py --scenario s6-multi-region` from the
  TerraBaseV2 worktree root (the fresh-checkout sequence is in
  `evidence/sync/se11-live-update-tracer.md`).

Outcome: **PASS** (6/6 checks).

Stand-in, recorded honestly: the cross-region link is the tracer's scripted re-submission of the same operation identities (same idempotency keys) through the other region's real write path — the member transport between serving engines is a named integrator action and is not wired today. The visibility, convergence, and lag assertions are real observations at each region's serving surface; the transport between them is not the engine's replication seam.


| Check | Outcome | Detail |
| --- | --- | --- |
| `the write made through region a is visible through region b's delivery path (the reader's nearest region)` | pass | reader checkpoint on region b: {'storageSequence': 1, 'ends': [[1, 2]]} |
| `replication lag is a storage-sequence distance, not a latency claim (region-b behind before the pump)` | pass | region-b lag 1 at the observation: region-a published sequence 2, region-b durable cursor 1 |
| `convergence observation after the pump names both regions and closes the lag` | pass | convergence: op-w1 and op-w2 held by region-a and region-b at the observation (region-b lag 0: a sequence distance); region-b cursor 2 |
| `the idempotent retry identity: a re-submitted key answers the recorded receipt and advances nothing` | pass | receipt sequence 1 (region-b cursor unchanged at 3) |
| `the write through region b is visible through region a (the mirror direction)` | pass | region-a session checkpoint 3 (2 written through a + 1 carried from b) |
| `both regions converged under the same version-vector ends (per-region storage sequences are per-generation facts)` | pass | ends region-a [[1, 5]] == region-b [[1, 5]] |
