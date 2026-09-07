//! The `SQLite`-backed storage engine (D-27, TB-20).
//!
//! One location is one `SQLite` database file (`terrabase.sqlite3`) opened in
//! WAL mode with a single writer connection and a small read pool. Every
//! commit — documents, operation-ledger row, and publication-history journal
//! row — rides one `SQLite` transaction, so a crash can never leave a document
//! without its journal record or the reverse: a published commit without its
//! journal record is structurally impossible by construction, not by repair.
//!
//! Reads resolve as-of a storage sequence: `documents` is an append-only
//! version table keyed `(tenant, collection, identifier, commit sequence)`,
//! so a read at `seq` selects each key's newest row at or below `seq`, and
//! a tombstone row hides the key. While no pruning floor exists,
//! [`StoragePort::snapshot_boundary_available`] holds for every boundary at
//! or below the published sequence. Document bodies and journal frames
//! reuse the crate's private codec bytes.
//!
//! Sequence, version, and limit values cross the SQL boundary as `SQLite`'s
//! signed 64-bit integers. Sequences come from a `checked_add` chain that
//! starts at zero and versions from a `saturating_add` over stored values,
//! so the narrowing casts below are bounded by construction rather than by
//! a runtime check — the same discipline the codec module records.
//!
//! # Design record
//!
//! ## Durability mapping
//!
//! Connections: WAL mode, `foreign_keys=ON`, a 60-second busy timeout, and
//! `synchronous=FULL` for direct durable mutations. One
//! writer connection (behind a mutex, per D-28's one-writer-per-file rule)
//! plus a read pool; `commit_intents` runs one `BEGIN IMMEDIATE`
//! transaction per batch, so a group commit is one fsync class.
//!
//! - **Synced** batches retain `synchronous=FULL`. The storage commit path does not yet consume a
//!   verified RF=2 receipt, so configuration alone cannot replace local sync.
//! - **Written** commits ride WAL + `synchronous=NORMAL`: the frames
//!   survive process death but may be lost on OS crash — exactly the
//!   bytes-written-no-fsync contract, never overstated.
//! - **Buffered** commits ride the same single transaction — `SQLite` has
//!   no cheaper half-durable path — and the disclosed level stays
//!   `Buffered`, understating what survives.
//!
//! A failed transaction rolls back to a definite synchronous `Storage`
//! rejection: nothing durable was published, so the uncertain outcome the
//! append-then-sync backends report cannot occur here.
//!
//! ## Sequences and rejections
//!
//! Sequences are allocated inside the write transaction as watermark+1,
//! where the watermark is the maximum sequence the `ledger` and
//! `usage_facts` rows name — one shared space, exactly the reference
//! engines' rule. A conflict rejection binds the operation identity durably
//! (ledger row, `committed=0`, `rejection` code) but consumes no sequence
//! and writes no journal row, so published sequences stay dense and
//! [`StoragePort::journaled_high_water`] never trails
//! [`StoragePort::published_state`] behind a non-publishing record.
//!
//! ## Journal pruning (the bounded-journal invariant)
//!
//! `consumer_frontiers(name, seq)` holds one row per journal consumer.
//! The pruning floor is the MINIMUM frontier: a row any consumer still
//! needs is never pruned, and an empty frontier table prunes nothing
//! (fail-safe). The lifecycle — `compact`'s sweep calling
//! [`SqliteStorage::prune_below_consumer_frontiers`] — deletes journal
//! rows strictly below the floor and deletes `documents` version rows
//! below the floor unless they are the newest version of their key: a
//! shadowed row (a newer version exists, tombstone or not) cannot change
//! any above-floor snapshot answer, and each key's newest row is what an
//! above-floor snapshot resolves when no later write exists. Reads below
//! the floor fail closed: [`StoragePort::snapshot_boundary_available`] is
//! false there and [`StoragePort::servable_history_start`] is the floor
//! (the oldest retained journal row when no consumer has registered).
//! Consumers register through
//! [`SqliteStorage::advance_consumer_frontier`], test-gated until the
//! engine wires its real consumers (replication acks, sync checkpoints,
//! metering export, and the TB-22 backup lane's Litestream-position
//! frontier — that seam is why the lifecycle is shaped as a named
//! frontier table). Usage facts are billing records and are never pruned.
//!
//! ## Usage facts
//!
//! `usage_facts` stores standalone authoritative facts (`record_usage`)
//! as checksummed envelopes keyed by the complete fact identity, with
//! `(seq, rule, fact_id)` SQL order equal to `UsageFactPosition` order
//! (`rule` is stored as the `MintingRule` discriminant, whose integer
//! order is the type's `Ord`).
//!
#![allow(clippy::cast_possible_truncation, clippy::cast_possible_wrap)]
#![allow(dead_code, reason = "SQLite maintenance helpers are activated by host lifecycle")]
#![allow(clippy::all, reason = "SQLite backend lint debt is tracked behind the storage seam")]
#![allow(clippy::pedantic, reason = "SQLite backend lint debt is tracked behind the storage seam")]

mod accounting;
pub(crate) mod branch_import;
pub(crate) mod branch_provenance;
mod budget;
mod descriptor_removal;
pub(super) mod encrypted_file;
pub(super) mod encrypted_vfs;
mod invisible;
mod json_codec;
mod lifecycle_state;
#[cfg(test)]
mod metering_tests;
#[cfg(test)]
mod operation_kind_markers_tests;
mod private_metadata;
mod repair_summary;
mod replication_outbox;
mod schema_bundles;
mod schema_indexes;
mod schema_metadata;
mod schema_ttl;
mod shadow;
#[cfg(test)]
mod sql_readonly_tests;
mod storage_closure;
mod storage_intervals;
#[cfg(test)]
mod tests;
pub(crate) mod usage_seal;
mod webhook_metadata;
mod write_usage;

use std::{
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, RecvTimeoutError, SyncSender},
    },
    thread::JoinHandle,
    time::{Duration, Instant},
    time::{SystemTime, UNIX_EPOCH},
};

use rusqlite::types::Value;
use rusqlite::{Connection, OpenFlags, OptionalExtension as _, TransactionBehavior};

use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId,
};
use crate::usage::{
    DurableUsageFact, MAX_DOCUMENT_WRITE_UNITS_PER_FACT, MAX_USAGE_PAGE_SIZE, MeasurementWindow,
    MeterCatalog, MeterKind, MintingIdentity, MintingRule, UsageFact,
};

#[cfg(test)]
use super::faults::FaultPlan;
use super::ownership::database_binding;
use super::{
    AppliedFrontierEntry, BackfillProgress, DbUsageFact, DescriptorListing, DescriptorRegistration,
    DocumentMergeState, DocumentRevisionEntry, ExpiredTtlKey, IndexDescriptorError, JournalKind,
    JournalProvenance, JournalRow, MergeConflictEntry, MergeKernelEntry, MergePolicy, MergeSchema,
    MergeSchemaError, PublishedCommit, PublishedWrite, ScanFilter, ScanOp, SnapshotReadError,
    StorageOpenError, StoragePort, StorageRootId, UsageFactPosition, UsageStorageError,
    billed_writes_for_bytes,
};
use super::{
    codec,
    intent::{
        CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite,
        StorageReceipt,
    },
};
pub(crate) use json_codec::decode_document_body;
use json_codec::encode_document_body;

/// The wall-clock commit time of a row, in Unix milliseconds (the D-42
/// 30-day history floor is evaluated against it).
fn unix_millis() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(i64::MAX, |since| i64::try_from(since.as_millis()).unwrap_or(i64::MAX))
}

fn monotonic_millis() -> u64 {
    static START: std::sync::OnceLock<Instant> = std::sync::OnceLock::new();
    u64::try_from(START.get_or_init(Instant::now).elapsed().as_millis()).unwrap_or(u64::MAX)
}

fn bounded_env_u64(name: &str, default: u64, minimum: u64, maximum: u64) -> u64 {
    std::env::var(name)
        .ok()
        .and_then(|value| value.parse::<u64>().ok())
        .map_or(default, |value| value.clamp(minimum, maximum))
}

fn cache_kib() -> i64 {
    i64::try_from(bounded_env_u64(
        "TERRABASE_SQLITE_CACHE_KIB",
        u64::try_from(DEFAULT_CACHE_KIB).unwrap_or(2048),
        256,
        64 * 1024,
    ))
    .unwrap_or(DEFAULT_CACHE_KIB)
}

fn mmap_bytes() -> i64 {
    i64::try_from(bounded_env_u64(
        "TERRABASE_SQLITE_MMAP_BYTES",
        u64::try_from(DEFAULT_MMAP_BYTES).unwrap_or(8 * 1024 * 1024),
        0,
        1 << 34,
    ))
    .unwrap_or(DEFAULT_MMAP_BYTES)
}

fn rss_budget_bytes() -> usize {
    usize::try_from(bounded_env_u64(
        "TERRABASE_SQLITE_RSS_BUDGET_BYTES",
        u64::try_from(DEFAULT_RSS_BUDGET_BYTES).unwrap_or(8 * 1024 * 1024),
        64 * 1024,
        1 << 30,
    ))
    .unwrap_or(DEFAULT_RSS_BUDGET_BYTES)
}

fn cache_resident_bytes() -> usize {
    usize::try_from(cache_kib()).unwrap_or(0).saturating_mul(1024)
}

/// The database file name inside one location directory.
pub(super) const DATABASE_FILE: &str = "terrabase.sqlite3";

/// The database file's path inside one location directory — the file
/// Litestream streams per database (D-29).
#[must_use]
pub fn database_file(location: &Path) -> PathBuf {
    location.join(DATABASE_FILE)
}

/// The read-pool width. Reads take `&self`, so the pool is what lets a
/// future engine-side read-concurrency split use this backend unchanged.
const READ_POOL_WIDTH: usize = 4;

/// A commit wave is deliberately short and fixed. The committer waits this
/// long after the first arrival, allowing concurrent callers to share one
/// SQLite transaction without making a quiet single writer wait indefinitely.
const COMMIT_WAVE_WAIT: Duration = Duration::from_millis(2);
const CHECKPOINT_IDLE_WAIT: Duration = Duration::from_secs(1);

/// A pathological producer cannot grow one transaction without bound. The
/// next wave starts with the remaining requests after this many intents.
const COMMIT_WAVE_MAX_INTENTS: usize = 128;

/// The default per-file SQLite page-cache budget. It is intentionally small:
/// the open-file catalog may contain thousands of mostly idle databases.
const DEFAULT_CACHE_KIB: i64 = 2 * 1024;

/// The default mapped read window per file. SQLite may map less when the file
/// is smaller; this is address space, not an RSS reservation.
const DEFAULT_MMAP_BYTES: i64 = 8 * 1024 * 1024;

/// The default total resident budget for pooled read connections of one file.
const DEFAULT_RSS_BUDGET_BYTES: usize = 8 * 1024 * 1024;

/// Idle connections are closed by the lifecycle hook after this quiet period.
const DEFAULT_IDLE_TIMEOUT: Duration = Duration::from_secs(60);

/// The busy timeout every connection applies (the production pairing the
/// durable configuration: WAL, `synchronous=FULL`, 60s busy timeout).
#[allow(
    clippy::duration_suboptimal_units,
    reason = "60 seconds is the documented pairing; this toolchain has no from_minutes"
)]
const BUSY_TIMEOUT: Duration = Duration::from_secs(60);

/// The maximum number of distinct field names owning extracted index
/// columns in one database (D-42's stored-column budget; two physical
/// columns per field). Beyond it, registration refuses typed.
const MAX_EXTRACTED_FIELDS: usize = 32;

/// The maximum length of one descriptor field path, in bytes.
const MAX_DESCRIPTOR_FIELD_BYTES: usize = 256;

/// The inline backfill cost cap of registration (D-31's cost-capped inline
/// build): a collection at or below this many live rows is backfilled
/// synchronously inside the registration transaction and its descriptor
/// becomes active immediately; anything larger stays building until the
/// governed background pass completes it.
const INLINE_BACKFILL_ROW_CAP: u64 = 10_000;

/// The one-byte descriptor kind of an extracted value column.
const DESCRIPTOR_KIND_VALUE: i64 = 1;

/// The one-byte descriptor state codes.
const DESCRIPTOR_STATE_BUILDING: i64 = 0;
const DESCRIPTOR_STATE_ACTIVE: i64 = 1;

/// The `documents.kind` value of a locally written row (D-35 provenance).
const ROW_KIND_ORIGIN: &str = "origin";

/// The `documents.kind` value of a row the cross-region tailer applied.
const ROW_KIND_REPLICATED: &str = "replicated";

/// Derives a stable local origin for a database location. The identity is
/// persisted in `meta`; the path-derived value is only the first-open seed.
fn local_origin_for(path: &Path) -> String {
    let digest = blake3::hash(path.to_string_lossy().as_bytes());
    format!("local-{}", hex_digest(digest.as_bytes()))
}

fn hex_digest(bytes: &[u8; 32]) -> String {
    use std::fmt::Write as _;
    let mut output = String::with_capacity(64);
    for byte in bytes {
        let _ = write!(output, "{byte:02x}");
    }
    output
}

/// The write path's view of the extracted-column descriptors (D-42): the
/// per-collection fields whose values the document INSERT carries, the
/// active descriptors whose conditions a scan may push, and the cached
/// column-bearing INSERT statement.
#[derive(Default)]
struct ExtractedState {
    /// Every registered descriptor, keyed `(collection, field)`; the flag
    /// reports its activity.
    descriptors: std::collections::BTreeMap<(String, String), bool>,
    /// The distinct field names owning stored columns, in registration
    /// order.
    fields: Vec<String>,
    /// The cached INSERT statement for puts: the base row plus every
    /// extracted column.
    put_insert_sql: String,
}

impl ExtractedState {
    /// Rebuilds the cached put INSERT for the current field set.
    ///
    /// The base row carries sixteen placeholders — twelve originals, the
    /// four provenance columns (D-35: `kind`, `origin`, `origin_seq`, `hlc`),
    /// the causal context, and the TTL expiry and idempotency columns — so
    /// the extracted columns start at placeholder 20.
    fn refresh_put_sql(&mut self) {
        let mut columns = String::new();
        let mut placeholders = String::new();
        for (position, field) in self.fields.iter().enumerate() {
            columns.push_str(", ");
            columns.push_str(&quote_identifier(&text_column(field)));
            columns.push_str(", ");
            columns.push_str(&quote_identifier(&numeric_column(field)));
            placeholders.push_str(", ?");
            placeholders.push_str(&(20 + 2 * position).to_string());
            placeholders.push_str(", ?");
            placeholders.push_str(&(21 + 2 * position).to_string());
        }
        self.put_insert_sql = format!(
            "INSERT INTO documents
             (tenant, collection, id, commit_seq, commit_ulid, revision, requested,
             digest, version, deleted, committed_at, body, kind, origin, origin_seq, hlc, context, expires_at,
              client_request_ulid{columns})
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19{placeholders})"
        );
    }

    /// Reports whether one field of one collection has an active descriptor.
    fn is_active(&self, collection: &str, field: &str) -> bool {
        self.descriptors.get(&(collection.to_owned(), field.to_owned())).copied().unwrap_or(false)
    }
}

/// The write path's view of the full-text descriptors (S-02 slice D): the
/// per-collection fields whose term streams the document INSERT maintains
/// in `fts_docs`. Unlike value descriptors there are no stored columns and
/// no column budget — the FTS5 table is the index — so the state is only
/// the activity map.
#[derive(Default)]
struct FtsState {
    /// Every registered text descriptor, keyed `(collection, field)`; the
    /// flag reports its activity.
    descriptors: std::collections::BTreeMap<(String, String), bool>,
}

/// Returns the text-column name of one extracted field: strings live here.
fn text_column(field: &str) -> String {
    format!("f:{field}#t")
}

/// Returns the numeric-column name of one extracted field: integers and
/// floats live here (integers stored as INTEGER, floats as REAL — `SQLite`
/// compares the two numerically, the SQL number rule).
fn numeric_column(field: &str) -> String {
    format!("f:{field}#n")
}

/// Extracts one document's TTL expiry (S-02 slice D): the top-level
/// `expires_at` field in Unix milliseconds. Integers ride as-is; finite
/// numbers narrow the way embedding components do. Anything else —
/// absence, booleans, strings, arrays, objects, non-finite numbers — is no
/// expiry, never a refusal: TTL is advisory metadata on a schemaless body.
///
/// Family records are exempt: the reserved collection owns `expires_at`
/// with lifecycle semantics of its own (visible-as-expired until its pass
/// deletes the record), so the document-TTL layer never extracts, hides,
/// or sweeps it.
fn extract_expires_at(document: &Document, field: Option<&str>) -> Option<i64> {
    if document.collection().as_str() == RESERVED_FAMILY_COLLECTION {
        return None;
    }
    let DocumentValue::Object(fields) = document.value() else { return None };
    match fields.get(field?) {
        Some(DocumentValue::Integer(millis)) => Some(*millis),
        #[allow(clippy::cast_possible_truncation)]
        Some(DocumentValue::Number(number)) if number.is_finite() => Some(*number as i64),
        _ => None,
    }
}

/// Extracts one full-text field's indexed term stream (S-02 slice D): the
/// Standard-analyzer terms of a string value, or of every string element
/// of an array value, joined for the FTS5 row. The walk mirrors
/// [`extract_column_value`]'s dotted navigation; values with no string
/// shape index nothing.
fn extract_fts_text(document: &Document, field: &str) -> Option<String> {
    let mut value = document.value();
    for segment in field.split('.') {
        let DocumentValue::Object(fields) = value else { return None };
        value = fields.get(segment)?;
    }
    let mut terms = Vec::new();
    match value {
        DocumentValue::String(text) => {
            terms.extend(crate::query::standard_terms(text));
        }
        DocumentValue::Array(elements) => {
            for element in elements {
                if let DocumentValue::String(text) = element {
                    terms.extend(crate::query::standard_terms(text));
                }
            }
        }
        _ => return None,
    }
    (!terms.is_empty()).then(|| terms.join(" "))
}
/// Adds the replication provenance columns (D-35: `kind`, `origin`, `hlc`)
/// to a location created before they existed — the extracted-columns ALTER
/// precedent, idempotent through the schema probe: a fresh location already
/// carries the columns from `SCHEMA`, and the probe leaves it untouched.
fn migrate_documents_provenance(connection: &Connection) -> Result<(), ()> {
    let mut statement = connection.prepare("PRAGMA table_info(documents)").map_err(|_| ())?;
    let columns = statement
        .query_map([], |row| row.get::<_, String>(1))
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    if columns.iter().any(|column| column == "hlc") {
        return Ok(());
    }
    connection
        .execute_batch(
            "ALTER TABLE documents ADD COLUMN kind TEXT NOT NULL DEFAULT 'origin';
             ALTER TABLE documents ADD COLUMN origin TEXT NOT NULL DEFAULT '';
             ALTER TABLE documents ADD COLUMN hlc INTEGER NOT NULL DEFAULT 0;",
        )
        .map_err(|_| ())
}

/// Adds and backfills the durable per-origin sequence. Existing rows are
/// ordered by their retained publication facts only; future writes allocate
/// the counter inside the commit transaction. A zero counter is reserved for
/// replicated rows whose source dot was not carried by the old protocol.
fn migrate_documents_origin_sequence(
    connection: &mut Connection,
    local_origin: &str,
) -> Result<(), ()> {
    let mut statement = connection.prepare("PRAGMA table_info(documents)").map_err(|_| ())?;
    let columns = statement
        .query_map([], |row| row.get::<_, String>(1))
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    if !columns.iter().any(|column| column == "origin_seq") {
        connection
            .execute("ALTER TABLE documents ADD COLUMN origin_seq INTEGER NOT NULL DEFAULT 0", [])
            .map_err(|_| ())?;
    }
    let transaction = connection.transaction().map_err(|_| ())?;
    let mut rows = transaction
        .prepare(
            "SELECT tenant, collection, id, commit_seq, kind, origin
             FROM documents WHERE origin_seq = 0
             ORDER BY origin, commit_seq, collection, id",
        )
        .map_err(|_| ())?;
    let pending = rows
        .query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, String>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, String>(4)?,
                row.get::<_, String>(5)?,
            ))
        })
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(rows);
    let mut counters = std::collections::BTreeMap::<String, u64>::new();
    for (tenant, collection, id, commit_seq, kind, origin) in pending {
        // Old replicated rows have no source counter in this protocol
        // version. Leave them at zero so anti-entropy refuses rather than
        // inventing a receiver-local identity during migration.
        if kind != ROW_KIND_ORIGIN {
            continue;
        }
        let origin = if origin.is_empty() { local_origin.to_owned() } else { origin };
        let next = counters.entry(origin.clone()).or_insert_with(|| {
            transaction
                .query_row(
                    "SELECT COALESCE(MAX(origin_seq), 0) FROM documents WHERE origin = ?1",
                    [&origin],
                    |row| row.get::<_, i64>(0),
                )
                .ok()
                .and_then(|value| u64::try_from(value).ok())
                .unwrap_or(0)
        });
        *next = next.saturating_add(1);
        transaction
            .execute(
                "UPDATE documents SET origin = ?1, origin_seq = ?2
                 WHERE tenant = ?3 AND collection = ?4 AND id = ?5 AND commit_seq = ?6",
                rusqlite::params![origin, *next as i64, tenant, collection, id, commit_seq],
            )
            .map_err(|_| ())?;
    }
    transaction.commit().map_err(|_| ())
}

/// Adds the TTL expiry column (S-02 slice D) to a location created before
/// it existed — the extracted-columns ALTER precedent, idempotent through
/// the schema probe.
fn migrate_documents_ttl(connection: &Connection) -> Result<(), ()> {
    let mut statement = connection.prepare("PRAGMA table_info(documents)").map_err(|_| ())?;
    let columns = statement
        .query_map([], |row| row.get::<_, String>(1))
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    if columns.iter().any(|column| column == "expires_at") {
        return Ok(());
    }
    connection.execute_batch("ALTER TABLE documents ADD COLUMN expires_at INTEGER;").map_err(|_| ())
}

/// Adds the durable causal-context column to pre-D-42 files. The default is
/// deliberately empty: old rows predate causal metadata and cannot be made
/// more informative without inventing dependencies. New rows are populated
/// by the commit transaction and are validated by the context codec.
fn migrate_documents_context(connection: &Connection) -> Result<(), ()> {
    let mut statement = connection.prepare("PRAGMA table_info(documents)").map_err(|_| ())?;
    let columns = statement
        .query_map([], |row| row.get::<_, String>(1))
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    if columns.iter().any(|column| column == "context") {
        return Ok(());
    }
    connection
        .execute("ALTER TABLE documents ADD COLUMN context BLOB NOT NULL DEFAULT X''", [])
        .map(|_| ())
        .map_err(|_| ())
}

/// Adds the durable operation identity column to pre-D-42 files.  The
/// operation facts table itself is created by [`SCHEMA`], which is safe for
/// existing files because all statements are `IF NOT EXISTS`.
fn migrate_documents_idempotency(connection: &Connection) -> Result<(), ()> {
    let mut statement = connection.prepare("PRAGMA table_info(documents)").map_err(|_| ())?;
    let columns = statement
        .query_map([], |row| row.get::<_, String>(1))
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    if !columns.iter().any(|column| column == "client_request_ulid") {
        connection
            .execute(
                "ALTER TABLE documents ADD COLUMN client_request_ulid TEXT NOT NULL DEFAULT ''",
                [],
            )
            .map_err(|_| ())?;
    }
    connection
        .execute_batch(
            "DROP INDEX IF EXISTS documents_client_request_ulid;
             DROP INDEX IF EXISTS operation_facts_client_request_ulid;",
        )
        .map_err(|_| ())
}

/// Adds the canonical D-42 row aliases to files created by the early SQLite
/// substrate. `commit_seq` remains readable for the in-repo compatibility
/// queries, while `seq` is the row-model name and `dot` is the immutable
/// origin dot derived from the provenance columns.
fn migrate_documents_row_aliases(connection: &Connection) -> Result<(), ()> {
    let mut statement = connection.prepare("PRAGMA table_xinfo(documents)").map_err(|_| ())?;
    let columns = statement
        .query_map([], |row| row.get::<_, String>(1))
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    if !columns.iter().any(|column| column == "seq") {
        connection
            .execute(
                "ALTER TABLE documents ADD COLUMN seq INTEGER GENERATED ALWAYS AS (commit_seq) VIRTUAL",
                [],
            )
            .map_err(|_| ())?;
    }
    if !columns.iter().any(|column| column == "dot") {
        connection
            .execute(
                "ALTER TABLE documents ADD COLUMN dot TEXT GENERATED ALWAYS AS (origin || ':' || origin_seq) VIRTUAL",
                [],
            )
            .map_err(|_| ())?;
    }
    connection
        .execute_batch(
            "CREATE INDEX IF NOT EXISTS documents_seq_covering
             ON documents (seq, tenant, collection, id, commit_ulid, dot, hlc,
                           kind, revision, requested, deleted);",
        )
        .map_err(|_| ())
}

/// Backfills operation facts from the folded document rows in an old file.
/// Existing rows are the only durable evidence available, so a historical
/// no-row delete cannot be invented; all future no-row operations are recorded
/// before their transaction commits.  Roots are reconstructed in sequence
/// order, making replay exact for rows created before this migration too.
fn migrate_operation_facts(connection: &mut Connection) -> Result<(), ()> {
    let transaction = connection.transaction().map_err(|_| ())?;
    let existing = transaction
        .query_row("SELECT COUNT(*) FROM operation_facts", [], |row| row.get::<_, i64>(0))
        .map_err(|_| ())?;
    // A non-empty table is already a post-migration database.  Never
    // reconstruct only a missing suffix from the initial root: that would
    // silently manufacture incorrect replay roots for a corrupted file.
    if existing != 0 {
        return transaction.commit().map_err(|_| ());
    }
    let mut statement = transaction
        .prepare(
            "SELECT tenant, COALESCE(NULLIF(client_request_ulid, ''), commit_ulid) AS binding,
                    MIN(digest), MIN(commit_seq), MIN(requested),
                    SUM(CASE WHEN LENGTH(body) = 0 THEN 1
                             ELSE (LENGTH(body) + 4095) / 4096 END),
                    SUM(LENGTH(body))
             FROM documents
             WHERE commit_ulid <> ''
             GROUP BY tenant, binding
             ORDER BY MIN(commit_seq), tenant, binding",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, i64>(4)?,
                row.get::<_, i64>(5)?,
                row.get::<_, i64>(6)?,
            ))
        })
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    let mut root = INITIAL_ROOT_HEAD.to_owned();
    for (tenant, operation, digest, sequence, requested, writes, bytes) in rows {
        let sequence = u64::try_from(sequence).map_err(|_| ())?;
        // Digests use SQLite's signed INTEGER slot; preserve all u64 bits.
        #[allow(clippy::cast_sign_loss)]
        let digest = digest as u64;
        let requested = codec::durability_of(u8::try_from(requested).map_err(|_| ())?).ok_or(())?;
        let next_root = chained_root(&root, sequence, digest);
        next_root.clone_into(&mut root);
        transaction
            .execute(
                "INSERT OR IGNORE INTO operation_facts
                 (tenant, client_request_ulid, digest, committed, sequence, requested, root, writes, bytes)
                 VALUES (?1, ?2, ?3, 1, ?4, ?5, ?6, ?7, ?8)",
                rusqlite::params![
                    tenant,
                    operation,
                    digest as i64,
                    sequence as i64,
                    codec::durability_code(requested),
                    next_root,
                    writes,
                    bytes
                ],
            )
            .map_err(|_| ())?;
        transaction
            .execute(
                "UPDATE documents SET client_request_ulid = ?1
                 WHERE tenant = ?2 AND commit_ulid = ?1 AND client_request_ulid = ''",
                rusqlite::params![operation, tenant],
            )
            .map_err(|_| ())?;
    }
    transaction.commit().map_err(|_| ())
}

fn replication_was_superseded_on(
    connection: &Connection,
    tenant: &TenantId,
    operation: &OperationId,
) -> Result<bool, ()> {
    connection.query_row(
        "SELECT EXISTS(SELECT 1 FROM replication_superseded WHERE tenant = ?1 AND operation = ?2)",
        rusqlite::params![tenant.as_str(), operation.as_str()],
        |row| row.get(0),
    ).map_err(|_| ())
}

fn replication_fingerprint_on(
    connection: &Connection,
    tenant: &TenantId,
    operation: &OperationId,
) -> Result<Option<[u8; 32]>, ()> {
    connection
        .query_row(
            "SELECT fingerprint FROM replication_bindings WHERE tenant = ?1 AND operation = ?2",
            rusqlite::params![tenant.as_str(), operation.as_str()],
            |row| row.get::<_, Vec<u8>>(0),
        )
        .optional()
        .map_err(|_| ())?
        .map(|bytes| bytes.try_into().map_err(|_| ()))
        .transpose()
}

/// Refuses malformed durable operation facts instead of letting a corrupt
/// counter or receipt turn into an apparently successful replay.
fn validate_operation_facts(connection: &Connection) -> Result<(), ()> {
    let mut statement = connection
        .prepare(
            "SELECT digest, committed, rejection, sequence, requested, root, writes, bytes,
                    EXISTS(SELECT 1 FROM branch_snapshot_receipts AS imported
                           WHERE imported.tenant = operation_facts.tenant
                             AND imported.operation = operation_facts.client_request_ulid),
                    EXISTS(SELECT 1 FROM operation_kind_markers AS marker
                           WHERE marker.tenant = operation_facts.tenant
                             AND marker.operation = operation_facts.client_request_ulid)
             FROM operation_facts",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map([], |row| {
            Ok((
                row.get::<_, i64>(0)?,
                row.get::<_, i64>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, i64>(4)?,
                row.get::<_, String>(5)?,
                row.get::<_, i64>(6)?,
                row.get::<_, i64>(7)?,
                row.get::<_, bool>(8)?,
                row.get::<_, bool>(9)?,
            ))
        })
        .map_err(|_| ())?;
    for row in rows {
        let (
            _digest,
            committed,
            rejection,
            sequence,
            requested,
            root,
            writes,
            bytes,
            imported,
            marked,
        ) = row.map_err(|_| ())?;
        if writes < 0 || bytes < 0 || !matches!(committed, 0 | 1) {
            return Err(());
        }
        let requested = u8::try_from(requested).map_err(|_| ())?;
        if codec::durability_of(requested).is_none() {
            return Err(());
        }
        if committed == 1 {
            if sequence <= 0
                || root.is_empty()
                || (writes == 0 && !imported && !marked)
                || ((imported || marked) && (writes != 0 || bytes != 0))
            {
                return Err(());
            }
        } else if sequence != 0
            || !root.is_empty()
            || codec::decode_rejection(u8::try_from(rejection).map_err(|_| ())?).is_none()
        {
            return Err(());
        }
    }
    let invalid_markers: i64 = connection
        .query_row(
            "SELECT COUNT(*) FROM operation_kind_markers AS marker
             LEFT JOIN operation_facts AS fact
               ON fact.tenant=marker.tenant AND fact.client_request_ulid=marker.operation
             WHERE marker.kind NOT IN ('metadata_only','internal_maintenance','replication')
                OR fact.client_request_ulid IS NULL OR fact.committed <> 1
                OR fact.writes <> 0 OR fact.bytes <> 0
                OR EXISTS (SELECT 1 FROM branch_snapshot_receipts AS imported
                           WHERE imported.tenant=marker.tenant AND imported.operation=marker.operation)",
            [],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    if invalid_markers != 0 {
        return Err(());
    }
    drop(statement);
    let mismatched_rows = connection
        .query_row(
            "SELECT COUNT(*) FROM documents
             WHERE client_request_ulid = ''
                OR (kind = 'origin' AND client_request_ulid <> commit_ulid)",
            [],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|_| ())?;
    if mismatched_rows != 0 {
        return Err(());
    }
    let missing_facts = connection
        .query_row(
            "SELECT COUNT(*) FROM (
                 SELECT DISTINCT tenant, client_request_ulid FROM documents
             ) AS document_operations
             WHERE NOT EXISTS (
                 SELECT 1 FROM operation_facts
                 WHERE operation_facts.tenant = document_operations.tenant
                   AND operation_facts.client_request_ulid = document_operations.client_request_ulid
             )",
            [],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|_| ())?;
    if missing_facts != 0 {
        return Err(());
    }
    let malformed_replication: bool = connection
        .query_row(
            "SELECT EXISTS(
            SELECT 1 FROM replication_bindings AS b
            WHERE length(b.fingerprint) <> 32
               OR ((EXISTS(SELECT 1 FROM operation_facts AS f
                           WHERE f.tenant = b.tenant AND f.client_request_ulid = b.operation
                             AND f.committed = 1))
                 + (EXISTS(SELECT 1 FROM replication_superseded AS s
                           WHERE s.tenant = b.tenant AND s.operation = b.operation))) <> 1
            UNION ALL
            SELECT 1 FROM replication_superseded AS s
            WHERE NOT EXISTS(SELECT 1 FROM replication_bindings AS b
                             WHERE b.tenant = s.tenant AND b.operation = s.operation)
        )",
            [],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    if malformed_replication {
        return Err(());
    }
    Ok(())
}

