# Copy faults

A faulted database copy stops serving permanently; no later integrity check clears its fault. Regional placement does not certify a replacement automatically. Backup recovery and replica publication preserve source identity and catalog fences; see [copy placement](recovery-copy-placement.md).

## The fault ledger

The storage fault ledger keeps two tables:

- `_tb_write_intent`, in the intent store: a FULL row naming the database, file incarnation and process replica before
  a write or copy of that file starts, removed once it ends.
- `_tb_fault`, in the SYSTEM database: one row per faulted database with a bounded reason.

After an unclean exit, startup proves each remaining intent against an isolated copy of the database and log. It checks full integrity and the incarnation named by the intent. PASSED retires the intent; DAMAGED records a permanent fault before retirement; INCOMPLETE preserves the intent and retries with backoff. Missing keys, busy files and I/O errors are INCOMPLETE. There is no page-cache or flush-based proof. See [crash recovery](crash-recovery.md) for the outcomes, measurements and startup limits.

An intent with `run = 0` means fault recording began durably. Startup completes the permanent fault row from that marker before any proof; a proof cannot retire it as healthy. A fault recorded before the process exited remains a fault, even if a later integrity check would pass. Failing to record a fault, or a fault of SYSTEM itself, fails the whole node closed.

## What a fault refuses

A faulted copy refuses session opens, read and write transaction admission, each operation in an existing transaction, and use as a copy source. Recording a fault interrupts running SQL statements. HTTP reads return `503 copy_faulted`; PostgreSQL returns SQLSTATE `57P03` with `copy_faulted`. A write whose commit outcome is unknown can instead return `503 write_outcome_unknown`; never replay it automatically. Proof-pending copies return retryable `503 recovery_pending` over HTTP or SQLSTATE `57P03` over PostgreSQL, without recording a fault.

The following readers of the owning copy are refused for both faults and pending proofs:

| Reader | Refusal boundary |
| --- | --- |
| Tenant SQL, metadata, shapes and exports | Store/session and transaction admission; every existing transaction operation rechecks refusal and running SQL is interrupted |
| Backup CDC tap and pinned snapshot | Tap open settles the ledger; every read, snapshot and acknowledgement checks availability, links refusal cancellation to native reads and rechecks before returning or writing each read page |
| Backup buffered segments and detached snapshots awaiting upload | Shipping jobs and snapshot side work link the tap's refusal token; upload checks availability before the object-store call and before recording success |
| Replication source page streams, current revisions and range checksums | Admission checks fault/pending state; refusal interrupts native reads, each outgoing batch is checked, and results are checked before returning |
| Database copy/fork and rewrite sources | Source admission, native interruption, cancellation during copy/replay, and a final availability check before publication |
| Direct tenant format migration | Ledger admission registers a copy read lease before opening the live file; recording or marking a fault cancels the lease and interrupts native migration; completion rechecks the ledger |
| Writer-side revision indexing, history maintenance and rewind targets | Availability checks before native work and before exposing results; maintenance skips known refused stores |

Proof readers are the intentional recovery readers: they only open isolated scratch after copying the main/WAL read-only and unwrapping the existing key; a durable fault marker prevents even this proof for that copy. Restore/rewind inputs, backup checksums and snapshot bundles read detached backup artifacts, not an owning copy's live files. Replica-cache readers open separate rebuildable cache files, not these owning files; source pages come through the gated replication reader. Format migration also checks copy state before bootstrap; the background migration reader uses the ledger lease above. SYSTEM/intent format preparation is startup bootstrap, whose failure prevents startup. Direct file handles used for directory ownership, key access, file-size checks and synchronization do not publish database content.

Healthy databases keep working. The fault-count and pending-proof gauges include copies that have not been opened for a tenant request. Every thrown integrity check is INCOMPLETE, including codes 11/26 (`integrity_engine_corruption`), so a copy that keeps throwing stays pending and refused until an operator acts. Alert on sustained `terrabase_storage_proof_pending` and increases in `terrabase_storage_proof_incomplete_total{reason="integrity_engine_corruption"}`; see [crash recovery](crash-recovery.md).

## Clearing a fault

Deleting the database removes its fault and intent rows with its files. To recover the data, restore it from backup
into a new database ([backup.md](backup.md#restore)); the restore creates a new incarnation with no fault. Nothing
clears a fault in place.

ts-auth stays unready while its storage copy is faulted ([signing keys](../auth/signing-keys.md)).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
