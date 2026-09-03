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
//! `synchronous=NORMAL` — the pairing the bench comparator pinned. One
//! writer connection (behind a mutex, per D-28's one-writer-per-file rule)
//! plus a read pool; `commit_intents` runs one `BEGIN IMMEDIATE`
//! transaction per batch, so a group commit is one fsync class.
//!
//! - **Synced** batches raise that one transaction to `synchronous=FULL`
//!   (restored to `NORMAL` after), and the ledger records `synced` at write
//!   time: a FULL commit is durable before it returns, so there is no
//!   published-but-unsynced window for a crash to expose.
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
//! ## Detection and wiring
//!
//! A location self-describes through its `format` marker (magic
//! `TBV3SQLITE` + schema version); ownership validation recognizes the
//! marker. A marker naming another format never converts, a missing
//! marker beside durable artifacts is loss (not a fresh location), and a
//! marker without its database file is loss too — `Connection::open`
//! would otherwise silently mint a fresh database over a location whose
//! data was lost.
#![allow(clippy::cast_possible_truncation, clippy::cast_possible_wrap)]

#[cfg(test)]
mod tests;

use std::{
    path::{Path, PathBuf},
    sync::Mutex,
    time::Duration,
};

use rusqlite::types::Value;
use rusqlite::{Connection, TransactionBehavior};

use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId,
};
use crate::usage::{
    DurableUsageFact, MAX_ADMITTED_REQUEST_UNITS_PER_FACT, MAX_PUBLIC_EGRESS_BYTES_PER_FACT,
    MeasurementWindow, MeterCatalog, MeterKind, MintingIdentity, MintingRule, UsageFact,
};

#[cfg(test)]
use super::faults::FaultPlan;
use super::{
    BackfillProgress, DescriptorListing, DescriptorRegistration, IndexDescriptorError,
    PublishedCommit, PublishedWrite, ScanFilter, ScanOp, SnapshotReadError, StorageOpenError,
    StoragePort, StorageRootId, UsageFactPosition, UsageStorageError,
};
use super::{
    codec,
    intent::{
        CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite,
        StorageReceipt,
    },
};

/// The wall-clock commit time of a row, in Unix milliseconds (the D-42
/// 30-day history floor is evaluated against it).
fn unix_millis() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(i64::MAX, |since| i64::try_from(since.as_millis()).unwrap_or(i64::MAX))
}

/// The D-42 history floor: superseded rows stay reachable for at least
/// thirty days regardless of consumer frontiers.
const HISTORY_FLOOR_MILLIS: i64 = 30 * 24 * 60 * 60 * 1000;

/// Magic bytes prefixing the `SQLite` location's `format` marker.
const SQLITE_FORMAT_MAGIC: &[u8; 10] = b"TBV3SQLITE";

/// The `SQLite` storage schema version written after the marker magic.
const SQLITE_FORMAT_VERSION: u64 = 2;

/// The database file name inside one location directory.
const DATABASE_FILE: &str = "terrabase.sqlite3";

/// The read-pool width. Reads take `&self`, so the pool is what lets a
/// future engine-side read-concurrency split use this backend unchanged.
const READ_POOL_WIDTH: usize = 4;

/// The busy timeout every connection applies (the production pairing the
/// bench comparator pinned: WAL, `synchronous=NORMAL`, 60s busy timeout).
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
    fn refresh_put_sql(&mut self) {
        let mut columns = String::new();
        let mut placeholders = String::new();
        for (position, field) in self.fields.iter().enumerate() {
            columns.push_str(", ");
            columns.push_str(&quote_identifier(&text_column(field)));
            columns.push_str(", ");
            columns.push_str(&quote_identifier(&numeric_column(field)));
            placeholders.push_str(", ?");
            placeholders.push_str(&(13 + 2 * position).to_string());
            placeholders.push_str(", ?");
            placeholders.push_str(&(14 + 2 * position).to_string());
        }
        self.put_insert_sql = format!(
            "INSERT INTO documents
             (tenant, collection, id, commit_seq, commit_ulid, revision, requested,
              digest, version, deleted, committed_at, body{columns})
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12{placeholders})"
        );
    }

    /// Reports whether one field of one collection has an active descriptor.
    fn is_active(&self, collection: &str, field: &str) -> bool {
        self.descriptors.get(&(collection.to_owned(), field.to_owned())).copied().unwrap_or(false)
    }
}

/// Returns the text-column name of one extracted field: strings live here.
fn text_column(field: &str) -> String {
    format!("f:{field}#t")
}

/// Returns the numeric-column name of one extracted field: integers and
/// floats live here (integers stored as INTEGER, floats as REAL — SQLite
/// compares the two numerically, the `tql/1.0` number rule).
fn numeric_column(field: &str) -> String {
    format!("f:{field}#n")
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
    /// An integer, in the numeric column.
    Integer(i64),
    /// A float, in the numeric column.
    Real(f64),
}

/// Extracts one descriptor field's stored-column value from a document.
///
/// The path is dotted navigation over object members (D-30's write-time
/// parse is the only extraction). Values the columns do not carry —
/// booleans, nulls, arrays, objects, and absent fields — extract nothing:
/// a condition over them never pushes, and the scan falls back to the
/// executor's own predicate.
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
        _ => None,
    }
}

/// The schema statements every location carries.
const SCHEMA: &str = "
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
    PRIMARY KEY (tenant, collection, id, commit_seq)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS documents_seq ON documents (commit_seq);

CREATE INDEX IF NOT EXISTS documents_commit_ulid
    ON documents (tenant, commit_ulid);