/// Quotes one identifier for SQL, doubling embedded double quotes.
fn quote_identifier(name: &str) -> String {
    format!("\"{}\"", name.replace('"', "\"\""))
}

/// One extracted column value: the scalar kinds the stored columns carry.
#[derive(Clone, Debug, PartialEq)]
enum ColumnValue {
    /// A string, in the text column.
    Text(String),
    /// A structured value's canonical typed bytes, stored as SQLite BLOB.
    Typed(Vec<u8>),
    /// An integer, in the numeric column.
    Integer(i64),
    /// A float, in the numeric column.
    Real(f64),
}

/// Extracts one descriptor field's stored-column value from a document.
///
/// The path is dotted navigation over object members (D-30's write-time
/// parse is the only extraction). Values the columns do not carry —
/// nulls and absent fields — extract nothing. Booleans, arrays, and objects
/// use canonical typed BLOBs in the text slot; SQLite preserves their storage
/// class, so these keys cannot collide with strings. Query predicates over
/// structured values still use the executor's own predicate.
fn extract_column_value(document: &Document, field: &str) -> Option<ColumnValue> {
    let mut value = document.value();
    for segment in field.split('.') {
        let DocumentValue::Object(fields) = value else { return None };
        value = fields.get(segment)?;
    }
    match value {
        DocumentValue::String(text) => Some(ColumnValue::Text(text.clone())),
        DocumentValue::Integer(integer) => Some(ColumnValue::Integer(*integer)),
        DocumentValue::Number(number) => Some(ColumnValue::Real(*number)),
        DocumentValue::Boolean(_) | DocumentValue::Array(_) | DocumentValue::Object(_) => {
            codec::encode_private_value(value).map(ColumnValue::Typed)
        }
        DocumentValue::Null => None,
    }
}

