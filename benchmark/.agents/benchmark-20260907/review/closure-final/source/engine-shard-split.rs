//! Engine-owned durable shard route catalog and cutover CAS.

use super::{CommitError, CommitOutcome, Engine, EngineError, EngineOpenError};
use crate::{
    shard_split::{
        AtomicSplitCutover, CommittedSplitRoute, CopyBatch, CopyEntry, CutoverContext,
        DurableSplitState, RangeCopySource, RangeCopyTarget, RouteTable, ShardAdmissionToken,
        ShardRoute, ShardSplit, SplitContext, SplitEngineAdapter, SplitPhase, SplitStateStore,
        canonical_shard_key, decode_canonical_shard_key,
    },
    types::{
        DocumentValue, ExpectedVersion, LocalDurability, OperationId, StorageSequence, TenantScope,
    },
};

const VERSION: u8 = 1;
const MAX_ROUTES: usize = 1024;
const MAX_BYTES: usize = 1024 * 1024;

#[cfg(test)]
#[path = "shard_admission_tests.rs"]
mod admission_tests;

impl super::Transaction<'_> {
    /// Admits a named document write against this file's durable route.
    /// Databases without a shard catalog keep ordinary write admission.
    ///
    /// # Errors
    /// Refuses a moved key, stale route, malformed file identity, or duplicate binding.
    pub fn bind_document_shard_admission(
        &mut self,
        collection: &crate::types::CollectionName,
        id: &crate::types::DocumentId,
    ) -> Result<(), EngineError> {
        let Some((router, _)) = self.engine.load_shard_router(&self.scope)? else {
            return Ok(());
        };
        let token = self.engine.shard_admission_for_key(
            &self.scope,
            &router,
            &canonical_shard_key(collection, id),
        )?;
        self.bind_shard_admission(token)
    }
}

/// Engine cutover adapter bound to one admitted tenant and database
/// incarnation. The scope prevents a split from crossing database owners.
pub(crate) struct ScopedShardEngine {
    engine: std::sync::Arc<Engine>,
    scope: TenantScope,
    incarnation: u128,
}

impl ScopedShardEngine {
    pub(crate) fn new(
        engine: std::sync::Arc<Engine>,
        scope: TenantScope,
    ) -> Result<Self, RouteCatalogError> {
        let incarnation = u128::from_be_bytes(engine.database_id().as_bytes().to_owned());
        Ok(Self { engine, scope, incarnation })
    }
    fn operation(split: SplitContext) -> Result<OperationId, RouteCatalogError> {
        OperationId::try_new(&format!("shard-split-{:032x}", split.split_id()))
            .map_err(|_| RouteCatalogError::Malformed)
    }

    fn target_path(&self, context: &SplitContext) -> std::path::PathBuf {
        self.engine.location.join("shards").join(format!("{:016x}.sqlite3", context.target_shard()))
    }

    /// Opens (or reopens) the target file with the parent database's owner and
    /// encryption lease. Data movement owns the returned handle in its worker.
    pub(crate) fn open_target(&self, context: &SplitContext) -> Result<Engine, EngineOpenError> {
        self.open_target_with_mode(context, true)
    }

    fn open_target_with_mode(
        &self,
        context: &SplitContext,
        create: bool,
    ) -> Result<Engine, EngineOpenError> {
        let path = self.target_path(context);
        std::fs::create_dir_all(path.parent().expect("target has shard directory"))
            .map_err(|_| EngineOpenError::StorageUnavailable)?;
        let mut config = if create {
            super::EngineConfig::durable(path)
        } else {
            super::EngineConfig::existing_durable(path)
        }
        .map_err(|_| EngineOpenError::InvalidConfig)?
        .with_owner(self.engine.database_owner().clone());
        if let Some(keys) = &self.engine.storage_keys {
            config = config.with_storage_keys(std::sync::Arc::clone(keys));
        }
        let origin =
            crate::types::RegionId::try_new(self.engine.database_owner().region().as_str())
                .map_err(|_| EngineOpenError::InvalidConfig)?;
        config = config.with_replication_origin(origin);
        Engine::open(config)
    }

    /// Reopens a committed target without treating missing storage as a new
    /// database. Its durable split identity is validated separately.
    pub(crate) fn open_existing_target(
        &self,
        context: &SplitContext,
    ) -> Result<Engine, EngineOpenError> {
        if !crate::storage::database_file(&self.target_path(context)).is_file() {
            return Err(EngineOpenError::StorageUnavailable);
        }
        self.open_target_with_mode(context, false)
    }

    /// Records the exact verified target snapshot before route publication.
    pub(crate) fn prepare_split_verification(
        &self,
        context: &SplitContext,
        through: u64,
    ) -> Result<(), EngineError> {
        let state =
            self.load().map_err(|_| EngineError::StorageFailure)?.ok_or(EngineError::Conflict)?;
        let identity = Self::target_identity(*context, &state)?;
        let target = self.open_existing_target(context).map_err(|_| EngineError::StorageFailure)?;
        let scope = Self::target_scope(&target, &self.scope);
        if Self::target_identity_at(&target, &scope)?.as_deref() != Some(identity.as_slice()) {
            return Err(EngineError::Conflict);
        }
        let snapshot = target.read_snapshot(&scope)?;
        let existing = target
            .lock_storage()?
            .port
            .private_metadata_at(scope.tenant(), TARGET_VERIFICATION_KEY, snapshot.sequence())
            .map_err(|()| EngineError::StorageFailure)?;
        let pinned = match existing {
            None => None,
            Some(DocumentValue::String(receipt)) => {
                let receipt = hex::decode(receipt).map_err(|_| EngineError::Conflict)?;
                let receipt: [u8; 56] = receipt.try_into().map_err(|_| EngineError::Conflict)?;
                let prior =
                    u64::from_be_bytes(receipt[..8].try_into().map_err(|_| EngineError::Conflict)?);
                let boundary = u64::from_be_bytes(
                    receipt[8..16].try_into().map_err(|_| EngineError::Conflict)?,
                );
                (prior == through).then_some(StorageSequence::new(boundary))
            }
            Some(_) => return Err(EngineError::Conflict),
        };
        let boundary = pinned.unwrap_or(snapshot.sequence());
        let facts =
            range_fingerprint(&self.engine, &self.scope, StorageSequence::new(through), &state)?;
        if facts != range_fingerprint(&target, &scope, boundary, &state)? {
            return Err(EngineError::Conflict);
        }
        let (_, source_generation) =
            self.engine.load_shard_router(&self.scope)?.ok_or(EngineError::Conflict)?;
        let target_generation = source_generation.checked_add(1).ok_or(EngineError::Conflict)?;
        // A proof transaction advances the target sequence itself. Reuse the
        // pinned boundary on retry instead of rebinding its operation identity
        // to a new current sequence after a lost response or failed route CAS.
        if pinned.is_some() {
            target.install_split_target_catalog(&scope, &state, target_generation)?;
            return Ok(());
        }
        let mut receipt = through.to_be_bytes().to_vec();
        receipt.extend_from_slice(&boundary.get().to_be_bytes());
        receipt.extend_from_slice(&facts.0.to_be_bytes());
        receipt.extend_from_slice(&facts.1);
        let operation =
            OperationId::try_new(&format!("shard-proof-{:032x}-{through}", context.split_id()))
                .map_err(|_| EngineError::InvalidDocument)?;
        let mut tx = target.begin(&scope, target.read_snapshot(&scope)?, operation)?;
        tx.internal_maintenance = true;
        tx.put_private_metadata(TARGET_VERIFICATION_KEY, value(&receipt))?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {
                target.install_split_target_catalog(&scope, &state, target_generation)
            }
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }

    fn target_scope(target: &Engine, source_scope: &TenantScope) -> TenantScope {
        target.scope_for(source_scope.tenant())
    }

    fn target_identity(
        context: SplitContext,
        state: &DurableSplitState,
    ) -> Result<Vec<u8>, EngineError> {
        if state.context() != Some(context) || state.split_key().is_empty() {
            return Err(EngineError::Conflict);
        }
        let lower = state.lower().unwrap_or_default();
        let lower_len = u32::try_from(lower.len()).map_err(|_| EngineError::InvalidDocument)?;
        let upper_len =
            u32::try_from(state.split_key().len()).map_err(|_| EngineError::InvalidDocument)?;
        let mut bytes =
            Vec::with_capacity(1 + 16 + 16 + 8 + 8 + 4 + lower.len() + 4 + state.split_key().len());
        bytes.extend_from_slice(b"TSPLIT1");
        bytes.extend_from_slice(&context.split_id().to_be_bytes());
        bytes.extend_from_slice(&context.database_incarnation().to_be_bytes());
        bytes.extend_from_slice(&context.source_shard().to_be_bytes());
        bytes.extend_from_slice(&context.target_shard().to_be_bytes());
        bytes.extend_from_slice(&context.placement_epoch().to_be_bytes());
        bytes.extend_from_slice(&context.session_frontier().to_be_bytes());
        bytes.extend_from_slice(&lower_len.to_be_bytes());
        bytes.extend_from_slice(lower);
        bytes.extend_from_slice(&upper_len.to_be_bytes());
        bytes.extend_from_slice(state.split_key());
        Ok(bytes)
    }

    fn target_identity_at(
        target: &Engine,
        scope: &TenantScope,
    ) -> Result<Option<Vec<u8>>, EngineError> {
        let snapshot = target.read_snapshot(scope)?;
        let value = target
            .lock_storage()?
            .port
            .private_metadata_at(scope.tenant(), &target_identity_key(), snapshot.sequence())
            .map_err(|()| EngineError::StorageFailure)?;
        match value {
            None => Ok(None),
            Some(DocumentValue::String(value)) => {
                hex::decode(value).map(Some).map_err(|_| EngineError::Conflict)
            }
            Some(_) => Err(EngineError::Conflict),
        }
    }

    fn persist_target_identity(
        target: &Engine,
        scope: &TenantScope,
        context: SplitContext,
        identity: &[u8],
    ) -> Result<(), EngineError> {
        let operation = OperationId::try_new(&format!("shard-target-{:032x}", context.split_id()))
            .map_err(|_| EngineError::InvalidDocument)?;
        let mut tx = target.begin(scope, target.read_snapshot(scope)?, operation)?;
        tx.internal_maintenance = true;
        tx.put_private_metadata(&target_identity_key(), value(identity))?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(
                CommitError::Conflict | CommitError::OperationIdReuse { .. },
            ) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }

    fn copy_schema_metadata(
        &self,
        target: &Engine,
        target_scope: &TenantScope,
        context: SplitContext,
    ) -> Result<(), EngineError> {
        let database = self.engine.database_owner().database().to_text();
        let records = self.engine.schema_metadata_list(&database, "main", None, 64)?;
        if records.is_empty() {
            return Ok(());
        }
        let operation = OperationId::try_new(&format!("shard-schema-{:032x}", context.split_id()))
            .map_err(|_| EngineError::InvalidDocument)?;
        let mut tx = target.begin(target_scope, target.read_snapshot(target_scope)?, operation)?;
        tx.internal_maintenance = true;
        for (collection, envelope) in records {
            tx.stage_schema_metadata(&database, "main", &collection, None, &envelope)?;
        }
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_)
            | CommitOutcome::Rejected(CommitError::OperationIdReuse { .. }) => Ok(()),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }
}

const TARGET_VERIFICATION_KEY: &str = "control:shard-target/v1/verified-boundary";

const fn target_identity_key() -> &'static str {
    "control:shard-target/v1/identity"
}

impl SplitEngineAdapter for ScopedShardEngine {
    type DataError = EngineError;

    fn create_split_target(&mut self, context: &SplitContext) -> Result<(), Self::DataError> {
        if context.database_incarnation() != self.incarnation {
            return Err(EngineError::Conflict);
        }
        let state =
            self.load().map_err(|_| EngineError::StorageFailure)?.ok_or(EngineError::Conflict)?;
        let identity = Self::target_identity(*context, &state)?;
        let target = self.open_target(context).map_err(|error| match error {
            EngineOpenError::InvalidConfig | EngineOpenError::FormatMismatch => {
                EngineError::Conflict
            }
            EngineOpenError::StorageUnavailable => EngineError::StorageFailure,
        })?;
        let target_scope = Self::target_scope(&target, &self.scope);
        match Self::target_identity_at(&target, &target_scope)? {
            Some(existing) if existing == identity => Ok(()),
            Some(_) => Err(EngineError::Conflict),
            None => {
                // The staging target inherits only an admitted, still-open
                // source writer epoch. Owned Engine creation starts fenced;
                // writing its identity before this admission always conflicts.
                let (epoch, fenced) = self.engine.origin_epoch()?;
                if epoch == 0 || fenced {
                    return Err(EngineError::Conflict);
                }
                target.activate_origin_epoch(epoch)?;
                self.copy_schema_metadata(&target, &target_scope, *context)?;
                Self::persist_target_identity(&target, &target_scope, *context, &identity)
            }
        }
    }

