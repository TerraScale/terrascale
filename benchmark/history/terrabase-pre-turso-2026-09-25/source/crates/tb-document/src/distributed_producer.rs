//! Certified local source events for the distributed document shard.

use std::collections::BTreeMap;

use sha2::{Digest, Sha256};
use tb_core::{
    Attribution, CommitReceipt, CommitSeq, Key, MemoryCategory, OriginId, ReadSnapshot,
    Reservation, TransactionId,
};
use tb_format::{
    DocumentBinding, DocumentEventId, DocumentKind, StatePointer, WalOperation, decode_catalog,
    decode_change, decode_document, decode_retry_value, encode_causal_manifest_key,
    encode_change_into, encode_change_key, encode_document_into, encode_high_water_key,
    encode_high_water_value, encode_marker_key, encode_primary_key, encode_retry_key,
    encode_retry_value, encode_state_pointer, encode_wal_frame_into, validate_document_schema,
};
use tb_replication::{
    CausalContext, CollectionBinding, EventId, EventMetadata, MetadataChange, MetadataHeader,
    WalRole, compute_event_digest, decode_metadata, encode_metadata_into, verify_incoming,
};
use tb_storage::{
    CommittedBatch, Mutation, PlannedBatch, PreparedBatchOutcome, StageWorkPlan,
    StageWorkReservation, StorageError,
};

use crate::distributed::{DistributedDocumentStore, DistributedWriterState};
use crate::distributed_state::{effective_limits, load_manifest, merge_manifest};
use crate::{DocumentError, DocumentWrite, DocumentWriteValue, key};

/// One durably committed distributed source event and its exact resend bytes.
pub struct DistributedDocumentCommit {
    pub receipt: CommitReceipt,
    pub event: DocumentEventId,
    pub event_digest: [u8; 32],
    metadata: Vec<u8>,
    frame: CommittedBatch,
    _metadata_permit: Reservation,
}

impl DistributedDocumentCommit {
    #[must_use]
    pub fn metadata(&self) -> &[u8] {
        &self.metadata
    }

    #[must_use]
    pub fn wal_frame(&self) -> &[u8] {
        self.frame.encoded_bytes()
    }
}

/// One complete retained source event ready to resend, excluding local D6 stages.
pub struct DistributedSourceFrame {
    pub event: DocumentEventId,
    pub event_digest: [u8; 32],
    metadata: Vec<u8>,
    frame: CommittedBatch,
    _metadata_permit: Reservation,
}

impl DistributedSourceFrame {
    #[must_use]
    pub fn metadata(&self) -> &[u8] {
        &self.metadata
    }

    #[must_use]
    pub fn wal_frame(&self) -> &[u8] {
        self.frame.encoded_bytes()
    }
}

struct PreparedRow {
    binding: DocumentBinding,
    observed: CausalContext,
    source: Mutation,
    pointer: Mutation,
    manifest_sha256: [u8; 32],
    stage_missing: bool,
}

struct StageGroup {
    indexes: Vec<usize>,
    token: TransactionId,
    fingerprint: [u8; 32],
}

struct PreparedSourceEvent {
    rows: Vec<PreparedRow>,
    changes: Vec<MetadataChange>,
    bindings: Vec<CollectionBinding>,
    stage_plans: Vec<PlannedBatch>,
    stage_groups: Vec<StageGroup>,
}

struct PreparedWrite {
    row: PreparedRow,
    signed_binding: CollectionBinding,
    change: MetadataChange,
    stage: Option<Mutation>,
}

struct StageBuilder {
    candidate: Vec<Mutation>,
    indexes: Vec<usize>,
    plans: Vec<PlannedBatch>,
    groups: Vec<StageGroup>,
}

struct SourceCommitPlan {
    event: DocumentEventId,
    event_id: EventId,
    metadata: Vec<u8>,
    metadata_permit: Reservation,
    mutations: Vec<Mutation>,
    prepared: PreparedSourceEvent,
}

fn source_parts(row: &PreparedRow) -> (&[u8], &[u8]) {
    let Mutation::Put { key, value } = &row.source else {
        unreachable!("source is Put")
    };
    (key.as_bytes(), value)
}

fn encode_source_row(
    write: DocumentWrite<'_>,
    event: DocumentEventId,
    ordinal: u16,
    observed: CausalContext,
    kind: DocumentKind,
    document_bytes: &[u8],
) -> Result<PreparedRow, DocumentError> {
    let source_key = key(encode_change_key(
        write.binding.collection,
        write.binding.document,
        event.origin,
        event.sequence,
        ordinal,
    )?)?;
    let mut source_value = Vec::new();
    encode_change_into(
        &mut source_value,
        event,
        write.binding,
        kind,
        document_bytes,
    )?;
    Ok(PreparedRow {
        binding: write.binding,
        observed,
        source: Mutation::Put {
            key: source_key,
            value: source_value,
        },
        pointer: Mutation::Put {
            key: key(encode_primary_key(
                write.binding.collection,
                write.binding.document,
            ))?,
            value: Vec::new(),
        },
        manifest_sha256: [0; 32],
        stage_missing: false,
    })
}

