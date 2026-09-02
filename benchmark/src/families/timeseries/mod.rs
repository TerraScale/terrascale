//! Time-series execution (GAP-F04): rollup generation, retention cleanup,
//! and the engine-side drive that orders them.
//!
//! W3-F stored the time-series family's policy — retention window and named
//! rollups — without executing it: rollup generation and retention cleanup
//! did not run, and pages were bounded only by the record's document size
//! (`docs/families/w3-families.md:318-326`). This module is the execution
//! layer the decision record `docs/families/gapf04-timeseries-execution.md`
//! specifies:
//!
//! - **D1 (execution owner).** The steps are engine-side typed operations in
//!   the migration-drive shape: each step opens its own Engine transaction,
//!   stages through the family record path, and commits. The tick — when a
//!   run happens — belongs to the caller (the host's maintenance lane);
//!   V1's `TimeSeriesPolicyRunner` background loop maps onto caller-driven
//!   steps because the engine is synchronous and owns no clock.
//! - **D2 (incremental encoding).** Derived rollup series live inside the
//!   series' own record as a `rollups` section keyed by rollup name. A run
//!   recomputes every complete window deterministically and replaces the
//!   section, so a re-run without new raw points stages nothing.
//! - **D3 (retention horizon).** Raw points survive while their rollup
//!   window is incomplete, and the horizon is the read path's own
//!   `newest − retention_ms`.
//! - **D4 (paging).** The family read seam's bounded cursor-stable paging
//!   ([`TimeSeriesPaging`](crate::families::TimeSeriesPaging)) is
//!   implemented in the store's read path; this module changes no read
//!   behavior.
//!
//! The drive order is rollups, then retention, so the retention half sees
//! every complete window already aggregated.

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicU64, Ordering};

use super::state::{Records, TimeSeriesRecord, record_id};
use super::{Aggregation, FamilyError, TimeSeriesAppend, TimeSeriesPolicy};
use crate::engine::{CommitOutcome, DurableResolution, Engine, EngineError, Transaction};
use crate::types::{DocumentId, LocalDurability, OperationId, StorageSequence, TenantScope};

/// The receipt of one series' rollup run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RollupReceipt {
    series: String,
    points_written: u64,
    changed: bool,
}

impl RollupReceipt {
    /// Creates one receipt. Crate-only.
    pub(crate) fn new(series: &str, points_written: u64, changed: bool) -> Self {
        Self { series: series.to_owned(), points_written, changed }
    }

    /// Returns the series the run addressed.
    #[must_use]
    pub fn series(&self) -> &str {
        &self.series
    }

    /// Returns how many rollup points the run wrote across every section.
    #[must_use]
    pub const fn points_written(&self) -> u64 {
        self.points_written
    }

    /// Returns whether the run staged anything. `false` is the idempotent
    /// no-op: the recomputed sections were identical, so nothing committed.
    #[must_use]
    pub const fn changed(&self) -> bool {
        self.changed
    }
}

/// The receipt of one series' retention run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RetentionReceipt {
    series: String,
    horizon: Option<i64>,
    entries_deleted: u64,
    skipped_document_evidence: bool,
    changed: bool,
}

impl RetentionReceipt {
    /// Creates one receipt. Crate-only.
    pub(crate) fn new(
        series: &str,
        horizon: Option<i64>,
        entries_deleted: u64,
        skipped_document_evidence: bool,
        changed: bool,
    ) -> Self {
        Self {
            series: series.to_owned(),
            horizon,
            entries_deleted,
            skipped_document_evidence,
            changed,
        }
    }

    /// Returns the series the run addressed.
    #[must_use]
    pub fn series(&self) -> &str {
        &self.series
    }

    /// Returns the retention horizon the run applied, or `None` when the
    /// series carries no retention window.
    #[must_use]
    pub const fn horizon(&self) -> Option<i64> {
        self.horizon
    }

    /// Returns how many raw entries the run deleted below the horizon.
    #[must_use]
    pub const fn entries_deleted(&self) -> u64 {
        self.entries_deleted
    }

    /// Returns whether the run skipped the series without staging anything
    /// (decision D3): the pass touched nothing and this flag discloses it.
    #[must_use]
    pub const fn skipped_document_evidence(&self) -> bool {
        self.skipped_document_evidence
    }

