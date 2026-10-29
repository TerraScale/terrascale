# 100M-document continuous backup and PITR proof (TerraBase)

An independent acceptance harness: write exactly 100,000,000 deterministic documents to one pinned TerraBase node,
ship its change log to real Tigris, restore at many exact cuts into new storage, and prove every restore holds exactly
the acknowledged prefix at its cut. It verifies; it does not change TerraBase. Product defects go to their owners.

Status (2026-10-13, work stopped by the user): harness implemented; the product fixes are integrated on
`fix/backup-recovery-integration` and merged to main in #192 (2026-10-13 11:46Z, after local required checks on its head
`8b9c81a2`: fmt, analyzers, Fast 1496/1496, Turso 6/6; merged before CI and review, on the user's instruction to merge). G0b at
1M on the local S3 emulator passed qualification on `087a8e2b` and went RED on the G1 candidate `6dec34a7` only because a
restore crossed the 8 GB memory guard (P11, root cause found below, not fixed). **No Tigris data written yet (G1 waits for
P11 and the user's key handoff). No claim below is proven until its gate's evidence is linked in the results table.**
Decision B1 (2026-10-12): no paid or cloud run (G1–G3) until a fixed TerraBase image exists.

Continuous backup under concurrent writes is an acceptance requirement. Pausing writes around a large snapshot is only a
diagnostic scenario and never establishes that backup works under writes. A passing defect reproduction (it asserts
today's defective behaviour) is never counted as an acceptance pass.

## Claims and vocabulary

| Term | Meaning here |
| --- | --- |
| Local acknowledgement | A 2xx from the node: committed in its Turso/SQLite file with `synchronous=NORMAL` (decision 0003). A process crash loses nothing; an OS crash or power loss may lose the last acknowledged commits. |
| Remote durable | The change is inside a segment or snapshot object stored in the bucket. Backup is asynchronous (one pass per second per file), so remote durability always trails local acknowledgement. |
| Selected PITR | Restore as of a chosen cut T: must equal the acknowledged state at T exactly (exact cut) or a per-stream acknowledged prefix within proven bounds (bounded cut). |
| Latest disaster recovery | Restore of the newest data in the bucket after losing the node. **RPO > 0 by design**: writes acknowledged after the last shipped read are lost. Measured, never claimed to be zero. |

## Pins (each experiment records its own in `run.json`)

| Item | Value |
| --- | --- |
| Code | `origin/main` `c379cc3b` (2026-10-12), storage format 3 (Turso CDC, #98), backup layout v1, `TBK1` objects, change log format 1 |
| Binary | `make terrabase-publish RUNTIME=linux-x64`, sha256 `eef2dfa3b5b6b7100964b49c0feca61d9ed2d22d8603b73e9cb8d7322a9b812b`; the harness refuses to start a node whose binary hash differs |
| Harness | this branch's `benchmark/cmd/pitr-bench` (Go 1.27.2) and `tests/TerraBase.E2E/PitrLab` (bucket audit, byte-exact copies, local S3 emulator) |
| Client | raw HTTP/2 to the node's internal listener (loopback, platform operator, no credential); the ts-go SDK is not involved |
| Node | one process, `nice -n 19`, Production environment when a bucket is configured (backup mandatory), data under the run directory |

## Workload and oracle

- **Documents:** collection `events`, 100,000,000 documents, id = ULID with time `1760000000000 + i` ms and entropy
  SHA-256(seed, i): unique by construction, id order = index order. Body = canonical JSON, about 410 B (250 to 580):
  `acct` (indexed, 20,011 values), `amountCents`, `createdAt`, `customer{id,region,vip}`, `kind`, `note` (32 to 352
  seeded letters), `qty`, `rev`, `seq`, `status`, `tags[2..4]`. Everything is a function of (seed, i, rev): nothing per
  document is held in memory.
- **Phases:** `load-a` [0, 40M), `load-b` [40M, 100M) as atomic 1,000-document transactions; then `u1` updates
  i%1000=7 (100,000), `d1` deletes i%1000=13 plus i%10000=7 (110,000, 10,000 of them updated first), `r1` re-creates
  i%10000=13 (10,000) over their tombstones.
- **Exactly-once:** every op is conditional. `create` requires no live document; `put`/`delete` carry `if_match` =
  the version the ledger recorded for the previous phase that touched the document; re-create carries `if_match` = the
  tombstone's recorded version. A transaction is atomic, so at most one attempt of a batch can apply, however late a
  timed-out attempt reaches the node; a retry is the identical request. A batch is terminal only with proof: a 200 (its
  versions) or a 412 after which every document reads back exactly as written at a version other than its precondition
  (a tombstone proven by the 404's ETag, never a placeholder). Anything else stays **unresolved**: never counted as
  written, failed or absent, and it blocks exact cuts. The probe step re-checks these server semantics on the pinned binary.
- **Phase fences (no time barriers):** a late attempt could still reach the node after its phase ended (a `create`
  only needs the document not live, so a delayed load attempt after `d1` would re-create a deleted document). Elapsed
  time proves nothing, and TerraBase has no idempotency keys. So every transaction of phase P also carries
  `assert fences/closed-P absent`, which the node evaluates before any write of the same write transaction and which
  rejects the whole transaction once the fence exists (`DocumentService.cs:424-462,570-575`; one writer connection per
  file). The fence is written (by proof) only when every batch of P is terminal by proof. A late attempt then either
  reaches the node before the fence (it cannot apply: the document is live, or its `if_match` version is gone) or after
  it (rejected). A phase with an unresolved batch can never be fenced, so the ambiguity blocks the next phase and every
  cut. **Full acceptance stays blocked whenever an unresolved operation could affect a cut or the final state.**
  Verified against product code by an adversarial review (asserts and writes share one savepoint; group-commit re-runs
  re-check asserts; one writer per file; versions never repeat; no server replay of client writes). Assumptions the
  harness enforces: the database has no peers (single node), `events`, `fences` and `markers` are never dropped, and
  after any node restart the proven fences and completed phases are read back before the next fence, phase or cut
  (`synchronous=NORMAL` may lose acknowledged commits on an OS crash; a mismatch is reported, never rewritten).
- **Ledger:** `ledger/<phase>.jsonl`, one fsynced line per intent (start), ambiguous attempt (retry), proof (ack,
  present), rejection or unresolved mark, keyed by (phase, batch, attempt). `versions/<phase>.bin` holds each document's
  returned version in a 64-byte slot, fsynced before the terminal line. Counters: attempted batches, attempts,
  acknowledged, proven by read, unresolved, retries, ambiguous, throttled. Retries cannot inflate any count.
- **Deterministic acceptance tests** (`go test -race ./internal/pitrbench`): delayed commit of a timed-out attempt in
  both orders, lost acknowledgement (stream reset, 500 after commit), duplicated retries, delete/re-create with tombstone
  versions, a missed delete that a re-create must not hide, absent-without-tombstone left unresolved, crashes at intent,
  after commit, after response and after versions, exact cuts refusing unresolved batches while pinning the ledger, and
  a lost answer to the phase-fence create (retried as the identical conditional create, proven by its 412 and a read).
  These run against an in-process fake: **fake-only** until a real-binary run exercises the same path.
- **Real-binary lost answers** (`TestRealBinaryLostAcknowledgements`, opt-in through `PITR_REAL_*`): a test-only h2c
  proxy between the writer and the pinned node lets the node commit, discards its answer and closes the connection, for a
  create (load-a), put `if_match` (u1), delete `if_match` (d1), re-create over the d1 tombstone (r1) and the load-b
  phase-fence create. Each must be ambiguous in the ledger, its retry must get 412/404, proof by read must record exactly
  the versions of the node's single commit, and a fenced exact cut plus an exact source verify must pass after every
  phase. **Not covered:** patch, increment, append or any other non-idempotent update: the workload has none, so no
  evidence here makes them safe to retry.

## Cuts

- **Exact:** taken with no writer running. One second after the last acknowledgement of every applied phase, the
  harness writes a marker document (`markers/<cut>`) and waits until `/v1/backup` shows the database file's newest
  shipped read at or after the marker's request start with nothing pending: that read began after every applied batch
  committed. A blocked pass (snapshot, drill) never advances, so this times out rather than passing falsely. The cut is
  recorded with the ledger line counts it was taken against; the next phase starts at least 1.1 s later.
- **Bounded:** taken while `load-*` runs. The restore must hold, per writer stream, a prefix of batches in send order,
  every batch all-or-nothing; no batch first sent after the cut; and, given the bucket audit's segment times, every
  batch acknowledged by the second-latest segment at or before the cut. The acknowledged-but-absent batches give the
  realized loss window at that instant.

| Cut | When | Expect | Restore source |
| --- | --- | --- | --- |
| C0 | after setup | empty collection, index present | generation G0 snapshot |
| C1 | bounded, mid `load-a` (~20M) | per-stream prefix | G0 log |
| C2 | after `load-a` | 40M exact | G0 snapshot + 40M rows of log |
| — | graceful restart → G1 with snapshot S1 at 40M (and its drill); `load-b` starts immediately, so writes run while S1 is copied and uploaded (acceptance: backup must keep up; a paused variant is diagnostic only) | | |
| C3 | bounded, early `load-b` while S1 uploads | S1 plus a per-stream prefix | S1 + G1 log |
| CK | SIGKILL during `load-b` while uploading (~45M), restart → G2 | process crash loses no local ack; record unshipped acks at kill | G1/G2 |
| C4 | bounded, mid `load-b` (~70M) | per-stream prefix | G2 log |
| C5 | after `load-b` | 100M exact | G2 log |
| C6, C7, C8 | after `u1`, `d1`, `r1` | updates; tombstones (incl. over updates); re-creations | G2 log |
| T±1ms | the first segment containing `u1`/`d1`/`r1` writes: its time and 1 ms before | the bounded rule on the mutation | G2 log |

## Restores (never over the source, never over existing data)

- **Path A, product API on the source node:** `POST …/point-in-time/restore` into a new database; reads Tigris
  directly. On this node the restored database is itself backed up (a new generation with a full snapshot), which costs
  upload and storage; used at C5 and C8 only.
- **Path B, isolated target node:** a second node with its own data directory, bucket settings naming a byte-exact local
  copy of the source's Tigris objects (PitrLab `download` → `s3-serve`), and the source's organization/project/database
  ids prepared before startup by PitrLab's `prepare-target` test fixture. `adopt` then validates that live catalog
  registration and initializes the collections and index through the surviving routes. Database create routes generate
  IDs, while encrypted backup objects require their original paths, so ordinary create cannot replace this fixture.
  This is tenant backup replay with explicitly seeded catalog identity, not production catalog recovery or whole-node
  disaster recovery. It restores from the copy alone, without any source file. Used for every cut, then the target
  node is restarted and every restored database verified again. The source run directory and the downloaded copy are
  never modified; every mutation and every target node works on its own new copy.
- **Fault matrix (isolated mutated copies, PitrLab `mutate`):** missing middle segment, missing last segment at or
  before the cut, missing final segment of a generation, corrupted and truncated segment, duplicated start position,
  stray unparseable key, missing or corrupted snapshot, wrong master key, ambiguous upload (stored then 500, via
  `s3-serve` faults), SIGKILL during upload. Each must fail clearly or restore exactly; a restore that silently returns
  an older state is a product defect. Known risk from code: restore stops at the first segment after the cut before any
  gap check (`BackupReader.cs:144-147`), so a missing last segment at or before the cut may truncate silently.
- **Catalog anchors (R10a, not in the pinned binary nor in the 4cfbf85d integration):** `catalog-anchor/<machineId>`
  objects live outside `v1/` and are never deleted, expired or swept. `s3-serve` stores and serves them like any key and
  never applies a fault to them; `mutate` refuses any key outside `v1/`; `download` copies only the database and keys
  prefixes, so a Path B copy holds no anchor. A binary with R10a may then fail CLOSED on a target (non-genesis missing
  anchor: UNKNOWN until trusted-peer proof or operator action): that is correct behaviour, recorded, never worked around
  and never a harness pass. Rolling R10a onto an existing non-genesis cluster needs one explicit operator catalog-open
  (an approval and recovery prerequisite the harness never automates); restoring a bucket copy can roll anchors back, so
  `_system` PITR behind an anchor needs explicit operator recovery, never automatic success.

## Verification of every restore and of the source

Parallel `$id` range queries over 1,000 partitions, merge-joined with the oracle: full count, missing, extra, foreign,
duplicate, out-of-order, content (canonical JSON) and resurrected tombstones, per-partition SHA-256 digests of
`id, content hash` and a global digest; versions exactly equal to the ledger on the source, and on restores the logical
revision plus a per-partition version digest compared across the target node's restart (a restore's ETags include its
own database identity). Plus markers (every exact cut's marker up to the cut, none after) and indexed `acct` queries
against the oracle. The bucket audit (PitrLab `audit`) decrypts and authenticates every object and checks contiguity,
duplicates, gaps, time order, snapshot positions on segment boundaries and data-key references.

## Gates (no gate substitutes for 100M)

| Gate | Target | Pass |
| --- | --- | --- |
| G0a | pinned binary, no backup | probe all PASS; every phase at 200k verified exact; SIGKILL during writes then resume: nothing lost or doubled; throughput, CPU, RSS, bytes per document measured |
| G0b | pinned binary + local S3 emulator (`s3-serve`), **emulation, not Tigris** | the full cut matrix and the fault matrix at 1M |
| G1 | real Tigris, 1M | uplink and segment upload rate measured; full cut matrix; audit; Path B from a byte-exact copy |
| G2 | real Tigris, 10M | snapshot of a ≥5 GB database (single-PUT size and 15-minute timeout: Tigris documents no single-PUT or object size limit as of 2026-10-12, so it is measured; AWS S3 caps a single PUT at 5 GiB), sustained writes during the snapshot (backlog over 256 MiB restarts the generation?), measured rates → 100M runtime |
| G3 | real Tigris, 100M | everything above at full size |

## Estimates for 100M (G0a measurements on the pinned binary, 2026-10-12, local, shared host; extrapolated)

| Quantity | Measured | 100M extrapolation | Basis |
| --- | --- | --- | --- |
| Load rate, 1 / 8 / 16 / 32 writers | ~850 / 713 / 628 / 488 docs/s | **33–40 h of load** | writes to one database serialize on one core; more writers are slower |
| Node CPU, RSS while loading | 102–114 %, 282–343 MiB | — | one core |
| Source data directory | 2.33 KB per document (465.6 MB at 199,800) | **~233 GB** plus WAL | linear assumption, only observed to 200k |
| Disk writes during load | ~84 KB per document (one sample) | **~8 TB written** | a single 10k-document sample; to be re-measured |
| Verify (or reconcile) rate, 4 / 8 / 12 workers | 3.9k / 5.6k / 6.6k docs/s | **~4–5 h per full verify** | `$id` range queries |
| Node RSS at 1M during G0b | up to ~5.0 GB | unknown | one observation with restored databases on the node |

Consequences: one full 100M pass (load, mutations, fences, cuts, about six full verifies and restores) is several days
of wall time on this host, the source alone takes ~233 GB and each full restore or byte-exact copy needs its own
~233 GB, so the 550 GB reservation in A5 does not hold more than one isolated restore at a time. The A5 and A3 numbers
are recomputed from G0b measurements before the approval bundle is finalized. Tigris prices (official page https://www.tigrisdata.com/pricing/, verified 2026-10-12): Standard (global, paid once, no
per-region charge) $0.02 per GiB-month, Class A (PUT, COPY, POST, LIST) $0.005 per 1,000, Class B (GET, HEAD)
$0.0005 per 1,000, egress and retrieval free, no Standard minimum duration. The account's free allowance may already be
used. A monitored spend stop is the harness stopping its writes; it is not a provider billing cap, and retained data
keeps costing $0.02 per GiB-month until the user deletes it.

## Full acceptance scope (not waivable, decided 2026-10-12)

**Qualification matrix (2026-10-12).** A required-success set may be empty only in exploratory runs. Before qualification a NONEMPTY matrix is frozen:
- every known fully durable checkpoint or transaction boundary actually restores (201) with exact counts, per-partition digests, versions and identity: snapshot times, exact quiesced cuts with a later shipped witness, and the latest durable point (tail watermark);
- the fault cases are kept separate, each with its own expected outcome.
- harness: `MODE=qualification` freezes the required set `C0 C2 C5 C6 C7 C8 S*` (`S*` = cuts at snapshot times) and refuses an empty one; the run is red if a required cut does not restore with 201 and verify exactly, any restore fails, or half or more of the restores are refusals. `MODE=exploratory` takes `MUST_RESTORE` (empty by default) and is marked not qualification evidence. `pitr-run.sh` requires `MODE` on every call.

A typed refusal can pass only a safety expectation. It never counts as a functional restore success, and a run of all or mostly refusals is invalid (red).

**A-7 proven contract (`3290a26b`, restore owner report, lead-reviewed 2026-10-13).** `change_time` (Turso v0.8.2 source and `TursoChangeTimeTests` on the real driver): every captured row, the COMMIT row too, gets its own wall-clock `unixepoch()` in whole seconds (truncated), written inside the transaction before it is durable. The log now records each transaction's first-row and COMMIT-row seconds (record kind 6), and a read that reached the newest commit ends with a read mark holding the read's start time in milliseconds (kind 7). A restore at T replays whole transactions only: every transaction whose COMMIT second ends by T, none whose COMMIT second starts at or after T. Completeness is proven by one of: the snapshot was taken in T's millisecond; the last segment read by T was read in T's millisecond and ends with a mark at or after T; the segments read after T reach a transaction whose COMMIT second starts at or after T (a witness) or a mark at or after T; the generation's next snapshot is exactly at the replayed position. Otherwise `409 backup_history_incomplete` (hole, nothing shipped after T, ended generation) or `409 restore_point_unproven` (T inside a COMMIT second, an untimed transaction, times going back). Guaranteed 201: snapshot times (S*); whole seconds (or any time in a second without a commit) followed by a shipped transaction or mark in the same generation with contiguous history, which covers C0, C2, C5–C8 once the post-cut nudge is shipped; the DR latest point (whole second at or before the newest mark, from the bucket); quiet times closed by the next snapshot. Limits: a transaction committed within T's own millisecond can be included; a backward clock step between a commit and the shipper's read, or between a COMMIT row and its acknowledgement, is undetectable (steps between transactions are caught); logs from before this change restore only at snapshot times and times closed by the next snapshot; a live node's window `latest` is still the shipper's progress and may be refused (follow-up routed); a read stopped by the 32 MiB budget writes no mark. Before-fix evidence: all 8 new history tests and the bucket-window test fail on `6b3dbc3a`; no existing restore moved from 409 to 201.

Validation requirements for the two designs: [pitr-design-validation.md](pitr-design-validation.md).

- **D6 decided:** when bucket history up to T cannot be proven contiguous, a restore at T is refused with a typed
  error, never 201 and never 500 (routine correctness; `fix/backup-restore-history-proof`).
- **Whole-node disaster recovery** is in full scope: rebuild `_system` and every database of a lost node from the bucket
  alone onto new storage, with R10a's explicit operator catalog-open for a non-genesis cluster (never automatic). No
  product path exists today (`backup.md` "Status of the work"); the harness's Path B (adopting tenant ids on a fresh
  node, per database) is test scaffolding, not disaster recovery. Needs a product owner and design.
- **No acknowledged loss** is in full scope. Today a 2xx means a local commit (`synchronous=NORMAL` for tenant
  databases) and backup ships asynchronously, so a lost node loses writes acknowledged after its last shipped segment
  (G0b: 1.7 s at a mid-load cut, 3 min 22 s after a restart, P10). Closing it needs acknowledgments that wait for remote
  durability: either the write's commit position is inside a stored segment before the 2xx (ack after ship; adds
  unmeasured latency, since today's once-per-second polling is current behaviour and T1 proposes an on-demand trigger
  with group commit, and makes writes fail closed while the bucket is unreachable), or a quorum of other
  regions holds it (the superseded decision 0010 design). That is a storage-contract change for the storage owner (T1)
  through main; until it exists, G3 can only measure the loss window, never claim zero.
- Incremental gates (G1, G2) may run before these exist; they never count as completing full scope.

## Evidence status (what is and is not proven)

- G0a (no backup, 200k, fenced harness): source-state evidence at its cuts only; not backup, PITR or general
  exactly-once proof.
- G0b runs made before the fenced harness (`runs/g0b-*`, old CLI): **provisional** recovery and reproduction evidence;
  their writes carry no fence asserts, so fenced/full acceptance needs fresh runs.
  Provisional passes: Path A C2/C5/C8 exact and C4 bounded; Path B C0–C8 exact before and after the target restart;
  ambiguous-upload run exact. **Not run:** restores of the 10 corrupted, truncated, missing, duplicated and stray-key
  copies, the wrong master key, a real mid-load C1 cut, and the T±1 ms mutation cuts.
- Fake-only until exercised on the real binary: the committed-but-lost-acknowledgement path, the clock-free cut
  quiesce, the post-cut window extension, the shipped-history recorder and audit-problems-fail-verify.
- Defect reproductions (P1–P3) demonstrate bugs; they are never acceptance passes.

## Safety

- Only test-owned paths: `/home/mariogk/tb-100m-pitr/<run>` locally, one dedicated bucket in Tigris. No production or
  preview organization, app, bucket, dashboard or shared test data.
- Credentials live only in a 0600 env file the node reads (whitelisted names); the harness never prints or logs them.
- Nothing is deleted automatically: no bucket, object or local run cleanup. Deletion needs an exact, separate confirmation.
- Heavy phases hold `flock -o /tmp/ts-heavy.lock` (no child, node or emulator inherits it); every process runs at
  `nice -n 19`; writes stop below 150 GB free disk. Every node sampler stops its node above 8 GB RSS (`RSSStopBytes`) and
  `pitr-run.sh rss-report` records each node's peak RSS per phase and per restore.
- **G1 spend and resource monitor** (`pitr-bench monitor --run DIR --lab-dll PITRLAB_DLL`, alongside every G1 run):
  every 10 minutes a read-only listing of the whole dedicated bucket (PitrLab `inventory`, credentials only from the
  run's 0600 env file, the master key never passed), the node's backup counters from its metrics port, and a cost
  estimate at Tigris list prices ($0.02/GiB-month, $0.005 per 1,000 class A, $0.0005 per 1,000 class B, egress free):
  class A = stored objects + the parts the node's snapshot bytes can make + 5 per failed pass + the monitor's and
  harness's LISTs + drills; class B = the harness's GETs (downloads, Path A restores) + a generation's objects per drill.
  It checks node RSS every 10 s and the run base's size every minute. At 4.5 GB stored, a projected $0.80 for the first
  month, 8 GB node RSS or 28 GB of run base it stops every writer of the run (SIGTERM: in-flight batches stay
  unresolved) and the node (graceful), records `evidence/monitor-stop.json` and fails. Every listing is a line of
  `evidence/monitor.jsonl`; `evidence/monitor-final.json` holds the final estimate. The limits are constants; the only
  overrides, `--test-max-stored-bytes` and `--test-interval`, lower the stored limit and shorten the listing interval for
  a run against the local emulator, and are refused above the G1 values.

## Product defects found

Candidate fixes (2026-10-12, at the user's request; local branches off `origin/main` `4cfbf85d`, not pushed, no accepted
owner confirmed yet): `fix/backup-restore-history-proof` (P1, P8, typed error), `fix/backup-shipper-backlog-and-retry`
(P2, P3, P5), `fix/backup-multipart-upload` (P4), `fix/write-ack-after-commit` (P9, P6 wording). Each converts its
reproduction into an acceptance test of the correct behaviour.

Local integration (owned by this harness's lead; local only, no upstream ownership, no push, no merge): branch
`fix/backup-recovery-integration` at `/home/mariogk/tb-100m-pitr/worktrees/backup-recovery-integration`, created from
`origin/main` `4cfbf85d`. Each fix branch is merged in only after its own report; the canonical integration SHA, the
included and excluded fixes and the combined results (build, analyzers, format, the full Fast suite, each fix's
acceptance tests, the backup Slow tests, an independent review, then the harness's fenced recovery and fault matrix
against the published binary of that exact commit) are recorded here. Standalone green fix-branch tests never replace
combined evidence.

| Item | Value |
| --- | --- |
| Upstream order | T1 (storage owner, PR #143 note 2026-10-12): #143 (WAL sync faults, H4) and `fix/system-synchronous-full` (decision 0012, `_system` synchronous FULL via `SyncsEveryCommit`) land on main first; the candidates then rebase onto that main and get T1's written acceptance; R10a reviews anything touching the backup-bucket anchor. Our reply with file ownership: PR #143 comment 6130556906. Our decision records start at 0013. |
| Upstream order (decided 2026-10-12) | T1 #143 → T1 `fix/system-synchronous-full` → R10a (catalog gate and anchor, after R3's written approval of its exact head and with the missing-anchor case fixed to fail closed) → these candidates, rebased onto that main. R10a changes no `Features/Backup` source; its known fail-open head `79ecf7c2` (missing anchor treated as not restored) is not adopted or encoded as accepted anywhere here. If R10a's reviewed SHA is not ready when the candidates have T1's acceptance, the order is renegotiated through main; never on top of a fail-open head. |
| Upstream progress | #143 merged 2026-10-12T20:07Z and #144 (`1e421848`) at 20:08Z; `origin/main` = `4afd7d9d`. Next: after the running combined gates on `e9a824fa` finish (pre-rebase local evidence), merge `origin/main` into the integration branch, keep #144's `SyncsEveryCommit` behaviour, and rerun every gate on the new SHA before the binary is published. R10a is not on main; it enters only at a reviewed fixed head. |
| R10a status (owner report 19:13) | local head `f2c32f99` supersedes `79ecf7c2` and fixes the non-genesis missing-anchor fail-open; R3 exact-head review pending (PR #108 comment 6130910267). Integration stays gated on that written review, not the owner report. Rolling out to an existing non-genesis cluster needs one explicit operator catalog-open for the first anchor: a future approval and recovery prerequisite, never automated by the harness. R10a adds `FakeS3.RemoveObject` (overlaps the multipart branch's FakeS3 changes: additive regions, hunks to be listed). |
| Retry safety | An ambiguous persisted operation is retry-safe only with operation-level idempotency (create with `if_none_match`, put/delete with `if_match` on the exact prior version), proven by the harness's real lost-response cases; never claimed for unconditional writes, increments/appends, delete-then-recreate without preconditions, or security anchors. |
| R10a contract kept | anchors `catalog-anchor/<machineId>` live outside `v1/` and are never deleted, expired or swept; restoring a bucket snapshot can roll anchors back and needs an explicit recovery decision; `_system` PITR behind the anchor needs operator recovery, never automatic success; TBK1 format 1 and old `_system` key ids stay readable; `PutAsync` stays whole-object replacement and a same-key retry never lets an older body overwrite a newer one; IObjectStore/DataKeys stay registered only with backup configured; management commits with revoking ops anchor before answering and a failed anchor stays fail-closed (the commit-ack fix covers tenant transaction routes only) |
| Local frozen candidate | built on `4cfbf85d` with no T1 or R10a code: local evidence only. The final frozen matrix runs on the rebased integration SHA. |
| Overlaps to re-test after the rebase | commit ack ↔ T1 (`SqliteDatabaseStore.cs`, separate hunks) and ↔ R10a (anchor fault test re-run); multipart ↔ R10a (FakeS3 API, single-PUT anchors); shipper ↔ R10a (retention never outside `v1/`, unwrap counts in `CustomerBackupBoundaryTests`); restore ↔ R10a (`TerraBaseJsonContext.cs` line 47, no automatic `_system` restore) |
| Integration SHA | `6dec34a7` = `e9a824fa` + `origin/main` `4afd7d9d` (`b83e5c93`, no conflicts, #144 `SyncsEveryCommit` kept) + write-path robustness `2010ac17` (`18e74b6c`) + multipart follow-up `f7137ccb` (`223ef62e`, clean) + shipper `fix/backup-drill-off-shipping-path` `39d59677` (`dfb88be7`; merged with its cherry-pick `db86f1d4` of `f4f197ea` as the merge base, so only its own four commits apply; the merged tree differs from `223ef62e` by exactly `db86f1d4..39d59677`). + F4 `fix/write-ack-after-commit` `20abe0a9` (`4743e31f`; `GroupCommitTests.cs` conflict resolved by keeping both sides' tests; api.md now says the logged post-commit accounting of `2010ac17` also answers committed). + A-7 `fix/backup-restore-history-proof` `3290a26b` (`62bb65db`; `TursoChangeTap.cs` and backup.md conflicts resolved by hand: the 32 MiB read budget kept, a read mark only after a read that reached the newest commit, `BackupAnchor` published by the caller after cleanup). + `248ff3bc` (the shipper's two test decorators of `IWalBackup` forward the A-7 `CutLog` and `ReadMark`; the first combined build failed without them) + F4 hardening `518c1473` (`16ec14cd`, clean: a batch that changed reader state is never answered committed after a failed step; a throwing log call cannot skip post-commit steps or stall the batch loop, fail-first: the write hung to the 2-minute timeout on `20abe0a9`). + live window `latest` `fix/backup-window-proven-latest` `e597e7bc` (`5483818a`; lead-reviewed: on the shipping node `latest` is the newest stored snapshot's time or the whole second at or before the newest stored read mark (the mark itself when it ends a segment read in its millisecond), forward only within a generation; fail-first: a daily snapshot pin with no new commits and a commit in the mark's second both answered 409 at the advertised `latest` and restore 201 exactly after; agent report Fast 1465/1465, Turso 6/6) + `b9113066` (drops the duplicate decorator members both `248ff3bc` and the window branch added). + restore follow-ups `473c25a8` (`be479a33`: 409 `backup_unreadable` for objects failing authentication or cut short and for a failed integrity check; 409 `backup_history_incomplete` for an overlapping segment, checked before its download, and for times outside the window but inside [retention start, now]; 400 only for future times or before retention; `restore_point_unproven` names the nearest earlier placeable second; fail-first for every case) and `da7a56de` (`491d9e5d`: a window or plan read from the bucket falls back past an unreadable newest segment instead of 503) + `4b750b42` (the window endpoint description states the shipping node's proven latest). + multipart proof `d8f22113` (`087a8e2b`, clean; lead-reviewed: a completed multipart upload is proven by the exact length on HEAD plus either its id in `x-amz-meta-terrabase-upload` or S3's multipart ETag of its own parts; an unproven completed snapshot is deleted (snapshot keys are unique per attempt) and counted in `terrabase_backup_unproven_uploads_deleted_total`; an unproven segment stays; fail-first with FakeS3 variants that drop metadata; agent report Fast 1419/1419, Slow MultipartBackupTests 4/4; MD5 for the ETag comes from the AWS SDK helper, not a suppressed analyzer rule). G1 must check, on Tigris, metadata through CompleteMultipartUpload and the composite ETag: with neither, every upload over 128 MiB fails visibly and backup stalls. + F4 docs `db28dc5d` (`b850353c`: api.md names the post-commit failures that answer outcome 3) + restore follow-ups `fix/backup-restore-followups` `fd8a342b` (`8e772187`: a real-driver test that a commit returned on the writer is visible to the tap's reader in the next newest-commit query, 0 misses in about 94,000 checks over three runs, and the test detects a reader that keeps a read transaction (30,000+ misses); the plan answers times outside the window as the restore does (400 only after now or before retention, else 409); `_system` restores only at its snapshot times, closing the PLAUSIBLE follower path; agent report Fast 1475/1475, Turso 6/6). + `4ce58f03` and `b66f47a8` (`7b99716d`, `6dec34a7`: a snapshot whose read transaction cannot end, or that fails between BEGIN and its pinning read, replaces the tap's reader connection, so no later read mark rests on a stale view; fail-first: the next read appended 0 bytes before) + shipper `fix/backup-shipper-shutdown-and-retry` `507864fb` (`6e207e5d`, review round 4 items 1, 2, 3, 6: shutdown ships the last writes beside a cancelled daily upload and starts no generation (except a just-detached customer destination's final platform snapshot); a failing daily snapshot backs off on its own (1 s to 5 min), stays in `lastError` and no longer holds segments; a drill cancelled with its generation runs for the next one; each fail-first on `087a8e2b`; item 5, the generation-start checksum on the shipping path, deferred: it needs a scratch-disk bound).
  - Shipper `39d59677` (lead-reviewed; agent report on its head: fmt and analyzers pass, Fast 1436/1436, Turso 6/6, Slow Backlog 1/1, KeyCache 2/2, CustomerBackupRetention 1/1). Drills and daily snapshot uploads run beside shipping (P10 part 2); a short outage keeps the backlog in `turso_cdc` and a due retry resends the failed segment first, and the generation drops (reason `outage`) only after about 8.5 min of continuous failure (review 1); shutdown ships the whole backlog (3); a destination switch or generation start reads the backlog before the snapshot holds writers (4); `BackupAnchor` is published only after the cleanup delete (6, a guard: not shown failing first); a deferred daily snapshot is counted (`terrabase.backup.snapshots_deferred`) (8). Fail-first: P10 part 2, 1, 3, 4 and 8 failed on the code before the fix. Review notes: the daily snapshot now moves `LastShipped` to its pin before the snapshot object is stored (sent to the A-7 owner: a pin with nothing new shipped is not a read mark); the `busy` deferral counter grows every pass while an upload runs (cosmetic). Open: restart storms can fill the 4 job slots with first-snapshot uploads; a cancelled drill waits for the next snapshot.
  - `f7137ccb` (lead-reviewed): removes the false bucket-lifecycle claim. Each multipart upload gets a whole-object deadline (15 min + 1 min per extra part). Daily, and at start, it aborts uploads under the file's own `v1/` prefix older than the largest possible deadline + 1 h (about 7 days); anchors and other nodes' uploads are never touched.
    - Tigris docs: list and abort are supported; no incomplete-upload lifecycle rule exists; metadata surviving multipart completion and HEAD consistency are not documented, so G1 must check them.
    - Agent report: Fast 1419/1419, Slow Multipart 4/4.
    - Limits: orphans can live up to about 8 days; customer buckets are not cleaned; it was never run against Tigris. Review nit: check how the SDK sets `DateTime.Kind` on `Initiated`.
  - Earlier merge: The merge had a test-file conflict in `GroupCommitTests.cs`: both sides added tests, both kept. No gate has run on it yet.
  - `2010ac17` (lead-reviewed): writer and admission release and the transaction gate are exception-safe; accounting failures after a commit are logged, not answered with a 5xx. Agent report: Fast 1422/1422.
  - **F4 post-commit consistency `dee0477a` + review follow-ups `20abe0a9`** (agent report on `20abe0a9`: fmt, analyzers, build pass; Fast 1426/1426; Turso 6/6. Collection-name, statistics and incarnation updates run on the write thread right after the commit, each failure logged; a `Commit()` that throws after writing still drops the collection cache and moves the commit count; `Settled` removed (a group-commit batch never changes collection names or the incarnation); no fail-first test for the window and the throwing `Commit()` (no driver hook). Delta re-review by the independent reviewer running.) Earlier: (on `4d04664f`; agent report: fmt, analyzers and build pass; Fast 1426/1426; Turso 6/6; five fail-first tests that failed on `4d04664f`):
    - each post-commit step runs in its own guard;
    - the commit count and wake-up run in a `finally`;
    - a client batch is answered committed only when `Committed` and `Settled` are both set (collection cache and incarnation followed the commit); otherwise it gets the unknown outcome;
    - `_system` still fails closed.
    - Untested: the `Settled = false` path (no injection point); the merged tree was not built.
    - Independent review: no blocking findings; follow-ups with the owner (Excluded row). T1's acceptance is not verified.
  - **Removed again:** commit-ack follow-up `4d04664f`, merged at `cafaf04b` and then reset out of the local branch. A failure right after `Commit()` skips `ForgetCollections`, `StatisticsChanged`, `AdoptIncarnation`, the commit signal and `changesCommitted`. That leaves stale state, and waiters that only progress at the next commit. Back with the F4 owner, with fail-first tests: no later commit, cache and incarnation observations, `_system` fail-closed on the new hook. It must not answer success while critical state is unknown, and it needs an independent review.
  - **Not included yet:** history-proof `6b3dbc3a` (equal-start empty segments; lead-reviewed OK). A-7 continues on that branch, and the branch merges once A-7 is done.
  - **T1's acceptance of any write-path hunk is NOT verified.** A clean merge-tree is not acceptance. Hunks to submit: `SqliteDatabaseStore.cs` `End`, `SqliteStorageTransaction.cs` dispose `finally`, `DocumentService` accounting, and later F4's.
  - Earlier: `e9a824fa` (all four candidates merged; not canonical until the combined gates and the independent review pass; not canonical until all candidates are merged and the combined suite passes) |
| Included | `fix/backup-multipart-upload` `ef5aac96` (P4; lead review: approved, follow-up requested: prove each completed multipart upload by evidence bound to its own bytes (composite ETag or checksum, or a verified key-must-not-exist precondition), failing closed otherwise, with a fail-first control where Complete answers 200 with an error while an older equal-length object exists; a length check alone is not proof); `fix/write-ack-after-commit` `4ca6892e` (P9; lead review: approved, `_system` stays fail-closed, three-outcome API wording). Agent reports: Fast 1418/1418 and 1420/1420, multipart Slow 3/3, fmt and analyzers clean. Combined run on `a1f42269` invalid (the build could not resolve `TsDotnetRoot` from the integration worktree; no test ran); on `502de814` (with `TsDotnetRoot` set): build 0 errors, Fast `GroupCommitTests` 9 total, 0 failed (includes the two commit-ack tests); Slow `MultipartBackupTests` 3 total, 0 failed (build 0 warnings, 0 errors). The full combined suite runs once every candidate is merged. `fix/backup-restore-history-proof` `3c33d59e` (P1, P8; lead review: approved; 409 `backup_history_incomplete`; window `latest` stays in shipper memory, documented). Repro files removed from the integration (`0d66c115`). Combined on `0d66c115`: build 0 warnings, 0 errors; Fast PointInTimeHistoryTests 4/4, PointInTimePlanTests 13/13, CustomerBackupTests 24/24, GroupCommitTests 9/9, ContinuousBackupTests 10/10, RestoreWindowTests 3/3 (0 failed). Multipart follow-up `86810626` merged at `07b5cc7d`: completion proven by this upload's own metadata id (`x-amz-meta-terrabase-upload`) plus exact length on HEAD, failing closed; control test with an older equal-length object; SDK v4.0.104.1 observed raising on 200-with-error; Tigris metadata behaviour unverified until G1. Combined on `07b5cc7d` (`make terrabase-test-fast` with `TsDotnetRoot` set): builds 0 warnings, 0 errors; Turso 6/6; **Fast 1424/1424, 0 failed, 0 skipped**; Slow `MultipartBackupTests` 4/4. `fix/backup-shipper-backlog-and-retry` `f4f197ea` (P2, P3, P5, P10 lag; agent report Fast 1423/1423, Slow `BackupBacklogTests` 1/1, fmt and analyzers clean) merged at `8ef18e1c`; repro files it carried removed again (`e9a824fa`). Combined on `e9a824fa`: fmt and analyzers pass; **full Fast 1429/1429**, Turso 6/6; Slow MultipartBackupTests 4/4, BackupBacklogTests 1/1, BackupKeyCacheTests 2/2, CustomerBackupRetentionTests 1/1, TigrisBackupTests skipped (no credentials); **BackupKeySaturationTests 0/1 FAILED**. Cause (detailed rerun): the priming restore answers 409 Conflict. `3c33d59e`'s metadata pre-check (`PointInTimeRestore.cs` ~368, `segment.Start > last.Start`) rejects equal starts. The fixture's 65,534 empty segments all start at 0, which the download-time proof (`BackupReader.TakeAsync`) accepts. Routed to the history-proof owner: non-decreasing starts in the pre-check, exact contiguity still proven at download, the test unchanged, plus a fail-first test showing a non-empty duplicate start still gets 409. Independent review round 3 done (next row). |
| Review round 3 (`4cfbf85d..e9a824fa`, read-only) | **Blocking:** (1) CONFIRMED: during a backlog, passes that aren't due for a retry still read up to 32 MiB each, so about 9 s of failed PUTs pass 256 MiB and drop the generation (P3 with a transient trigger). (2) CONFIRMED: with capped reads, a segment's time is its read time, so a restore at T can lack writes acknowledged before T, by up to the backlog lag. **Decided A-7 (2026-10-12):** a restore at T includes every write acknowledged before T, or answers a typed refusal; never a silent partial success. The same gap exists without a backlog: a write acknowledged in the ~1 s before the next read is missing from a 201 at T. Fix routed to the restore owner, who must verify `change_time` semantics and clock behavior and define a durable complete-prefix proof before any T is called exact; unproven cases get a typed 409. Harness: a two-sided A-7 check plus a before-fix control on the pinned binary. No exact PITR is claimed from coarse timestamps. **Non-blocking:** (3) the shutdown flush ships at most 32 MiB; (4) switching a customer destination during a backlog holds writers; (5) the P9 window inside `CommitAsync` after `Commit()` is still open; (6) the real anchor-restore path is untested and `BackupAnchor` is set before the delete; (7) the docs claim a bucket lifecycle rule that doesn't exist, so multipart parts can be orphaned; (8) daily snapshots are skipped silently during a backlog. **Routing:** 1, 3, 4, 6 and 8 go to the shipper owner (same branch as the drill follow-up, one writer; `TursoChangeTap.cs` split by hunk with the restore owner); 2 (A-7) to the restore owner; 5 to the commit-ack owner (needs T1 acceptance); 7 to the multipart owner (Tigris list/abort support and metadata-through-completion to be cited from docs, then verified at G1). Constraints the reviewer confirmed: no overlapping segments, reads stop at COMMIT, same-key identical retries, 409 on holes and ended generations, TBK1 and layout unchanged, anchors untouched, `_system` fail-closed. |
| Review round 4 (`e9a824fa..087a8e2b`, read-only, 2026-10-13) | **No restore-correctness finding**: no path to a 201 missing, splitting or including a transaction against the A-7 contract; the live and bucket window `latest` restore as advertised; multipart proof passes only this upload or byte-identical bytes; F4 answers committed only with `Committed` set and no reader-state change, `_system` fails closed. **Medium (routed to the shipper owner, before G1):** (1) a daily snapshot upload in flight makes the shutdown flush skip shipping (the final second is missing from the backup); (2) shutdown starts a doomed full generation snapshot when a file has none, starving other files; (3) a failing daily snapshot retries every pass without growing backoff, re-copying and re-uploading the whole file. **Low:** generation-start checksum on the shipping path; a cancelled drill silent for 24 h; an idle database's `latest` never covers its final second until a later write or snapshot (the harness writes witnesses); `_system` shipped from a catalog follower may let the next-snapshot rule prove a quiet time over uncaptured writes (PLAUSIBLE, to the restore owner); api.md and the plan's 400 versus the restore's 409 disagree with behavior (routed). Not checked by the reviewer: that a commit returned on the writer is visible to the tap's reader when the read mark is taken (routed to the restore owner as a real-driver test); Tigris metadata and ETags |
| P11 root cause (restore owner, 2026-10-13, measured, not fixed) | Measured on `6dec34a7` (`runs/p11-mem-6dec34a7-before`): the replay itself is flat (~2.3 GB through a 1M replay); managed heap is small (~190 MB committed); RSS is native. The peak comes from `TursoCopy.Write`, which copies a whole database in one write transaction: Turso v0.8.2 reads every spilled dirty page back at commit, so a copy peaks at about 2.2× the file (2.6 GB for the 1.2 GB C5 file) and glibc keeps the freed memory (1.1–1.4 GB resident afterwards; `malloc_trim` returns it). A restore runs the copy at least twice (staging into the named database, then the new database's first generation snapshot), and restarts and daily snapshots repeat it: 6.2 GB peak at the end of the C5 restore, 8.09 GB with C2 already restored on the same target. Checksum (110 MB) and `PRAGMA integrity_check` (105 MB) are not the cause. Proposed fix (unproven): commit `TursoCopy.Write` in bounded chunks (the target file is not live until published; the source keeps one read transaction), then measure the copy and the node peak before and after; then attribute the 5.2 GB spike during verify and the prefetch share. Probe: `tests/TerraBase.Fast/Backup/P11MemoryProbe.cs` (no-op unless `P11_DB` and `P11_STEP` are set). |
| Excluded (pending) | P11 restore memory (now blocking 1M qualification and G1; restore owner, `fix/backup-restore-memory`). No R10a code: R10a enters only at a reviewed fixed head. T1's written acceptance is needed for the write-path hunks (`2010ac17`, F4). |
| Combined results | **`087a8e2b` (2026-10-13 06:08Z, `integration-logs/087a8e2b-gates.log`): fmt pass; analyzers pass; Fast 1473/1473, Turso 6/6; Slow MultipartBackupTests 4/4, BackupBacklogTests 1/1, BackupKeyCacheTests 2/2, BackupKeySaturationTests 1/1, CustomerBackupRetentionTests 1/1, TigrisBackupTests skipped (no credentials). Binary `/home/mariogk/tb-100m-pitr/bin-087a8e2b/terrabase`, sha256 `e540c0c49ef4ea43cfc6ca8b41be6699215fb5fc5ba99013e84e82accfe60767`. PitrLab `o6-build5` (s3-serve keeps user metadata and answers S3 ETags; self-check 25/25). Full Slow (`make terrabase-test-slow`, `integration-logs/087a8e2b-slow-full.log`): 46 total, 44 passed, 1 skipped (Tigris, no credentials), 1 failed: `UserAuthTests.Access_tokens_from_the_real_ts_auth_authenticate_on_the_management_api` ("BaseAddress must be the HTTPS origin of a TerraBase client listener"), a known failure on main (open issue #154, `slow-failure`); this branch does not change that test or its harness.** Earlier: **`4b750b42` (2026-10-13 04:01Z, `integration-logs/4b750b42-gates.log`, every candidate merged): fmt pass; analyzers pass; Fast 1473/1473, Turso 6/6; Slow MultipartBackupTests 4/4, BackupBacklogTests 1/1, BackupKeyCacheTests 2/2, BackupKeySaturationTests 1/1, CustomerBackupRetentionTests 1/1, TigrisBackupTests skipped (no credentials). Binary `/home/mariogk/tb-100m-pitr/bin-4b750b42/terrabase`, sha256 `46aef4407ad2f45deccba8ce267b1bb2ef5f070d8abbc5937ffe4129451c6469`. The full Slow suite has not run on it.** Earlier: **`b9113066` (2026-10-13 03:25Z, `integration-logs/b9113066-gates.log`): fmt pass; analyzers pass; Fast 1466/1466, Turso 6/6; Slow MultipartBackupTests 4/4, BackupBacklogTests 1/1, BackupKeyCacheTests 2/2, BackupKeySaturationTests 1/1, CustomerBackupRetentionTests 1/1, TigrisBackupTests skipped (no credentials). Binary `/home/mariogk/tb-100m-pitr/bin-b9113066/terrabase`, sha256 `ad1cbc626a0e0690ff18410c0ac3225ada4e35a5feed26bdc816d7bb621e3f98`.** Earlier: **`16ec14cd` (2026-10-13 02:53Z, `integration-logs/16ec14cd-gates.log`, every candidate merged): fmt pass; analyzers pass; Fast 1464/1464, Turso 6/6; Slow MultipartBackupTests 4/4, BackupBacklogTests 1/1, BackupKeyCacheTests 2/2, BackupKeySaturationTests 1/1, CustomerBackupRetentionTests 1/1, TigrisBackupTests skipped (no credentials). Binary `/home/mariogk/tb-100m-pitr/bin-16ec14cd/terrabase`, sha256 `4499368b434a2508e05887795a618f0a60a3dbe175b6a112fb146ef741fa289e` (`make terrabase-publish`, linux-x64, self-contained).** Earlier: `248ff3bc` (2026-10-13 02:40Z, `integration-logs/248ff3bc-gates.log`; every candidate but the F4 hardening): fmt pass; analyzers pass; **Fast 1463/1463**, Turso 6/6; Slow MultipartBackupTests 4/4, BackupBacklogTests 1/1, BackupKeyCacheTests 2/2, **BackupKeySaturationTests 1/1**, CustomerBackupRetentionTests 1/1, TigrisBackupTests skipped (no credentials). Earlier: `62bb65db` Fast did not build (decorators, fixed in `248ff3bc`), Slow all pass; `4743e31f` Fast 1451/1451, KeySaturation 0/1; `dfb88be7` Fast 1445/1445, KeySaturation 0/1. |

Reproduced deterministically on the pinned commit `c379cc3b` by opt-in tests (in-process node, in-memory FakeS3) that
asserted the defective behaviour. They live in `tests/TerraBase.E2E/PitrRepro/`, run only with
`TERRABASE_PITR_REPRO=1`, and assert the pre-fix behaviour of `c379cc3b`, so they fail on current main by design. Each
fix carries its own fail-first test.

| Id | Defect | Repro | Impact |
| --- | --- | --- | --- |
| P1 | Restore stops at the first segment after T before checking its start (`BackupReader.cs:143-146`); the window's `latest` comes from the shipper's memory, not the bucket (`PointInTimeRestore.cs:166-167`) | `SegmentGapRepro`: segment s2 deleted, restore at s2 < T < s3 answers 201 without B; final segment deleted, restore at latest answers 201 without C | **silent loss of committed writes at restore** |
| P2 | A segment PUT stored then answered 500 is retried with more frames under a new key with the same start (`ContinuousBackup.cs:672-694,803-818`) | `AmbiguousPutRepro`: restores after the retry answer 500 "missing a log segment" while status shows healthy | restore points unusable until the next snapshot (up to 24 h) |
| P3 | Over 256 MiB of unread log abandons the generation with no log line or metric (`ContinuousBackup.cs:621-626`); snapshot copy, upload and drill run inside the shipping job | `BacklogLivelockRepro`: 3 consecutive generations abandoned, no segment ever stored, failures 0, holes 0, lastError null | point-in-time coverage collapses to snapshot instants, unalerted |
| P4 | Snapshots are one `PutObject` with a 15-minute timeout (`ObjectStore.cs:48,91`); every start snapshots every database | analysis; G2 measures on Tigris | at 100M a 25–40 GB snapshot needs ≥ ~300 Mbit/s sustained; with P3, a busy large database may never re-establish backup after a restart |
| P5 | Lag and pending read 0 for up to 5 minutes after a tap-read failure (`ContinuousBackup.cs:584,595-599`) | code review | `/backupz` healthy during loss |
| P6 | Documentation overstates durability: only the last second before T may be missing (`backup.md:297`), lag is "the RPO" (`:405`), writes answer `local_durable` after `synchronous=NORMAL` | code review | readers assume a smaller RPO than the code gives |
| P10 | After every restart, no change-log segment ships until the generation's snapshot upload and restore drill finish, while `/v1/backup` shows lag 0, pending 0 and logBytes 0 (`ContinuousBackup.cs` StartGenerationAsync → DrillAsync) | G0b (emulator, pinned): 5 min 10 s without shipping in one generation; a SIGKILL during another left 3 min 22 s of acknowledged writes outside the bucket while status looked healthy | unbounded, unreported RPO after restarts |
| P11 | Restore memory is not bounded: a 1M-document restore peaked at 7.4 GB RSS (a 3 GiB heap cap still reached 4.7 GB); O6 targets with a 2 GiB cap peaked at 4.3 GB (flast, fctl). A target's graceful stop after a 1M restore was still shipping its own backup of the restored database after 7 minutes | G0b samples | at 100M a restore may exceed host memory; the harness stops any node above 8 GB |
| P8 | After an abandoned generation, a restore at any T between its last segment and the next snapshot silently omits the dropped frames (`PointInTimeRestore.cs:165-191,222`); no generation-end record | code review | silent loss, same family as P1 and P3 |
| P9 | A failure after `transaction.Commit()` answers 500 for writes that committed (`SqliteDatabaseStore.cs:734-738`) | code review | clients see an ambiguous outcome for a committed write |
| P7 | Bytes after an object's last chunk are ignored on read (`BackupObject.cs:172`); keys truncate times to ms; the plan endpoint does not check contiguity; a detected gap answers 500 `internal_error` | code review, PitrLab audit | low |

## Approvals before any cloud or full-scale work

All testing spend is tracked against the user's US$200 per month total in test-cost-ledger.md (retired evidence removed).

G1, G2 and G3 stay blocked until all four are granted: the bucket (A1), the user-executed key handoff (A2), the spend
cap (A3) and the host reservation (A5). Local gates (G0a, G0b) need none of them. Isolated restores use byte-exact local
copies of the bucket objects (A4, decided), so no extra read-only key is needed and the source is never touched.

**A1 bucket (user):** one new private Tigris bucket used by nothing else; for G1 the proposed target below. Never `ts-terrabase-dev`: every node writes `v1/_system/` there, and another node's retention can delete objects under it.

**G1 financial approval: received 2026-10-12 (user, relayed by the parent).** Up to US$1 of Tigris charges in the first
month, at most 5 GB stored, at most 30 GB local disk and 8 GB database-process memory on host beta, in one dedicated
private test bucket in the existing Tigris account. It is a monitored stop threshold, not a provider billing cap:
retained storage keeps accruing until a separately approved cleanup. It does not authorize generating, transmitting or
configuring persistent credentials, compute beyond a bounded cost plan, deleting resources, or changing production
acknowledgment or availability semantics; the user's broader "approve all charges" wording is not spending scope beyond
this G1 proposal. G2 and G3 stay unapproved; a total ceiling for larger stages is being clarified separately.

G1 still waits on readiness gates: the fixed integrated binary (all candidates merged, independently reviewed, the full
combined suite green), the harness's G1 spend monitor, and the user's bucket and key handoff below.

**Proposed target (user selects it in the UI):** the existing TerraScale account, i.e. the Tigris organization whose
bucket list shows `ts-terrabase` and `ts-terrabase-dev` (read-only evidence 2026-10-12: Fly organization `terrascale`
held those two buckets; the repository's backup docs record that
bucket-scoped key policies already exist in that Tigris account: `ts-terrabase-only`, `ts-terrabase-dev-only`). New
dedicated bucket `ts-pitr-test-g1-20261012`, used by nothing else: private, Standard, default placement, no lifecycle or
expiry rules, no bucket snapshots, no public access. Isolation: the test node's key can reach only this bucket; this
bucket never shares a name, prefix or key with `ts-terrabase` or `ts-terrabase-dev`.

Not verified, and not needed for this handoff: which Tigris organization id (`flyio_…`) belongs to which Fly
organization (no read-only route without credentials shows the ids; display names alone are not evidence). If the user
prefers another existing billing account for tests, they pick it in the same UI step and tell the lead its name only.

**A2 handoff (user-only, in the Tigris console; nothing is pasted into chat):**

1. Open the Tigris console for the account (for example from the Fly dashboard's Tigris page) and select the
   organization whose bucket list shows `ts-terrabase` and `ts-terrabase-dev`.
2. Create bucket `ts-pitr-test-g1-20261012` with the settings above.
3. Create one access key whose permissions cover only that bucket (read, write and list; delete only if the console
   cannot separate it, the harness never deletes), with no organization or admin rights and a 7-day expiry if the
   console offers one (otherwise revoke it after G1). Check in the key's policy view that no other bucket is listed.
4. Run in `bash` (secrets are typed with `read -s`, never echoed or kept in history):

```bash
install -d -m 700 /home/mariogk/tb-100m-pitr/secrets
umask 077
f=/home/mariogk/tb-100m-pitr/secrets/tigris-g1.env
read -rp 'S3 endpoint shown for the key: ' ep
read -rp 'Region shown for the key: ' rg
read -rp 'Access key id: ' ak
read -rsp 'Secret access key: ' sk; echo
printf 'AWS_ENDPOINT_URL_S3=%s\nAWS_REGION=%s\nBUCKET_NAME=ts-pitr-test-g1-20261012\nAWS_ACCESS_KEY_ID=%s\nAWS_SECRET_ACCESS_KEY=%s\nTERRABASE_MASTER_KEY=%s\n' \
  "$ep" "$rg" "$ak" "$sk" "$(head -c32 /dev/urandom | base64 -w0)" > "$f"
unset ep rg ak sk
chmod 600 "$f"
stat -c %a "$f"            # must print 600
cut -d= -f1 "$f"           # must list the six names only
```

5. Copy the `TERRABASE_MASTER_KEY` line to your password manager (without it the test backups cannot be read), then tell
   the lead "G1 handoff done" and the organization name you chose. After G1, revoke the key; deleting the bucket is a
   separate decision.

The harness checks the file's mode and the six names only, passes the values to the node's environment, and never
prints, copies or logs them.

**A3 spend (user):** a total cap and an ongoing retention cost. The proposal is computed from G0b's measured bytes and
object counts at the time of approval: stored GB-months × $0.02, PUT and LIST requests × $0.005 per 1,000, GET requests ×
$0.0005 per 1,000, egress $0. The harness stops writing when the bucket's measured stored bytes reach the cap's share.

**A5 host (user):** up to 550 GB of disk under `/home/mariogk/tb-100m-pitr`, at most 10 CPUs at `nice 19`, about 4 GB of
memory, and the `/tmp/ts-heavy.lock` lock, for 1 to 3 days.

**Consequences:** the bucket holds only synthetic documents, encrypted under the test master key. Objects stay until
the user deletes them; the harness never deletes. At the end the user revokes the key and decides separately whether to
delete the bucket.

## Results

| Gate | Date | Commit / binary | Evidence | Outcome |
| --- | --- | --- | --- | --- |
| G0b, lost answers (real binary) | 2026-10-12 | `c379cc3b`, `eef2dfa3…` (excludes R10a and every candidate fix) | `runs/o6-lostack-1556/EVIDENCE.md`, `source2/evidence/lostack-*.json` | **acceptance evidence**, operation-level idempotency of create, put `if_match`, delete `if_match`, re-create over a tombstone and the phase-fence create on the real binary; 20k docs, LOCAL S3 EMULATOR, NOT TIGRIS; exact cuts C0, L1–L5 and exact source verifies passed, Path A at L5 19,980/19,980. Patch/increment/append NOT covered |
| harness, PitrLab self-check | 2026-10-12 | PitrLab build of this branch | `runs/o6-selfcheck-1615/…/self-check.json` | 22/22: multipart create/part/complete/abort (a failed part retried, a restart mid-upload, EntityTooSmall, InvalidPart), catalog anchors never faulted or mutated |
| G0b defect subset (pinned) | 2026-10-12 | `c379cc3b`, `eef2dfa3…` | `runs/o6-defects-pinned-1624/EVIDENCE.md` | **defect demonstrations**, never acceptance: P1a 201 with 4,000 of 40,000 documents and C2's marker missing; P1b refused at the final segment's time (400), and at the shortened window end 201 with the loss visible only to the shipped history (verify fails); P2 not triggered by isolated stored-then-500 PUTs (the SDK re-PUTs the same key) but reproduced by a burst (duplicate starts, restores at C1 and C2 answer 500 while the exact cut C2 completed); P3 at 20k + 400k docs: with the snapshot PUT held 780 s, lag 740 s with pending 0, then the generation abandoned (generation null, lag growing, failures 0, no error, no log line) |
| G0b fault copies at 1M (pinned) | 2026-10-12 | `c379cc3b`, `eef2dfa3…` | `runs/g0b-1m-1410/O6-FAULTS-EVIDENCE.md` (O5's pre-fence run) | **defect demonstrations**: flast 201 with 1,000 documents missing (P1), ffinal0 201 without C2's marker (P1/P8); fmid, fcorrupt, ftrunc, fdup, fcorsnap and the wrong master key answer 500 internal_error (P7); fdelsnap and ffinal2 refused 400 (window); fstray restores exactly |
| harness smoke of the matrix script (pinned) | 2026-10-12 | `c379cc3b`, `eef2dfa3…` | `runs/o6-smoke-pinned-1732/out/steps.log` | **provisional harness check, 20k docs, not the G0b matrix**: clock-free exact cuts C0–C8, reconcile after the G1 restart and the CK SIGKILL, bounded C1 C3 C4, Path A, Path B C0–C8 before and after the target restart (`--compare`); T±1 ms cuts around u1, d1, r1 pass (at T each mutation batch present, at T−1 ms absent) after a harness marker-window fix (`smoke-tcheck.log`) |
| G1 spend monitor (test limits) | 2026-10-12 | `c379cc3b` | `runs/o6-defects-pinned-1624/monitor-test` | at a test stored limit of 8 MB with 20 s listings it stopped load-a's writer and the node at 8,246,189 bytes; final estimate $0.0004 |
| A-7 before-fix control (pinned) | 2026-10-12 | `c379cc3b`, `eef2dfa3…` | `runs/a7-control-1916/EVIDENCE.md` (`runs/a7-control-1903` void: host `date` printed `%3N` unpadded) | **defect demonstration**, never acceptance; 50k docs, LOCAL S3 EMULATOR, NOT TIGRIS: all 10 mid-load restores that answered 201 lacked a batch acknowledged before T (by 14 ms to 2,837 ms); T+1 ms after a commit lacks it too; restoring while the final segment was unshipped answers an untyped 400 (recorded `failed`); C0, C2 and snapshot S1 restore exactly. Not covered: several commits in one second (about 0.44 commits/s at 1,000 documents per transaction), a delayed (not dropped) commit answer |
| Integrated smoke, qualification mode | 2026-10-13 | `16ec14cd`, `4499368b…` (excludes R10a and the live window-latest fix) | `runs/int-smoke-16ec14cd-0000/out/steps.log`, `driver.log` | **smoke, 20k docs, LOCAL S3 EMULATOR, NOT TIGRIS; not the G0b matrix**: verdict GREEN, required successes 9/9 (C0 C2 C5 C6 C7 C8 S1 S2 S3 restore 201 and verify exactly, also after the target restart with per-partition `--compare`), 24 restores: 16 restored, 8 typed refusals `restore_point_unproven`, 0 unexpected, 0 failures; every verify passed its two-sided A-7 check (0 unresolved batches); bounded C3 and the 1 ms-before cuts Tu1m1/Tr1m1 restored a per-stream prefix; bounded C1 and C4 and the mutation-boundary cuts Tu1, Td1, Td1m1, Tr1 were refused (T inside a commit second); graceful restart and SIGKILL reconcile exact (8000/8000, 13000 at CK); bucket audit `ok` for the database and `_system` (3 generations, 30 segments, 3 snapshots); source RSS peak 488 MiB, restore peak 966 MiB |
| Integrated fault matrix (smoke copies) | 2026-10-13 | `16ec14cd`, `4499368b…` | `runs/int-smoke-16ec14cd-0000/out/fault-matrix.jsonl`, `steps.log` | **20k docs, LOCAL S3 EMULATOR, NOT TIGRIS.** Fixed against the pinned binary: fmid (gap), flast (P1: was 201 with documents missing) and ffinal0 (was 201 without C2's marker) now answer 409 `backup_history_incomplete`; fstray restores (201). **Still open (routed to the restore owner):** fcorrupt, ftrunc, fcorsnap, the wrong master key and fdup answer 500 `internal_error` (P7: a failure, but not typed); fdelsnap and ffinal answer 400 `invalid_request` (outside the window) where a retryable typed 409 is expected. Harness items (routed): fctl at the final segment's own time is correctly refused under A-7 and needs the DR control at the whole second before the final read mark; fstray's verify fails only on the injected audit problem |
| A-7 probes after the fix | 2026-10-13 | `16ec14cd`, `4499368b…` | `runs/a7-fixed-16ec14cd-0008/out/steps.log` | **50k docs, LOCAL S3 EMULATOR, NOT TIGRIS.** Every restore that answered 201 passed the two-sided A-7 check (C0, C2, S1, A7w0–3, A7h1–3), against 10 of 10 failing on `c379cc3b` (`runs/a7-control-1916`); A7h0, A7cpre and A7cpost answered 409 `restore_point_unproven` (T inside a commit second). Verdict RED for one item: A7u, a restore during the load whose final segment is unshipped, answers 400 `invalid_request` instead of a typed 409 (routed). Not covered: several commits in one second (none occurred) |
| Integrated smoke + fault matrix, harness controls | 2026-10-13 | `b9113066`, `ad1cbc62…` (before the typed-error fixes) | `runs/int-smoke-b9113066-0035/out/steps.log` | **20k docs, LOCAL S3 EMULATOR, NOT TIGRIS.** Verdict GREEN: required 9/9, 24 restores (18 restored, 6 expected `restore_point_unproven` refusals, 0 failures); new controls: fdr (disaster-recovery point: the window's `latest` on a fresh node, 03:44:21.000, the whole second before the final read mark) restored 201 and verified exactly; fctl at the final segment's own time refused 409 `backup_history_incomplete` (correct under A-7); fstray restored and verified exactly by content. As expected on this binary: fcorrupt, ftrunc, fcorsnap, fdup and the wrong key still 500, fdelsnap and ffinal still 400 (fixed in `473c25a8`, merged at `be479a33`) |
| Integrated smoke + fault matrix, final commit | 2026-10-13 | `4b750b42`, `46aef440…` (every candidate; excludes R10a) | `runs/int-smoke-4b750b42-0109/out/steps.log`, `fault-matrix.jsonl` | **20k docs, LOCAL S3 EMULATOR, NOT TIGRIS.** Verdict GREEN: required 9/9 exact (also after the target restart), 24 restores, 9 expected `restore_point_unproven` refusals, 0 failures; bucket audits `ok`. **Every fault case ends typed or exact:** fmid, flast, ffinal0, fdelsnap, fdup, fctl and ffinal answer 409 `backup_history_incomplete`; fcorrupt, ftrunc, fcorsnap and the wrong master key answer 409 `backup_unreadable` (the harness listed this new code as a typed refusal only afterwards, so their step-log lines read "FAILED" with status 409); fstray and fdr (the disaster-recovery point at the window's latest, 04:11:03.000) restore 201 and verify exactly. No 500 and no 400 remain |
| G0b 1M qualification, first attempt | 2026-10-13 | `4b750b42`, `46aef440…` | `runs/g0b-int-4b750b42-q1m` (`FAILED.txt`, `source/logs/node.log`) | **FAILED, not evidence; LOCAL S3 EMULATOR.** load-a (400k), C0–C2, the graceful restart and the SIGKILL reconcile passed (400,000 / 670,000 exact), but after the first restart every generation snapshot (222–557 MB, multipart) failed the multipart completion proof: s3-serve keeps no user metadata, so `x-amz-meta-terrabase-upload` was absent on HEAD; the shipper retried for 70 min with `lastError` IOException, shipped no segment (lag 3,188 s) and left 20 complete snapshot objects (~6 GB) under unique keys; C5's cut timed out. The 20k smokes never used multipart. Routed: multipart owner (also accept the S3 composite ETag; delete the object of a failed proof; fail-first tests), harness owner (s3-serve keeps metadata and answers S3 multipart ETags; self-check cases; stop a run when shipping stalls for 10 min). Tigris must be checked at G1 for metadata through CompleteMultipartUpload and composite ETags |
| **G0b 1M qualification matrix** | 2026-10-13 | `087a8e2b`, `e540c0c4…` (every candidate; excludes R10a) | [`benchmark/bench/2026-10-13-pitr-g0b-1m-integrated.md`](../bench/2026-10-13-pitr-g0b-1m-integrated.md), `runs/g0b-int-087a8e2b-q1m` | **GREEN, LOCAL S3 EMULATOR, NOT TIGRIS:** required 9/9 exact on a fresh node from a byte-exact copy, again after its restart; 24 restores, 13 restored, 11 typed `restore_point_unproven`, 0 failures; source exact at C8 (999,000); restart and SIGKILL reconcile exact; audit `ok`; all 13 fault cases typed or exact; restore RSS up to 6.7 GB (P11 open) |
| G1 candidate smoke + fault matrix | 2026-10-13 | `6dec34a7`, `cab16526…` (every fix incl. review round 4; excludes R10a) | `runs/int-smoke-6dec34a7-b/out/steps.log` (`runs/int-smoke-6dec34a7`: harness race, load-b finished before the CK kill and the resume hit its closed fence; fixed in `pitr-run.sh`) | **20k docs, LOCAL S3 EMULATOR, NOT TIGRIS.** GREEN: required 9/9 exact; 24 restores, 8 expected refusals, 0 failures; SIGKILL reconcile exact; audits `ok`; all 13 fault cases typed or exact (fdr and fstray restore exactly). Gates on `6dec34a7`: fmt, analyzers, Fast 1481/1481, Turso 6/6, Slow backup classes all pass |
| G0b 1M qualification on the G1 candidate | 2026-10-13 | `6dec34a7`, `cab16526…` | `runs/g0b-int-6dec34a7-q1m` (`RED.txt`) | **RED, LOCAL S3 EMULATOR: restore memory (P11).** Passed: restart and SIGKILL reconciles exact (400,000; 671,000), source exact at C8 (999,000), audit `ok`, Path A, Path B C0 and C2 exact, all 13 fault cases typed or exact. Then the harness stopped the Path B target over its 8 GB RSS guard (8.09 GB) right after the C5 restore of 1,000,000 documents (the target also held C2's 400k), so the remaining 13 Path B restores and verifies failed with connection refused. On `087a8e2b` the same matrix passed with peaks of 6.7 GB (source) and 5.4 GB (fresh target): restore memory grows with data and sits at the guard at 1M. P11 assigned to the restore owner (`fix/backup-restore-memory`): measure managed versus native versus the restored database's own first backup, bound it, before G1 |