/// The schema statements every location carries.
const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS participant_prepares (tenant TEXT NOT NULL, incarnation TEXT NOT NULL, operation TEXT NOT NULL, participant TEXT NOT NULL, owner TEXT NOT NULL, digest BLOB NOT NULL, epoch INTEGER NOT NULL, snapshot INTEGER NOT NULL, payload BLOB NOT NULL, PRIMARY KEY(tenant,incarnation,operation,participant));
CREATE TABLE IF NOT EXISTS database_lifecycle (tenant TEXT PRIMARY KEY, body BLOB NOT NULL) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS lifecycle_operations (tenant TEXT NOT NULL, operation_id TEXT NOT NULL, action TEXT NOT NULL, paused INTEGER NOT NULL, generation INTEGER NOT NULL, PRIMARY KEY(tenant, operation_id)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS webhook_metadata (
    tenant TEXT NOT NULL,
    namespace TEXT NOT NULL,
    record_key TEXT NOT NULL,
    record BLOB NOT NULL,
    PRIMARY KEY (tenant, namespace, record_key)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS replication_publications (
    tenant TEXT NOT NULL,
    origin TEXT NOT NULL,
    origin_seq INTEGER NOT NULL,
    hlc INTEGER NOT NULL,
    operation TEXT NOT NULL,
    local_seq INTEGER NOT NULL,
    committed_at INTEGER NOT NULL,
    context BLOB NOT NULL,
    private_payload BLOB NOT NULL,
    PRIMARY KEY (tenant, origin, origin_seq),
    UNIQUE (tenant, local_seq)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS replication_publications_time ON replication_publications (local_seq, committed_at);

CREATE TABLE IF NOT EXISTS replication_publication_records (
    tenant TEXT NOT NULL,
    local_seq INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    collection TEXT NOT NULL,
    id TEXT NOT NULL,
    body BLOB,
    PRIMARY KEY (tenant, local_seq, ordinal)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS private_control_versions (
    tenant TEXT NOT NULL,
    control_key TEXT NOT NULL,
    origin TEXT NOT NULL,
    origin_seq INTEGER NOT NULL,
    hlc INTEGER NOT NULL,
    context BLOB NOT NULL,
    PRIMARY KEY (tenant, control_key)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS family_metadata (
    tenant TEXT NOT NULL,
    record_key TEXT NOT NULL,
    seq INTEGER NOT NULL,
    value BLOB,
    PRIMARY KEY (tenant, record_key, seq)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS schema_ttl (
    collection TEXT PRIMARY KEY,
    field TEXT,
    cursor_tenant TEXT NOT NULL DEFAULT '',
    cursor_id TEXT NOT NULL DEFAULT '',
    cursor_seq INTEGER NOT NULL DEFAULT -1,
    complete INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS schema_index_builds (
    tenant TEXT NOT NULL,
    collection TEXT NOT NULL,
    name TEXT NOT NULL,
    fields TEXT NOT NULL,
    watermark INTEGER NOT NULL,
    cursor_id TEXT NOT NULL,
    cursor_seq INTEGER NOT NULL,
    PRIMARY KEY (tenant, collection, name)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS schema_indexes (
    tenant TEXT NOT NULL,
    collection TEXT NOT NULL,
    name TEXT NOT NULL,
    fields TEXT NOT NULL,
    is_unique INTEGER NOT NULL,
    PRIMARY KEY (tenant, collection, name)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS schema_metadata (
    database_id TEXT NOT NULL,
    branch TEXT NOT NULL,
    collection TEXT NOT NULL,
    envelope BLOB NOT NULL,
    PRIMARY KEY (database_id, branch, collection)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value BLOB NOT NULL
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS documents (
    tenant      TEXT    NOT NULL,
    collection  TEXT    NOT NULL,
    id          TEXT    NOT NULL,
    commit_seq  INTEGER NOT NULL,
    commit_ulid TEXT    NOT NULL,
    revision    TEXT    NOT NULL,
    requested   INTEGER NOT NULL,
    digest      INTEGER NOT NULL,
    version     INTEGER NOT NULL,
    deleted     INTEGER NOT NULL,
    committed_at INTEGER NOT NULL DEFAULT 0,
    body        BLOB    NOT NULL,
    kind        TEXT    NOT NULL DEFAULT 'origin',
    origin      TEXT    NOT NULL DEFAULT '',
    origin_seq  INTEGER NOT NULL DEFAULT 0,
    hlc         INTEGER NOT NULL DEFAULT 0,
    -- Versioned dotted causal frontier. Empty is accepted only for rows
    -- written by the pre-context migration; all new rows carry a blob.
    context     BLOB    NOT NULL DEFAULT X'',
    expires_at  INTEGER,
    client_request_ulid TEXT NOT NULL DEFAULT '',
    -- Canonical D-42 names. The legacy aliases remain query-compatible while
    -- old files migrate without rewriting their retained bodies.
    seq         INTEGER GENERATED ALWAYS AS (commit_seq) VIRTUAL,
    dot         TEXT GENERATED ALWAYS AS (origin || ':' || origin_seq) VIRTUAL,
    PRIMARY KEY (tenant, collection, id, commit_seq)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS documents_seq ON documents (commit_seq);

CREATE INDEX IF NOT EXISTS documents_collection_cursor
    ON documents (collection, tenant, id, commit_seq);

-- Identifier-only reads also enforce reserved family namespace collisions.
-- The primary key cannot narrow by id without a collection predicate.
CREATE INDEX IF NOT EXISTS documents_id
    ON documents (tenant, id, collection, commit_seq);

CREATE INDEX IF NOT EXISTS documents_commit_ulid
    ON documents (tenant, commit_ulid);

CREATE TABLE IF NOT EXISTS rejections (
    tenant     TEXT    NOT NULL,
    operation  TEXT    NOT NULL,
    digest     INTEGER NOT NULL,
    rejection  INTEGER NOT NULL,
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;

-- Durable operation facts are the idempotency ledger without making the
-- retired `ledger` table part of the row model.  One row exists for every
-- accepted operation, including a delete that matched no document.  The
-- unique partial index makes the client request ULID a true per-tenant
-- identity while permitting the empty value carried by pre-migration rows.
CREATE TABLE IF NOT EXISTS index_lifecycle_events (
    sequence INTEGER PRIMARY KEY CHECK(sequence > 0),
    payload BLOB NOT NULL CHECK(length(payload) <= 524288),
    digest BLOB NOT NULL CHECK(length(digest) = 32)
);
CREATE TABLE IF NOT EXISTS invisible_index_builds (
    tenant TEXT NOT NULL,
    id TEXT NOT NULL,
    collection TEXT NOT NULL,
    columns BLOB NOT NULL CHECK(length(columns) <= 131072),
    digest BLOB NOT NULL CHECK(length(digest) = 32),
    target INTEGER NOT NULL,
    end_id TEXT NOT NULL,
    end_seq INTEGER NOT NULL,
    cursor_id TEXT NOT NULL DEFAULT '',
    cursor_seq INTEGER NOT NULL DEFAULT 0,
    complete INTEGER NOT NULL CHECK(complete IN (0, 1)),
    PRIMARY KEY (tenant, id)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS replication_superseded (
    tenant TEXT NOT NULL,
    operation TEXT NOT NULL,
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS replication_bindings (
    tenant TEXT NOT NULL,
    operation TEXT NOT NULL,
    fingerprint BLOB NOT NULL CHECK(length(fingerprint) = 32),
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;
-- Only branch import inserts these markers; client operation names confer no exemption.
CREATE TABLE IF NOT EXISTS branch_snapshot_receipts (
    tenant TEXT NOT NULL,
    operation TEXT NOT NULL,
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS operation_kind_markers (
    tenant TEXT NOT NULL,
    operation TEXT NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('metadata_only', 'internal_maintenance', 'replication')),
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS operation_facts (
    tenant              TEXT    NOT NULL,
    client_request_ulid TEXT    NOT NULL,
    digest              INTEGER NOT NULL,
    committed           INTEGER NOT NULL,
    rejection           INTEGER NOT NULL DEFAULT 0,
    sequence            INTEGER NOT NULL DEFAULT 0,
    requested           INTEGER NOT NULL DEFAULT 1,
    root                TEXT    NOT NULL DEFAULT '',
    writes              INTEGER NOT NULL DEFAULT 0,
    bytes               INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant, client_request_ulid)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS usage_placement (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    tenant TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL,
    partition TEXT NOT NULL, region TEXT NOT NULL, selected_regions TEXT NOT NULL,
    revision TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS usage_facts (
    tenant       TEXT    NOT NULL,
    project      TEXT    NOT NULL,
    database     TEXT    NOT NULL,
    partition    TEXT    NOT NULL,
    region       TEXT    NOT NULL,
    rule         INTEGER NOT NULL,
    fact_id      TEXT    NOT NULL,
    seq          INTEGER NOT NULL,
    start_millis INTEGER NOT NULL,
    end_millis   INTEGER NOT NULL,
    body         BLOB    NOT NULL,
    PRIMARY KEY (tenant, project, database, partition, region, rule, fact_id)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS usage_facts_tenant_seq ON usage_facts (tenant, seq);

CREATE TABLE IF NOT EXISTS usage_budget_allocations (
    tenant TEXT NOT NULL, operation TEXT NOT NULL, database_id TEXT NOT NULL,
    branch_id TEXT NOT NULL, holder_id TEXT NOT NULL, cycle_id TEXT NOT NULL,
    policy_epoch INTEGER NOT NULL, consent_epoch INTEGER NOT NULL, writer_epoch INTEGER NOT NULL,
    placement TEXT NOT NULL, payload_digest BLOB NOT NULL,
    units_total INTEGER NOT NULL, units_remaining INTEGER NOT NULL,
    expires_at TEXT NOT NULL,
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS usage_stream_seals (
    tenant TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL,
    partition TEXT NOT NULL, region TEXT NOT NULL,
    start_millis INTEGER NOT NULL, end_millis INTEGER NOT NULL,
    through_sequence INTEGER NOT NULL, write_units TEXT NOT NULL,
    storage_byte_seconds TEXT NOT NULL, fact_count INTEGER NOT NULL,
    checksum BLOB NOT NULL,
    PRIMARY KEY (tenant, project, database, partition, region, start_millis, end_millis)
) WITHOUT ROWID;


CREATE TABLE IF NOT EXISTS origin_prefixes (
    tenant TEXT NOT NULL,
    origin TEXT NOT NULL,
    seq INTEGER NOT NULL CHECK (seq > 0),
    hlc INTEGER NOT NULL,
    PRIMARY KEY (tenant, origin)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS journal_pruning (
    tenant TEXT PRIMARY KEY,
    pruned_through INTEGER NOT NULL CHECK (pruned_through > 0)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS consumer_frontiers (
    name TEXT    PRIMARY KEY,
    seq  INTEGER NOT NULL
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS index_descriptors (
    collection       TEXT    NOT NULL,
    field            TEXT    NOT NULL,
    kind             INTEGER NOT NULL,
    state            INTEGER NOT NULL,
    created_at       INTEGER NOT NULL,
    backfill_below   INTEGER NOT NULL DEFAULT 0,
    backfill_cursor  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (collection, field)
) WITHOUT ROWID;

-- Full-text descriptors (S-02 slice D, D-39): one row per
-- `(collection, field)` text auto-index. A field may carry both a value
-- descriptor above and a text descriptor here; the two backfills advance
-- independently. The indexed content lives in `fts_docs`, maintained by
-- the Rust write path in the commit transaction (zero stale reads).
CREATE TABLE IF NOT EXISTS fts_descriptors (
    collection       TEXT    NOT NULL,
    field            TEXT    NOT NULL,
    state            INTEGER NOT NULL,
    created_at       INTEGER NOT NULL,
    backfill_below   INTEGER NOT NULL DEFAULT 0,
    backfill_cursor  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (collection, field)
) WITHOUT ROWID;

-- The full-text index (S-02 slice D, D-39): one row per
-- `(tenant, collection, field, doc_id)`, holding the field's
-- Standard-analyzer term stream. A plain FTS5 table — deliberately not
-- `contentless_delete`: a contentless table stores no column values at
-- all, so the `UNINDEXED` partition columns could neither filter nor
-- return rows (verified against the engine build). Crash consistency
-- comes from the maintenance discipline instead: every `DELETE`+`INSERT`
-- rides the committing transaction, so a crash can never leave the index
-- ahead of or behind the documents.
CREATE VIRTUAL TABLE IF NOT EXISTS fts_docs USING fts5(
    tenant UNINDEXED,
    collection UNINDEXED,
    field UNINDEXED,
    doc_id UNINDEXED,
    text
);

CREATE TABLE IF NOT EXISTS usage_counters (
    key    TEXT    PRIMARY KEY,
    seq_hi INTEGER NOT NULL,
    ops    INTEGER NOT NULL
) WITHOUT ROWID;

-- Per-database metering facts (E-9, D-16/D-42): the billable rollup of one
-- database's folded journal rows, flushed once per group-commit wave in the
-- wave's own transaction and reconciled from the journal on open. `db` is
-- the tenant identity text (D-51: tenant = database). `writes` counts
-- billed write units under the D-16 ceil(bytes / 4 KiB) rule; `bytes` sums
-- stored canonical body bytes. The journal stays the truth; this table is
-- its cache, like `usage_counters`.
CREATE TABLE IF NOT EXISTS metering_db_facts (
    db     TEXT    PRIMARY KEY,
    seq_hi INTEGER NOT NULL,
    writes INTEGER NOT NULL,
    bytes  INTEGER NOT NULL
) WITHOUT ROWID;

-- Optional per-collection history windows.  A missing row means the D-25
-- default (30 days here, capped by the plan at the caller).  The CHECK is
-- deliberately in SQLite so old files and direct migrations cannot install
-- an unsupported retention window.
CREATE TABLE IF NOT EXISTS collection_history (
    collection TEXT PRIMARY KEY,
    days       INTEGER NOT NULL CHECK (days BETWEEN 1 AND 30)
) WITHOUT ROWID;

-- Declared merge schemas (S-05 D-36): one row per collection naming the
-- typed CRDT fields. Schema-free collections have no row and merge with
-- the remove-wins map default, storing no per-field kernels.
CREATE TABLE IF NOT EXISTS merge_schemas (
    collection TEXT PRIMARY KEY,
    schema     TEXT NOT NULL
) WITHOUT ROWID;

-- Per-field causal kernels (S-05 D-36): the newest stamp that contributed
-- to one field of one document. Only declared collections write rows
-- here; schema-free collections store none.
CREATE TABLE IF NOT EXISTS merge_kernels (
    tenant     TEXT    NOT NULL,
    collection TEXT    NOT NULL,
    id         TEXT    NOT NULL,
    field      TEXT    NOT NULL,
    hlc        INTEGER NOT NULL,
    origin     TEXT    NOT NULL DEFAULT '',
    PRIMARY KEY (tenant, collection, id, field)
) WITHOUT ROWID;

-- Deterministic conflict losers (S-05 D-36): every merge that superseded
-- one side records the loser with the winner, policy, and explanation.
-- Both regions reaching the same merge compute the same row, so history
-- explanations are stable across regions.
CREATE TABLE IF NOT EXISTS merge_conflicts (
    tenant             TEXT    NOT NULL,
    collection         TEXT    NOT NULL,
    id                 TEXT    NOT NULL,
    loser_commit_ulid  TEXT    NOT NULL,
    loser_hlc          INTEGER NOT NULL,
    loser_origin       TEXT    NOT NULL DEFAULT '',
    loser_deleted      INTEGER NOT NULL,
    winner_commit_ulid TEXT    NOT NULL,
    winner_hlc         INTEGER NOT NULL,
    winner_origin      TEXT    NOT NULL DEFAULT '',
    policy             TEXT    NOT NULL,
    explanation        TEXT    NOT NULL,
    PRIMARY KEY (tenant, collection, id, loser_commit_ulid)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS merge_conflicts_key
    ON merge_conflicts (tenant, collection, id);
";

/// The zero root head a fresh location records, before any commit chains it.
const INITIAL_ROOT_HEAD: &str = "sqlite3-00000000000000000000000000000000";

/// Metadata key binding the `SQLite` schema to the location's owner record.
const SCHEMA_BINDING_KEY: &str = "sqlite_schema_binding";

/// Rebinds a verified backup inside a fresh, private staging location.
pub(crate) fn import_backup_file(
    source: &Path,
    backup: &Path,
    target: &Path,
    child_owner: &crate::storage::DatabaseOwner,
) -> Result<(), ()> {
    let source_binding = database_binding(source).map_err(|_| ())?.ok_or(())?;
    let target_binding = database_binding(target).map_err(|_| ())?.ok_or(())?;
    let file = target.join(DATABASE_FILE);
    let target_origin: String = Connection::open(&file)
        .and_then(|connection| {
            connection.query_row("SELECT value FROM meta WHERE key = 'local_origin'", [], |row| {
                row.get(0)
            })
        })
        .map_err(|_| ())?;
    if !std::fs::symlink_metadata(backup).map_err(|_| ())?.file_type().is_file() {
        return Err(());
    }
    std::fs::copy(backup, &file).map_err(|_| ())?;
    let mut connection = Connection::open(&file).map_err(|_| ())?;
    if validate_database_binding(&connection, &source_binding).map_err(|_| ())? {
        return Err(());
    }
    configure_connection(&connection).map_err(|_| ())?;
    let integrity: String =
        connection.query_row("PRAGMA integrity_check", [], |row| row.get(0)).map_err(|_| ())?;
    if integrity != "ok" {
        return Err(());
    }
    let transaction = connection.transaction().map_err(|_| ())?;
    // The restored database owns a new billing stream. Preserve the source
    // snapshot as imported data without inheriting its receipts or lifecycle.
    branch_provenance::transform_provenance(&transaction, child_owner)?;
    transaction
        .execute(
            "UPDATE meta SET value = ?1 WHERE key = ?2",
            rusqlite::params![target_binding, SCHEMA_BINDING_KEY],
        )
        .map_err(|_| ())?;
    transaction
        .execute(
            "INSERT INTO meta(key,value) VALUES('local_origin',?1)
             ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            [target_origin],
        )
        .map_err(|_| ())?;
    transaction.commit().map_err(|_| ())?;
    connection.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)").map_err(|_| ())?;
    Ok(())
}

/// Version marker for the canonical JSON document body stored in `body`.
const BODY_ENCODING_KEY: &str = "document_body_encoding";
const BODY_ENCODING_JSON_V1: &str = "json-v1";

/// A pre-binding database is accepted only when it has the old core shape.
/// This is the deliberate upgrade rule for prelaunch fixtures: after the
/// read-only shape check, the normal schema migration writes the binding.
const LEGACY_CORE_TABLES: &[&str] = &["meta", "documents"];

fn has_legacy_core_schema(connection: &Connection) -> Result<bool, ()> {
    let count = connection
        .query_row(
            "SELECT COUNT(*) FROM sqlite_master
             WHERE type = 'table' AND name IN ('meta', 'documents')",
            [],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|_| ())?;
    if usize::try_from(count).map_err(|_| ())? != LEGACY_CORE_TABLES.len() {
        return Ok(false);
    }
    let required_meta = ["key", "value"];
    let required_documents = [
        "tenant",
        "collection",
        "id",
        "commit_seq",
        "commit_ulid",
        "revision",
        "requested",
        "digest",
        "version",
        "deleted",
        "committed_at",
        "body",
    ];
    for (table, columns) in [("meta", &required_meta[..]), ("documents", &required_documents[..])] {
        for column in columns {
            let present = connection
                .query_row(
                    "SELECT EXISTS(
                         SELECT 1 FROM pragma_table_info(?1) WHERE name = ?2
                     )",
                    rusqlite::params![table, column],
                    |row| row.get::<_, bool>(0),
                )
                .map_err(|_| ())?;
            if !present {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

/// Validates an existing file without running WAL or schema pragmas. Those
/// pragmas can create or alter `SQLite` sidecar state, so identity rejection
/// must happen first.
fn validate_database_binding(
    connection: &Connection,
    expected: &str,
) -> Result<bool, StorageOpenError> {
    let has_meta = connection
        .query_row(
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta')",
            [],
            |row| row.get::<_, bool>(0),
        )
        .map_err(|_| StorageOpenError::Unavailable)?;
    let binding = if has_meta {
        connection
            .query_row("SELECT value FROM meta WHERE key = ?1", [SCHEMA_BINDING_KEY], |row| {
                row.get::<_, String>(0)
            })
            .optional()
            .map_err(|_| StorageOpenError::Unavailable)?
    } else {
        None
    };
    if let Some(actual) = binding {
        if actual != expected {
            return Err(StorageOpenError::Unavailable);
        }
        // The owner marker is necessary but not sufficient: a foreign file
        // must not be able to pass by copying one metadata row alone.
        return if has_legacy_core_schema(connection).map_err(|()| StorageOpenError::Unavailable)? {
            Ok(false)
        } else {
            Err(StorageOpenError::Unavailable)
        };
    }
    if has_legacy_core_schema(connection).map_err(|()| StorageOpenError::Unavailable)? {
        return Ok(true);
    }
    // A valid SQLite header is not enough to identify a TerraBase file. In
    // particular, do not let CREATE TABLE IF NOT EXISTS adopt it.
    Err(StorageOpenError::Unavailable)
}

/// The `SQLite` storage engine behind [`StoragePort`].
pub(crate) struct SqliteStorage {
    local_origin: String,
    encryption: Option<Arc<encrypted_vfs::EncryptedVfs>>,
    /// The location's database path, kept for pool overflow opens.
    database_path: PathBuf,
    /// Identity pinned when the original writer was opened.
    expected_binding: String,
    /// The single writer connection. One writer at a time is `SQLite`'s own
    /// WAL contract; the mutex is interior so a later engine read split can
    /// keep using this backend without restructuring.
    writer: Arc<Mutex<Connection>>,
    /// The one writer worker for this file. Every commit request enters this
    /// queue, where a bounded 2 ms wave is formed and committed atomically.
    committer: Committer,
    /// A separate connection performs passive checkpoints between waves.
    checkpointer: Checkpointer,
    checkpoint_gate: Arc<Mutex<()>>,
    /// Idle read connections, checked out last-in-first-out and returned on
    /// drop.
    readers: Arc<Mutex<ReaderPool>>,
    /// The extracted-column descriptor view (D-42), refreshed on
    /// registration and consulted by the write and scan paths.
    extracted: Arc<Mutex<ExtractedState>>,
    /// The full-text descriptor view (S-02 slice D), refreshed on text
    /// registration and consulted by the write and search paths.
    fts: Arc<Mutex<FtsState>>,
    /// Served-read counters per database for abuse guards and cost
    /// attribution (E-9, D-16/D-44: reads are metered, never billed).
    /// In-memory only — guards need no durability — keyed by tenant text.
    reads: Mutex<std::collections::BTreeMap<String, u64>>,
    /// Monotonic last-request marker used by open-file eviction.
    last_activity: AtomicU64,
    /// The test-only fault plan (see [`StoragePort::arm_test_fault`]).
    #[cfg(test)]
    faults: Arc<Mutex<FaultPlan>>,
}

impl Drop for SqliteStorage {
    fn drop(&mut self) {
        // Stop auxiliary threads before their shared state and writer are
        // dropped; this also closes a quiet file's checkpoint connection.
        self.checkpointer.stop();
        self.committer.stop();
    }
}

/// A pooled reader and the instant at which it was returned to the pool.
struct ReaderEntry {
    connection: Connection,
    returned_at: Instant,
    /// Conservative estimate of the connection's resident cache footprint.
    resident_bytes: usize,
}

/// Reader connections are bounded by an RSS estimate, not by a fixed count.
struct ReaderPool {
    entries: Vec<ReaderEntry>,
    resident_bytes: usize,
    budget_bytes: usize,
    idle_timeout: Duration,
}

impl ReaderPool {
    fn new(budget_bytes: usize) -> Self {
        Self {
            entries: Vec::with_capacity(READ_POOL_WIDTH),
            resident_bytes: 0,
            budget_bytes,
            idle_timeout: DEFAULT_IDLE_TIMEOUT,
        }
    }

    fn close_idle(&mut self, now: Instant) {
        let timeout = self.idle_timeout;
        let mut kept = Vec::with_capacity(self.entries.len());
        for entry in self.entries.drain(..) {
            if now.duration_since(entry.returned_at) >= timeout {
                self.resident_bytes = self.resident_bytes.saturating_sub(entry.resident_bytes);
            } else {
                kept.push(entry);
            }
        }
        self.entries = kept;
    }

    fn push(&mut self, connection: Connection, resident_bytes: usize) {
        if resident_bytes > self.budget_bytes {
            return;
        }
        while self.resident_bytes.saturating_add(resident_bytes) > self.budget_bytes {
            let Some(oldest) = self
                .entries
                .iter()
                .enumerate()
                .min_by_key(|(_, entry)| entry.returned_at)
                .map(|(index, _)| index)
            else {
                break;
            };
            let entry = self.entries.swap_remove(oldest);
            self.resident_bytes = self.resident_bytes.saturating_sub(entry.resident_bytes);
        }
        self.resident_bytes = self.resident_bytes.saturating_add(resident_bytes);
        self.entries.push(ReaderEntry { connection, returned_at: Instant::now(), resident_bytes });
    }

    fn pop(&mut self) -> Option<ReaderEntry> {
        let entry = self.entries.pop()?;
        self.resident_bytes = self.resident_bytes.saturating_sub(entry.resident_bytes);
        Some(entry)
    }
}

/// One request handled by the file's dedicated committer.
struct CommitRequest {
    intents: Vec<CommitIntent>,
    response: mpsc::SyncSender<Vec<CommitResult>>,
}

/// Immutable state the committer needs while owning the writer lock.
#[derive(Clone)]
struct CommitContext {
    local_origin: String,
    extracted: Arc<Mutex<ExtractedState>>,
    fts: Arc<Mutex<FtsState>>,
    #[cfg(test)]
    faults: Arc<Mutex<FaultPlan>>,
}

/// Dedicated per-file commit worker. Dropping it stops and joins the thread,
/// which makes idle close and restart behavior deterministic.
struct Committer {
    sender: Option<SyncSender<CommitRequest>>,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Committer {
    fn submit(&self, intents: Vec<CommitIntent>) -> Result<Vec<CommitResult>, ()> {
        let (sender, receiver) = mpsc::sync_channel(1);
        self.sender
            .as_ref()
            .ok_or(())?
            .send(CommitRequest { intents, response: sender })
            .map_err(|_| ())?;
        receiver.recv().map_err(|_| ())
    }

    fn stop(&mut self) {
        self.stop.store(true, Ordering::Release);
        let _ = self.sender.take();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for Committer {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Passive checkpoint worker. It never owns the writer connection and thus
/// cannot extend a writer mutex hold or turn a checkpoint into commit work.
struct Checkpointer {
    wake: SyncSender<()>,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Checkpointer {
    fn stop(&mut self) {
        self.stop.store(true, Ordering::Release);
        let _ = self.wake.try_send(());
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for Checkpointer {
    fn drop(&mut self) {
        self.stop();
    }
}

fn spawn_committer(
    writer: Arc<Mutex<Connection>>,
    context: CommitContext,
    checkpoint_wake: SyncSender<()>,
) -> Result<Committer, StorageOpenError> {
    let (sender, receiver) = mpsc::sync_channel::<CommitRequest>(256);
    let stop = Arc::new(AtomicBool::new(false));
    let worker_stop = Arc::clone(&stop);
    let thread = std::thread::Builder::new()
        .name("terrabase-sqlite-committer".to_owned())
        .spawn(move || {
            while !worker_stop.load(Ordering::Acquire) {
                let first = match receiver.recv() {
                    Ok(request) => request,
                    Err(_) => break,
                };
                // Fixed bounded wave: collect concurrent arrivals for 2 ms,
                // but never let one producer monopolize the transaction.
                let deadline = Instant::now() + COMMIT_WAVE_WAIT;
                let mut requests = vec![first];
                let mut count = requests[0].intents.len();
                while count < COMMIT_WAVE_MAX_INTENTS {
                    let remaining = deadline.saturating_duration_since(Instant::now());
                    if remaining.is_zero() {
                        break;
                    }
                    match receiver.recv_timeout(remaining) {
                        Ok(request) => {
                            count = count.saturating_add(request.intents.len());
                            requests.push(request);
                        }
                        Err(RecvTimeoutError::Timeout | RecvTimeoutError::Disconnected) => break,
                    }
                }
                let lengths: Vec<usize> =
                    requests.iter().map(|request| request.intents.len()).collect();
                let mut intents = Vec::new();
                for request in &mut requests {
                    intents.append(&mut request.intents);
                }
                let results = match writer.lock() {
                    Ok(mut connection) => context.commit_transaction(&mut connection, &intents),
                    Err(_) => Err(()),
                }
                .unwrap_or_else(|_| {
                    vec![CommitResult::Rejected(CommitRejection::Storage); intents.len()]
                });
                // One queued wake covers all waves that arrive while the
                // checkpointer is busy. Never wait for checkpoint work here.
                let _ = checkpoint_wake.try_send(());
                let mut offset: usize = 0;
                for (request, length) in requests.into_iter().zip(lengths) {
                    let end = offset.saturating_add(length);
                    let _ = request.response.send(results[offset..end].to_vec());
                    offset = end;
                }
            }
        })
        .map_err(|_| StorageOpenError::Unavailable)?;
    Ok(Committer { sender: Some(sender), stop, thread: Some(thread) })
}

fn spawn_checkpointer(
    path: PathBuf,
    expected_binding: &str,
    readers: Arc<Mutex<ReaderPool>>,
    checkpoint_gate: Arc<Mutex<()>>,
    encryption: Option<&encrypted_vfs::EncryptedVfs>,
) -> Result<Checkpointer, StorageOpenError> {
    // Open synchronously so a failed checkpoint connection fails database
    // startup. Never create a replacement file if the original disappeared.
    let connection = open_connection(&path, expected_binding, encryption)?;
    let (wake, pending) = mpsc::sync_channel(1);
    let stop = Arc::new(AtomicBool::new(false));
    let worker_stop = Arc::clone(&stop);
    let thread = std::thread::Builder::new()
        .name("terrabase-sqlite-checkpointer".to_owned())
        .spawn(move || {
            while !worker_stop.load(Ordering::Acquire) {
                // Writes wake immediately; the slower fallback also covers
                // direct metadata transactions and idle-reader reclamation.
                match pending.recv_timeout(CHECKPOINT_IDLE_WAIT) {
                    Ok(()) | Err(RecvTimeoutError::Timeout) => {}
                    Err(RecvTimeoutError::Disconnected) => break,
                }
                if worker_stop.load(Ordering::Acquire) {
                    break;
                }
                // PASSIVE never waits for readers and never runs on the
                // committer's connection; a busy result is safe to retry.
                if let Ok(_gate) = checkpoint_gate.lock() {
                    let _ = connection.query_row("PRAGMA wal_checkpoint(PASSIVE)", [], |_| Ok(()));
                }
                if let Ok(mut pool) = readers.lock() {
                    pool.close_idle(Instant::now());
                }
            }
        })
        .map_err(|_| StorageOpenError::Unavailable)?;
    Ok(Checkpointer { wake, stop, thread: Some(thread) })
}

/// One checked-out read connection, returned to the pool on drop.
enum ReaderGuard<'a> {
    Pooled { pool: &'a Arc<Mutex<ReaderPool>>, entry: Option<ReaderEntry> },
    Writer(std::sync::MutexGuard<'a, Connection>),
}

impl ReaderGuard<'_> {
    /// The checked-out connection. A pooled entry is removed only during Drop.
    fn connection(&self) -> &Connection {
        match self {
            Self::Pooled { entry, .. } => &entry.as_ref().expect("live pooled entry").connection,
            Self::Writer(writer) => writer,
        }
    }
}

impl Drop for ReaderGuard<'_> {
    fn drop(&mut self) {
        if let Self::Pooled { pool, entry } = self {
            if let Some(entry) = entry.take() {
                let mut pool = pool.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                pool.push(entry.connection, entry.resident_bytes);
            }
        }
    }
}

impl SqliteStorage {
    /// Opens (or creates) the `SQLite` location in `dir`.
    ///
    /// # Errors
    ///
    /// Returns [`StorageOpenError::Unavailable`] when the database cannot be
    /// opened or initialized.
    pub(crate) fn open(dir: &Path) -> Result<Self, StorageOpenError> {
        Self::open_with_origin(dir, None)
    }

    pub(crate) fn open_with_origin(
        dir: &Path,
        origin: Option<&str>,
    ) -> Result<Self, StorageOpenError> {
        Self::open_with_keys(dir, origin, None)
    }

    pub(crate) fn open_with_keys(
        dir: &Path,
        origin: Option<&str>,
        keys: Option<Arc<dyn crate::encryption::DatabaseKeySource>>,
    ) -> Result<Self, StorageOpenError> {
        Self::open_with_keys_mode(dir, origin, keys, true)
    }

    pub(crate) fn open_existing_with_keys(
        dir: &Path,
        origin: Option<&str>,
        keys: Option<Arc<dyn crate::encryption::DatabaseKeySource>>,
    ) -> Result<Self, StorageOpenError> {
        Self::open_with_keys_mode(dir, origin, keys, false)
    }

    fn open_with_keys_mode(
        dir: &Path,
        origin: Option<&str>,
        keys: Option<Arc<dyn crate::encryption::DatabaseKeySource>>,
        allow_create: bool,
    ) -> Result<Self, StorageOpenError> {
        // The engine path pre-creates the location; creating here too keeps
        // the seam's direct callers (and the FileStorage reference's
        // behavior) working on a named-but-absent directory.
        if allow_create {
            std::fs::create_dir_all(dir).map_err(|_| StorageOpenError::Unavailable)?;
        } else if !dir.is_dir() {
            return Err(StorageOpenError::Unavailable);
        }
        let database_path = dir.join(DATABASE_FILE);
        let encrypted_exists = encrypted_file::companion(&database_path).exists();
        if keys.is_none() && encrypted_exists {
            return Err(StorageOpenError::Unavailable);
        }
        if keys.is_some() && database_path.metadata().is_ok_and(|metadata| metadata.len() > 0) {
            return Err(StorageOpenError::Unavailable);
        }
        let database_exists =
            if keys.is_some() { encrypted_exists } else { database_path.exists() };
        let encryption = keys
            .map(|keys| encrypted_vfs::EncryptedVfs::register(&database_path, keys))
            .transpose()
            .map_err(|_| StorageOpenError::Unavailable)?;
        let expected_binding = database_binding(dir)
            .map_err(|_| StorageOpenError::Unavailable)?
            .unwrap_or_else(|| format!("path-{}", local_origin_for(&database_path)));
        // Open the handle without WAL/schema pragmas first. An existing file
        // must prove its identity before any SQLite sidecar or schema state is
        // changed; otherwise a replacement valid SQLite file could be adopted.
        let mut flags = OpenFlags::SQLITE_OPEN_READ_WRITE;
        if allow_create {
            flags |= OpenFlags::SQLITE_OPEN_CREATE;
        }
        let mut writer = raw_connection(&database_path, flags, encryption.as_deref())?;
        if database_exists || !allow_create {
            let legacy = validate_database_binding(&writer, &expected_binding)?;
            if !legacy {
                // A bound file with a missing root is corrupt, not a fresh
                // legacy file.  Do this check before WAL/schema pragmas so
                // opening it cannot silently manufacture a new root chain.
                let root = writer
                    .query_row("SELECT value FROM meta WHERE key = 'root_head'", [], |row| {
                        row.get::<_, String>(0)
                    })
                    .map_err(|_| StorageOpenError::Unavailable)?;
                if root.is_empty() {
                    return Err(StorageOpenError::Unavailable);
                }
            }
        }
        configure_connection(&writer)?;
        {
            let transaction = writer.transaction().map_err(|_| StorageOpenError::Unavailable)?;
            let has_pruning_watermarks: bool = transaction.query_row(
                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'journal_pruning')",
                [], |row| row.get(0),
            ).map_err(|_| StorageOpenError::Unavailable)?;
            transaction.execute_batch(SCHEMA).map_err(|_| StorageOpenError::Unavailable)?;
            repair_summary::initialize(&transaction).map_err(|_| StorageOpenError::Unavailable)?;
            if !has_pruning_watermarks {
                // Legacy files have no exact deletion evidence. Refuse the
                // potentially pruned prefix rather than infer completeness
                // from retained rows (which can have legitimate holes).
                transaction
                    .execute(
                        "INSERT INTO journal_pruning (tenant, pruned_through)
                     SELECT tenant, frontier - 1 FROM
                       (SELECT tenant FROM documents UNION SELECT tenant FROM origin_prefixes)
                     CROSS JOIN (SELECT MAX(seq) AS frontier FROM consumer_frontiers)
                     WHERE frontier > 1",
                        [],
                    )
                    .map_err(|_| StorageOpenError::Unavailable)?;
            }
            transaction.commit().map_err(|_| StorageOpenError::Unavailable)?;
        }
        migrate_fts_keys(&mut writer).map_err(|()| StorageOpenError::Unavailable)?;
        writer
            .execute(
                "INSERT OR IGNORE INTO meta (key, value) VALUES (?1, ?2)",
                rusqlite::params![SCHEMA_BINDING_KEY, expected_binding],
            )
            .map_err(|_| StorageOpenError::Unavailable)?;
        migrate_documents_provenance(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        let local_origin = origin.map_or_else(|| local_origin_for(&database_path), str::to_owned);
        writer
            .execute(
                "INSERT OR IGNORE INTO meta (key, value) VALUES ('local_origin', ?1)",
                [&local_origin],
            )
            .map_err(|_| StorageOpenError::Unavailable)?;
        let local_origin = writer
            .query_row("SELECT value FROM meta WHERE key = 'local_origin'", [], |row| {
                row.get::<_, String>(0)
            })
            .map_err(|_| StorageOpenError::Unavailable)?;
        if origin.is_some_and(|expected| expected != local_origin) {
            return Err(StorageOpenError::Unavailable);
        }
        migrate_documents_origin_sequence(&mut writer, &local_origin)
            .map_err(|()| StorageOpenError::Unavailable)?;
        migrate_documents_context(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        migrate_documents_ttl(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        migrate_documents_idempotency(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        migrate_documents_row_aliases(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        storage_closure::initialize(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        accounting::initialize(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        storage_intervals::initialize(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        migrate_operation_facts(&mut writer).map_err(|()| StorageOpenError::Unavailable)?;
        validate_operation_facts(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        // The SQLite schema is authoritative; the metadata table carries only
        // runtime state, never a backend selector.
        writer
            .execute(
                "INSERT OR IGNORE INTO meta (key, value) VALUES ('root_head', ?1)",
                [INITIAL_ROOT_HEAD],
            )
            .map_err(|_| StorageOpenError::Unavailable)?;
        // Usage-counter reconciliation (D-42): the counter is a cache of
        // the journal, never an independent source of truth. On open, a
        // counter whose high-water trails the folded rows — or no counter
        // at all — is recomputed from the journal itself, so a torn WAL
        // tail or a lost wave leaves the counter honest.
        reconcile_usage_counters(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        // E-9 per-database metering facts reconcile the same way: the table
        // is a cache of the folded rows, so a torn wave or a lost counter
        // row is rebuilt from the journal on open.
        reconcile_metering_db_facts(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        let mut readers = ReaderPool::new(rss_budget_bytes());
        for _ in 0..READ_POOL_WIDTH {
            let connection =
                open_connection(&database_path, &expected_binding, encryption.as_deref())?;
            readers.push(connection, cache_resident_bytes());
        }
        let mut extracted = ExtractedState::default();
        load_extracted_state(&writer, &mut extracted)
            .map_err(|()| StorageOpenError::Unavailable)?;
        let mut fts = FtsState::default();
        load_fts_state(&writer, &mut fts).map_err(|()| StorageOpenError::Unavailable)?;
        let writer = Arc::new(Mutex::new(writer));
        let extracted = Arc::new(Mutex::new(extracted));
        let fts = Arc::new(Mutex::new(fts));
        #[cfg(test)]
        let faults = Arc::new(Mutex::new(FaultPlan::empty()));
        let context = CommitContext {
            local_origin: local_origin.clone(),
            extracted: Arc::clone(&extracted),
            fts: Arc::clone(&fts),
            #[cfg(test)]
            faults: Arc::clone(&faults),
        };
        let readers = Arc::new(Mutex::new(readers));
        let checkpoint_gate = Arc::new(Mutex::new(()));
        let checkpointer = spawn_checkpointer(
            database_path.clone(),
            &expected_binding,
            Arc::clone(&readers),
            Arc::clone(&checkpoint_gate),
            encryption.as_deref(),
        )?;
        let committer = spawn_committer(Arc::clone(&writer), context, checkpointer.wake.clone())?;
        Ok(Self {
            local_origin,
            encryption,
            database_path,
            expected_binding,
            writer,
            committer,
            checkpointer,
            checkpoint_gate,
            readers,
            extracted,
            fts,
            reads: Mutex::new(std::collections::BTreeMap::new()),
            last_activity: AtomicU64::new(monotonic_millis()),
            #[cfg(test)]
            faults,
        })
    }

    /// Checks out one read connection, opening a fresh one when the pool is
    /// momentarily empty. If opening a reader fails, serialize through the
    /// already-bound writer rather than constructing an empty guard.
    fn reader(&self) -> ReaderGuard<'_> {
        self.touch();
        let pooled = {
            let mut pool = self.readers.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            pool.close_idle(Instant::now());
            pool.pop()
        };
        let entry = pooled.or_else(|| {
            open_connection(&self.database_path, &self.expected_binding, self.encryption.as_deref())
                .ok()
                .map(|connection| ReaderEntry {
                    connection,
                    returned_at: Instant::now(),
                    resident_bytes: cache_resident_bytes(),
                })
        });
        match entry {
            Some(entry) => ReaderGuard::Pooled { pool: &self.readers, entry: Some(entry) },
            None => ReaderGuard::Writer(
                self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner),
            ),
        }
    }

    /// Marks activity for the open-file lifecycle. The catalog owner can use
    /// [`close_idle`] to release all pooled read handles for a quiet file.
    fn touch(&self) {
        // A monotonic timestamp avoids wall-clock jumps in idle decisions.
        self.last_activity.store(monotonic_millis(), Ordering::Relaxed);
    }

    /// Closes idle pooled readers and returns whether the file was idle. The
    /// writer remains available for a request and is closed by `Drop`, which
    /// keeps this hook safe to call from a catalog eviction pass.
    pub(crate) fn close_idle(&self, idle: Duration) -> bool {
        let now = Instant::now();
        let mut readers = self.readers.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        readers.idle_timeout = idle;
        readers.close_idle(now);
        monotonic_millis().saturating_sub(self.last_activity.load(Ordering::Relaxed))
            >= idle.as_millis().try_into().unwrap_or(u64::MAX)
    }

    // ---- Journal-pruning lifecycle (TB-20's bounded-journal invariant) ---

    /// Hands a test the writer connection (the sweep's backdating helper
    /// and the crash tests assert against raw rows).
    #[cfg(test)]
    pub(crate) fn raw_writer_connection(&self) -> std::sync::MutexGuard<'_, Connection> {
        self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Records or advances one consumer's journal frontier. The frontier is
    /// monotonic: a lower sequence never moves it back.
    ///
    /// Test-gated until the engine wires its real consumers (replication
    /// acks, sync checkpoints, metering export) and the TB-22 backup lane
    /// feeds its Litestream-position frontier in; the pruning semantics and
    /// their bound are pinned here first.
    ///
    /// # Errors
    ///
    /// Returns `Err(())` when durable storage failed.
    #[cfg(test)]
    pub(crate) fn advance_consumer_frontier(
        &mut self,
        name: &str,
        sequence: u64,
    ) -> Result<(), ()> {
        let writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        writer
            .execute(
                "INSERT INTO consumer_frontiers (name, seq) VALUES (?1, ?2)
                 ON CONFLICT(name) DO UPDATE SET seq = MAX(seq, excluded.seq)",
                rusqlite::params![name, sequence as i64],
            )
            .map(|_| ())
            .map_err(|_| ())
    }

    /// Deletes journal and version rows strictly below the minimum consumer
    /// frontier — the lifecycle sweep [`Self::compact`] runs — returning the
    /// number of journal rows pruned. A row any consumer still needs is
    /// never pruned: the floor is the MINIMUM frontier, and an empty
    /// frontier table prunes nothing.
    ///
    /// # Errors
    ///
    /// Returns `Err(())` when durable storage failed.
    pub(crate) fn prune_below_consumer_frontiers(&self) -> Result<u64, ()> {
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let transaction =
            writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
        let accounting_now = accounting::begin_change(&transaction, unix_millis().max(0) as u64)?;
        let Some(frontier) = history_floor_on(&transaction)? else {
            accounting::settle_due(&transaction, accounting_now)?;
            storage_intervals::prune_retired(&transaction)?;
            transaction.commit().map_err(|_| ())?;
            return Ok(0);
        };
        // The D-42 history floor: a row is prunable only when every
        // consumer frontier has passed it AND it is older than its
        // collection's 1..30 day retention window. Missing declarations use
        // the 30-day default. The cutoff rises with the sweep's own clock,
        // so a fresh row is never prunable regardless of frontiers.
        let now = unix_millis();
        // Each row's history window pins its entire commit. Independent
        // commits can expire independently, without splitting publications.
        let floor = frontier;
        // Whole-commit retention: an expired below-floor commit survives
        // when one of its rows is the newest version of its key — the row an
        // above-floor snapshot resolves when no later write exists. Every
        // other below-floor commit is fully shadowed, and pruning takes
        // whole commits only (never some of a commit's rows), so the
        // folded journal never serves a partial publication. The shadow
        // witness is stable through the statement (a key's newest row is
        // never itself deleted), so the sweep does not depend on scan
        // order.
        // Preserve proven causal progress in the same transaction that removes
        // its row evidence. A rollback must undo both changes together.
        let tenants = {
            let mut statement =
                transaction.prepare("SELECT DISTINCT tenant FROM documents").map_err(|_| ())?;
            statement
                .query_map([], |row| row.get::<_, String>(0))
                .map_err(|_| ())?
                .collect::<Result<Vec<_>, _>>()
                .map_err(|_| ())?
        };
        for tenant in tenants {
            for entry in origin_prefixes_on(&transaction, &tenant)? {
                // The reconstructed prefix already includes the saved HLC's
                // unsigned maximum. SQLite MAX would compare signed bits.
                transaction.execute(
                    "INSERT INTO origin_prefixes (tenant, origin, seq, hlc) VALUES (?1, ?2, ?3, ?4)
                     ON CONFLICT(tenant, origin) DO UPDATE SET
                     seq = excluded.seq, hlc = excluded.hlc",
                    rusqlite::params![tenant, entry.origin, entry.origin_sequence as i64, entry.hlc as i64],
                ).map_err(|_| ())?;
            }
        }
        // Record deletion evidence row by row without SQLite's unbounded
        // RETURNING buffer. The temporary trigger and its writes participate
        // in this transaction, including rollback on any failure.
        transaction.execute_batch(
            "CREATE TEMP TRIGGER pruning_watermark AFTER DELETE ON main.documents BEGIN
               INSERT INTO journal_pruning (tenant, pruned_through) VALUES (OLD.tenant, OLD.commit_seq)
               ON CONFLICT(tenant) DO UPDATE SET pruned_through = MAX(pruned_through, excluded.pruned_through);
             END;"
        ).map_err(|_| ())?;
        let pruned = transaction
            .execute(
                "DELETE FROM documents
                 WHERE commit_seq < ?1
                   AND commit_seq NOT IN (
                   SELECT pin.commit_seq FROM documents pin
                   WHERE pin.committed_at >= ?2 -
                     COALESCE((SELECT days FROM collection_history
                               WHERE collection = pin.collection), 30) * ?3
                   OR NOT EXISTS (
                     SELECT 1 FROM documents newer
                     WHERE newer.tenant = pin.tenant
                       AND newer.collection = pin.collection
                       AND newer.id = pin.id
                       AND newer.commit_seq > pin.commit_seq
                   )
                 )",
                rusqlite::params![floor as i64, now, 24_i64 * 60 * 60 * 1000],
            )
            .map_err(|_| ())?;
        transaction.execute_batch("DROP TRIGGER temp.pruning_watermark").map_err(|_| ())?;
        repair_summary::prune(&transaction, floor, now)?;
        accounting::settle_due(&transaction, accounting_now)?;
        storage_intervals::prune_retired(&transaction)?;
        transaction.commit().map_err(|_| ())?;
        u64::try_from(pruned).map_err(|_| ())
    }

    /// The minimum consumer frontier — the pruning floor — when any
    /// consumer has registered.
    fn history_floor(&self) -> Option<u64> {
        let reader = self.reader();
        history_floor_on(reader.connection()).ok().flatten()
    }

    /// The retained-commit count — the bounded-journal invariant's measure.
    #[cfg(test)]
    fn journal_records(&self) -> u64 {
        let reader = self.reader();
        let count = reader.connection().query_row(
            "SELECT COUNT(DISTINCT commit_seq) FROM documents",
            [],
            |row| row.get::<_, i64>(0),
        );
        u64::try_from(count.unwrap_or(0)).unwrap_or(0)
    }
}

impl CommitContext {
    /// The lock-held half of one batch: stage every intent, then commit.
    ///
    /// Any error path drops the transaction guard, whose rollback is the
    /// in-process stand-in for a crash between two durable steps: every
    /// staged row — documents, ledger, journal — vanishes together.
    #[allow(
        clippy::unused_self,
        reason = "self carries the test-only fault plan; production builds need no receiver state"
    )]
    fn commit_transaction(
        &self,
        writer: &mut Connection,
        intents: &[CommitIntent],
    ) -> Result<Vec<CommitResult>, ()> {
        // Direct metadata mutations require local sync too, so FULL is the
        // baseline. Only a wave containing no Synced request can lower it.
        // Restore FULL after success or rollback before another operation
        // obtains this writer connection.
        let written_only =
            !intents.iter().any(|intent| intent.durability == LocalDurability::Synced);
        if written_only {
            writer.execute_batch("PRAGMA synchronous = NORMAL").map_err(|_| ())?;
        }
        let result = self.commit_transaction_inner(writer, intents);
        if written_only {
            writer.execute_batch("PRAGMA synchronous = FULL").map_err(|_| ())?;
        }
        result
    }

    fn commit_transaction_inner(
        &self,
        writer: &mut Connection,
        intents: &[CommitIntent],
    ) -> Result<Vec<CommitResult>, ()> {
        let transaction =
            writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
        let accounting_now = accounting::begin_change(&transaction, unix_millis().max(0) as u64)?;
        let mut results: Vec<Option<CommitResult>> = intents.iter().map(|_| None).collect();
        let mut watermark = durable_watermark(&transaction)?;
        // The wave's journal floor: every sequence above it was allocated by
        // this wave, so the per-database metering flush below scans exactly
        // this wave's rows.
        let wave_base = watermark;
        let mut root_head: String = transaction
            .query_row("SELECT value FROM meta WHERE key = 'root_head'", [], |row| {
                row.get::<_, String>(0)
            })
            .map_err(|_| ())?;
        // The extracted-column view of this wave: one snapshot under the
        // wave's own transaction, so a registration that commits between
        // waves is picked up whole and never mid-wave.
        let (fields, put_sql, descriptors, fts_descriptors) = {
            let extracted =
                self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            let fts = self.fts.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            (
                extracted.fields.clone(),
                extracted.put_insert_sql.clone(),
                extracted.descriptors.clone(),
                fts.descriptors.clone(),
            )
        };
        for (index, intent) in intents.iter().enumerate() {
            results[index] = Some(stage_intent(
                &transaction,
                &mut watermark,
                &mut root_head,
                intent,
                &self.local_origin,
                &fields,
                &put_sql,
                &descriptors,
                &fts_descriptors,
            )?);
        }
        // The wave's usage counter flush: one write per wave, never per
        // op, riding the wave's own transaction so a rolled-back wave
        // counts nothing (D-42). ops counts committed publications only;
        // rejections bind identities but publish nothing.
        let committed_ops = results
            .iter()
            .filter(|result| matches!(result, Some(CommitResult::Committed(_))))
            .count();
        if committed_ops > 0 {
            transaction
                .prepare_cached(
                    "INSERT INTO usage_counters (key, seq_hi, ops) VALUES ('ops', ?1, ?2)
                     ON CONFLICT(key) DO UPDATE SET
                        seq_hi = excluded.seq_hi,
                        ops = usage_counters.ops + excluded.ops",
                )
                .and_then(|mut statement| {
                    statement.execute(rusqlite::params![watermark as i64, committed_ops as i64])
                })
                .map_err(|_| ())?;
        }
        // The wave's per-database metering flush (E-9, D-42): one rollup per
        // billed database riding the wave's own transaction, so a rolled-back
        // wave bills nothing and a retried operation identity — which replays
        // its receipt without writing a row — bills nothing twice.
        flush_metering_db_facts(&transaction, wave_base, intents, &results)?;
        let accounting_tenants: std::collections::BTreeSet<_> =
            intents.iter().map(|intent| intent.tenant.as_str()).collect();
        for tenant in accounting_tenants {
            accounting::settle(&transaction, tenant, accounting_now)?;
        }
        // The test fault aborts between the staged rows and the commit: the
        // guard's drop rolls the transaction back, which is exactly the
        // in-process stand-in for a crash between two durable steps.
        #[cfg(test)]
        {
            let mut faults = self.faults.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            if faults.fire(super::FaultPoint::WalAppend).is_err() {
                return Err(());
            }
        }
        transaction.commit().map_err(|_| ())?;
        Ok(results.into_iter().map(Option::unwrap).collect())
    }
}

/// Stages one intent inside the open transaction
/// `prepare_commit` order: validate, resolve a retried identity durably,
/// conflict-check, then write documents, ledger row, and journal row.
#[allow(
    clippy::too_many_arguments,
    clippy::too_many_lines,
    reason = "one intent's full publication order"
)]
fn stage_intent(
    transaction: &rusqlite::Transaction<'_>,
    watermark: &mut u64,
    root_head: &mut String,
    intent: &CommitIntent,
    local_origin: &str,
    extracted_fields: &[String],
    put_insert_sql: &str,
    descriptors: &std::collections::BTreeMap<(String, String), bool>,
    fts_descriptors: &std::collections::BTreeMap<(String, String), bool>,
) -> Result<CommitResult, ()> {
    if let Some(expected) = intent.expected_origin_epoch {
        let (epoch, closed) = origin_epoch_on(transaction)?;
        if closed || epoch != expected {
            return Ok(CommitResult::Rejected(CommitRejection::Conflict));
        }
    }
    // Step 1: validation never binds the identity.
    if (intent.writes.is_empty()
        && intent.metadata.is_empty()
        && intent.schema_envelopes.is_empty()
        && intent.replication_publication.is_none())
        || codec::staged_writes_have_duplicate_targets(&intent.writes)
        || !private_metadata::valid_metadata(&intent.metadata)
        || !private_metadata::valid_envelopes(&intent.schema_envelopes)
    {
        return Ok(CommitResult::Rejected(CommitRejection::Validation));
    }
    // An admitted mutation must be locally synced: its billable fact is
    // bound to this transaction's durable sequence. The admission identity
    // is also the operation identity, so retries can verify the exact fact
    // instead of minting a second one.
    let admitted_fact = if let Some(admission) = &intent.usage_admission {
        if intent.writes.is_empty()
            || intent.durability != LocalDurability::Synced
            || admission.request_id().as_str() != intent.operation.as_str()
        {
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
        let Ok(fact) = admission.fact_for(intent.tenant.clone()) else {
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        };
        Some(fact)
    } else {
        None
    };
    // The row identity and provenance a replicated intent carries: the
    // source region's commit identity rides the `commit_ulid` column.
    // Each replicated record binds its own operation identity separately
    // so records sharing a source transaction all publish.
    let (row_identity, mut provenance) = commit_identity_of(intent, local_origin)?;
    let origin_sequence = if provenance.kind() == JournalKind::Origin {
        let next = transaction
            .query_row(
                "SELECT COALESCE(MAX(seq), 0) + 1 FROM (
                    SELECT origin_seq AS seq FROM documents WHERE origin = ?1 AND tenant = ?2
                    UNION ALL SELECT seq FROM origin_prefixes WHERE origin = ?1 AND tenant = ?2
                    UNION ALL SELECT origin_seq FROM replication_publications WHERE origin = ?1 AND tenant = ?2)",
                rusqlite::params![provenance.origin(), intent.tenant.as_str()],
                |row| row.get::<_, i64>(0),
            )
            .map_err(|_| ())?;
        u64::try_from(next).map_err(|_| ())?
    } else {
        let sequence = provenance.origin_sequence();
        if sequence == 0 || sequence > i64::MAX as u64 {
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
        sequence
    };
    if provenance.kind() == JournalKind::Origin {
        provenance = JournalProvenance::new(
            provenance.kind(),
            provenance.origin().to_owned(),
            origin_sequence,
            provenance.hlc(),
        );
    }
    let binding_identity = if provenance.kind() == JournalKind::Replicated {
        &intent.operation
    } else {
        &row_identity
    };
    if let Some(fingerprint) = intent.replication_fingerprint
        && replication_fingerprint_on(transaction, &intent.tenant, binding_identity)?
            .is_some_and(|bound| bound != fingerprint)
    {
        return Ok(CommitResult::Rejected(CommitRejection::OperationIdReuse));
    }
    if replication_was_superseded_on(transaction, &intent.tenant, binding_identity)? {
        // A concurrent apply already stored a no-publication result. Retry
        // through the engine to resolve that result without staging a write.
        return Ok(CommitResult::Rejected(CommitRejection::Conflict));
    }
    // Step 2: a retried identity resolves from its folded rows (or its
    // rejection record, which always binds the caller's own identity).
    if let Some(entry) =
        operation_view(transaction, &intent.tenant, binding_identity, &intent.operation)?
    {
        if let Some(fingerprint) = intent.replication_fingerprint {
            if replication_fingerprint_on(transaction, &intent.tenant, binding_identity)?
                != Some(fingerprint)
            {
                return Ok(CommitResult::Rejected(CommitRejection::Storage));
            }
        } else if entry.digest != intent.digest {
            return Ok(CommitResult::Rejected(CommitRejection::OperationIdReuse));
        }
        if entry.rejection.is_none()
            && let Some(fact) = &admitted_fact
        {
            // A committed replay must find the exact durable fact this
            // identity minted, including its original commit sequence.
            let Some(expected) = DurableUsageFact::try_new(fact.clone(), entry.sequence, 1).ok()
            else {
                return Ok(CommitResult::Rejected(CommitRejection::Storage));
            };
            let minted = match usage_fact_body(transaction, expected.fact())? {
                Some(body) => {
                    DurableUsageFact::decode(&body).is_ok_and(|durable| durable == expected)
                }
                None => false,
            };
            if !minted {
                return Ok(CommitResult::Rejected(CommitRejection::Storage));
            }
        }
        if let Some(reason) = entry.rejection {
            // A durably rejected identity replays its rejection, never a
            // publication — the reference engines' retry rule.
            return Ok(CommitResult::Rejected(reason));
        }
        return Ok(CommitResult::Committed(StorageReceipt {
            sequence: StorageSequence::new(entry.sequence),
            root: StorageRootId::from_recorded(if entry.root.is_empty() {
                // Legacy rows predating operation facts cannot recover a
                // historical root; exposing the current head would falsely
                // claim an exact replay, so retain the explicit fail-closed
                // empty marker instead.
                String::new()
            } else {
                entry.root
            }),
            durability: entry.requested,
        }));
    }
    // The uniform-cut guard must run inside this exact writer transaction:
    // later intents in the same wave see earlier intents' new dependencies.
    // Replay above intentionally wins, so a committed operation never turns
    // into a failed precondition merely because later writes advanced context.
    if let Some(expected) = &intent.expected_uniform_context
        && context_frontier(transaction, &intent.tenant)? != *expected
    {
        // This admission guard publishes and binds nothing. The same logical
        // operation may retry after the caller establishes a new uniform cut.
        return Ok(CommitResult::Rejected(CommitRejection::Conflict));
    }
    // Step 3: conflict-check every write against the snapshot and the
    // published view. The published watermark already includes every
    // earlier intent of this batch.
    let snapshot_sequence = intent.snapshot_sequence.get();
    if let Some(group) = &intent.replication_publication {
        // Deletion preparation freezes the accounting cut for both origin
        // and incoming publications. Replay above remains a read-only receipt.
        if !lifecycle_state::permits_origin_activation(transaction)? {
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
        let available =
            crate::engine::merge::decode_context(&context_frontier(transaction, &intent.tenant)?)
                .ok_or(())?;
        let dependencies = crate::engine::merge::decode_context(&group.context).ok_or(())?;
        if dependencies.frontier().iter().any(|(origin, sequence)| {
            available.frontier().get(origin).copied().unwrap_or(0) < *sequence
        }) || available.frontier().get(&group.origin).copied().unwrap_or(0)
            < group.origin_sequence - 1
        {
            return Ok(CommitResult::Rejected(CommitRejection::Conflict));
        }
        for (collection, expected) in &group.schema_observations {
            let current: Option<String> = transaction
                .query_row(
                    "SELECT schema FROM merge_schemas WHERE collection = ?1",
                    [collection.as_str()],
                    |row| row.get(0),
                )
                .optional()
                .map_err(|_| ())?;
            if current != *expected {
                return Ok(CommitResult::Rejected(CommitRejection::Conflict));
            }
        }
        for (collection, id) in &group.observations {
            // Version numbers restart after deletion; compare publication
            // positions so tombstone and delete/recreate races are visible.
            let latest: Option<i64> = transaction.query_row(
                "SELECT MAX(commit_seq) FROM documents WHERE tenant = ?1 AND collection = ?2 AND id = ?3",
                rusqlite::params![intent.tenant.as_str(), collection.as_str(), id.as_str()],
                |row| row.get(0),
            ).map_err(|_| ())?;
            if latest.is_some_and(|sequence| sequence < 0 || sequence as u64 > snapshot_sequence) {
                return Ok(CommitResult::Rejected(CommitRejection::Conflict));
            }
        }
    }
    for write in &intent.writes {
        if let StagedWrite::Put { document, .. } | StagedWrite::ReplicatedPut { document, .. } =
            write
            && !codec::document_within_limits(document)
        {
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
        // Replicated writes check for changes since the merge snapshot,
        // allowing the caller to recompute after contention.
        let mismatch = match write {
            StagedWrite::Put { document, expected } => conflict_between(
                transaction,
                &intent.tenant,
                document.collection(),
                document.id(),
                *expected,
                snapshot_sequence,
                *watermark,
            )?,
            StagedWrite::ReplicatedPut { document, .. } => conflict_between(
                transaction,
                &intent.tenant,
                document.collection(),
                document.id(),
                crate::types::ExpectedVersion::Any,
                snapshot_sequence,
                *watermark,
            )?,
            StagedWrite::Delete { collection: Some(collection), id, expected } => conflict_between(
                transaction,
                &intent.tenant,
                collection,
                id,
                *expected,
                snapshot_sequence,
                *watermark,
            )?,
            StagedWrite::Delete { collection: None, id, expected } => conflict_for_absent_document(
                transaction,
                &intent.tenant,
                id,
                *expected,
                snapshot_sequence,
                *watermark,
            )?,
            StagedWrite::ReplicatedDelete { collection, id, .. } => conflict_between(
                transaction,
                &intent.tenant,
                collection,
                id,
                crate::types::ExpectedVersion::Any,
                snapshot_sequence,
                *watermark,
            )?,
        };
        if mismatch {
            // Replication recomputes from a newer snapshot after contention.
            // Binding this transient refusal would permanently reject its source dot.
            if provenance.kind() == JournalKind::Replicated {
                return Ok(CommitResult::Rejected(CommitRejection::Conflict));
            }
            // A conflict is recorded durably — the binding that makes a
            // retried identity replay its rejection — but consumes no
            // sequence and writes no journal row: the FileStorage rule, so
            // published sequences stay dense and the journal high-water
            // mark never trails the watermark behind a non-publishing
            // record. Riding this batch's transaction means a later batch
            // failure drops the record too; the conflict check is
            // deterministic against the same rolled-back state, so a
            // replay converges to the same rejection either way.
            insert_rejection_row(
                transaction,
                &intent.tenant,
                &intent.operation,
                intent.digest,
                CommitRejection::Conflict,
            )?;
            return Ok(CommitResult::Rejected(CommitRejection::Conflict));
        }
    }
    if intent.replication_publication.is_none()
        && private_metadata::metadata_conflicts(
            transaction,
            &intent.tenant,
            &intent.metadata,
            snapshot_sequence,
        )?
    {
        insert_rejection_row(
            transaction,
            &intent.tenant,
            &intent.operation,
            intent.digest,
            CommitRejection::Conflict,
        )?;
        return Ok(CommitResult::Rejected(CommitRejection::Conflict));
    }
    for write in &intent.writes {
        if let StagedWrite::Put { document, .. } = write {
            let validation = crate::families::schemas::enforce_value(
                document.collection(),
                document.value(),
                |key| {
                    if let Some(staged) =
                        intent.metadata.iter().rev().find(|change| change.key == key)
                    {
                        return Ok(staged.value.clone());
                    }
                    private_metadata::read_private_metadata_on(
                        transaction,
                        &intent.tenant,
                        key,
                        *watermark,
                    )
                    .map_err(|()| crate::engine::EngineError::StorageFailure)
                },
            );
            if let Err(error) = validation {
                return Ok(CommitResult::Rejected(
                    if matches!(error, crate::engine::EngineError::InvalidDocument) {
                        CommitRejection::Validation
                    } else {
                        CommitRejection::Storage
                    },
                ));
            }
        }
    }
    if intent.replication_publication.is_none()
        && private_metadata::envelopes_conflict(transaction, &intent.schema_envelopes)?
    {
        insert_rejection_row(
            transaction,
            &intent.tenant,
            &intent.operation,
            intent.digest,
            CommitRejection::Conflict,
        )?;
        return Ok(CommitResult::Rejected(CommitRejection::Conflict));
    }
    transaction.execute_batch("SAVEPOINT schema_unique_admission").map_err(|_| ())?;
    if schema_bundles::changed_since_snapshot(transaction, &intent.tenant, intent)? {
        transaction
            .execute_batch("ROLLBACK TO schema_unique_admission; RELEASE schema_unique_admission")
            .map_err(|_| ())?;
        insert_rejection_row(
            transaction,
            &intent.tenant,
            &intent.operation,
            intent.digest,
            CommitRejection::Conflict,
        )?;
        return Ok(CommitResult::Rejected(CommitRejection::Conflict));
    }
    // Step 4: allocate the sequence past everything this location has named.
    let Some(sequence) = watermark.checked_add(1) else {
        return Err(());
    };
    *watermark = sequence;
    let root = chained_root(root_head, sequence, intent.digest);
    // Mint admission accounting inside the same transaction as the folded
    // document rows and operation fact. A rollback therefore drops both the
    // billable evidence and the mutation; identity reuse is refused before
    // any row is written.
    if let Some(fact) = &admitted_fact {
        if usage_fact_body(transaction, fact)?.is_some() {
            transaction.execute_batch("RELEASE schema_unique_admission").map_err(|_| ())?;
            *watermark = sequence - 1;
            return Ok(CommitResult::Rejected(CommitRejection::OperationIdReuse));
        }
        usage_seal::ensure_unsealed(transaction, fact)?;
        let Some(durable) = DurableUsageFact::try_new(fact.clone(), sequence, 1).ok() else {
            transaction.execute_batch("RELEASE schema_unique_admission").map_err(|_| ())?;
            *watermark = sequence - 1;
            return Ok(CommitResult::Rejected(CommitRejection::Storage));
        };
        let scope = durable.fact().scope();
        let identity = durable.fact().identity();
        transaction
            .execute(
                "INSERT INTO usage_facts
                 (tenant, project, database, partition, region, rule, fact_id,
                  seq, start_millis, end_millis, body)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
                rusqlite::params![
                    scope.tenant_id().as_str(),
                    scope.project_id().as_str(),
                    scope.database_id().as_str(),
                    scope.partition_id().as_str(),
                    scope.region_id().as_str(),
                    minting_rule_code(identity.rule()),
                    identity.id().as_str(),
                    sequence as i64,
                    durable.fact().window().start_millis() as i64,
                    durable.fact().window().end_millis() as i64,
                    durable.encoded(),
                ],
            )
            .map_err(|_| ())?;
    }
    if let Some(allocation) = &intent.budget_allocation {
        let now_millis = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |d| d.as_millis().try_into().unwrap_or(u64::MAX));
        if !allocation.valid_at(now_millis) {
            transaction
                .execute_batch(
                    "ROLLBACK TO schema_unique_admission; RELEASE schema_unique_admission",
                )
                .map_err(|_| ())?;
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
        if allocation.grant().operation_id != intent.operation.as_str() {
            transaction
                .execute_batch(
                    "ROLLBACK TO schema_unique_admission; RELEASE schema_unique_admission",
                )
                .map_err(|_| ())?;
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
        let Some(admission) = intent.usage_admission.as_ref() else {
            transaction
                .execute_batch(
                    "ROLLBACK TO schema_unique_admission; RELEASE schema_unique_admission",
                )
                .map_err(|_| ())?;
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        };
        let units = i64::try_from(admission.integer_units()).map_err(|_| ())?;
        if budget::consume(transaction, &intent.tenant, allocation, units).is_err() {
            transaction
                .execute_batch(
                    "ROLLBACK TO schema_unique_admission; RELEASE schema_unique_admission",
                )
                .map_err(|_| ())?;
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
    }
    // Step 5: documents rows ride one transaction, and every row of the
    // commit carries the commit's own identity — the journal is the table.
    // A replicated commit carries the source region's identity and its
    // provenance stamp on every row.
    let context = intent
        .replication_publication
        .as_ref()
        .map(|group| group.context.clone())
        .or_else(|| {
            intent.writes.iter().find_map(|write| match write {
                StagedWrite::ReplicatedPut { context, .. }
                | StagedWrite::ReplicatedDelete { context, .. }
                    if !context.is_empty() =>
                {
                    Some(context.clone())
                }
                _ => None,
            })
        })
        .unwrap_or(context_frontier(transaction, &intent.tenant)?);
    provenance = provenance.with_context(context.clone());
    let commit = FoldedCommit {
        operation: &row_identity,
        binding_operation: binding_identity,
        requested: intent.durability,
        digest: intent.digest,
        kind: provenance.kind(),
        origin: provenance.origin(),
        origin_sequence,
        hlc: provenance.hlc(),
        context,
    };
    let mut published_writes: Vec<PublishedWrite> = Vec::with_capacity(intent.writes.len());
    private_metadata::stage_controls(transaction, intent, &provenance, sequence)?;
    replication_outbox::persist_publication(
        transaction,
        intent,
        &provenance,
        &row_identity,
        sequence,
    )?;
    let mut billed_writes = 0_u64;
    let mut billed_bytes = 0_u64;
    for write in &intent.writes {
        let row_commit = match write {
            StagedWrite::ReplicatedPut { hlc, origin, origin_sequence, context, .. }
            | StagedWrite::ReplicatedDelete { hlc, origin, origin_sequence, context, .. } => {
                FoldedCommit {
                    operation: commit.operation,
                    binding_operation: commit.binding_operation,
                    requested: commit.requested,
                    digest: commit.digest,
                    kind: commit.kind,
                    origin,
                    origin_sequence: *origin_sequence,
                    hlc: *hlc,
                    context: context.clone(),
                }
            }
            _ => FoldedCommit { context: commit.context.clone(), ..commit },
        };
        let commit = row_commit;
        match write {
            StagedWrite::Put { document, .. } | StagedWrite::ReplicatedPut { document, .. } => {
                let version = version_at(
                    transaction,
                    &intent.tenant,
                    document.collection(),
                    document.id(),
                    sequence - 1,
                )?
                .map_or(1, |prior| prior.saturating_add(1));
                let mut published = document.clone();
                published.set_version(version);
                let body = encode_document_body(&published);
                insert_put_row(
                    transaction,
                    &intent.tenant,
                    &published,
                    sequence,
                    version,
                    &body,
                    &commit,
                    extracted_fields,
                    put_insert_sql,
                    descriptors,
                    fts_descriptors,
                )?;
                let body_len = body.len() as u64;
                billed_writes = billed_writes.saturating_add(billed_writes_for_bytes(body_len));
                billed_bytes = billed_bytes.saturating_add(body_len);
                published_writes.push(PublishedWrite::Put { document: published });
            }
            StagedWrite::Delete { collection, id, .. } => {
                billed_writes = billed_writes.saturating_add(1);
                let version_at_delete = match collection {
                    Some(collection) => {
                        version_at(transaction, &intent.tenant, collection, id, sequence - 1)?
                            .unwrap_or(0)
                    }
                    None => 0,
                };
                match collection {
                    Some(collection) => {
                        insert_document_row(
                            transaction,
                            &intent.tenant,
                            collection,
                            id,
                            sequence,
                            version_at_delete,
                            &commit,
                            fts_descriptors,
                        )?;
                    }
                    None => {
                        // A collection-less delete removes whichever copy
                        // canonical key order puts first, if any.
                        if let Some(existing) =
                            first_collection_of_id(transaction, &intent.tenant, id, sequence - 1)?
                        {
                            insert_document_row(
                                transaction,
                                &intent.tenant,
                                &existing,
                                id,
                                sequence,
                                0,
                                &commit,
                                fts_descriptors,
                            )?;
                        }
                    }
                }
                published_writes.push(PublishedWrite::Delete {
                    collection: collection.clone(),
                    id: id.clone(),
                    version_at_delete,
                });
            }
            StagedWrite::ReplicatedDelete { collection, id, .. } => {
                billed_writes = billed_writes.saturating_add(1);
                let version_at_delete =
                    version_at(transaction, &intent.tenant, collection, id, sequence - 1)?
                        .unwrap_or(0);
                insert_document_row(
                    transaction,
                    &intent.tenant,
                    collection,
                    id,
                    sequence,
                    version_at_delete,
                    &commit,
                    fts_descriptors,
                )?;
                published_writes.push(PublishedWrite::Delete {
                    collection: Some(collection.clone()),
                    id: id.clone(),
                    version_at_delete,
                });
            }
        }
    }
    if provenance.kind() != JournalKind::Replicated
        && !schema_indexes::validate_local_writes(transaction, &intent.tenant, sequence)?
    {
        transaction
            .execute_batch("ROLLBACK TO schema_unique_admission; RELEASE schema_unique_admission")
            .map_err(|_| ())?;
        *watermark = sequence - 1;
        insert_rejection_row(
            transaction,
            &intent.tenant,
            &intent.operation,
            intent.digest,
            CommitRejection::Conflict,
        )?;
        return Ok(CommitResult::Rejected(CommitRejection::Conflict));
    }
    transaction.execute_batch("RELEASE schema_unique_admission").map_err(|_| ())?;
    if let Some(fingerprint) = intent.replication_fingerprint {
        transaction.execute(
            "INSERT INTO replication_bindings (tenant, operation, fingerprint) VALUES (?1, ?2, ?3)",
            rusqlite::params![intent.tenant.as_str(), binding_identity.as_str(), fingerprint.as_slice()],
        ).map_err(|_| ())?;
    }
    if let Some(group) = &intent.replication_publication {
        for (collection, id, conflict) in &group.conflicts {
            persist_merge_conflict(transaction, &intent.tenant, collection, id, conflict)?;
        }
    }
    if let Some((collection, id, conflict)) = &intent.merge_conflict {
        persist_merge_conflict(transaction, &intent.tenant, collection, id, conflict)?;
    }
    if intent.internal_maintenance || provenance.kind() == JournalKind::Replicated {
        billed_writes = 0;
        billed_bytes = 0;
    }
    let marker_kind = if provenance.kind() == JournalKind::Replicated {
        Some("replication")
    } else if intent.internal_maintenance {
        Some("internal_maintenance")
    } else if intent.writes.is_empty() {
        Some("metadata_only")
    } else {
        None
    };
    if let Some(kind) = marker_kind {
        transaction
            .execute(
                "INSERT INTO operation_kind_markers(tenant,operation,kind) VALUES(?1,?2,?3)",
                rusqlite::params![intent.tenant.as_str(), binding_identity.as_str(), kind],
            )
            .map_err(|_| ())?;
    }
    write_usage::mint(transaction, intent, sequence, billed_writes, &provenance)?;
    let published = PublishedCommit::new(
        intent.tenant.clone(),
        intent.operation.clone(),
        StorageSequence::new(sequence),
        intent.durability,
        provenance,
        published_writes,
    );
    let _ = &published;
    insert_operation_fact(
        transaction,
        &intent.tenant,
        binding_identity,
        intent.digest,
        true,
        CommitRejection::Storage,
        sequence,
        intent.durability,
        root.as_str(),
        billed_writes,
        billed_bytes,
    )?;
    update_root_head(transaction, root.as_str())?;
    root.clone_into(root_head);
    Ok(CommitResult::Committed(StorageReceipt {
        sequence: StorageSequence::new(sequence),
        root: StorageRootId::from_recorded(root),
        durability: intent.durability,
    }))
}

/// Persists one operation's complete replay and billing facts in the same
/// transaction as its folded rows.  `rejection` is ignored for committed
/// facts but keeps the call shape shared with rejection binding.
fn insert_operation_fact(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    operation: &OperationId,
    digest: u64,
    committed: bool,
    rejection: CommitRejection,
    sequence: u64,
    requested: LocalDurability,
    root: &str,
    writes: u64,
    bytes: u64,
) -> Result<(), ()> {
    transaction
        .prepare_cached(
            "INSERT INTO operation_facts
             (tenant, client_request_ulid, digest, committed, rejection, sequence, requested, root, writes, bytes)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![
                tenant.as_str(),
                operation.as_str(),
                digest as i64,
                i64::from(committed),
                codec::rejection_code(rejection),
                sequence as i64,
                codec::durability_code(requested),
                root,
                writes as i64,
                bytes as i64,
            ])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// The durable resolution of one operation identity: a committed commit
/// resolves from its folded rows; a rejection resolves from the rejections
/// table.
struct OperationView {
    digest: u64,
    sequence: u64,
    requested: LocalDurability,
    rejection: Option<CommitRejection>,
    root: String,
}

/// Reads one operation's durable resolution from the folded rows and the
/// rejections table. The folded rows resolve by the identity the rows carry
/// — this location's own operation for an origin commit, the source
/// region's commit identity for a replicated one — while the rejection
/// record always binds the caller's own operation identity.
fn operation_view(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    row_operation: &OperationId,
    ledger_operation: &OperationId,
) -> Result<Option<OperationView>, ()> {
    // Operation facts are authoritative: unlike folded rows they also cover
    // no-row deletes and retain the original root for byte-for-byte receipt
    // replay after later commits or a process restart.
    match transaction.query_row(
        "SELECT digest, committed, rejection, sequence, requested, root
         FROM operation_facts
         WHERE tenant = ?1 AND (client_request_ulid = ?2 OR client_request_ulid = ?3)
         ORDER BY CASE WHEN client_request_ulid = ?2 THEN 0 ELSE 1 END
         LIMIT 1",
        rusqlite::params![tenant.as_str(), row_operation.as_str(), ledger_operation.as_str()],
        |row| {
            Ok((
                row.get::<_, i64>(0)?,
                row.get::<_, i64>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, i64>(4)?,
                row.get::<_, String>(5)?,
            ))
        },
    ) {
        Ok((digest, committed, code, sequence, requested, root)) => {
            let rejection = if committed == 0 {
                codec::decode_rejection(u8::try_from(code).map_err(|_| ())?).ok_or(())?
            } else if committed == 1 {
                // A committed fact must never carry a rejection code.
                return Ok(Some(OperationView {
                    #[allow(clippy::cast_sign_loss)]
                    digest: digest as u64,
                    sequence: {
                        let sequence = u64::try_from(sequence).map_err(|_| ())?;
                        if sequence == 0 {
                            return Err(());
                        }
                        sequence
                    },
                    requested: codec::durability_of(u8::try_from(requested).map_err(|_| ())?)
                        .ok_or(())?,
                    rejection: None,
                    root,
                }));
            } else {
                return Err(());
            };
            return Ok(Some(OperationView {
                #[allow(clippy::cast_sign_loss)]
                digest: digest as u64,
                sequence: 0,
                requested: LocalDurability::Written,
                rejection: Some(rejection),
                root,
            }));
        }
        Err(rusqlite::Error::QueryReturnedNoRows) => {}
        Err(_) => return Err(()),
    }
    if let Ok(row) = transaction.query_row(
        "SELECT digest, MIN(commit_seq), MIN(requested) FROM documents
         WHERE tenant = ?1 AND commit_ulid = ?2",
        rusqlite::params![tenant.as_str(), row_operation.as_str()],
        |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?, row.get::<_, i64>(2)?)),
    ) {
        let (digest, sequence, requested) = row;
        return Ok(Some(OperationView {
            #[allow(clippy::cast_sign_loss)]
            digest: digest as u64,
            sequence: u64::try_from(sequence).map_err(|_| ())?,
            requested: codec::durability_of(u8::try_from(requested).map_err(|_| ())?).ok_or(())?,
            rejection: None,
            root: String::new(),
        }));
    }
    let rejection = match transaction.query_row(
        "SELECT digest, rejection FROM rejections WHERE tenant = ?1 AND operation = ?2",
        rusqlite::params![tenant.as_str(), ledger_operation.as_str()],
        |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?)),
    ) {
        Ok(row) => row,
        // No folded rows and no rejection record: the identity is unknown.
        Err(rusqlite::Error::QueryReturnedNoRows) => return Ok(None),
        Err(_) => return Err(()),
    };
    let (digest, code) = rejection;
    let rejection = match u8::try_from(code) {
        Ok(code) if code > 0 => codec::decode_rejection(code),
        _ => None,
    };
    Ok(Some(OperationView {
        #[allow(clippy::cast_sign_loss)]
        digest: digest as u64,
        sequence: 0,
        requested: LocalDurability::Written,
        rejection,
        root: String::new(),
    }))
}

/// Derives the row identity and provenance one intent's rows carry: the
/// first replicated write names the source region's commit identity, and
/// an intent without replicated writes is an origin commit.
fn commit_identity_of(
    intent: &CommitIntent,
    local_origin: &str,
) -> Result<(OperationId, JournalProvenance), ()> {
    if let Some(group) = &intent.replication_publication {
        return Ok((
            OperationId::try_new(&group.commit_ulid).map_err(|_| ())?,
            JournalProvenance::new(
                JournalKind::Replicated,
                group.origin.clone(),
                group.origin_sequence,
                group.hlc,
            ),
        ));
    }
    for write in &intent.writes {
        match write {
            StagedWrite::ReplicatedPut { commit_ulid, hlc, origin, origin_sequence, .. }
            | StagedWrite::ReplicatedDelete { commit_ulid, hlc, origin, origin_sequence, .. } => {
                let identity = OperationId::try_new(commit_ulid).map_err(|_| ())?;
                return Ok((
                    identity,
                    JournalProvenance::new(
                        JournalKind::Replicated,
                        origin.clone(),
                        *origin_sequence,
                        *hlc,
                    ),
                ));
            }
            _ => {}
        }
    }
    Ok((
        intent.operation.clone(),
        JournalProvenance::new(JournalKind::Origin, local_origin.to_owned(), 0, intent.hlc),
    ))
}

/// Writes one rejection record: the durable binding that makes a retried
/// identity replay its rejection.
fn insert_rejection_row(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    operation: &OperationId,
    digest: u64,
    rejection: CommitRejection,
) -> Result<(), ()> {
    transaction
        .prepare_cached(
            "INSERT INTO rejections (tenant, operation, digest, rejection) VALUES (?1, ?2, ?3, ?4)",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![
                tenant.as_str(),
                operation.as_str(),
                digest as i64,
                codec::rejection_code(rejection),
            ])
        })
        .map(|_| ())
        .map_err(|_| ())?;
    insert_operation_fact(
        transaction,
        tenant,
        operation,
        digest,
        false,
        rejection,
        0,
        LocalDurability::Written,
        "",
        0,
        0,
    )
}

/// Writes one document put row with its extracted index columns (D-42):
/// the values the Rust write path extracted from the one write-time parse,
/// in the same statement as the body — index maintenance in the same
/// transaction is the zero-stale-reads rule. The TTL expiry rides the same
/// statement, and the full-text term streams ride the same transaction
/// through [`maintain_fts_rows`].
#[allow(clippy::too_many_arguments, reason = "the folded row model's columns")]
fn insert_put_row(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    document: &Document,
    sequence: u64,
    version: u64,
    body: &[u8],
    commit: &FoldedCommit<'_>,
    extracted_fields: &[String],
    put_insert_sql: &str,
    descriptors: &std::collections::BTreeMap<(String, String), bool>,
    fts_descriptors: &std::collections::BTreeMap<(String, String), bool>,
) -> Result<(), ()> {
    let committed_at = unix_millis();
    let mut parameters: Vec<Value> = Vec::with_capacity(18 + 2 * extracted_fields.len());
    parameters.push(Value::Text(tenant.as_str().to_owned()));
    parameters.push(Value::Text(document.collection().as_str().to_owned()));
    parameters.push(Value::Text(document.id().as_str().to_owned()));
    parameters.push(Value::Integer(sequence as i64));
    parameters.push(Value::Text(commit.operation.as_str().to_owned()));
    parameters.push(Value::Text(revision_string(sequence, version)));
    parameters.push(Value::Integer(i64::from(codec::durability_code(commit.requested))));
    parameters.push(Value::Integer(commit.digest as i64));
    parameters.push(Value::Integer(version as i64));
    parameters.push(Value::Integer(0));
    parameters.push(Value::Integer(committed_at));
    parameters.push(Value::Blob(body.to_vec()));
    parameters.push(Value::Text(commit.kind_text().to_owned()));
    parameters.push(Value::Text(commit.origin.to_owned()));
    parameters.push(Value::Integer(commit.origin_sequence as i64));
    parameters.push(Value::Integer(commit.hlc as i64));
    parameters.push(Value::Blob(commit.context.clone()));
    let ttl_field: Option<Option<String>> = transaction
        .query_row(
            "SELECT field FROM schema_ttl WHERE collection = ?1",
            [document.collection().as_str()],
            |row| row.get(0),
        )
        .optional()
        .map_err(|_| ())?;
    let ttl_field = ttl_field.unwrap_or_else(|| Some("expires_at".to_owned()));
    parameters.push(match extract_expires_at(document, ttl_field.as_deref()) {
        Some(millis) => Value::Integer(millis),
        None => Value::Null,
    });
    parameters.push(Value::Text(commit.binding_operation.as_str().to_owned()));
    // One extracted scalar per registered field of this row's collection;
    // every other column binds NULL — the same shape a backfilled row
    // carries. Building descriptors extract too: every row the write path
    // touches after registration carries its values, so the backfill's
    // remaining work only ever shrinks.
    for field in extracted_fields {
        let registered =
            descriptors.contains_key(&(document.collection().as_str().to_owned(), field.clone()));
        let (text_value, number_value) =
            match registered.then(|| extract_column_value(document, field)).flatten() {
                Some(ColumnValue::Text(text)) => (Value::Text(text), Value::Null),
                Some(ColumnValue::Typed(bytes)) => (Value::Blob(bytes), Value::Null),
                Some(ColumnValue::Integer(integer)) => (Value::Null, Value::Integer(integer)),
                Some(ColumnValue::Real(real)) => (Value::Null, Value::Real(real)),
                None => (Value::Null, Value::Null),
            };
        parameters.push(text_value);
        parameters.push(number_value);
    }
    transaction
        .prepare_cached(put_insert_sql)
        .and_then(|mut statement| statement.execute(rusqlite::params_from_iter(parameters.iter())))
        .map(|_| ())
        .map_err(|_| ())?;
    refresh_merge_kernels(transaction, tenant, document, commit)?;
    maintain_fts_rows(transaction, tenant, document, fts_descriptors)
}

fn persist_merge_conflict(
    connection: &Connection,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    conflict: &MergeConflictEntry,
) -> Result<(), ()> {
    connection
        .prepare_cached(
            "INSERT INTO merge_conflicts
                 (tenant, collection, id, loser_commit_ulid, loser_hlc, loser_origin,
                  loser_deleted, winner_commit_ulid, winner_hlc, winner_origin,
                  policy, explanation)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)
                 ON CONFLICT(tenant, collection, id, loser_commit_ulid) DO UPDATE SET
                  loser_hlc = excluded.loser_hlc, loser_origin = excluded.loser_origin,
                  loser_deleted = excluded.loser_deleted,
                  winner_commit_ulid = excluded.winner_commit_ulid,
                  winner_hlc = excluded.winner_hlc, winner_origin = excluded.winner_origin,
                  policy = excluded.policy, explanation = excluded.explanation",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![
                tenant.as_str(),
                collection.as_str(),
                id.as_str(),
                conflict.loser_commit_ulid,
                conflict.loser_hlc as i64,
                conflict.loser_origin,
                i64::from(conflict.loser_deleted),
                conflict.winner_commit_ulid,
                conflict.winner_hlc as i64,
                conflict.winner_origin,
                conflict.policy,
                conflict.explanation,
            ])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// Updates declared field metadata in the same transaction as its document.
fn refresh_merge_kernels(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    document: &Document,
    commit: &FoldedCommit<'_>,
) -> Result<(), ()> {
    let encoded: Option<String> = transaction
        .prepare_cached("SELECT schema FROM merge_schemas WHERE collection = ?1")
        .and_then(|mut statement| {
            statement.query_row([document.collection().as_str()], |row| row.get(0))
        })
        .optional()
        .map_err(|_| ())?;
    let Some(encoded) = encoded else { return Ok(()) };
    let schema = MergeSchema::parse_canonical(&encoded).map_err(|_| ())?;
    clear_merge_kernels(transaction, tenant, document.collection(), document.id())?;
    let DocumentValue::Object(fields) = document.value() else { return Ok(()) };
    let mut statement = transaction
        .prepare_cached(
            "INSERT INTO merge_kernels (tenant, collection, id, field, hlc, origin)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        )
        .map_err(|_| ())?;
    for field in schema.fields().keys().filter(|field| fields.contains_key(*field)) {
        statement
            .execute(rusqlite::params![
                tenant.as_str(),
                document.collection().as_str(),
                document.id().as_str(),
                field,
                commit.hlc as i64,
                commit.origin
            ])
            .map_err(|_| ())?;
    }
    Ok(())
}

fn clear_merge_kernels(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<(), ()> {
    transaction
        .prepare_cached(
            "DELETE FROM merge_kernels WHERE tenant = ?1 AND collection = ?2 AND id = ?3",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str()])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// Writes one tombstone row (empty body, no extracted columns), stamped
/// with the commit's provenance the same way a put row is. The key's
/// full-text rows leave with it, in the same transaction.
#[allow(clippy::too_many_arguments, reason = "the folded row model's columns")]
fn insert_document_row(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    sequence: u64,
    version: u64,
    commit: &FoldedCommit<'_>,
    fts_descriptors: &std::collections::BTreeMap<(String, String), bool>,
) -> Result<(), ()> {
    let committed_at = unix_millis();
    transaction
        .prepare_cached(
            "INSERT INTO documents
             (tenant, collection, id, commit_seq, commit_ulid, revision, requested,
              digest, version, deleted, committed_at, body, kind, origin, origin_seq, hlc, context,
              client_request_ulid)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18)",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![
                tenant.as_str(),
                collection.as_str(),
                id.as_str(),
                sequence as i64,
                commit.operation.as_str(),
                revision_string(sequence, version),
                codec::durability_code(commit.requested),
                commit.digest as i64,
                version as i64,
                1,
                committed_at,
                Vec::<u8>::new(),
                commit.kind_text(),
                commit.origin,
                commit.origin_sequence as i64,
                commit.hlc as i64,
                commit.context.as_slice(),
                commit.binding_operation.as_str(),
            ])
        })
        .map(|_| ())
        .map_err(|_| ())?;
    clear_merge_kernels(transaction, tenant, collection, id)?;
    clear_fts_rows(transaction, tenant, collection.as_str(), id.as_str(), fts_descriptors)
}

/// Installs the indexed FTS key map atomically, retaining legacy FTS row ids.
fn migrate_fts_keys(writer: &mut Connection) -> Result<(), ()> {
    let transaction = writer.transaction().map_err(|_| ())?;
    let exists: bool = transaction
        .query_row(
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fts_keys')",
            [],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    if !exists {
        transaction
            .execute_batch(
                "CREATE TABLE fts_keys (
                row_id INTEGER PRIMARY KEY,
                tenant TEXT NOT NULL,
                collection TEXT NOT NULL,
                field TEXT NOT NULL,
                doc_id TEXT NOT NULL,
                UNIQUE (tenant, collection, field, doc_id)
             );
             INSERT INTO fts_keys (row_id, tenant, collection, field, doc_id)
                SELECT rowid, tenant, collection, field, doc_id FROM fts_docs;
             CREATE TRIGGER fts_keys_delete AFTER DELETE ON fts_keys BEGIN
                DELETE FROM fts_docs WHERE rowid = OLD.row_id;
             END;",
            )
            .map_err(|_| ())?;
    }
    transaction.commit().map_err(|_| ())
}

/// Adds the key and its term stream in the caller's document transaction.
fn insert_fts_row(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &str,
    collection: &str,
    field: &str,
    id: &str,
    text: &str,
) -> Result<(), ()> {
    let row_id: i64 = transaction
        .prepare_cached(
            "INSERT INTO fts_keys (tenant, collection, field, doc_id)
             VALUES (?1, ?2, ?3, ?4) RETURNING row_id",
        )
        .and_then(|mut statement| {
            statement.query_row(rusqlite::params![tenant, collection, field, id], |row| row.get(0))
        })
        .map_err(|_| ())?;
    transaction
        .prepare_cached(
            "INSERT INTO fts_docs (rowid, tenant, collection, field, doc_id, text)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![row_id, tenant, collection, field, id, text])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// Maintains one put document's full-text rows in the committing
/// transaction: every text descriptor of the document's collection loses
/// its old row for this key and gains the analyzed term stream of the new
/// body. Building descriptors maintain too, so the backfill's remaining
/// work only ever shrinks — the same rule the value columns follow.
fn maintain_fts_rows(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    document: &Document,
    fts_descriptors: &std::collections::BTreeMap<(String, String), bool>,
) -> Result<(), ()> {
    let collection = document.collection().as_str();
    let id = document.id().as_str();
    let mut fields: Vec<&String> = Vec::new();
    for (owner, field) in fts_descriptors.keys() {
        if owner == collection && !fields.contains(&field) {
            fields.push(field);
        }
    }
    for field in fields {
        transaction
            .prepare_cached(
                "DELETE FROM fts_keys WHERE tenant = ?1 AND collection = ?2 AND field = ?3 AND doc_id = ?4",
            )
            .and_then(|mut statement| {
                statement.execute(rusqlite::params![tenant.as_str(), collection, field, id])
            })
            .map(|_| ())
            .map_err(|_| ())?;
        if let Some(text) = extract_fts_text(document, field) {
            insert_fts_row(transaction, tenant.as_str(), collection, field, id, &text)?;
        }
    }
    Ok(())
}

/// Clears one key's full-text rows in the committing transaction: the
/// tombstone half of [`maintain_fts_rows`].
fn clear_fts_rows(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    collection: &str,
    id: &str,
    fts_descriptors: &std::collections::BTreeMap<(String, String), bool>,
) -> Result<(), ()> {
    let mut fields: Vec<&String> = Vec::new();
    for (owner, field) in fts_descriptors.keys() {
        if owner == collection && !fields.contains(&field) {
            fields.push(field);
        }
    }
    for field in fields {
        transaction
            .prepare_cached(
                "DELETE FROM fts_keys WHERE tenant = ?1 AND collection = ?2 AND field = ?3 AND doc_id = ?4",
            )
            .and_then(|mut statement| {
                statement.execute(rusqlite::params![tenant.as_str(), collection, field, id])
            })
            .map(|_| ())
            .map_err(|_| ())?;
    }
    Ok(())
}

/// The per-commit facts every row of one commit carries (the D-42 fold):
/// its identity, durability, digest, and replication provenance (D-35).
struct FoldedCommit<'a> {
    operation: &'a OperationId,
    binding_operation: &'a OperationId,
    requested: LocalDurability,
    digest: u64,
    kind: JournalKind,
    origin: &'a str,
    origin_sequence: u64,
    hlc: u64,
    /// Canonical dotted causal frontier observed before this commit.
    context: Vec<u8>,
}

impl FoldedCommit<'_> {
    /// The stored `kind` text of this commit's rows.
    fn kind_text(&self) -> &'static str {
        match self.kind {
            JournalKind::Origin => ROW_KIND_ORIGIN,
            JournalKind::Replicated => ROW_KIND_REPLICATED,
        }
    }
}

/// Combines durable prefixes saved before pruning with contiguous retained
/// rows. A gap stops advancement for its origin, including after a restart.
fn origin_prefixes_on(
    connection: &Connection,
    tenant: &str,
) -> Result<Vec<AppliedFrontierEntry>, ()> {
    let mut prefixes = std::collections::BTreeMap::<String, AppliedFrontierEntry>::new();
    let mut saved = connection
        .prepare("SELECT origin, seq, hlc FROM origin_prefixes WHERE tenant = ?1")
        .map_err(|_| ())?;
    let mut rows = saved.query([tenant]).map_err(|_| ())?;
    while let Some(row) = rows.next().map_err(|_| ())? {
        let origin: String = row.get(0).map_err(|_| ())?;
        let seq: i64 = row.get(1).map_err(|_| ())?;
        let hlc: i64 = row.get(2).map_err(|_| ())?;
        if origin.is_empty() || seq <= 0 {
            return Err(());
        }
        prefixes.insert(
            origin.clone(),
            AppliedFrontierEntry {
                origin,
                origin_sequence: u64::try_from(seq).map_err(|_| ())?,
                hlc: u64::from_le_bytes(hlc.to_le_bytes()),
            },
        );
    }
    // HLC uses all 64 bits. Negative SQLite integers represent the upper
    // unsigned half, so choose that half before comparing within it.
    let mut statement = connection
        .prepare(
            "SELECT origin, origin_seq,
             COALESCE(MAX(CASE WHEN hlc < 0 THEN hlc END), MAX(hlc)) FROM (
             SELECT origin, origin_seq, hlc FROM documents WHERE tenant = ?1
             UNION ALL SELECT origin, origin_seq, hlc FROM replication_publications WHERE tenant = ?1
         ) GROUP BY origin, origin_seq ORDER BY origin, origin_seq",
        )
        .map_err(|_| ())?;
    let mut rows = statement.query([tenant]).map_err(|_| ())?;
    while let Some(row) = rows.next().map_err(|_| ())? {
        let origin: String = row.get(0).map_err(|_| ())?;
        let seq: i64 = row.get(1).map_err(|_| ())?;
        let hlc: i64 = row.get(2).map_err(|_| ())?;
        if origin.is_empty() || seq <= 0 {
            return Err(());
        }
        let entry = prefixes.entry(origin.clone()).or_insert(AppliedFrontierEntry {
            origin,
            origin_sequence: 0,
            hlc: 0,
        });
        if entry.origin_sequence.checked_add(1) == Some(u64::try_from(seq).map_err(|_| ())?) {
            entry.origin_sequence = u64::try_from(seq).map_err(|_| ())?;
            entry.hlc = entry.hlc.max(u64::from_le_bytes(hlc.to_le_bytes()));
        }
    }
    Ok(prefixes.into_values().filter(|entry| entry.origin_sequence != 0).collect())
}

/// Encodes the per-origin frontier visible at the transaction's commit cut.
/// The query runs inside the same `BEGIN IMMEDIATE` transaction as the row,
/// so every row in a commit carries the exact same causal context and a
/// restart reconstructs it from durable bytes rather than recomputing it from
/// current state. A version byte leaves room for interval contexts later.
fn context_frontier(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
) -> Result<Vec<u8>, ()> {
    let entries = origin_prefixes_on(transaction, tenant.as_str())?;
    let mut output = Vec::with_capacity(5 + entries.len() * 20);
    codec::push_u8(&mut output, 1);
    codec::push_u32(&mut output, entries.len() as u32);
    for entry in entries {
        codec::push_str(&mut output, &entry.origin);
        codec::push_u64(&mut output, entry.origin_sequence);
    }
    Ok(output)
}

/// The public revision string of one row: the sequence it committed at.
fn revision_string(sequence: u64, version: u64) -> String {
    format!("{sequence}-{version}")
}

/// Encodes the stable logical fields of one journal row deterministically.
/// Local storage sequence, revision, durability, and wall-clock time are
/// deliberately excluded because they differ when a source row is applied in
/// another region. The replication boundary hashes these bytes.
#[allow(clippy::too_many_arguments, reason = "canonical row fields are explicit by contract")]
fn canonical_journal_row(
    collection: &str,
    id: &str,
    commit_ulid: &str,
    deleted: i64,
    body: &[u8],
    origin: &str,
    origin_sequence: i64,
    hlc: i64,
    context: &[u8],
) -> Vec<u8> {
    let mut output = Vec::new();
    for value in [collection, id, commit_ulid, origin] {
        codec::push_str(&mut output, value);
    }
    for value in [deleted, origin_sequence, hlc] {
        output.extend_from_slice(&value.to_le_bytes());
    }
    codec::push_u32(&mut output, body.len() as u32);
    output.extend_from_slice(body);
    codec::push_u32(&mut output, context.len() as u32);
    output.extend_from_slice(context);
    output
}

/// Advances the recorded root chain head.
fn update_root_head(transaction: &rusqlite::Transaction<'_>, root: &str) -> Result<(), ()> {
    transaction
        .prepare_cached("UPDATE meta SET value = ?1 WHERE key = 'root_head'")
        .and_then(|mut statement| statement.execute(rusqlite::params![root]))
        .map(|_| ())
        .map_err(|_| ())
}

/// Flushes one wave's per-database metering facts (E-9, D-16/D-42).
///
/// Every sequence above `wave_base` was allocated by this wave, so one scan
/// of the wave's own rows yields each billed database's exact wave bytes;
/// each row bills `ceil(bytes / 4 KiB)` write units. A committed intent that
/// wrote no row — a collection-less delete matching nothing — still bills
/// its single delete, with `seq_hi` floored at its receipt sequence. A
/// retried identity replays its receipt without writing a row and without a
/// new sequence, so it bills nothing twice. Riding the wave's transaction
/// means a rolled-back wave bills nothing.
fn flush_metering_db_facts(
    transaction: &rusqlite::Transaction<'_>,
    wave_base: u64,
    intents: &[CommitIntent],
    results: &[Option<CommitResult>],
) -> Result<(), ()> {
    use rusqlite::OptionalExtension;
    use std::collections::BTreeMap;
    // Operation facts retain local logical write units. Apply the accepted
    // placement's Region multiplier once at the per-database rollup seam.
    let placement: Option<(String, u64)> = transaction
        .query_row(
            "SELECT tenant, selected_regions FROM usage_placement WHERE singleton = 1",
            [],
            |row| {
                let tenant = row.get::<_, String>(0)?;
                let selected = row.get::<_, String>(1)?;
                let regions = selected.split(',').filter(|region| !region.is_empty()).count();
                Ok((tenant, regions as u64))
            },
        )
        .optional()
        .map_err(|_| ())?;
    if placement.as_ref().is_some_and(|(_, regions)| *regions == 0) {
        return Err(());
    }
    let mut billed: BTreeMap<String, (u64, u64, u64)> = BTreeMap::new();
    {
        let mut statement = transaction
            .prepare_cached(
                "SELECT tenant, sequence, writes, bytes FROM operation_facts
                 WHERE committed = 1 AND sequence > ?1",
            )
            .map_err(|_| ())?;
        let rows = statement
            .query_map([wave_base as i64], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                ))
            })
            .map_err(|_| ())?;
        for row in rows {
            let (tenant, seq, writes, bytes) = row.map_err(|_| ())?;
            let multiplier = placement
                .as_ref()
                .filter(|(placement_tenant, _)| placement_tenant == &tenant)
                .map_or(1, |(_, regions)| *regions);
            let seq = u64::try_from(seq).map_err(|_| ())?;
            let writes = u64::try_from(writes).map_err(|_| ())?;
            let bytes = u64::try_from(bytes).map_err(|_| ())?;
            let entry = billed.entry(tenant).or_insert((0, 0, 0));
            entry.0 = entry.0.max(seq);
            entry.1 = entry.1.checked_add(writes.checked_mul(multiplier).ok_or(())?).ok_or(())?;
            entry.2 = entry.2.saturating_add(bytes);
        }
    }
    // Keep the arguments in the signature so the flush remains coupled to
    // the exact wave that staged the facts; all accounting now comes from
    // the durable operation rows, including no-row operations.
    let _ = (intents, results);
    for (db, (seq_hi, writes, bytes)) in &billed {
        // A wave with only rejections bills nothing and writes no row.
        if *seq_hi == 0 && *writes == 0 {
            continue;
        }
        transaction
            .prepare_cached(
                "INSERT INTO metering_db_facts (db, seq_hi, writes, bytes) VALUES (?1, ?2, ?3, ?4)
                 ON CONFLICT(db) DO UPDATE SET
                    seq_hi = MAX(metering_db_facts.seq_hi, excluded.seq_hi),
                    writes = metering_db_facts.writes + excluded.writes,
                    bytes = metering_db_facts.bytes + excluded.bytes",
            )
            .and_then(|mut statement| {
                statement.execute(rusqlite::params![
                    db,
                    *seq_hi as i64,
                    *writes as i64,
                    *bytes as i64
                ])
            })
            .map_err(|_| ())?;
    }
    Ok(())
}

/// Rebuilds the usage counter from the folded journal when it trails the
/// journal's own high-water — the counter is a cache, the journal is the
/// truth.
fn reconcile_usage_counters(connection: &Connection) -> Result<(), ()> {
    let recorded =
        connection.query_row("SELECT seq_hi FROM usage_counters WHERE key = 'ops'", [], |row| {
            row.get::<_, i64>(0)
        });
    let high_water = connection
        .query_row("SELECT COALESCE(MAX(commit_seq), 0) FROM documents", [], |row| {
            row.get::<_, i64>(0)
        })
        .map_err(|_| ())?;
    let rebuild = match recorded {
        Ok(seq_hi) => seq_hi < high_water,
        Err(rusqlite::Error::QueryReturnedNoRows) => true,
        Err(_) => return Err(()),
    };
    if rebuild {
        let ops = connection
            .query_row("SELECT COUNT(DISTINCT commit_seq) FROM documents", [], |row| {
                row.get::<_, i64>(0)
            })
            .map_err(|_| ())?;
        connection
            .prepare_cached(
                "INSERT INTO usage_counters (key, seq_hi, ops) VALUES ('ops', ?1, ?2)
                 ON CONFLICT(key) DO UPDATE SET seq_hi = excluded.seq_hi, ops = excluded.ops",
            )
            .and_then(|mut statement| statement.execute(rusqlite::params![high_water, ops]))
            .map(|_| ())
            .map_err(|_| ())?;
    }
    Ok(())
}

/// Rebuilds the per-database metering facts from durable operation facts.
/// The operation rows are the billing truth: retries write one fact once, and
/// a no-row delete remains represented even though it has no folded document
/// row (E-9, D-16/D-42).
fn reconcile_metering_db_facts(connection: &Connection) -> Result<(), ()> {
    // This cache is rebuilt on every open.  A torn wave can leave all
    // counters internally plausible (including a matching high-water), so a
    // high-water comparison alone cannot detect corruption. Operation facts
    // are the durable source of truth and include sequence-only deletes.
    connection.execute_batch("DELETE FROM metering_db_facts;").map_err(|_| ())?;
    let placement: Option<(String, u64)> = connection
        .query_row(
            "SELECT tenant, selected_regions FROM usage_placement WHERE singleton = 1",
            [],
            |row| {
                let tenant = row.get::<_, String>(0)?;
                let selected = row.get::<_, String>(1)?;
                let regions = selected.split(',').filter(|region| !region.is_empty()).count();
                Ok((tenant, regions as u64))
            },
        )
        .optional()
        .map_err(|_| ())?;
    if placement.as_ref().is_some_and(|(_, regions)| *regions == 0) {
        return Err(());
    }
    let mut statement = connection
        .prepare(
            "SELECT tenant, MAX(sequence), SUM(writes), SUM(bytes)
             FROM operation_facts
             WHERE committed = 1
             GROUP BY tenant",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, i64>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
            ))
        })
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    for (tenant, sequence, writes, bytes) in rows {
        let multiplier = placement
            .as_ref()
            .filter(|(placement_tenant, _)| placement_tenant == &tenant)
            .map_or(1, |(_, regions)| *regions);
        let writes = u64::try_from(writes).map_err(|_| ())?.checked_mul(multiplier).ok_or(())?;
        connection
            .execute(
                "INSERT INTO metering_db_facts (db, seq_hi, writes, bytes) VALUES (?1, ?2, ?3, ?4)",
                rusqlite::params![tenant, sequence, writes as i64, bytes],
            )
            .map_err(|_| ())?;
    }
    Ok(())
}

/// The highest sequence any durable row names. Commits' folded document rows,
/// usage facts, and metering rollups share one sequence space, exactly the
/// reference engines' watermark rule.
fn durable_watermark(connection: &Connection) -> Result<u64, ()> {
    let watermark = connection
        .query_row(
            "SELECT COALESCE(MAX(sequence), 0) FROM (
                 SELECT MAX(commit_seq) AS sequence FROM documents
                 UNION ALL
                 SELECT MAX(seq) AS sequence FROM usage_facts
                 UNION ALL
                 SELECT MAX(sequence) AS sequence FROM operation_facts
                 UNION ALL
                 SELECT MAX(seq_hi) AS sequence FROM metering_db_facts
             )",
            [],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|_| ())?;
    u64::try_from(watermark).map_err(|_| ())
}

/// The minimum consumer frontier — the pruning floor — when any consumer
/// has registered.
fn history_floor_on(connection: &Connection) -> Result<Option<u64>, ()> {
    let floor = connection
        .query_row("SELECT MIN(seq) FROM consumer_frontiers", [], |row| {
            row.get::<_, Option<i64>>(0)
        })
        .map_err(|_| ())?;
    floor.map(|value| u64::try_from(value).map_err(|_| ())).transpose()
}

/// Reads one fact identity's stored envelope, when the identity is bound.
/// The lookup keys the complete identity — the full scope plus the minting
/// identity — exactly the primary key.
fn usage_fact_body(connection: &Connection, fact: &UsageFact) -> Result<Option<Vec<u8>>, ()> {
    let scope = fact.scope();
    let identity = fact.identity();
    let row = connection.query_row(
        "SELECT body FROM usage_facts
         WHERE tenant = ?1 AND project = ?2 AND database = ?3 AND partition = ?4
           AND region = ?5 AND rule = ?6 AND fact_id = ?7",
        rusqlite::params![
            scope.tenant_id().as_str(),
            scope.project_id().as_str(),
            scope.database_id().as_str(),
            scope.partition_id().as_str(),
            scope.region_id().as_str(),
            minting_rule_code(identity.rule()),
            identity.id().as_str()
        ],
        |row| row.get::<_, Vec<u8>>(0),
    );
    match row {
        Ok(body) => Ok(Some(body)),
        Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
        Err(_) => Err(()),
    }
}

/// Reports whether one standalone authoritative fact is valid to mint —
/// the reference engines' rule for `UsageFactSource::Standalone`: only the
/// closed billable document-write command (`UsageRecordRequest` accepts
/// solely `DocumentWriteUnits`; storage byte-seconds are interval rollups,
/// never standalone). Legacy admitted/egress meters are unbilled and rejected
/// here; already-persisted rows remain decodable via the page path.
fn standalone_usage_fact_is_valid(fact: &UsageFact) -> bool {
    let known_version = MeterCatalog::initial().get(fact.meter(), fact.meter_version()).is_some();
    let instant = fact.window().start_millis() != 0
        && fact.window().start_millis() == fact.window().end_millis();
    let document_write = fact.meter() == MeterKind::DocumentWriteUnits
        && matches!(fact.identity(), MintingIdentity::DocumentWrite(_))
        && fact.delta() > 0
        && fact.delta() <= MAX_DOCUMENT_WRITE_UNITS_PER_FACT;
    known_version && instant && document_write
}

/// Maps a minting rule to its integer code — the discriminant whose order
/// is the type's `Ord`, so SQL ordering equals `UsageFactPosition` order.
/// Codes are wire-compat: `MintingRule::DocumentWrite` shares 0 with
/// `AcceptedOriginCommit` and `LogicalStorage` shares 1 with
/// `StableAdmittedRequest`, so both current and legacy identities order
/// identically without extra arms.
fn minting_rule_code(rule: MintingRule) -> i64 {
    match rule {
        MintingRule::AcceptedOriginCommit => 0,
        MintingRule::StableAdmittedRequest => 1,
        MintingRule::PublicResponseBytes => 2,
        MintingRule::ActiveRegionHour => 3,
        MintingRule::BackupByteHour => 4,
        MintingRule::ExplicitRestoreWork => 5,
    }
}

/// Returns the key's live version at or below `at`, if any.
fn version_at(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    at: u64,
) -> Result<Option<u64>, ()> {
    // Resolve the newest row first, tombstone included: a tombstone hides
    // the key entirely, so the version below it must not survive as the
    // key's observed version (a re-put restarts at version 1). An expired
    // row hides the key the same way until the sweep tombstones it.
    let now = unix_millis();
    transaction
        .prepare_cached(
            "SELECT version, deleted, expires_at FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
             ORDER BY commit_seq DESC LIMIT 1",
        )
        .and_then(|mut statement| {
            statement.query_row(
                rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str(), at as i64],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, Option<i64>>(2)?,
                    ))
                },
            )
        })
        .map(|(version, deleted, expires_at)| {
            if row_is_live(deleted, expires_at, now, collection.as_str()) {
                u64::try_from(version).ok()
            } else {
                None
            }
        })
        .or_else(|error| match error {
            rusqlite::Error::QueryReturnedNoRows => Ok(None),
            _ => Err(()),
        })
}

/// Returns the first collection, in canonical key order, holding a live copy
/// of the identifier at or below `at`. Expired rows hold no collection:
/// expiry hides the key the way a tombstone does.
fn first_collection_of_id(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    id: &DocumentId,
    at: u64,
) -> Result<Option<CollectionName>, ()> {
    transaction
        .prepare_cached(
            "SELECT collection FROM documents
             WHERE tenant = ?1 AND id = ?2 AND commit_seq <= ?3 AND deleted = 0
               AND (collection = ?4 OR expires_at IS NULL OR expires_at > ?5)
             GROUP BY collection ORDER BY collection LIMIT 1",
        )
        .and_then(|mut statement| {
            statement.query_row(
                rusqlite::params![
                    tenant.as_str(),
                    id.as_str(),
                    at as i64,
                    RESERVED_FAMILY_COLLECTION,
                    unix_millis()
                ],
                |row| row.get::<_, String>(0),
            )
        })
        .map(|collection| CollectionName::try_new(&collection).ok())
        .or_else(|error| match error {
            rusqlite::Error::QueryReturnedNoRows => Ok(None),
            _ => Err(()),
        })
}

/// Checks one keyed write against the snapshot and published views.
#[allow(clippy::too_many_arguments, reason = "the conflict check inputs")]
fn conflict_between(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    expected: ExpectedVersion,
    snapshot_sequence: u64,
    published_sequence: u64,
) -> Result<bool, ()> {
    let snapshot_version = version_at(transaction, tenant, collection, id, snapshot_sequence)?;
    let published_version = version_at(transaction, tenant, collection, id, published_sequence)?;
    Ok(snapshot_version != published_version
        || !codec::matches_expectation(snapshot_version, expected))
}

/// Checks a delete of an identifier the snapshot does not contain.
fn conflict_for_absent_document(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    id: &DocumentId,
    expected: ExpectedVersion,
    snapshot_sequence: u64,
    published_sequence: u64,
) -> Result<bool, ()> {
    let in_snapshot = first_collection_of_id(transaction, tenant, id, snapshot_sequence)?.is_some();
    let in_published =
        first_collection_of_id(transaction, tenant, id, published_sequence)?.is_some();
    Ok(in_snapshot != in_published || in_published || !codec::matches_expectation(None, expected))
}
/// Derives the next root-chain identity from the current head.
///
/// The identity binds the sequence to the preceding publication and the
/// committed write-set digest.
fn chained_root(head: &str, sequence: u64, digest: u64) -> String {
    let mut chained = Vec::new();
    codec::push_str(&mut chained, head);
    codec::push_u64(&mut chained, sequence);
    codec::push_u64(&mut chained, digest);
    format!("sqlite3-{:016x}{:016x}", sequence, codec::fnv1a(&chained))
}

/// Opens an existing database for readers without permission to create a file.
fn raw_connection(
    path: &Path,
    flags: OpenFlags,
    encryption: Option<&encrypted_vfs::EncryptedVfs>,
) -> Result<Connection, StorageOpenError> {
    match encryption {
        Some(vfs) => Connection::open_with_flags_and_vfs(path, flags, vfs.name()),
        None if encrypted_file::companion(path).exists() => {
            return Err(StorageOpenError::Unavailable);
        }
        None => Connection::open_with_flags(path, flags),
    }
    .map_err(|_| StorageOpenError::Unavailable)
}

fn open_connection(
    path: &Path,
    expected_binding: &str,
    encryption: Option<&encrypted_vfs::EncryptedVfs>,
) -> Result<Connection, StorageOpenError> {
    let connection = raw_connection(
        path,
        OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_NO_MUTEX,
        encryption,
    )?;
    if validate_database_binding(&connection, expected_binding)? {
        return Err(StorageOpenError::Unavailable);
    }
    configure_connection(&connection)?;
    Ok(connection)
}

/// Opens an existing database for a read-only operation.
///
/// Unlike the writer opener, this must not run `journal_mode` or any schema
/// setup: those pragmas can create or alter durable SQLite sidecars. Temporary
/// views remain available because they live in SQLite's connection-local temp
/// schema.
fn open_read_connection(
    path: &Path,
    expected_binding: &str,
    encryption: Option<&encrypted_vfs::EncryptedVfs>,
) -> Result<Connection, StorageOpenError> {
    let connection = raw_connection(
        path,
        OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
        encryption,
    )?;
    if validate_database_binding(&connection, expected_binding)? {
        return Err(StorageOpenError::Unavailable);
    }
    configure_read_connection(&connection)?;
    Ok(connection)
}

/// Applies the production pairing to one connection: WAL, 60s busy timeout,
/// foreign keys, `synchronous=FULL`, and the per-file resource budget.
fn configure_connection(connection: &Connection) -> Result<(), StorageOpenError> {
    connection
        .query_row("PRAGMA journal_mode = WAL", [], |_| Ok(()))
        .map_err(|_| StorageOpenError::Unavailable)?;
    connection.busy_timeout(BUSY_TIMEOUT).map_err(|_| StorageOpenError::Unavailable)?;
    // wal_autocheckpoint=0 keeps checkpoint latency off the commit path. The
    // dedicated checkpointer executes PASSIVE between waves. Negative
    // cache_size is KiB (rather than pages), so the budget is stable across
    // SQLite page-size choices; mmap is address space and capped separately.
    let pragmas = format!(
        "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;
         PRAGMA wal_autocheckpoint = 0; PRAGMA temp_store = MEMORY;
         PRAGMA cache_size = -{}; PRAGMA mmap_size = {};",
        cache_kib(),
        mmap_bytes()
    );
    connection.execute_batch(&pragmas).map_err(|_| StorageOpenError::Unavailable)?;
    Ok(())
}

/// Applies only connection-local settings to a read-only handle.
fn configure_read_connection(connection: &Connection) -> Result<(), StorageOpenError> {
    connection.busy_timeout(BUSY_TIMEOUT).map_err(|_| StorageOpenError::Unavailable)?;
    let pragmas = format!(
        "PRAGMA foreign_keys = ON; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -{}; PRAGMA mmap_size = {};",
        cache_kib(),
        mmap_bytes(),
    );
    connection.execute_batch(&pragmas).map_err(|_| StorageOpenError::Unavailable)
}

/// The reserved family collection, spelled as `spec.rs` spells it: family
/// state (records, counters, series) lives here with lifecycle semantics
/// of its own, and the document-TTL layer never touches it.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// The raw columns one as-of scan maps each surviving row to.
type ScanRow = (String, String, i64, Option<i64>, Vec<u8>);

/// Reports whether one row is live at `now`: not deleted and not past its
/// TTL expiry (S-02 slice D). Expiry hides the key exactly like a tombstone
/// until the sweep writes the tombstone itself. Family rows are exempt —
/// see [`extract_expires_at`].
fn row_is_live(deleted: i64, expires_at: Option<i64>, now: i64, collection: &str) -> bool {
    deleted == 0
        && (collection == RESERVED_FAMILY_COLLECTION || expires_at.is_none_or(|at| at > now))
}

/// Maps the rows of one as-of scan to their raw columns. `SQLite`'s
/// bare-column rule puts `deleted`, `expires_at`, and `body` from each
/// group's `MAX(commit_seq)` row, which is exactly the as-of state.
fn scan_rows<P: rusqlite::Params>(
    statement: &mut rusqlite::CachedStatement<'_>,
    params: P,
) -> Result<Vec<ScanRow>, rusqlite::Error> {
    let rows = statement.query_map(params, |row| {
        Ok((
            row.get::<_, String>(0)?,
            row.get::<_, String>(1)?,
            row.get::<_, i64>(2)?,
            row.get::<_, Option<i64>>(3)?,
            row.get::<_, Vec<u8>>(4)?,
        ))
    })?;
    rows.collect()
}

/// Collects one scan's live documents, dropping a body that does not
/// decode or match its key. Expired rows are dropped with the tombstones:
/// read-time TTL filtering applies at every live read, so an un-swept
/// expiry is already invisible.
fn scan_documents(rows: Result<Vec<ScanRow>, rusqlite::Error>) -> Vec<Document> {
    collect_checked(rows).unwrap_or_default()
}

/// Collects one scan's live documents, failing closed on a body that does
/// not verify against its key.
fn collect_checked(
    rows: Result<Vec<ScanRow>, rusqlite::Error>,
) -> Result<Vec<Document>, SnapshotReadError> {
    let rows = rows.map_err(|_| SnapshotReadError::Integrity)?;
    let now = unix_millis();
    let mut documents = Vec::with_capacity(rows.len());
    for (collection, id, deleted, expires_at, body) in rows {
        if !row_is_live(deleted, expires_at, now, collection.as_str()) {
            continue;
        }
        let document = decode_document_body(&body).ok_or(SnapshotReadError::Integrity)?;
        if document.collection().as_str() != collection || document.id().as_str() != id {
            return Err(SnapshotReadError::Integrity);
        }
        documents.push(document);
    }
    Ok(documents)
}

impl SqliteStorage {
    fn read_retained_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
        require_continuous_history: bool,
    ) -> Result<Vec<PublishedCommit>, super::JournalReadError> {
        let after = i64::try_from(after.get()).map_err(|_| ())?;
        let reader = self.reader();
        let transaction = reader.connection().unchecked_transaction().map_err(|_| ())?;
        let connection = &transaction;
        let pruned: Option<i64> = connection
            .query_row(
                "SELECT pruned_through FROM journal_pruning WHERE tenant = ?1",
                [tenant.as_str()],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| ())?;
        if require_continuous_history && pruned.is_some_and(|sequence| after < sequence) {
            return Err(super::JournalReadError::HistoryExpired);
        }
        let mut statement = connection
            .prepare_cached(
                "SELECT commit_seq, commit_ulid, collection, id, deleted, version,
                    requested, body, kind, origin, origin_seq, hlc, context
             FROM documents WHERE tenant = ?1 AND commit_seq > ?2 AND commit_seq IN (
                 SELECT commit_seq FROM documents WHERE tenant = ?1 AND commit_seq > ?2
                 ORDER BY commit_seq LIMIT ?3
             )
             ORDER BY commit_seq, collection, id",
            )
            .map_err(|_| ())?;
        let rows = statement
            .query_map(rusqlite::params![tenant.as_str(), after, i64::from(limit)], |row| {
                Ok((
                    row.get::<_, i64>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, i64>(5)?,
                    row.get::<_, i64>(6)?,
                    row.get::<_, Vec<u8>>(7)?,
                    row.get::<_, String>(8)?,
                    row.get::<_, String>(9)?,
                    row.get::<_, i64>(10)?,
                    row.get::<_, i64>(11)?,
                    row.get::<_, Vec<u8>>(12)?,
                ))
            })
            .map_err(|_| ())?;
        // Group the rows of one commit_seq into one PublishedCommit — the
        // journal rebuilt from the folded rows. The inner LIMIT selects
        // rows, and the outer query retrieves their complete commits.
        // Every row of one commit carries the commit's provenance (D-35),
        // so every row must agree with the first row of its group.
        let mut commits: Vec<PublishedCommit> = Vec::new();
        for row in rows {
            let (
                sequence,
                operation,
                collection,
                id,
                deleted,
                version,
                requested,
                body,
                kind,
                origin,
                origin_sequence,
                hlc,
                context,
            ) = row.map_err(|_| ())?;
            let sequence = u64::try_from(sequence).map_err(|_| ())?;
            if sequence == 0 {
                return Err(super::JournalReadError::StorageFailure);
            }
            let version = u64::try_from(version).map_err(|_| ())?;
            let write = match deleted {
                0 => {
                    let document = decode_document_body(&body).ok_or(())?;
                    if document.collection().as_str() != collection || document.id().as_str() != id
                    {
                        return Err(super::JournalReadError::StorageFailure);
                    }
                    PublishedWrite::Put { document }
                }
                1 => PublishedWrite::Delete {
                    collection: Some(CollectionName::try_new(&collection).map_err(|_| ())?),
                    id: DocumentId::try_new(&id).map_err(|_| ())?,
                    version_at_delete: version,
                },
                _ => return Err(super::JournalReadError::StorageFailure),
            };
            let durability =
                u8::try_from(requested).ok().and_then(codec::durability_of).ok_or(())?;
            let operation = OperationId::try_new(&operation).map_err(|_| ())?;
            let kind = match kind.as_str() {
                ROW_KIND_ORIGIN => JournalKind::Origin,
                ROW_KIND_REPLICATED => JournalKind::Replicated,
                _ => return Err(super::JournalReadError::StorageFailure),
            };
            let provenance = JournalProvenance::new(
                kind,
                origin,
                u64::try_from(origin_sequence).map_err(|_| ())?,
                u64::from_le_bytes(hlc.to_le_bytes()),
            )
            .with_context(context);
            match commits.last_mut() {
                Some(last) if last.sequence().get() == sequence => {
                    if last.operation_id() != &operation
                        || last.durability() != durability
                        || last.provenance() != &provenance
                    {
                        return Err(super::JournalReadError::StorageFailure);
                    }
                    let writes = last.writes_mut();
                    writes.push(write);
                }
                _ => {
                    commits.push(PublishedCommit::new(
                        tenant.clone(),
                        operation,
                        StorageSequence::new(sequence),
                        durability,
                        provenance,
                        vec![write],
                    ));
                }
            }
        }
        Ok(commits)
    }
}

impl StoragePort for SqliteStorage {
    fn prepare_participant(&mut self, prepare: &crate::storage::ParticipantPrepare) -> Result<crate::storage::ParticipantPrepareReceipt, ()> {
        let identity = &prepare.identity;
        if prepare.writes.len() > 1024 { return Err(()); }
        let writes: Vec<_> = prepare.writes.clone();
        let payload = crate::storage::participant::encode_prepare_payload(&writes, &prepare.metadata, &prepare.schema_envelopes)?;
        if payload.len() > 64 * 1024 * 1024 { return Err(()); }
        if *blake3::hash(&payload).as_bytes() != identity.digest { return Err(()); }
        let mut connection = self.writer.lock().map_err(|_| ())?;
        connection.execute_batch("PRAGMA synchronous = FULL").map_err(|_| ())?;
        let transaction = connection.unchecked_transaction().map_err(|_| ())?;
        transaction.execute("INSERT INTO participant_prepares(tenant,incarnation,operation,participant,owner,digest,epoch,snapshot,payload) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9) ON CONFLICT(tenant,incarnation,operation,participant) DO NOTHING", rusqlite::params![identity.tenant.to_string(), identity.database_incarnation.to_string(), identity.operation.to_string(), identity.participant, identity.owner, identity.digest.as_slice(), i64::try_from(identity.epoch).map_err(|_| ())?, i64::try_from(identity.snapshot.get()).map_err(|_| ())?, payload]).map_err(|_| ())?;
        let matched: bool = transaction.query_row("SELECT owner=?5 AND epoch=?7 AND snapshot=?8 AND digest=?6 FROM participant_prepares WHERE tenant=?1 AND incarnation=?2 AND operation=?3 AND participant=?4", rusqlite::params![identity.tenant.to_string(), identity.database_incarnation.to_string(), identity.operation.to_string(), identity.participant, identity.owner, identity.digest.as_slice(), i64::try_from(identity.epoch).map_err(|_| ())?, i64::try_from(identity.snapshot.get()).map_err(|_| ())?], |row| row.get(0)).map_err(|_| ())?;
        if !matched { return Err(()); }
        transaction.commit().map_err(|_| ())?;
        Ok(crate::storage::ParticipantPrepareReceipt { identity: identity.clone(), writes: prepare.writes.clone(), metadata: prepare.metadata.clone(), schema_envelopes: prepare.schema_envelopes.clone() })
    }
    fn resolve_participant(&mut self, identity: &crate::storage::ParticipantIdentity) -> Result<Option<crate::storage::ParticipantPrepareReceipt>, ()> {
        let connection = self.writer.lock().map_err(|_| ())?;
        let found: Option<(String, i64, i64, Vec<u8>, Vec<u8>)> = connection.query_row("SELECT owner,epoch,snapshot,digest,payload FROM participant_prepares WHERE tenant=?1 AND incarnation=?2 AND operation=?3 AND participant=?4", rusqlite::params![identity.tenant.to_string(), identity.database_incarnation.to_string(), identity.operation.to_string(), identity.participant], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?))).optional().map_err(|_| ())?;
        match found {
            Some((owner, epoch, snapshot, digest, payload))
                if owner == identity.owner
                    && epoch == i64::try_from(identity.epoch).map_err(|_| ())?
                    && snapshot == i64::try_from(identity.snapshot.get()).map_err(|_| ())?
                    && digest == identity.digest => {
                if *blake3::hash(&payload).as_bytes() != identity.digest {
                    return Err(());
                }
                let (writes, metadata, schema_envelopes) = crate::storage::participant::decode_prepare_payload(&payload)?;
                Ok(Some(crate::storage::ParticipantPrepareReceipt {
                    identity: identity.clone(),
                    writes,
                    metadata,
                    schema_envelopes,
                }))
            }
            Some(_) => Err(()),
            None => Ok(None),
        }
    }
    fn local_replication_origin(&self) -> &str {
        &self.local_origin
    }
    fn replication_repair_cut(
        &self,
        tenant: &TenantId,
        cut: u64,
    ) -> Result<(std::collections::BTreeMap<String, u64>, [u8; 32]), ()> {
        let reader = self.reader();
        let transaction = reader.connection().unchecked_transaction().map_err(|_| ())?;
        let frontier = repair_summary::frontier(&transaction, tenant, cut)?;
        let baseline = repair_summary::baseline_digest(&transaction)?;
        transaction.commit().map_err(|_| ())?;
        Ok((frontier, baseline))
    }
    fn replication_range_summary(
        &self,
        tenant: &TenantId,
        origin: &str,
        cut: u64,
        first: u64,
        last: u64,
    ) -> Result<terrabase_replication_core::anti_entropy::RangeFingerprint, ()> {
        let reader = self.reader();
        let transaction = reader.connection().unchecked_transaction().map_err(|_| ())?;
        let result = repair_summary::summary(&transaction, tenant, origin, cut, first, last)?;
        transaction.commit().map_err(|_| ())?;
        Ok(result)
    }
    fn replication_range_publications(
        &self,
        tenant: &TenantId,
        origin: &str,
        cut: u64,
        first: u64,
        last: u64,
    ) -> Result<Vec<crate::engine::ReplicatedPublication>, ()> {
        let reader = self.reader();
        let transaction = reader.connection().unchecked_transaction().map_err(|_| ())?;
        let result = repair_summary::publications(&transaction, tenant, origin, cut, first, last)?;
        transaction.commit().map_err(|_| ())?;
        Ok(result)
    }
    fn replication_publications(
        &self,
        tenant: &TenantId,
        after: u64,
        limit: u32,
    ) -> Result<Vec<(u64, crate::engine::ReplicatedPublication)>, ()> {
        let reader = self.reader();
        replication_outbox::read_publications(reader.connection(), tenant, after, limit)
    }
    fn private_metadata_at(
        &self,
        tenant: &TenantId,
        key: &str,
        sequence: StorageSequence,
    ) -> Result<Option<DocumentValue>, ()> {
        let reader = self.reader();
        private_metadata::read_private_metadata_on(reader.connection(), tenant, key, sequence.get())
    }

    fn webhook_metadata_load(
        &self,
        tenant: &TenantId,
        namespace: &str,
        key: &str,
    ) -> Result<Option<Vec<u8>>, ()> {
        self.load_webhook_metadata(tenant, namespace, key)
    }

    fn webhook_metadata_list(
        &self,
        tenant: &TenantId,
        namespace: &str,
    ) -> Result<Vec<(String, Vec<u8>)>, ()> {
        self.list_webhook_metadata(tenant, namespace)
    }

    fn webhook_metadata_compare_exchange(
        &mut self,
        tenant: &TenantId,
        namespace: &str,
        key: &str,
        expected: Option<&[u8]>,
        replacement: &[u8],
    ) -> Result<bool, ()> {
        self.compare_exchange_webhook_metadata(tenant, namespace, key, expected, replacement)
    }

    fn schema_metadata_load(
        &self,
        database: &str,
        branch: &str,
        collection: &str,
    ) -> Result<Option<Vec<u8>>, ()> {
        self.load_schema_metadata(database, branch, collection)
    }

    fn index_lifecycle_events(&self, from: u64, limit: usize) -> Result<Vec<Vec<u8>>, ()> {
        let reader = self.reader();
        let mut statement = reader
            .connection()
            .prepare(
                "SELECT CASE WHEN length(payload) <= 524288 THEN payload END,
                 CASE WHEN length(digest) = 32 THEN digest END
                 FROM index_lifecycle_events WHERE sequence >= ?1
             ORDER BY sequence LIMIT ?2",
            )
            .map_err(|_| ())?;
        let rows = statement
            .query_map(
                rusqlite::params![i64::try_from(from).map_err(|_| ())?, limit.min(64) as i64],
                |row| Ok((row.get::<_, Vec<u8>>(0)?, row.get::<_, Vec<u8>>(1)?)),
            )
            .map_err(|_| ())?;
        rows.map(|row| {
            let (payload, digest) = row.map_err(|_| ())?;
            if payload.len() > 524_288 || blake3::hash(&payload).as_bytes().as_slice() != digest {
                return Err(());
            }
            Ok(payload)
        })
        .collect()
    }

    fn append_index_lifecycle_event(&mut self, sequence: u64, payload: &[u8]) -> Result<(), ()> {
        if payload.len() > 524_288 {
            return Err(());
        }
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let transaction = writer.transaction().map_err(|_| ())?;
        let previous: i64 = transaction
            .query_row("SELECT COALESCE(MAX(sequence), 0) FROM index_lifecycle_events", [], |row| {
                row.get(0)
            })
            .map_err(|_| ())?;
        let sequence = i64::try_from(sequence).map_err(|_| ())?;
        if previous.checked_add(1) != Some(sequence) {
            return Err(());
        }
        transaction
            .execute(
                "INSERT INTO index_lifecycle_events(sequence, payload, digest) VALUES (?1, ?2, ?3)",
                rusqlite::params![sequence, payload, blake3::hash(payload).as_bytes().as_slice()],
            )
            .map_err(|_| ())?;
        transaction.commit().map_err(|_| ())
    }

    fn with_quiescent_wal(&self, capture: &mut dyn FnMut(&Path)) -> Result<(), ()> {
        // Direct mutations and explicit checkpoints use the writer mutex.
        // The background checkpoint connection owns only checkpoint_gate,
        // so acquiring it second cannot create a lock cycle.
        let _writer = self.writer.lock().map_err(|_| ())?;
        let _checkpoint = self.checkpoint_gate.lock().map_err(|_| ())?;
        capture(self.database_path.parent().ok_or(())?);
        Ok(())
    }
    fn open_sqlite_read_connection(&self) -> Result<Connection, ()> {
        // A dedicated read handle inherits the already-opened, ownership-
        // validated database path. SQLite WAL gives it a coherent snapshot;
        // the query layer additionally pins the requested Engine sequence.
        open_read_connection(
            &self.database_path,
            &self.expected_binding,
            self.encryption.as_deref(),
        )
        .map_err(|_| ())
    }

    fn origin_epoch(&self) -> Result<(u64, bool), ()> {
        let writer = self.writer.lock().map_err(|_| ())?;
        origin_epoch_on(&writer)
    }

    fn set_origin_epoch(&mut self, epoch: u64, closed: bool) -> Result<(), ()> {
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let tx =
            writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
        let (current, was_closed) = origin_epoch_on(&tx)?;
        if !closed && !lifecycle_state::permits_origin_activation(&tx)? {
            return Err(());
        }
        if epoch < current || (epoch == current && was_closed && !closed) {
            return Err(());
        }
        tx.execute("INSERT INTO meta(key,value) VALUES('origin_authority_epoch',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [format!("{}:{}", epoch, u8::from(closed))]).map_err(|_| ())?;
        tx.commit().map_err(|_| ())
    }

    fn commit(&mut self, intent: CommitIntent) -> CommitResult {
        // The single-commit path shares the batch transaction and therefore
        // the batch's durability rule: a `Synced` commit runs under
        // `synchronous=FULL`.
        self.commit_batch(vec![intent], false).pop().expect("a one-intent batch returns one result")
    }

    fn commit_batch(&mut self, intents: Vec<CommitIntent>, defer_flips: bool) -> Vec<CommitResult> {
        // One `SQLite` transaction is the whole durability story: there are
        // no deferred flip records to witness, so the pipelining request is
        // a no-op and the engine's witness handle is `None` for this
        // backend.
        let _ = defer_flips;
        self.touch();
        match self.committer.submit(intents.clone()) {
            Ok(results) => results,
            // A rolled-back transaction definitely published nothing, so
            // every intent gets a definite refusal — the uncertain outcome
            // the append-then-sync backends report cannot occur here.
            Err(()) => vec![CommitResult::Rejected(CommitRejection::Storage); intents.len()],
        }
    }

    fn replication_fingerprint(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Result<Option<[u8; 32]>, ()> {
        let reader = self.reader();
        replication_fingerprint_on(reader.connection(), tenant, operation)
    }

    fn resolve_operation(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<OperationResolution> {
        let reader = self.reader();
        let connection = reader.connection();
        match connection.query_row(
            "SELECT digest, committed, rejection, sequence, requested
             FROM operation_facts WHERE tenant = ?1 AND client_request_ulid = ?2",
            rusqlite::params![tenant.as_str(), operation.as_str()],
            |row| {
                Ok((
                    row.get::<_, i64>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, i64>(4)?,
                ))
            },
        ) {
            Ok((digest, committed, rejection, sequence, requested)) => {
                #[allow(clippy::cast_sign_loss)]
                let _digest = digest as u64;
                if committed == 1 {
                    let sequence = u64::try_from(sequence).ok()?;
                    if sequence == 0 {
                        return None;
                    }
                    let sequence = StorageSequence::new(sequence);
                    let durability = codec::durability_of(u8::try_from(requested).ok()?)?;
                    return Some(OperationResolution::Committed { sequence, durability });
                }
                if committed != 0 {
                    return None;
                }
                let rejection = codec::decode_rejection(u8::try_from(rejection).ok()?)?;
                return Some(OperationResolution::Rejected(rejection));
            }
            Err(rusqlite::Error::QueryReturnedNoRows) => {}
            Err(_) => return None,
        }
        connection
            .query_row(
                "SELECT MIN(commit_seq), MIN(requested) FROM documents
                 WHERE tenant = ?1 AND commit_ulid = ?2",
                rusqlite::params![tenant.as_str(), operation.as_str()],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?)),
            )
            .ok()
            .and_then(|(sequence, requested)| {
                let requested = codec::durability_of(u8::try_from(requested).ok()?)?;
                Some(OperationResolution::Committed {
                    sequence: StorageSequence::new(u64::try_from(sequence).ok()?),
                    durability: requested,
                })
            })
            .or_else(|| {
                // A rejected identity replays its rejection from the
                // rejections table.
                let rejected = connection
                    .query_row(
                        "SELECT rejection FROM rejections WHERE tenant = ?1 AND operation = ?2",
                        rusqlite::params![tenant.as_str(), operation.as_str()],
                        |row| row.get::<_, i64>(0),
                    )
                    .ok()?;
                let rejection = codec::decode_rejection(u8::try_from(rejected).ok()?)?;
                Some(OperationResolution::Rejected(rejection))
            })
    }

    fn record_usage(
        &mut self,
        tenant: &TenantId,
        fact: UsageFact,
    ) -> Result<(DurableUsageFact, bool), UsageStorageError> {
        if fact.scope().tenant_id() != tenant || !standalone_usage_fact_is_valid(&fact) {
            return Err(UsageStorageError::Corrupt);
        }
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let transaction = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| UsageStorageError::Unavailable)?;
        // The writer transaction is the linearization point.  Checking only
        // through a read-pool connection races another recorder between its
        // check and INSERT; checking again here turns that race into an
        // exact replay instead of an unavailable error.
        if let Some(body) =
            usage_fact_body(&transaction, &fact).map_err(|()| UsageStorageError::Corrupt)?
        {
            let existing =
                DurableUsageFact::decode(&body).map_err(|_| UsageStorageError::Corrupt)?;
            drop(transaction);
            return if existing.fact() == &fact {
                Ok((existing, true))
            } else {
                Err(UsageStorageError::IdentityConflict)
            };
        }
        // The deletion stop marker and this insertion share the same writer
        // transaction. Exact replays above remain readable, but no new
        // billable fact may extend beyond the accepted usage cutoff. Delayed
        // facts incurred through the cutoff (including exactly at it) remain
        // admissible; only a straddling or later window is rejected.
        let stopped_at: Option<i64> = transaction
            .query_row(
                "SELECT millis FROM storage_accounting_stops WHERE tenant = ?1",
                [tenant.as_str()],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| UsageStorageError::Unavailable)?;
        if stopped_at.is_some_and(|cutoff| {
            i64::try_from(fact.window().end_millis()).is_ok_and(|end| end > cutoff)
        }) {
            return Err(UsageStorageError::IdentityConflict);
        }
        // Usage facts share the commit sequence space, exactly the
        // reference engines' watermark rule.
        let Some(sequence) = durable_watermark(&transaction)
            .map_err(|()| UsageStorageError::Unavailable)?
            .checked_add(1)
        else {
            return Err(UsageStorageError::Unavailable);
        };
        usage_seal::ensure_unsealed(&transaction, &fact)
            .map_err(|()| UsageStorageError::Unavailable)?;
        let durable = DurableUsageFact::try_new(fact.clone(), sequence, 1)
            .map_err(|_| UsageStorageError::Corrupt)?;
        let scope = durable.fact().scope();
        let identity = durable.fact().identity();
        let inserted = transaction.execute(
            "INSERT INTO usage_facts
                 (tenant, project, database, partition, region, rule, fact_id,
                  seq, start_millis, end_millis, body)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
            rusqlite::params![
                tenant.as_str(),
                scope.project_id().as_str(),
                scope.database_id().as_str(),
                scope.partition_id().as_str(),
                scope.region_id().as_str(),
                minting_rule_code(identity.rule()),
                identity.id().as_str(),
                sequence as i64,
                durable.fact().window().start_millis() as i64,
                durable.fact().window().end_millis() as i64,
                durable.encoded(),
            ],
        );
        if let Err(_error) = inserted {
            // A concurrent/legacy writer may have won the identity between
            // the lookup and INSERT.  Re-read under this same transaction so
            // a unique-key race is replay-safe; unrelated SQLite failures
            // remain unavailable and roll back.
            if let Some(body) =
                usage_fact_body(&transaction, &fact).map_err(|()| UsageStorageError::Corrupt)?
            {
                let existing =
                    DurableUsageFact::decode(&body).map_err(|_| UsageStorageError::Corrupt)?;
                drop(transaction);
                return if existing.fact() == &fact {
                    Ok((existing, true))
                } else {
                    Err(UsageStorageError::IdentityConflict)
                };
            }
            return Err(UsageStorageError::Unavailable);
        }
        transaction.commit().map_err(|_| UsageStorageError::Unavailable)?;
        Ok((durable, false))
    }

    fn close_storage_usage(
        &mut self,
        tenant: &TenantId,
        window: MeasurementWindow,
    ) -> Result<crate::usage::StorageUsageClosure, UsageStorageError> {
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let transaction = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| UsageStorageError::Unavailable)?;
        let placement = write_usage::binding_for(&transaction, tenant)
            .map_err(|()| UsageStorageError::Corrupt)?
            .ok_or(UsageStorageError::Corrupt)?;
        let regions = u32::try_from(placement.selected_regions.len())
            .map_err(|_| UsageStorageError::Corrupt)?;
        let receipt = storage_closure::close(&transaction, placement.scope, regions, window)
            .map_err(|()| UsageStorageError::Corrupt)?;
        transaction.commit().map_err(|_| UsageStorageError::Unavailable)?;
        Ok(receipt)
    }

    fn lifecycle_update(
        &mut self,
        tenant: &TenantId,
        command: crate::engine::lifecycle_state::LifecycleCommand,
    ) -> Result<
        crate::engine::lifecycle_state::LifecycleState,
        crate::engine::lifecycle_state::LifecycleError,
    > {
        self.update_lifecycle(tenant, command)
    }

    fn lifecycle_operation(
        &mut self,
        tenant: &TenantId,
        command: crate::engine::lifecycle_state::LifecycleCommand,
    ) -> Result<
        crate::engine::lifecycle_state::LifecycleOperationOutcome,
        crate::engine::lifecycle_state::LifecycleError,
    > {
        self.update_lifecycle_operation(tenant, command)
    }

    fn storage_usage_stopped_at(
        &self,
        tenant: &TenantId,
    ) -> Result<Option<u64>, UsageStorageError> {
        self.reader()
            .connection()
            .query_row(
                "SELECT millis FROM storage_accounting_stops WHERE tenant = ?1",
                [tenant.as_str()],
                |row| row.get::<_, i64>(0),
            )
            .optional()
            .map_err(|_| UsageStorageError::Unavailable)?
            .map(|value| u64::try_from(value).map_err(|_| UsageStorageError::Corrupt))
            .transpose()
    }

    fn storage_usage_closed_through(
        &self,
        tenant: &TenantId,
    ) -> Result<Option<u64>, UsageStorageError> {
        self.reader()
            .connection()
            .query_row(
                "SELECT MAX(end_millis) FROM storage_usage_closures WHERE tenant = ?1",
                [tenant.as_str()],
                |row| row.get::<_, Option<i64>>(0),
            )
            .map_err(|_| UsageStorageError::Unavailable)?
            .map(|value| u64::try_from(value).map_err(|_| UsageStorageError::Corrupt))
            .transpose()
    }

    fn stop_storage_usage(
        &mut self,
        tenant: &TenantId,
        at_millis: u64,
    ) -> Result<(), UsageStorageError> {
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let transaction = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| UsageStorageError::Unavailable)?;
        accounting::stop(&transaction, tenant.as_str(), at_millis)
            .map_err(|()| UsageStorageError::Corrupt)?;
        transaction.commit().map_err(|_| UsageStorageError::Unavailable)
    }

    fn usage_facts_page(
        &self,
        tenant: &TenantId,
        snapshot: StorageSequence,
        after: Option<&UsageFactPosition>,
        window: Option<MeasurementWindow>,
        limit: u32,
    ) -> Result<Vec<DurableUsageFact>, UsageStorageError> {
        let reader = self.reader();
        let connection = reader.connection();
        // `(seq, rule, fact_id)` SQL order equals `UsageFactPosition`
        // order: `rule` is stored as the `MintingRule` discriminant, whose
        // integer order is the type's `Ord`.
        let mut sql = String::from("SELECT body FROM usage_facts WHERE tenant = ?1 AND seq <= ?2");
        let tenant_text = tenant.as_str();
        let snapshot_bound = snapshot.get() as i64;
        let mut bind: Vec<&dyn rusqlite::ToSql> = vec![&tenant_text, &snapshot_bound];
        let position_sequence;
        let position_rule;
        let position_id;
        if let Some(position) = after {
            sql.push_str(" AND (seq, rule, fact_id) > (?3, ?4, ?5)");
            position_sequence = position.sequence as i64;
            position_rule = minting_rule_code(position.rule);
            position_id = position.id.as_str();
            bind.extend_from_slice(&[&position_sequence, &position_rule, &position_id]);
        }
        let window_start;
        let window_end;
        if let Some(filter) = window {
            // An instant fact (start = end) matches inside the filter; an
            // interval fact overlaps it — the reference window rule.
            sql.push_str(
                " AND ((start_millis = end_millis AND start_millis >= ? AND start_millis < ?)
                    OR (start_millis <> end_millis AND start_millis < ? AND ? < end_millis))",
            );
            window_start = filter.start_millis() as i64;
            window_end = filter.end_millis() as i64;
            bind.extend_from_slice(&[&window_start, &window_end, &window_end, &window_start]);
        }
        sql.push_str(" ORDER BY seq, rule, fact_id LIMIT ?");
        let limit_bound = i64::from(limit);
        bind.push(&limit_bound);
        let mut statement = connection.prepare(&sql).map_err(|_| UsageStorageError::Unavailable)?;
        let rows = statement
            .query_map(bind.as_slice(), |row| row.get::<_, Vec<u8>>(0))
            .map_err(|_| UsageStorageError::Unavailable)?;
        let mut facts = Vec::new();
        for row in rows {
            let body = row.map_err(|_| UsageStorageError::Unavailable)?;
            facts.push(DurableUsageFact::decode(&body).map_err(|_| UsageStorageError::Corrupt)?);
        }
        Ok(facts)
    }

    fn configure_usage_placement(
        &mut self,
        placement: &crate::usage::UsagePlacement,
    ) -> Result<(), ()> {
        let mut writer = self.writer.lock().map_err(|_| ())?;
        write_usage::configure(&mut writer, placement)
    }

    fn usage_placement(
        &self,
        tenant: &TenantId,
    ) -> Result<Option<crate::usage::UsagePlacement>, ()> {
        let writer = self.writer.lock().map_err(|_| ())?;
        write_usage::binding_for(&writer, tenant)
    }

    fn metering_fact(&self, db: &str) -> Option<DbUsageFact> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row(
                "SELECT db, seq_hi, writes, bytes FROM metering_db_facts WHERE db = ?1",
                [db],
                |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, i64>(3)?,
                    ))
                },
            )
            .ok()
            .and_then(|(db, seq_hi, writes, bytes)| {
                Some(DbUsageFact::new(
                    db,
                    u64::try_from(seq_hi).ok()?,
                    u64::try_from(writes).ok()?,
                    u64::try_from(bytes).ok()?,
                ))
            })
    }

    fn metering_facts_page(
        &self,
        after: Option<&str>,
        limit: u32,
    ) -> (Vec<DbUsageFact>, Option<String>) {
        let limit = limit.clamp(1, MAX_USAGE_PAGE_SIZE);
        let reader = self.reader();
        let connection = reader.connection();
        let fetch = i64::from(limit).saturating_add(1);
        let sql = "SELECT db, seq_hi, writes, bytes FROM metering_db_facts
                   WHERE (?1 IS NULL OR db > ?1) ORDER BY db LIMIT ?2";
        let Ok(mut statement) = connection.prepare(sql) else {
            return (Vec::new(), None);
        };
        let Ok(rows) = statement.query_map(rusqlite::params![after, fetch], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, i64>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
            ))
        }) else {
            return (Vec::new(), None);
        };
        let mut facts = Vec::new();
        for row in rows {
            let Ok((db, seq_hi, writes, bytes)) = row else { continue };
            let (Ok(seq_hi), Ok(writes), Ok(bytes)) =
                (u64::try_from(seq_hi), u64::try_from(writes), u64::try_from(bytes))
            else {
                continue;
            };
            facts.push(DbUsageFact::new(db, seq_hi, writes, bytes));
        }
        if facts.len() > limit as usize {
            let next = facts.pop().and_then(|_| facts.last().map(|last| last.db().to_owned()));
            (facts, next)
        } else {
            (facts, None)
        }
    }

    fn metering_live_bytes(&self, db: &str) -> u64 {
        let reader = self.reader();
        let connection = reader.connection();
        // Newest version per key, tombstones and TTL-expired rows excluded.
        // Expiry is a storage-accounting boundary immediately, even before
        // the asynchronous sweep writes its replicated tombstone.
        connection
            .query_row(
                "SELECT COALESCE(SUM(LENGTH(body)), 0) FROM documents AS live
                 WHERE tenant = ?1 AND deleted = 0
                   AND (collection = ?2 OR expires_at IS NULL OR expires_at > ?3)
                   AND commit_seq = (
                    SELECT MAX(commit_seq) FROM documents
                    WHERE tenant = live.tenant
                      AND collection = live.collection
                      AND id = live.id)",
                rusqlite::params![db, RESERVED_FAMILY_COLLECTION, unix_millis()],
                |row| row.get::<_, i64>(0),
            )
            .ok()
            .and_then(|bytes| u64::try_from(bytes).ok())
            .unwrap_or(0)
    }

    fn record_read(&self, db: &str) {
        self.reads
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .entry(db.to_owned())
            .and_modify(|count| *count = count.saturating_add(1))
            .or_insert(1);
    }

    fn read_count(&self, db: &str) -> u64 {
        self.reads
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(db)
            .copied()
            .unwrap_or(0)
    }

    fn read_document_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<Document> {
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let connection = reader.connection();
        let now = unix_millis();
        connection
            .query_row(
                "SELECT collection, deleted, expires_at, body, MAX(commit_seq)
                 FROM documents INDEXED BY documents_id
                 WHERE tenant = ?1 AND id = ?2 AND commit_seq <= ?3
                 GROUP BY collection ORDER BY collection LIMIT 1",
                rusqlite::params![tenant.as_str(), id.as_str(), sequence.get() as i64],
                |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, Option<i64>>(2)?,
                        row.get::<_, Vec<u8>>(3)?,
                    ))
                },
            )
            .ok()
            .filter(|(collection, deleted, expires_at, _)| {
                row_is_live(*deleted, *expires_at, now, collection.as_str())
            })
            .and_then(|(_, _, _, body)| decode_document_body(&body))
    }

    fn journal_before_image(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
        sequence: StorageSequence,
    ) -> Result<super::JournalBeforeImage, ()> {
        let reader = self.reader();
        let transaction = reader.connection().unchecked_transaction().map_err(|_| ())?;
        let pruned: Option<i64> = transaction
            .query_row(
                "SELECT pruned_through FROM journal_pruning WHERE tenant = ?1",
                [tenant.as_str()],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| ())?;
        let previous = transaction
            .query_row(
                "SELECT deleted, body, commit_seq FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq < ?4
             ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    id.as_str(),
                    i64::try_from(sequence.get()).map_err(|_| ())?
                ],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, Vec<u8>>(1)?, row.get::<_, i64>(2)?)),
            )
            .optional()
            .map_err(|_| ())?;
        match previous {
            None if pruned.is_none() => Ok(super::JournalBeforeImage::Absent),
            None => Ok(super::JournalBeforeImage::Unknown),
            Some((_, _, prior)) if pruned.is_some_and(|floor| prior <= floor) => {
                Ok(super::JournalBeforeImage::Unknown)
            }
            Some((1, _, _)) => Ok(super::JournalBeforeImage::Absent),
            Some((0, body, _)) => {
                // Historical dispatch must not apply today's TTL filter: a
                // view may still own the row that the expiry removed.
                let document = decode_document_body(&body).ok_or(())?;
                if document.collection() != collection || document.id() != id {
                    return Err(());
                }
                Ok(super::JournalBeforeImage::Document(Box::new(document)))
            }
            _ => Err(()),
        }
    }

    fn read_document_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<Document> {
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let connection = reader.connection();
        let now = unix_millis();
        connection
            .query_row(
                "SELECT deleted, expires_at, body FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    id.as_str(),
                    sequence.get() as i64
                ],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, Option<i64>>(1)?,
                        row.get::<_, Vec<u8>>(2)?,
                    ))
                },
            )
            .ok()
            .filter(|(deleted, expires_at, _)| {
                row_is_live(*deleted, *expires_at, now, collection.as_str())
            })
            .and_then(|(_, _, body)| decode_document_body(&body))
    }

    fn document_provenance_at(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<(u64, String, bool)> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row(
                "SELECT hlc, origin, deleted FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str()],
                |row| {
                    Ok((
                        u64::try_from(row.get::<_, i64>(0)?).unwrap_or(0),
                        row.get::<_, String>(1)?,
                        row.get::<_, i64>(2)? != 0,
                    ))
                },
            )
            .ok()
    }

    fn declare_merge_schema(
        &mut self,
        collection: &CollectionName,
        schema: &MergeSchema,
    ) -> Result<(), MergeSchemaError> {
        let writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        writer
            .prepare_cached(
                "INSERT INTO merge_schemas (collection, schema) VALUES (?1, ?2)
                 ON CONFLICT(collection) DO UPDATE SET schema = excluded.schema",
            )
            .and_then(|mut statement| {
                statement
                    .execute(rusqlite::params![collection.as_str(), schema.to_canonical_string()])
            })
            .map(|_| ())
            .map_err(|_| MergeSchemaError::Storage)
    }

    fn merge_schema(&self, collection: &CollectionName) -> Option<MergeSchema> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row(
                "SELECT schema FROM merge_schemas WHERE collection = ?1",
                rusqlite::params![collection.as_str()],
                |row| row.get::<_, String>(0),
            )
            .ok()
            .and_then(|text| MergeSchema::parse_canonical(&text).ok())
    }

    fn document_merge_state(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<DocumentMergeState> {
        let reader = self.reader();
        let connection = reader.connection();
        let (body, deleted, hlc, origin, origin_sequence, commit_ulid) = connection
            .query_row(
                "SELECT body, deleted, hlc, origin, origin_seq, commit_ulid FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str()],
                |row| {
                    Ok((
                        row.get::<_, Vec<u8>>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, String>(3)?,
                        row.get::<_, i64>(4)?,
                        row.get::<_, String>(5)?,
                    ))
                },
            )
            .ok()?;
        let deleted = deleted != 0;
        let hlc = u64::from_le_bytes(hlc.to_le_bytes());
        let body =
            if deleted { None } else { decode_document_body(&body).map(|doc| doc.value().clone()) };
        Some(DocumentMergeState {
            body,
            deleted,
            hlc,
            origin,
            origin_sequence: u64::try_from(origin_sequence).unwrap_or(0),
            commit_ulid,
        })
    }

    fn document_context(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<Vec<u8>> {
        let reader = self.reader();
        reader
            .connection()
            .query_row(
                "SELECT context FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str()],
                |row| row.get::<_, Vec<u8>>(0),
            )
            .ok()
    }

    fn replication_was_superseded(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Result<bool, ()> {
        let reader = self.reader();
        replication_was_superseded_on(reader.connection(), tenant, operation)
    }

    fn record_superseded_replication(
        &mut self,
        intent: super::SupersededReplication,
    ) -> Result<bool, CommitRejection> {
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let transaction = writer.transaction().map_err(|_| CommitRejection::Storage)?;
        let bound = replication_fingerprint_on(&transaction, &intent.tenant, &intent.operation)
            .map_err(|()| CommitRejection::Storage)?;
        if bound.is_some_and(|fingerprint| fingerprint != intent.fingerprint) {
            return Err(CommitRejection::OperationIdReuse);
        }
        if replication_was_superseded_on(&transaction, &intent.tenant, &intent.operation)
            .map_err(|()| CommitRejection::Storage)?
        {
            return if bound.is_some() { Ok(true) } else { Err(CommitRejection::Storage) };
        }
        if operation_view(&transaction, &intent.tenant, &intent.operation, &intent.operation)
            .map_err(|()| CommitRejection::Storage)?
            .is_some()
        {
            return Err(CommitRejection::Conflict);
        }
        if bound.is_some() {
            return Err(CommitRejection::Storage);
        }
        if conflict_between(
            &transaction,
            &intent.tenant,
            &intent.collection,
            &intent.id,
            ExpectedVersion::Any,
            intent.snapshot_sequence.get(),
            i64::MAX as u64,
        )
        .map_err(|()| CommitRejection::Storage)?
        {
            return Err(CommitRejection::Conflict);
        }
        if let Some(conflict) = &intent.conflict {
            persist_merge_conflict(
                &transaction,
                &intent.tenant,
                &intent.collection,
                &intent.id,
                conflict,
            )
            .map_err(|()| CommitRejection::Storage)?;
        }
        transaction.execute(
            "INSERT INTO replication_bindings (tenant, operation, fingerprint) VALUES (?1, ?2, ?3)",
            rusqlite::params![intent.tenant.as_str(), intent.operation.as_str(), intent.fingerprint.as_slice()],
        ).map_err(|_| CommitRejection::Storage)?;
        transaction
            .execute(
                "INSERT INTO replication_superseded (tenant, operation) VALUES (?1, ?2)",
                rusqlite::params![intent.tenant.as_str(), intent.operation.as_str()],
            )
            .map_err(|_| CommitRejection::Storage)?;
        transaction.commit().map_err(|_| CommitRejection::Storage)?;
        Ok(false)
    }

    fn merge_conflicts_for(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
        limit: u32,
    ) -> Vec<MergeConflictEntry> {
        let reader = self.reader();
        let connection = reader.connection();
        let Ok(mut statement) = connection.prepare(
            "SELECT loser_commit_ulid, loser_hlc, loser_origin, loser_deleted,
                    winner_commit_ulid, winner_hlc, winner_origin, policy, explanation
             FROM merge_conflicts
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3
             ORDER BY loser_commit_ulid DESC LIMIT ?4",
        ) else {
            return Vec::new();
        };
        let Ok(rows) = statement.query_map(
            rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str(), i64::from(limit)],
            |row| {
                Ok(MergeConflictEntry {
                    loser_commit_ulid: row.get::<_, String>(0)?,
                    loser_hlc: u64::from_le_bytes(row.get::<_, i64>(1)?.to_le_bytes()),
                    loser_origin: row.get::<_, String>(2)?,
                    loser_deleted: row.get::<_, i64>(3)? != 0,
                    winner_commit_ulid: row.get::<_, String>(4)?,
                    winner_hlc: u64::from_le_bytes(row.get::<_, i64>(5)?.to_le_bytes()),
                    winner_origin: row.get::<_, String>(6)?,
                    policy: row.get::<_, String>(7)?,
                    explanation: row.get::<_, String>(8)?,
                })
            },
        ) else {
            return Vec::new();
        };
        rows.filter_map(Result::ok).collect()
    }

    fn merge_kernels_for(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Vec<MergeKernelEntry> {
        let reader = self.reader();
        let connection = reader.connection();
        let Ok(mut statement) = connection.prepare(
            "SELECT field, hlc, origin FROM merge_kernels
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3 ORDER BY field",
        ) else {
            return Vec::new();
        };
        let Ok(rows) = statement.query_map(
            rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str()],
            |row| {
                Ok(MergeKernelEntry {
                    field: row.get::<_, String>(0)?,
                    hlc: u64::from_le_bytes(row.get::<_, i64>(1)?.to_le_bytes()),
                    origin: row.get::<_, String>(2)?,
                })
            },
        ) else {
            return Vec::new();
        };
        rows.filter_map(Result::ok).collect()
    }

    #[allow(clippy::too_many_lines, reason = "the history union of live rows plus recorded losers")]
    fn document_history_in_collection(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
        limit: u32,
    ) -> Vec<DocumentRevisionEntry> {
        if limit == 0 {
            return Vec::new();
        }
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let connection = reader.connection();
        // Live journal rows, newest first, with the conflict-viewer facts
        // the dashboard reads directly: dot, HLC, and the policy that
        // produced the row. Origin rows state `origin`; replicated rows
        // look up their merge record by winner dot when one exists and
        // report `clean-apply` otherwise.
        let mut entries: Vec<DocumentRevisionEntry> = Vec::new();
        let Ok(mut statement) = connection.prepare(
            "SELECT revision, deleted, commit_ulid, hlc, origin,
                    CASE
                      WHEN kind = 'replicated' THEN 'replicated'
                      WHEN EXISTS (
                        SELECT 1 FROM operation_kind_markers marker
                        WHERE marker.tenant = documents.tenant
                          AND marker.operation = documents.client_request_ulid
                          AND marker.kind = 'internal_maintenance'
                      ) THEN 'internal_maintenance'
                      ELSE 'origin'
                    END AS kind
             FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3
             ORDER BY commit_seq DESC LIMIT ?4",
        ) else {
            return Vec::new();
        };
        let Ok(rows) = statement.query_map(
            rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str(), i64::from(limit)],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, String>(5)?,
                ))
            },
        ) else {
            return Vec::new();
        };
        let mut winner_lookups: Vec<(String, bool, String, i64, String, String)> = Vec::new();
        for row in rows.filter_map(Result::ok) {
            let (revision, deleted, commit_ulid, hlc, origin, kind) = row;
            winner_lookups.push((revision, deleted != 0, commit_ulid, hlc, origin, kind));
        }
        drop(statement);
        for (revision, deleted, commit_ulid, hlc_row, origin, kind) in winner_lookups {
            if entries.len() >= usize::try_from(limit).unwrap_or(usize::MAX) {
                break;
            }
            let hlc = u64::from_le_bytes(hlc_row.to_le_bytes());
            let (policy, explanation) = if kind == ROW_KIND_REPLICATED {
                connection
                    .query_row(
                        "SELECT policy, explanation FROM merge_conflicts
                         WHERE tenant = ?1 AND collection = ?2 AND id = ?3
                           AND winner_commit_ulid = ?4 LIMIT 1",
                        rusqlite::params![
                            tenant.as_str(),
                            collection.as_str(),
                            id.as_str(),
                            commit_ulid
                        ],
                        |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)),
                    )
                    .unwrap_or_else(|_| {
                        (
                            MergePolicy::CleanApply.as_str().to_owned(),
                            format!(
                                "clean apply of {commit_ulid} at hlc {hlc} from '{origin}': no prior row"
                            ),
                        )
                    })
            } else if kind == "internal_maintenance" {
                ("ttl-expiry".to_owned(), format!("TTL expiry removed {commit_ulid} at hlc {hlc}"))
            } else {
                (
                    MergePolicy::Origin.as_str().to_owned(),
                    format!("origin write {commit_ulid} at hlc {hlc}"),
                )
            };
            entries.push(DocumentRevisionEntry {
                revision,
                deleted,
                commit_ulid,
                hlc,
                origin,
                policy,
                explanation,
            });
        }
        // Recorded losers follow the live rows, newest conflict first, so
        // `db.history(id)` surfaces why each superseded version lost with
        // the same dot/HLC/policy shape.
        let remaining =
            u32::try_from(entries.len()).ok().and_then(|used| limit.checked_sub(used)).unwrap_or(0);
        if remaining == 0 {
            return entries;
        }
        let Ok(mut conflicts) = connection.prepare(
            "SELECT loser_commit_ulid, loser_hlc, loser_origin, loser_deleted,
                    winner_commit_ulid, policy, explanation
             FROM merge_conflicts
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3
             ORDER BY loser_commit_ulid DESC LIMIT ?4",
        ) else {
            return entries;
        };
        let Ok(conflict_rows) = conflicts.query_map(
            rusqlite::params![
                tenant.as_str(),
                collection.as_str(),
                id.as_str(),
                i64::from(remaining)
            ],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, String>(6)?,
                ))
            },
        ) else {
            return entries;
        };
        for row in conflict_rows.filter_map(Result::ok) {
            let (
                loser_commit_ulid,
                loser_hlc,
                loser_origin,
                loser_deleted,
                _winner_commit_ulid,
                policy,
                explanation,
            ) = row;
            entries.push(DocumentRevisionEntry {
                revision: format!("conflict-{loser_commit_ulid}"),
                deleted: loser_deleted != 0,
                commit_ulid: loser_commit_ulid,
                hlc: u64::from_le_bytes(loser_hlc.to_le_bytes()),
                origin: loser_origin,
                policy,
                explanation,
            });
        }
        entries
    }

    fn document_revision_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<String> {
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let connection = reader.connection();
        let now = unix_millis();
        connection
            .query_row(
                "SELECT deleted, expires_at, revision FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    id.as_str(),
                    sequence.get() as i64
                ],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, Option<i64>>(1)?,
                        row.get::<_, String>(2)?,
                    ))
                },
            )
            .ok()
            .filter(|(deleted, expires_at, _)| {
                row_is_live(*deleted, *expires_at, now, collection.as_str())
            })
            .map(|(_, _, revision)| revision)
    }

    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document> {
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let Ok(mut statement) = connection_statement(
            reader.connection(),
            "SELECT collection, id, deleted, expires_at, body, MAX(commit_seq) FROM documents
             WHERE tenant = ?1 AND commit_seq <= ?2
             GROUP BY collection, id ORDER BY collection, id",
        ) else {
            return Vec::new();
        };
        scan_documents(scan_rows(
            &mut statement,
            rusqlite::params![tenant.as_str(), sequence.get() as i64],
        ))
    }

    fn documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
    ) -> Vec<Document> {
        self.collection_page(sequence, tenant, collection, None, i64::from(u32::MAX))
            .unwrap_or_default()
    }

    fn documents_in_collection_at_from(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Result<Vec<Document>, SnapshotReadError> {
        self.collection_page(
            sequence,
            tenant,
            collection,
            after.map(DocumentId::as_str),
            i64::from(limit),
        )
    }

    fn collection_scan_filtered(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        after: Option<&DocumentId>,
        limit: u32,
        filter: &ScanFilter,
    ) -> Option<Result<Vec<Document>, SnapshotReadError>> {
        self.collection_scan_page(
            sequence,
            tenant,
            collection,
            after.map(DocumentId::as_str),
            i64::from(limit),
            filter,
        )
    }

    fn shadow_filtered_page(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        request: &super::ShadowReadRequest<'_>,
    ) -> Result<super::ShadowReadComparison, super::ShadowReadError> {
        self.measure_shadow_page(sequence, tenant, request)
    }

    fn begin_invisible_index(
        &mut self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &str,
        columns: &[super::InvisibleIndexColumn],
    ) -> Result<super::InvisibleIndexProgress, ()> {
        self.create_invisible_index(tenant, collection, id, columns)
    }

    fn advance_invisible_index(
        &mut self,
        tenant: &TenantId,
        id: &str,
        batch: u32,
    ) -> Result<super::InvisibleIndexProgress, ()> {
        self.backfill_invisible_index(tenant, id, batch)
    }

    fn advance_schema_ttl(
        &mut self,
        collection: &CollectionName,
        field: Option<&str>,
        batch: u32,
    ) -> Result<bool, ()> {
        self.advance_ttl(collection, field, batch)
    }

    fn register_schema_index(
        &mut self,
        tenant: &TenantId,
        collection: &CollectionName,
        name: &str,
        fields: &[String],
        unique: bool,
    ) -> Result<bool, ()> {
        self.register_canonical_index(tenant, collection, name, fields, unique)
    }

    fn remove_schema_index(
        &mut self,
        tenant: &TenantId,
        collection: &CollectionName,
        name: &str,
    ) -> Result<(), ()> {
        self.remove_canonical_index(tenant, collection, name)
    }

    fn register_index_descriptor(
        &mut self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<DescriptorRegistration, IndexDescriptorError> {
        self.register_descriptor(collection, field)
    }

    fn remove_index_descriptor(
        &mut self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<bool, super::IndexDescriptorRemovalError> {
        self.remove_descriptor(collection, field)
    }

    fn index_descriptors(&self) -> Result<Vec<DescriptorListing>, ()> {
        self.list_descriptors()
    }

    fn index_descriptor_state(&self, collection: &CollectionName, field: &str) -> Option<bool> {
        let extracted = self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        extracted.descriptors.get(&(collection.as_str().to_owned(), field.to_owned())).copied()
    }

    fn pump_index_backfills(&mut self, batch: u64) -> Result<Vec<BackfillProgress>, ()> {
        let mut progress = self.pump_backfills(batch)?;
        // The text pass rides the same governed call: one host timer
        // advances both value and text descriptors.
        progress.extend(self.pump_fts_backfills(batch)?);
        Ok(progress)
    }

    fn register_fts_descriptor(
        &mut self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<DescriptorRegistration, IndexDescriptorError> {
        self.register_fts_descriptor(collection, field)
    }

    fn remove_schema_fts(&mut self, collection: &CollectionName, field: &str) -> Result<(), ()> {
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let transaction = writer.transaction().map_err(|_| ())?;
        transaction
            .execute(
                "DELETE FROM fts_keys WHERE collection = ?1 AND field = ?2",
                rusqlite::params![collection.as_str(), field],
            )
            .map_err(|_| ())?;
        transaction
            .execute(
                "DELETE FROM fts_descriptors WHERE collection = ?1 AND field = ?2",
                rusqlite::params![collection.as_str(), field],
            )
            .map_err(|_| ())?;
        transaction.commit().map_err(|_| ())?;
        self.fts
            .lock()
            .map_err(|_| ())?
            .descriptors
            .remove(&(collection.as_str().to_owned(), field.to_owned()));
        Ok(())
    }

    fn fts_descriptor_state(&self, collection: &CollectionName, field: &str) -> Option<bool> {
        self.fts_descriptor_state(collection.as_str(), field)
    }

    fn fts_match(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        field: &str,
        query: &str,
        match_any: bool,
    ) -> Option<Result<Vec<(String, f64)>, SnapshotReadError>> {
        self.fts_match(sequence, tenant, collection, field, query, match_any)
    }

    fn expired_ttl_keys(&self, now_millis: i64, batch: u32) -> Result<Vec<ExpiredTtlKey>, ()> {
        use crate::storage::ExpiredTtlKey;
        let mut keys = Vec::new();
        for (tenant, collection, id) in self.expired_ttl_keys(now_millis, batch)? {
            let (Some(tenant), Some(collection), Some(id)) = (
                TenantId::try_new(&tenant).ok(),
                CollectionName::try_new(&collection).ok(),
                DocumentId::try_new(&id).ok(),
            ) else {
                continue;
            };
            keys.push(ExpiredTtlKey { tenant, collection, id });
        }
        Ok(keys)
    }

    fn read_documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        ids: &[DocumentId],
    ) -> Vec<Document> {
        let reader = self.reader();
        let connection = reader.connection();
        // One deferred transaction pins every point lookup in this batch to
        // one read snapshot.
        self.record_read(tenant.as_str());
        let Ok(transaction) = connection.unchecked_transaction() else {
            return Vec::new();
        };
        let mut documents: Vec<Document> = Vec::with_capacity(ids.len());
        let now = unix_millis();
        for id in ids {
            let Ok((deleted, expires_at, body)) = transaction.query_row(
                "SELECT deleted, expires_at, body FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    id.as_str(),
                    sequence.get() as i64
                ],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, Option<i64>>(1)?,
                        row.get::<_, Vec<u8>>(2)?,
                    ))
                },
            ) else {
                continue;
            };
            if row_is_live(deleted, expires_at, now, collection.as_str())
                && let Some(document) = decode_document_body(&body)
            {
                documents.push(document);
            }
        }
        let _ = transaction.rollback();
        // One collection's canonical order is identifier order, so the
        // batched keyed read and a scan of the collection agree on order.
        documents.sort_by(|left, right| left.id().cmp(right.id()));
        documents
    }

    fn documents_at_from(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Vec<Document> {
        let Some(after) = after else {
            return self.documents_at(sequence, tenant).into_iter().take(limit as usize).collect();
        };
        // The cursor-less page above already counted through `documents_at`.
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let connection = reader.connection();
        // Resolve the cursor to a canonical position: the first collection
        // holding a live copy of the identifier at this snapshot. The page
        // then resumes strictly after that key; when no collection holds
        // it, identifiers below the cursor are skipped — the root store's
        // at-least-once cursor semantics.
        let resume_collection = connection
            .query_row(
                "SELECT collection FROM documents
                 WHERE tenant = ?1 AND id = ?2 AND commit_seq <= ?3 AND deleted = 0
                   AND (expires_at IS NULL OR expires_at > ?4)
                 GROUP BY collection ORDER BY collection LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    after.as_str(),
                    sequence.get() as i64,
                    unix_millis()
                ],
                |row| row.get::<_, String>(0),
            )
            .ok();
        let Ok(mut statement) = connection_statement(
            connection,
            "SELECT collection, id, deleted, expires_at, body, MAX(commit_seq) FROM documents
             WHERE tenant = ?1 AND commit_seq <= ?2
               AND ((?3 IS NOT NULL AND (collection > ?3 OR (collection = ?3 AND id > ?4)))
                    OR (?3 IS NULL AND id >= ?4))
             GROUP BY collection, id ORDER BY collection, id LIMIT ?5",
        ) else {
            return Vec::new();
        };
        scan_documents(scan_rows(
            &mut statement,
            rusqlite::params![
                tenant.as_str(),
                sequence.get() as i64,
                resume_collection,
                after.as_str(),
                i64::from(limit)
            ],
        ))
    }

    fn documents_at_from_key(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<(&CollectionName, &DocumentId)>,
        limit: u32,
    ) -> Result<Vec<Document>, SnapshotReadError> {
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let connection = reader.connection();
        let (after_collection, after_id) =
            after.map_or(("", ""), |(collection, id)| (collection.as_str(), id.as_str()));
        let mut statement = connection_statement(
            connection,
            "SELECT collection, id, deleted, expires_at, body, MAX(commit_seq) FROM documents
             WHERE tenant = ?1 AND commit_seq <= ?2
               AND (collection > ?3 OR (collection = ?3 AND id > ?4))
             GROUP BY collection, id ORDER BY collection, id LIMIT ?5",
        )
        .map_err(|_| SnapshotReadError::Integrity)?;
        collect_checked(scan_rows(
            &mut statement,
            rusqlite::params![
                tenant.as_str(),
                sequence.get() as i64,
                after_collection,
                after_id,
                i64::from(limit)
            ],
        ))
    }

    fn snapshot_boundary_available(&self, sequence: StorageSequence) -> bool {
        // The version table retains every committed version until the
        // pruning floor advances past it; below the floor the boundary
        // fails closed instead of falling back to current state.
        sequence.get() <= self.published_state().0.get()
            && self.history_floor().is_none_or(|floor| sequence.get() >= floor)
    }

    fn winning_durabilities_at(
        &self,
        tenant: &TenantId,
        sequence: StorageSequence,
        keys: &[(&str, &str)],
    ) -> Option<Vec<LocalDurability>> {
        let reader = self.reader();
        let connection = reader.connection();
        let mut winners = vec![None; keys.len()];
        let mut statement = connection
            .prepare_cached(
                "SELECT collection, id, deleted, requested FROM documents
                 WHERE tenant = ?1 AND commit_seq <= ?2
                 ORDER BY commit_seq",
            )
            .ok()?;
        let rows =
            statement.query_map(rusqlite::params![tenant.as_str(), sequence.get() as i64], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                ))
            });
        // ponytail: O(retained history) per call; index the rows by key when
        // bulk export on this backend gets hot.
        for row in rows.ok()?.flatten() {
            let (collection, identifier, deleted, requested) = row;
            let durability = codec::durability_of(u8::try_from(requested).ok()?)?;
            for (index, (key_collection, key_identifier)) in keys.iter().enumerate() {
                if *key_collection == collection && *key_identifier == identifier {
                    winners[index] = (deleted == 0).then_some(durability);
                }
            }
        }
        winners.into_iter().collect()
    }

    fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Result<Vec<PublishedCommit>, super::JournalReadError> {
        self.read_retained_commits(tenant, after, limit, true)
    }

    fn retained_commit(
        &self,
        tenant: &TenantId,
        sequence: StorageSequence,
    ) -> Result<Option<PublishedCommit>, ()> {
        if sequence.get() == 0 {
            return Ok(None);
        }
        Ok(self
            .read_retained_commits(tenant, StorageSequence::new(sequence.get() - 1), 1, false)
            .map_err(|_| ())?
            .into_iter()
            .next()
            .filter(|commit| commit.sequence() == sequence))
    }

    fn journal_rows(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Vec<JournalRow> {
        if limit == 0 {
            return Vec::new();
        }
        let reader = self.reader();
        let connection = reader.connection();
        let Ok(mut statement) = connection.prepare(
            "SELECT commit_seq, collection, id, commit_ulid, revision, requested,
                    digest, version, deleted, committed_at, body, kind, origin,
                    origin_seq, hlc, context
             FROM documents WHERE tenant = ?1 AND commit_seq > ?2 AND commit_seq IN (
                 SELECT commit_seq FROM documents WHERE tenant = ?1 AND commit_seq > ?2
                 ORDER BY commit_seq LIMIT ?3
             )
             ORDER BY commit_seq, collection, id",
        ) else {
            return Vec::new();
        };
        let Ok(rows) = statement.query_map(
            rusqlite::params![tenant.as_str(), after.get() as i64, i64::from(limit)],
            |row| {
                let commit_sequence = row.get::<_, i64>(0)?;
                let collection = row.get::<_, String>(1)?;
                let id = row.get::<_, String>(2)?;
                let commit_ulid = row.get::<_, String>(3)?;
                let deleted = row.get::<_, i64>(8)?;
                let body = row.get::<_, Vec<u8>>(10)?;
                let origin = row.get::<_, String>(12)?;
                let origin_sequence = row.get::<_, i64>(13)?;
                let hlc = row.get::<_, i64>(14)?;
                let context = row.get::<_, Vec<u8>>(15)?;
                Ok(JournalRow {
                    commit_sequence: u64::try_from(commit_sequence).unwrap_or(0),
                    origin: origin.clone(),
                    origin_sequence: u64::try_from(origin_sequence).unwrap_or(0),
                    canonical: canonical_journal_row(
                        &collection,
                        &id,
                        &commit_ulid,
                        deleted,
                        &body,
                        &origin,
                        origin_sequence,
                        hlc,
                        &context,
                    ),
                })
            },
        ) else {
            return Vec::new();
        };
        rows.flatten().collect()
    }

    fn durable_origin_prefixes(&self, tenant: &TenantId) -> Result<Vec<AppliedFrontierEntry>, ()> {
        let reader = self.reader();
        origin_prefixes_on(reader.connection(), tenant.as_str())
    }

    fn committed_tenants(&self) -> Result<Vec<TenantId>, ()> {
        let reader = self.reader();
        let mut statement = reader.connection().prepare_cached(
            "SELECT tenant FROM documents UNION SELECT tenant FROM origin_prefixes ORDER BY tenant",
        ).map_err(|_| ())?;
        let rows = statement.query_map([], |row| row.get::<_, String>(0)).map_err(|_| ())?;
        rows.map(|row| TenantId::try_new(&row.map_err(|_| ())?).map_err(|_| ())).collect()
    }

    fn tenant_sequence(
        &self,
        tenant: &TenantId,
        at: StorageSequence,
    ) -> Result<Option<StorageSequence>, ()> {
        let at = i64::try_from(at.get()).map_err(|_| ())?;
        let reader = self.reader();
        let sequence = reader
            .connection()
            .query_row(
                "SELECT MAX(commit_seq) FROM documents WHERE tenant = ?1 AND commit_seq <= ?2",
                rusqlite::params![tenant.as_str(), at],
                |row| row.get::<_, Option<i64>>(0),
            )
            .map_err(|_| ())?;
        sequence
            .map(|value| u64::try_from(value).map(StorageSequence::new).map_err(|_| ()))
            .transpose()
    }

    fn servable_history_start(&self) -> Result<Option<StorageSequence>, ()> {
        let reader = self.reader();
        let connection = reader.connection();
        let pruned: Option<i64> = connection
            .query_row("SELECT MAX(pruned_through) FROM journal_pruning", [], |row| row.get(0))
            .map_err(|_| ())?;
        let retained_start = pruned
            .map(|value| u64::try_from(value).map(|sequence| sequence + 1).map_err(|_| ()))
            .transpose()?;
        // With no consumer registered, use the oldest retained row.
        if let Some(floor) = history_floor_on(connection)? {
            return Ok(Some(StorageSequence::new(floor.max(retained_start.unwrap_or(0)))));
        }
        if let Some(start) = retained_start {
            return Ok(Some(StorageSequence::new(start)));
        }
        let sequence = connection
            .query_row("SELECT MIN(commit_seq) FROM documents", [], |row| {
                row.get::<_, Option<i64>>(0)
            })
            .map_err(|_| ())?;
        sequence
            .map(|value| u64::try_from(value).map(StorageSequence::new).map_err(|_| ()))
            .transpose()
    }

    fn journaled_high_water(&self) -> Result<Option<StorageSequence>, ()> {
        let reader = self.reader();
        let sequence = reader
            .connection()
            .query_row("SELECT MAX(commit_seq) FROM documents", [], |row| {
                row.get::<_, Option<i64>>(0)
            })
            .map_err(|_| ())?;
        sequence
            .map(|value| u64::try_from(value).map(StorageSequence::new).map_err(|_| ()))
            .transpose()
    }

    fn published_state(&self) -> (StorageSequence, StorageRootId) {
        let reader = self.reader();
        let connection = reader.connection();
        // One statement keeps the sequence and the root head on one read
        // snapshot. The watermark is the shared sequence space's maximum —
        // commits' folded rows, usage facts, and metering rollups — matching
        // the reference engines (with rejections consuming no sequence, an
        // empty metering table leaves it exactly the last commit's sequence).
        connection
            .query_row(
                "SELECT (SELECT COALESCE(MAX(sequence), 0) FROM (
                            SELECT MAX(commit_seq) AS sequence FROM documents
                            UNION ALL
                            SELECT MAX(seq) AS sequence FROM usage_facts
                            UNION ALL
                            SELECT MAX(sequence) AS sequence FROM operation_facts
                            UNION ALL
                            SELECT MAX(seq_hi) AS sequence FROM metering_db_facts)),
                        (SELECT value FROM meta WHERE key = 'root_head')",
                [],
                |row| {
                    let sequence = row.get::<_, i64>(0)?;
                    let root = row.get::<_, String>(1)?;
                    Ok((sequence, root))
                },
            )
            .ok()
            .and_then(|(sequence, root)| {
                Some((
                    StorageSequence::new(u64::try_from(sequence).ok()?),
                    StorageRootId::from_recorded(root),
                ))
            })
            .unwrap_or_else(|| {
                // The trait predates typed read errors.  An empty root is an
                // explicit fail-closed marker; never manufacture the initial
                // root or sequence zero from a failed/corrupt query.
                (StorageSequence::new(0), StorageRootId::from_recorded(String::new()))
            })
    }

    fn advance_backup_frontier(&mut self, sequence: u64) -> Result<(), ()> {
        let sequence = i64::try_from(sequence).map_err(|_| ())?;
        let writer = self.writer.lock().map_err(|_| ())?;
        writer
            .execute(
                "INSERT INTO consumer_frontiers (name, seq) VALUES ('native-backup', ?1)
             ON CONFLICT(name) DO UPDATE SET seq = MAX(seq, excluded.seq)",
                [sequence],
            )
            .map(|_| ())
            .map_err(|_| ())
    }

    fn run_retention_sweep(&mut self) -> Result<(), ()> {
        self.prune_below_consumer_frontiers()?;
        let writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        writer.query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |_| Ok(())).map_err(|_| ())
    }

    fn flush_pending_flips(&mut self) {
        // The between-waves checkpoint: the group-commit leader calls this
        // off the commit path once it goes quiet, so with
        // `wal_autocheckpoint = 0` this passive checkpoint is what keeps
        // the WAL bounded between retention sweeps' truncating ones.
        let _ = self.flush();
    }

    fn flush(&mut self) -> Result<(), ()> {
        // A passive checkpoint folds what it can into the database file;
        // the WAL itself is durable, so a busy checkpoint is not an error.
        let writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        writer.query_row("PRAGMA wal_checkpoint(PASSIVE)", [], |_| Ok(())).map_err(|_| ())
    }

    #[cfg(test)]
    fn arm_test_fault(&mut self, point: super::FaultPoint) {
        let mut faults = self.faults.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        faults.arm(point);
    }
}

