//! The `SQLite`-backed storage engine (D-27, TB-20).
//!
//! One location is one `SQLite` database file (`terrabase.sqlite3`) opened in
//! WAL mode with a single writer connection and a small read pool. Every
//! commit — documents, operation-ledger row, and publication-history journal
//! row — rides one `SQLite` transaction, so a crash can never leave a document
//! without its journal record or the reverse;
//! [`StoragePort::journal_divergence`] is structurally zero by
//! construction, not by repair.
//!
//! Reads resolve as-of a storage sequence the same way the LSM engine's
//! versioned keys do: `documents` is an append-only version table keyed
//! `(tenant, collection, identifier, commit sequence)`, so a read at `seq`
//! selects each key's newest row at or below `seq`, and a tombstone row
//! hides the key exactly as an LSM deletion marker does. While no pruning
//! floor exists, [`StoragePort::snapshot_boundary_available`] holds for
//! every boundary at or below the published sequence — the same contract an
//! un-compacted LSM location gives. Document bodies and journal frames
//! reuse the crate's private codec bytes, so structural limits and decode
//! semantics stay identical to the other backends.
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
//! and writes no journal row — the `FileStorage` rule, so published
//! sequences stay dense and [`StoragePort::journaled_high_water`] never
//! trails [`StoragePort::published_state`] behind a non-publishing record.
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
//! order is the type's `Ord`). The surface matches the LSM engine's and
//! is pinned against it by a differential test.
//!
//! ## Detection and wiring
//!
//! A location self-describes through its `format` marker (magic
//! `TBV3SQLITE` + schema version); the engine's `backend_of_location`
//! sniffs the marker, `TERRABASE_STORAGE_BACKEND=sqlite` selects the
//! backend for fresh locations, and ownership validation recognizes the
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

use rusqlite::{Connection, TransactionBehavior};

use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantId,
};
use crate::usage::{
    DurableUsageFact, MAX_ADMITTED_REQUEST_UNITS_PER_FACT, MAX_PUBLIC_EGRESS_BYTES_PER_FACT,
    MeasurementWindow, MeterCatalog, MeterKind, MintingIdentity, MintingRule, UsageFact,
};

#[cfg(test)]
use super::faults::FaultPlan;
use super::{
    PublishedCommit, PublishedWrite, SnapshotReadError, StorageOpenError, StoragePort,
    StorageRootId, UsageFactPosition, UsageStorageError,
};
use super::{
    codec,
    compaction::{CompactionDriverFacts, CompactionError, CompactionReceipt, StorageCompaction},
    file::min_level,
    intent::{
        CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite,
        StorageReceipt,
    },
    ledger, lsm,
};

/// Magic bytes prefixing the `SQLite` location's `format` marker.
const SQLITE_FORMAT_MAGIC: &[u8; 10] = b"TBV3SQLITE";

/// The `SQLite` storage schema version written after the marker magic.
const SQLITE_FORMAT_VERSION: u64 = 1;

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

