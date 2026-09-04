//! Journal-independent catch-up over range-based set reconciliation (D-45):
//! state comparison, bounded repair, and snapshot bootstrap.
//!
//! The journal tailer ([`crate::replication`], [`crate::replication_lanes`])
//! converges members while the publication journal still holds the writes.
//! This module owns what happens when it cannot: the cursor is past
//! retention, the journal never saw the writes (a partition predates the
//! tailer), or a member restarts so far behind that replaying is the wrong
//! shape. The decision surface is the transport-free
//! [`RangeSummaryTable`](terrabase_replication_core::anti_entropy::RangeSummaryTable):
//! additive fingerprints descend through fixed-fanout sequence ranges and
//! exact ids cross the boundary only for differing leaves.
//!
//! - **Journal rows**: [`JournalRowSource`] supplies the persisted
//!   `(origin, sequence) -> hash` set, including tombstones. No materialized
//!   document scan can stand in for this set because it loses history and
//!   cannot distinguish a tombstone from an absent key.
//! - **Bounded repair**: [`reconcile_journal_rows`] follows the range plan
//!   over those stable identities. `EngineHost` now supplies the persisted
//!   rows; missing-row application remains snapshot-gated until the repair
//!   transfer carries the source mutation and source dot.
//! - **Snapshot bootstrap**: [`snapshot_bootstrap`] copies the peer's
//!   quiesced durable file set into a fresh location — the in-process form
//!   of installing the region-local Litestream snapshot (production reads
//!   the snapshot artifact from its own region's object storage via the
//!   S-06 `BackupStreams::restore_to_location` path, which lands the
//!   identical file set). The tailer's cursor resumes from
//!   [`journal_end`] past the snapshot, so pre-snapshot rows never re-ship.
//!
//! Composition barrier, stated exactly:
//!
//! - Snapshot bootstrap assumes quiescent members — no commit wave in flight
//!   on either side — the same barrier the WAL fold and follower bootstrap
//!   already take.
//! - A tombstone remains a durable row in the source set. Its identity and
//!   hash differ from complete absence, so a missing tombstone is always a
//!   repair difference rather than an accidental no-op.

use std::path::Path;

use terrabase::streaming::ChangeCheckpoint;
use terrabase_replication_core::anti_entropy::{
    Hash32, JournalRowId, RangeSummaryTable, ReconciliationPlan, RepairError, RowSummary,
};

use crate::engine_host::EngineHost;
use crate::host::HostingError;
use crate::registry::BoundScope;

/// The bounded count of key ranges one repair pass may ship. Past this
/// budget the members diverge by more than a catch-up pass should move and
/// the caller bootstraps from a snapshot instead.
pub const MAX_REPAIR_RANGES: usize = 64;

/// One leaf's maximum row span.  A repair pass never needs to retain more
/// than this many exact rows for a differing leaf; the journal scan uses the
/// same span as its per-range memory/work bound.
#[allow(
    clippy::cast_possible_truncation,
    reason = "DEFAULT_LEAF_ROWS is 4096 and fits in usize on every supported target"
)]
pub const MAX_REPAIR_ROWS_PER_RANGE: usize =
    terrabase_replication_core::anti_entropy::DEFAULT_LEAF_ROWS as usize;

/// What one repair pass decided.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RepairDecision {
    /// Both members serve the identical state; `repaired` records published
    /// on this pass (replays and merge-superseded records count zero).
    Converged {
        /// Records this pass published.
        repaired: usize,
    },
    /// The divergence exceeds the range budget: bootstrap the lagging
    /// member from a snapshot ([`snapshot_bootstrap`]) instead of repairing.
    NeedsSnapshot {
        /// Differing ranges the plan found.
        ranges: usize,
        /// Budget the pass ran under.
        max_ranges: usize,
    },
}