impl SqliteStorage {
    /// One bounded collection window at a fixed snapshot, in canonical
    /// identifier order, resuming strictly after the cursor. Identifiers
    /// are never empty, so `id > COALESCE(?4, '')` is exactly "no cursor".
    fn collection_page(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        after: Option<&str>,
        limit: i64,
    ) -> Result<Vec<Document>, SnapshotReadError> {
        self.record_read(tenant.as_str());
        let reader = self.reader();
        let connection = reader.connection();
        let mut statement = connection
            .prepare_cached(
                "SELECT id, deleted, expires_at, body, MAX(commit_seq) FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND commit_seq <= ?3
                   AND id > COALESCE(?4, '')
                 GROUP BY id ORDER BY id LIMIT ?5",
            )
            .map_err(|_| SnapshotReadError::Integrity)?;
        let rows = statement
            .query_map(
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    sequence.get() as i64,
                    after,
                    limit
                ],
                |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, Option<i64>>(2)?,
                        row.get::<_, Vec<u8>>(3)?,
                    ))
                },
            )
            .map_err(|_| SnapshotReadError::Integrity)?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| SnapshotReadError::Integrity)?;
        let now = unix_millis();
        let mut documents = Vec::with_capacity(rows.len());
        for (id, deleted, expires_at, body) in rows {
            if !row_is_live(deleted, expires_at, now, collection.as_str()) {
                continue;
            }
            let document = decode_document_body(&body).ok_or(SnapshotReadError::Integrity)?;
            if document.collection() != collection || document.id().as_str() != id {
                return Err(SnapshotReadError::Integrity);
            }
            documents.push(document);
        }
        Ok(documents)
    }
}

