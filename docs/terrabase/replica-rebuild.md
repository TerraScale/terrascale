# Copy faults

A faulted database copy stops serving permanently; no later integrity check clears its fault. Regional placement does not certify a replacement automatically. Backup recovery and replica publication preserve source identity and catalog fences; see [copy placement](recovery-copy-placement.md).

## The fault ledger

The storage fault ledger keeps two tables:

- `_tb_write_intent`, in the intent store: a FULL row naming the database, file incarnation and process replica before
  a write or copy of that file starts, removed once it ends.
- `_tb_fault`, in the SYSTEM database: one row per faulted database with a bounded reason.

After an unclean exit, startup proves each remaining intent against an isolated copy of the database and log. It checks full integrity and the incarnation named by the intent. PASSED retires the intent; DAMAGED records a permanent fault before retirement; INCOMPLETE preserves the intent and retries with backoff. Missing keys, busy files and I/O errors are INCOMPLETE. There is no page-cache or flush-based proof. See [crash recovery](crash-recovery.md) for the outcomes, measurements and startup limits.

A fault recorded before the process exited remains a fault, even if a later integrity check would pass. Failing to record a fault, or a fault of SYSTEM itself, fails the whole node closed.

## What a fault refuses

A faulted copy refuses session opens, read and write transaction admission, each operation in an existing transaction, and use as a copy source. Recording a fault interrupts running SQL statements. HTTP reads return `503 copy_faulted`; PostgreSQL returns SQLSTATE `57P03` with `copy_faulted`. A write whose commit outcome is unknown can instead return `503 write_outcome_unknown`; never replay it automatically. Proof-pending copies return retryable `503 recovery_pending` over HTTP or SQLSTATE `57P03` over PostgreSQL, without recording a fault.

Healthy databases keep working. Recovery proof readers inspect isolated files; the backup recovery snapshot reader opens a detached backup artifact without the live-copy ledger. No internal reader serves or backs up a faulted live copy. The fault-count and pending-proof gauges include copies that have not been opened for a tenant request.

## Clearing a fault

Deleting the database removes its fault and intent rows with its files. To recover the data, restore it from backup
into a new database ([backup.md](backup.md#restore)); the restore creates a new incarnation with no fault. Nothing
clears a fault in place.

ts-auth stays unready while its storage copy is faulted ([signing keys](../auth/signing-keys.md)).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
