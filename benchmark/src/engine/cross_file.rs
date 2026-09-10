//! Database-owned identity and visibility records for cross-file operations.

use std::collections::BTreeMap;

use crate::types::OperationId;
use crate::{
    engine::{CommitError, CommitOutcome, Engine, EngineError},
    types::{DocumentValue, LocalDurability, TenantScope},
};

const MAX_PARTICIPANTS: usize = 64;

type CrossFileInstallResult = Result<
    (super::CommitReceipt, Vec<(std::sync::Arc<Engine>, super::CommitPublicationEvent)>),
    EngineError,
>;

type PreparedCrossFileParticipant =
    (std::sync::Arc<Engine>, crate::storage::PreparedCrossFileIntent);

/// An admitted immutable route and all physical snapshots for one logical
/// transaction. Only the member registry can construct this binding.
pub(crate) struct CrossFileBatch {
    pub(crate) router: crate::shard_split::ShardRouter,
    pub(crate) participants: BTreeMap<u64, (std::sync::Arc<Engine>, crate::types::ReadSnapshot)>,
    pub(crate) keys: std::collections::BTreeSet<Vec<u8>>,
    pub(crate) uniform_contexts: BTreeMap<u64, Vec<u8>>,
}

impl CrossFileBatch {
    pub(super) fn read_identifier(
        &self,
        tenant: &crate::types::TenantId,
        id: &crate::types::DocumentId,
    ) -> Result<Option<crate::types::Document>, EngineError> {
        for key in &self.keys {
            let (collection, candidate) = crate::shard_split::decode_canonical_shard_key(key)
                .map_err(|_| EngineError::InvalidDocument)?;
            if candidate != *id {
                continue;
            }
            let (engine, sequence) = self.read_target(&collection, id)?;
            engine.validate_storage_authorization()?;
            if let Some(document) = engine.lock_storage()?.port.read_document_in_collection_at(
                sequence,
                tenant,
                &collection,
                id,
            ) {
                return Ok(Some(document));
            }
        }
        Ok(None)
    }

    pub(super) fn read_target(
        &self,
        collection: &crate::types::CollectionName,
        id: &crate::types::DocumentId,
    ) -> Result<(&Engine, crate::types::StorageSequence), EngineError> {
        let key = crate::shard_split::canonical_shard_key(collection, id);
        if !self.keys.contains(&key) {
            return Err(EngineError::Conflict);
        }
        let shard = self.router.route(&key).ok_or(EngineError::Conflict)?;
        let (engine, snapshot) = self.participants.get(&shard).ok_or(EngineError::Conflict)?;
        Ok((engine, snapshot.sequence()))
    }
}