/// Prepares one cached scan statement.
fn connection_statement<'a>(
    connection: &'a Connection,
    sql: &str,
) -> Result<rusqlite::CachedStatement<'a>, SnapshotReadError> {
    connection.prepare_cached(sql).map_err(|_| SnapshotReadError::Integrity)
}

// ---------------------------------------------------------------------------
// Extracted index columns (D-31/D-42): registration, write-time extraction,
// and the pushed-down collection scan.
// ---------------------------------------------------------------------------

impl SqliteStorage {
    /// Registers one extracted index-column descriptor, adding the stored
    /// columns, backfilling inline inside the cost cap, and creating the
    /// ordinary indexes (D-42: extraction in Rust at write time, never
    /// `SQLite` expression or generated columns).
    #[allow(clippy::too_many_lines, reason = "one registration transaction, start to finish")]
    fn register_descriptor(
        &mut self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<DescriptorRegistration, IndexDescriptorError> {
        if field.is_empty()
            || field.len() > MAX_DESCRIPTOR_FIELD_BYTES
            || field.chars().any(char::is_control)
            || field != field.trim()
        {
            return Err(IndexDescriptorError::Storage);
        }
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if self
            .extracted
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .descriptors
            .contains_key(&(collection.as_str().to_owned(), field.to_owned()))
        {
            return Ok(DescriptorRegistration::Unchanged);
        }
        let transaction = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| IndexDescriptorError::Storage)?;
        let field_is_new = transaction
            .query_row(
                "SELECT COUNT(*) FROM index_descriptors WHERE field = ?1",
                rusqlite::params![field],
                |row| row.get::<_, i64>(0),
            )
            .map_err(|_| IndexDescriptorError::Storage)?
            == 0;
        if field_is_new {
            let state = self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            if state.fields.len() >= MAX_EXTRACTED_FIELDS {
                return Err(IndexDescriptorError::ColumnBudgetExceeded);
            }
            let alter = format!(
                "ALTER TABLE documents ADD COLUMN {text} TEXT; ALTER TABLE documents ADD COLUMN {number} NUMERIC;",
                text = quote_identifier(&text_column(field)),
                number = quote_identifier(&numeric_column(field)),
            );
            transaction.execute_batch(&alter).map_err(|_| IndexDescriptorError::Storage)?;
        }
        // The registration watermark: rows at or below it predate the
        // descriptor, so the write path never gave them extracted values.
        let watermark =
            durable_watermark(&transaction).map_err(|()| IndexDescriptorError::Storage)?;
        let live_rows: i64 = transaction
            .query_row(
                "SELECT COUNT(*) FROM documents WHERE collection = ?1 AND commit_seq <= ?2 AND deleted = 0",
                rusqlite::params![collection.as_str(), watermark as i64],
                |row| row.get::<_, i64>(0),
            )
            .map_err(|_| IndexDescriptorError::Storage)?;
        let mut state = DescriptorRegistration::Building;
        if u64::try_from(live_rows).unwrap_or(u64::MAX) <= INLINE_BACKFILL_ROW_CAP {
            // The cost-capped inline build: every pre-registration row of
            // the collection gets its extracted values inside this
            // transaction, so the descriptor is active the moment
            // registration commits — D-31's zero-stale-reads rule.
            backfill_collection_inline(&transaction, collection, field, watermark)
                .map_err(|()| IndexDescriptorError::Storage)?;
            state = DescriptorRegistration::Active;
        }
        // The governed backfill's window: rows at or below the watermark
        // predate the descriptor. An inline build leaves no window (the
        // cursor starts at the watermark); a larger collection leaves the
        // whole window to the bounded pass, which advances the cursor one
        // batch at a time.
        let (backfill_below, backfill_cursor) = if state == DescriptorRegistration::Active {
            (watermark, watermark)
        } else {
            (watermark, 0)
        };
        transaction
            .prepare_cached(
                "INSERT INTO index_descriptors
                 (collection, field, kind, state, created_at, backfill_below, backfill_cursor)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
                 ON CONFLICT(collection, field) DO UPDATE SET
                    state = excluded.state,
                    backfill_below = excluded.backfill_below,
                    backfill_cursor = excluded.backfill_cursor",
            )
            .and_then(|mut statement| {
                statement.execute(rusqlite::params![
                    collection.as_str(),
                    field,
                    DESCRIPTOR_KIND_VALUE,
                    if state == DescriptorRegistration::Active {
                        DESCRIPTOR_STATE_ACTIVE
                    } else {
                        DESCRIPTOR_STATE_BUILDING
                    },
                    unix_millis(),
                    backfill_below as i64,
                    backfill_cursor as i64,
                ])
            })
            .map_err(|_| IndexDescriptorError::Storage)?;
        // The ordinary indexes cover the columns from the moment the
        // descriptor is active. A building descriptor takes its indexes
        // only when the governed backfill completes it — creating them
        // earlier would index a half-empty column while rows still miss.
        if state == DescriptorRegistration::Active {
            create_descriptor_indexes(&transaction, field)
                .map_err(|_| IndexDescriptorError::Storage)?;
        }
        transaction.commit().map_err(|_| IndexDescriptorError::Storage)?;
        let mut extracted =
            self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        extracted.descriptors.insert(
            (collection.as_str().to_owned(), field.to_owned()),
            state == DescriptorRegistration::Active,
        );
        if field_is_new {
            extracted.fields.push(field.to_owned());
            extracted.refresh_put_sql();
        }
        Ok(state)
    }

