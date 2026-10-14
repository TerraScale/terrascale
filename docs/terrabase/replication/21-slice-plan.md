# 21. Slice plan

The focused regression matrix runs with `make terrabase-replication-matrix`: six write directions,
six delete directions, a three-region same-field conflict, and each region stopped and restarted
on its existing data. Every local case owns three real application nodes, ephemeral listeners and
storage directories; TUnit runs them in parallel. Catch-up includes writes from both surviving
regions, conflicts and deletes of documents that the stopped region still holds. Local delete
cases also inspect the persisted tombstones. No matrix case is an expected failure.

The required CI TerraBase test stage runs this local matrix. The post-deploy `e2e` job first
selects `Category=replication-matrix-live`: 13 write, conflict and delete cases against gru, sjc
and fra through `fly-prefer-region`; the harness rejects responses naming a different serving
region. That step requires 13 actual passes on Fly machines; skips and expected failures cannot
qualify it. The broader e2e suite still runs after the matrix. Convergence measurements appear in detailed
test output, measured from write acknowledgement to the first matching pinned read. These are
client-observed bounds including HTTP and polling overhead, rather than internal replication timings.
Publish measured results in `benchmark/bench/`, distinguishing local results from Fly results.

Local outage cases skip on Fly because parallel tests cannot each own a production machine.
Live outage proof uses `make terrabase-e2e-fly-outage` on a pinned disposable three-region app
(see [the disposable-app runbook](../e2e-disposable-app.md)); its result must be reported separately.
A skipped outage is never live catch-up evidence. The post-deploy job preserves the matrix and
broader suite logs and per-test outcomes as the `replication-e2e-<commit>` artifact. Each write direction reports ten
samples with minimum, median, p95 (nearest rank) and maximum latency.

Ordered PRs, each at most 1,500 changed lines. Pure deletion slices are listed separately so they stay reviewable.
Model: **Opus** = Opus 5.5 (complex, correctness-critical); **Haiku** = Haiku 5.5 (mechanical against this spec).
Metrics: R3, R4a and R6 emit the replication instruments named in `ops/grafana/README.md` (Metrics); the Grafana
dashboards and alerts already query those names.

| # | Slice | Contents | ≈ lines | Depends on | Model | Turns green |
| --- | --- | --- | ---: | --- | --- | --- |
| R1 | Field lattice library | `DocumentState`/`FieldState`: join, normalize, ops, delta-at-S, materialize, canonical encoding, digest. Property tests, under the user-approved lattice exception (anna-core decision 3): ACI, reorder and duplicate convergence, resurrection rules, counter reset, add-wins, kind precedence | 1,300 | none (start now) | Opus | lattice laws (Fast) |
| R0 | E2E harness on 3a | Adapt PR #6's `Cluster.StartNodeAsync` to PR #11 identity; add E8 to E15 as expected-failure; CI job after each deploy | 700 | PR #6, PR #11 | Haiku | E1–E6 on today's engine (local) |
| R2a | Turso layout + local write and read path | Section 5 tables, bootstrap, collection DDL from `_collections`, PUT/PATCH/DELETE/transactions through `DocumentState`, point reads, cache invalidation by lane. Deletes the old client tables and registers, and the old client replication apply paths that no longer compile (multi-region replication is down until R4a) | 1,500 | 1b engine swap (Turso provider, single writer, rowid), R1 | Opus | single-region Fast suite |
| R2b | Queries, indexes, unique, schemas on collection tables | `SqlSubset` compiler, list/query, `CREATE INDEX` on `"<c>"`, recommendations, unique tables, collection drop; planner check vs the spike's index issue | 1,200 | R2a | Haiku | Fast query and index suites |
| R2c | Delete preconditions, shapes, per-collection feed | `If-Match`/`If-None-Match`/`assert*`, `ChangeFeedService`, shapes, subscriptions, `StampingStorageEngine`, docs | deletion | R2a | Haiku | — |
| R3 | CDC tailer | Capture `after` on the lane connection (retry), tailer, pairing, change groups, `_tb_feed`, Merkle folding, checkpoint and truncation keeping the newest row, metrics; measure CDC cost on a Fly machine | 1,100 | R2a | Opus | Fast: groups match writes, no echo, atomic transactions |
| R4a | Peer streams | New proto, sender per peer from CDC with retention by acks, receiver join path (`FULL`), HLC observe, `origin`, heartbeats, `applied`/`stable`, placement-based peers from `FlyMachines` | 1,500 | R3, 3a (#11 + 8081 internal listener) | Opus | E1–E6 (local harness) |
| R10a | Catalog as lattice DB | `_system` on the new layout, placed everywhere, unique-name loser + `name_taken`, usage counters, management commits local | 1,400 | R4a | Opus | E1–E6 on Fly, E12 |
| R4b | Delete WAN, catalog primary, old replication | `CrossRegion/*`, `Catalog*`, `ForwardedRequests`, old protos, `PeerCredentials`, write-wait, `JournalDigest`, `VersionClock`, 0009/0010 superseded | deletion | R10a | Haiku | — |
| R5 | Counters and sets API | `inc`/`add`/`remove` ops, validation, OpenAPI, docs | 600 | R2a (R4a for e2e) | Haiku | E8, E9 |
| R6 | Anti-entropy + refill | `Digest`/`ReadBucket`, rounds, Merkle rebuild after unclean start, refill, `refill_required`, CDC retention cut, readiness per database | 1,300 | R4a | Opus | E7 |
| R8 | Changes feed SSE | `/databases/{id}/changes`, vector cursor, `cursor=now`, `Last-Event-ID`, collection filter, 410, retention | 900 | R3 (R4a for e2e) | Haiku | E10 |
| R9 | SQL writes | `INSERT`/`UPDATE`/`DELETE` grammar, compiled `SELECT` in the write transaction, `inc` detection, chunking, caps, scope | 1,200 | R2b, R5 | Opus | E11 |
| R7 | GC | `stable`/horizon, directory-based origins, eviction after 24 h, tombstone and removed-dot collection, feed retention | 800 | R6 | Opus | E14 |
| R11 | Home region + live move | `home`/`regions`, file placement, `fly-replay`, `/v1/operations/{id}`, move steps, file drop after acks; delete 421 placement | 1,400 | R10a, R6 | Opus | E13 |
| R12 | ts-auth lattice-native | `uses` sets on refresh tokens and codes, lockout counter, `/changes` watcher revoking families; delete ts-auth CAS | 1,200 | R5, R8 | Opus | E15 |
