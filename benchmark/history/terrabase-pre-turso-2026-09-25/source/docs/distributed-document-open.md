# Certified distributed document open and C0 installation

Status: **design / NOT IMPLEMENTED**. This brief defines a catalog-only open
boundary. It does not enable distributed source writes, document apply,
`AppliedAck`, or applied readiness. The accepted state and replay contracts are
[document-state-v2.md](document-state-v2.md) and
[replication-v2-replay.md](replication-v2-replay.md). Their event and D0/D5/D6
rules control if this open brief conflicts with them.

## Source identity and prerequisites

At TerraBase HEAD `352d9f61185d1b685e3032519579040d169ad47d`, these are
SHA-256 identities of the **uncommitted working tree** observed on 2026-09-24:

| Path | SHA-256 | Relevant current behavior |
| --- | --- | --- |
| `crates/tb-replication/src/authority.rs` | `867eb8293903c7fa622928305e3b4c40aa0dbe30f98857b15a9194df6db3c0ed` | `AcceptedAuthority::open_or_install`, `CertifiedShardView::{version,cluster,tenant,local_origin,catalogs,checked_active_catalog,documents_path}` |
| `crates/tb-replication/src/lib.rs` | `7490666d2972312b1d0b398d505fcc13abd8291c894dce77370b939093b913e9` | Exports the authority view and `AuthorityVersion` |
| `crates/tb-replication/src/journal.rs` | `d23b140fbfedad340afaf6a8519fda50b8070176d354a29cb3bea5ee95c530a0` | `ReplicaJournal::open` calls ordinary raw `Storage::open` |
| `crates/tb-storage/src/local.rs` | `57140294e78eef2ecbef7224af093e28125ac3d7ccd901a0487609ae33074f55` | `Storage::open` mutates before document validation; storage stage-admission edits are active |
| `crates/tb-storage/src/floor.rs` | `9675b4932c9740ba921fa24dac9eb550b5b425c1165b55e79687f1eb84ddbeca` | `genesis_state` rejects unexpected in-shard files during fresh floor creation |
| `crates/tb-document/src/lib.rs` | `def9cbbf2401305cb1e34e0fffe22853e61037c1ecb9e68cecd6a61c8ca42e11` | Only local-v1 `DocumentStore::open` exists |
| `crates/tb-document/src/producer.rs` | `dc93b4982922afded4ff5e257bb07edb3c9308dabbba77e937cad1e9179dab02` | `reconcile_local_tail` accepts catalog-only local WAL and audits mainly the latest event when D4 exists |
| `crates/tb-server/src/state.rs` | `0735ff2833aa7049fcc29b56adb1e640651c751c75d2c167f9e1074c7f453227` | `LocalServerState` calls raw `Storage::open` |
| `crates/tb-server/src/replication_server.rs` | `4b6641546346578c354f7d7636fda35d22c82163fe01e522317f42cd8df62e80` | The server opens one certified origin inbox at a time |
| `crates/tb-format/src/document_v2.rs` | `da73c94766d11f4b0be7194a6535c72e9bf8d5baef5ddb24af45388cfb30f7da` | D0/D5/D6 codecs exist; this does not imply a distributed document caller |

The authority accessors above are present in source; their latest guest/runtime
qualification is a separate evidence question. The mode-aware storage open,
distributed document constructor, and C0 installer described below do not yet
exist. `local.rs` moved repeatedly during this draft (from `59c889b0…` to the
table hash); its `Storage::open` still calls directory creation, floor initialization,
orphan cleanup, and recovery before document validation. Refresh its hash and
behavior after that owner hands the file off. Do not put a second writer in
`local.rs` meanwhile. The network receiver remains
durable-inbox-only, and the server's `--data-dir` raw shard remains separate
from the authority-bound `--replication-data-dir/documents/` shard. In the
accepted directory, `CertifiedShardView::documents_path()` is
`data_dir/documents`, while `inbox_path(origin)` is
`data_dir/inbox/<64 lowercase hex origin bytes>`. `ReplicaReceiver::open`
creates that inbox path and `ReplicaJournal::open` uses ordinary raw
`Storage::open` there. A marker beside `documents/` must not block those
sibling journal shards.

## Storage-owned mode boundary

The mode marker is a sibling of `documents/`, at the accepted data directory's
`documents.mode` path. It must not live inside `documents/`: fresh
`FloorAuthority::load_or_init` treats an unexpected in-shard file as established
or corrupt genesis. The marker is local mode evidence, **not** an independent
schema or membership authority. An `Arc<AcceptedAuthority>` must remain alive
with the distributed document handle so the accepted certificate and lock
outlive all access.

The proposed marker has exactly 148 bytes, with no padding or trailing data.
All integer fields are unsigned little-endian; IDs and configuration use
their fixed canonical bytes.