    fn rollback_split_target(&mut self, context: &SplitContext) -> Result<(), Self::DataError> {
        if context.database_incarnation() != self.incarnation {
            return Err(EngineError::Conflict);
        }
        // A route lookup error is deliberately propagated: deleting a target
        // while its commit state is unknown could destroy the serving shard.
        if self.recover_split_route(context).map_err(|_| EngineError::StorageFailure)?.is_some() {
            return Err(EngineError::Conflict);
        }
        let path = self.target_path(context);
        if path.exists() {
            std::fs::remove_dir_all(path).map_err(|_| EngineError::StorageFailure)?;
        }
        Ok(())
    }

    fn verify_split_target(&mut self, context: &SplitContext) -> Result<(), Self::DataError> {
        if context.database_incarnation() != self.incarnation {
            return Err(EngineError::Conflict);
        }
        let state =
            self.load().map_err(|_| EngineError::StorageFailure)?.ok_or(EngineError::Conflict)?;
        if !matches!(state.phase(), SplitPhase::CutOver | SplitPhase::Complete) {
            return Err(EngineError::Conflict);
        }
        let identity = Self::target_identity(*context, &state)?;
        let target = self.open_existing_target(context).map_err(|_| EngineError::StorageFailure)?;
        let scope = Self::target_scope(&target, &self.scope);
        if Self::target_identity_at(&target, &scope)?.as_deref() != Some(identity.as_slice()) {
            return Err(EngineError::Conflict);
        }
        // Compare the actual stored documents, including their versions. A
        // target identity proves ownership, never that the data was copied.
        // Any unavailable historical boundary or mismatch retains the source.
        let source = range_fingerprint(
            &self.engine,
            &self.scope,
            StorageSequence::new(state.source_high_water()),
            &state,
        )?;
        let snapshot = target.read_snapshot(&scope)?;
        let receipt = target
            .lock_storage()?
            .port
            .private_metadata_at(scope.tenant(), TARGET_VERIFICATION_KEY, snapshot.sequence())
            .map_err(|()| EngineError::StorageFailure)?;
        let Some(DocumentValue::String(receipt)) = receipt else {
            return Err(EngineError::Conflict);
        };
        let receipt = hex::decode(receipt).map_err(|_| EngineError::Conflict)?;
        let receipt: [u8; 56] = receipt.try_into().map_err(|_| EngineError::Conflict)?;
        let through =
            u64::from_be_bytes(receipt[..8].try_into().map_err(|_| EngineError::Conflict)?);
        let records =
            u64::from_be_bytes(receipt[16..24].try_into().map_err(|_| EngineError::Conflict)?);
        let fingerprint: [u8; 32] = receipt[24..].try_into().map_err(|_| EngineError::Conflict)?;
        if through != state.source_high_water() {
            return Err(EngineError::Conflict);
        }
        // These are measured target facts, durably recorded before the route
        // was exposed. Post-cutover writes may legitimately change the live
        // target, and an empty pre-write snapshot may no longer be servable.
        let copied = (records, fingerprint);
        if source != copied {
            return Err(EngineError::Conflict);
        }
        Ok(())
    }

    fn cleanup_split_source(&mut self, _context: &SplitContext) -> Result<(), Self::DataError> {
        // Source deletion requires the range-copy worker's durable cursor and
        // is intentionally left to that worker after verification.
        Ok(())
    }
}

/// Streams bounded pages into an unambiguous digest of the complete moved
/// range. Counts and length prefixes prevent concatenation ambiguities.
fn range_fingerprint(
    engine: &Engine,
    scope: &TenantScope,
    boundary: StorageSequence,
    state: &DurableSplitState,
) -> Result<(u64, [u8; 32]), EngineError> {
    let mut after = None;
    let mut count = 0_u64;
    let mut digest = blake3::Hasher::new();
    digest.update(b"shard-range-verification-v1\0");
    loop {
        let page = engine.snapshot_records_from(scope.tenant().clone(), boundary, after, 256)?;
        let done = page.len() < 256;
        after = page
            .last()
            .map(|record| (record.document().collection().clone(), record.document().id().clone()));
        for record in page {
            let document = record.document();
            let key = canonical_shard_key(document.collection(), document.id());
            if key.as_slice() >= state.split_key() {
                return Ok((count, *digest.finalize().as_bytes()));
            }
            if state.lower().is_some_and(|lower| key.as_slice() < lower) {
                continue;
            }
            let value = crate::storage::encode_document_body(document);
            digest.update(&(key.len() as u64).to_be_bytes());
            digest.update(&key);
            digest.update(&(value.len() as u64).to_be_bytes());
            digest.update(&value);
            count = count.checked_add(1).ok_or(EngineError::StorageFailure)?;
        }
        if done {
            return Ok((count, *digest.finalize().as_bytes()));
        }
    }
}

/// Snapshot reader for one immutable source boundary. The cursor is the
/// canonical `(collection, id)` key, so duplicate IDs across collections are
/// retained and page boundaries never skip rows.
pub(crate) struct ScopedShardRangeSource {
    engine: std::sync::Arc<Engine>,
    tenant: crate::types::TenantId,
    boundary: StorageSequence,
}

impl ScopedShardRangeSource {
    pub(crate) fn new(
        engine: std::sync::Arc<Engine>,
        scope: &TenantScope,
    ) -> Result<Self, EngineError> {
        Ok(Self {
            boundary: engine.read_snapshot(scope)?.sequence(),
            engine,
            tenant: scope.tenant().clone(),
        })
    }
}

impl RangeCopySource for ScopedShardRangeSource {
    type Error = EngineError;