/// One durable publication-journal row supplied to anti-entropy.
///
/// The storage owner must provide the identity and hash that were persisted
/// with the row. In particular, callers must not derive `sequence` from a
/// live-document scan: one document may have many journal rows, and a
/// tombstone is a row even when the materialized document is absent.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurableJournalRow {
    /// Stable `(origin, per-origin sequence)` identity.
    pub id: JournalRowId,
    /// Hash of the canonical durable journal-row bytes.
    pub row_hash: Hash32,
}

/// Result of a bounded journal summary scan. `complete` is false when the
/// source has more retained rows than this pass is willing to inspect. That
/// is a snapshot decision, never an empty or converged result.
pub struct JournalSummary {
    /// Range summaries inspected during this pass.
    pub table: RangeSummaryTable,
    /// Whether the source was fully inspected under the row bound.
    pub complete: bool,
}

impl DurableJournalRow {
    /// Constructs one durable row summary.
    #[must_use]
    pub const fn new(id: JournalRowId, row_hash: Hash32) -> Self {
        Self { id, row_hash }
    }
}

/// Narrow storage seam required by range anti-entropy.
///
/// Implementations must read the durable journal rows for one bound scope,
/// including tombstones, and return their persisted `(origin, sequence)` and
/// canonical row hash.
pub trait JournalRowSource {
    /// Reads all retained durable journal rows for a scope in any order.
    ///
    /// Implementations may return rows in any order; the summary table is
    /// order-independent and keys every row by its supplied identity.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError`] when the durable journal cannot be read
    /// under the bound scope.
    fn durable_journal_rows(
        &self,
        scope: &BoundScope,
    ) -> Result<Vec<DurableJournalRow>, HostingError>;

    /// Builds a bounded summary for one repair pass. Implementations backed
    /// by a pager should override this to avoid materializing all rows; the
    /// fallback remains bounded at the summary boundary and propagates every
    /// source error.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Invalid`] for a zero row budget and
    /// [`HostingError`] when the durable journal cannot be read or holds a
    /// duplicate row identity.
    fn durable_journal_summary(
        &self,
        scope: &BoundScope,
        max_rows: usize,
    ) -> Result<JournalSummary, HostingError> {
        if max_rows == 0 {
            return Err(HostingError::Invalid("the journal row budget must be nonzero".into()));
        }
        let rows = self.durable_journal_rows(scope)?;
        let complete = rows.len() <= max_rows;
        let mut table = RangeSummaryTable::new();
        for row in rows.into_iter().take(max_rows) {
            let summary = RowSummary::new(row.id, row.row_hash);
            if table.insert(&summary).is_some() {
                return Err(HostingError::EngineFailure(
                    "the durable anti-entropy journal contains a duplicate row identity".into(),
                ));
            }
        }
        Ok(JournalSummary { table, complete })
    }
}

/// Builds a range reconciliation plan from two durable journal row sets.
///
/// Every row identity is taken verbatim from the source; no
/// document key, scan index, or synthetic origin is introduced here.
///
/// # Errors
///
/// Returns [`RepairError::DuplicateRowIdentity`] when either set holds a
/// duplicate row identity, [`RepairError::LayoutMismatch`] for mixed
/// layouts, or [`RepairError::RepairBudgetExceeded`] when the differing
/// rows exceed `max_differences`.
pub fn reconcile_journal_rows(
    local: &[DurableJournalRow],
    peer: &[DurableJournalRow],
    max_differences: usize,
) -> Result<ReconciliationPlan, RepairError> {
    fn table(rows: &[DurableJournalRow]) -> Result<RangeSummaryTable, RepairError> {
        let mut table = RangeSummaryTable::new();
        for row in rows {
            if table.insert(&RowSummary::new(row.id.clone(), row.row_hash)).is_some() {
                return Err(RepairError::DuplicateRowIdentity);
            }
        }
        Ok(table)
    }

    table(local)?.reconcile(&table(peer)?, max_differences)
}

/// One repair pass between two members of the same database.
///
/// # Errors
///
/// The host compares its persisted journal rows. A divergent row set is
/// reported as snapshot work until the transfer path carries reconstructable
/// mutations and source dots.
pub fn repair_once(
    local: &EngineHost,
    peer: &EngineHost,
    scope: &BoundScope,
) -> Result<RepairDecision, HostingError> {
    repair_once_bounded(local, peer, scope, MAX_REPAIR_RANGES)
}