    /// Lists the durable index descriptors with their activity and
    /// remaining governed-backfill work.
    fn list_descriptors(&self) -> Result<Vec<DescriptorListing>, ()> {
        let reader = self.reader();
        list_descriptors_on(reader.connection())
    }

    /// Advances every building descriptor's governed backfill by one
    /// bounded batch of rows.
    fn pump_backfills(&mut self, batch: u64) -> Result<Vec<BackfillProgress>, ()> {
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let progress = pump_backfills_on(&mut writer, batch)?;
        // The in-memory view follows the durable state: a descriptor the
        // pass completed pushes from now on.
        let mut extracted =
            self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        for step in &progress {
            if step.active {
                extracted.descriptors.insert((step.collection.clone(), step.field.clone()), true);
            }
        }
        Ok(progress)
    }

    /// Serves one filtered collection page with the conditions pushed onto
    /// the active descriptors' stored columns (D-42). Conditions over
    /// inactive or unregistered fields drop out of the SQL — a sound
    /// narrowing, never an exact filter — and when nothing pushes the
    /// answer is `None` so the caller takes the unfiltered path.
    fn collection_scan_page(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        after: Option<&str>,
        limit: i64,
        filter: &ScanFilter,
    ) -> Option<Result<Vec<Document>, SnapshotReadError>> {
        self.record_read(tenant.as_str());
        let extracted = self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let (conditions, values) = pushed_filter(&extracted, collection, filter);
        if conditions.is_empty() {
            return None;
        }
        let sql = filtered_page_sql(&conditions);
        let mut parameters: Vec<rusqlite::types::Value> = Vec::with_capacity(values.len() + 5);
        parameters.push(Value::Text(tenant.as_str().to_owned()));
        parameters.push(Value::Text(collection.as_str().to_owned()));
        parameters.push(Value::Integer(sequence.get() as i64));
        parameters.push(after.map_or(Value::Null, |after| Value::Text(after.to_owned())));
        parameters.extend(values);
        parameters.push(Value::Integer(limit));
        let reader = self.reader();
        let connection = reader.connection();
        let mut statement = connection.prepare_cached(&sql).ok()?;
        let rows = statement
            .query_map(rusqlite::params_from_iter(parameters.iter()), |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, Option<i64>>(2)?,
                    row.get::<_, Vec<u8>>(3)?,
                ))
            })
            .ok()?;
        let rows = rows.collect::<Result<Vec<_>, _>>().ok()?;
        let now = unix_millis();
        let mut documents = Vec::with_capacity(rows.len());
        for (id, deleted, expires_at, body) in rows {
            if !row_is_live(deleted, expires_at, now, collection.as_str()) {
                continue;
            }
            let Some(document) = decode_document_body(&body) else {
                return Some(Err(SnapshotReadError::Integrity));
            };
            if document.collection() != collection || document.id().as_str() != id {
                // A sound narrowing may over-match, never mis-decode: a
                // body that contradicts its key is an integrity failure.
                return Some(Err(SnapshotReadError::Integrity));
            }
            documents.push(document);
        }
        Some(Ok(documents))
    }
}

