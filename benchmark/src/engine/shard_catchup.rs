//! Engine backed journal catch-up for online shard splits.
//!
//! The shard split contracts intentionally know nothing about the storage
//! format.  This adapter binds them to the retained original publication
//! groups so a transaction's schema controls and data history stay together.

use std::sync::Arc;

use crate::{
    engine::{Engine, ReplicatedPublication},
    shard_split::{DurableSplitState, canonical_shard_key},
    types::{StorageSequence, TenantScope},
};

/// Maximum number of retained source transactions read by one catch-up step.
pub(crate) const MAX_SHARD_CATCH_UP_BATCH: u32 = 1024;

/// Failure while reading or applying a shard catch-up page.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShardCatchUpError {
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
        state: &DurableSplitState,
        applied_through: u64,
    ) -> Result<Self, ShardCatchUpError> {
        let upper = state.split_key().to_vec();
        if upper.is_empty()
            || state.lower().is_some_and(|lower| lower.is_empty() || lower >= state.split_key())
        {
            return Err(ShardCatchUpError::InvalidRange);
        }
        Ok(Self {
            source,
            target,
            source_scope,
            target_scope,
            lower: state.lower().map(ToOwned::to_owned),
            upper,
            applied_through,
        })
    }

    /// Returns the last source publication durably accepted by the target.
    pub(crate) const fn applied_through(&self) -> u64 {
        self.applied_through
    }

    /// Returns the durable source publication boundary to be covered by the
    /// retained original transaction groups, including metadata-only groups.
    pub(crate) fn source_high_water(&self) -> Result<u64, ShardCatchUpError> {
        self.source
            .published_sequence()
            .map_err(|_| ShardCatchUpError::SourceFailed)
            .map(StorageSequence::get)
    }

    /// Applies one bounded page and returns the number of source groups read.
    pub(crate) fn step(&mut self, limit: u32) -> Result<usize, ShardCatchUpError> {
        if limit == 0 || limit > MAX_SHARD_CATCH_UP_BATCH {
            return Err(ShardCatchUpError::BatchTooLarge);
        }
        let source_high_water = self.source_high_water()?;
        let groups = self
            .source
            .replication_publications(&self.source_scope, self.applied_through, limit)
            .map_err(|_| ShardCatchUpError::SourceFailed)?;
        // Document-history high water excludes metadata-only publications.
        // Check the actual outbox tail instead: an empty page before the
        // durable boundary cannot prove the source was fully replayed.
        if groups.is_empty() && self.applied_through < source_high_water {
            return Err(ShardCatchUpError::SourceHistoryGap);
        }
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
            let selected = range_publication(publication, self.lower.as_deref(), &self.upper)?;
            self.target
                .apply_replicated_publication(&self.target_scope, &selected)
                .map_err(|_| ShardCatchUpError::TargetFailed)?;
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
) -> Result<ReplicatedPublication, ShardCatchUpError> {
    let mut selected = publication.clone();
    selected.records.retain(|record| {
        let key = canonical_shard_key(&record.collection, &record.id);
        lower.is_none_or(|bound| key.as_slice() >= bound) && key.as_slice() < upper
    });
    if !selected.private_payload.is_empty() {
        let (mut metadata, schemas) =
            crate::storage::decode_private_changes(&selected.private_payload)
                .ok_or(ShardCatchUpError::SourceFailed)?;
        let before = metadata.len();
        // These controls describe the source's physical routing and split
        // worker, not customer state. Installing its route catalog on the
        // target would reject later target transactions against source routes;
        // copying target identity/proof would overwrite the new split owner.
        metadata.retain(|entry| {
            ![
                "control:shard-route/v1/",
                "control:shard-split/v1/",
                "control:shard-target/v1/",
                "control:shard-file/v1/",
            ]
            .iter()
            .any(|prefix| entry.key.starts_with(prefix))
        });
        if metadata.len() != before {
            // Keep the versioned empty envelope so the publication still
            // advances the source frontier after all controls are filtered.
            selected.private_payload = crate::storage::encode_private_changes(&metadata, &schemas);
        }
    }
    Ok(selected)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        engine::{CommitOutcome, EngineConfig},
        types::{
            CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
            OperationId, TenantId,
        },
    };

    #[test]
    fn retained_engine_publication_is_filtered_by_canonical_range() {
        let path = std::env::temp_dir().join(format!(
            "terrabase-shard-catchup-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("clock")
                .as_nanos()
        ));
        let engine = Engine::open(EngineConfig::durable(&path).expect("config")).expect("open");
        let tenant = TenantId::try_new("catchup-test").expect("tenant");
        let scope = engine.scope_for(&tenant);
        let collection = CollectionName::try_new("docs").expect("collection");
        let mut transaction = engine
            .begin(
                &scope,
                engine.read_snapshot(&scope).expect("snapshot"),
                OperationId::try_new("catchup-write").expect("operation"),
            )
            .expect("begin");
        for id in ["a", "z"] {
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(id).expect("id"),
                        collection.clone(),
                        DocumentValue::String(id.to_owned()),
                    ),
                    ExpectedVersion::Any,
                )
                .expect("put");
        }
        for key in [
            "control:shard-route/v1/source",
            "control:shard-split/v1/source",
            "control:shard-target/v1/identity",
            "control:shard-file/v1/source",
            "retained-control",
        ] {
            transaction.put_private_metadata(key, DocumentValue::String(key.to_owned())).unwrap();
        }
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        let publication = engine
            .replication_publications(&scope, 0, 1)
            .expect("journal")
            .pop()
            .expect("publication")
            .1;
        let upper = canonical_shard_key(&collection, &DocumentId::try_new("z").expect("id"));
        let selected = range_publication(&publication, None, &upper).unwrap();
        assert_eq!(selected.records.len(), 1);
        assert_eq!(selected.records[0].id.as_str(), "a");
        let (metadata, _) =
            crate::storage::decode_private_changes(&selected.private_payload).unwrap();
        assert_eq!(metadata.len(), 1);
        assert_eq!(metadata[0].key, "retained-control");
        assert_eq!(metadata[0].value, Some(DocumentValue::String("retained-control".into())));
        drop(engine);
        let _ = std::fs::remove_dir_all(path);
    }
}