    fn read_range(
        &mut self,
        lower: Option<&[u8]>,
        after_key: Option<&[u8]>,
        upper: &[u8],
        limit: usize,
    ) -> Result<CopyBatch, Self::Error> {
        if upper.is_empty() || lower.is_some_and(|bound| bound.is_empty() || bound >= upper) {
            return Err(EngineError::InvalidDocument);
        }
        let after = after_key
            .map(decode_canonical_shard_key)
            .transpose()
            .map_err(|_| EngineError::InvalidDocument)?;
        let records = self.engine.snapshot_records_from(
            self.tenant.clone(),
            self.boundary,
            after,
            u32::try_from(limit.max(1)).map_err(|_| EngineError::InvalidDocument)?,
        )?;
        let raw_len = records.len();
        let mut entries = Vec::with_capacity(raw_len);
        let mut reached_upper = false;
        for record in records {
            let key = canonical_shard_key(record.document().collection(), record.document().id());
            if key.as_slice() >= upper {
                reached_upper = true;
                break;
            }
            if lower.is_some_and(|bound| key.as_slice() < bound) {
                continue;
            }
            let value = crate::storage::encode_document_body(record.document());
            entries.push(CopyEntry::new(key, value));
        }
        let done = reached_upper || raw_len < limit.max(1);
        Ok(CopyBatch::new(entries, self.boundary.get(), done))
    }
}

/// Target-side page applier. Each page is one durable transaction and uses a
/// stable operation identity derived from its canonical bytes, making a crash
/// retry safe while preserving the source document version in its envelope.
pub(crate) struct ScopedShardRangeTarget {
    engine: std::sync::Arc<Engine>,
    scope: TenantScope,
}

impl ScopedShardRangeTarget {
    pub(crate) fn open(engine: std::sync::Arc<Engine>, source_scope: &TenantScope) -> Self {
        let scope = engine.scope_for(source_scope.tenant());
        Self { engine, scope }
    }
}

impl RangeCopyTarget for ScopedShardRangeTarget {
    type Error = EngineError;

    fn apply_copy_batch(&mut self, batch: &CopyBatch) -> Result<(), Self::Error> {
        if batch.entries().is_empty() {
            return Ok(());
        }
        let mut digest = blake3::Hasher::new();
        digest.update(b"shard-copy-page-v1\0");
        for entry in batch.entries() {
            digest.update(entry.key());
            digest.update(entry.value());
        }
        let operation = OperationId::try_new(&format!(
            "shard-copy-{}",
            hex::encode(&digest.finalize().as_bytes()[..16])
        ))
        .map_err(|_| EngineError::InvalidDocument)?;
        let mut tx =
            self.engine.begin(&self.scope, self.engine.read_snapshot(&self.scope)?, operation)?;
        tx.internal_maintenance = true;
        for entry in batch.entries() {
            let (collection, id) = decode_canonical_shard_key(entry.key())
                .map_err(|_| EngineError::InvalidDocument)?;
            let document = crate::storage::decode_document_body(entry.value())
                .ok_or(EngineError::InvalidDocument)?;
            if document.collection() != &collection || document.id() != &id {
                return Err(EngineError::Conflict);
            }
            tx.put(document, ExpectedVersion::Any)?;
        }
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(CommitError::OperationIdReuse { .. }) => Ok(()),
            CommitOutcome::Rejected(CommitError::Conflict) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
            CommitOutcome::Rejected(_) => Err(EngineError::StorageFailure),
        }
    }
}

impl SplitStateStore for ScopedShardEngine {
    type Error = RouteCatalogError;

    fn load(&self) -> Result<Option<DurableSplitState>, Self::Error> {
        let snapshot = self.engine.read_snapshot(&self.scope).map_err(RouteCatalogError::from)?;
        let value = self
            .engine
            .lock_storage()
            .map_err(RouteCatalogError::from)?
            .port
            .private_metadata_at(
                self.scope.tenant(),
                &split_key(self.incarnation),
                snapshot.sequence(),
            )
            .map_err(|()| RouteCatalogError::Engine(EngineError::StorageFailure))?;
        bytes(value)?
            .map(|raw| DurableSplitState::decode(&raw).map_err(|_| RouteCatalogError::Malformed))
            .transpose()
    }

    fn save(&mut self, state: &DurableSplitState) -> Result<(), Self::Error> {
        let operation = OperationId::try_new(&format!(
            "shard-state-{:032x}-{}-{}",
            self.incarnation,
            state.phase() as u8,
            crate::identity::generate()
        ))
        .map_err(|_| RouteCatalogError::Malformed)?;
        let mut tx =
            self.engine.begin(&self.scope, self.engine.read_snapshot(&self.scope)?, operation)?;
        tx.internal_maintenance = true;
        tx.put_private_metadata(&split_key(self.incarnation), value(&state.encode()))?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(
                CommitError::Conflict | CommitError::OperationIdReuse { .. },
            ) => Err(RouteCatalogError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(RouteCatalogError::Engine(EngineError::StorageFailure))
            }
        }
    }

    fn remove(&mut self) -> Result<(), Self::Error> {
        let operation =
            OperationId::try_new(&format!("shard-state-remove-{:032x}", self.incarnation))
                .map_err(|_| RouteCatalogError::Malformed)?;
        let mut tx =
            self.engine.begin(&self.scope, self.engine.read_snapshot(&self.scope)?, operation)?;
        tx.internal_maintenance = true;
        tx.delete_private_metadata(&split_key(self.incarnation))?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(RouteCatalogError::Engine(EngineError::StorageFailure))
            }
        }
    }
}

impl AtomicSplitCutover for ScopedShardEngine {
    type Error = RouteCatalogError;

    fn install_split_route(
        &mut self,
        context: &CutoverContext,
        state: &DurableSplitState,
    ) -> Result<u64, Self::Error> {
        if context.split.database_incarnation() != self.incarnation {
            return Err(RouteCatalogError::Conflict);
        }
        self.engine.install_shard_split_route(
            &self.scope,
            context,
            state,
            Self::operation(context.split)?,
        )
    }

    fn recover_split_route(
        &mut self,
        split: &SplitContext,
    ) -> Result<Option<CommittedSplitRoute>, Self::Error> {
        if split.database_incarnation() != self.incarnation {
            return Err(RouteCatalogError::Conflict);
        }
        Ok(self
            .engine
            .recover_shard_split_route(&self.scope, split)?
            .map(|(receipt, _catalog)| receipt))
    }
}

/// A validated route catalog with its durable generation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct RouteCatalog {
    /// The validated key-range table.
    pub(crate) table: RouteTable,
    /// Monotonic route generation (zero is the initial catalog).
    pub(crate) generation: u64,
}