CREATE TABLE IF NOT EXISTS rejections (
    tenant     TEXT    NOT NULL,
    operation  TEXT    NOT NULL,
    digest     INTEGER NOT NULL,
    rejection  INTEGER NOT NULL,
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;

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

CREATE TABLE IF NOT EXISTS usage_counters (
    key    TEXT    PRIMARY KEY,
    seq_hi INTEGER NOT NULL,
    ops    INTEGER NOT NULL
) WITHOUT ROWID;
";

/// The zero root head a fresh location records, before any commit chains it.
const INITIAL_ROOT_HEAD: &str = "sqlite3-00000000000000000000000000000000";

/// The `SQLite` storage engine behind [`StoragePort`].
pub(crate) struct SqliteStorage {
    /// The location's database path, kept for pool overflow opens.
    database_path: PathBuf,
    /// The single writer connection. One writer at a time is `SQLite`'s own
    /// WAL contract; the mutex is interior so a later engine read split can
    /// keep using this backend without restructuring.
    writer: Mutex<Connection>,
    /// Idle read connections, checked out last-in-first-out and returned on
    /// drop.
    readers: Mutex<Vec<Connection>>,
    /// The extracted-column descriptor view (D-42), refreshed on
    /// registration and consulted by the write and scan paths.
    extracted: Mutex<ExtractedState>,
    /// The test-only fault plan (see [`StoragePort::arm_test_fault`]).
    #[cfg(test)]
    faults: Mutex<FaultPlan>,
}

/// One checked-out read connection, returned to the pool on drop.
struct ReaderGuard<'a> {
    pool: &'a Mutex<Vec<Connection>>,
    connection: Option<Connection>,
}

impl ReaderGuard<'_> {
    /// The checked-out connection.
    fn connection(&self) -> &Connection {
        self.connection.as_ref().expect("checked-out connection")
    }
}

impl Drop for ReaderGuard<'_> {
    fn drop(&mut self) {
        if let Some(connection) = self.connection.take() {
            let mut pool = self.pool.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            pool.push(connection);
        }
    }
}

impl SqliteStorage {
    /// Opens (or creates) the `SQLite` location in `dir`.
    ///
    /// # Errors
    ///
    /// Returns [`StorageOpenError::FormatMismatch`] when the location's
    /// marker names another format or is missing beside durable artifacts,
    /// and [`StorageOpenError::Unavailable`] when the database cannot be
    /// opened or initialized.
    pub(crate) fn open(dir: &Path) -> Result<Self, StorageOpenError> {
        // The engine path pre-creates the location; creating here too keeps
        // the seam's direct callers (and the FileStorage reference's
        // behavior) working on a named-but-absent directory.
        std::fs::create_dir_all(dir).map_err(|_| StorageOpenError::Unavailable)?;
        let marker_created_fresh = verify_or_create_format(dir)?;
        let database_path = dir.join(DATABASE_FILE);
        if !marker_created_fresh && !database_path.exists() {
            // A marker without its database is loss, not a fresh location:
            // opening would silently mint an empty database over it.
            return Err(StorageOpenError::FormatMismatch);
        }
        let writer = open_connection(&database_path)?;
        writer.execute_batch(SCHEMA).map_err(|_| StorageOpenError::Unavailable)?;
        // Seed the identity rows idempotently: `INSERT OR IGNORE` keeps an
        // existing location's root chain and format version untouched.
        let seed = format!(
            "INSERT OR IGNORE INTO meta (key, value) VALUES
                ('format_version', '{SQLITE_FORMAT_VERSION}'),
                ('root_head', '{INITIAL_ROOT_HEAD}');"
        );
        writer.execute_batch(&seed).map_err(|_| StorageOpenError::Unavailable)?;
        // Usage-counter reconciliation (D-42): the counter is a cache of
        // the journal, never an independent source of truth. On open, a
        // counter whose high-water trails the folded rows — or no counter
        // at all — is recomputed from the journal itself, so a torn WAL
        // tail or a lost wave leaves the counter honest.
        reconcile_usage_counters(&writer).map_err(|()| StorageOpenError::Unavailable)?;
        let mut readers = Vec::with_capacity(READ_POOL_WIDTH);
        for _ in 0..READ_POOL_WIDTH {
            readers.push(open_connection(&database_path)?);
        }
        let mut extracted = ExtractedState::default();
        load_extracted_state(&writer, &mut extracted)
            .map_err(|()| StorageOpenError::Unavailable)?;
        Ok(Self {
            database_path,
            writer: Mutex::new(writer),
            readers: Mutex::new(readers),
            extracted: Mutex::new(extracted),
            #[cfg(test)]
            faults: Mutex::new(FaultPlan::empty()),
        })
    }

