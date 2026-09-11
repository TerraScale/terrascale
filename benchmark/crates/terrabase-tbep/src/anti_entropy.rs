//! Journal-independent catch-up over range-based set reconciliation (D-45):
//! state comparison and bounded repair.
//!
//! The journal tailer ([`crate::replication`], [`crate::production_causal`])
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
//!
//! Composition barrier, stated exactly:
//!
//! - A tombstone remains a durable row in the source set. Its identity and
//!   hash differ from complete absence, so a missing tombstone is always a
//!   repair difference rather than an accidental no-op.

use terrabase::engine::ReplicatedRecord;
use terrabase_replication_core::anti_entropy::{
    Hash32, JournalRowId, RangeFingerprint, RangeSummaryTable, ReconciliationPlan, RepairError,
    RowSummary,
};

use crate::host::HostingError;
use crate::registry::BoundScope;

/// One leaf's maximum row span.  A repair pass never needs to retain more
/// than this many exact rows for a differing leaf; the journal scan uses the
/// same span as its per-range memory/work bound.
#[allow(
    clippy::cast_possible_truncation,
    reason = "DEFAULT_LEAF_ROWS is 4096 and fits in usize on every supported target"
)]
pub const MAX_REPAIR_ROWS_PER_RANGE: usize =
    terrabase_replication_core::anti_entropy::DEFAULT_LEAF_ROWS as usize;

/// Maximum number of content-bearing mutations one repair pass may apply.
pub const MAX_REPAIR_MUTATIONS: usize = 256;

/// Maximum encoded mutation bytes one repair pass may transfer.
pub const MAX_REPAIR_MUTATION_BYTES: usize = 4 * 1024 * 1024;

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
    /// member from a snapshot (the production restore path) instead of
    /// repairing.
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

/// A content-bearing journal row together with the hash that authenticates it.
///
/// The hash is checked against the reconciliation row before application and
/// the full row set is reconciled again afterwards.  A transport must not
/// substitute a document scan or a synthetic identity for these fields.
#[derive(Clone, Debug, PartialEq)]
pub struct AuthenticatedMutation {
    /// Tenant whose journal contains the mutation.
    pub tenant: String,
    /// Stable identity and durable row hash advertised by the source.
    pub row: DurableJournalRow,
    /// Reconstructable mutation, including source causal provenance.
    pub mutation: ReplicatedRecord,
    /// Authentication tag over the complete mutation envelope.
    pub authentication: Hash32,
}

impl AuthenticatedMutation {
    /// Builds an authenticated mutation envelope.
    #[must_use]
    pub fn new(
        tenant: impl Into<String>,
        row: DurableJournalRow,
        mutation: ReplicatedRecord,
    ) -> Self {
        let tenant = tenant.into();
        let authentication = mutation_authentication(&tenant, &row, &mutation);
        Self { tenant, row, mutation, authentication }
    }

    /// Returns a conservative encoded size used by the transfer byte budget.
    #[must_use]
    pub fn encoded_len(&self) -> usize {
        self.tenant
            .len()
            .saturating_add(self.row.id.origin().len())
            .saturating_add(self.mutation.collection.as_str().len())
            .saturating_add(self.mutation.id.as_str().len())
            .saturating_add(self.mutation.commit_ulid.len())
            .saturating_add(self.mutation.body_debug_len())
            .saturating_add(8 + 8 + 1 + 32)
    }

    fn verify(&self, scope: &BoundScope) -> Result<(), HostingError> {
        if self.tenant != scope.tenant
            || self.row.id.origin() != self.mutation.origin
            || self.row.id.sequence() != self.mutation.origin_sequence
            || self.authentication
                != mutation_authentication(&self.tenant, &self.row, &self.mutation)
        {
            return Err(HostingError::Invalid(
                "the anti-entropy mutation authentication or scope does not match".into(),
            ));
        }
        Ok(())
    }
}

/// Hashes the immutable transfer envelope.  `Debug` is deterministic here:
/// document objects use `BTreeMap`, and this is an internal authenticated
/// transport seam rather than a public serialization format.
fn mutation_authentication(
    tenant: &str,
    row: &DurableJournalRow,
    mutation: &ReplicatedRecord,
) -> Hash32 {
    let bytes = format!(
        "{tenant}\0{}\0{}\0{}\0{}\0{}\0{}\0{}\0{:?}",
        row.id.origin(),
        row.id.sequence(),
        mutation.collection.as_str(),
        mutation.id.as_str(),
        mutation.commit_ulid,
        mutation.hlc,
        mutation.deleted,
        mutation.body,
    );
    terrabase_replication_core::anti_entropy::row_hash(bytes.as_bytes())
}

