# 2026-11-05: regional promotion blocked by builtin home leases

TerraBase's rollout handoff committed regional ownership, then refused to activate
its new writer because another region held the global lease for a local-only
builtin database. GRU returned 502 and token issuance failed while neither node
had write admission. SJC's independent builtins intentionally share database IDs
`0003`/`0004` with GRU and FRA. Acquiring global leases for them was incorrect.

## Timeline (UTC)

- 22:25:59: FRA's prepared handoff committed but activation stalled. Guarded
  same-node activation recovered FRA at epochs 3 and 5; both attempts retained
  the applied-data proof. The first canary's read timed out during the following
  legacy handback; the second canary passed and both resources were deleted.
- By 22:44:45: GRU had the same collision, with owner GRU2 at epoch 3 and no
  admitted writer. Both processes remained alive and fenced writes.
- 22:47:49: after capturing all eight GRU databases on both nodes, recovery moved
  its one replicated customer database's home lease to the prepared owner's live
  session with CAS, then activated GRU2 at epoch 4. At 22:47:55 both public GRU
  and global `/healthz` returned 200.
- 22:48:32: the already-running legacy update handed ownership back to GRU1 and
  encountered the same collision. A second guarded activation completed at
  22:49:01 (epoch 6). Token issuance and a GRU-home SQL canary passed by 22:49:51;
  writes through all three regions and exact reconciliation passed by 22:50:12.
- Around 22:51: rollout runs `42123579873` (b443) and `42124818234` (5fd) were
  cancelled. Cancellation did not revoke intents already published to ts-orch.
  SJC/FRA advanced to b443, and GRU was already handed to its old candidate again.
- 22:53:12: a third guarded activation restored GRU2 at epoch 8. The previously
  published update then completed; at 22:53:54 all six nodes ran b443 and GRU1
  was the ready writer at epoch 9, using the narrower activation guard.
- CI and Images workflows were disabled to prevent new automatic publications.
  No new rollout or CAS is authorized during the incident hold.

## Data checks and recovery validation

Before each guarded GRU activation, all eight databases had equal user table row
counts and checksum ranges across both nodes. Recovery preserved the regional
fence, the prepared data, R10 acknowledgement holds, and the source data directories.
Only the real replicated database's home lease and the regional owner record were
changed; foreign builtin leases were left intact.

After the third recovery, all eight databases retained their table row counts.
The five customer/demo databases and the status canary retained exact data hashes
on both nodes, including all **960** acceptance rows. Auth/dashboard hashes changed
in usage counters, rate limits, deletion cursors and allowance processing after
serving resumed; their row counts remained equal. Replica shape bookkeeping and
`commit_offset` are excluded from user-data equality. Import creates a new local
revision incarnation; raw applied positions are recorded, not compared as strings
across an import.

Token issuance returned 200. Three fresh databases, one homed in each region,
each accepted a write from GRU, SJC and FRA. All nine final region/database reads
returned exactly the three acknowledged IDs. All three databases were deleted
with 204. This is a small recovery canary, not the three-rollout latency proof.

Raw counts, revision positions, checksum ranges and synthetic canaries are in
[the evidence manifest](2026-11-05-builtin-lease-collision/manifest.json). The first
post-handback capture includes only the surviving founder because the worker was
being replaced; the later stable capture covers both nodes. No zero-downtime
acceptance pass is claimed.

## Code correction and remaining work

Runtime b443 changes `HomeLeases.TakeRegionalAsync`: require catalog placement in
the local region; local-only databases use the existing regional writer fence
and never acquire global home leases. Replicated databases still transfer their
real leases by CAS. Lease records, regional-owner records, HTTP/peer protocols and
storage format are unchanged from 4cd; the fix changes activation eligibility.
No manual CAS belongs in future normal rollouts.

An independent review found that b443 was incomplete: `ObserveAsync` still
accepted global platform leases, and expiration/claim paths could act on them.
At 22:59:11–36, a read-only audit found all 12 platform placements on the six
nodes still in their own region. Both stale global keys remained SJC2 epoch 3,
without a message TTL or stream max age. They are a restart hazard, even though
current placements are correct. No repair was needed or performed.

The pending isolation correction excludes platform IDs before catalog lookup and
excludes every known region-only placement before observing, claiming, taking or
demoting a global home lease. Region-only databases share their existing,
region-scoped authority at `terrabase-region-owners/<region>`; they never acquire
`terrabase-homes/home.<database-id>`. Replicated database lease keys keep their
existing format. This preserves one regional writer and R10 acknowledgement holds
without adding a second competing lease for each local-only database.