    /// Checks out one read connection, opening a fresh one when the pool is
    /// momentarily empty.
    fn reader(&self) -> ReaderGuard<'_> {
        let pooled = {
            let mut pool = self.readers.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            pool.pop()
        };
        // ponytail: unbounded fallback opens; bound with checkout waiting
        // only if a read-split engine ever exceeds the pool.
        let connection = pooled.or_else(|| open_connection(&self.database_path).ok());
        ReaderGuard { pool: &self.readers, connection }
    }

    /// Commits a batch of intents inside one `BEGIN IMMEDIATE` transaction.
    fn commit_intents(&self, intents: &[CommitIntent]) -> Result<Vec<CommitResult>, ()> {
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        // Durability is a batch-wide property here: one transaction, one
        // commit. A single `Synced` intent raises the whole batch's commit
        // to `synchronous=FULL`; every weaker request still lands at least
        // `Written`, its honest floor under WAL + NORMAL.
        let wants_full = intents.iter().any(|intent| intent.durability == LocalDurability::Synced);
        if wants_full {
            writer.execute_batch("PRAGMA synchronous = FULL").map_err(|_| ())?;
        }
        let result = self.commit_transaction(&mut writer, intents);
        // Restore the steady-state pragma. A failed restore only leaves the
        // connection more durable than requested, never less, so it is not
        // a failure the caller can observe.
        if wants_full {
            let _ = writer.execute_batch("PRAGMA synchronous = NORMAL");
        }
        result
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
        let Some(frontier) = history_floor_on(&transaction)? else {
            transaction.commit().map_err(|_| ())?;
            return Ok(0);
        };
        // The D-42 history floor: a row is prunable only when every
        // consumer frontier has passed it AND it is older than the floor's
        // 30-day retention. The cutoff rises with the sweep's own clock, so
        // a fresh row is never prunable regardless of frontiers.
        let cutoff = unix_millis().saturating_sub(HISTORY_FLOOR_MILLIS);
        // The OLDEST row still inside the floor's window: sequences are
        // allocated in commit order under the one writer, so every row at
        // or below it is inside the window too and only older rows may
        // leave. Zero means every row is older than the floor and only the
        // frontiers bound the sweep.
        let oldest_retained = u64::try_from(
            transaction
                .query_row(
                    "SELECT COALESCE(MIN(commit_seq), 0) FROM documents
                     WHERE committed_at >= ?1",
                    rusqlite::params![cutoff],
                    |row| row.get::<_, i64>(0),
                )
                .map_err(|_| ())?,
        )
        .unwrap_or(0);
        let floor = if oldest_retained == 0 { frontier } else { frontier.min(oldest_retained) };
        // Whole-commit retention: a below-floor commit survives only when
        // one of its rows is the newest version of its key — the row an
        // above-floor snapshot resolves when no later write exists. Every
        // other below-floor commit is fully shadowed, and pruning takes
        // whole commits only (never some of a commit's rows), so the
        // folded journal never serves a partial publication. The shadow
        // witness is stable through the statement (a key's newest row is
        // never itself deleted), so the sweep does not depend on scan
        // order.
        let pruned = transaction
            .execute(
                "DELETE FROM documents WHERE commit_seq < ?1 AND commit_seq NOT IN (
                   SELECT pin.commit_seq FROM documents pin
                   WHERE NOT EXISTS (
                     SELECT 1 FROM documents newer
                     WHERE newer.tenant = pin.tenant
                       AND newer.collection = pin.collection
                       AND newer.id = pin.id
                       AND newer.commit_seq > pin.commit_seq
                   )
                 )",
                rusqlite::params![floor as i64],
            )
            .map_err(|_| ())?;
        transaction.commit().map_err(|_| ())?;
        Ok(u64::try_from(pruned).unwrap_or(0))
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