    /// Returns whether the run staged anything.
    #[must_use]
    pub const fn changed(&self) -> bool {
        self.changed
    }
}

/// The receipt of one series' full drive: the rollup half then the
/// retention half, in V1's order.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SeriesRunReceipt {
    rollups: RollupReceipt,
    retention: RetentionReceipt,
}

impl SeriesRunReceipt {
    /// Creates one receipt. Crate-only.
    pub(crate) fn new(rollups: RollupReceipt, retention: RetentionReceipt) -> Self {
        Self { rollups, retention }
    }

    /// Returns the rollup half's receipt.
    #[must_use]
    pub const fn rollups(&self) -> &RollupReceipt {
        &self.rollups
    }

    /// Returns the retention half's receipt.
    #[must_use]
    pub const fn retention(&self) -> &RetentionReceipt {
        &self.retention
    }
}

/// Runs one series' rollup generation (decision D2).
///
/// Every complete window of every configured rollup policy is recomputed
/// deterministically and the record's `rollups` sections are replaced. A
/// run whose recompute is identical to the stored sections stages nothing
/// and commits nothing — the idempotent no-op. A series without a record or
/// without a policy is a reported no-op, never an error.
///
/// # Errors
///
/// Returns the family seam's typed failures: a reserved-state collision or
/// undecodable record is [`FamilyError::ReservedStateInvalid`], a closed
/// engine is [`FamilyError::EngineClosed`], and a failed commit is
/// [`FamilyError::StorageFailure`]. A refused run stages nothing.
pub fn rollup_series(
    engine: &Engine,
    scope: &TenantScope,
    document: &DocumentId,
    name: &str,
    now_ms: i64,
) -> Result<RollupReceipt, FamilyError> {
    let snapshot = engine.read_snapshot(scope).map_err(|error| map_engine_error(&error))?;
    let operation = unique_operation("timeseries-rollup");
    let mut transaction = engine
        .begin(scope, snapshot, operation.clone())
        .map_err(|error| map_engine_error(&error))?;
    let id = record_id("time-series", &[document.as_str(), name])?;
    let staged = {
        let mut records = Records::new(&mut transaction);
        let mut record = match records.read(&id)? {
            Some(value) => TimeSeriesRecord::decode(&value)?,
            None => return Ok(RollupReceipt::new(name, 0, false)),
        };
        let Some(policy) = record.policy.clone() else {
            return Ok(RollupReceipt::new(name, 0, false));
        };
        let computed = compute_rollups(&record.entries, &policy, now_ms);
        if computed == record.rollups {
            return Ok(RollupReceipt::new(name, 0, false));
        }
        let points_written =
            u64::try_from(computed.values().map(Vec::len).sum::<usize>()).unwrap_or(u64::MAX);
        record.rollups = computed;
        records.write(id, record.encode())?;
        points_written
    };
    commit_step(engine, scope, transaction, operation)?;
    Ok(RollupReceipt::new(name, staged, true))
}