Keep both stale global platform keys until the isolation correction is deployed
on all watchers. Deletion is deferred: the current SJC watcher may retain cached
state, and deletion alone does not establish safe startup behavior in GRU/FRA.
Do not restart production watchers before that deployment.

The regression seeds an auth lease before startup, then replays a dashboard lease
and a custom region-only lease with matching tenant IDs. An ordinary lease event
provides an ordered watch barrier. It checks that the foreign leases never enter
the lease cache, platform placements remain local, and promotion and handback
preserve acknowledged writes. Initial fixture attempts failed to compile, tried
to alter the runtime bucket TTL configuration, and omitted platform seeding;
those attempts are not passes. Exact-source qualification passed all five repeats (3.963, 4.773, 5.370, 5.554 and 3.816 seconds). The same fixture against shipped 5fd runtime failed on the foreign auth lease entering the cache, as expected. See [the lease-isolation report](../../benchmark/bench/2026-11-05-zdt-lease-isolation.md).

The complete local candidate now adds exclusive regional writer admission
(six-second server TTL, five-second monotonic admission), persisted drain and
activation phases, preparation certificates, bounded attempts, and automatic
recovery only before activation commits. Database confirmation also runs after
process restart and retains progress across bounded attempts. A durable retiring
intent prevents stopping the surviving writer until its peer has restarted and
caught up. Legitimate foreign-held replicated databases fence only that database.

The original six-drill qualification was rejected by independent re-review:
it did not cover a conflicting per-DB lease, an admitted target that fails
activation, or a certified target competing with a connected source. The old
[recovery report](../../benchmark/bench/2026-11-06-zdt-control.md) is explicitly
superseded and does not authorize release.

Old b443 processes admit writes without the exclusive lease. The approved
bootstrap approach retains each owner node and epoch, replaces the follower
first, then replaces that owner in place during declared regional maintenance.
It still requires the reviewed executor fence/driver, independent re-review and
final GO. No candidate has been pushed or deployed.
Restore CI/Images only after the collision regressions pass and explicit release
approval. No live rollout, deployment or manual CAS is authorized during this
hold. Three measured live rollouts and the independent acceptance handoff remain
outstanding. Durable notice replay (#1104) is outside this incident proof.

The owned `rollout-zdt-proof-20261105` synthetic account was deleted after its
databases were cleaned up. The independent acceptance account is not owned by
this worker and was retained.

## Re-review corrections, locally qualified

The first exclusive-admission candidate was rejected: one conflicting database
lease could still block every regional activation, and the manual-CAS drill did
not exercise a certified target competing with a connected source. The corrected
candidate isolates that database/revision, retries persisted recovery, chooses
epoch-one owners independently of peers-file order, and avoids recovering to a
dead source when the certified target can retry. New drills cover those paths
and a target that acquired the exclusive key before failing activation.

Expanded repeats also found two real lifecycle problems. Promotion could close a
primary while its continuous-backup tap still reported itself live; ended taps
now recognize closed stores and retire safely when a pending read races closure.
A restarted source could keep a same-region replica in memory before recovery;
after admission it wrote the retained primary but could read the older replica.
The recovered writer's SQL reads, descriptions and table listing now select its
primary, preserving active transaction snapshots, follower reads and R10 holds.
Preparation retry after an abandoned activation could also use stale cached
ownership and leave its previous follower suspended. It now observes the validated
owner under the change gate and reconciles the imported catalog before catch-up.
The backup and recovered-read problems have deterministic negative controls; failed and superseded runs
remain recorded in issue #1124 and the re-review benchmark report.

The corrected source passed all ten Slow drills five consecutive times (50/50),
with exact acknowledged-row checks; both target-failure recoveries took
11.947–12.721 seconds against a 30-second bound. Fast passed 66/66 TerraBase
and 4/4 native driver. See the [re-review proof](../../benchmark/bench/2026-11-06-zdt-rereview.md)
for source hashes, every repeat, negative controls and failed-attempt history.

These local changes do not authorize deploying, restarting production watchers,
deleting global keys, re-enabling workflows, or counting the maintenance bootstrap
as a successful zero-downtime rollout. The executor installation gate and final
GO remain outstanding.