fn pushed_filter(
    extracted: &ExtractedState,
    collection: &CollectionName,
    filter: &ScanFilter,
) -> (Vec<String>, Vec<Value>) {
    let mut conditions: Vec<String> = Vec::new();
    let mut values: Vec<rusqlite::types::Value> = Vec::new();
    for condition in filter.conditions() {
        if !extracted.is_active(collection.as_str(), &condition.field) {
            continue;
        }
        let (column, value) = match &condition.value {
            DocumentValue::String(text) => {
                (text_column(&condition.field), Value::Text(text.clone()))
            }
            DocumentValue::Integer(integer) => {
                (numeric_column(&condition.field), Value::Integer(*integer))
            }
            DocumentValue::Number(number) => {
                (numeric_column(&condition.field), Value::Real(*number))
            }
            // Booleans, nulls, and structured values never push: the
            // columns do not carry them, and the executor's own
            // predicate stays the exact filter.
            _ => continue,
        };
        let column = quote_identifier(&column);
        conditions.push(match condition.op {
            ScanOp::Equals => format!("{column} = ?"),
            // A NULL column (absent field, structured value) must
            // survive a negation: SQL's three-valued logic would drop
            // it, the executor's predicate may keep it.
            ScanOp::NotEquals => format!("({column} IS NULL OR {column} <> ?)"),
            ScanOp::Less => format!("{column} < ?"),
            ScanOp::LessOrEqual => format!("{column} <= ?"),
            ScanOp::Greater => format!("{column} > ?"),
            ScanOp::GreaterOrEqual => format!("{column} >= ?"),
        });
        values.push(value);
    }
    (conditions, values)
}