/// Runs one series' retention cleanup (decision D3).
///
/// The horizon is the read path's own `newest raw timestamp − retention_ms`;
/// raw entries strictly below it are dropped and every rollup section is
/// trimmed to it — but a raw entry survives while any configured rollup
/// policy's window containing it is incomplete.
///
/// # Errors
///
/// Returns the family seam's typed failures, exactly as
/// [`rollup_series`]. A refused run stages nothing.
pub fn enforce_retention(
    engine: &Engine,
    scope: &TenantScope,
    document: &DocumentId,
    name: &str,
    now_ms: i64,
) -> Result<RetentionReceipt, FamilyError> {
    let snapshot = engine.read_snapshot(scope).map_err(|error| map_engine_error(&error))?;
    let operation = unique_operation("timeseries-retention");
    let mut transaction = engine
        .begin(scope, snapshot, operation.clone())
        .map_err(|error| map_engine_error(&error))?;
    let id = record_id("time-series", &[document.as_str(), name])?;
    let staged = {
        let mut records = Records::new(&mut transaction);
        let mut record = match records.read(&id)? {
            Some(value) => TimeSeriesRecord::decode(&value)?,
            None => return Ok(RetentionReceipt::new(name, None, 0, false, false)),
        };
        let Some(policy) = record.policy.clone() else {
            return Ok(RetentionReceipt::new(name, None, 0, false, false));
        };
        let Some(retention_ms) = policy.retention_ms() else {
            return Ok(RetentionReceipt::new(name, None, 0, false, false));
        };
        let Some(newest) = record.entries.last().map(TimeSeriesAppend::timestamp) else {
            return Ok(RetentionReceipt::new(name, None, 0, false, false));
        };
        let bound = newest.saturating_sub(retention_ms);
        // D3.2: a raw entry is deletable only below the horizon and only
        // when every configured rollup window containing it is complete.
        let before = record.entries.len();
        record.entries.retain(|entry| {
            entry.timestamp() >= bound
                || policy
                    .rollups()
                    .iter()
                    .any(|rollup| window_end(entry.timestamp(), rollup.window_ms()) > now_ms)
        });
        let deleted = u64::try_from(before - record.entries.len()).unwrap_or(u64::MAX);
        // D3.1: the rollup sections age to the same horizon; a section that
        // empties is dropped.
        let sections_before = record.rollups.clone();
        record.rollups.retain(|_, section| {
            section.retain(|entry| entry.timestamp() >= bound);
            !section.is_empty()
        });
        if deleted == 0 && sections_before == record.rollups {
            // Nothing is deletable yet: the horizon is still reported so the
            // caller can observe the bound the pass applied.
            return Ok(RetentionReceipt::new(name, Some(bound), 0, false, false));
        }
        let empty =
            record.entries.is_empty() && record.policy.is_none() && record.rollups.is_empty();
        if empty {
            records.clear(&id)?;
        } else {
            records.write(id, record.encode())?;
        }
        (Some(bound), deleted)
    };
    let (horizon, deleted) = staged;
    commit_step(engine, scope, transaction, operation)?;
    Ok(RetentionReceipt::new(name, horizon, deleted, false, true))
}

/// Runs one series' full maintenance drive: rollups, then retention (V1's
/// `DoWork` order).
///
/// # Errors
///
/// Returns whichever half's typed failure occurred first; a failed first
/// half never reaches the second.
pub fn run_series(
    engine: &Engine,
    scope: &TenantScope,
    document: &DocumentId,
    name: &str,
    now_ms: i64,
) -> Result<SeriesRunReceipt, FamilyError> {
    let rollups = rollup_series(engine, scope, document, name, now_ms)?;
    let retention = enforce_retention(engine, scope, document, name, now_ms)?;
    Ok(SeriesRunReceipt::new(rollups, retention))
}

/// Runs every time-series of one tenant: the maintenance tick's drive unit.
///
/// The series of the tenant's snapshot are enumerated through the backup
/// seam's family-record snapshot (`FamilyStore::snapshot_family_records`),
/// and each series runs the full drive in record order. A record identifier
/// is `family:time-series/{document}/{series}` and series names reject the
/// `/` separator, so the final segment names the series and everything
/// before it names the owning document — the same split the record
/// identifier's own construction (`record_id`) guarantees is unambiguous.
/// A later series' failure stops the drive and is returned; every earlier
/// series' committed step stands.
///
/// # Errors
///
/// Returns the enumeration's typed failures (a snapshot of another engine,
/// a reserved-state collision) or the first failing series' step failure.
pub fn run_tenant(
    engine: &Engine,
    scope: &TenantScope,
    now_ms: i64,
) -> Result<Vec<SeriesRunReceipt>, FamilyError> {
    let snapshot = engine.read_snapshot(scope).map_err(|error| map_engine_error(&error))?;
    let records = engine.families().snapshot_family_records(scope, &snapshot)?;
    let mut receipts = Vec::new();
    for record in records {
        if record.kind() != super::FamilyKind::TimeSeries {
            continue;
        }
        let Some((owner, series)) = record.id().as_str().rsplit_once('/') else {
            continue;
        };
        let Some(document) = owner.strip_prefix(SERIES_RECORD_PREFIX) else {
            continue;
        };
        let document =
            DocumentId::try_new(document).map_err(|_| FamilyError::ReservedStateInvalid)?;
        receipts.push(run_series(engine, scope, &document, series, now_ms)?);
    }
    Ok(receipts)
}

/// The reserved-identifier prefix of one series record: everything after it
/// in the owner segment of `family:time-series/{document}/{series}` is the
/// owning document's own identifier.
const SERIES_RECORD_PREFIX: &str = "family:time-series/";