/// One repair pass under an explicit range budget.
///
/// # Errors
///
/// Returns [`HostingError::Invalid`] for a zero budget, and an explicit
/// unavailable-source error until the host is wired to a journal source.
pub fn repair_once_bounded(
    local: &EngineHost,
    peer: &EngineHost,
    scope: &BoundScope,
    max_ranges: usize,
) -> Result<RepairDecision, HostingError> {
    repair_once_sources_bounded(local, peer, scope, max_ranges)
}

/// Runs a bounded pass against two journal sources. This seam is useful for
/// transports and for proving that a source read failure cannot be mistaken
/// for an empty, converged journal.
///
/// # Errors
///
/// Returns [`HostingError::Invalid`] for a zero range budget and
/// [`HostingError`] when either journal source cannot be read.
pub fn repair_once_sources_bounded<L, R>(
    local: &L,
    peer: &R,
    scope: &BoundScope,
    max_ranges: usize,
) -> Result<RepairDecision, HostingError>
where
    L: JournalRowSource,
    R: JournalRowSource,
{
    if max_ranges == 0 {
        return Err(HostingError::Invalid("the repair range budget must be nonzero".into()));
    }
    let row_budget = max_ranges.saturating_mul(MAX_REPAIR_ROWS_PER_RANGE).max(1);
    let local_summary = JournalRowSource::durable_journal_summary(local, scope, row_budget)?;
    let peer_summary = JournalRowSource::durable_journal_summary(peer, scope, row_budget)?;
    if !local_summary.complete || !peer_summary.complete {
        return Ok(RepairDecision::NeedsSnapshot {
            ranges: max_ranges.saturating_add(1),
            max_ranges,
        });
    }
    let plan = match local_summary.table.reconcile(&peer_summary.table, max_ranges) {
        Ok(plan) => plan,
        Err(RepairError::RepairBudgetExceeded { differences, max_differences }) => {
            return Ok(RepairDecision::NeedsSnapshot {
                ranges: differences,
                max_ranges: max_differences,
            });
        }
        Err(error) => {
            return Err(HostingError::EngineFailure(format!(
                "the anti-entropy repair plan failed: {error:?}"
            )));
        }
    };
    if plan.is_converged() {
        Ok(RepairDecision::Converged { repaired: 0 })
    } else {
        // Journal rows are now compared correctly, but applying a missing row
        // still requires the source's reconstructable mutation payload and
        // the source dot on the replication envelope. Keep the operation
        // fail-closed until that transport carries both facts.
        Ok(RepairDecision::NeedsSnapshot { ranges: plan.differences().len(), max_ranges })
    }
}

/// The journal cursor past everything the member's journal currently holds:
/// the position a link adopts after a snapshot bootstrap so pre-snapshot
/// rows never re-ship (the copied file set already serves them).
///
/// # Errors
///
/// Returns [`HostingError`] when the durable journal cannot be replayed
/// under the bound scope.
pub fn journal_end(
    host: &EngineHost,
    scope: &BoundScope,
) -> Result<Option<ChangeCheckpoint>, HostingError> {
    let mut cursor = None;
    loop {
        let batch = host.replay_sync_journal(scope, cursor)?;
        if batch.next_checkpoint().is_some() {
            cursor = batch.next_checkpoint();
        }
        if batch.drained() {
            return Ok(cursor);
        }
    }
}

/// What a snapshot bootstrap installed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SnapshotBootstrap {
    /// Location files copied, in copy order.
    pub files_copied: Vec<String>,
    /// Whether the peer held a WAL beside its database file.
    pub had_wal: bool,
}

