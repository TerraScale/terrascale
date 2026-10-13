# Validation requirements: no acknowledged loss and whole-node disaster recovery

Requirements only. The designs belong to their owners: T1 (storage) compares remote-segment-before-acknowledgment with
regional-quorum-before-acknowledgment; R10a (catalog and security) designs whole-node disaster recovery after its
route-anchor fix. This harness owns validation. Any implementation that changes production durability or availability
semantics stays held until its design and the user's decision exist. Both are full-acceptance scope of the 100M proof
([plan](pitr-100m.md)); neither may be waived by a smaller gate.

## Evidence rules for both designs

1. Every run pins the exact commit and binary sha256, and repeats the same scenarios on the last image without the
   change (before/after controls). A run that demonstrates a known defect is never a pass.
2. The oracle is the harness ledger. An ambiguous outcome (timeout, 5xx, lost connection) stays unresolved until a
   200 or a 412 plus read-back proves it. A quiet read never resolves a possible late commit, and elapsed time never
   proves that a request will not commit later. Retry safety is claimed only for conditional operations; unconditional
   writes, increments and appends, re-creates without a version precondition, and security anchors are never "safe to
   retry".
3. Test-only tenant-id adoption (the harness's Path B) is never product disaster recovery.
4. Faults touch only test-owned processes and storage: the local S3 emulator (`s3-serve` faults), the response-dropping
   proxy, H4 WAL sync faults (`WalSyncFaults`, `DropUnsynced`, PR #143), SIGKILL/SIGSTOP, and destruction of test-owned
   data directories. Never live, shared or production resources.
5. "Source destruction" means the node is gone: SIGKILL, then its data directory (and, for power loss, its unsynced
   WAL bytes via H4) is destroyed, so recovery can use only the bucket or the surviving regions.
6. Each scenario reports expected vs actual, the ledger's acknowledged / unresolved / rejected counts, full counts and
   digests per database, and raw logs.

## A. No acknowledged loss

Designs under comparison: **A1 remote-segment-before-ACK** (a 2xx only once the commit's change-log position is inside
an authenticated segment stored in the bucket) and **A2 regional-quorum-before-ACK** (a 2xx only once enough other
regions durably hold it).

| Id | Scenario | Pass condition |
| --- | --- | --- |
| A-1 | Source destruction at each acknowledgment boundary: (a) after the local commit, before remote durability; (b) after remote durability, before the 2xx is sent; (c) after the 2xx is sent, before the client receives it (proxy drops it); (d) after the client received it. Repeated for create, conditional put, conditional delete, re-create over a tombstone, a phase fence, and a management commit with a revoking operation. | Recovery onto a genuinely fresh node holds **every** write the client received a 2xx for (zero acknowledged loss, by count and digest). (b) is present. (a) and (c) may be present or absent but are listed as unresolved by the ledger, never counted as lost or as written. |
| A-2 | Real lost responses and ambiguous outcomes on the binary (response-dropping proxy) for every operation above, plus duplicated and delayed retries. | Conditional retries apply exactly once (412 or 404 plus read-back proof); nothing is classified written without proof; unconditional operations stay unresolved. |
| A-3 | Object-store outage and partition (A1): all requests failing, slow, stored-then-failed, and a partition between node and bucket, each during sustained writes and during a snapshot upload. | No 2xx while remote durability is unavailable; writers get a bounded, typed failure; after recovery no overlapping or missing segments (P1, P2, P3, P10), and `/v1/backup`, `/backupz` and metrics report the outage truthfully (P5). |
| A-4 | Region partition (A2): minority/majority splits, quorum loss, heal, destruction of the acknowledging node during and after a partition. | No 2xx with fewer than the required regions; no divergence after heal; destroying any single region loses no acknowledged write. |
| A-5 | Group commit and concurrency: batches straddling the durable position, 1/16/64 concurrent writers. | A write is acknowledged only when its own commit is durable; no batch-level over-acknowledgment. |
| A-6 | Cost: write latency p50/p99 and documents per second before and after, bucket requests per million writes (A1) or cross-region round trips (A2), at 1M documents. | Recorded; no pass threshold until the user sets one. |
| A-7 | Restore at T during a backlog (found in review round 3): sustained writes faster than shipping, then restores at times T inside the backlog window, each compared against the ledger of 2xx responses received before T. | A restore at T holds every write acknowledged before T, or the restore is refused (fail closed). Today a segment's time is its read time, so a restore at T can lack writes acknowledged before T, by up to the backlog lag; the product docs say so. **Decided 2026-10-12 (lead, within the user's no-loss acceptance): required.** Owner: the restore owner. **No T is exact until its proof is verified.**
- **Not yet verified:** Turso `change_time` semantics (statement vs commit time, precision and rounding, boundary inclusivity) and clock behavior.
- **The owner defines:** the commit/ACK cut, including group commit, and a durable complete-prefix proof (a shipped witness past the cut, a watermark from a read that reached the tail, or an exact next snapshot). Timestamps alone cannot prove that no committed tail is still unshipped.
- **Replay** is whole-transaction only and never treats statement time as commit time. Anything unproven gets a typed 409; holes stay `backup_history_incomplete`.
- **Required fail-first tests:** a long transaction spanning T; several commits in one second; exact-second T and T±1 ms; delayed shipping and an unshipped final segment; lost ACK and late commit; clock skew and rollback; group commit.
- **Tail watermark (approved 2026-10-12, in progress):** a read-mark record (ChangeLog kind 7, carrying R in Unix ms). R is taken immediately before the tap's visibility query, and the mark is written only when that read appended through the newest commit and appended something, so every commit finished before R precedes the mark.
  - It makes provable: the DR latest point floor_s(R_last), and any T <= R whose second holds no commit.
  - Capped reads and empty reads write no mark.
  - The window/plan endpoint may read a fixed, small number of newest segments to find a mark. Otherwise it falls back to the newest point it can prove; the restore stays the authority.
  - Unchanged: the bucket layout and the shipper.
- **Limits:** clock behavior that can't be detected is reported as a stated limit. The production ACK contract is unchanged.
- **Harness:** a two-sided check on every 201 (acknowledged before T present, first sent at or after T absent, partial batches fail). A typed refusal is recorded as refused, never as a pass for an exact-cut requirement. |

## B. Whole-node disaster recovery

| Id | Scenario | Pass condition |
| --- | --- | --- |
| B-1 | Genuinely fresh node: new machine id, empty data directory, no copied local file; only the bucket, the master key from the user's secure handoff, and the operator's explicit action. | The node recovers `_system` (organizations, projects, databases, keys, memberships, API keys) first, then every tenant database, without tenant-id adoption or any harness-side catalog write. |
| B-2 | Ordering and consistency between the catalog and tenant databases. | A documented cut rule holds: no tenant database without its catalog record, no catalog record whose database history is missing; the rule is checked, not assumed. |
| B-3 | Catalog anchor (R10a): anchor missing, stale (bucket snapshot rolled back), unreachable, tampered; a non-genesis cluster's first anchor deployment. | Recovery fails closed (unknown) until trusted-peer proof or an explicit operator catalog-open; never automatic success. Revocations anchored before their response stay revoked after recovery. |
| B-4 | Missing or corrupt history for `_system` or any tenant database (the PitrLab fault matrix: missing middle, last and final segments, corrupt, truncated, duplicate start, stray key, missing or corrupt snapshot, wrong master key). | A typed failure naming the database; no partially recovered node reported healthy. |
| B-5 | Verification of the recovered node, then restart and verify again. | Every tenant database passes the full oracle (count, per-partition digests, logical versions, tombstones, markers, index queries); catalog ids identical to the source's; the version digest equal across the restart. |
| B-6 | Continuity: writes resume on the recovered node. | Backup continues in new generations under the recovered identity without writing to, or deleting, the destroyed node's objects. |
| B-7 | Destruction timing: mid-load, mid-snapshot upload, mid-drill, during an object-store outage. | B-1 to B-5 hold; the loss window, if any, is exactly what design A allows (zero acknowledged loss once A ships). |
| B-8 | Scale: 1M locally, then 1M on Tigris (G1), then 100M. | Recovery time and peak memory recorded; restore memory bounded (P11). |

R10a's read-only design (`feec75e5`) names four further gaps. Each gets a fail-first requirement: the scenario must
first be shown to fail (or to pass only through a wrong mechanism) on the current code, then pass with the fix.
Repeated operator catalog-opens and test-only tenant-id adoption satisfy none of them.

| Id | Gap | Fail-first scenario | Pass condition |
| --- | --- | --- | --- |
| B-9 | CDC anchor ids are file-specific (the change log's ids restart in a rebuilt file), so anchors that cite them do not survive a PITR rebuild | Anchor a catalog state, destroy the node, rebuild `_system` by PITR onto a fresh node, then verify the anchor. Control: today's file-specific id does not bind to the rebuilt history (mismatch, or a match that does not identify the same logical state). | A restore-stable stamp binds the anchor to the same logical catalog history before and after any rebuild; a rebuilt history that differs from the anchored one fails closed. |
| B-10 | The anchor proves a revocation happened but cannot recover its content | Revoke an API key or member (anchored before the response), destroy the node before the revocation's change-log segment reaches the bucket, recover. Control: today recovery either blocks forever or restores a catalog where the revoked credential works again. | The revocation's content is externally durable before the response, recovery re-applies it, and the revoked credential answers 401 after recovery; no revoked access comes back. |
| B-11 | A tenant database whose backup is missing could lazily become an empty database | Recover a node from a bucket copy in which one tenant database's backup prefix is missing; then read, write and list that database. Control: today the first access may create an empty database (loss masked as success). | The database stays unavailable behind a restore-pending gate with a typed error; no empty file is created; writes are refused until an explicit operator decision. |
| B-12 | Dead recorded members block every future restart, and their orphaned intents are never resolved | A cluster whose recorded membership includes a destroyed member with in-flight catalog intents; restart the survivors. Control: today every restart stays blocked (fail-closed with no way forward except repeated catalog-open). | An audited retirement operation (operator, logged, anchored) retires the dead member and resolves each orphaned intent explicitly (applied or aborted, never silently dropped); restarts then proceed; an unaudited path does not exist. |

## Test ownership across owners

| Area | Product fail-first tests (in the product suites) | End-to-end validation on built binaries |
| --- | --- | --- |
| Backup defects P1–P5, P8–P10 | this program's local candidate branches (one writer per branch) | this harness (O6): before/after on the old and the integrated binary |
| P11 restore memory | unowned; staged after the restore and shipper candidates | this harness: peak memory per phase |
| No acknowledged loss (A-1 to A-7) | T1 (storage owner), including H4 power-loss tests (#143) | this harness: source destruction at each boundary, lost responses, outages |
| Whole-node DR, catalog and anchors (B-1 to B-12) | R10a (catalog and security owner), after its route-fix review | this harness: fresh-node recovery, fault matrix, restart and resume |
| Shared fixtures | FakeS3 (additive changes only: R10a `RemoveObject`, multipart), H4 `WalSyncFaults` (T1), region processes (E2E owners) | PitrLab `s3-serve`, the response-dropping proxy, node destruction (this harness) |

The local candidate integration (`fix/backup-recovery-integration`) carries only the backup defect fixes; it contains
no production acknowledgment, security or availability change. Order: R10a's route-fix review, then R10a's DR
implementation with its fail-first tests and T1's acknowledgment design, then this harness's end-to-end runs on the
exact integrated binaries.

## Dependencies

| Dependency | State |
| --- | --- |
| Fixed image with P1, P2, P3, P5, P10 | local candidates; integration branch `fix/backup-recovery-integration` |
| P11 bounded restore memory | unowned, staged after the restore and shipper fixes |
| T1 #143 (H4 WAL sync faults) and #144 (`_system` synchronous FULL) on main | open PRs |
| R10a reviewed fixed head (anchor fail-closed) | under R3 review |
| Harness: response-dropping proxy | exists (O6), extended to management and fence writes |
| Harness: `s3-serve` outage faults and multipart | outage faults exist; multipart in progress (O6) |
| Harness: test-owned node destruction and fresh-node recovery driver | to build once a design exists |
| Multi-region environment for A2 | local region processes on main; live is SJC-only with no 3-region proof |
| Real Tigris variants | require the G1 approval (account, bucket, user-executed key handoff, spend stop) |

## Recommendation for the user decision

The user decides which no-acknowledged-loss mechanism T1 designs to completion first:

- **A1, remote-segment-before-ACK (recommended first):** works on the single-node path that this proof covers,
  is fully testable with the existing harness and emulator, and closes the measured loss windows (1.7 s mid-load,
  3 min 22 s after a restart). Cost: added write latency and writes that fail closed while the bucket is unreachable;
  more, smaller bucket requests. The latency is not measured: the shipper polls about once per second today, which is
  current behaviour, not a requirement; T1 proposes an on-demand trigger with group commit. Real provider durability
  and latency (Tigris) are unverified until A-6 runs on the provider.
- **A2, regional quorum:** lower latency and survives a bucket outage, but depends on multi-region replication that
  is not proven yet (live is SJC-only), and the harness's exactly-once argument is single-node.

Whole-node disaster recovery (B) is needed in either case and is independent of the choice.