fn check_retry_writes(
    writes: &[DocumentWrite<'_>],
    metadata: &EventMetadata,
    mutations: &[Mutation],
    event: DocumentEventId,
    limits: tb_replication::ReplicationLimits,
) -> Result<(), DocumentError> {
    for (index, write) in writes.iter().enumerate() {
        let change = &metadata.changes[index];
        if change.collection != write.binding.collection
            || change.document != write.binding.document
            || metadata
                .bindings
                .iter()
                .find(|binding| binding.collection == change.collection)
                .is_none_or(|binding| {
                    binding.generation != write.binding.generation
                        || binding.schema_digest != write.binding.schema_digest
                })
        {
            return Err(DocumentError::Storage(StorageError::IdempotencyConflict));
        }
        let Some(Mutation::Put { value, .. }) =
            mutations.get(usize::from(change.source_wal_ordinal))
        else {
            return Err(DocumentError::Corrupt("retry D1 ordinal"));
        };
        let original = decode_change(value)?;
        let mut intended = Vec::new();
        let kind = match write.value {
            DocumentWriteValue::Live(fields) => {
                encode_document_into(&mut intended, write.binding, fields)?;
                DocumentKind::Live
            }
            DocumentWriteValue::Tombstone => DocumentKind::Tombstone,
        };
        if original.kind != kind || original.document_bytes != intended {
            return Err(DocumentError::Storage(StorageError::IdempotencyConflict));
        }
        let mut observed = BTreeMap::from_iter(write.observed.entries());
        if event.sequence > 1 {
            if observed
                .get(&event.origin)
                .is_some_and(|&n| n > event.sequence - 1)
            {
                return Err(DocumentError::Corrupt("retry causal token exceeds source"));
            }
            observed.insert(event.origin, event.sequence - 1);
        }
        if change.observed != CausalContext::from_entries(observed, limits.max_origins)? {
            return Err(DocumentError::Storage(StorageError::IdempotencyConflict));
        }
    }
    Ok(())
}

fn stage_token(
    final_token: TransactionId,
    event: DocumentEventId,
    fingerprint: [u8; 32],
) -> Result<TransactionId, DocumentError> {
    let mut hash = Sha256::new();
    hash.update(b"TBDOC-D6-STAGE-v1\0");
    hash.update(final_token.to_bytes());
    hash.update(event.database.to_bytes());
    hash.update(event.shard.get().to_le_bytes());
    hash.update(event.origin.replica_id.to_bytes());
    hash.update(event.origin.incarnation);
    hash.update(event.sequence.to_le_bytes());
    hash.update(fingerprint);
    let digest: [u8; 32] = hash.finalize().into();
    let mut nonce = [0; 16];
    nonce.copy_from_slice(&digest[..16]);
    let token = TransactionId::new(final_token.expires_at_unix_ms(), nonce);
    if token == final_token {
        return Err(DocumentError::Capacity("stage token collision"));
    }
    Ok(token)
}

fn mutation_fingerprint(mutations: &[Mutation]) -> [u8; 32] {
    let mut hash = Sha256::new();
    hash.update(b"TBDOC-D6-GROUP-v1\0");
    for mutation in mutations {
        let Mutation::Put { key, value } = mutation else {
            unreachable!("stage is Put")
        };
        hash.update((key.as_bytes().len() as u64).to_le_bytes());
        hash.update(key.as_bytes());
        hash.update((value.len() as u64).to_le_bytes());
        hash.update(value);
    }
    hash.finalize().into()
}

impl DistributedDocumentStore {
    /// Return the audited local source event sequence, excluding catalog and receiver commits.
    ///
    /// # Errors
    /// Returns `NeedsRecovery` while the writer is poisoned or applied inboxes
    /// have not been reconciled after open.
    pub fn source_high_water(&self) -> Result<u64, DocumentError> {
        let source_state = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if source_state.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        if !source_state.applied_prefix.is_empty() && !source_state.inboxes_reconciled {
            return Err(DocumentError::NeedsRecovery);
        }
        Ok(source_state.local_sequence)
    }

    /// Find the next complete retained source event after one origin sequence.
    ///
    /// # Errors
    /// Rejects any unclassified or unverifiable retained group.
    #[allow(
        clippy::too_many_lines,
        reason = "source scan verifies one retained event and its complete C0, D6, and D5 identity before export"
    )]
    pub fn next_source_after(
        &self,
        after_sequence: u64,
    ) -> Result<Option<DistributedSourceFrame>, DocumentError> {
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        let mut cursor = self.storage.committed_cursor()?;
        let mut after_commit = CommitSeq::ZERO;
        while let Some(batch) =
            cursor.next(after_commit, self.config.storage.format.max_frame_bytes)?
        {
            after_commit = batch.sequence;
            let frame = batch.frame(&self.config.storage.format)?;
            let mut c0 = 0_usize;
            let mut d6 = 0_usize;
            let mut d5 = 0_usize;
            let mut d1 = 0_usize;
            let mut d0 = 0_usize;
            let mut marker = None;
            for operation in frame.operations() {
                let WalOperation::Put { key, value } = operation? else {
                    return Err(DocumentError::Corrupt("Delete in distributed resend WAL"));
                };
                match key.first() {
                    Some(0xC0) => c0 += 1,
                    Some(0xD6) => d6 += 1,
                    Some(0xD5) => d5 += 1,
                    Some(0xD1) => d1 += 1,
                    Some(0xD0) => d0 += 1,
                    Some(0xD2) if marker.replace(value).is_some() => {
                        return Err(DocumentError::Corrupt("multiple resend D2 markers"));
                    }
                    _ => {}
                }
            }
            if c0 == frame.operation_count() || d6 == frame.operation_count() {
                continue;
            }
            if c0 != 0 || d6 != 0 {
                return Err(DocumentError::Corrupt("mixed resend group"));
            }
            if d5 != 0 {
                if d5 != 1
                    || marker.is_some()
                    || d1 == 0
                    || d1 != d0
                    || frame.operation_count() != d1 * 2 + 1
                {
                    return Err(DocumentError::Corrupt("invalid receiver final in resend"));
                }
                // Open audited the full D1/D0/D5 group. Resend is local D2 only.
                continue;
            }
            let bytes = marker.ok_or(DocumentError::Corrupt("source resend marker missing"))?;
            let metadata = decode_metadata(bytes, limits)?;
            if metadata.header.id.origin() != view.local_origin()
                || metadata.header.id.database() != view.database()
                || metadata.header.id.shard() != view.shard()
                || metadata.header.epoch != view.epoch()
                || metadata.header.configuration != view.configuration()
                || metadata.header.record_format != 2
            {
                return Err(DocumentError::Corrupt("source resend scope"));
            }
            if metadata.header.id.sequence() <= after_sequence {
                continue;
            }
            let digest = compute_event_digest(bytes, batch.encoded_bytes())?;
            let proof = verify_incoming(
                bytes,
                batch.encoded_bytes(),
                digest,
                limits,
                &self.budget,
                Attribution {
                    tenant: self.config.storage.tenant,
                    worker: self.config.storage.worker,
                    category: MemoryCategory::Replication,
                },
            )?;
            if proof.event().id() != metadata.header.id {
                return Err(DocumentError::Corrupt("source resend proof identity"));
            }
            let permit = self.budget.try_reserve(
                Attribution {
                    tenant: self.config.storage.tenant,
                    worker: self.config.storage.worker,
                    category: MemoryCategory::WriteBuffer,
                },
                bytes.len(),
            )?;
            let event = DocumentEventId {
                database: view.database(),
                shard: view.shard(),
                origin: view.local_origin(),
                sequence: metadata.header.id.sequence(),
            };
            return Ok(Some(DistributedSourceFrame {
                event,
                event_digest: digest.to_bytes(),
                metadata: bytes.to_vec(),
                frame: batch,
                _metadata_permit: permit,
            }));
        }
        Ok(None)
    }

    /// Commit sorted source changes under the certified active schemas.
    ///
    /// # Errors
    /// Rejects stale schemas, unknown causal claims, capacity failures, and
    /// uncertain durability. An uncertain final write requires reopen.
    pub fn commit_documents(
        &self,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<DistributedDocumentCommit, DocumentError> {
        let mut source_state = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if source_state.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        if !source_state.applied_prefix.is_empty() && !source_state.inboxes_reconciled {
            return Err(DocumentError::NeedsRecovery);
        }
        let limits = effective_limits(self.config.replication);
        let _work_permit = self.reserve_source_work(writes, limits)?;
        let snapshot = self.storage.snapshot();
        if let Some(retry) = self.resolve_retry(snapshot, transaction_id, writes)? {
            return Ok(retry);
        }
        let sequence =
            source_state
                .local_sequence
                .checked_add(1)
                .ok_or(DocumentError::Capacity(
                    "distributed source sequence exhausted",
                ))?;
        let view = self.authority.view();
        let event = DocumentEventId {
            database: view.database(),
            shard: view.shard(),
            origin: view.local_origin(),
            sequence,
        };
        let event_id = EventId::new(event.database, event.shard, event.origin, sequence)?;
        let prepared =
            self.prepare_source_event(snapshot, transaction_id, event, writes, limits)?;
        let plan = self.build_source_plan(transaction_id, event, event_id, prepared, limits)?;
        self.commit_source_plan(snapshot, transaction_id, plan, &mut source_state)
    }

    fn reserve_source_work(
        &self,
        writes: &[DocumentWrite<'_>],
        limits: tb_replication::ReplicationLimits,
    ) -> Result<Reservation, DocumentError> {
        if writes.is_empty()
            || writes.len() > limits.max_changes
            || writes
                .len()
                .checked_mul(2)
                .and_then(|n| n.checked_add(3))
                .is_none_or(|n| n > self.config.storage.format.max_operations)
        {
            return Err(DocumentError::Capacity(
                "distributed change or final operation count",
            ));
        }
        let mut previous = None;
        for write in writes {
            let pair = (write.binding.collection, write.binding.document);
            if previous.is_some_and(|old| old >= pair) {
                return Err(DocumentError::Corrupt("changes not strictly sorted"));
            }
            previous = Some(pair);
        }
        let work_bytes = writes
            .iter()
            .try_fold(0_usize, |total, write| {
                let fields = match write.value {
                    DocumentWriteValue::Live(fields) => fields
                        .iter()
                        .try_fold(0_usize, |bytes, field| bytes.checked_add(field.value.len()))?,
                    DocumentWriteValue::Tombstone => 0,
                };
                total.checked_add(fields.checked_add(512)?.checked_mul(4)?)
            })
            .and_then(|bytes| bytes.checked_add(tb_core::MAX_EVENT_METADATA_BYTES))
            .and_then(|bytes| {
                bytes.checked_add(self.config.storage.format.max_frame_bytes.checked_mul(2)?)
            })
            .ok_or(DocumentError::Capacity("distributed write work bytes"))?;
        Ok(self.budget.try_reserve(
            Attribution {
                tenant: self.config.storage.tenant,
                worker: self.config.storage.worker,
                category: MemoryCategory::WriteBuffer,
            },
            work_bytes,
        )?)
    }

    fn prepare_source_event(
        &self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        event: DocumentEventId,
        writes: &[DocumentWrite<'_>],
        limits: tb_replication::ReplicationLimits,
    ) -> Result<PreparedSourceEvent, DocumentError> {
        let mut rows = Vec::new();
        rows.try_reserve_exact(writes.len())
            .map_err(|_| DocumentError::Capacity("source row allocation"))?;
        let mut changes = Vec::new();
        let mut bindings = Vec::new();
        let mut stages = StageBuilder {
            candidate: Vec::new(),
            indexes: Vec::new(),
            plans: Vec::new(),
            groups: Vec::new(),
        };
        let mut payload_bytes = 0_usize;
        for (index, &write) in writes.iter().enumerate() {
            let prepared =
                self.prepare_source_row(snapshot, event, write, index, limits, &mut payload_bytes)?;
            if let Some(stage) = prepared.stage {
                self.append_stage(transaction_id, event, index, stage, &mut stages)?;
            }
            if bindings.last().is_none_or(|last: &CollectionBinding| {
                last.collection != prepared.signed_binding.collection
            }) {
                bindings.push(prepared.signed_binding);
            }
            changes.push(prepared.change);
            rows.push(prepared.row);
        }
        if !stages.candidate.is_empty() {
            self.finish_stage_group(
                transaction_id,
                event,
                &mut stages.candidate,
                &mut stages.indexes,
                &mut stages.plans,
                &mut stages.groups,
            )?;
        }
        Ok(PreparedSourceEvent {
            rows,
            changes,
            bindings,
            stage_plans: stages.plans,
            stage_groups: stages.groups,
        })
    }

    fn prepare_source_row(
        &self,
        snapshot: ReadSnapshot,
        event: DocumentEventId,
        write: DocumentWrite<'_>,
        index: usize,
        limits: tb_replication::ReplicationLimits,
        payload_bytes: &mut usize,
    ) -> Result<PreparedWrite, DocumentError> {
        let view = self.authority.view();
        let (signed_binding, signed_bytes) = view
            .checked_active_catalog(write.binding.collection)
            .map_err(|_| DocumentError::SchemaChanged)?;
        if signed_binding.generation != write.binding.generation
            || signed_binding.schema_digest != write.binding.schema_digest
        {
            return Err(DocumentError::SchemaChanged);
        }
        let catalog = decode_catalog(signed_bytes)?;
        let mut document_bytes = Vec::new();
        let kind = match write.value {
            DocumentWriteValue::Live(fields) => {
                encode_document_into(&mut document_bytes, write.binding, fields)?;
                validate_document_schema(decode_document(&document_bytes)?, &catalog.schema)?;
                DocumentKind::Live
            }
            DocumentWriteValue::Tombstone => DocumentKind::Tombstone,
        };
        if document_bytes.len() > limits.max_value_bytes {
            return Err(DocumentError::Capacity("document value bytes"));
        }
        *payload_bytes = (*payload_bytes)
            .checked_add(document_bytes.len())
            .ok_or(DocumentError::Capacity("event payload bytes"))?;
        if *payload_bytes > limits.max_payload_bytes {
            return Err(DocumentError::Capacity("event payload bytes"));
        }
        let observed = self.validated_observed(snapshot, write, event.sequence)?;
        let ordinal =
            u16::try_from(index).map_err(|_| DocumentError::Capacity("logical ordinal"))?;
        let mut row = encode_source_row(
            write,
            event,
            ordinal,
            observed.clone(),
            kind,
            &document_bytes,
        )?;
        let (manifest, sha) = self.manifest_for(snapshot, &row)?;
        row.manifest_sha256 = sha;
        let (pointer_key, pointer_value) = encode_state_pointer(StatePointer {
            collection: write.binding.collection,
            document: write.binding.document,
            manifest_sha256: sha,
        });
        row.pointer = Mutation::Put {
            key: key(pointer_key)?,
            value: pointer_value.to_vec(),
        };
        let manifest_key = key(encode_causal_manifest_key(
            write.binding.collection,
            write.binding.document,
            sha,
        ))?;
        let stage = match self.storage.get(snapshot, &manifest_key)? {
            Some(value) if value.bytes() == manifest => None,
            Some(_) => return Err(DocumentError::Corrupt("D6 content-address collision")),
            None => {
                row.stage_missing = true;
                Some(Mutation::Put {
                    key: manifest_key,
                    value: manifest,
                })
            }
        };
        Ok(PreparedWrite {
            row,
            signed_binding,
            change: MetadataChange {
                collection: write.binding.collection,
                document: write.binding.document,
                source_wal_ordinal: ordinal,
                kind,
                observed,
            },
            stage,
        })
    }

    fn append_stage(
        &self,
        transaction_id: TransactionId,
        event: DocumentEventId,
        index: usize,
        mutation: Mutation,
        stages: &mut StageBuilder,
    ) -> Result<(), DocumentError> {
        stages.candidate.push(mutation);
        stages.indexes.push(index);
        let token = stage_token(
            transaction_id,
            event,
            mutation_fingerprint(&stages.candidate),
        )?;
        let fits = PlannedBatch::exact(&self.config.storage.format, token, &stages.candidate)
            .is_ok_and(|plan| {
                let footprint = plan.footprint();
                footprint.mutable_bytes <= self.config.storage.max_mutable_bytes
                    && footprint
                        .directory_bytes
                        .checked_add(16)
                        .is_some_and(|n| n <= self.config.storage.format.max_directory_bytes)
            });
        if !fits {
            let last = stages
                .candidate
                .pop()
                .ok_or(DocumentError::Corrupt("empty stage group"))?;
            stages.indexes.pop();
            self.finish_stage_group(
                transaction_id,
                event,
                &mut stages.candidate,
                &mut stages.indexes,
                &mut stages.plans,
                &mut stages.groups,
            )?;
            stages.candidate.push(last);
            stages.indexes.push(index);
            let token = stage_token(
                transaction_id,
                event,
                mutation_fingerprint(&stages.candidate),
            )?;
            PlannedBatch::exact(&self.config.storage.format, token, &stages.candidate)?;
        }
        Ok(())
    }

    fn build_source_plan(
        &self,
        transaction_id: TransactionId,
        event: DocumentEventId,
        event_id: EventId,
        prepared: PreparedSourceEvent,
        limits: tb_replication::ReplicationLimits,
    ) -> Result<SourceCommitPlan, DocumentError> {
        let view = self.authority.view();
        let mut roles = Vec::with_capacity(prepared.rows.len() * 2 + 3);
        roles.extend(std::iter::repeat_n(
            WalRole::SourceChange,
            prepared.rows.len(),
        ));
        roles.extend(std::iter::repeat_n(WalRole::Primary, prepared.rows.len()));
        roles.extend([WalRole::Marker, WalRole::Retry, WalRole::HighWater]);
        let mut metadata = Vec::new();
        encode_metadata_into(
            &mut metadata,
            MetadataHeader {
                id: event_id,
                epoch: view.epoch(),
                configuration: view.configuration(),
                transaction_id,
                record_format: 2,
                merge_policy: 1,
            },
            &prepared.bindings,
            &prepared.changes,
            &roles,
            limits,
        )?;
        let metadata_permit = self.budget.try_reserve(
            Attribution {
                tenant: self.config.storage.tenant,
                worker: self.config.storage.worker,
                category: MemoryCategory::WriteBuffer,
            },
            metadata.len(),
        )?;
        let mut mutations = Vec::with_capacity(prepared.rows.len() * 2 + 3);
        for row in &prepared.rows {
            let Mutation::Put { key, value } = &row.source else {
                unreachable!()
            };
            mutations.push(Mutation::Put {
                key: key.clone(),
                value: value.clone(),
            });
        }
        for row in &prepared.rows {
            let Mutation::Put { key, value } = &row.pointer else {
                unreachable!()
            };
            mutations.push(Mutation::Put {
                key: key.clone(),
                value: value.clone(),
            });
        }
        mutations.push(Mutation::Put {
            key: key(encode_marker_key(event.origin, event.sequence)?)?,
            value: metadata.clone(),
        });
        mutations.push(Mutation::Put {
            key: key(encode_retry_key(transaction_id))?,
            value: encode_retry_value(event)?.to_vec(),
        });
        mutations.push(Mutation::Put {
            key: key(encode_high_water_key(event.origin)?)?,
            value: encode_high_water_value(event.sequence)?.to_vec(),
        });
        Ok(SourceCommitPlan {
            event,
            event_id,
            metadata,
            metadata_permit,
            mutations,
            prepared,
        })
    }

    fn commit_source_plan(
        &self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        mut plan: SourceCommitPlan,
        source_state: &mut DistributedWriterState,
    ) -> Result<DistributedDocumentCommit, DocumentError> {
        let final_plan =
            PlannedBatch::exact(&self.config.storage.format, transaction_id, &plan.mutations)?;
        let mut work = self.storage.reserve_stage_work(StageWorkPlan::new(
            std::mem::take(&mut plan.prepared.stage_plans),
            final_plan,
        ))?;
        self.commit_stage_groups(snapshot, &plan, &mut work, source_state)?;
        self.commit_final_source(transaction_id, plan, work, source_state)
    }

    fn commit_stage_groups(
        &self,
        snapshot: ReadSnapshot,
        plan: &SourceCommitPlan,
        work: &mut StageWorkReservation<'_>,
        source_state: &mut DistributedWriterState,
    ) -> Result<(), DocumentError> {
        for group in &plan.prepared.stage_groups {
            let mut regenerated = Vec::with_capacity(group.indexes.len());
            for &index in &group.indexes {
                let row = &plan.prepared.rows[index];
                let (value, sha) = self.manifest_for(snapshot, row)?;
                if sha != row.manifest_sha256 || !row.stage_missing {
                    source_state.poisoned = true;
                    return Err(DocumentError::Corrupt("D6 regeneration changed"));
                }
                regenerated.push(Mutation::Put {
                    key: key(encode_causal_manifest_key(
                        row.binding.collection,
                        row.binding.document,
                        sha,
                    ))?,
                    value,
                });
            }
            if mutation_fingerprint(&regenerated) != group.fingerprint {
                source_state.poisoned = true;
                return Err(DocumentError::Corrupt("D6 stage group changed"));
            }
            if let Err(error) =
                work.commit_stage(self.storage.snapshot(), group.token, &regenerated)
            {
                if matches!(error, StorageError::Io(_) | StorageError::Poisoned) {
                    source_state.poisoned = true;
                }
                return Err(error.into());
            }
        }
        Ok(())
    }

    fn commit_final_source(
        &self,
        transaction_id: TransactionId,
        plan: SourceCommitPlan,
        work: StageWorkReservation<'_>,
        source_state: &mut DistributedWriterState,
    ) -> Result<DistributedDocumentCommit, DocumentError> {
        let SourceCommitPlan {
            event,
            event_id,
            metadata,
            metadata_permit,
            mutations,
            ..
        } = plan;
        let final_snapshot = self.storage.snapshot();
        let expected_commit_sequence = final_snapshot
            .seq
            .get()
            .checked_add(1)
            .ok_or(DocumentError::Capacity("storage commit sequence exhausted"))?;
        let operations = mutations
            .iter()
            .map(|mutation| match mutation {
                Mutation::Put { key, value } => WalOperation::Put {
                    key: key.as_bytes(),
                    value,
                },
                Mutation::Delete { .. } => unreachable!(),
            })
            .collect::<Vec<_>>();
        let mut expected_frame = Vec::new();
        encode_wal_frame_into(
            &mut expected_frame,
            expected_commit_sequence,
            transaction_id,
            &operations,
            &self.config.storage.format,
        )?;
        let receipt = match work.prepare_final(final_snapshot, transaction_id, &mutations) {
            Ok(PreparedBatchOutcome::Ready(ready)) => match ready.commit() {
                Ok(receipt) => receipt,
                Err(error) => {
                    source_state.poisoned = true;
                    return Err(error.into());
                }
            },
            Ok(PreparedBatchOutcome::AlreadyCommitted(_)) => {
                source_state.poisoned = true;
                return Err(DocumentError::Corrupt("new final event reused transaction"));
            }
            Err(error) => {
                source_state.poisoned = true;
                return Err(error.into());
            }
        };
        source_state.local_sequence = event.sequence;
        let verified = self.verify_committed_source(
            transaction_id,
            event_id,
            &metadata,
            &expected_frame,
            expected_commit_sequence,
            receipt,
        );
        let (frame, event_digest) = match verified {
            Ok(result) => result,
            Err(error) => {
                source_state.poisoned = true;
                return Err(error);
            }
        };
        Ok(DistributedDocumentCommit {
            receipt,
            event,
            event_digest,
            metadata,
            frame,
            _metadata_permit: metadata_permit,
        })
    }

    fn verify_committed_source(
        &self,
        transaction_id: TransactionId,
        event_id: EventId,
        metadata: &[u8],
        expected_frame: &[u8],
        expected_commit_sequence: u64,
        receipt: CommitReceipt,
    ) -> Result<(CommittedBatch, [u8; 32]), DocumentError> {
        if receipt.commit_seq.get() != expected_commit_sequence {
            return Err(DocumentError::Corrupt("source receipt sequence mismatch"));
        }
        let batch = self
            .storage
            .committed_batch_for_transaction(transaction_id)?
            .ok_or(DocumentError::Corrupt("retained source frame missing"))?;
        if batch.sequence != receipt.commit_seq || batch.encoded_bytes() != expected_frame {
            return Err(DocumentError::Corrupt("retained source frame changed"));
        }
        let digest = compute_event_digest(metadata, batch.encoded_bytes())?;
        let proof = verify_incoming(
            metadata,
            batch.encoded_bytes(),
            digest,
            effective_limits(self.config.replication),
            &self.budget,
            Attribution {
                tenant: self.config.storage.tenant,
                worker: self.config.storage.worker,
                category: MemoryCategory::Replication,
            },
        )?;
        if proof.event().id() != event_id {
            return Err(DocumentError::Corrupt(
                "verified source event identity changed",
            ));
        }
        Ok((batch, digest.to_bytes()))
    }

    fn finish_stage_group(
        &self,
        transaction_id: TransactionId,
        event: DocumentEventId,
        candidate: &mut Vec<Mutation>,
        indexes: &mut Vec<usize>,
        plans: &mut Vec<PlannedBatch>,
        groups: &mut Vec<StageGroup>,
    ) -> Result<(), DocumentError> {
        if candidate.is_empty() {
            return Err(DocumentError::Capacity("one D6 exceeds stage limits"));
        }
        let fingerprint = mutation_fingerprint(candidate);
        let token = stage_token(transaction_id, event, fingerprint)?;
        let plan = PlannedBatch::exact(&self.config.storage.format, token, candidate)?;
        let footprint = plan.footprint();
        if footprint.mutable_bytes > self.config.storage.max_mutable_bytes
            || footprint
                .directory_bytes
                .checked_add(16)
                .is_none_or(|n| n > self.config.storage.format.max_directory_bytes)
        {
            return Err(DocumentError::Capacity("D6 stage footprint"));
        }
        groups.push(StageGroup {
            indexes: std::mem::take(indexes),
            token,
            fingerprint,
        });
        plans.push(plan);
        candidate.clear();
        Ok(())
    }

    fn validated_observed(
        &self,
        snapshot: ReadSnapshot,
        write: DocumentWrite<'_>,
        sequence: u64,
    ) -> Result<CausalContext, DocumentError> {
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        let primary_key = key(encode_primary_key(
            write.binding.collection,
            write.binding.document,
        ))?;
        let mut known = BTreeMap::<OriginId, u64>::new();
        if let Some(value) = self.storage.get(snapshot, &primary_key)? {
            let (manifest, _permit) = load_manifest(
                &self.storage,
                &self.budget,
                &self.config.storage,
                view,
                limits,
                snapshot,
                (primary_key.as_bytes(), value.bytes()),
            )?;
            for dot in manifest.context() {
                known.insert(dot.origin, dot.sequence);
            }
            for sibling in manifest.siblings() {
                known
                    .entry(sibling.event.origin)
                    .and_modify(|n| *n = (*n).max(sibling.event.sequence))
                    .or_insert(sibling.event.sequence);
            }
        }
        if sequence > 1 {
            known.insert(view.local_origin(), sequence - 1);
        }
        let mut observed = BTreeMap::new();
        for (origin, prefix) in write.observed.entries() {
            if prefix == 0 || prefix > known.get(&origin).copied().unwrap_or(0) {
                return Err(DocumentError::Corrupt("causal token exceeds known history"));
            }
            observed.insert(origin, prefix);
        }
        if sequence > 1 {
            observed.insert(view.local_origin(), sequence - 1);
        }
        Ok(CausalContext::from_entries(observed, limits.max_origins)?)
    }

    fn manifest_for(
        &self,
        snapshot: ReadSnapshot,
        row: &PreparedRow,
    ) -> Result<(Vec<u8>, [u8; 32]), DocumentError> {
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        let (source_key, source_value) = source_parts(row);
        let (encoded, sha) = merge_manifest(
            &self.storage,
            &self.budget,
            &self.config.storage,
            view,
            limits,
            snapshot,
            row.binding.collection,
            row.binding.document,
            &row.observed,
            source_key,
            source_value,
        )?;
        Ok((encoded, sha))
    }

    fn resolve_retry(
        &self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<Option<DistributedDocumentCommit>, DocumentError> {
        if transaction_id.expires_at_unix_ms() <= self.storage.stats().expired_floor_ms {
            return Err(DocumentError::Storage(StorageError::IdempotencyExpired));
        }
        let retry_key = key(encode_retry_key(transaction_id))?;
        let Some(retry) = self.storage.get(snapshot, &retry_key)? else {
            return Ok(None);
        };
        let event = decode_retry_value(retry.bytes())?;
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        if event.database != view.database()
            || event.shard != view.shard()
            || event.origin != view.local_origin()
        {
            return Err(DocumentError::Corrupt("retry source scope"));
        }
        let marker = self
            .storage
            .get(
                snapshot,
                &key(encode_marker_key(event.origin, event.sequence)?)?,
            )?
            .ok_or(DocumentError::Corrupt("retry D2 missing"))?;
        let metadata = decode_metadata(marker.bytes(), limits)?;
        if metadata.header.transaction_id != transaction_id
            || metadata.header.id
                != EventId::new(event.database, event.shard, event.origin, event.sequence)?
            || metadata.header.record_format != 2
            || metadata.changes.len() != writes.len()
        {
            return Err(DocumentError::Corrupt("retry metadata identity"));
        }
        let frame = self
            .storage
            .committed_batch_for_transaction(transaction_id)?
            .ok_or(DocumentError::Corrupt("retry exact frame missing"))?;
        let digest = compute_event_digest(marker.bytes(), frame.encoded_bytes())?;
        verify_incoming(
            marker.bytes(),
            frame.encoded_bytes(),
            digest,
            limits,
            &self.budget,
            Attribution {
                tenant: self.config.storage.tenant,
                worker: self.config.storage.worker,
                category: MemoryCategory::Replication,
            },
        )?;
        let mut mutations = Vec::new();
        for operation in frame.frame(&self.config.storage.format)?.operations() {
            let WalOperation::Put { key: bytes, value } = operation? else {
                return Err(DocumentError::Corrupt("retry Delete"));
            };
            mutations.push(Mutation::Put {
                key: Key::new(bytes.to_vec())?,
                value: value.to_vec(),
            });
        }
        check_retry_writes(writes, &metadata, &mutations, event, limits)?;
        let receipt = self
            .storage
            .commit_batch(snapshot, transaction_id, &mutations)?;
        if receipt.commit_seq != frame.sequence {
            return Err(DocumentError::Corrupt("retry receipt changed"));
        }
        let metadata_permit = self.budget.try_reserve(
            Attribution {
                tenant: self.config.storage.tenant,
                worker: self.config.storage.worker,
                category: MemoryCategory::WriteBuffer,
            },
            marker.bytes().len(),
        )?;
        Ok(Some(DistributedDocumentCommit {
            receipt,
            event,
            event_digest: digest.to_bytes(),
            metadata: marker.bytes().to_vec(),
            frame,
            _metadata_permit: metadata_permit,
        }))
    }
}

#[cfg(test)]
mod tests {
    use tb_core::{DatabaseId, OriginId, ReplicaId, ShardId, TransactionId};
    use tb_format::DocumentEventId;

    use super::stage_token;

    #[test]
    fn stage_token_follows_exact_group_across_crash_replan() {
        let event = DocumentEventId {
            database: DatabaseId::from_bytes([1; 16]),
            shard: ShardId::new(2),
            origin: OriginId {
                replica_id: ReplicaId::from_bytes([3; 16]),
                incarnation: [4; 16],
            },
            sequence: 7,
        };
        let final_token = TransactionId::new(50_000, [5; 16]);
        let first = stage_token(final_token, event, [6; 32]).expect("first stage token");
        let second = stage_token(final_token, event, [7; 32]).expect("second stage token");
        assert_ne!(first, second);
        assert_ne!(first, final_token);
        assert_ne!(second, final_token);
        assert_eq!(
            second,
            stage_token(final_token, event, [7; 32]).unwrap(),
            "after a persisted first stage is omitted, the remaining group keeps its own token"
        );
        assert_eq!(
            second.expires_at_unix_ms(),
            final_token.expires_at_unix_ms()
        );
    }
}
