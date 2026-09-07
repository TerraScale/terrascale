//! Engine backed journal catch-up for online shard splits.
//!
//! The shard split contracts intentionally know nothing about the storage
//! format.  This adapter binds them to the retained original publication
//! groups so a transaction's schema controls and data history stay together.

use std::sync::Arc;

use crate::{
    engine::{Engine, ReplicatedPublication},
    shard_split::{SplitContext, canonical_shard_key},
    types::{StorageSequence, TenantScope},
};

/// Maximum number of retained source transactions read by one catch-up step.
pub(crate) const MAX_SHARD_CATCH_UP_BATCH: u32 = 1024;

/// Failure while reading or applying a shard catch-up page.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ShardCatchUpError {
    /// The source publication history could not be read.
    SourceFailed,
    /// The target rejected a publication.
    TargetFailed,
    /// The source cursor or returned page regressed.
    SequenceRegressed,
    /// The requested lower/upper canonical key range is invalid.
    InvalidRange,
    /// The source journal has a durable gap and cannot prove completeness.
    SourceHistoryGap,
    /// The requested page size exceeded the adapter bound.
    BatchTooLarge,
}

/// A resumable, bounded adapter over the Engine publication journal.
///
/// The cursor is the source location's global publication sequence.  A page
/// is applied transaction by transaction, and the cursor advances only after
/// every selected transaction has been accepted by the target.  Target
/// retries are safe because [`Engine::apply_replicated_publication`] binds the
/// complete source operation to its durable operation identity.
pub(crate) struct ShardJournalCatchUp {
    source: Arc<Engine>,
    target: Arc<Engine>,
    source_scope: TenantScope,
    target_scope: TenantScope,
    lower: Option<Vec<u8>>,
    upper: Vec<u8>,
    applied_through: u64,
}

impl ShardJournalCatchUp {
    /// Creates a catch-up adapter for the lower-inclusive, upper-exclusive
    /// canonical shard-key range.
    pub(crate) fn new(
        source: Arc<Engine>,
        target: Arc<Engine>,
        source_scope: TenantScope,
        target_scope: TenantScope,
        context: &SplitContext,
        applied_through: u64,
    ) -> Result<Self, ShardCatchUpError> {
        let upper = context.split_key().to_vec();
        if upper.is_empty()
            || context.lower().is_some_and(|lower| lower.is_empty() || lower >= context.split_key())
        {
            return Err(ShardCatchUpError::InvalidRange);
        }
        Ok(Self {
            source,
            target,
            source_scope,
            target_scope,
            lower: context.lower().map(ToOwned::to_owned),
            upper,
            applied_through,
        })
    }

    /// Returns the last source publication durably accepted by the target.
    pub(crate) const fn applied_through(&self) -> u64 {
        self.applied_through
    }

    /// Returns the exact latest source journal sequence currently retained.
    /// `None` is the empty journal baseline.
    pub(crate) fn source_high_water(&self) -> Result<u64, ShardCatchUpError> {
        self.source
            .journaled_high_water()
            .map_err(|_| ShardCatchUpError::SourceFailed)
            .map(|sequence| sequence.map_or(0, |sequence| sequence.get()))
    }

    /// Applies one bounded page and returns the number of source groups read.
    pub(crate) fn step(&mut self, limit: u32) -> Result<usize, ShardCatchUpError> {
        if limit == 0 || limit > MAX_SHARD_CATCH_UP_BATCH {
            return Err(ShardCatchUpError::BatchTooLarge);
        }
        let journal_high_water = self.source_high_water()?;
        let published = self.source.published_state().0.get();
        if journal_high_water < published {
            return Err(ShardCatchUpError::SourceHistoryGap);
        }
        let groups = self
            .source
            .replication_publications(&self.source_scope, self.applied_through, limit)
            .map_err(|_| ShardCatchUpError::SourceFailed)?;
        let mut previous = self.applied_through;
        for (cursor, publication) in &groups {
            if *cursor <= previous
                || publication
                    .records
                    .iter()
                    .any(|record| record.origin.is_empty() || record.commit_ulid.is_empty())
            {
                return Err(ShardCatchUpError::SequenceRegressed);
            }
            let selected = range_publication(publication, self.lower.as_deref(), &self.upper);
            if !selected.records.is_empty() || !selected.private_payload.is_empty() {
                self.target
                    .apply_replicated_publication(&self.target_scope, &selected)
                    .map_err(|_| ShardCatchUpError::TargetFailed)?;
            }
            previous = *cursor;
        }
        self.applied_through = previous;
        Ok(groups.len())
    }
}

fn range_publication(
    publication: &ReplicatedPublication,
    lower: Option<&[u8]>,
    upper: &[u8],
) -> ReplicatedPublication {
    let mut selected = publication.clone();
    selected.records.retain(|record| {
        let key = canonical_shard_key(&record.collection, &record.id);
        lower.is_none_or(|bound| key.as_slice() >= bound) && key.as_slice() < upper
    });
    selected
}