/// Failures reading or installing a route catalog.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum RouteCatalogError {
    /// Bytes were malformed or violated route invariants.
    Malformed,
    /// The caller's expected catalog is stale.
    Conflict,
    /// The underlying engine failed.
    Engine(EngineError),
}

impl From<EngineError> for RouteCatalogError {
    fn from(error: EngineError) -> Self {
        Self::Engine(error)
    }
}

fn key(incarnation: u128) -> String {
    format!("control:shard-route/v1/{incarnation:032x}")
}
fn split_key(incarnation: u128) -> String {
    format!("control:shard-split/v1/{incarnation:032x}")
}
fn file_shard_key(incarnation: u128) -> String {
    format!("control:shard-file/v1/{incarnation:032x}")
}

fn encode_file_identity(identity: crate::shard_split::FileShardIdentity) -> Vec<u8> {
    let mut bytes = vec![1];
    bytes.extend_from_slice(&identity.database_incarnation.to_be_bytes());
    bytes.extend_from_slice(&identity.shard_id.to_be_bytes());
    bytes.extend_from_slice(&identity.region.as_bytes());
    bytes.extend_from_slice(&identity.origin_lane_incarnation.to_be_bytes());
    bytes
}

fn decode_file_identity(
    bytes: &[u8],
) -> Result<crate::shard_split::FileShardIdentity, EngineError> {
    if bytes.len() != 44 || bytes[0] != 1 {
        return Err(EngineError::Conflict);
    }
    let identity = crate::shard_split::FileShardIdentity {
        database_incarnation: u128::from_be_bytes(
            bytes[1..17].try_into().map_err(|_| EngineError::Conflict)?,
        ),
        shard_id: u64::from_be_bytes(bytes[17..25].try_into().map_err(|_| EngineError::Conflict)?),
        region: crate::regions::RegionCode::from_wire_bytes(
            bytes[25..28].try_into().map_err(|_| EngineError::Conflict)?,
        )
        .map_err(|_| EngineError::Conflict)?,
        origin_lane_incarnation: u128::from_be_bytes(
            bytes[28..44].try_into().map_err(|_| EngineError::Conflict)?,
        ),
    };
    if identity.database_incarnation == 0
        || identity.shard_id == 0
        || identity.origin_lane_incarnation == 0
    {
        return Err(EngineError::Conflict);
    }
    Ok(identity)
}

fn push_bound(out: &mut Vec<u8>, bound: Option<&[u8]>) -> Result<(), RouteCatalogError> {
    let bytes = bound.unwrap_or_default();
    let len = u32::try_from(bytes.len()).map_err(|_| RouteCatalogError::Malformed)?;
    out.extend_from_slice(&len.to_be_bytes());
    out.extend_from_slice(bytes);
    Ok(())
}

/// Encodes a route catalog in a bounded, versioned binary form.
pub(crate) fn encode_catalog(catalog: &RouteCatalog) -> Result<Vec<u8>, RouteCatalogError> {
    let routes = catalog.table.routes();
    if routes.is_empty() || routes.len() > MAX_ROUTES {
        return Err(RouteCatalogError::Malformed);
    }
    let mut out = Vec::with_capacity(32);
    out.push(VERSION);
    out.extend_from_slice(&catalog.generation.to_be_bytes());
    out.extend_from_slice(
        &(u32::try_from(routes.len()).map_err(|_| RouteCatalogError::Malformed)?).to_be_bytes(),
    );
    for route in routes {
        if route.shard_id() == 0 {
            return Err(RouteCatalogError::Malformed);
        }
        out.extend_from_slice(&route.shard_id().to_be_bytes());
        push_bound(&mut out, route.lower())?;
        push_bound(&mut out, route.upper())?;
    }
    (out.len() <= MAX_BYTES).then_some(out).ok_or(RouteCatalogError::Malformed)
}

