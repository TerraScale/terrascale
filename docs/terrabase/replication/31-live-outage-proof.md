# 31. Live outage proof: every region down and back on Fly

`make terrabase-e2e-fly-outage` proves on Fly what the local replication matrix proves on in-process nodes: each region
can go down while the other two keep writing, deleting and conflicting, and it rejoins with all of it. Both runs share one
proof, `ReplicationMatrixTests.RejoinAsync` (tests/TerraBase.E2E).

## What one run proves

`FlyOutageE2ETests` runs gru, sjc and fra down in turn, inside one test, so no two outages stop the app's machines at the
same time. Each outage uses a collection of its own and, for the region it stops:

1. Seeds one document per survivor (to be deleted) and one shared document (to be conflicted), and waits until every
   region serves them.
2. Stops the region's single machine.
3. Probes both survivors: each one's write must be acknowledged `remote_durable` and then served by the other survivor.
   Fly's internal listener is out of reach, so this replaces the local check of connected peer streams.
4. Writes 40 documents split between the two survivors; each survivor deletes its seeded document and patches the shared
   document with its own field and value.
5. Starts the machine on its volume and sends no more writes: catch-up must replay the durable backlog.
6. Waits until the rejoined region serves each survivor's backlog, both deleted documents are absent everywhere, every
   region answers each deleted document with 404 and the same tombstone ETag, the conflict settles to both fields with
   one survivor's value everywhere, and every region lists the same collection digest (probes included).

Across the three outages that is six survivor-to-rejoined directions, each with a deletion whose retained tombstone
(404 with an equal ETag in all three regions) is checked, and six reported latencies, `rejoin <survivor> to <stopped>`.
The server's ETag is strong and includes the database identity, version and deletion state, so equal ETags mean the same
tombstone. A 404 without an ETag is reported as `no-tombstone(never-written-or-collected)`.

## Writes that are sent again

Only the probe write is sent again, and only after a refusal that executed nothing: 429 from tenant admission, or 503
whose JSON `error.code` is `remote_region_unavailable` or `confirmation_busy`. `write_outcome_unknown`, any other 503,
a body that is not JSON and the proxy's own 503 are sent exactly once and fail the test. While a restarted machine boots,
reads (never writes) treat the proxy's answer without TerraBase's identity, and 503, as not there yet.

## The target is strict

- It selects only the `replication-matrix-fly-outage` category, expects at least one test, and fails unless the run
  records exactly one test: `FlyOutageE2ETests`, an actual pass on Fly machines with the node-stop fault. A skip, a
  second test or a missing result is never live evidence.
- Without `TERRABASE_E2E_FLY_URL`, `TERRABASE_E2E_FLY_REVISION` and `TERRABASE_E2E_FLY_OUTAGE=1` the test is skipped,
  so the target fails. `TERRABASE_E2E_FLY_APP` must name the pinned disposable app; production and preview apps are
  refused (docs/terrabase/e2e-disposable-app.md).
- It runs under the suite's assembly timeout (10 minutes) with no override of its own.
- Run it alone: never with `terrabase-e2e` or another run against the same app.

The other `fly-outage` scenarios also stop the app's machines and can run concurrently with each other. They are not
part of this target and must not run against the disposable app during this proof.

## Coverage update

The region-down/rejoin replacement is `ReplicationMatrixTests.Each_region_rejoins_with_writes_and_tombstones_from_both_survivors`
(three local cases), plus `FlyOutageE2ETests.Each_region_machine_stopped_then_started_catches_up_on_writes_tombstones_and_conflicts_from_both_survivors`
(one Fly case covering gru, sjc and fra sequentially). Both use the same proof: survivor writes, deletes and conflicting
patches, then backlog, retained tombstone ETags, settled conflict and listing digest. The Fly case is skipped without
the pinned disposable target and explicit fault-run environment; the strict target treats that skip as failure.
No live run is recorded yet.

## Recovery after a failure or cancellation

If anything fails or is canceled while the region may be down (the stop included), the region is started again and
waited on until `/healthz` answers 200, on a five-minute clock of its own that the test's token does not cancel. The
original error is rethrown; if the recovery fails too, both are reported together. Recovery is cleanup, not success:
the test still fails. `RejoinRecoveryTests` (local-only category) cancels a real local outage and checks that the region
serves again and that the original cancellation is returned.

The proof's own calls (which refusals are sent again, the durability returned, the tombstone answer) are checked against
a handler-level stand-in by `DocsTests`, also in the local-only category, so the required local stage runs them.

## The test app

The proof's app is fresh for the run: three shared-cpu-2x 512 MB machines in gru, sjc and fra, three 1 GB volumes, its
own peer secret and its own Tigris bucket, nothing shared with production. Approved on 2026-10-14 (Q-E2E-CAP): right
after a green `make terrabase-e2e-fly-outage`, the whole app with its machines and volumes, and its own bucket, is
deleted. Machines are not kept stopped between runs.