impl SqliteStorage {
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
        let transaction =
            writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
        let mut results: Vec<Option<CommitResult>> = intents.iter().map(|_| None).collect();
        let mut watermark = durable_watermark(&transaction)?;
        let mut root_head: String = transaction
            .query_row("SELECT value FROM meta WHERE key = 'root_head'", [], |row| {
                row.get::<_, String>(0)
            })
            .map_err(|_| ())?;
        // The extracted-column view of this wave: one snapshot under the
        // wave's own transaction, so a registration that commits between
        // waves is picked up whole and never mid-wave.
        let (fields, put_sql, descriptors) = {
            let extracted =
                self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            (
                extracted.fields.clone(),
                extracted.put_insert_sql.clone(),
                extracted.descriptors.clone(),
            )
        };
        for (index, intent) in intents.iter().enumerate() {
            results[index] = Some(stage_intent(
                &transaction,
                &mut watermark,
                &mut root_head,
                intent,
                &fields,
                &put_sql,
                &descriptors,
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
#[allow(clippy::too_many_lines, reason = "one intent's full publication order")]
fn stage_intent(
    transaction: &rusqlite::Transaction<'_>,
    watermark: &mut u64,
    root_head: &mut String,
    intent: &CommitIntent,
    extracted_fields: &[String],
    put_insert_sql: &str,
    descriptors: &std::collections::BTreeMap<(String, String), bool>,
) -> Result<CommitResult, ()> {
    // Step 1: validation never binds the identity.
    if intent.writes.is_empty() || codec::staged_writes_have_duplicate_targets(&intent.writes) {
        return Ok(CommitResult::Rejected(CommitRejection::Validation));
    }
    // The `SQLite` backend mints no usage facts yet, so it refuses an
    // admission-bound intent exactly as the root-file backend does instead
    // of silently dropping billable evidence.
    if intent.usage_admission.is_some() {
        return Ok(CommitResult::Rejected(CommitRejection::Storage));
    }
    // Step 2: a retried identity resolves from its folded rows (or its
    // rejection record).
    if let Some(entry) = operation_view(transaction, &intent.tenant, &intent.operation)? {
        if entry.digest != intent.digest {
            return Ok(CommitResult::Rejected(CommitRejection::OperationIdReuse));
        }
        if let Some(reason) = entry.rejection {
            // A durably rejected identity replays its rejection, never a
            // publication — the reference engines' retry rule.
            return Ok(CommitResult::Rejected(reason));
        }
        return Ok(CommitResult::Committed(StorageReceipt {
            sequence: StorageSequence::new(entry.sequence),
            root: StorageRootId::from_recorded(root_head.clone()),
            durability: entry.requested,
        }));
    }
    // Step 3: conflict-check every write against the snapshot and the
    // published view. The published watermark already includes every
    // earlier intent of this batch.
    let snapshot_sequence = intent.snapshot_sequence.get();
    for write in &intent.writes {
        if let StagedWrite::Put { document, .. } = write
            && !codec::document_within_limits(document)
        {
            return Ok(CommitResult::Rejected(CommitRejection::Validation));
        }
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
        };
        if mismatch {
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
    // Step 4: allocate the sequence past everything this location has named.
    let Some(sequence) = watermark.checked_add(1) else {
        return Err(());
    };
    *watermark = sequence;
    let root = chained_root(root_head, sequence, intent.digest);
    // Step 5: documents rows ride one transaction, and every row of the
    // commit carries the commit's own identity — the journal is the table.
    let commit = FoldedCommit {
        operation: &intent.operation,
        requested: intent.durability,
        digest: intent.digest,
    };
    let mut published_writes: Vec<PublishedWrite> = Vec::with_capacity(intent.writes.len());
    for write in &intent.writes {
        match write {
            StagedWrite::Put { document, .. } => {
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
                insert_put_row(
                    transaction,
                    &intent.tenant,
                    &published,
                    sequence,
                    version,
                    &encode_document_body(&published),
                    &commit,
                    extracted_fields,
                    put_insert_sql,
                    descriptors,
                )?;
                published_writes.push(PublishedWrite::Put { document: published });
            }
            StagedWrite::Delete { collection, id, .. } => {
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
        }
    }
    let published = PublishedCommit::new(
        intent.tenant.clone(),
        intent.operation.clone(),
        StorageSequence::new(sequence),
        intent.durability,
        published_writes,
    );
    let _ = &published;
    update_root_head(transaction, root.as_str())?;
    root.clone_into(root_head);
    Ok(CommitResult::Committed(StorageReceipt {
        sequence: StorageSequence::new(sequence),
        root: StorageRootId::from_recorded(root),
        durability: intent.durability,
    }))
}

/// The durable resolution of one operation identity: a committed commit
/// resolves from its folded rows; a rejection resolves from the rejections
/// table.
struct OperationView {
    digest: u64,
    sequence: u64,
    requested: LocalDurability,
    rejection: Option<CommitRejection>,
}

/// Reads one operation's durable resolution from the folded rows and the
/// rejections table.
fn operation_view(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    operation: &OperationId,
) -> Result<Option<OperationView>, ()> {
    if let Ok(row) = transaction.query_row(
        "SELECT digest, MIN(commit_seq), MIN(requested) FROM documents
         WHERE tenant = ?1 AND commit_ulid = ?2",
        rusqlite::params![tenant.as_str(), operation.as_str()],
        |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?, row.get::<_, i64>(2)?)),
    ) {
        let (digest, sequence, requested) = row;
        return Ok(Some(OperationView {
            #[allow(clippy::cast_sign_loss)]
            digest: digest as u64,
            sequence: u64::try_from(sequence).map_err(|_| ())?,
            requested: codec::durability_of(u8::try_from(requested).map_err(|_| ())?).ok_or(())?,
            rejection: None,
        }));
    }
    let rejection = match transaction.query_row(
        "SELECT digest, rejection FROM rejections WHERE tenant = ?1 AND operation = ?2",
        rusqlite::params![tenant.as_str(), operation.as_str()],
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
    }))
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
        .map_err(|_| ())
}

/// Writes one document put row with its extracted index columns (D-42):
/// the values the Rust write path extracted from the one write-time parse,
/// in the same statement as the body — index maintenance in the same
/// transaction is the zero-stale-reads rule.
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
) -> Result<(), ()> {
    let committed_at = unix_millis();
    let mut parameters: Vec<Value> = Vec::with_capacity(12 + 2 * extracted_fields.len());
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
        .map_err(|_| ())
}

/// Writes one tombstone row (empty body, no extracted columns).
#[allow(clippy::too_many_arguments, reason = "the folded row model's columns")]
fn insert_document_row(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    sequence: u64,
    version: u64,
    commit: &FoldedCommit<'_>,
) -> Result<(), ()> {
    let committed_at = unix_millis();
    transaction
        .prepare_cached(
            "INSERT INTO documents
             (tenant, collection, id, commit_seq, commit_ulid, revision, requested,
              digest, version, deleted, committed_at, body)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
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
            ])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// The per-commit facts every row of one commit carries (the D-42 fold).
struct FoldedCommit<'a> {
    operation: &'a OperationId,
    requested: LocalDurability,
    digest: u64,
}

/// The public revision string of one row: the sequence it committed at.
fn revision_string(sequence: u64, version: u64) -> String {
    format!("{sequence}-{version}")
}

