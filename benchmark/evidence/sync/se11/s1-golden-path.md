# SE-11 S1 golden path — tracer receipt

Scenario `s1-golden-path` of the SE-11 end-to-end client live-update tracer
(ticket #13). Correctness evidence only — no performance claim. Loopback
hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal `backend-operator` / X-TB-Tenant `tenant-a`) only; no
secrets; no live systems; the engine and hosts are throwaway processes
under the run's temp directory.

- TerraBaseV2 worktree: branch `fleet/se11-2`
- Sibling SDK checkouts (read-only, never edited or pushed):
  TerraScale.TypeScript @ `cde691866e04882998a29e6ecd2c661802d48459`, TerraScale.DotNet @ `21d21aa9a5f3d9231f005a4b4eb5fb6bd986abe7`
- Reproduce: `python3 scripts/se11/run.py --scenario s1-golden-path` from the
  TerraBaseV2 worktree root (the fresh-checkout sequence is in
  `evidence/sync/se11-live-update-tracer.md`).

Outcome: **PASS** (15/15 checks).


| Check | Outcome | Detail |
| --- | --- | --- |
| `ts reaches the vector-predicted final checkpoint` | pass | storage sequence 27 (27 = 3 seeded + 16 established + 8 repeated) |
| `net reaches the vector-predicted final checkpoint` | pass | storage sequence 27 (27 = 3 seeded + 16 established + 8 repeated) |
| `ts snapshot checkpoint equals the golden vector's` | pass | observed (sequence 3, ends [[1, 6]]) vs vector (3, ((1, 6),)) |
| `ts snapshot payload bytes equal the golden vector's (byte-exact Loro delivery)` | pass | 451 bytes (vector 451), digest match True |
| `net snapshot checkpoint equals the golden vector's` | pass | observed (sequence 3, ends [[1, 6]]) vs vector (3, ((1, 6),)) |
| `net snapshot payload bytes equal the golden vector's (byte-exact Loro delivery)` | pass | 451 bytes (vector 451), digest match True |
| `the vector's recorded budget row: repeated changes ride deltas below the snapshot of the same state` | pass | recorded delta_bytes 497 < snapshot_bytes 2788 (payload_bytes 407, envelope_overhead_bytes 90, bytes_per_delivered_change 62) |
| `ts repeated-change deltas stay below the snapshot re-send (re-derived from observation)` | pass | 1124 delta bytes for 8 repeated changes vs 2772 snapshot bytes of the established state |
| `ts every delta batch fits the delivery budget` | pass | max batch 320 bytes (budget 8192) |
| `net repeated-change deltas stay below the snapshot re-send (re-derived from observation)` | pass | 1124 delta bytes for 8 repeated changes vs 2772 snapshot bytes of the established state |
| `net every delta batch fits the delivery budget` | pass | max batch 320 bytes (budget 8192) |
| `both SDK cores observed identical transition and checkpoint sequences` | pass | 25 ts transitions vs 25 net transitions |
| `every committed update applied by both cores exactly once (no loss, no duplicate)` | pass | ts applied 24 batches, net applied 24 batches (24 = 16 established + 8 repeated; the seeded 3 arrive inside the snapshot) |
| `no undocumented duplicate application` | pass | 0 duplicate batches counted by the .NET core |
| `the trigger notification is content-free (no document data rides the hint)` | pass | key set ['collection', 'operationId', 'sequence', 'subscriptionId']; 114 wire bytes for the SSE event |
