# SE-11 S4 TBEP link loss — tracer receipt

Scenario `s4-tbep-link-loss` of the SE-11 end-to-end client live-update tracer
(ticket #13). Correctness evidence only — no performance claim. Loopback
hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal `backend-operator` / X-TB-Tenant `tenant-a`) only; no
secrets; no live systems; the engine and hosts are throwaway processes
under the run's temp directory.

- TerraBaseV2 worktree: branch `fleet/se11-2`
- Sibling SDK checkouts (read-only, never edited or pushed):
  TerraScale.TypeScript @ `cde691866e04882998a29e6ecd2c661802d48459`, TerraScale.DotNet @ `21d21aa9a5f3d9231f005a4b4eb5fb6bd986abe7`
- Reproduce: `python3 scripts/se11/run.py --scenario s4-tbep-link-loss` from the
  TerraBaseV2 worktree root (the fresh-checkout sequence is in
  `evidence/sync/se11-live-update-tracer.md`).

Outcome: **PASS** (6/6 checks).


| Check | Outcome | Detail |
| --- | --- | --- |
| `the host closed the stream with the resume-able engine-generation-lost frame` | pass | close reason engine-generation-lost |
| `the durable checkpoint ahead of a fresh in-memory generation is answered by the typed refusal (both cores)` | pass | ts resume-path errors [('engine_failure', None)] (the typed engine_failure: checkpoint end ahead of the server); net resume-path errors [('host-failure', 500), ('InvalidOperationException', None), ('InvalidOperationException', None)] (the same refusal body, surfaced through the recorded parse defect) |
| `the ended session is answered truthfully (410 session-ended), never re-served` | pass | stale-identity answers [('InvalidOperationException', None)] (the .NET core surfaces the 410 body through the recorded parse defect) |
| `ts converged on the fresh generation with no lost committed update among its commits` | pass | final checkpoint storage sequence 3 (3 post-recovery commits) |
| `net converged on the fresh generation with no lost committed update among its commits` | pass | final checkpoint storage sequence 3 (3 post-recovery commits) |
| `recovery ran through snapshot adoption / fresh setup (resync), not stale deltas` | pass | the .NET core adopts the server snapshot on every fresh setup (the fresh generation answers an empty state); the TS core adopts only when the server state advances past its own |