/// Advances the recorded root chain head.
fn update_root_head(transaction: &rusqlite::Transaction<'_>, root: &str) -> Result<(), ()> {
    transaction
        .prepare_cached("UPDATE meta SET value = ?1 WHERE key = 'root_head'")
        .and_then(|mut statement| statement.execute(rusqlite::params![root]))
        .map(|_| ())
        .map_err(|_| ())
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

/// The highest sequence any durable row names. Commits' folded document
/// rows and usage facts share one sequence space, exactly the reference
/// engines' watermark rule.
fn durable_watermark(connection: &Connection) -> Result<u64, ()> {
    let watermark = connection
        .query_row(
            "SELECT COALESCE(MAX(sequence), 0) FROM (
                 SELECT MAX(commit_seq) AS sequence FROM documents
                 UNION ALL
                 SELECT MAX(seq) AS sequence FROM usage_facts
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
    Ok(floor.and_then(|value| u64::try_from(value).ok()))
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
/// the reference engines' rule for `UsageFactSource::Standalone`.
fn standalone_usage_fact_is_valid(fact: &UsageFact) -> bool {
    let known_version = MeterCatalog::initial().get(fact.meter(), fact.meter_version()).is_some();
    let instant = fact.window().start_millis() != 0
        && fact.window().start_millis() == fact.window().end_millis();
    let admitted = fact.meter() == MeterKind::AdmittedRequestUnits
        && matches!(fact.identity(), MintingIdentity::StableAdmittedRequest(_))
        && fact.delta() > 0
        && fact.delta() <= MAX_ADMITTED_REQUEST_UNITS_PER_FACT;
    let public_egress = fact.meter() == MeterKind::PublicEgressBytes
        && matches!(fact.identity(), MintingIdentity::PublicResponse(_))
        && fact.delta() > 0
        && fact.delta() <= MAX_PUBLIC_EGRESS_BYTES_PER_FACT;
    known_version && instant && (admitted || public_egress)
}

/// Maps a minting rule to its integer code — the discriminant whose order
/// is the type's `Ord`, so SQL ordering equals `UsageFactPosition` order.
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
    // key's observed version (a re-put restarts at version 1).
    transaction
        .prepare_cached(
            "SELECT version, deleted FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
             ORDER BY commit_seq DESC LIMIT 1",
        )
        .and_then(|mut statement| {
            statement.query_row(
                rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str(), at as i64],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?)),
            )
        })
        .map(|(version, deleted)| if deleted == 0 { u64::try_from(version).ok() } else { None })
        .or_else(|error| match error {
            rusqlite::Error::QueryReturnedNoRows => Ok(None),
            _ => Err(()),
        })
}