impl super::Transaction<'_> {
    pub(super) fn commit_cross_file(mut self, durability: LocalDurability) -> CommitOutcome {
        if durability != LocalDurability::Synced
            || !self.valid_batch_request_binding()
            || !self.valid_document_request_binding()
            || self.internal_maintenance
            || self.replication_publication.is_some()
            || self.merge_conflict.is_some()
            || self.replication_fingerprint.is_some()
            || self.shard_copy
        {
            return CommitOutcome::Rejected(CommitError::Validation);
        }
        let Some(batch) = self.cross_file_batch.take() else {
            return CommitOutcome::Rejected(CommitError::Validation);
        };
        let engine = self.engine;
        let Ok(admission) = engine.origin_commit_barrier.write() else {
            return CommitOutcome::Rejected(CommitError::StorageFailure);
        };
        let mut intents = match self.cross_file_intents(&batch, durability) {
            Ok(intents) => intents,
            Err(reason) => return CommitOutcome::Rejected(reason),
        };
        let identity = match self.cross_file_identity(&batch) {
            Ok(identity) => identity,
            Err(reason) => return CommitOutcome::Rejected(reason),
        };
        if let Ok(Some((existing, _, receipt))) =
            engine.cross_file_completion(&self.scope, &self.operation)
        {
            return if existing.write_digest == identity.write_digest {
                CommitOutcome::Committed(receipt)
            } else {
                CommitOutcome::Rejected(CommitError::OperationIdReuse)
            };
        }
        let prepared = match self.prepare_cross_file_participants(&batch, &identity, &mut intents) {
            Ok(prepared) => prepared,
            Err(outcome) => return outcome,
        };
        if engine
            .decide_cross_file_locked(&self.scope, &identity, CrossFileDecision::Committed)
            .is_err()
        {
            return self.unknown_cross_file();
        }
        let result = engine.install_cross_file_prepared_locked(&self.scope, &identity, &prepared);
        drop(admission);
        match result {
            Ok((receipt, publications)) => {
                for (participant, event) in publications {
                    participant.emit_publication_event(&event);
                }
                CommitOutcome::Committed(receipt)
            }
            Err(_) => self.unknown_cross_file(),
        }
    }

    fn unknown_cross_file(&self) -> CommitOutcome {
        CommitOutcome::Unknown(super::UnknownCommit::new(
            self.operation.clone(),
            super::UnknownReason::ResponseInterrupted,
        ))
    }

    fn cross_file_identity(
        &self,
        batch: &CrossFileBatch,
    ) -> Result<CrossFileIdentity, CommitError> {
        let engine = self.engine;
        let Some(writes) = self
            .writes
            .iter()
            .map(crate::storage::participant::ParticipantWrite::from_staged)
            .collect::<Option<Vec<_>>>()
        else {
            return Err(CommitError::Validation);
        };
        let Ok(payload) = crate::storage::participant::encode_prepare_payload(
            &writes,
            &self.metadata,
            &self.schema_envelopes,
            None,
        ) else {
            return Err(CommitError::Validation);
        };
        let ordinary_digest = super::usage_bound_commit_digest(
            crate::storage::transaction_digest(
                self.scope.tenant(),
                &self.writes,
                &self.metadata,
                &self.schema_envelopes,
            ),
            self.usage_admission.as_ref(),
            self.budget_allocation.as_ref(),
        );
        let mut hash = blake3::Hasher::new();
        hash.update(b"terrabase-cross-file-origin-v1\0");
        hash.update(&payload);
        hash.update(&ordinary_digest.to_be_bytes());
        Ok(CrossFileIdentity {
            tenant: self.scope.tenant().to_string(),
            coordinator_origin: match engine.replication_origin() {
                Ok(origin) => origin,
                Err(_) => return Err(CommitError::StorageFailure),
            },
            incarnation: u128::from_be_bytes(engine.database_id().as_bytes()),
            authority_epoch: self.expected_origin_epoch,
            route_generation: batch.router.table().generation(),
            read_frontier: self.snapshot_sequence.get(),
            write_digest: *hash.finalize().as_bytes(),
            participants: batch
                .participants
                .iter()
                .map(|(shard, (_, snapshot))| (format!("shard-{shard}"), snapshot.sequence().get()))
                .collect(),
            operation: self.operation.clone(),
            decision_epoch: self.snapshot_sequence.get().saturating_add(1),
        })
    }

    fn abort_cross_file_preparation(
        &self,
        identity: &CrossFileIdentity,
        prepared: &[PreparedCrossFileParticipant],
    ) -> Result<(), EngineError> {
        let engine = self.engine;
        // No install has run. A durable abort is the only path
        // from prepared reservations back to ordinary admission.
        if engine
            .decide_cross_file_locked(&self.scope, identity, CrossFileDecision::Aborted)
            .is_err()
        {
            return Err(EngineError::StorageFailure);
        }
        for (participant, intent) in prepared {
            if participant
                .lock_storage()
                .and_then(|mut storage| {
                    storage
                        .port
                        .abort_cross_file_intent(&intent.identity)
                        .map_err(|()| EngineError::StorageFailure)
                })
                .is_err()
            {
                return Err(EngineError::StorageFailure);
            }
        }
        if engine.clear_cross_file_abort_locked(&self.scope, identity).is_err() {
            return Err(EngineError::StorageFailure);
        }
        Ok(())
    }

    fn prepare_cross_file_participants(
        &self,
        batch: &CrossFileBatch,
        identity: &CrossFileIdentity,
        intents: &mut BTreeMap<u64, crate::storage::CommitIntent>,
    ) -> Result<Vec<PreparedCrossFileParticipant>, CommitOutcome> {
        let engine = self.engine;
        let request = self
            .metadata
            .iter()
            .find(|write| write.key == format!("batch-request:{}", self.operation.as_str()))
            .and_then(|write| write.value.clone());
        let Ok((_, prepare_receipt)) =
            engine.prepare_cross_file_request_locked(&self.scope, identity.clone(), request)
        else {
            return Err(self.unknown_cross_file());
        };
        let Ok(active_dot) = engine.cross_file_publication_dot(
            &self.scope,
            prepare_receipt.sequence(),
            prepare_receipt.operation(),
        ) else {
            return Err(self.unknown_cross_file());
        };
        let active_context = super::merge::encode_context(
            &super::merge::DocumentContext::from_frontier(BTreeMap::from([active_dot])),
        );
        for intent in intents.values_mut() {
            intent.cross_file_dependencies = Some(active_context.clone());
        }
        let mut prepared = Vec::new();
        for (shard, (participant, snapshot)) in &batch.participants {
            let participant_identity = participant_identity(
                participant,
                self.scope.tenant(),
                identity,
                *shard,
                snapshot.sequence(),
            );
            let preparation = participant
                .lock_storage()
                .map_err(|_| crate::storage::CommitRejection::Storage)
                .and_then(|mut storage| {
                    storage
                        .port
                        .prepare_cross_file_intent(&participant_identity, intents.get(shard))
                });
            match preparation {
                Ok(intent) => prepared.push((participant.clone(), intent)),
                Err(reason) => {
                    if self.abort_cross_file_preparation(identity, &prepared).is_err() {
                        return Err(self.unknown_cross_file());
                    }
                    return Err(CommitOutcome::Rejected(reason.to_commit_error()));
                }
            }
        }
        Ok(prepared)
    }

    fn cross_file_intents(
        &self,
        batch: &CrossFileBatch,
        durability: LocalDurability,
    ) -> Result<BTreeMap<u64, crate::storage::CommitIntent>, CommitError> {
        let mut groups = self.cross_file_write_groups(batch)?;
        // The refill is installed once in the logical database's root file.
        // Moving the meter with the touched key set would clone or strand credit.
        let billing_shard = *batch
            .participants
            .iter()
            .find(|(_, (engine, _))| std::ptr::eq(engine.as_ref(), self.engine))
            .map(|(shard, _)| shard)
            .ok_or(CommitError::Conflict)?;
        let total_write_units = self.writes.iter().try_fold(0_u64, |units, write| {
            let amount = match write {
                crate::storage::StagedWrite::Put { document, .. } => {
                    crate::storage::billed_writes_for_bytes(
                        crate::storage::encode_document_body(document).len() as u64,
                    )
                }
                crate::storage::StagedWrite::Delete { .. } => 1,
                _ => return Err(CommitError::Validation),
            };
            units.checked_add(amount).ok_or(CommitError::Validation)
        })?;
        let mut intents = BTreeMap::new();
        for (shard, (engine, snapshot)) in &batch.participants {
            let expected_uniform_context =
                self.cross_file_uniform_context(batch, *shard, engine, snapshot)?;
            let writes = groups.remove(shard).unwrap_or_default();
            let billing = crate::storage::CrossFileBilling {
                operation: self.operation.clone(),
                metering_participant: format!("shard-{billing_shard}"),
                participant: format!("shard-{shard}"),
                total_write_units,
            };
            if let Some(intent) = self.cross_file_participant_intent(
                engine,
                snapshot,
                writes,
                expected_uniform_context,
                billing,
                durability,
            )? {
                intents.insert(*shard, intent);
            }
        }
        if !groups.is_empty() {
            return Err(CommitError::Conflict);
        }
        Ok(intents)
    }
    fn cross_file_write_groups(
        &self,
        batch: &CrossFileBatch,
    ) -> Result<BTreeMap<u64, Vec<crate::storage::StagedWrite>>, CommitError> {
        let refused = |_| CommitError::Conflict;
        if self.writes.is_empty()
            || self.writes.len() > 1000
            || crate::storage::staged_writes_have_duplicate_targets(&self.writes)
            || self.engine.validate_storage_authorization().is_err()
            || self.engine.origin_epoch().map_err(refused)? != (self.expected_origin_epoch, false)
            || self.engine.read_cross_file_active(&self.scope).map_err(refused)?.is_some()
            || self
                .engine
                .load_shard_router(&self.scope)
                .map_err(refused)?
                .as_ref()
                .map(|(_, generation)| *generation)
                != Some(batch.router.table().generation())
        {
            return Err(CommitError::Conflict);
        }
        let mut groups = BTreeMap::<u64, Vec<crate::storage::StagedWrite>>::new();
        for write in &self.writes {
            let (collection, id) = match write {
                crate::storage::StagedWrite::Put { document, .. } => {
                    (document.collection(), document.id())
                }
                crate::storage::StagedWrite::Delete {
                    collection: Some(collection),
                    id,
                    ttl_guard: None,
                    ..
                } => (collection, id),
                _ => return Err(CommitError::Validation),
            };
            let key = crate::shard_split::canonical_shard_key(collection, id);
            if !batch.keys.contains(&key) {
                return Err(CommitError::Conflict);
            }
            let shard = batch.router.route(&key).ok_or(CommitError::Conflict)?;
            groups.entry(shard).or_default().push(write.clone());
        }
        Ok(groups)
    }

    fn cross_file_uniform_context(
        &self,
        batch: &CrossFileBatch,
        shard: u64,
        engine: &Engine,
        snapshot: &crate::types::ReadSnapshot,
    ) -> Result<Option<Vec<u8>>, CommitError> {
        let refused = |_| CommitError::Conflict;
        if engine.validate_storage_authorization().is_err()
            || engine.published_sequence().map_err(refused)? != snapshot.sequence()
            || engine
                .lifecycle_state(&engine.scope_for(self.scope.tenant()))
                .map(|state| state.paused || state.access_revoked())
                .map_err(|_| CommitError::Conflict)?
        {
            return Err(CommitError::Conflict);
        }
        let is_root = std::ptr::eq(engine, self.engine);
        let expected_uniform_context = batch
            .uniform_contexts
            .get(&shard)
            .cloned()
            .or_else(|| if is_root { self.expected_uniform_context.clone() } else { None });
        if let Some(expected) = &expected_uniform_context {
            // Check the user's original cut before our own ACTIVE publication
            // advances it. Storage subsequently proves any delta consists only
            // of this operation's durable controls before freezing admission.
            let frontier = engine
                .lock_storage()
                .map_err(refused)?
                .port
                .live_origin_prefixes(self.scope.tenant())
                .map_err(|()| CommitError::StorageFailure)?
                .into_iter()
                .map(|entry| (entry.origin, entry.origin_sequence))
                .collect::<Vec<(String, u64)>>();
            let current = super::merge::encode_context(
                &super::merge::DocumentContext::from_frontier(frontier),
            );
            if current != *expected {
                return Err(CommitError::Conflict);
            }
        }
        Ok(expected_uniform_context)
    }

    fn cross_file_participant_intent(
        &self,
        engine: &Engine,
        snapshot: &crate::types::ReadSnapshot,
        writes: Vec<crate::storage::StagedWrite>,
        expected_uniform_context: Option<Vec<u8>>,
        billing: crate::storage::CrossFileBilling,
        durability: LocalDurability,
    ) -> Result<Option<crate::storage::CommitIntent>, CommitError> {
        let refused = |_| CommitError::Conflict;
        let is_root = std::ptr::eq(engine, self.engine);
        let metadata = if is_root {
            self.metadata
                .iter()
                .filter(|write| write.key != format!("batch-request:{}", self.operation.as_str()))
                .cloned()
                .collect()
        } else {
            Vec::new()
        };
        let schema_envelopes = if is_root { self.schema_envelopes.clone() } else { Vec::new() };
        if !is_root && writes.is_empty() && metadata.is_empty() && schema_envelopes.is_empty() {
            return Ok(None);
        }
        if engine.admit_origin_clock().is_err() {
            return Err(CommitError::Validation);
        }
        let usage_admission = if billing.participant == billing.metering_participant {
            self.usage_admission.clone()
        } else {
            None
        };
        let budget_allocation = if billing.participant == billing.metering_participant {
            self.budget_allocation.clone()
        } else {
            None
        };
        let digest = super::usage_bound_commit_digest(
            crate::storage::transaction_digest(
                self.scope.tenant(),
                &writes,
                &metadata,
                &schema_envelopes,
            ),
            usage_admission.as_ref(),
            budget_allocation.as_ref(),
        );
        Ok(Some(crate::storage::CommitIntent {
            tenant: self.scope.tenant().clone(),
            operation: self.operation.clone(),
            digest,
            snapshot_sequence: snapshot.sequence(),
            durability,
            expected_origin_epoch: Some(engine.origin_epoch().map_err(refused)?.0),
            expected_uniform_context,
            usage_admission,
            budget_allocation,
            internal_maintenance: false,
            shard_copy: false,
            replication_fingerprint: None,
            replication_publication: None,
            merge_conflict: None,
            hlc: engine.tick_replication_clock(),
            writes,
            metadata,
            schema_envelopes,
            cross_file_control: None,
            cross_file_dependencies: None,
            cross_file_billing: Some(billing),
            generated_primary_file_identity: None,
        }))
    }

    pub(crate) fn bind_cross_file_batch(&mut self, batch: CrossFileBatch) {
        self.cross_file_batch = Some(batch);
    }

    /// Binds a proven uniform frontier to the physical participant serving
    /// this admitted batch key. Each lane retains its own causal context.
    ///
    /// # Errors
    /// Rejects keys outside this batch and malformed frontiers.
    pub fn require_routed_uniform_context(
        &mut self,
        collection: &crate::types::CollectionName,
        id: &crate::types::DocumentId,
        frontier: BTreeMap<String, u64>,
    ) -> Result<(), EngineError> {
        let Some(batch) = &self.cross_file_batch else {
            return self.require_uniform_context(frontier);
        };
        let key = crate::shard_split::canonical_shard_key(collection, id);
        if !batch.keys.contains(&key) {
            return Err(EngineError::Conflict);
        }
        let shard = batch.router.route(&key).ok_or(EngineError::Conflict)?;
        let previous = self.expected_uniform_context.take();
        let result = self.require_uniform_context(frontier);
        let encoded = self.expected_uniform_context.take();
        self.expected_uniform_context = previous;
        result?;
        self.cross_file_batch
            .as_mut()
            .ok_or(EngineError::Conflict)?
            .uniform_contexts
            .insert(shard, encoded.ok_or(EngineError::InvalidDocument)?);
        Ok(())
    }
}