/// Installs the peer's snapshot into a fresh location: copies the quiesced
/// durable file set (the ownership record when present, `terrabase.sqlite3`,
/// and its `-wal` when present) so opening the target
/// serves the peer's state byte-identically — the in-process form of
/// installing the region-local Litestream snapshot. The caller owns the
/// barrier: the peer must be quiescent (no commit wave or checkpoint in
/// flight), exactly the bootstrap contract [`wal_follower`](crate::wal_follower)
/// documents. The target directory must not hold a live engine.
///
/// [`crate::replication_lanes::RegionLink::set_cursor`] adopts
/// [`journal_end`] afterwards; the composition never re-ships what the
/// snapshot already serves.
///
/// # Errors
///
/// Returns [`HostingError::Invalid`] when the target is not a fresh
/// directory, and [`HostingError::EngineFailure`] when a file cannot be
/// copied durably.
pub fn snapshot_bootstrap(
    peer_location: &Path,
    target_location: &Path,
) -> Result<SnapshotBootstrap, HostingError> {
    if target_location.exists() && !is_empty_dir(target_location) {
        return Err(HostingError::Invalid("the snapshot target must be a fresh directory".into()));
    }
    std::fs::create_dir_all(target_location).map_err(|_| {
        HostingError::EngineFailure("the snapshot target refused to initialize".into())
    })?;
    let mut files_copied = Vec::new();
    if copy_optional(peer_location, target_location, "owner.tbv1")? {
        files_copied.push("owner.tbv1".to_owned());
    }
    copy_required(peer_location, target_location, "terrabase.sqlite3")?;
    files_copied.push("terrabase.sqlite3".to_owned());
    let had_wal = copy_optional(peer_location, target_location, "terrabase.sqlite3-wal")?;
    if had_wal {
        files_copied.push("terrabase.sqlite3-wal".to_owned());
    }
    std::fs::File::open(target_location).and_then(|directory| directory.sync_all()).map_err(
        |_| HostingError::EngineFailure("the snapshot target could not be made durable".into()),
    )?;
    Ok(SnapshotBootstrap { files_copied, had_wal })
}

/// Reports whether the path names an empty directory: the snapshot target
/// a bootstrap may populate.
fn is_empty_dir(path: &Path) -> bool {
    std::fs::read_dir(path).is_ok_and(|mut entries| entries.next().is_none())
}

/// Copies one required location file, fsyncing the copy by content before
/// returning. Permissions travel with the bytes.
fn copy_required(peer: &Path, target: &Path, name: &str) -> Result<(), HostingError> {
    let source = peer.join(name);
    let permissions = std::fs::metadata(&source)
        .map_err(|_| HostingError::EngineFailure("the snapshot source is incomplete".into()))?
        .permissions();
    let mut reader = std::fs::File::open(&source)
        .map_err(|_| HostingError::EngineFailure("the snapshot source is incomplete".into()))?;
    let mut writer = std::fs::File::create(target.join(name)).map_err(|_| {
        HostingError::EngineFailure("the snapshot target refused to initialize".into())
    })?;
    std::io::copy(&mut reader, &mut writer)
        .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
    writer
        .sync_all()
        .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
    drop(writer);
    std::fs::set_permissions(target.join(name), permissions)
        .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
    Ok(())
}

/// Copies one optional location file, reporting whether it existed.
fn copy_optional(peer: &Path, target: &Path, name: &str) -> Result<bool, HostingError> {
    match std::fs::metadata(peer.join(name)) {
        Ok(permissions) => {
            let mut reader = std::fs::File::open(peer.join(name)).map_err(|_| {
                HostingError::EngineFailure("the snapshot source is incomplete".into())
            })?;
            let mut writer = std::fs::File::create(target.join(name)).map_err(|_| {
                HostingError::EngineFailure("the snapshot target refused to initialize".into())
            })?;
            std::io::copy(&mut reader, &mut writer)
                .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
            writer
                .sync_all()
                .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
            drop(writer);
            std::fs::set_permissions(target.join(name), permissions.permissions())
                .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
            Ok(true)
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(_) => Err(HostingError::EngineFailure("the snapshot source is incomplete".into())),
    }
}
