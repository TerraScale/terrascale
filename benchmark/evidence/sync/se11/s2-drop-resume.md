# SE-11 S2 connection drop and checkpoint resume — tracer receipt

Scenario `s2-drop-resume` of the SE-11 end-to-end client live-update tracer
(ticket #13). Correctness evidence only — no performance claim. Loopback
hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal `backend-operator` / X-TB-Tenant `tenant-a`) only; no
secrets; no live systems; the engine and hosts are throwaway processes
under the run's temp directory.

- TerraBaseV2 worktree: branch `fleet/se11-2`
- Sibling SDK checkouts (read-only, never edited or pushed):
  TerraScale.TypeScript @ `cde691866e04882998a29e6ecd2c661802d48459`, TerraScale.DotNet @ `21d21aa9a5f3d9231f005a4b4eb5fb6bd986abe7`
- Reproduce: `python3 scripts/se11/run.py --scenario s2-drop-resume` from the
  TerraBaseV2 worktree root (the fresh-checkout sequence is in
  `evidence/sync/se11-live-update-tracer.md`).

Outcome: **PASS** (6/6 checks).


| Check | Outcome | Detail |
| --- | --- | --- |
| `ts resumed from its durable checkpoint and observed every committed update` | pass | final checkpoint storage sequence 5 (5 committed updates) |
| `net resumed from its durable checkpoint and observed every committed update` | pass | final checkpoint storage sequence 5 (5 committed updates) |
| `the TS session surfaced the drop through its documented failure ladder, then re-set up from the durable checkpoint` | pass | status transitions ['connecting', 'live', 'reconnecting', 'failed', 'closed', 'connecting', 'live']; the re-setup resumed from the durable checkpoint |
| `the .NET session re-set up by resuming from its durable checkpoint` | pass | 1 resumed setups after the drop |
| `the stale session identity was answered by the fresh host (never silently re-served)` | pass | stale-identity answers: [('HttpRequestException', None, 'An error occurred while sending the request.'), ('InvalidOperationException', None, "The requested operation requires an element of type 'Boolean', but the target element has type 'Null'.")] |
| `no duplicate application beyond the documented at-least-once identities` | pass | 0 duplicate batches |