| Offset | Length | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII magic `TBDMOD01` |
| 8 | 2 | version `1` |
| 10 | 2 | flags `0` |
| 12 | 4 | total length `148` |
| 16 | 16 | signed cluster ID |
| 32 | 16 | signed tenant ID |
| 48 | 16 | signed database ID |
| 64 | 8 | signed shard ID |
| 72 | 8 | signed membership epoch |
| 80 | 32 | signed configuration ID |
| 112 | 16 | selected certified local replica ID |
| 128 | 16 | that member's signed incarnation |
| 144 | 4 | CRC32C of bytes `[0, 144)` |

Storage owns this codec, the marker path, publication, recovery, and a common
open gate. The intended non-cyclic interface is a typed primitive
`DistributedDocumentMode` carrying these fields and an explicit
`Storage::open_distributed_document(config, budget, mode)` entrypoint. Storage
must not import `tb-replication`: that crate already depends on storage.
`tb-document` constructs the mode only after comparing the supplied config
against `AcceptedAuthority::view()`, including `AuthorityVersion::V2`, exact
`documents_path`, tenant, database, shard, epoch, configuration, and the full
selected local `OriginId`. It uses `checked_active_catalog` for signed active
generation; C1 cannot supply distributed schema authority.

Both public ordinary `Storage::open` and the distributed entrypoint must use
the same storage-owned parent-directory gate. Resolve the **physical** parent
and target for ordinary opens, so a relative path, final-component symlink, or
other alias cannot check a different sibling marker and enter the marked
`documents/` shard. Preserve ordinary relative and empty-path behavior by
normalizing the physical target before checking its marker. If an ordinary
target's ancestors do not exist, create and sync only ancestor parents, then
lock the real immediate parent and check the marker **before** creating or
opening the shard target, `shard.lock`, floor slots, WAL, or runs. Reject a
marker in ordinary `Storage::open` regardless of its validity; a malformed
marker is not permission to open locally.

The ordinary-open lookup is **target-specific**: inspect
`<physical-parent>/documents.mode` only when the physical target is that
parent's `documents` entry. Also inspect the requested normalized
`<parent>/documents.mode` when the caller spells a `documents` entry, so a
replaced final-component symlink cannot evade its marker. An alias such as
`data_dir/other -> data_dir/documents` resolves to the physical document
target and must reject. A different real sibling, including
`data_dir/inbox/<origin>` or `data_dir/other`, must not consult
`data_dir/documents.mode` merely because it shares the parent. Bind the
lookup and open gate to the same checked target/parent identity; apply the
existing protected-directory and rename-race limits below.

Distributed open has the stricter path rule: require the exact lexically
normalized, absolute, symlink-free `view.documents_path()` and a real parent
directory. Reject `.`/`..` aliases and symlink components; do not use
`canonicalize` to turn an alias into an accepted distributed path. Check the
parent and target identity again at the storage-open boundary. The gate is an
exclusive advisory lock on an opened real parent directory, held through
acquiring the shard lock and storage recovery. Lock order is accepted-authority
lock (already held) → parent open gate → shard lock → storage inner lock.
No guard may cross an `await`.

| Existing `documents/` | Marker | Required transition under the parent gate |
| --- | --- | --- |
| Absent | Absent | Fresh: write a unique, no-follow temporary marker, sync it, atomically publish without clobbering an existing marker, sync the parent, then call the mutating storage open. |
| Absent | Exact and durable | Resume after marker publication: create/open the shard and continue. |
| Real directory | Exact and durable | Recover storage, then perform the document C0 audit. |
| Any existing target, including an empty directory | Absent | Reject before changing that shard. No local-v1-to-distributed migration is inferred. |
| Any | Torn, foreign, linked, symlink, wrong type, or wrong length | Reject; never repair by overwriting accepted evidence. |
| Symlink or non-directory target | Any | Reject distributed open. |

A crash before the marker's final parent sync must precede any storage open;
an orphan temporary file is not accepted mode evidence. A crash after durable
marker publication may leave no `documents/`, incomplete storage genesis, or a
partial C0 installation; all are resumable through the matching marker and
normal storage recovery. An uncertain storage commit requires reopen before
the installer continues. Do not delete or reinterpret an old unmarked local
shard. The explicit public distributed storage entrypoint remains callable by
trusted in-process Rust code; this contract does not claim an unforgeable
authority capability against deliberate raw callers. The deployment must
protect the data directory from hostile same-UID mutation. Advisory directory
locking serializes cooperating openers on qualified Linux/local filesystems;
it does not stop an uncooperative rename or symlink swap. Recheck parent and
target device/inode identity at open boundaries. Full hostile-filesystem
protection would require directory-FD-relative I/O across marker and storage
recovery and is outside this slice.

`AcceptedAuthority::open_or_install` can mutate the authority directory
before `tb-document` sees `documents/`. The library guarantee above means an
unmarked old **document shard** is left unchanged. The later real server
caller should inspect `--replication-data-dir/documents/` read-only before
authority installation so ordinary rejection occurs before authority
mutation too; that preflight does not replace signature validation or the
storage gate.

## Document-owned catalog-only open

