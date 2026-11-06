# Regional handoff re-review qualification — release held

The corrected candidate passed all ten regional Slow drills five consecutive
times: **50/50 PASS**, no skipped tests, with default parallelism. This supersedes
the rejected six-drill qualification. It is local protocol qualification; no live
rollout acceptance is claimed. Production remains on b443, workflows stay disabled,
and stale global platform keys are untouched. The scoped executor driver and final
GO are separate prerequisites.

Base: `5fd89c3e7271abc350125b0e68e18b4fd50f5f76`. Exact source hashes and raw-log SHA256
are in [the machine-readable proof](2026-11-06-zdt-rereview-proof.json).
The isolated checkout ran through `ts-heavy-any`, `terrabase-test-slow`, on
the host recorded in the proof. The transport faults use a real NATS server and actual TCP proxies;
they do not claim three-server Raft election coverage.

| Drill | Result | Durations, repeats 1–5 |
| --- | --- | --- |
| Foreign lease replay, guarded handback and retired backup reader | 5/5 PASS | 18s 606ms, 18s 185ms, 18s 283ms, 18s 669ms, 18s 585ms |
| Target fails before exclusive admission | 5/5 PASS | 26s 758ms, 26s 278ms, 26s 350ms, 26s 385ms, 26s 949ms |
| Controller disconnect at every phase | 5/5 PASS | 23s 036ms, 22s 659ms, 22s 421ms, 22s 313ms, 22s 419ms |
| Quorum transport loss during rollback | 5/5 PASS | 23s 394ms, 23s 098ms, 23s 229ms, 23s 188ms, 22s 659ms |
| Both processes killed during open native write | 5/5 PASS | 25s 045ms, 24s 984ms, 25s 053ms, 25s 154ms, 24s 971ms |
| Certified manual CAS with connected source holding admission | 5/5 PASS | 23s 348ms, 23s 020ms, 22s 579ms, 23s 098ms, 23s 915ms |
| Target fails after acquiring exclusive admission | 5/5 PASS | 26s 636ms, 26s 722ms, 26s 455ms, 26s 301ms, 26s 408ms |
| Foreign and third-node DB isolation | 5/5 PASS | 24s 631ms, 24s 642ms, 24s 683ms, 24s 758ms, 24s 286ms |
| Retained Version:0 owner bootstrap | 5/5 PASS | 24s 992ms, 24s 826ms, 24s 740ms, 24s 429ms, 24s 900ms |
| Certified target recovery while source is dead | 5/5 PASS | 27s 339ms, 27s 844ms, 28s 251ms, 28s 359ms, 28s 433ms |

The first blocker is fixed by fencing a conflicting per-database home lease and
continuing activation for healthy DBs. The exact conflicting revision stays
isolated in normal confirmation and expiry claims. Recovery continues renewing
and activating from its persisted phase; a healthy certified target can retry
when the retained source is dead. Epoch-one selection uses node-name order,
independent of peers-file enumeration. Version:0 bootstrap/refusal semantics are
explicit in the handoff protocol documentation.

The rewritten manual-CAS drill exports the source's actual catalog/applied
positions and invokes real target preparation through the supported recovery
prepare path, while the source still renews admission. The source stays TCP-connected;
only its owner reads lag, while node-lease traffic continues. At the CAS the
source is still admitted and owns the exclusive writer key. Both nodes attempt
SQL writes: the target is refused while the key is held, then commits after
expiry; releasing the source's open native transaction is refused before COMMIT.
The final inventory contains all prior acknowledged IDs and the target's new ID,
with neither rejected ID. There is no handwritten preparation certificate.

Both target-failure drills kill the source handler before killing the target,
so no surviving handler/controller finally block can hide the recovery path.
The second case first proves that the target acquired the exclusive key, then
failed per-DB activation. The source restarts while the target remains unadmitted, then primes a same-region
replica before the target is killed. Persisted recovery
and exclusive expiry must restore native writes within 30 seconds of the CAS.

Qualification also reproduced a stale recovered-writer read. After the source
restarts as a follower, its retained primary can coexist with a same-region
replica. Recovery resumes native primary writes, but SQL previously chose a
replica session solely because that copy existed. The deterministic negative
control acknowledges an INSERT (rows_affected=1), then omits it from inventory.
The corrected SQL session uses the admitted regional writer's primary for local
home databases; SQL describe and table listing follow the same rule. Active
transaction snapshots and follower replicas/R10 holds remain intact. No #1105
admission or SqlExecuteEndpoint code changed. The same recovery drill now checks
the immediately acknowledged row in every repeat.

Controller-loss retries also exposed a suspended-follower race. Preparation
validated fresh durable ownership but still used a cached abandoned activation
until background refresh; after importing catalog data it could have no active
follower to catch up. Preparation now observes that validated owner under its
change gate and reconciles the imported catalog before the same strict applied
position checks. Diagnostic run r7 reproduced this refusal after the source had
recovered to epoch 4; r6 had failed on the same path in repeat 4. Both logs and
source manifests are retained; neither is a passing qualification.

The retained-owner bootstrap drill restarts a follower first against a Version:0
owner record with no protocol marker, proves the follower never admits writes,
then starts the same owner from retained data. Its node/epoch remain pinned;
protocol-2 activation and exact acknowledged rows are checked. This qualifies
engine adoption only, not executor installation or the mixed-version fleet run.

Qualification also found and fixed a backup lifecycle race: promotion replaces
a target primary, but an old tap checked lineage only and could attempt shutdown
shipping from the disposed store. The negative control holds a real native tap
across promotion and fails at Ended=false. Closed/disposed stores now end their
taps; a closure racing an in-flight backup read retires only that ended tap.
Discovery can open the replacement copy. Backup errors on live taps still fail.

Failed attempts are retained in /tmp/zdt-rereview-second.log (fixture omitted
Content-Length), /tmp/zdt-rereview-five.log (fixture requested handback before the
replacement observed authority), and /tmp/zdt-rereview-five-r2.log (real backup
shutdown race in repeat 3, after two complete passing repeats). Issue #1124
records these failures. The r3 attempt then failed in repeat 2 on the real stale recovered read. Negative
fixture attempts r2/r3 failed before priming because startup readiness was not
complete; the corrected r4 negative control fails on the missing acknowledged
row. The r5 attempt passed two complete repeats then failed at manual-CAS target
preparation in repeat 3. Its fixture dropped source owner reads before
certification, racing the source admission deadline; certification now finishes
before the owner-read fault is injected. r4 was interrupted for a SQL follower
session correction and does not count. No failed repeat was discarded or counted as a pass.

Before-admission recovery, ms: 12438.461, 12092.694, 12386.895, 11946.689, 12463.215.

After-admission recovery, ms: 12434.472, 12428.999, 12500.994, 12351.445, 12721.492.

Fast on the same source: 66/66 TerraBase in 8.438 s TUnit / 10.036 s budget
wall time; slowest test 3.659 s. Native driver: 4/4 in 0.389 s TUnit / 5.918 s budget wall time. Both run
with the existing parallel targets and timing checks. The source snapshot build
also passed with warnings as errors.
