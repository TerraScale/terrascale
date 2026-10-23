# Replication scenario coverage: old suite → design C

## Last runs

<!-- e2e-summary -->
E2E run (local), 2026-10-14T13:19Z on mariogk-dev

- Commit: `3d1491bcf05caa6688f398333c953b8c2bb13379`; working tree: clean
- Stacked on: none (merge base with origin/main `778fba30cbfcc9c4e879578ad869e158480b51c2`)

| Category | Tests |
| --- | ---: |
| actual pass | 2 |
| harness check | 0 |
| expected failure | 0 |
| not supported | 0 |
| skip | 0 |
| fail | 0 |
| total | 2 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin` | actual pass | separate OS processes | ProcessKill |  |
| `The_remote_durable_frontier_survives_the_origins_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
<!-- /e2e-summary -->

## Live ts-orch qualification

Environment-selected scenarios use the disposable ts-orch service `terrabase-e2e` when
`TERRABASE_E2E_ORCH_NODES` names the direct NetBird client listener in each region. Each request carries the e2e
project's ts-auth project token, minted from `TERRABASE_E2E_ORCH_KEY`; authentication refusals fail the test.
`TERRABASE_E2E_ORCH_REVISION` pins every node's reported revision and region before and after a run.
See [the live run guide](../../docs/terrabase/e2e-ts-orch.md).

`OrchOutageE2ETests.Each_region_stopped_then_started_catches_up_on_writes_tombstones_and_conflicts_from_both_survivors`
stops each region in turn and checks its catch-up from both survivors. It runs alone through
`make terrabase-e2e-orch-outage`, only in a pinned outage run of the disposable service, and must record exactly one
actual pass on `ts-orch nodes`. Ordinary live runs cannot stop regions. Partitions, clock skew, file edits and process
kills explicitly start owned local nodes, independently of the live environment.

Explicit local fixtures, including backup and mirror qualification, always own their resources even when a live
service is configured. Their outcomes remain labelled `in-process nodes` or `separate OS processes`; only
`Nodes.Cluster` outcomes select `ts-orch nodes`. The local summaries above are local evidence only. No ts-orch run
result is recorded here.

## Backup qualification producer (#164)

`BackupQualificationE2ETests` uses a real three-node local cluster. It takes a ready
manual backup and two shipped PIT cuts, performs actual HTTP restores, and feeds their count, canonical digest,
versions and source/placement identity to `BackupQualification.Failures`. A pre-window cut must answer exactly
400 `invalid_request`. Supported-cut refusals fail. The same-body rewrite regression proves version loss fails
independently of the body digest. This row is not a `Gaps` expected failure.

Local backup storage uses test-only S3 transport; a local result is not Tigris evidence.

Per-cut details are retained in the run's
`TERRABASE_E2E_RESULTS` JSONL and console log. Exact acknowledgement-time cuts before convergence, shipping faults,
lease handoff and restored multi-region placement are not qualified by this scenario.

## Shape publication durability (FR-25)

`ShapeDurabilityE2ETests` drives the real shape sealer in separate gru/sjc operating-system processes through a
test-only probe. The peer refuses database applies before storing them; catalog replication continues. Both served
chunks and raw persisted chunks exclude the unacknowledged insertion and overwrite. After SIGKILL of the origin,
the peer lacks both writes. A second scenario proves that a remotely acknowledged frontier and safe immutable chunk
survive origin SIGKILL/restart. These scenarios passed locally; they do not qualify a deployment or power loss.

The probe bypasses endpoint wiring owned by FR-20. Fast tests cover held/refused acknowledgements, publication after
ack, immutable old values during an unacknowledged overwrite, worker wake, missing capture, and cell-of-one commits.

## Sealed shape chunks under power loss (V1)

`ShapeSealPowerLossTests` loads the write-ahead log fault shim into both gru and sjc processes
(`RegionProcesses.StartWithWalFaultsAsync`). After remote durable writes are sealed into snapshot pages and chunks
(shape HTTP API on the public listener, plus the probe's served and persisted chunk rows), one tenant-log sync is
killed: a gru write after the seals, a seal's own commit, or sjc's apply of the next batch. Every unsynced byte is
dropped, then the dropped frames but the last are written back with a torn commit frame. After restart every op of
every chunk, page and persisted row resolves to its document value on gru (served ones on sjc too), served chunks keep
their range and body, and the old HTTP handle resumes or answers 409 `must_refetch`. Six seed/cut pairs; results in
`benchmark/bench/2026-10-14-replication-v1.md`. The shim models only log cuts: torn main-database pages after a
checkpoint are not simulated, and no machine lost power.

## Shape cache headers (FR-24)

`ShapeCacheE2ETests` runs once per serving origin (gru, sjc, fra) of a local three-region mirror cluster through the
public shape, manifest (FR-22) and presigned-object paths. It seals a chunk, then fetches it from its presigned URL
with no TerraBase credential. Every answer must be the manifest's TBS1 bytes (size, SHA-256) with
`public, max-age=31536000, immutable`; the bootstrap 307, snapshot page, chunk, manifest, bounded live tail and
malformed-offset errors must be `private, no-store`. The object is fetched twice, with identical ciphertext required.
The `local-only` scenario uses test-only object storage; it does not qualify Tigris or deployment cache behavior.
Cache-hit evidence and origin storage-read counts per cache location belong to V3 (#305).