trait ReplicatedRecordBodyLen {
    fn body_debug_len(&self) -> usize;
}

impl ReplicatedRecordBodyLen for ReplicatedRecord {
    fn body_debug_len(&self) -> usize {
        format!("{:?}", self.body).len()
    }
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

    /// Returns content-bearing mutations for bounded anti-entropy transfer.
    ///
    /// A source that cannot reconstruct the exact mutation must leave this
    /// seam unavailable; callers then retain the snapshot fallback.
    ///
    /// # Errors
    ///
    /// Returns a hosting error when the adapter cannot read or apply the
    /// authenticated durable mutations under the supplied scope.
    fn durable_mutations(
        &self,
        _scope: &BoundScope,
    ) -> Result<Vec<AuthenticatedMutation>, HostingError> {
        Err(HostingError::EngineFailure("mutation-range transfer is unavailable".into()))
    }

    /// Applies a verified mutation range to this source's member.
    ///
    /// The default is deliberately unavailable for summary-only adapters.
    /// Implementations must apply records idempotently and durably before
    /// returning so the post-apply proof can be trusted.
    ///
    /// # Errors
    ///
    /// Returns a hosting error when the adapter cannot read or apply the
    /// authenticated durable mutations under the supplied scope.
    fn apply_mutations(
        &self,
        _scope: &BoundScope,
        _mutations: &[AuthenticatedMutation],
    ) -> Result<usize, HostingError> {
        Err(HostingError::EngineFailure("mutation-range apply is unavailable".into()))
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
    repair_once_sources_bounded_with_budgets(
        local,
        peer,
        scope,
        max_ranges,
        MAX_REPAIR_MUTATIONS,
        MAX_REPAIR_MUTATION_BYTES,
    )
}

fn repair_row_budget(
    max_ranges: usize,
    max_mutations: usize,
    max_bytes: usize,
) -> Result<usize, HostingError> {
    if max_ranges == 0 {
        return Err(HostingError::Invalid("the repair range budget must be nonzero".into()));
    }
    if max_mutations == 0 || max_bytes == 0 {
        return Err(HostingError::Invalid("the mutation repair budgets must be nonzero".into()));
    }
    Ok(max_ranges.saturating_mul(MAX_REPAIR_ROWS_PER_RANGE).max(1))
}

fn verified_mutation_index(
    scope: &BoundScope,
    mutations: Vec<AuthenticatedMutation>,
) -> Result<std::collections::BTreeMap<JournalRowId, AuthenticatedMutation>, HostingError> {
    let mut by_id = std::collections::BTreeMap::new();
    for mutation in mutations {
        mutation.verify(scope)?;
        if by_id.insert(mutation.row.id.clone(), mutation).is_some() {
            return Err(HostingError::Invalid(
                "the anti-entropy mutation range contains a duplicate identity".into(),
            ));
        }
    }
    Ok(by_id)
}

/// Runs a bounded reconciliation and, when possible, transfers the exact
/// missing mutations instead of immediately requiring a snapshot.
///
/// A source or target that does not implement the mutation seams returns a
/// snapshot decision.  This is intentionally a safe fallback for old
/// transports: a digest-only exchange must never be treated as repair.
///
/// # Errors
///
/// Returns a hosting error for invalid budgets, unauthenticated mutations,
/// unavailable journal summaries, or a failed post-apply equality proof.
pub fn repair_once_sources_bounded_with_budgets<L, R>(
    local: &L,
    peer: &R,
    scope: &BoundScope,
    max_ranges: usize,
    max_mutations: usize,
    max_bytes: usize,
) -> Result<RepairDecision, HostingError>
where
    L: JournalRowSource,
    R: JournalRowSource,
{
    let row_budget = repair_row_budget(max_ranges, max_mutations, max_bytes)?;
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
        if plan.differences().len() > max_mutations {
            return Ok(RepairDecision::NeedsSnapshot {
                ranges: plan.differences().len(),
                max_ranges,
            });
        }
        // This function mutates `local`; a row which only local has would
        // require a reciprocal target. Keep that direction snapshot-gated.
        if plan.differences().iter().any(|difference| {
            matches!(
                difference,
                terrabase_replication_core::anti_entropy::RowDifference::MissingRemote(_)
            )
        }) {
            return Ok(RepairDecision::NeedsSnapshot {
                ranges: plan.differences().len(),
                max_ranges,
            });
        }
        let Ok(mutations) = peer.durable_mutations(scope) else {
            return Ok(RepairDecision::NeedsSnapshot {
                ranges: plan.differences().len(),
                max_ranges,
            });
        };
        let mut by_id = verified_mutation_index(scope, mutations)?;
        let mut transfer = Vec::with_capacity(plan.differences().len());
        let mut bytes = 0usize;
        for difference in plan.differences() {
            let id = difference.id();
            let Some(mutation) = by_id.remove(id) else {
                return Ok(RepairDecision::NeedsSnapshot {
                    ranges: plan.differences().len(),
                    max_ranges,
                });
            };
            let expected = match difference {
                terrabase_replication_core::anti_entropy::RowDifference::MissingLocal(row) => {
                    row.row_hash
                }
                terrabase_replication_core::anti_entropy::RowDifference::HashMismatch {
                    remote,
                    ..
                } => *remote,
                terrabase_replication_core::anti_entropy::RowDifference::MissingRemote(_) => {
                    unreachable!()
                }
            };
            if mutation.row.row_hash != expected {
                return Err(HostingError::Invalid(
                    "the anti-entropy mutation hash does not match its reconciliation row".into(),
                ));
            }
            bytes = bytes.saturating_add(mutation.encoded_len());
            if bytes > max_bytes {
                return Ok(RepairDecision::NeedsSnapshot {
                    ranges: plan.differences().len(),
                    max_ranges,
                });
            }
            transfer.push(mutation);
        }
        let Ok(repaired) = local.apply_mutations(scope, &transfer) else {
            return Ok(RepairDecision::NeedsSnapshot {
                ranges: plan.differences().len(),
                max_ranges,
            });
        };
        let local_after = local.durable_journal_summary(scope, row_budget)?;
        if !local_after.complete || local_after.table != peer_summary.table {
            return Err(HostingError::EngineFailure(
                "the anti-entropy mutation transfer failed its post-apply proof".into(),
            ));
        }
        Ok(RepairDecision::Converged { repaired })
    }
}