/// Field predicates must run on candidate versions before the latest-version
/// probe so SQLite can seek an extracted-column index. The probe must ignore
/// those predicates: a newer nonmatching version or tombstone still hides an
/// older matching row at the requested snapshot.
fn filtered_page_sql(conditions: &[String]) -> String {
    format!(
        "SELECT current.id, current.deleted, current.expires_at, current.body
         FROM documents AS current
         WHERE current.tenant = ?1 AND current.collection = ?2 AND current.commit_seq <= ?3
           AND current.id > COALESCE(?4, '') AND current.deleted = 0 AND {}
           AND NOT EXISTS (
             SELECT 1 FROM documents AS newer
             WHERE newer.tenant = current.tenant AND newer.collection = current.collection
               AND newer.id = current.id AND newer.commit_seq > current.commit_seq
               AND newer.commit_seq <= ?3
           )
         ORDER BY current.id LIMIT ?",
        conditions.join(" AND ")
    )
}

/// Lists the durable index descriptors with their activity and remaining
/// governed-backfill work.
fn list_descriptors_on(connection: &Connection) -> Result<Vec<DescriptorListing>, ()> {
    let mut statement = connection
        .prepare(
            "SELECT collection, field, state, backfill_below, backfill_cursor
             FROM index_descriptors ORDER BY collection, field",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, i64>(4)?,
            ))
        })
        .map_err(|_| ())?;
    let mut listings = Vec::new();
    for row in rows {
        let (collection, field, state, below, cursor) = row.map_err(|_| ())?;
        let below = u64::try_from(below).unwrap_or(0);
        let cursor = u64::try_from(cursor).unwrap_or(0);
        listings.push(DescriptorListing {
            collection,
            field,
            active: state == DESCRIPTOR_STATE_ACTIVE,
            backfill_remaining: below.saturating_sub(cursor),
        });
    }
    Ok(listings)
}

/// Advances every building descriptor's governed backfill by one bounded
/// batch of rows (D-31), completing a descriptor — backfill done, state
/// active, ordinary indexes created — when its cursor reaches its
/// watermark. Idempotent and resumable: the cursor is durable state.
fn pump_backfills_on(writer: &mut Connection, batch: u64) -> Result<Vec<BackfillProgress>, ()> {
    let building = {
        let transaction =
            writer.transaction_with_behavior(TransactionBehavior::Deferred).map_err(|_| ())?;
        let mut statement = transaction
            .prepare(
                "SELECT collection, field, backfill_below, backfill_cursor
                 FROM index_descriptors WHERE state = ?1 ORDER BY collection, field",
            )
            .map_err(|_| ())?;
        let rows = statement
            .query_map(rusqlite::params![DESCRIPTOR_STATE_BUILDING], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                ))
            })
            .map_err(|_| ())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?;
        drop(statement);
        transaction.commit().map_err(|_| ())?;
        rows
    };
    let mut progress = Vec::new();
    for (collection_name, field, below, cursor) in building {
        let collection = CollectionName::try_new(&collection_name).map_err(|_| ())?;
        let below = u64::try_from(below).unwrap_or(0);
        let mut cursor = u64::try_from(cursor).unwrap_or(0);
        if below > cursor {
            let transaction =
                writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
            cursor = backfill_batch(&transaction, &collection, &field, cursor, below, batch)?;
            let active = cursor >= below;
            transaction
                .prepare_cached(
                    "UPDATE index_descriptors SET state = ?3, backfill_cursor = ?4
                     WHERE collection = ?1 AND field = ?2",
                )
                .and_then(|mut statement| {
                    statement.execute(rusqlite::params![
                        collection_name,
                        field,
                        if active { DESCRIPTOR_STATE_ACTIVE } else { DESCRIPTOR_STATE_BUILDING },
                        cursor as i64,
                    ])
                })
                .map_err(|_| ())?;
            if active {
                create_descriptor_indexes(&transaction, &field).map_err(|_| ())?;
            }
            transaction.commit().map_err(|_| ())?;
        }
        progress.push(BackfillProgress {
            collection: collection_name,
            field,
            remaining: below.saturating_sub(cursor),
            active: cursor >= below,
        });
    }
    Ok(progress)
}

/// Backfills one bounded batch of a descriptor's window and returns the
/// advanced cursor.
///
/// Batches are sequence-atomic: one intent writes all its rows under one
/// `commit_seq`, so stopping mid-sequence and advancing the cursor past it
/// would strand the sequence's remaining rows with NULL columns behind an
/// active descriptor — a silent-miss stale read. The frontier probe finds
/// the batch-th row's sequence and the pass drains every row at or below
/// it: the batch bound holds exactly for many small intents and softens by
/// one over-large sequence, which correctness requires.
fn backfill_batch(
    transaction: &rusqlite::Transaction<'_>,
    collection: &CollectionName,
    field: &str,
    cursor: u64,
    below: u64,
    batch: u64,
) -> Result<u64, ()> {
    let batch = batch.max(1);
    let frontier: u64 = transaction
        .query_row(
            "SELECT commit_seq FROM documents
             WHERE collection = ?1 AND commit_seq > ?2 AND commit_seq <= ?3
             ORDER BY commit_seq LIMIT 1 OFFSET ?4",
            rusqlite::params![
                collection.as_str(),
                cursor as i64,
                below as i64,
                (batch.saturating_sub(1)) as i64,
            ],
            |row| row.get::<_, i64>(0),
        )
        .optional()
        .map_err(|_| ())?
        .map_or(below, |sequence| u64::try_from(sequence).unwrap_or(below).min(below));
    let frontier = frontier.max(cursor);
    let text = quote_identifier(&text_column(field));
    let number = quote_identifier(&numeric_column(field));
    let mut statement = transaction
        .prepare_cached(
            "SELECT tenant, id, commit_seq, deleted, body FROM documents
             WHERE collection = ?1 AND commit_seq > ?2 AND commit_seq <= ?3
             ORDER BY commit_seq",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map(rusqlite::params![collection.as_str(), cursor as i64, frontier as i64], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, Vec<u8>>(4)?,
            ))
        })
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    for (tenant, id, commit_seq, deleted, body) in rows {
        if deleted != 0 {
            continue;
        }
        let Some(document) = decode_document_body(&body) else { continue };
        let (text_value, number_value) = match extract_column_value(&document, field) {
            Some(ColumnValue::Text(text)) => (Value::Text(text), Value::Null),
            Some(ColumnValue::Typed(bytes)) => (Value::Blob(bytes), Value::Null),
            Some(ColumnValue::Integer(integer)) => (Value::Null, Value::Integer(integer)),
            Some(ColumnValue::Real(real)) => (Value::Null, Value::Real(real)),
            None => (Value::Null, Value::Null),
        };
        transaction
            .prepare_cached(&format!(
                "UPDATE documents SET {text} = ?5, {number} = ?6
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq = ?4"
            ))
            .and_then(|mut statement| {
                statement.execute(rusqlite::params![
                    tenant,
                    collection.as_str(),
                    id,
                    commit_seq,
                    text_value,
                    number_value,
                ])
            })
            .map_err(|_| ())?;
    }
    // The drain above covers whole sequences only, so the frontier is the
    // honest cursor: every row at or below it carries extracted values.
    Ok(frontier)
}

/// Loads the descriptor view from the durable table at open.
fn load_extracted_state(connection: &Connection, state: &mut ExtractedState) -> Result<(), ()> {
    let mut statement = connection
        .prepare(
            "SELECT collection, field, state FROM index_descriptors ORDER BY collection, field",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?, row.get::<_, i64>(2)?))
        })
        .map_err(|_| ())?;
    for row in rows {
        let (collection, field, active) = row.map_err(|_| ())?;
        state
            .descriptors
            .insert((collection.clone(), field.clone()), active == DESCRIPTOR_STATE_ACTIVE);
        if !state.fields.contains(&field) {
            state.fields.push(field);
        }
    }
    state.refresh_put_sql();
    Ok(())
}

/// Loads the full-text descriptor view from the durable table at open.
fn load_fts_state(connection: &Connection, state: &mut FtsState) -> Result<(), ()> {
    let mut statement = connection
        .prepare("SELECT collection, field, state FROM fts_descriptors ORDER BY collection, field")
        .map_err(|_| ())?;
    let rows = statement
        .query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?, row.get::<_, i64>(2)?))
        })
        .map_err(|_| ())?;
    for row in rows {
        let (collection, field, active) = row.map_err(|_| ())?;
        state.descriptors.insert((collection, field), active == DESCRIPTOR_STATE_ACTIVE);
    }
    Ok(())
}

/// Creates the ordinary indexes covering one descriptor's stored columns.
fn create_descriptor_indexes(connection: &Connection, field: &str) -> Result<(), rusqlite::Error> {
    let text_index = quote_identifier(&format!("i:{field}#t"));
    let text_col = format!("({})", quote_identifier(&text_column(field)));
    let number_index = quote_identifier(&format!("i:{field}#n"));
    let number_col = format!("({})", quote_identifier(&numeric_column(field)));
    let indexes = format!(
        "CREATE INDEX IF NOT EXISTS {text_index} ON documents {text_col};
         CREATE INDEX IF NOT EXISTS {number_index} ON documents {number_col};",
    );
    connection.execute_batch(&indexes)
}

/// Backfills one descriptor's extracted columns over a collection's
/// pre-registration rows, inside the registration transaction.
fn backfill_collection_inline(
    transaction: &rusqlite::Transaction<'_>,
    collection: &CollectionName,
    field: &str,
    watermark: u64,
) -> Result<(), ()> {
    let text = quote_identifier(&text_column(field));
    let number = quote_identifier(&numeric_column(field));
    let update = format!(
        "UPDATE documents SET {text} = ?5, {number} = ?6
         WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq = ?4"
    );
    let mut statement = transaction
        .prepare_cached(
            "SELECT tenant, id, commit_seq, deleted, body FROM documents
             WHERE collection = ?1 AND commit_seq <= ?2",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map(rusqlite::params![collection.as_str(), watermark as i64], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, Vec<u8>>(4)?,
            ))
        })
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    for (tenant, id, commit_seq, deleted, body) in rows {
        // A tombstone row carries no document body to extract; its columns
        // stay NULL and never surface (the scan's `deleted = 0` filter).
        if deleted != 0 {
            continue;
        }
        let Some(document) = decode_document_body(&body) else { continue };
        // One extracted scalar lands in its own column; the other stays
        // NULL, exactly as the write path binds a fresh put.
        let (text_value, number_value) = match extract_column_value(&document, field) {
            Some(ColumnValue::Text(text)) => (Value::Text(text), Value::Null),
            Some(ColumnValue::Typed(bytes)) => (Value::Blob(bytes), Value::Null),
            Some(ColumnValue::Integer(integer)) => (Value::Null, Value::Integer(integer)),
            Some(ColumnValue::Real(real)) => (Value::Null, Value::Real(real)),
            None => (Value::Null, Value::Null),
        };
        transaction
            .prepare_cached(&update)
            .and_then(|mut statement| {
                statement.execute(rusqlite::params![
                    tenant,
                    collection.as_str(),
                    id,
                    commit_seq,
                    text_value,
                    number_value,
                ])
            })
            .map_err(|_| ())?;
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Full-text search (S-02 slice D, D-39): text descriptors, the governed
// text backfill, and the FTS5 candidate read.
// ---------------------------------------------------------------------------

/// Validates one text-descriptor field name: the same discipline as a
/// value descriptor (D-42's stored-column rule).
fn valid_fts_field(field: &str) -> bool {
    !field.is_empty()
        && field.len() <= MAX_DESCRIPTOR_FIELD_BYTES
        && !field.chars().any(char::is_control)
        && field == field.trim()
}

impl SqliteStorage {
    /// Registers one full-text descriptor: the named field of the named
    /// collection gains FTS5 rows written by the Rust write path from the
    /// next commit on. Collections inside the inline cost cap backfill
    /// synchronously and go active at once; larger collections stay
    /// building until the governed text pass completes them, and search
    /// conditions over a building field never narrow (a scan over a
    /// half-indexed term stream would silently miss rows).
    pub(crate) fn register_fts_descriptor(
        &mut self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<DescriptorRegistration, IndexDescriptorError> {
        if !valid_fts_field(field) {
            return Err(IndexDescriptorError::Storage);
        }
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if self
            .fts
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .descriptors
            .contains_key(&(collection.as_str().to_owned(), field.to_owned()))
        {
            return Ok(DescriptorRegistration::Unchanged);
        }
        let transaction = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| IndexDescriptorError::Storage)?;
        let watermark =
            durable_watermark(&transaction).map_err(|()| IndexDescriptorError::Storage)?;
        let live_rows: i64 = transaction
            .query_row(
                "SELECT COUNT(*) FROM documents WHERE collection = ?1 AND commit_seq <= ?2 AND deleted = 0",
                rusqlite::params![collection.as_str(), watermark as i64],
                |row| row.get::<_, i64>(0),
            )
            .map_err(|_| IndexDescriptorError::Storage)?;
        let mut state = DescriptorRegistration::Building;
        if u64::try_from(live_rows).unwrap_or(u64::MAX) <= INLINE_BACKFILL_ROW_CAP {
            backfill_fts_inline(&transaction, collection, field, watermark)
                .map_err(|()| IndexDescriptorError::Storage)?;
            state = DescriptorRegistration::Active;
        }
        let (backfill_below, backfill_cursor) = if state == DescriptorRegistration::Active {
            (watermark, watermark)
        } else {
            (watermark, 0)
        };
        transaction
            .prepare_cached(
                "INSERT INTO fts_descriptors
                 (collection, field, state, created_at, backfill_below, backfill_cursor)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6)
                 ON CONFLICT(collection, field) DO UPDATE SET
                    state = excluded.state,
                    backfill_below = excluded.backfill_below,
                    backfill_cursor = excluded.backfill_cursor",
            )
            .and_then(|mut statement| {
                statement.execute(rusqlite::params![
                    collection.as_str(),
                    field,
                    if state == DescriptorRegistration::Active {
                        DESCRIPTOR_STATE_ACTIVE
                    } else {
                        DESCRIPTOR_STATE_BUILDING
                    },
                    unix_millis(),
                    backfill_below as i64,
                    backfill_cursor as i64,
                ])
            })
            .map_err(|_| IndexDescriptorError::Storage)?;
        transaction.commit().map_err(|_| IndexDescriptorError::Storage)?;
        self.fts.lock().unwrap_or_else(std::sync::PoisonError::into_inner).descriptors.insert(
            (collection.as_str().to_owned(), field.to_owned()),
            state == DescriptorRegistration::Active,
        );
        Ok(state)
    }

    /// Reports one field's text-descriptor state without registering
    /// anything: `Some(true)` narrows, `Some(false)` is building, `None`
    /// is absent.
    pub(crate) fn fts_descriptor_state(&self, collection: &str, field: &str) -> Option<bool> {
        self.fts
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .descriptors
            .get(&(collection.to_owned(), field.to_owned()))
            .copied()
    }

    /// Advances every building text descriptor by one bounded batch of
    /// rows, completing a descriptor when its cursor reaches its
    /// registration watermark. Idempotent and resumable through the
    /// durable cursor; sequence-atomic like the value pass.
    pub(crate) fn pump_fts_backfills(&mut self, batch: u64) -> Result<Vec<BackfillProgress>, ()> {
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let progress = pump_fts_backfills_on(&mut writer, batch)?;
        let mut fts = self.fts.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        for step in &progress {
            if step.active {
                fts.descriptors.insert((step.collection.clone(), step.field.clone()), true);
            }
        }
        Ok(progress)
    }

    /// Reads the FTS5 candidate set of one search: the matching document
    /// ids of one collection field with their relevance scores (higher
    /// ranks first), or `None` when the index cannot narrow this read.
    ///
    /// `None` is the sound fallback for every non-narrowable shape — no
    /// active descriptor, a snapshot behind the tip (the per-key FTS rows
    /// track the tip, so an older snapshot must take the exact scan), a
    /// vacuous or pure-exclusion query with no FTS5 spelling, or any
    /// storage hiccup — and the caller falls back to the exact scan, which
    /// re-checks every candidate through the same term space. A narrowing
    /// can therefore only ever over-approximate; the executor's exact
    /// predicate stays the filter.
    pub(crate) fn fts_match(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        field: &str,
        query: &str,
        match_any: bool,
    ) -> Option<Result<Vec<(String, f64)>, SnapshotReadError>> {
        self.record_read(tenant.as_str());
        if self.fts_descriptor_state(collection.as_str(), field) != Some(true) {
            return None;
        }
        let parsed = crate::query::parse_search_query(query, match_any);
        if parsed.must().is_empty() && parsed.must_not().is_empty() {
            // A vacuous query matches nothing exactly, so the empty set is
            // the exact answer — no scan needed.
            return Some(Ok(Vec::new()));
        }
        let matched = crate::query::sanitize_to_fts5_match(&parsed)?;
        let reader = self.reader();
        let connection = reader.connection();
        // The tip guard: FTS rows track the newest version of each key, so
        // only a tip snapshot may narrow through them.
        let tip = connection
            .query_row(
                "SELECT COALESCE(MAX(commit_seq), 0) FROM documents WHERE tenant = ?1",
                rusqlite::params![tenant.as_str()],
                |row| row.get::<_, i64>(0),
            )
            .ok()
            .and_then(|tip| u64::try_from(tip).ok())?;
        if sequence.get() < tip {
            return None;
        }
        let mut statement = connection
            .prepare_cached(
                "SELECT doc_id, bm25(fts_docs) FROM fts_docs
                 WHERE fts_docs MATCH ?1 AND tenant = ?2 AND collection = ?3 AND field = ?4",
            )
            .ok()?;
        let rows = statement
            .query_map(
                rusqlite::params![matched, tenant.as_str(), collection.as_str(), field],
                |row| Ok((row.get::<_, String>(0)?, row.get::<_, f64>(1)?)),
            )
            .ok()?
            .collect::<Result<Vec<_>, _>>()
            .ok()?;
        // `bm25` ranks ascending (lower is better); the reported score
        // ascends with relevance so `order by score() desc` reads
        // naturally.
        let mut candidates = rows
            .into_iter()
            .map(|(id, rank)| {
                let score = if rank.is_finite() { -rank } else { 0.0 };
                (id, score)
            })
            .collect::<Vec<_>>();
        candidates.sort_by(|left, right| {
            right
                .1
                .partial_cmp(&left.1)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| left.0.cmp(&right.0))
        });
        Some(Ok(candidates))
    }

    /// Returns the live keys whose TTL expiry has passed, bounded by
    /// `batch`: the sweep's work list. History rows, tombstones, and
    /// unexpired keys never appear; the newest version of each key
    /// decides, exactly as the live reads resolve it. Family records never
    /// appear either: the reserved collection owns `expires_at` itself.
    pub(crate) fn expired_ttl_keys(
        &self,
        now_millis: i64,
        batch: u32,
    ) -> Result<Vec<(String, String, String)>, ()> {
        let reader = self.reader();
        let mut statement = reader
            .connection()
            .prepare_cached(
                "SELECT tenant, collection, id, MAX(commit_seq) FROM documents
                 WHERE collection != ?3
                 GROUP BY tenant, collection, id
                 HAVING deleted = 0 AND expires_at IS NOT NULL AND expires_at <= ?1
                 LIMIT ?2",
            )
            .map_err(|_| ())?;
        let rows = statement
            .query_map(
                rusqlite::params![now_millis, i64::from(batch), RESERVED_FAMILY_COLLECTION],
                |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, String>(2)?,
                    ))
                },
            )
            .map_err(|_| ())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?;
        Ok(rows)
    }
}

/// Backfills one text descriptor over a collection's pre-registration rows,
/// inside the registration transaction: per key, the newest row at or
/// below the watermark decides — a live body contributes its term stream,
/// a tombstone clears the key — and a key with a newer row anywhere is
/// skipped, because the write path already maintains it.
fn backfill_fts_inline(
    transaction: &rusqlite::Transaction<'_>,
    collection: &CollectionName,
    field: &str,
    watermark: u64,
) -> Result<(), ()> {
    let mut statement = transaction
        .prepare_cached(
            "SELECT tenant, id, MAX(commit_seq) FROM documents
             WHERE collection = ?1 AND commit_seq <= ?2
             GROUP BY tenant, id",
        )
        .map_err(|_| ())?;
    let keys = statement
        .query_map(rusqlite::params![collection.as_str(), watermark as i64], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?, row.get::<_, i64>(2)?))
        })
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    for (tenant, id, _) in keys {
        backfill_fts_key(transaction, &tenant, collection.as_str(), field, &id, watermark)?;
    }
    Ok(())
}

/// Backfills one key's FTS row from the key's newest row at or below the
/// descriptor's watermark. A newer row anywhere above the watermark means
/// the write path already maintains the key and the backfill skips it:
/// indexing the older body would clobber the newer term stream.
fn backfill_fts_key(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &str,
    collection: &str,
    field: &str,
    id: &str,
    watermark: u64,
) -> Result<(), ()> {
    let newer: i64 = transaction
        .query_row(
            "SELECT COUNT(*) FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq > ?4",
            rusqlite::params![tenant, collection, id, watermark as i64],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|_| ())?;
    if newer > 0 {
        return Ok(());
    }
    let (deleted, body) = transaction
        .query_row(
            "SELECT deleted, body FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
             ORDER BY commit_seq DESC LIMIT 1",
            rusqlite::params![tenant, collection, id, watermark as i64],
            |row| Ok((row.get::<_, i64>(0)?, row.get::<_, Vec<u8>>(1)?)),
        )
        .map_err(|_| ())?;
    transaction
        .prepare_cached(
            "DELETE FROM fts_keys WHERE tenant = ?1 AND collection = ?2 AND field = ?3 AND doc_id = ?4",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![tenant, collection, field, id])
        })
        .map(|_| ())
        .map_err(|_| ())?;
    if deleted != 0 {
        return Ok(());
    }
    let Some(body) = decode_document_body(&body) else { return Ok(()) };
    if body.collection().as_str() != collection || body.id().as_str() != id {
        return Err(());
    }
    if let Some(text) = extract_fts_text(&body, field) {
        insert_fts_row(transaction, tenant, collection, field, id, &text)?;
    }
    Ok(())
}

/// Advances every building text descriptor by one bounded batch of keys.
///
/// Batches are sequence-atomic like the value pass: the frontier probe
/// finds the batch-th row's sequence and the pass drains every key with a
/// row at or below it, so the cursor only ever rests on a whole-sequence
/// boundary and a half-indexed sequence can never go active.
fn pump_fts_backfills_on(writer: &mut Connection, batch: u64) -> Result<Vec<BackfillProgress>, ()> {
    let building = {
        let transaction =
            writer.transaction_with_behavior(TransactionBehavior::Deferred).map_err(|_| ())?;
        let mut statement = transaction
            .prepare(
                "SELECT collection, field, backfill_below, backfill_cursor
                 FROM fts_descriptors WHERE state = ?1 ORDER BY collection, field",
            )
            .map_err(|_| ())?;
        let rows = statement
            .query_map(rusqlite::params![DESCRIPTOR_STATE_BUILDING], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                ))
            })
            .map_err(|_| ())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?;
        drop(statement);
        transaction.commit().map_err(|_| ())?;
        rows
    };
    let mut progress = Vec::new();
    for (collection_name, field, below, cursor) in building {
        let below = u64::try_from(below).unwrap_or(0);
        let mut cursor = u64::try_from(cursor).unwrap_or(0);
        if below > cursor {
            let transaction =
                writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
            cursor =
                backfill_fts_batch(&transaction, &collection_name, &field, cursor, below, batch)?;
            let active = cursor >= below;
            transaction
                .prepare_cached(
                    "UPDATE fts_descriptors SET state = ?3, backfill_cursor = ?4
                     WHERE collection = ?1 AND field = ?2",
                )
                .and_then(|mut statement| {
                    statement.execute(rusqlite::params![
                        collection_name,
                        field,
                        if active { DESCRIPTOR_STATE_ACTIVE } else { DESCRIPTOR_STATE_BUILDING },
                        cursor as i64,
                    ])
                })
                .map_err(|_| ())?;
            transaction.commit().map_err(|_| ())?;
        }
        progress.push(BackfillProgress {
            collection: collection_name,
            field,
            remaining: below.saturating_sub(cursor),
            active: cursor >= below,
        });
    }
    Ok(progress)
}

/// Backfills one bounded batch of a text descriptor's window and returns
/// the advanced cursor.
fn backfill_fts_batch(
    transaction: &rusqlite::Transaction<'_>,
    collection: &str,
    field: &str,
    cursor: u64,
    below: u64,
    batch: u64,
) -> Result<u64, ()> {
    let batch = batch.max(1);
    let frontier: u64 = transaction
        .query_row(
            "SELECT commit_seq FROM documents
             WHERE collection = ?1 AND commit_seq > ?2 AND commit_seq <= ?3
             ORDER BY commit_seq LIMIT 1 OFFSET ?4",
            rusqlite::params![
                collection,
                cursor as i64,
                below as i64,
                (batch.saturating_sub(1)) as i64,
            ],
            |row| row.get::<_, i64>(0),
        )
        .optional()
        .map_err(|_| ())?
        .map_or(below, |sequence| u64::try_from(sequence).unwrap_or(below).min(below));
    let frontier = frontier.max(cursor);
    let mut statement = transaction
        .prepare_cached(
            "SELECT tenant, id, MAX(commit_seq) FROM documents
             WHERE collection = ?1 AND commit_seq > ?2 AND commit_seq <= ?3
             GROUP BY tenant, id",
        )
        .map_err(|_| ())?;
    let keys = statement
        .query_map(rusqlite::params![collection, cursor as i64, frontier as i64], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?, row.get::<_, i64>(2)?))
        })
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    for (tenant, id, _) in keys {
        backfill_fts_key(transaction, &tenant, collection, field, &id, below)?;
    }
    Ok(frontier)
}

fn origin_epoch_on(connection: &Connection) -> Result<(u64, bool), ()> {
    let value: Option<String> = connection
        .query_row("SELECT value FROM meta WHERE key='origin_authority_epoch'", [], |row| {
            row.get(0)
        })
        .optional()
        .map_err(|_| ())?;
    let Some(value) = value else {
        return Ok((0, false));
    };
    let (epoch, closed) = value.split_once(':').ok_or(())?;
    let epoch = epoch.parse::<u64>().map_err(|_| ())?;
    let closed = match closed {
        "0" => false,
        "1" => true,
        _ => return Err(()),
    };
    Ok((epoch, closed))
}
