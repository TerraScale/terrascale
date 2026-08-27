# SE-11 S5 region route change — tracer receipt

Scenario `s5-region-route-change` of the SE-11 end-to-end client live-update tracer
(ticket #13). Correctness evidence only — no performance claim. Loopback
hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal `backend-operator` / X-TB-Tenant `tenant-a`) only; no
secrets; no live systems; the engine and hosts are throwaway processes
under the run's temp directory.

- TerraBaseV2 worktree: branch `fleet/se11-2`
- Sibling SDK checkouts (read-only, never edited or pushed):
  TerraScale.TypeScript @ `cde691866e04882998a29e6ecd2c661802d48459`, TerraScale.DotNet @ `21d21aa9a5f3d9231f005a4b4eb5fb6bd986abe7`
- Reproduce: `python3 scripts/se11/run.py --scenario s5-region-route-change` from the
  TerraBaseV2 worktree root (the fresh-checkout sequence is in
  `evidence/sync/se11-live-update-tracer.md`).

Outcome: **PASS** (5/5 checks).

Stand-in, recorded honestly: the `region_route_change` advisory control frame has no emission point on the host's HTTP surface today, so the tracer drives the vector's client half exactly — the durable checkpoint carried to the new region, acknowledged there, nothing re-delivered behind it. The two regions are two real engine + host pairs on distinct loopback ports (serving instances 1 and 2); region b's journal is seeded by the tracer's re-submission of the same operation identities through region b's real write path (the member transport between serving engines is a named integrator action, not wired today). The post-route write is released only after both cores' region-b subscriptions are observed on their own wire (the `waitStream` gate): the .NET live loop has no fallback pull before its first trigger, so a write beating the subscription would be undeliverable — the race behind this receipt's earlier 2-of-4 flake.


| Check | Outcome | Detail |
| --- | --- | --- |
| `ts carried its durable checkpoint to the new region with no lost committed update` | pass | final checkpoint storage sequence 4 on region b (3 carried + 1 written through b) |
| `net carried its durable checkpoint to the new region with no lost committed update` | pass | final checkpoint storage sequence 4 on region b (3 carried + 1 written through b) |
| `the carried checkpoint acked at the new region; nothing re-delivered behind it (the vector's checkpoint_ack step)` | pass | 0 duplicate batches after the route change |
| `the write made through the new region is the only delta observed there` | pass | delta batches on region b: ts 1, net 1 |
| `both cores applied byte-identical payloads for the post-route write` | pass | digest 811cacb0f0e07a4b… |