/// Local database receipt for a cross-file decision. It carries no replica
/// acknowledgement or cross-region finality claim.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LocalCrossFileReceipt {
    operation: OperationId,
    sequence: u64,
    digest: [u8; 32],
    durability: LocalDurability,
    phase: CrossFileReceiptPhase,
}

/// Local coordinator phase represented by a receipt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CrossFileReceiptPhase {
    /// Coordinator prepared the decision without committing it.
    Prepare,
    /// Coordinator reached a committed decision.
    Decision,
}

impl LocalCrossFileReceipt {
    /// Logical operation identity.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }
    /// Local publication sequence.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }
    /// Canonical coordinator record digest.
    #[must_use]
    pub const fn digest(&self) -> [u8; 32] {
        self.digest
    }
    /// Local durability reached by the Engine.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }
    /// Coordinator phase reached by this receipt.
    #[must_use]
    pub const fn phase(&self) -> CrossFileReceiptPhase {
        self.phase
    }
}

/// Immutable identity binding one cross-file operation to its database view.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CrossFileIdentity {
    /// Tenant scope owning the coordinator record.
    pub(crate) tenant: String,
    /// Authenticated root publication lane that alone may drive crash recovery.
    pub(crate) coordinator_origin: String,
    /// Durable database incarnation.
    pub(crate) incarnation: u128,
    /// Authority epoch used while preparing the operation.
    pub(crate) authority_epoch: u64,
    /// Route generation used while preparing the operation.
    pub(crate) route_generation: u64,
    /// Read frontier at which participants were selected.
    pub(crate) read_frontier: u64,
    /// Exact operation write-set digest.
    pub(crate) write_digest: [u8; 32],
    /// Exact participant name to snapshot sequence binding.
    pub(crate) participants: BTreeMap<String, u64>,
    /// Stable operation identity.
    pub(crate) operation: OperationId,
    /// Monotonic coordinator decision epoch.
    pub(crate) decision_epoch: u64,
}

impl CrossFileIdentity {
    /// Validates bounded identity fields and participant uniqueness.
    pub(crate) fn validate(&self) -> bool {
        !self.tenant.is_empty()
            && self.tenant.len() <= 255
            && !self.coordinator_origin.is_empty()
            && self.coordinator_origin.len() <= 255
            && !self.coordinator_origin.chars().any(char::is_control)
            && self.incarnation != 0
            && self.authority_epoch != 0
            && i64::try_from(self.authority_epoch).is_ok()
            && i64::try_from(self.route_generation).is_ok()
            && i64::try_from(self.read_frontier).is_ok()
            && self.decision_epoch != 0
            && i64::try_from(self.decision_epoch).is_ok()
            && !self.participants.is_empty()
            && self.participants.len() <= MAX_PARTICIPANTS
            && self.participants.iter().all(|(name, sequence)| {
                !name.is_empty() && name.len() <= 255 && i64::try_from(*sequence).is_ok()
            })
    }
}

/// Durable coordinator decision. A committed decision alone is not visible.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum CrossFileDecision {
    /// All participants may install the operation.
    Committed,
    /// No participant may install the operation.
    Aborted,
}

/// Durable coordinator record, retaining both identity and terminal state.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CrossFileRecord {
    /// Immutable operation identity.
    pub(crate) identity: CrossFileIdentity,
    /// Current durable decision, when terminal.
    pub(crate) decision: Option<CrossFileDecision>,
}

/// One database-wide visible frontier after participant installation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct VisibleFrontier {
    /// Database version made visible to readers.
    pub(crate) version: u64,
    /// Exact participant snapshot sequence map installed for this version.
    pub(crate) participants: BTreeMap<String, u64>,
}

impl VisibleFrontier {
    /// Validates a bounded, nonzero visible frontier.
    pub(crate) fn validate(&self) -> bool {
        self.version != 0
            && !self.participants.is_empty()
            && self.participants.len() <= MAX_PARTICIPANTS
            && self.participants.iter().all(|(name, sequence)| {
                !name.is_empty() && name.len() <= 255 && *sequence != u64::MAX
            })
    }
}