/// Recomputes every complete window of every configured rollup policy.
///
/// Window boundaries floor Euclidean, so negative epoch timestamps land in
/// the window that covers them; a window is complete when its end is at or
/// before `now_ms`; the aggregated point's timestamp is the window start
/// and its values are the policy's aggregation of each raw value column.
fn compute_rollups(
    entries: &[TimeSeriesAppend],
    policy: &TimeSeriesPolicy,
    now_ms: i64,
) -> BTreeMap<String, Vec<TimeSeriesAppend>> {
    let mut sections = BTreeMap::new();
    for rollup in policy.rollups() {
        let window = rollup.window_ms();
        if window <= 0 {
            // The stage seam rejects non-positive windows; a record that
            // carries one anyway contributes no section.
            continue;
        }
        let mut by_window: BTreeMap<i64, Vec<&TimeSeriesAppend>> = BTreeMap::new();
        for entry in entries {
            let start = entry.timestamp().div_euclid(window) * window;
            if window_end(entry.timestamp(), window) <= now_ms {
                by_window.entry(start).or_default().push(entry);
            }
        }
        let section = by_window
            .into_iter()
            .map(|(start, group)| {
                TimeSeriesAppend::new(start, aggregate(&group, rollup.aggregation()), None)
            })
            .collect();
        sections.insert(rollup.name().to_owned(), section);
    }
    sections
}

/// Returns the exclusive end of the window of `window_ms` containing `t`.
fn window_end(timestamp: i64, window_ms: i64) -> i64 {
    timestamp.div_euclid(window_ms).saturating_mul(window_ms).saturating_add(window_ms)
}

/// Aggregates one window's entries column by column.
///
/// A column missing from an entry (impossible through the stage seam's
/// arity rule) contributes nothing rather than panicking.
#[allow(
    clippy::cast_precision_loss,
    reason = "count widens a small entry count into the values' representation"
)]
fn aggregate(group: &[&TimeSeriesAppend], aggregation: Aggregation) -> Vec<f64> {
    let Some(arity) = group.first().map(|entry| entry.values().len()) else {
        return Vec::new();
    };
    let mut values = Vec::with_capacity(arity);
    for column in 0..arity {
        let column_values = group.iter().filter_map(|entry| entry.values().get(column).copied());
        let value = match aggregation {
            Aggregation::Min => column_values.fold(f64::INFINITY, f64::min),
            Aggregation::Max => column_values.fold(f64::NEG_INFINITY, f64::max),
            Aggregation::Sum => column_values.fold(0.0, |sum, value| sum + value),
            Aggregation::Mean => {
                let mut count = 0_u64;
                let sum = column_values.fold(0.0, |sum, value| {
                    count += 1;
                    sum + value
                });
                if count == 0 { f64::NAN } else { sum / count as f64 }
            }
            Aggregation::Count => column_values.count() as f64,
        };
        values.push(value);
    }
    values
}

/// Maps engine snapshot and begin failures to the family vocabulary.
fn map_engine_error(error: &EngineError) -> FamilyError {
    match error {
        EngineError::SnapshotEngineMismatch | EngineError::ScopeMismatch => {
            FamilyError::SnapshotEngineMismatch
        }
        EngineError::SnapshotTenantMismatch => FamilyError::TenantMismatch,
        EngineError::Closed(_) => FamilyError::EngineClosed,
        _ => FamilyError::StorageFailure,
    }
}

/// Commits one step transaction at the strongest local durability point,
/// resolving an uncertain outcome through the durable operation ledger —
/// the migration drive's `commit_step`, verbatim in shape.
fn commit_step(
    engine: &Engine,
    scope: &TenantScope,
    transaction: Transaction<'_>,
    operation: OperationId,
) -> Result<(), FamilyError> {
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => Ok(()),
        CommitOutcome::Rejected(_) => Err(FamilyError::StorageFailure),
        CommitOutcome::Unknown(_) => match engine.resolve_commit(scope, operation) {
            Ok(DurableResolution::Committed(_)) => Ok(()),
            Ok(_) | Err(_) => Err(FamilyError::StorageFailure),
        },
    }
}

/// Mints a never-reused operation identity for one step transaction.
fn unique_operation(prefix: &str) -> OperationId {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("{prefix}-{index}")).expect("valid operation identity")
}

#[cfg(test)]
mod tests;