/// The schema statements every location carries.
const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value BLOB NOT NULL
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS documents (
    tenant     TEXT    NOT NULL,
    collection TEXT    NOT NULL,
    id         TEXT    NOT NULL,
    commit_seq INTEGER NOT NULL,
    version    INTEGER NOT NULL,
    deleted    INTEGER NOT NULL,
    body       BLOB    NOT NULL,
    PRIMARY KEY (tenant, collection, id, commit_seq)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS ledger (
    tenant     TEXT    NOT NULL,
    operation  TEXT    NOT NULL,
    digest     INTEGER NOT NULL,
    sequence   INTEGER NOT NULL,
    root       TEXT    NOT NULL,
    requested  INTEGER NOT NULL,
    committed  INTEGER NOT NULL,
    synced     INTEGER NOT NULL,
    rejection  INTEGER NOT NULL,
    PRIMARY KEY (tenant, operation)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS journal (
    sequence  INTEGER PRIMARY KEY,
    tenant    TEXT NOT NULL,
    operation TEXT NOT NULL,
    frame     BLOB NOT NULL
);

CREATE INDEX IF NOT EXISTS journal_tenant_sequence ON journal (tenant, sequence);

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
        let mut readers = Vec::with_capacity(READ_POOL_WIDTH);
        for _ in 0..READ_POOL_WIDTH {
            readers.push(open_connection(&database_path)?);
        }
        Ok(Self {
            database_path,
            writer: Mutex::new(writer),
            readers: Mutex::new(readers),
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
        let Some(floor) = history_floor_on(&transaction)? else {
            transaction.commit().map_err(|_| ())?;
            return Ok(0);
        };
        let pruned = transaction
            .execute("DELETE FROM journal WHERE sequence < ?1", rusqlite::params![floor as i64])
            .map_err(|_| ())?;
        // Version-row retention: a below-floor row survives only when it is
        // the newest version of its key — the row an above-floor snapshot
        // resolves when no later write exists. Everything else below the
        // floor is shadowed by a newer version, tombstone or not, and a
        // shadowed row cannot change any above-floor answer. The MAX
        // witness is stable through the statement (a key's newest row is
        // never itself deleted), so the sweep does not depend on scan
        // order.
        transaction
            .execute(
                "DELETE FROM documents WHERE commit_seq < ?1 AND commit_seq < (
                   SELECT MAX(newer.commit_seq) FROM documents newer
                   WHERE newer.tenant = documents.tenant
                     AND newer.collection = documents.collection
                     AND newer.id = documents.id
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

    /// The journal's record count — the bounded-journal invariant's measure.
    #[cfg(test)]
    fn journal_records(&self) -> u64 {
        let reader = self.reader();
        let count = reader
            .connection()
            .query_row("SELECT COUNT(*) FROM journal", [], |row| row.get::<_, i64>(0));
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
        for (index, intent) in intents.iter().enumerate() {
            results[index] =
                Some(stage_intent(&transaction, &mut watermark, &mut root_head, intent)?);
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

/// Stages one intent inside the open transaction, mirroring the LSM
/// `prepare_commit` order: validate, resolve a retried identity durably,
/// conflict-check, then write documents, ledger row, and journal row.
#[allow(
    clippy::too_many_lines,
    reason = "one intent's full publication order, mirroring the LSM's prepare_commit"
)]
fn stage_intent(
    transaction: &rusqlite::Transaction<'_>,
    watermark: &mut u64,
    root_head: &mut String,
    intent: &CommitIntent,
) -> Result<CommitResult, ()> {
    // Step 1: validation never binds the identity. An admitted commit must
    // be local-synced — the rule that binds the billable fact to a durable
    // sequence — and the admission's request identity must equal the
    // transaction's operation identity.
    if intent.writes.is_empty() || codec::staged_writes_have_duplicate_targets(&intent.writes) {
        return Ok(CommitResult::Rejected(CommitRejection::Validation));
    }
    let admitted_fact = if let Some(admission) = &intent.usage_admission {
        if intent.durability != LocalDurability::Synced
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
    // Step 2: a retried identity resolves from the durable ledger.
    // Step 2: a retried identity resolves from the durable ledger.
    if let Some(entry) = ledger_entry(transaction, &intent.tenant, &intent.operation)? {
        if entry.digest != intent.digest {
            return Ok(CommitResult::Rejected(CommitRejection::OperationIdReuse));
        }
        if entry.rejection.is_none()
            && let Some(fact) = &admitted_fact
        {
            // A committed replay must find exactly the fact this identity
            // minted — same content, bound to the replayed commit's
            // sequence — or the identity's billable evidence is broken.
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
        let proven = if entry.synced { LocalDurability::Synced } else { LocalDurability::Written };
        return Ok(CommitResult::Committed(StorageReceipt {
            sequence: StorageSequence::new(entry.sequence),
            root: StorageRootId::from_recorded(entry.root),
            durability: min_level(entry.requested, proven),
        }));
    }
    // Step 3: conflict-check every write against the snapshot and the
    // published view. The published watermark already includes every
    // earlier intent of this batch, exactly as the LSM's does.
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
            insert_ledger_row(
                transaction,
                &LedgerRow {
                    tenant: intent.tenant.as_str(),
                    operation: intent.operation.as_str(),
                    digest: intent.digest,
                    sequence: 0,
                    root: "",
                    requested: intent.durability,
                    committed: false,
                    synced: false,
                    rejection: Some(CommitRejection::Conflict),
                },
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
    // An admitted commit mints its billable fact in the same transaction,
    // bound to this commit's sequence and identity — the LSM rule that
    // keeps the usage fact and the write it bills atomically one outcome.
    // A fact identity already bound to different evidence is a reuse.
    if let Some(fact) = &admitted_fact {
        if usage_fact_body(transaction, fact)?.is_some() {
            return Ok(CommitResult::Rejected(CommitRejection::OperationIdReuse));
        }
        let Some(durable) = DurableUsageFact::try_new(fact.clone(), sequence, 1).ok() else {
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
    // Step 5: documents, ledger row, and journal row ride one transaction.
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
                insert_document_row(
                    transaction,
                    &intent.tenant,
                    published.collection(),
                    published.id(),
                    sequence,
                    version,
                    false,
                    &encode_document_body(&published),
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
                            true,
                            &[],
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
                                true,
                                &[],
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
    insert_ledger_row(
        transaction,
        &LedgerRow {
            tenant: intent.tenant.as_str(),
            operation: intent.operation.as_str(),
            digest: intent.digest,
            sequence,
            root: root.as_str(),
            requested: intent.durability,
            committed: true,
            // A `synchronous=FULL` commit is durable before it returns, so
            // the ledger records the flip at write time: there is no
            // published-but-unsynced window for a crash to expose.
            synced: intent.durability == LocalDurability::Synced,
            rejection: None,
        },
    )?;
    let commit = PublishedCommit::new(
        intent.tenant.clone(),
        intent.operation.clone(),
        StorageSequence::new(sequence),
        intent.durability,
        published_writes,
    );
    insert_journal_row(transaction, sequence, &commit)?;
    update_root_head(transaction, root.as_str())?;
    root.clone_into(root_head);
    Ok(CommitResult::Committed(StorageReceipt {
        sequence: StorageSequence::new(sequence),
        root: StorageRootId::from_recorded(root),
        durability: intent.durability,
    }))
}

/// The durable ledger view of one operation identity.
struct LedgerEntryView {
    digest: u64,
    sequence: u64,
    root: String,
    requested: LocalDurability,
    synced: bool,
    rejection: Option<CommitRejection>,
}

/// One ledger row's columns, borrowed from the intent being staged.
struct LedgerRow<'a> {
    tenant: &'a str,
    operation: &'a str,
    digest: u64,
    sequence: u64,
    root: &'a str,
    requested: LocalDurability,
    committed: bool,
    synced: bool,
    rejection: Option<CommitRejection>,
}

/// Reads one operation's durable ledger row.
fn ledger_entry(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    operation: &OperationId,
) -> Result<Option<LedgerEntryView>, ()> {
    let row = transaction.query_row(
        "SELECT digest, sequence, root, requested, synced, rejection FROM ledger
         WHERE tenant = ?1 AND operation = ?2",
        rusqlite::params![tenant.as_str(), operation.as_str()],
        |row| {
            Ok((
                row.get::<_, i64>(0)?,
                row.get::<_, i64>(1)?,
                row.get::<_, String>(2)?,
                row.get::<_, u8>(3)?,
                row.get::<_, i64>(4)?,
                row.get::<_, u8>(5)?,
            ))
        },
    );
    let (digest, sequence, root, requested, synced, rejection) = match row {
        Ok(row) => row,
        Err(rusqlite::Error::QueryReturnedNoRows) => return Ok(None),
        Err(_) => return Err(()),
    };
    Ok(Some(LedgerEntryView {
        // The writes narrow u64 hashes through SQLite's signed integers; the
        // byte round-trip reads them back losslessly for every value, high
        // bit included, so a replay never fails on its own digest.
        digest: u64::from_be_bytes(digest.to_be_bytes()),
        sequence: u64::from_be_bytes(sequence.to_be_bytes()),
        root,
        requested: lsm::durability_of(requested).ok_or(())?,
        synced: synced != 0,
        rejection: (rejection != 0).then(|| lsm::decode_rejection(rejection)).flatten(),
    }))
}

/// Writes one ledger row.
#[allow(clippy::too_many_arguments, reason = "the ledger row columns")]
fn insert_ledger_row(
    transaction: &rusqlite::Transaction<'_>,
    row: &LedgerRow<'_>,
) -> Result<(), ()> {
    transaction
        .prepare_cached(
            "INSERT INTO ledger
             (tenant, operation, digest, sequence, root, requested, committed, synced, rejection)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![
                row.tenant,
                row.operation,
                row.digest as i64,
                row.sequence as i64,
                row.root,
                lsm::durability_code(row.requested),
                i64::from(row.committed),
                i64::from(row.synced),
                row.rejection.map_or(0, lsm::rejection_code),
            ])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// Writes one document version row (a tombstone carries an empty body).
#[allow(clippy::too_many_arguments, reason = "one version row columns")]
fn insert_document_row(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    sequence: u64,
    version: u64,
    deleted: bool,
    body: &[u8],
) -> Result<(), ()> {
    transaction
        .prepare_cached(
            "INSERT INTO documents
             (tenant, collection, id, commit_seq, version, deleted, body)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![
                tenant.as_str(),
                collection.as_str(),
                id.as_str(),
                sequence as i64,
                version as i64,
                i64::from(deleted),
                body,
            ])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// Writes one publication-history journal row (the checksummed commit frame
/// the LSM history keyspace stores).
fn insert_journal_row(
    transaction: &rusqlite::Transaction<'_>,
    sequence: u64,
    commit: &PublishedCommit,
) -> Result<(), ()> {
    let frame = ledger::encode_commit_frame(commit);
    transaction
        .prepare_cached(
            "INSERT INTO journal (sequence, tenant, operation, frame) VALUES (?1, ?2, ?3, ?4)",
        )
        .and_then(|mut statement| {
            statement.execute(rusqlite::params![
                sequence as i64,
                commit.tenant().as_str(),
                commit.operation_id().as_str(),
                frame,
            ])
        })
        .map(|_| ())
        .map_err(|_| ())
}

/// Advances the recorded root chain head.
fn update_root_head(transaction: &rusqlite::Transaction<'_>, root: &str) -> Result<(), ()> {
    transaction
        .prepare_cached("UPDATE meta SET value = ?1 WHERE key = 'root_head'")
        .and_then(|mut statement| statement.execute(rusqlite::params![root]))
        .map(|_| ())
        .map_err(|_| ())
}

/// The highest sequence any durable row names. Commits' ledger rows and
/// usage facts share one sequence space, exactly the reference engines'
/// watermark rule.
fn durable_watermark(connection: &Connection) -> Result<u64, ()> {
    let watermark = connection
        .query_row(
            "SELECT COALESCE(MAX(sequence), 0) FROM (
                 SELECT MAX(sequence) AS sequence FROM ledger
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
        || !lsm::matches_expectation(snapshot_version, expected))
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
    Ok(in_snapshot != in_published || in_published || !lsm::matches_expectation(None, expected))
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

/// Decodes one checksummed journal frame into its commit.
fn decode_journal_frame(frame: &[u8]) -> Option<PublishedCommit> {
    let mut reader = codec::Reader::new(frame);
    let length = usize::try_from(reader.read_u32()?).ok()?;
    let checksum = reader.read_u64()?;
    let payload = reader.take(length)?;
    (codec::fnv1a(payload) == checksum).then(|| ledger::decode_commit_payload(payload)).flatten()
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
    connection
        .execute_batch("PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;")
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
        let reader = self.reader();
        let connection = reader.connection();
        let (requested, rejection, sequence, synced) = connection
            .query_row(
                "SELECT requested, rejection, sequence, synced FROM ledger
                 WHERE tenant = ?1 AND operation = ?2",
                rusqlite::params![tenant.as_str(), operation.as_str()],
                |row| {
                    Ok((
                        row.get::<_, u8>(0)?,
                        row.get::<_, u8>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, i64>(3)?,
                    ))
                },
            )
            .ok()?;
        let requested = lsm::durability_of(requested)?;
        if rejection != 0 {
            return Some(OperationResolution::Rejected(lsm::decode_rejection(rejection)?));
        }
        let proven = if synced != 0 { LocalDurability::Synced } else { LocalDurability::Written };
        Some(OperationResolution::Committed {
            sequence: StorageSequence::new(u64::try_from(sequence).ok()?),
            durability: min_level(requested, proven),
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
                "SELECT frame FROM journal WHERE tenant = ?1 AND sequence <= ?2
                 ORDER BY sequence",
            )
            .ok()?;
        let rows = statement
            .query_map(rusqlite::params![tenant.as_str(), sequence.get() as i64], |row| {
                row.get::<_, Vec<u8>>(0)
            })
            .ok()?;
        // ponytail: O(retained history) per call, matching the LSM's
        // history_index walk; index the journal by key when bulk export on
        // this backend gets hot.
        for frame in rows.flatten() {
            let commit = decode_journal_frame(&frame)?;
            for write in commit.writes() {
                let (collection, identifier, put) = match write {
                    PublishedWrite::Put { document } => {
                        (document.collection().as_str(), document.id().as_str(), true)
                    }
                    PublishedWrite::Delete { collection: Some(collection), id, .. } => {
                        (collection.as_str(), id.as_str(), false)
                    }
                    PublishedWrite::Delete { collection: None, .. } => continue,
                };
                for (index, (key_collection, key_identifier)) in keys.iter().enumerate() {
                    if *key_collection == collection && *key_identifier == identifier {
                        winners[index] = put.then_some(commit.durability());
                    }
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
            "SELECT frame FROM journal WHERE tenant = ?1 AND sequence > ?2
             ORDER BY sequence LIMIT ?3",
        ) else {
            return Vec::new();
        };
        let Ok(rows) = statement.query_map(
            rusqlite::params![tenant.as_str(), after.get() as i64, i64::from(limit)],
            |row| row.get::<_, Vec<u8>>(0),
        ) else {
            return Vec::new();
        };
        rows.flatten().filter_map(|frame| decode_journal_frame(&frame)).collect()
    }

    fn committed_tenants(&self) -> Vec<TenantId> {
        let reader = self.reader();
        let connection = reader.connection();
        let Ok(mut statement) =
            connection.prepare_cached("SELECT DISTINCT tenant FROM journal ORDER BY tenant")
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
                "SELECT MAX(sequence) FROM journal WHERE tenant = ?1 AND sequence <= ?2",
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
        // The pruning floor bounds what the journal can still serve; with
        // no consumer registered, the oldest retained journal row is the
        // bound.
        history_floor_on(connection)
            .ok()
            .flatten()
            .or_else(|| {
                connection
                    .query_row("SELECT MIN(sequence) FROM journal", [], |row| {
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
            .query_row("SELECT MAX(sequence) FROM journal", [], |row| row.get::<_, Option<i64>>(0))
            .ok()
            .flatten()
            .and_then(|sequence| u64::try_from(sequence).ok())
            .map(StorageSequence::new)
    }

    fn journal_divergence(&self) -> u64 {
        // Documents, ledger row, and journal row commit in one `SQLite`
        // transaction: a published commit without its journal record is
        // structurally impossible.
        0
    }

    fn published_state(&self) -> (StorageSequence, StorageRootId) {
        let reader = self.reader();
        let connection = reader.connection();
        // One statement keeps the sequence and the root head on one read
        // snapshot. The watermark is the shared sequence space's maximum —
        // commits' ledger rows and usage facts — matching the reference
        // engines (with rejections consuming no sequence, an empty
        // usage table leaves it exactly the last commit's sequence).
        connection
            .query_row(
                "SELECT (SELECT COALESCE(MAX(sequence), 0) FROM (
                            SELECT MAX(sequence) AS sequence FROM ledger
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

    fn compact(
        &mut self,
        _request: StorageCompaction,
    ) -> Result<(CompactionReceipt, CompactionDriverFacts), CompactionError> {
        // The lifecycle sweep first (the bounded-journal invariant): prune
        // what every consumer frontier has passed, so the receipt below
        // measures the reclaimed state. With no consumer registered the
        // sweep is a no-op.
        self.prune_below_consumer_frontiers().map_err(|()| CompactionError::Storage)?;
        // Then the reclaim this backend owns: checkpoint the WAL back into
        // the database file. Policy-gated history reclaim has no analogue
        // here — journal bounding is the consumer-frontier lifecycle's.
        let wal_before = wal_file_bytes(&self.database_path);
        let writer = self.writer.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        writer
            .query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |_| Ok(()))
            .map_err(|_| CompactionError::Storage)?;
        let wal_after = wal_file_bytes(&self.database_path);
        let journal_bytes = journal_length(&writer)?;
        let journal_records = journal_count(&writer)?;
        let servable_start = journal_min(&writer)?;
        let journaled_head = journal_max(&writer)?;
        let sequence = durable_watermark(&writer).map_err(|()| CompactionError::Storage)?;
        let journal_bytes = u64::try_from(journal_bytes).unwrap_or(0);
        let records = u64::try_from(journal_records).unwrap_or(0);
        let boundary = |value: i64| StorageSequence::new(u64::try_from(value).unwrap_or(0));
        Ok((
            CompactionReceipt::from_parts(
                Some(StorageSequence::new(sequence)),
                wal_before,
                wal_after,
                journal_bytes,
                journal_bytes,
                records,
                records,
                0,
                0,
                0,
                servable_start.map(boundary),
                servable_start.map(boundary),
                journaled_head.map(boundary),
                0,
                0,
                0,
                0,
                super::HistoryReclaimOutcome::NotRequested,
                vec![super::CompactionPhase::Checkpoint, super::CompactionPhase::WalReclaim],
            ),
            CompactionDriverFacts::from_parts(Vec::new(), None, journaled_head.map(boundary)),
        ))
    }

    fn retained_state(&self) -> super::compaction::RetainedState {
        super::compaction::RetainedState {
            bound: None,
            facts: Vec::new(),
            non_deletion: Vec::new(),
        }
    }

    // The `SQLite` backend carries no tiers: P4's tier administration is the
    // LSM engine's surface, and this backend answers the typed refusal
    // (the root-file engine's stub shape).
    fn mover_pass(
        &mut self,
        _policy: &lsm::tier::TierMovePolicy,
        _home_region: &str,
    ) -> Result<lsm::tier::MoverOutcome, lsm::tier::TierUnsupported> {
        Err(lsm::tier::TierUnsupported)
    }

    fn tier_snapshot(&self) -> Option<lsm::tier::TierSnapshot> {
        None
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

/// Reads the WAL sidecar's current byte length.
fn wal_file_bytes(database_path: &Path) -> u64 {
    let mut wal_path = database_path.as_os_str().to_owned();
    wal_path.push("-wal");
    std::fs::metadata(std::path::PathBuf::from(wal_path)).map_or(0, |metadata| metadata.len())
}

/// The journal's encoded byte length.
fn journal_length(connection: &Connection) -> Result<i64, CompactionError> {
    connection
        .query_row("SELECT COALESCE(SUM(LENGTH(frame)), 0) FROM journal", [], |row| {
            row.get::<_, i64>(0)
        })
        .map_err(|_| CompactionError::Storage)
}

/// The journal's record count.
fn journal_count(connection: &Connection) -> Result<i64, CompactionError> {
    connection
        .query_row("SELECT COUNT(*) FROM journal", [], |row| row.get::<_, i64>(0))
        .map_err(|_| CompactionError::Storage)
}

/// The journal's oldest sequence.
fn journal_min(connection: &Connection) -> Result<Option<i64>, CompactionError> {
    connection
        .query_row("SELECT MIN(sequence) FROM journal", [], |row| row.get::<_, Option<i64>>(0))
        .map_err(|_| CompactionError::Storage)
}

/// The journal's newest sequence.
fn journal_max(connection: &Connection) -> Result<Option<i64>, CompactionError> {
    connection
        .query_row("SELECT MAX(sequence) FROM journal", [], |row| row.get::<_, Option<i64>>(0))
        .map_err(|_| CompactionError::Storage)
}