fn key(operation: &OperationId) -> String {
    format!("control:cross-file/v1/{}", operation.as_str())
}
fn visible_key() -> &'static str {
    "control:cross-file-visible/v1"
}
fn active_key() -> &'static str {
    "control:cross-file-active/v1"
}
fn finishing_key() -> &'static str {
    "control:cross-file-finishing/v1"
}
fn complete_key(operation: &OperationId) -> String {
    format!("control:cross-file-complete/v1/{}", operation.as_str())
}
fn request_key(operation: &OperationId) -> String {
    format!("control:cross-file-request/v1/{}", operation.as_str())
}
fn participant_identity(
    engine: &Engine,
    tenant: &crate::types::TenantId,
    identity: &CrossFileIdentity,
    shard: u64,
    snapshot: crate::types::StorageSequence,
) -> crate::storage::ParticipantIdentity {
    crate::storage::ParticipantIdentity {
        tenant: tenant.clone(),
        database_incarnation: identity.incarnation,
        operation: identity.operation.clone(),
        participant: format!("shard-{shard}"),
        owner: engine.database_owner().database().to_text(),
        digest: identity.write_digest,
        epoch: identity.authority_epoch,
        snapshot,
    }
}
fn maintenance_operation(
    identity: &CrossFileIdentity,
    phase: &str,
) -> Result<OperationId, EngineError> {
    let mut input = b"terrascale.cross-file.maintenance.v1".to_vec();
    input.extend_from_slice(identity.operation.as_str().as_bytes());
    input.extend_from_slice(phase.as_bytes());
    OperationId::try_new(&format!("cross-file-{phase}-{}", blake3::hash(&input).to_hex()))
        .map_err(|_| EngineError::InvalidDocument)
}

fn encode(record: &CrossFileRecord) -> Result<DocumentValue, EngineError> {
    encode_phase(record, "prepare")
}