`tb-document` owns an explicit `open_distributed(Arc<AcceptedAuthority>,
DocumentStoreConfig, MemoryBudget)` that returns a distinct
`DistributedDocumentStore`/`CatalogReady` handle. In this first packet the
handle exposes no local-v1 `install_schema`, `commit_documents`, `read`, raw
storage, receiver apply, or ACK method. An ordinary `DocumentStore::open`
rejects a distributed mode marker. It also performs a bounded targeted scan
for visible D5/D6 keys and `TBPTR002` D0 values before returning, because its
current D4-tail reconciliation would otherwise miss older distributed state.
Keep existing unmarked local-v1 C0-only, C0/C1, and v1 event fixtures valid;
do not silently broaden unrelated local key-validation behavior.

After storage recovery and before any C0 write, scan **all visible keys** at
one snapshot using bounded pages and `Excluded(last_key)` continuation.
Allow only exact signed C0 key/value rows from `view.catalogs()`; missing
signed rows are resumable installation work. Reject extra or altered C0, any
C1, D0–D6, E0–EF, or other raw key. Compare full catalog bytes, not only their
digest, and require the explicit signed active binding for each collection.
The scan must make progress with a single largest legal C0 row under the
configured `max_scan_bytes`; if it cannot, return capacity and remain unready.
Charge output bytes **and items** through the process-wide memory budget and
hold the work permit while each page is examined. This visible-key audit is
sound for this first slice only in combination with marker-before-open and the
trusted raw-caller boundary; it does not prove that hidden historical/deleted
keys never existed.

Install only missing C0 rows, directly from their signed bytes, in ordered
batches bounded by the exact 2-MiB WAL-frame, 1,024-operation, storage quota,
and byte/item admission limits. Never synthesize or persist a C1 active
pointer in distributed mode. Keep at most a bounded page/batch in memory;
the certificate is already retained by `AcceptedAuthority`. A definite
capacity, disk, memory, scan, or I/O failure leaves the marker and any synced
C0 prefix for retry but returns no `CatalogReady`. After installation, take a
fresh snapshot and repeat the full-key audit; return the handle only when
every and only certified C0 row matches. No source write, remote apply, or
applied readiness follows from catalog readiness.

## Ownership, verification, and later boundaries

1. **Authority prerequisite (present in source):** `tb-replication` owns the
   signed v2 active table and the read-only version/cluster/tenant/local-origin
   accessors. The selected origin includes the incarnation from the signed
   member row. Its component and guest receipts must be assessed separately.
2. **Storage prerequisite (not implemented):** after the baseline owner
   finishes the active `local.rs` stage-admission edit, `tb-storage` owns the
   typed mode, marker codec/publication, physical-parent gate, ordinary-open
   marker rejection, and explicit distributed storage entrypoint. A new
   `mode.rs` can isolate codec/path logic, but the common constructor must
   still be integrated in `local.rs` by its owner. No second concurrent writer
   should modify `local.rs`.
3. **Document packet (not implemented):** `tb-document` owns distributed
   binding validation, full C0 audit/install/re-audit, separate catalog-only
   handle, and the targeted ordinary-local D5/D6/v2-D0 guard. It does not
   change raw-storage write semantics or the current durable-only ACK policy.
4. **Real caller (later):** `tb-server` owns the early read-only docs preflight,
   authority-bound `documents/` opening, lifecycle and Prometheus-compatible
   catalog progress/capacity/corruption metrics. Keep `--data-dir` separate.

Finite regression evidence for the C0 packet must cover: an existing
unmarked local-v1 `documents/` tree (including empty) rejected with its shard
files byte-identical; direct `Storage::open` and `LocalServerState` rejection of
a marked shard; physical alias and symlink cases; marker wrong scope/version,
CRC, length, type and link count; crash cuts around temp sync, marker publish,
parent sync, directory/floor/WAL genesis, and each C0 batch; marker-only and
partial-C0 restart; exact near-1-MiB C0 installation and byte/item/scan/disk
capacity refusal without readiness; extra/altered C0 or any C1/D*/E*/raw
visible key rejection; ordinary unmarked C0-only/C0+C1/v1-event acceptance
and D5/D6/`TBPTR002` refusal. With a real `documents.mode` in place, ordinary
raw opens of `data_dir/documents` and an alias resolving to it must fail
before target mutation, while `ReplicaReceiver` still opens
`data_dir/inbox/<origin>` and ordinary raw storage still opens a different
real sibling. Verify the parent-directory advisory lock on
the supported Linux/local filesystem. Keep benchmark artifacts and results
only in `../ts-benchmark`.

Source production and receiver apply are later packets. Production must use
signed active C0, stage immutable D6 manifests in bounded pure-D6 groups,
exclude those groups from resend, and reserve total stage plus final work
before the first stage. Its final source group is D1+D0(v2)+D2+D3+D4, with
one record format per stream and a singleton-Hello reconnect at a format
boundary. Apply consumes certified exact D1 bytes, translates a valid v1
source event into v2 target state, and atomically publishes D1+D0(v2)+D5.
The server must replay durable inboxes against recovered D5 and a visible
read before offering applied readiness or `AppliedAck`. Until those gates
pass, only durable-inbox ACKs are authorized.
