# Native backup implementation

This is an implementation record for ADR 0046, not qualification evidence.

## Cleanup and replacement boundary

The production member composes `NativeBackupRuntime` from
`crates/terrabase-server/src/native_backup_runtime.rs` and
`production_backup.rs`. The external process adapter
(`crates/terrabase-server/src/backup.rs`) is deleted; there is no other
backup owner. Native recovery, provider, and comparative performance gates
still need to pass before the replacement is
fully qualified. Existing `src/backup` is active native logical backup/recovery
code, not obsolete storage: it exports engine publication state and ordered
journal changes into encrypted, manifest-bound recovery points. Its tests and
evidence are retained. The unused `RestoreResult` compatibility alias had no
callers across the workspace and was removed with its sole documentation
reference. No active production backup path was deleted.

## Implemented boundaries

## Internal manifest format and capture provenance

The current internal manifest header is `terrabase-backup-manifest 4`. This is
the manifest schema version only; it does not change the encrypted artifact
protocol, whose outer envelope and object layout remain `manifest.tbv3e` and
the existing encrypted recovery-point paths. The version-4 body carries an
explicit `capture_kind`:

- `whole_database` identifies a standalone whole-database capture;
- `routed_shard_participant` identifies a manifest that may be assembled only
  as a participant of a complete, identity-matching routed envelope.

An old or ambiguous version-3 manifest is rejected during authenticated
validation. It is never assigned a default capture kind and is not treated as
whole-database evidence. Existing files are not deleted by this decision, but
they are not readable as accepted current-format points unless an explicit
future migration defines and verifies their provenance. This prevents a
participant manifest from being exposed as a whole database and prevents a
whole-database point from being assembled into a routed envelope without its
provenance binding.

Capture provenance is bound to the database identity and, for routed points,
the database incarnation and participant membership. A source that was later
sharded therefore retains historical standalone points by their explicit
identity, while current routed publication requires a complete matching
envelope; absence or mismatch fails closed. Current mode alone is not used to
reinterpret historical artifacts.

The Rust worker uses shared bounded scheduling, an explicit per-database staging
budget and maximum recovery-point count, database key leases, and separate OCI
and Tigris clients. It captures logical state/journal records through the existing
engine backup core. Full and incremental artifacts are encrypted. Recovery leases
reconstruct authenticated artifact contexts after process restart, and readers
can resolve retained key versions. No writer acknowledgement waits for object
storage. Failures preserve staged data and journal retention protection.

The archive cache and retained encrypted manifest inventory have separate,
explicit disk budgets. Half the archive budget holds the active chain; the other
half reserves root replacement or serialized restore/policy transfer. Roots
rotate before the active point or byte bound is exhausted. Replacement snapshots
pin the previous archived boundary and a following incremental covers subsequent
transactions, preserving timestamp recovery across rotation. A durable intent
resumes an interrupted replacement. Provider copies retain the entire promised
window while obsolete local chains are atomically hidden, fsynced, and removed.
Restart cleanup reclaims interrupted local evictions and unpublished capture,
restore, and migration directories in the source cache. It rejects symlinks and
preserves sealed points and durable journals. Inventory admission checks
both manifest count and bytes before capture; full inventories remain readable
for retention and recovery.

Persisted upload journals record transfer work, not current recoverability.
New objects require readback; existing active-chain receipts are process-local
and expire after sixty seconds. Restart and explicit capture force verification.
A failed readback marks that provider unhealthy and schedules authenticated
re-upload. Due idle ticks seal a no-op incremental after ten seconds so timestamp
coverage advances without customer writes or new billable usage. Immediate
repeat captures reuse existing coverage. Retention verifies its surviving root directly against each provider
before deleting predecessors. Restore downloads only the selected authenticated
chain, and policy migration stages one retained point at a time. These finite
work and disk bounds still require workload and provider qualification.

Encrypted incremental discovery now authenticates encrypted predecessor
manifests; the previous implementation searched the plaintext namespace only.
Idempotent publication now verifies existing member bytes as well as the manifest
and propagates directory fsync failures. Staging limits are checked before record
bytes are written. Remote uploads verify each object by readback and publish a
bounded transfer index last. Restores obtain an independent provider listing,
verify ciphertext transport digests, then authenticate the native archive before
publishing a new engine location.

## Qualification still required

This is not yet ADR 0046 completion. Timestamp PITR now authenticates complete
archives and applies whole transactions through the inclusive requested clock;
retention and policy transitions have concrete native execution paths. Policy
intent still belongs to the durable caller catalog. The server physical snapshot
bridge encrypts the replication driver's authenticated logical chunks and
installs through its admitted follower sink. Production runtime/API composition,
branch ownership/rekey integration, physical WAL capture/checkpoint/reuse fault
qualification and comprehensive retention/policy crash tests remain required. The logical archive
is not advertised as a complete physical SQLite image. The fleet's pending-age
gauge measures observed unprotected work, not the exact last remotely recoverable
commit time; a ten-second timer alone does not establish ten-second RPO.

Actual OCI and Tigris bucket placement, credentials, outages/throttling, key
revocation/deletion, fourteen daily restore drills, and comparative
measurements require intended provider and host access. No local
object-store fixture establishes provider qualification.
Comparisons must retain ADR 0046's database counts, hardware, encryption,
durability and workload controls and report lag/RSS/CPU/disk/object-request
tradeoffs in `bench/`.