/// The post-copy proof required before a snapshot cursor may be trusted.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SnapshotProof {
    /// Source's complete journal fingerprint.
    pub source: RangeFingerprint,
    /// Target's complete journal fingerprint after the copy.
    pub target: RangeFingerprint,
}

/// Reconciles both complete journal sets after a snapshot copy.
///
/// Callers must invoke this before advancing a replication cursor past the
/// copied file set, so pre-snapshot rows never re-ship. An incomplete or
/// unavailable summary is an error, never permission to trust the copied
/// bytes.
///
/// # Errors
///
/// Returns a hosting error for unavailable or incomplete journal summaries,
/// invalid budgets, or unequal source and target fingerprints.
pub fn prove_snapshot_reconciliation<L, R>(
    source: &L,
    target: &R,
    scope: &BoundScope,
    max_rows: usize,
) -> Result<SnapshotProof, HostingError>
where
    L: JournalRowSource,
    R: JournalRowSource,
{
    if max_rows == 0 {
        return Err(HostingError::Invalid("the snapshot proof row budget must be nonzero".into()));
    }
    let source_summary = source.durable_journal_summary(scope, max_rows)?;
    let target_summary = target.durable_journal_summary(scope, max_rows)?;
    if !source_summary.complete || !target_summary.complete {
        return Err(HostingError::EngineFailure(
            "the snapshot post-copy proof exceeded its row budget".into(),
        ));
    }
    let proof = SnapshotProof {
        source: source_summary.table.proof(),
        target: target_summary.table.proof(),
    };
    if proof.source != proof.target || source_summary.table != target_summary.table {
        return Err(HostingError::EngineFailure(
            "the snapshot post-copy reconciliation proof does not match".into(),
        ));
    }
    Ok(proof)
}