struct Reader<'a> {
    bytes: &'a [u8],
    at: usize,
}
impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], RouteCatalogError> {
        let end = self.at.checked_add(n).ok_or(RouteCatalogError::Malformed)?;
        let value = self.bytes.get(self.at..end).ok_or(RouteCatalogError::Malformed)?;
        self.at = end;
        Ok(value)
    }
    fn u32(&mut self) -> Result<u32, RouteCatalogError> {
        Ok(u32::from_be_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn u64(&mut self) -> Result<u64, RouteCatalogError> {
        Ok(u64::from_be_bytes(self.take(8)?.try_into().unwrap()))
    }
    fn bound(&mut self) -> Result<Option<Vec<u8>>, RouteCatalogError> {
        let n = usize::try_from(self.u32()?).map_err(|_| RouteCatalogError::Malformed)?;
        if n == 0 {
            return Ok(None);
        }
        Ok(Some(self.take(n)?.to_vec()))
    }
}

/// Decodes and validates a complete route catalog.
pub(crate) fn decode_catalog(bytes: &[u8]) -> Result<RouteCatalog, RouteCatalogError> {
    if bytes.len() > MAX_BYTES {
        return Err(RouteCatalogError::Malformed);
    }
    let mut r = Reader { bytes, at: 0 };
    if r.take(1)?[0] != VERSION {
        return Err(RouteCatalogError::Malformed);
    }
    let generation = r.u64()?;
    let count = usize::try_from(r.u32()?).map_err(|_| RouteCatalogError::Malformed)?;
    if count == 0 || count > MAX_ROUTES {
        return Err(RouteCatalogError::Malformed);
    }
    let mut routes = Vec::with_capacity(count);
    let mut ids = std::collections::BTreeSet::new();
    for _ in 0..count {
        let id = r.u64()?;
        if id == 0 || !ids.insert(id) {
            return Err(RouteCatalogError::Malformed);
        }
        let route = ShardRoute::new(id, r.bound()?, r.bound()?)
            .map_err(|_| RouteCatalogError::Malformed)?;
        routes.push(route);
    }
    if r.at != bytes.len() {
        return Err(RouteCatalogError::Malformed);
    }
    let table = RouteTable::new(routes)
        .map_err(|_| RouteCatalogError::Malformed)?
        .with_generation(generation);
    Ok(RouteCatalog { table, generation })
}

fn value(bytes: &[u8]) -> DocumentValue {
    DocumentValue::String(hex::encode(bytes))
}
fn bytes(value: Option<DocumentValue>) -> Result<Option<Vec<u8>>, RouteCatalogError> {
    let Some(value) = value else {
        return Ok(None);
    };
    let DocumentValue::String(value) = value else {
        return Err(RouteCatalogError::Malformed);
    };
    hex::decode(value).map(Some).map_err(|_| RouteCatalogError::Malformed)
}

impl Engine {
    /// Opens an existing split participant beneath this database's durable
    /// root. Member admission uses this only after the route catalog names
    /// the shard; callers cannot supply an arbitrary path.
    pub(crate) fn open_shard_participant(
        &self,
        shard_id: u64,
    ) -> Result<Option<Engine>, EngineOpenError> {
        let path = self.location.join("shards").join(format!("{shard_id:016x}.sqlite3"));
        if !crate::storage::database_file(&path).is_file() {
            return Ok(None);
        }
        let mut config = super::EngineConfig::existing_durable(path)
            .map_err(|_| EngineOpenError::InvalidConfig)?
            .with_owner(self.database_owner().clone());
        if let Some(keys) = &self.storage_keys {
            config = config.with_storage_keys(std::sync::Arc::clone(keys));
        }
        let origin = crate::types::RegionId::try_new(self.database_owner().region().as_str())
            .map_err(|_| EngineOpenError::InvalidConfig)?;
        config = config.with_replication_origin(origin);
        Engine::open(config).map(Some)
    }

    /// Validates a target's immutable split identity and verification receipt.
    pub(crate) fn validate_shard_target(
        &self,
        scope: &crate::types::TenantScope,
        database_incarnation: u128,
        shard_id: u64,
    ) -> Result<bool, EngineError> {
        let snapshot = self.read_snapshot(scope)?;
        let identity = self
            .lock_storage()?
            .port
            .private_metadata_at(scope.tenant(), &target_identity_key(), snapshot.sequence())
            .map_err(|()| EngineError::StorageFailure)?;
        let Some(DocumentValue::String(identity)) = identity else {
            return Ok(false);
        };
        let identity = hex::decode(identity).map_err(|_| EngineError::Conflict)?;
        if identity.len() < 55 || &identity[..7] != b"TSPLIT1" {
            return Ok(false);
        }
        let incarnation =
            u128::from_be_bytes(identity[23..39].try_into().map_err(|_| EngineError::Conflict)?);
        let target =
            u64::from_be_bytes(identity[47..55].try_into().map_err(|_| EngineError::Conflict)?);
        if incarnation != database_incarnation || target != shard_id {
            return Ok(false);
        }
        let verified = self
            .lock_storage()?
            .port
            .private_metadata_at(scope.tenant(), &TARGET_VERIFICATION_KEY, snapshot.sequence())
            .map_err(|()| EngineError::StorageFailure)?;
        Ok(matches!(verified, Some(DocumentValue::String(_))))
    }

    /// Installs the target's narrow route catalog before source cutover. The
    /// catalog is committed on the target file, so post-cutover writes retain
    /// the same generation and physical shard admission fence.
    pub(crate) fn install_split_target_catalog(
        &self,
        scope: &crate::types::TenantScope,
        state: &DurableSplitState,
        generation: u64,
    ) -> Result<(), EngineError> {
        let context = state.context().ok_or(EngineError::Conflict)?;
        let route = ShardRoute::new(
            context.target_shard(),
            state.lower().map(ToOwned::to_owned),
            Some(state.split_key().to_vec()),
        )
        .map_err(|_| EngineError::Conflict)?;
        let table = RouteTable::new(vec![route])
            .map_err(|_| EngineError::Conflict)?
            .with_generation(generation);
        let replacement = encode_catalog(&RouteCatalog { table, generation })
            .map_err(|_| EngineError::Conflict)?;
        let incarnation = u128::from_be_bytes(self.database_id().as_bytes().to_owned());
        let lane = context.split_id();
        let identity = crate::shard_split::FileShardIdentity {
            database_incarnation: incarnation,
            shard_id: context.target_shard(),
            region: self.database_owner().region(),
            origin_lane_incarnation: lane,
        };
        let operation =
            OperationId::try_new(&format!("shard-target-route-{:032x}", context.split_id()))
                .map_err(|_| EngineError::InvalidDocument)?;
        let mut tx = self.begin(scope, self.read_snapshot(scope)?, operation)?;
        tx.internal_maintenance = true;
        tx.put_private_metadata(&key(incarnation), value(&replacement))?;
        tx.put_private_metadata(
            &file_shard_key(incarnation),
            value(&encode_file_identity(identity)),
        )?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_)
            | CommitOutcome::Rejected(CommitError::OperationIdReuse { .. }) => Ok(()),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }

    /// Reads the immutable physical shard/origin identity without inventing defaults.
    ///
    /// # Errors
    /// Refuses malformed records and mismatched database or Region ownership.
    pub fn file_shard_identity(
        &self,
        scope: &TenantScope,
    ) -> Result<Option<crate::shard_split::FileShardIdentity>, EngineError> {
        let incarnation = u128::from_be_bytes(self.database_id().as_bytes());
        let snapshot = self.read_snapshot(scope)?;
        let raw = self
            .lock_storage()?
            .port
            .private_metadata_at(scope.tenant(), &file_shard_key(incarnation), snapshot.sequence())
            .map_err(|()| EngineError::StorageFailure)?;
        let Some(raw) = bytes(raw).map_err(|_| EngineError::StorageFailure)? else {
            return Ok(None);
        };
        let identity = decode_file_identity(&raw)?;
        if identity.database_incarnation != incarnation
            || identity.region != self.database_owner().region()
        {
            return Err(EngineError::Conflict);
        }
        Ok(Some(identity))
    }

    /// Checks physical range ownership before reading an original request result.
    ///
    /// # Errors
    /// Refuses keys assigned to another file and invalid durable routing state.
    pub fn validate_document_shard_route(
        &self,
        scope: &TenantScope,
        collection: &crate::types::CollectionName,
        id: &crate::types::DocumentId,
    ) -> Result<(), EngineError> {
        if let Some((router, _)) = self.load_shard_router(scope)? {
            self.shard_admission_for_key(scope, &router, &canonical_shard_key(collection, id))?;
        }
        Ok(())
    }

    /// Validates that a canonical routed key belongs to this physical file.
    /// The returned token is rechecked against durable routing at commit.
    ///
    /// # Errors
    /// Refuses malformed keys, stale routes, and keys owned by another file.
    pub fn shard_admission_for_key(
        &self,
        scope: &TenantScope,
        router: &crate::shard_split::ShardRouter,
        key: &[u8],
    ) -> Result<ShardAdmissionToken, EngineError> {
        decode_canonical_shard_key(key).map_err(|_| EngineError::InvalidDocument)?;
        let shard = router.route(key).ok_or(EngineError::Conflict)?;
        let token = ShardAdmissionToken::for_key(
            u128::from_be_bytes(self.database_id().as_bytes()),
            router.table().generation(),
            shard,
            key,
        );
        self.validate_shard_admission(scope, &token, &[])?;
        Ok(token)
    }

    /// Validates a bounded batch whose canonical keys all route to one file.
    pub fn shard_admission_for_keys(
        &self,
        scope: &TenantScope,
        router: &crate::shard_split::ShardRouter,
        keys: &[Vec<u8>],
    ) -> Result<ShardAdmissionToken, EngineError> {
        let Some(first) = keys.first() else { return Err(EngineError::Conflict) };
        let shard = router.route(first).ok_or(EngineError::Conflict)?;
        if keys.iter().any(|key| router.route(key) != Some(shard)) {
            return Err(EngineError::Conflict);
        }
        let token = ShardAdmissionToken::for_keys(
            u128::from_be_bytes(self.database_id().as_bytes()),
            router.table().generation(),
            shard,
            keys.iter().map(Vec::as_slice),
        );
        self.validate_shard_admission(scope, &token, &[])?;
        Ok(token)
    }

    /// Loads the authoritative serving router for an admitted database.
    /// Catalog decoding remains engine-owned; callers receive only the
    /// validated router and its durable generation.
    pub fn load_shard_router(
        &self,
        scope: &TenantScope,
    ) -> Result<Option<(crate::shard_split::ShardRouter, u64)>, EngineError> {
        let incarnation = u128::from_be_bytes(self.database_id().as_bytes().to_owned());
        Ok(self
            .load_shard_route_catalog(scope, incarnation)
            .map_err(|_| EngineError::StorageFailure)?
            .map(|catalog| {
                (crate::shard_split::ShardRouter::from_table(catalog.table), catalog.generation)
            }))
    }

    pub(crate) fn validate_shard_admission(
        &self,
        scope: &TenantScope,
        token: &ShardAdmissionToken,
        writes: &[crate::storage::StagedWrite],
    ) -> Result<(), EngineError> {
        let incarnation = u128::from_be_bytes(self.database_id().as_bytes().to_owned());
        if token.database_incarnation != incarnation || token.shard_id == 0 || token.keys.is_empty()
        {
            return Err(EngineError::Conflict);
        }
        let Some(catalog) = self
            .load_shard_route_catalog(scope, token.database_incarnation)
            .map_err(|_| EngineError::StorageFailure)?
        else {
            return Err(EngineError::Conflict);
        };
        let snapshot = self.read_snapshot(scope)?;
        let identity = self
            .lock_storage()?
            .port
            .private_metadata_at(scope.tenant(), &file_shard_key(incarnation), snapshot.sequence())
            .map_err(|()| EngineError::StorageFailure)?;
        let identity = bytes(identity).map_err(|_| EngineError::StorageFailure)?;
        (identity.as_deref() == Some(token.shard_id.to_be_bytes().as_slice())
            && catalog.generation == token.route_generation
            && token.keys.iter().all(|key| decode_canonical_shard_key(key).is_ok())
            && token.keys.iter().all(|key| catalog.table.route(key) == Some(token.shard_id))
            && writes.iter().all(|write| {
                let collection = match write {
                    crate::storage::StagedWrite::Put { document, .. }
                    | crate::storage::StagedWrite::ReplicatedPut { document, .. } => {
                        document.collection()
                    }
                    crate::storage::StagedWrite::Delete {
                        collection: Some(collection), ..
                    }
                    | crate::storage::StagedWrite::ReplicatedDelete { collection, .. } => {
                        collection
                    }
                    crate::storage::StagedWrite::Delete { collection: None, .. } => return false,
                };
                let key = canonical_shard_key(collection, write.document_id());
                token.keys.contains(&key)
            }))
        .then_some(())
        .ok_or(EngineError::Conflict)
    }

    pub(crate) fn shard_catalog_exists(&self, scope: &TenantScope) -> Result<bool, EngineError> {
        Ok(self
            .load_shard_route_catalog(
                scope,
                u128::from_be_bytes(self.database_id().as_bytes().to_owned()),
            )
            .map_err(|_| EngineError::StorageFailure)?
            .is_some())
    }
    /// Loads the route catalog for a database incarnation from a snapshot.
    /// Reservation-free: the origin-commit fast path calls this on every
    /// commit, so it must not reserve the shared `route-load` operation
    /// identity. Reserving it turned concurrent distinct-document commits
    /// into spurious `Conflict` rejections via the fail-closed
    /// `unwrap_or(true)` in the commit path.
    pub(crate) fn load_shard_route_catalog(
        &self,
        scope: &TenantScope,
        incarnation: u128,
    ) -> Result<Option<RouteCatalog>, RouteCatalogError> {
        let snapshot = self.read_snapshot(scope)?;
        let value = self
            .lock_storage()
            .map_err(RouteCatalogError::from)?
            .port
            .private_metadata_at(scope.tenant(), &key(incarnation), snapshot.sequence())
            .map_err(|()| RouteCatalogError::Engine(EngineError::StorageFailure))?;
        bytes(value).and_then(|value| value.map_or(Ok(None), |raw| decode_catalog(&raw).map(Some)))
    }

    /// Atomically installs route bytes and the durable cutover split record.
    pub(crate) fn install_shard_route_catalog(
        &self,
        scope: &TenantScope,
        incarnation: u128,
        expected: Option<&[u8]>,
        replacement: &[u8],
        split: &DurableSplitState,
        operation: OperationId,
    ) -> Result<u64, RouteCatalogError> {
        let catalog = decode_catalog(replacement)?;
        let mut tx = self.begin(scope, self.read_snapshot(scope)?, operation)?;
        let current = bytes(tx.read_private_metadata(&key(incarnation))?)?;
        let identity = bytes(tx.read_private_metadata(&file_shard_key(incarnation))?)?;
        if incarnation != u128::from_be_bytes(self.database_id().as_bytes()) {
            return Err(RouteCatalogError::Conflict);
        }
        if current.is_none() {
            let [route] = catalog.table.routes() else {
                return Err(RouteCatalogError::Malformed);
            };
            if identity.is_some() || catalog.generation != 0 {
                return Err(RouteCatalogError::Conflict);
            }
            tx.put_private_metadata(
                &file_shard_key(incarnation),
                value(&route.shard_id().to_be_bytes()),
            )?;
        } else if identity.as_ref().is_none_or(|bytes| bytes.len() != 8) {
            return Err(RouteCatalogError::Conflict);
        }
        if current.as_deref() == Some(replacement) {
            return Ok(catalog.generation);
        }
        if current.as_deref() != expected {
            return Err(RouteCatalogError::Conflict);
        }
        tx.put_private_metadata(&key(incarnation), value(replacement))?;
        tx.put_private_metadata(&split_key(incarnation), value(&split.encode()))?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(catalog.generation),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(RouteCatalogError::Conflict),
            CommitOutcome::Rejected(error) => Err(RouteCatalogError::Engine(match error {
                CommitError::Validation => EngineError::InvalidDocument,
                CommitError::OperationIdReuse { .. } => EngineError::Conflict,
                CommitError::EngineClosed => EngineError::Closed(super::EngineClosed),
                CommitError::UsageRequiresSynced => EngineError::InvalidDocument,
                CommitError::StorageFailure => EngineError::StorageFailure,
                CommitError::Conflict => EngineError::Conflict,
            })),
            CommitOutcome::Unknown(_) => {
                Err(RouteCatalogError::Engine(EngineError::StorageFailure))
            }
        }
    }

    /// Installs a fenced split route and its cut-over record atomically.
    pub(crate) fn install_shard_split_route(
        &self,
        scope: &TenantScope,
        context: &CutoverContext,
        state: &DurableSplitState,
        operation: OperationId,
    ) -> Result<u64, RouteCatalogError> {
        if !context.permit.is_valid() || state.context() != Some(context.split) {
            return Err(RouteCatalogError::Malformed);
        }
        if context.permit.required() < context.drained_through
            || state.source_high_water() > context.drained_through
            || state.acked_through() > context.drained_through
            || state.target_applied_through() < context.drained_through
        {
            return Err(RouteCatalogError::Conflict);
        }
        let _fence = self
            .origin_commit_barrier
            .write()
            .map_err(|_| RouteCatalogError::Engine(EngineError::StorageFailure))?;
        // A writer may have committed before this exclusive fence was
        // acquired while the split worker still carried the old watermark.
        // The published sequence is the engine's durable upper bound for all
        // origin writes, so the target must cover it before cutover proceeds.
        if context.drained_through < self.published_sequence()?.get() {
            return Err(RouteCatalogError::Conflict);
        }
        let mut tx = self.begin(scope, self.read_snapshot(scope)?, operation)?;
        tx.internal_maintenance = true;
        let route_key = key(context.split.database_incarnation());
        if bytes(tx.read_private_metadata(&file_shard_key(context.split.database_incarnation()))?)?
            .as_deref()
            != Some(context.split.source_shard().to_be_bytes().as_slice())
        {
            return Err(RouteCatalogError::Conflict);
        }
        let current =
            bytes(tx.read_private_metadata(&route_key)?)?.ok_or(RouteCatalogError::Malformed)?;
        let catalog = decode_catalog(&current)?;
        if catalog.generation != context.route_generation {
            return Err(RouteCatalogError::Conflict);
        }
        let mut table = catalog.table;
        table
            .split(
                context.split.source_shard(),
                context.split.target_shard(),
                state.split_key().to_vec(),
                context.permit,
            )
            .map_err(|_| RouteCatalogError::Conflict)?;
        let generation = catalog.generation.checked_add(1).ok_or(RouteCatalogError::Malformed)?;
        let replacement = encode_catalog(&RouteCatalog { table, generation })?;
        let mut lifecycle = ShardSplit::from_durable_state(state.clone())
            .map_err(|_| RouteCatalogError::Malformed)?;
        // The drained source frontier is the actual snapshot represented by
        // this route, even when the worker's last persisted checkpoint lagged.
        lifecycle.note_source_high_water(context.drained_through);
        lifecycle.cut_over_with_permit(context.permit).map_err(|_| RouteCatalogError::Conflict)?;
        tx.put_private_metadata(&route_key, value(&replacement))?;
        tx.put_private_metadata(
            &split_key(context.split.database_incarnation()),
            value(&lifecycle.durable_state().encode()),
        )?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(generation),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(RouteCatalogError::Conflict),
            CommitOutcome::Rejected(error) => Err(RouteCatalogError::Engine(match error {
                CommitError::Validation => EngineError::InvalidDocument,
                CommitError::OperationIdReuse { .. } => EngineError::Conflict,
                CommitError::EngineClosed => EngineError::Closed(super::EngineClosed),
                CommitError::UsageRequiresSynced => EngineError::InvalidDocument,
                CommitError::StorageFailure => EngineError::StorageFailure,
                CommitError::Conflict => EngineError::Conflict,
            })),
            CommitOutcome::Unknown(_) => {
                Err(RouteCatalogError::Engine(EngineError::StorageFailure))
            }
        }
    }

    /// Recovers the durable route receipt and authoritative catalog together.
    pub(crate) fn recover_shard_split_route(
        &self,
        scope: &TenantScope,
        split: &crate::shard_split::SplitContext,
    ) -> Result<Option<(CommittedSplitRoute, RouteCatalog)>, RouteCatalogError> {
        let tx = self.begin(
            scope,
            self.read_snapshot(scope)?,
            OperationId::try_new("route-recover").map_err(|_| RouteCatalogError::Malformed)?,
        )?;
        let catalog = bytes(tx.read_private_metadata(&key(split.database_incarnation()))?)?
            .map(|raw| decode_catalog(&raw))
            .transpose()?;
        let Some(catalog) = catalog else {
            return Ok(None);
        };
        let Some(raw) = bytes(tx.read_private_metadata(&split_key(split.database_incarnation()))?)?
        else {
            return Ok(None);
        };
        let state = DurableSplitState::decode(&raw).map_err(|_| RouteCatalogError::Malformed)?;
        if state.context() != Some(*split)
            || !matches!(state.phase(), SplitPhase::CutOver | SplitPhase::Complete)
        {
            return Ok(None);
        }
        Ok(Some((
            CommittedSplitRoute {
                split: *split,
                generation: catalog.generation,
                route: catalog.table.clone(),
            },
            catalog,
        )))
    }
}