/// Returns the first collection, in canonical key order, holding a live copy
/// of the identifier at or below `at`.
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
             GROUP BY collection ORDER BY collection LIMIT 1",
        )
        .and_then(|mut statement| {
            statement.query_row(rusqlite::params![tenant.as_str(), id.as_str(), at as i64], |row| {
                row.get::<_, String>(0)
            })
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

/// Encodes one document body with the shared private codec.
fn encode_document_body(document: &Document) -> Vec<u8> {
    let mut bytes = Vec::new();
    codec::encode_document(&mut bytes, document);
    bytes
}

/// Decodes one document body, rejecting trailing bytes.
fn decode_document_body(bytes: &[u8]) -> Option<Document> {
    let mut reader = codec::Reader::new(bytes);
    let document = codec::decode_document(&mut reader)?;
    (reader.remaining() == 0).then_some(document)
}

/// Derives the next root-chain identity from the current head.
///
/// The identity keeps the LSM chain's shape — sequence and a chained
/// checksum — behind a `SQLite`-namespace prefix, so a root string from this
/// backend is self-describing and never collides with an LSM root.
fn chained_root(head: &str, sequence: u64, digest: u64) -> String {
    let mut chained = Vec::new();
    codec::push_str(&mut chained, head);
    codec::push_u64(&mut chained, sequence);
    codec::push_u64(&mut chained, digest);
    format!("sqlite3-{:016x}{:016x}", sequence, codec::fnv1a(&chained))
}

/// Opens one configured connection to the location's database.
fn open_connection(path: &Path) -> Result<Connection, StorageOpenError> {
    let connection = Connection::open(path).map_err(|_| StorageOpenError::Unavailable)?;
    configure_connection(&connection)?;
    Ok(connection)
}

/// Applies the production pairing to one connection: WAL, 60s busy timeout,
/// foreign keys, and `synchronous=NORMAL`.
fn configure_connection(connection: &Connection) -> Result<(), StorageOpenError> {
    connection
        .query_row("PRAGMA journal_mode = WAL", [], |_| Ok(()))
        .map_err(|_| StorageOpenError::Unavailable)?;
    connection.busy_timeout(BUSY_TIMEOUT).map_err(|_| StorageOpenError::Unavailable)?;
    // wal_autocheckpoint=0: auto-checkpoints fire inside the committing
    // transaction and would put checkpoint latency on the write path. The
    // group-commit leader checkpoints passively between waves instead
    // (`flush_pending_flips`). cache_size and mmap_size stay at SQLite's
    // defaults until the member budget exists (S-05) — a per-connection
    // cache multiplied by the read pool would outweigh the RSS-sized
    // catalog LRU.
    // ponytail: member-budget pragmas when S-05 lands.
    connection
        .execute_batch(
            "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;
             PRAGMA wal_autocheckpoint = 0; PRAGMA temp_store = MEMORY;",
        )
        .map_err(|_| StorageOpenError::Unavailable)?;
    Ok(())
}

/// Verifies the location's format marker, creating it for a fresh location.
fn verify_or_create_format(dir: &Path) -> Result<bool, StorageOpenError> {
    let path = dir.join("format");
    match std::fs::read(&path) {
        Ok(bytes) => {
            if !bytes.starts_with(SQLITE_FORMAT_MAGIC) {
                // A location self-describes: a marker naming another format
                // never silently converts.
                return Err(StorageOpenError::FormatMismatch);
            }
            if bytes.len() != SQLITE_FORMAT_MAGIC.len() + 8 {
                return Err(StorageOpenError::FormatMismatch);
            }
            let raw: [u8; 8] = bytes[SQLITE_FORMAT_MAGIC.len()..]
                .try_into()
                .map_err(|_| StorageOpenError::FormatMismatch)?;
            if u64::from_le_bytes(raw) != SQLITE_FORMAT_VERSION {
                return Err(StorageOpenError::FormatMismatch);
            }
            Ok(false)
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            // A missing marker beside any durable artifact is loss, not a
            // fresh location — the same rule the other backends enforce.
            let entries = std::fs::read_dir(dir).map_err(|_| StorageOpenError::Unavailable)?;
            for entry in entries {
                let entry = entry.map_err(|_| StorageOpenError::Unavailable)?;
                let name = entry.file_name();
                if name != "owner.tbv1"
                    && !name.to_str().is_some_and(|name| name.starts_with("owner.tbv1.tmp-"))
                {
                    return Err(StorageOpenError::FormatMismatch);
                }
            }
            let mut bytes = Vec::new();
            bytes.extend_from_slice(SQLITE_FORMAT_MAGIC);
            bytes.extend_from_slice(&SQLITE_FORMAT_VERSION.to_le_bytes());
            std::fs::write(&path, bytes).map_err(|_| StorageOpenError::Unavailable)?;
            std::fs::File::open(dir)
                .and_then(|directory| directory.sync_all())
                .map_err(|_| StorageOpenError::Unavailable)?;
            Ok(true)
        }
        Err(_) => Err(StorageOpenError::Unavailable),
    }
}

/// The raw columns one as-of scan maps each surviving row to.
type ScanRow = (String, String, i64, Vec<u8>);

/// Maps the rows of one as-of scan to their raw columns. `SQLite`'s
/// bare-column rule puts `deleted` and `body` from each group's
/// `MAX(commit_seq)` row, which is exactly the as-of state.
fn scan_rows<P: rusqlite::Params>(
    statement: &mut rusqlite::CachedStatement<'_>,
    params: P,
) -> Result<Vec<ScanRow>, rusqlite::Error> {
    let rows = statement.query_map(params, |row| {
        Ok((
            row.get::<_, String>(0)?,
            row.get::<_, String>(1)?,
            row.get::<_, i64>(2)?,
            row.get::<_, Vec<u8>>(3)?,
        ))
    })?;
    rows.collect()
}

/// Collects one scan's live documents, dropping a body that does not
/// decode or match its key.
fn scan_documents(rows: Result<Vec<ScanRow>, rusqlite::Error>) -> Vec<Document> {
    collect_checked(rows).unwrap_or_default()
}

/// Collects one scan's live documents, failing closed on a body that does
/// not verify against its key.
fn collect_checked(
    rows: Result<Vec<ScanRow>, rusqlite::Error>,
) -> Result<Vec<Document>, SnapshotReadError> {
    let rows = rows.map_err(|_| SnapshotReadError::Integrity)?;
    let mut documents = Vec::with_capacity(rows.len());
    for (collection, id, deleted, body) in rows {
        if deleted != 0 {
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

impl StoragePort for SqliteStorage {
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
        match self.commit_intents(&intents) {
            Ok(results) => results,
            // A rolled-back transaction definitely published nothing, so
            // every intent gets a definite refusal — the uncertain outcome
            // the append-then-sync backends report cannot occur here.
            Err(()) => vec![CommitResult::Rejected(CommitRejection::Storage); intents.len()],
        }
    }

    fn resolve_operation(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<OperationResolution> {
        self.reader()
            .connection()
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
                let connection = self.reader();
                let connection = connection.connection();
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
        {
            let reader = self.reader();
            if let Some(body) = usage_fact_body(reader.connection(), &fact)
                .map_err(|()| UsageStorageError::Corrupt)?
            {
                // The identity is already bound: the same content is the
                // idempotent replay; different content is the rebinding.
                let existing =
                    DurableUsageFact::decode(&body).map_err(|_| UsageStorageError::Corrupt)?;
                return if existing.fact() == &fact {
                    Ok((existing, true))
                } else {
                    Err(UsageStorageError::IdentityConflict)
                };
            }
        }
        let mut writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let transaction = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| UsageStorageError::Unavailable)?;
        // Usage facts share the commit sequence space, exactly the
        // reference engines' watermark rule.
        let Some(sequence) = durable_watermark(&transaction)
            .map_err(|()| UsageStorageError::Unavailable)?
            .checked_add(1)
        else {
            return Err(UsageStorageError::Unavailable);
        };
        let durable =
            DurableUsageFact::try_new(fact, sequence, 1).map_err(|_| UsageStorageError::Corrupt)?;
        let scope = durable.fact().scope();
        let identity = durable.fact().identity();
        transaction
            .execute(
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
            )
            .map_err(|_| UsageStorageError::Unavailable)?;
        transaction.commit().map_err(|_| UsageStorageError::Unavailable)?;
        Ok((durable, false))
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

    fn read_document_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<Document> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row(
                "SELECT deleted, body, MAX(commit_seq) FROM documents
                 WHERE tenant = ?1 AND id = ?2 AND commit_seq <= ?3
                 GROUP BY collection ORDER BY collection LIMIT 1",
                rusqlite::params![tenant.as_str(), id.as_str(), sequence.get() as i64],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, Vec<u8>>(1)?)),
            )
            .ok()
            .filter(|(deleted, _)| *deleted == 0)
            .and_then(|(_, body)| decode_document_body(&body))
    }

    fn read_document_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<Document> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row(
                "SELECT deleted, body FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    id.as_str(),
                    sequence.get() as i64
                ],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, Vec<u8>>(1)?)),
            )
            .ok()
            .filter(|(deleted, _)| *deleted == 0)
            .and_then(|(_, body)| decode_document_body(&body))
    }

    fn document_history_in_collection(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
        limit: u32,
    ) -> Vec<crate::storage::DocumentRevisionEntry> {
        let reader = self.reader();
        let connection = reader.connection();
        let Ok(mut statement) = connection.prepare(
            "SELECT revision, deleted FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND id = ?3
             ORDER BY commit_seq DESC LIMIT ?4",
        ) else {
            return Vec::new();
        };
        let Ok(rows) = statement.query_map(
            rusqlite::params![tenant.as_str(), collection.as_str(), id.as_str(), i64::from(limit)],
            |row| {
                Ok(crate::storage::DocumentRevisionEntry {
                    revision: row.get::<_, String>(0)?,
                    deleted: row.get::<_, i64>(1)? != 0,
                })
            },
        ) else {
            return Vec::new();
        };
        rows.filter_map(Result::ok).collect()
    }

    fn document_revision_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<String> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row(
                "SELECT deleted, revision FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    id.as_str(),
                    sequence.get() as i64
                ],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, String>(1)?)),
            )
            .ok()
            .filter(|(deleted, _)| *deleted == 0)
            .map(|(_, revision)| revision)
    }

    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document> {
        let reader = self.reader();
        let Ok(mut statement) = connection_statement(
            reader.connection(),
            "SELECT collection, id, deleted, body, MAX(commit_seq) FROM documents
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

    fn register_index_descriptor(
        &mut self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<DescriptorRegistration, IndexDescriptorError> {
        self.register_descriptor(collection, field)
    }

    fn index_descriptors(&self) -> Result<Vec<DescriptorListing>, ()> {
        self.list_descriptors()
    }

    fn pump_index_backfills(&mut self, batch: u64) -> Result<Vec<BackfillProgress>, ()> {
        self.pump_backfills(batch)
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
        let Ok(transaction) = connection.unchecked_transaction() else {
            return Vec::new();
        };
        let mut documents: Vec<Document> = Vec::with_capacity(ids.len());
        for id in ids {
            let Ok((deleted, body)) = transaction.query_row(
                "SELECT deleted, body FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    tenant.as_str(),
                    collection.as_str(),
                    id.as_str(),
                    sequence.get() as i64
                ],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, Vec<u8>>(1)?)),
            ) else {
                continue;
            };
            if deleted == 0
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
                 GROUP BY collection ORDER BY collection LIMIT 1",
                rusqlite::params![tenant.as_str(), after.as_str(), sequence.get() as i64],
                |row| row.get::<_, String>(0),
            )
            .ok();
        let Ok(mut statement) = connection_statement(
            connection,
            "SELECT collection, id, deleted, body, MAX(commit_seq) FROM documents
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
        let reader = self.reader();
        let connection = reader.connection();
        let (after_collection, after_id) =
            after.map_or(("", ""), |(collection, id)| (collection.as_str(), id.as_str()));
        let mut statement = connection_statement(
            connection,
            "SELECT collection, id, deleted, body, MAX(commit_seq) FROM documents
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
        // ponytail: O(retained history) per call, matching the LSM's
        // history_index walk; index the rows by key when bulk export on
        // this backend gets hot.
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
    ) -> Vec<PublishedCommit> {
        let reader = self.reader();
        let connection = reader.connection();
        let Ok(mut statement) = connection.prepare_cached(
            "SELECT commit_seq, commit_ulid, collection, id, deleted, version,
                    requested, body
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
                Ok((
                    row.get::<_, i64>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, i64>(5)?,
                    row.get::<_, i64>(6)?,
                    row.get::<_, Vec<u8>>(7)?,
                ))
            },
        ) else {
            return Vec::new();
        };
        // Group the rows of one commit_seq into one PublishedCommit — the
        // journal rebuilt from the folded rows. The inner LIMIT bounds
        // commits (not rows), so no publication is ever served partial.
        let mut commits: Vec<PublishedCommit> = Vec::new();
        for row in rows.flatten() {
            let (sequence, operation, collection, id, deleted, version, requested, body) = row;
            let write = if deleted == 0 {
                let Some(document) = decode_document_body(&body) else { continue };
                PublishedWrite::Put { document }
            } else {
                let Some(collection) = CollectionName::try_new(&collection).ok() else { continue };
                let Some(id) = DocumentId::try_new(&id).ok() else { continue };
                PublishedWrite::Delete {
                    collection: Some(collection),
                    id,
                    version_at_delete: u64::try_from(version).unwrap_or(0),
                }
            };
            let Some(sequence) = u64::try_from(sequence).ok() else { continue };
            match commits.last_mut() {
                Some(last) if last.sequence().get() == sequence => {
                    let writes = last.writes_mut();
                    writes.push(write);
                }
                _ => {
                    let Some(durability) =
                        u8::try_from(requested).ok().and_then(codec::durability_of)
                    else {
                        continue;
                    };
                    let Some(operation) = OperationId::try_new(&operation).ok() else { continue };
                    commits.push(PublishedCommit::new(
                        tenant.clone(),
                        operation,
                        StorageSequence::new(sequence),
                        durability,
                        vec![write],
                    ));
                }
            }
        }
        commits
    }

    fn committed_tenants(&self) -> Vec<TenantId> {
        let reader = self.reader();
        let connection = reader.connection();
        let Ok(mut statement) =
            connection.prepare_cached("SELECT DISTINCT tenant FROM documents ORDER BY tenant")
        else {
            return Vec::new();
        };
        let Ok(rows) = statement.query_map([], |row| row.get::<_, String>(0)) else {
            return Vec::new();
        };
        rows.flatten().filter_map(|tenant| TenantId::try_new(&tenant).ok()).collect()
    }

    fn tenant_sequence(&self, tenant: &TenantId, at: StorageSequence) -> Option<StorageSequence> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row(
                "SELECT MAX(commit_seq) FROM documents WHERE tenant = ?1 AND commit_seq <= ?2",
                rusqlite::params![tenant.as_str(), at.get() as i64],
                |row| row.get::<_, Option<i64>>(0),
            )
            .ok()
            .flatten()
            .and_then(|sequence| u64::try_from(sequence).ok())
            .map(StorageSequence::new)
    }

    fn servable_history_start(&self) -> Option<StorageSequence> {
        let reader = self.reader();
        let connection = reader.connection();
        // The pruning floor bounds what the folded rows can still serve;
        // with no consumer registered, the oldest retained row is the
        // bound.
        history_floor_on(connection)
            .ok()
            .flatten()
            .or_else(|| {
                connection
                    .query_row("SELECT MIN(commit_seq) FROM documents", [], |row| {
                        row.get::<_, Option<i64>>(0)
                    })
                    .ok()
                    .flatten()
                    .and_then(|sequence| u64::try_from(sequence).ok())
            })
            .map(StorageSequence::new)
    }

    fn journaled_high_water(&self) -> Option<StorageSequence> {
        let reader = self.reader();
        let connection = reader.connection();
        connection
            .query_row("SELECT MAX(commit_seq) FROM documents", [], |row| {
                row.get::<_, Option<i64>>(0)
            })
            .ok()
            .flatten()
            .and_then(|sequence| u64::try_from(sequence).ok())
            .map(StorageSequence::new)
    }

    fn published_state(&self) -> (StorageSequence, StorageRootId) {
        let reader = self.reader();
        let connection = reader.connection();
        // One statement keeps the sequence and the root head on one read
        // snapshot. The watermark is the shared sequence space's maximum —
        // commits' folded rows and usage facts — matching the reference
        // engines (with rejections consuming no sequence, an empty
        // usage table leaves it exactly the last commit's sequence).
        connection
            .query_row(
                "SELECT (SELECT COALESCE(MAX(sequence), 0) FROM (
                            SELECT MAX(commit_seq) AS sequence FROM documents
                            UNION ALL
                            SELECT MAX(seq) AS sequence FROM usage_facts)),
                        (SELECT value FROM meta WHERE key = 'root_head')",
                [],
                |row| {
                    let sequence = row.get::<_, i64>(0)?;
                    let root = row.get::<_, String>(1)?;
                    Ok((u64::try_from(sequence).unwrap_or(0), root))
                },
            )
            .ok()
            .map_or(
                (StorageSequence::new(0), StorageRootId::from_recorded(String::new())),
                |(sequence, root)| {
                    (StorageSequence::new(sequence), StorageRootId::from_recorded(root))
                },
            )
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
        let reader = self.reader();
        let connection = reader.connection();
        let mut statement = connection
            .prepare_cached(
                "SELECT id, deleted, body, MAX(commit_seq) FROM documents
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
                    Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?, row.get::<_, Vec<u8>>(2)?))
                },
            )
            .map_err(|_| SnapshotReadError::Integrity)?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| SnapshotReadError::Integrity)?;
        let mut documents = Vec::with_capacity(rows.len());
        for (id, deleted, body) in rows {
            if deleted != 0 {
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
    /// SQLite expression or generated columns).
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
        let extracted = self.extracted.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
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
        if conditions.is_empty() {
            return None;
        }
        let sql = format!(
            "SELECT id, deleted, body, MAX(commit_seq) FROM documents
             WHERE tenant = ?1 AND collection = ?2 AND commit_seq <= ?3
               AND id > COALESCE(?4, '')
             GROUP BY id
             HAVING deleted = 0 AND {}
             ORDER BY id LIMIT ?",
            conditions.join(" AND ")
        );
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
                Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?, row.get::<_, Vec<u8>>(2)?))
            })
            .ok()?;
        let rows = rows.collect::<Result<Vec<_>, _>>().ok()?;
        let mut documents = Vec::with_capacity(rows.len());
        for (id, deleted, body) in rows {
            if deleted != 0 {
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
fn backfill_batch(
    transaction: &rusqlite::Transaction<'_>,
    collection: &CollectionName,
    field: &str,
    cursor: u64,
    below: u64,
    batch: u64,
) -> Result<u64, ()> {
    let text = quote_identifier(&text_column(field));
    let number = quote_identifier(&numeric_column(field));
    let mut statement = transaction
        .prepare_cached(
            "SELECT tenant, id, commit_seq, deleted, body FROM documents
             WHERE collection = ?1 AND commit_seq > ?2 AND commit_seq <= ?3
             ORDER BY commit_seq LIMIT ?4",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map(
            rusqlite::params![collection.as_str(), cursor as i64, below as i64, batch as i64],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, Vec<u8>>(4)?,
                ))
            },
        )
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    let mut advanced = cursor;
    for (tenant, id, commit_seq, deleted, body) in rows {
        advanced = advanced.max(u64::try_from(commit_seq).unwrap_or(advanced));
        if deleted != 0 {
            continue;
        }
        let Some(document) = decode_document_body(&body) else { continue };
        let (text_value, number_value) = match extract_column_value(&document, field) {
            Some(ColumnValue::Text(text)) => (Value::Text(text), Value::Null),
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
    Ok(advanced)
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