fn encode_phase(record: &CrossFileRecord, phase: &str) -> Result<DocumentValue, EngineError> {
    let identity = &record.identity;
    if !identity.validate() {
        return Err(EngineError::InvalidDocument);
    }
    let participants: Vec<_> = identity
        .participants
        .iter()
        .map(|(name, sequence)| {
            Ok(DocumentValue::Object(BTreeMap::from([
                ("name".into(), DocumentValue::String(name.clone())),
                (
                    "sequence".into(),
                    DocumentValue::Integer(
                        i64::try_from(*sequence).map_err(|_| EngineError::InvalidDocument)?,
                    ),
                ),
            ])))
        })
        .collect::<Result<_, _>>()?;
    Ok(DocumentValue::Object(BTreeMap::from([
        ("tenant".into(), DocumentValue::String(identity.tenant.clone())),
        ("coordinator_origin".into(), DocumentValue::String(identity.coordinator_origin.clone())),
        ("incarnation".into(), DocumentValue::String(format!("{:032x}", identity.incarnation))),
        (
            "authority_epoch".into(),
            DocumentValue::Integer(
                i64::try_from(identity.authority_epoch)
                    .map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        (
            "route_generation".into(),
            DocumentValue::Integer(
                i64::try_from(identity.route_generation)
                    .map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        (
            "read_frontier".into(),
            DocumentValue::Integer(
                i64::try_from(identity.read_frontier).map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        ("write_digest".into(), DocumentValue::String(hex::encode(identity.write_digest))),
        ("participants".into(), DocumentValue::Array(participants)),
        ("operation".into(), DocumentValue::String(identity.operation.as_str().into())),
        (
            "decision_epoch".into(),
            DocumentValue::Integer(
                i64::try_from(identity.decision_epoch).map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        ("phase".into(), DocumentValue::String(phase.into())),
    ])))
}

fn integer(fields: &BTreeMap<String, DocumentValue>, name: &str) -> Option<u64> {
    match fields.get(name) {
        Some(DocumentValue::Integer(value)) if *value >= 0 => (*value).try_into().ok(),
        _ => None,
    }
}

fn decode(value: DocumentValue) -> Result<CrossFileRecord, EngineError> {
    const FIELDS: &[&str] = &[
        "tenant",
        "coordinator_origin",
        "incarnation",
        "authority_epoch",
        "route_generation",
        "read_frontier",
        "write_digest",
        "participants",
        "operation",
        "decision_epoch",
        "phase",
    ];
    let DocumentValue::Object(fields) = value else {
        return Err(EngineError::InvalidDocument);
    };
    if fields.keys().any(|key| !FIELDS.contains(&key.as_str())) {
        return Err(EngineError::InvalidDocument);
    }
    if !matches!(fields.get("phase"), Some(DocumentValue::String(phase)) if phase == "prepare" || phase == "commit" || phase == "abort")
    {
        return Err(EngineError::InvalidDocument);
    }
    let Some(DocumentValue::String(incarnation)) = fields.get("incarnation") else {
        return Err(EngineError::InvalidDocument);
    };
    let Some(DocumentValue::String(tenant)) = fields.get("tenant") else {
        return Err(EngineError::InvalidDocument);
    };
    let Some(DocumentValue::String(coordinator_origin)) = fields.get("coordinator_origin") else {
        return Err(EngineError::InvalidDocument);
    };
    let incarnation =
        u128::from_str_radix(incarnation, 16).map_err(|_| EngineError::InvalidDocument)?;
    let Some(DocumentValue::String(digest)) = fields.get("write_digest") else {
        return Err(EngineError::InvalidDocument);
    };
    let digest: [u8; 32] = hex::decode(digest)
        .map_err(|_| EngineError::InvalidDocument)?
        .try_into()
        .map_err(|_| EngineError::InvalidDocument)?;
    let Some(DocumentValue::Array(items)) = fields.get("participants") else {
        return Err(EngineError::InvalidDocument);
    };
    let mut participants = BTreeMap::new();
    for item in items {
        let DocumentValue::Object(item) = item else {
            return Err(EngineError::InvalidDocument);
        };
        let Some(DocumentValue::String(name)) = item.get("name") else {
            return Err(EngineError::InvalidDocument);
        };
        let Some(sequence) = integer(item, "sequence") else {
            return Err(EngineError::InvalidDocument);
        };
        if participants.insert(name.clone(), sequence).is_some() {
            return Err(EngineError::InvalidDocument);
        }
    }
    let Some(DocumentValue::String(operation)) = fields.get("operation") else {
        return Err(EngineError::InvalidDocument);
    };
    let identity = CrossFileIdentity {
        tenant: tenant.clone(),
        coordinator_origin: coordinator_origin.clone(),
        incarnation,
        authority_epoch: integer(&fields, "authority_epoch").ok_or(EngineError::InvalidDocument)?,
        route_generation: integer(&fields, "route_generation")
            .ok_or(EngineError::InvalidDocument)?,
        read_frontier: integer(&fields, "read_frontier").ok_or(EngineError::InvalidDocument)?,
        write_digest: digest,
        participants,
        operation: OperationId::try_new(operation).map_err(|_| EngineError::InvalidDocument)?,
        decision_epoch: integer(&fields, "decision_epoch").ok_or(EngineError::InvalidDocument)?,
    };
    let decision = match fields.get("phase") {
        Some(DocumentValue::String(phase)) if phase == "prepare" => None,
        Some(DocumentValue::String(phase)) if phase == "commit" => {
            Some(CrossFileDecision::Committed)
        }
        Some(DocumentValue::String(phase)) if phase == "abort" => Some(CrossFileDecision::Aborted),
        _ => return Err(EngineError::InvalidDocument),
    };
    identity
        .validate()
        .then_some(CrossFileRecord { identity, decision })
        .ok_or(EngineError::InvalidDocument)
}

/// Validates the complete coordinator mutation before storage grants a reservation bypass.
pub(crate) fn validate_control_origin(
    metadata: &[crate::storage::PrivateMetadataWrite],
    prior: Option<&DocumentValue>,
    expected_origin: &str,
) -> bool {
    let body = metadata
        .iter()
        .find(|write| write.key.starts_with("control:cross-file/v1/"))
        .and_then(|write| write.value.as_ref())
        .or(prior);
    body.is_some_and(|body| {
        decode(body.clone())
            .is_ok_and(|record| record.identity.coordinator_origin == expected_origin)
    })
}

/// Validates the complete coordinator mutation before storage grants a reservation bypass.
pub(crate) fn validate_control_metadata(
    tenant: &crate::types::TenantId,
    operation: &OperationId,
    metadata: &[crate::storage::PrivateMetadataWrite],
    reservation: Option<&crate::storage::ParticipantIdentity>,
    prior: Option<&DocumentValue>,
) -> bool {
    let fields: BTreeMap<_, _> =
        metadata.iter().map(|write| (write.key.as_str(), &write.value)).collect();
    if fields.len() != metadata.len() || fields.is_empty() {
        return false;
    }
    let value = |name: &str| fields.get(name).and_then(|value| value.as_ref());
    let clearing = fields.len() == 1
        && (fields.get(active_key()) == Some(&&None)
            || fields.get(finishing_key()) == Some(&&None));
    let Some(body) = (if clearing { prior } else { value(&key(operation)) }) else {
        return false;
    };
    let Ok(record) = decode(body.clone()) else { return false };
    let identity = &record.identity;
    if identity.tenant != tenant.to_string() || identity.operation != *operation {
        return false;
    }
    if reservation.is_some_and(|reserved| {
        reserved.tenant != *tenant
            || reserved.operation != *operation
            || reserved.database_incarnation != identity.incarnation
            || reserved.epoch != identity.authority_epoch
            || reserved.digest != identity.write_digest
            || identity.participants.get(&reserved.participant) != Some(&reserved.snapshot.get())
    }) {
        return false;
    }
    let phase = match record.decision {
        None => "prepare",
        Some(CrossFileDecision::Committed) => "commit",
        Some(CrossFileDecision::Aborted) => "abort",
    };
    if encode_phase(&record, phase).ok().as_ref() != Some(body) {
        return false;
    }
    if clearing {
        return if fields.contains_key(active_key()) {
            record.decision == Some(CrossFileDecision::Aborted)
        } else {
            record.decision == Some(CrossFileDecision::Committed)
        };
    }
    let request = request_key(operation);
    if fields.contains_key(request.as_str()) && !valid_control_request(value(&request)) {
        return false;
    }
    if !fields.contains_key(visible_key()) {
        let expected = 2 + usize::from(fields.contains_key(request.as_str()));
        return fields.len() == expected
            && value(active_key()) == Some(body)
            && (record.decision.is_none() || !fields.contains_key(request.as_str()));
    }
    let Some(visible) = value(visible_key()) else { return false };
    let complete = DocumentValue::Object(BTreeMap::from([
        ("identity".into(), body.clone()),
        ("visible".into(), visible.clone()),
    ]));
    record.decision == Some(CrossFileDecision::Committed)
        && fields.len() == 5 + usize::from(fields.contains_key(request.as_str()))
        && fields.get(active_key()) == Some(&&None)
        && value(finishing_key()) == Some(body)
        && value(&complete_key(operation)) == Some(&complete)
        && valid_control_visible(visible, identity)
}

fn valid_control_request(value: Option<&DocumentValue>) -> bool {
    let Some(DocumentValue::Array(items)) = value else { return false };
    matches!(items.as_slice(), [DocumentValue::String(digest), DocumentValue::String(count)]
        if hex::decode(digest).is_ok_and(|bytes| bytes.len() == 32)
            && count.parse::<usize>().is_ok_and(|count| (1..=1000).contains(&count)))
}

fn valid_control_visible(value: &DocumentValue, identity: &CrossFileIdentity) -> bool {
    let DocumentValue::Object(fields) = value else { return false };
    let Some(DocumentValue::Array(items)) = fields.get("participants") else { return false };
    if fields.len() != 2
        || integer(fields, "version") != Some(identity.decision_epoch)
        || items.len() != identity.participants.len()
    {
        return false;
    }
    let mut seen = std::collections::BTreeSet::new();
    items.iter().all(|item| {
        let DocumentValue::Object(item) = item else { return false };
        let Some(DocumentValue::String(name)) = item.get("name") else { return false };
        item.len() == 2
            && seen.insert(name)
            && identity.participants.get(name).is_some_and(|baseline| {
                integer(item, "sequence").is_some_and(|sequence| sequence >= *baseline)
            })
    })
}

impl Engine {
    fn cross_file_publication_dot(
        &self,
        scope: &TenantScope,
        sequence: u64,
        operation: &OperationId,
    ) -> Result<(String, u64), EngineError> {
        let rows = self
            .lock_storage()?
            .port
            .replication_publications(
                scope.tenant(),
                sequence.checked_sub(1).ok_or(EngineError::Conflict)?,
                1,
            )
            .map_err(|()| EngineError::StorageFailure)?;
        let Some((stored_sequence, publication)) = rows.into_iter().next() else {
            return Err(EngineError::StorageFailure);
        };
        if stored_sequence != sequence
            || publication.commit_ulid != operation.as_str()
            || publication.origin != self.replication_origin()?
            || publication.origin_sequence == 0
        {
            return Err(EngineError::Conflict);
        }
        Ok((publication.origin, publication.origin_sequence))
    }

    fn clear_cross_file_finished_locked(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
    ) -> Result<(), EngineError> {
        let read_admission =
            self.cross_file_snapshot_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let _ = &read_admission;
        let (completed, _, _) =
            self.cross_file_completion(scope, &identity.operation)?.ok_or(EngineError::Conflict)?;
        if completed != *identity {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot_unfenced(scope)?,
            maintenance_operation(identity, "finished")?,
        )?;
        let Some(finishing) = tx.read_private_metadata(finishing_key())? else { return Ok(()) };
        let finishing = decode(finishing)?;
        if finishing.identity != *identity
            || finishing.decision != Some(CrossFileDecision::Committed)
        {
            return Err(EngineError::Conflict);
        }
        tx.internal_maintenance = true;
        tx.cross_file_control = Some(identity.operation.clone());
        tx.delete_private_metadata(finishing_key())?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }
    #[cfg(test)]
    pub(crate) fn interrupt_cross_file_after_installs(&self, count: usize) {
        self.cross_file_interrupt_after.store(count, std::sync::atomic::Ordering::SeqCst);
    }
    fn clear_cross_file_abort_locked(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
    ) -> Result<(), EngineError> {
        let read_admission =
            self.cross_file_snapshot_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let _ = &read_admission;
        let record = self
            .read_cross_file_decision(scope, &identity.operation)?
            .ok_or(EngineError::Conflict)?;
        if record.identity != *identity || record.decision != Some(CrossFileDecision::Aborted) {
            return Err(EngineError::Conflict);
        }
        let Some(active) = self.read_cross_file_active(scope)? else { return Ok(()) };
        if active != record {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot_unfenced(scope)?,
            maintenance_operation(identity, "abort-complete")?,
        )?;
        tx.internal_maintenance = true;
        tx.cross_file_control = Some(identity.operation.clone());
        tx.delete_private_metadata(active_key())?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {
                self.cross_file_publications
                    .lock()
                    .map_err(|_| EngineError::StorageFailure)?
                    .remove(&(scope.tenant().to_string(), identity.operation.as_str().to_owned()));
                Ok(())
            }
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }

    pub(crate) fn recover_cross_file(
        &self,
        scope: &TenantScope,
        participants: &BTreeMap<u64, std::sync::Arc<Engine>>,
        route_generation: u64,
    ) -> Result<(), EngineError> {
        let admission =
            self.origin_commit_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let Some(active) = self.read_cross_file_active(scope)? else { return Ok(()) };
        let identity = &active.identity;
        if identity.coordinator_origin != self.replication_origin()? {
            // A follower has no local participant prepares to drive. Keep its
            // durable fence intact while allowing the registry/receiver to open
            // and apply the causally ordered remainder of this operation.
            return Ok(());
        }
        if identity.tenant != scope.tenant().as_str()
            || identity.incarnation != u128::from_be_bytes(self.database_id().as_bytes())
            || identity.route_generation != route_generation
            || identity.participants.len() != participants.len()
            || self.origin_epoch()? != (identity.authority_epoch, false)
        {
            return Err(EngineError::Conflict);
        }
        let record = self
            .read_cross_file_decision(scope, &identity.operation)?
            .ok_or(EngineError::Conflict)?;
        if record.identity != *identity {
            return Err(EngineError::Conflict);
        }
        let mut prepared = Vec::new();
        for (shard, participant) in participants {
            let snapshot = identity
                .participants
                .get(&format!("shard-{shard}"))
                .ok_or(EngineError::Conflict)?;
            let participant_identity = participant_identity(
                participant,
                scope.tenant(),
                identity,
                *shard,
                crate::types::StorageSequence::new(*snapshot),
            );
            let intent = participant
                .lock_storage()?
                .port
                .resolve_cross_file_intent(&participant_identity)
                .map_err(|()| EngineError::StorageFailure)?;
            if let Some(intent) = intent {
                prepared.push((participant.clone(), intent));
            } else if record.decision == Some(CrossFileDecision::Committed) {
                return Err(EngineError::StorageFailure);
            }
        }
        if record.decision != Some(CrossFileDecision::Committed) {
            if record.decision.is_none() {
                self.decide_cross_file_locked(scope, identity, CrossFileDecision::Aborted)?;
            }
            for (participant, intent) in prepared {
                participant
                    .lock_storage()?
                    .port
                    .abort_cross_file_intent(&intent.identity)
                    .map_err(|()| EngineError::StorageFailure)?;
            }
            self.clear_cross_file_abort_locked(scope, identity)?;
            return Ok(());
        }
        let (_, publications) =
            self.install_cross_file_prepared_locked(scope, identity, &prepared)?;
        drop(admission);
        for (participant, event) in publications {
            participant.emit_publication_event(&event);
        }
        Ok(())
    }

    fn install_cross_file_prepared_locked(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
        prepared: &[(std::sync::Arc<Engine>, crate::storage::PreparedCrossFileIntent)],
    ) -> CrossFileInstallResult {
        let mut installed = identity.participants.clone();
        let mut publications = Vec::new();
        let mut dependencies = BTreeMap::new();
        let completed = self.cross_file_completion(scope, &identity.operation)?;
        if completed.as_ref().is_some_and(|(completed, _, _)| completed != identity) {
            return Err(EngineError::Conflict);
        }
        if prepared.len() != installed.len() {
            return Err(EngineError::Conflict);
        }
        for (engine, intent) in prepared {
            if intent.identity.operation != identity.operation
                || intent.identity.digest != identity.write_digest
                || intent.identity.database_incarnation != identity.incarnation
                || intent.identity.tenant != *scope.tenant()
                || intent.identity.epoch != identity.authority_epoch
                || installed.get(&intent.identity.participant)
                    != Some(&intent.identity.snapshot.get())
            {
                return Err(EngineError::Conflict);
            }
            if completed.is_some() || intent.intent.is_none() {
                continue;
            }
            let result = engine.lock_storage()?.port.install_cross_file_intent(intent);
            match result {
                crate::storage::CommitResult::Committed(receipt) => {
                    let (origin, sequence) = engine.cross_file_publication_dot(
                        scope,
                        receipt.sequence.get(),
                        &identity.operation,
                    )?;
                    dependencies.insert(origin, sequence);
                    installed.insert(intent.identity.participant.clone(), receipt.sequence.get());
                    publications.push((
                        engine.clone(),
                        super::CommitPublicationEvent::new(
                            identity.operation.clone(),
                            receipt.root,
                            receipt.sequence,
                        ),
                    ));
                    #[cfg(test)]
                    if self
                        .cross_file_interrupt_after
                        .compare_exchange(
                            publications.len(),
                            usize::MAX,
                            std::sync::atomic::Ordering::SeqCst,
                            std::sync::atomic::Ordering::SeqCst,
                        )
                        .is_ok()
                    {
                        return Err(EngineError::StorageFailure);
                    }
                }
                crate::storage::CommitResult::Rejected(_)
                | crate::storage::CommitResult::Unknown(_) => {
                    return Err(EngineError::StorageFailure);
                }
            }
        }
        if completed.is_none() {
            self.advance_cross_file_visible_locked(scope, identity, installed, Some(dependencies))?;
        }
        for (engine, intent) in prepared {
            engine
                .lock_storage()?
                .port
                .finish_cross_file_intent(&intent.identity)
                .map_err(|()| EngineError::StorageFailure)?;
        }
        self.clear_cross_file_finished_locked(scope, identity)?;
        let (_, _, receipt) = self
            .cross_file_completion(scope, &identity.operation)?
            .ok_or(EngineError::StorageFailure)?;
        if let Some(event) = self
            .cross_file_publications
            .lock()
            .map_err(|_| EngineError::StorageFailure)?
            .remove(&(scope.tenant().to_string(), identity.operation.as_str().to_owned()))
        {
            let root = prepared
                .iter()
                .find(|(engine, _)| std::ptr::eq(engine.as_ref(), self))
                .ok_or(EngineError::Conflict)?
                .0
                .clone();
            publications.push((root, event));
        }
        Ok((receipt, publications))
    }

    pub(crate) fn cross_file_installed_receipt(
        &self,
        scope: &TenantScope,
        operation: &OperationId,
        installed_sequence: u64,
    ) -> Result<super::CommitReceipt, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let Some(crate::storage::OperationResolution::Committed { sequence, durability }) =
            storage.port.resolve_operation(scope.tenant(), operation)
        else {
            return Err(EngineError::StorageFailure);
        };
        if sequence.get() != installed_sequence {
            return Err(EngineError::Conflict);
        }
        Ok(super::CommitReceipt::new(operation.clone(), sequence, durability))
    }

    pub(crate) fn cross_file_completion(
        &self,
        scope: &TenantScope,
        operation: &OperationId,
    ) -> Result<Option<(CrossFileIdentity, VisibleFrontier, super::CommitReceipt)>, EngineError>
    {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let Some(value) = storage
            .port
            .private_metadata_at(
                scope.tenant(),
                &complete_key(operation),
                storage.port.published_state().0,
            )
            .map_err(|()| EngineError::StorageFailure)?
        else {
            return Ok(None);
        };
        let DocumentValue::Object(mut fields) = value else {
            return Err(EngineError::InvalidDocument);
        };
        if fields.len() != 2 {
            return Err(EngineError::InvalidDocument);
        }
        let record = decode(fields.remove("identity").ok_or(EngineError::InvalidDocument)?)?;
        if record.identity.operation != *operation
            || record.identity.tenant != scope.tenant().as_str()
            || record.identity.incarnation != u128::from_be_bytes(self.database_id().as_bytes())
            || record.decision != Some(CrossFileDecision::Committed)
        {
            return Err(EngineError::Conflict);
        }
        let DocumentValue::Object(fields) =
            fields.remove("visible").ok_or(EngineError::InvalidDocument)?
        else {
            return Err(EngineError::InvalidDocument);
        };
        if fields.len() != 2 {
            return Err(EngineError::InvalidDocument);
        }
        let version = integer(&fields, "version").ok_or(EngineError::InvalidDocument)?;
        let Some(DocumentValue::Array(items)) = fields.get("participants") else {
            return Err(EngineError::InvalidDocument);
        };
        let mut participants = BTreeMap::new();
        for item in items {
            let DocumentValue::Object(fields) = item else {
                return Err(EngineError::InvalidDocument);
            };
            if fields.len() != 2 {
                return Err(EngineError::InvalidDocument);
            }
            let Some(DocumentValue::String(name)) = fields.get("name") else {
                return Err(EngineError::InvalidDocument);
            };
            let sequence = integer(fields, "sequence").ok_or(EngineError::InvalidDocument)?;
            if participants.insert(name.clone(), sequence).is_some() {
                return Err(EngineError::InvalidDocument);
            }
        }
        let frontier = VisibleFrontier { version, participants };
        if !frontier.validate()
            || version != record.identity.decision_epoch
            || frontier.participants.len() != record.identity.participants.len()
            || record.identity.participants.iter().any(|(name, baseline)| {
                frontier.participants.get(name).is_none_or(|sequence| sequence < baseline)
            })
        {
            return Err(EngineError::Conflict);
        }
        let visible_operation = maintenance_operation(&record.identity, "visible")?;
        let Some(crate::storage::OperationResolution::Committed { sequence, durability }) =
            storage.port.resolve_operation(scope.tenant(), &visible_operation)
        else {
            return Err(EngineError::StorageFailure);
        };
        Ok(Some((
            record.identity,
            frontier,
            super::CommitReceipt::new(operation.clone(), sequence, durability),
        )))
    }

    pub(crate) fn cross_file_request(
        &self,
        scope: &TenantScope,
        operation: &OperationId,
    ) -> Result<Option<DocumentValue>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        storage
            .port
            .private_metadata_at(
                scope.tenant(),
                &request_key(operation),
                storage.port.published_state().0,
            )
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Advances the local DB-visible frontier after every named participant
    /// has reported its installed snapshot. This method performs no RF2 or
    /// Global finality claim; those proofs remain cluster-owned.
    #[cfg(test)]
    pub(crate) fn advance_cross_file_visible(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
        installed: BTreeMap<String, u64>,
    ) -> Result<VisibleFrontier, EngineError> {
        let _barrier =
            self.origin_commit_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let result = self.advance_cross_file_visible_locked(scope, identity, installed, None)?;
        self.clear_cross_file_finished_locked(scope, identity)?;
        Ok(result)
    }

    fn advance_cross_file_visible_locked(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
        installed: BTreeMap<String, u64>,
        dependencies: Option<BTreeMap<String, u64>>,
    ) -> Result<VisibleFrontier, EngineError> {
        let read_admission =
            self.cross_file_snapshot_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let _ = &read_admission;
        if let Some((existing, frontier, _)) =
            self.cross_file_completion(scope, &identity.operation)?
        {
            return if existing == *identity && frontier.participants == installed {
                Ok(frontier)
            } else {
                Err(EngineError::Conflict)
            };
        }
        if !identity.validate()
            || identity.tenant != scope.tenant().as_str()
            || installed.len() != identity.participants.len()
            || identity.participants.iter().any(|(name, baseline)| {
                installed.get(name).is_none_or(|sequence| sequence < baseline)
            })
        {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot_unfenced(scope)?,
            maintenance_operation(identity, "visible")?,
        )?;
        let record = tx
            .read_private_metadata(&key(&identity.operation))?
            .ok_or(EngineError::Conflict)
            .and_then(decode)?;
        if record.identity != *identity || record.decision != Some(CrossFileDecision::Committed) {
            return Err(EngineError::Conflict);
        }
        let active = tx.read_private_metadata(active_key())?.map(decode).transpose()?;
        if active.as_ref().is_none_or(|active| {
            active.identity != *identity || active.decision != Some(CrossFileDecision::Committed)
        }) {
            return Err(EngineError::Conflict);
        }
        let version = identity.decision_epoch;
        let frontier = VisibleFrontier { version, participants: installed };
        if !frontier.validate() {
            return Err(EngineError::InvalidDocument);
        }
        tx.internal_maintenance = true;
        tx.cross_file_control = Some(identity.operation.clone());
        let fields = frontier
            .participants
            .iter()
            .map(|(name, seq)| {
                Ok(DocumentValue::Object(BTreeMap::from([
                    (String::from("name"), DocumentValue::String(name.clone())),
                    (
                        String::from("sequence"),
                        DocumentValue::Integer(
                            i64::try_from(*seq).map_err(|_| EngineError::InvalidDocument)?,
                        ),
                    ),
                ])))
            })
            .collect::<Result<Vec<_>, EngineError>>()?;
        let visible = DocumentValue::Object(BTreeMap::from([
            (
                String::from("version"),
                DocumentValue::Integer(
                    i64::try_from(version).map_err(|_| EngineError::InvalidDocument)?,
                ),
            ),
            (String::from("participants"), DocumentValue::Array(fields)),
        ]));
        tx.put_private_metadata(visible_key(), visible.clone())?;
        tx.cross_file_dependencies = dependencies.map(|frontier| {
            super::merge::encode_context(&super::merge::DocumentContext::from_frontier(frontier))
        });
        // Completion retains the exact identity and original request binding
        // in its own publication for receipt replay.
        tx.put_private_metadata(&key(&identity.operation), encode_phase(&record, "commit")?)?;
        if let Some(request) = tx.read_private_metadata(&request_key(&identity.operation))? {
            tx.put_private_metadata(&request_key(&identity.operation), request)?;
        }
        tx.put_private_metadata(
            &complete_key(&identity.operation),
            DocumentValue::Object(BTreeMap::from([
                ("identity".into(), encode_phase(&record, "commit")?),
                ("visible".into(), visible),
            ])),
        )?;
        tx.put_private_metadata(finishing_key(), encode_phase(&record, "commit")?)?;
        tx.delete_private_metadata(active_key())?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(frontier),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }
    /// Reads a durable cross-file prepare record for one operation.
    pub(crate) fn read_cross_file_decision(
        &self,
        scope: &TenantScope,
        operation: &OperationId,
    ) -> Result<Option<CrossFileRecord>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        storage
            .port
            .private_metadata_at(scope.tenant(), &key(operation), storage.port.published_state().0)
            .map_err(|()| EngineError::StorageFailure)?
            .map(decode)
            .transpose()
    }

    /// Returns the durable cross-file operation currently fencing this
    /// logical database, if any. Absence is the normal state for databases
    /// that have never run a cross-file operation.
    pub(crate) fn read_cross_file_active(
        &self,
        scope: &TenantScope,
    ) -> Result<Option<CrossFileRecord>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let active = storage
            .port
            .private_metadata_at(scope.tenant(), active_key(), storage.port.published_state().0)
            .map_err(|()| EngineError::StorageFailure)?;
        let active = match active {
            Some(active) => Some(active),
            None => storage
                .port
                .private_metadata_at(
                    scope.tenant(),
                    finishing_key(),
                    storage.port.published_state().0,
                )
                .map_err(|()| EngineError::StorageFailure)?,
        };
        active.map(decode).transpose()
    }

    /// Creates an immutable prepare record with exact-identity replay.
    #[cfg(test)]
    pub(crate) fn prepare_cross_file(
        &self,
        scope: &TenantScope,
        identity: CrossFileIdentity,
    ) -> Result<(CrossFileIdentity, LocalCrossFileReceipt), EngineError> {
        self.prepare_cross_file_request(scope, identity, None)
    }

    #[cfg(test)]
    pub(crate) fn prepare_cross_file_request(
        &self,
        scope: &TenantScope,
        identity: CrossFileIdentity,
        request: Option<DocumentValue>,
    ) -> Result<(CrossFileIdentity, LocalCrossFileReceipt), EngineError> {
        let _barrier =
            self.origin_commit_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        self.prepare_cross_file_request_locked(scope, identity, request)
    }

    fn prepare_cross_file_request_locked(
        &self,
        scope: &TenantScope,
        identity: CrossFileIdentity,
        request: Option<DocumentValue>,
    ) -> Result<(CrossFileIdentity, LocalCrossFileReceipt), EngineError> {
        let read_admission =
            self.cross_file_snapshot_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let _ = &read_admission;
        if !identity.validate() {
            return Err(EngineError::InvalidDocument);
        }
        if identity.tenant != scope.tenant().as_str()
            || identity.coordinator_origin != self.replication_origin()?
        {
            return Err(EngineError::Conflict);
        }
        if identity.incarnation != u128::from_be_bytes(self.database_id().as_bytes().to_owned()) {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot_unfenced(scope)?,
            maintenance_operation(&identity, "prepare")?,
        )?;
        if let Some(existing) = tx.read_private_metadata(&key(&identity.operation))? {
            let existing = decode(existing)?;
            if existing.identity != identity {
                return Err(EngineError::Conflict);
            }
            let maintenance = maintenance_operation(&identity, "prepare")?;
            let receipt = match self.resolve_commit_unfenced(scope, maintenance)? {
                super::DurableResolution::Committed(receipt) => LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Prepare,
                },
                super::DurableResolution::Pending => return Err(EngineError::StorageFailure),
                super::DurableResolution::Rejected(_) => return Err(EngineError::Conflict),
            };
            return Ok((identity, receipt));
        }
        // One marker is the database admission fence. Never overwrite another
        // operation's fence, even when that operation has a durable decision:
        // a committed decision may still have uninstalled participants.
        if tx.read_private_metadata(active_key())?.is_some()
            || tx.read_private_metadata(finishing_key())?.is_some()
        {
            return Err(EngineError::Conflict);
        }
        tx.internal_maintenance = true;
        tx.put_private_metadata(
            &key(&identity.operation),
            encode(&CrossFileRecord { identity: identity.clone(), decision: None })?,
        )?;
        tx.cross_file_control = Some(identity.operation.clone());
        tx.put_private_metadata(
            active_key(),
            encode(&CrossFileRecord { identity: identity.clone(), decision: None })?,
        )?;
        if let Some(request) = request {
            tx.put_private_metadata(&request_key(&identity.operation), request)?;
        }
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok((
                identity.clone(),
                LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Prepare,
                },
            )),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }

    /// Durably decides a prepared operation exactly once.
    #[cfg(test)]
    pub(crate) fn decide_cross_file(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
        decision: CrossFileDecision,
    ) -> Result<(CrossFileRecord, LocalCrossFileReceipt), EngineError> {
        let _barrier =
            self.origin_commit_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let result = self.decide_cross_file_locked(scope, identity, decision)?;
        if decision == CrossFileDecision::Aborted {
            self.clear_cross_file_abort_locked(scope, identity)?;
        }
        Ok(result)
    }

    fn decide_cross_file_locked(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
        decision: CrossFileDecision,
    ) -> Result<(CrossFileRecord, LocalCrossFileReceipt), EngineError> {
        let read_admission =
            self.cross_file_snapshot_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let _ = &read_admission;
        if !identity.validate() {
            return Err(EngineError::InvalidDocument);
        }
        if identity.tenant != scope.tenant().as_str() {
            return Err(EngineError::Conflict);
        }
        if identity.incarnation != u128::from_be_bytes(self.database_id().as_bytes().to_owned()) {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot_unfenced(scope)?,
            maintenance_operation(identity, "decide")?,
        )?;
        let current =
            tx.read_private_metadata(&key(&identity.operation))?.ok_or(EngineError::Conflict)?;
        let decoded = decode(current.clone())?;
        if decoded.identity != *identity {
            return Err(EngineError::Conflict);
        }
        if let Some(existing) = decoded.decision {
            if existing != decision {
                return Err(EngineError::Conflict);
            }
            let maintenance = maintenance_operation(identity, "decide")?;
            let receipt = match self.resolve_commit_unfenced(scope, maintenance)? {
                super::DurableResolution::Committed(receipt) => LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Decision,
                },
                super::DurableResolution::Pending => return Err(EngineError::StorageFailure),
                super::DurableResolution::Rejected(_) => return Err(EngineError::Conflict),
            };
            return Ok((decoded, receipt));
        }
        let active = tx.read_private_metadata(active_key())?.map(decode).transpose()?;
        if active.as_ref().is_none_or(|active| active.identity != *identity) {
            return Err(EngineError::Conflict);
        }
        tx.internal_maintenance = true;
        let phase = match decision {
            CrossFileDecision::Committed => "commit",
            CrossFileDecision::Aborted => "abort",
        };
        tx.cross_file_control = Some(identity.operation.clone());
        tx.put_private_metadata(
            &key(&identity.operation),
            encode_phase(
                &CrossFileRecord { identity: identity.clone(), decision: Some(decision) },
                phase,
            )?,
        )?;
        tx.put_private_metadata(
            active_key(),
            encode_phase(
                &CrossFileRecord { identity: identity.clone(), decision: Some(decision) },
                phase,
            )?,
        )?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok((
                CrossFileRecord { identity: identity.clone(), decision: Some(decision) },
                LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Decision,
                },
            )),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }
}

#[cfg(test)]
mod control_validation_tests {
    use super::*;

    #[test]
    fn control_bypass_requires_body_identity_and_terminal_clear() {
        let tenant = crate::types::TenantId::try_new("tenant").unwrap();
        let operation = OperationId::try_new("logical-control").unwrap();
        let mut record = CrossFileRecord {
            identity: CrossFileIdentity {
                tenant: tenant.to_string(),
                coordinator_origin: "root-origin".into(),
                incarnation: 1,
                authority_epoch: 1,
                route_generation: 1,
                read_frontier: 1,
                write_digest: [7; 32],
                participants: BTreeMap::from([("shard-1".into(), 1)]),
                operation: operation.clone(),
                decision_epoch: 2,
            },
            decision: None,
        };
        let body = encode(&record).unwrap();
        let mut writes = vec![
            crate::storage::PrivateMetadataWrite {
                key: key(&operation),
                value: Some(body.clone()),
            },
            crate::storage::PrivateMetadataWrite {
                key: active_key().into(),
                value: Some(body.clone()),
            },
        ];
        assert!(validate_control_metadata(&tenant, &operation, &writes, None, None));
        record.identity.tenant = "foreign".into();
        let foreign = encode(&record).unwrap();
        writes[0].value = Some(foreign.clone());
        writes[1].value = Some(foreign);
        assert!(!validate_control_metadata(&tenant, &operation, &writes, None, None));
        let clear =
            [crate::storage::PrivateMetadataWrite { key: active_key().into(), value: None }];
        assert!(!validate_control_metadata(&tenant, &operation, &clear, None, None));
        assert!(!validate_control_metadata(&tenant, &operation, &clear, None, Some(&body)));
        record.identity.tenant = tenant.to_string();
        record.decision = Some(CrossFileDecision::Aborted);
        let aborted = encode_phase(&record, "abort").unwrap();
        assert!(validate_control_metadata(&tenant, &operation, &clear, None, Some(&aborted)));
    }
}
