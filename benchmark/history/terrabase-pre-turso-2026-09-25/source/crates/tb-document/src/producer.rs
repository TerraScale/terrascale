//! Local-only source event production and durable retry reconciliation.

use std::collections::BTreeMap;

use tb_core::{
    CommitReceipt, CommitSeq, Key, KeyBound, KeyRange, OriginId, Reservation, TransactionId,
};
use tb_format::{
    DocumentBinding, DocumentDot, DocumentEventId, DocumentFieldInput, DocumentKey, DocumentKind,
    DocumentSibling, FormatError, WalOperation, decode_active_value, decode_catalog, decode_change,
    decode_document, decode_document_key, decode_high_water_value, decode_retry_value,
    decode_state_with_limits, encode_change_into, encode_change_key, encode_document_into,
    encode_high_water_key, encode_high_water_value, encode_marker_key, encode_primary_key,
    encode_retry_key, encode_retry_value, encode_state_into, encode_wal_frame_into,
    validate_document_schema,
};
use tb_replication::{
    CausalContext, CollectionBinding, EventId, MetadataChange, MetadataHeader, WalRole,
    compute_event_digest, decode_metadata, encode_metadata_into, verify_event,
};
use tb_storage::{CommittedBatch, Mutation, StorageError};

use crate::{DocumentError, DocumentStore, key};

/// A caller's intended live value or a causally retained deletion.
#[derive(Clone, Copy)]
pub enum DocumentWriteValue<'a> {
    Live(&'a [DocumentFieldInput<'a>]),
    Tombstone,
}

/// One mutation in strictly increasing `(CollectionId, DocumentId)` order.
/// `observed` is a validated causal token previously returned to the caller;
/// siblings it does not cover remain visible as conflicts.
#[derive(Clone, Copy)]
pub struct DocumentWrite<'a> {
    pub binding: DocumentBinding,
    pub observed: &'a CausalContext,
    pub value: DocumentWriteValue<'a>,
}

/// A locally synced source event with its exact marker and verified digest.
pub struct DocumentCommit {
    pub receipt: CommitReceipt,
    pub event: DocumentEventId,
    pub event_digest: [u8; 32],
    metadata: Vec<u8>,
    frame: CommittedBatch,
    _metadata_permit: Reservation,
}

struct PreparedWrites {
    bindings: Vec<CollectionBinding>,
    changes: Vec<MetadataChange>,
    source: Vec<Mutation>,
    primary: Vec<Mutation>,
}

struct PreparedGroup {
    mutations: Vec<Mutation>,
    metadata: Vec<u8>,
    metadata_permit: Reservation,
    expected_frame: Vec<u8>,
    expected_commit_sequence: u64,
}

impl DocumentCommit {
    /// Complete, checksummed `TBMET001` bytes held under a memory reservation.
    #[must_use]
    pub fn metadata(&self) -> &[u8] {
        &self.metadata
    }

    /// Exact synced, retained sender WAL frame held under a memory reservation.
    #[must_use]
    pub fn wal_frame(&self) -> &[u8] {
        self.frame.encoded_bytes()
    }
}

impl DocumentStore {
    /// Atomically write several sorted documents in this one shard. The D1
    /// source changes, D0 states, D2 metadata, D3 retry and D4 sequence are
    /// one synced WAL group. The event digest is calculated only afterward.
    ///
    /// # Errors
    /// Rejects stale bindings, forged causal tokens, limits, or uncertain sync.
    pub fn commit_documents(
        &mut self,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<DocumentCommit, DocumentError> {
        if self.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        let _work_permit = self.admit_writes(writes)?;
        let snapshot = self.storage.snapshot();
        if let Some(previous) = self.resolve_retry(snapshot, transaction_id, writes)? {
            return Ok(previous);
        }
        let sequence = self
            .local_sequence
            .checked_add(1)
            .ok_or(DocumentError::Capacity("local event sequence exhausted"))?;
        let event = DocumentEventId {
            database: self.database,
            shard: self.shard,
            origin: self.origin,
            sequence,
        };
        let event_id = EventId::new(self.database, self.shard, self.origin, sequence)?;
        let writes = self.prepare_writes(snapshot, writes, event)?;
        let PreparedGroup {
            mutations,
            metadata,
            metadata_permit,
            expected_frame,
            expected_commit_sequence,
        } = self.prepare_group(snapshot, transaction_id, event, event_id, writes)?;
        let receipt = match self
            .storage
            .commit_batch(snapshot, transaction_id, &mutations)
        {
            Ok(receipt) => receipt,
            Err(error) => {
                if matches!(error, StorageError::Io(_) | StorageError::Poisoned) {
                    self.poisoned = true;
                }
                return Err(error.into());
            }
        };
        self.local_sequence = sequence;
        let verified = (|| -> Result<_, DocumentError> {
            if receipt.commit_seq.get() != expected_commit_sequence {
                return Err(DocumentError::Corrupt(
                    "new event reused an old storage receipt",
                ));
            }
            let batch = self
                .retained_frame(transaction_id)?
                .ok_or(DocumentError::Corrupt("synced event WAL frame missing"))?;
            if batch.sequence != receipt.commit_seq || batch.encoded_bytes() != expected_frame {
                return Err(DocumentError::Corrupt("synced event WAL frame differs"));
            }
            let digest = compute_event_digest(&metadata, batch.encoded_bytes())?;
            if verify_event(&metadata, batch.encoded_bytes(), digest, self.limits)?.id() != event_id
            {
                return Err(DocumentError::Corrupt("synced event identity mismatch"));
            }
            Ok((batch, digest.to_bytes()))
        })();
        let (batch, event_digest) = match verified {
            Ok(verified) => verified,
            Err(error) => {
                self.poisoned = true;
                return Err(DocumentError::CommitUncertain(Box::new(error)));
            }
        };
        Ok(DocumentCommit {
            receipt,
            event,
            event_digest,
            metadata,
            frame: batch,
            _metadata_permit: metadata_permit,
        })
    }

    fn admit_writes(&self, writes: &[DocumentWrite<'_>]) -> Result<Reservation, DocumentError> {
        if writes.is_empty()
            || writes.len() > self.limits.max_changes
            || writes
                .len()
                .checked_mul(2)
                .and_then(|count| count.checked_add(3))
                .is_none_or(|count| count > self.format.max_operations)
        {
            return Err(DocumentError::Capacity(
                "document change or WAL operation count",
            ));
        }
        let work_bytes = writes
            .iter()
            .try_fold(0_usize, |bytes, write| {
                let next = match write.value {
                    DocumentWriteValue::Live(fields) => fields
                        .iter()
                        .try_fold(128_usize + fields.len() * 13, |size, field| {
                            size.checked_add(field.value.len())
                        })?,
                    DocumentWriteValue::Tombstone => 128,
                };
                bytes.checked_add(next.checked_mul(3)?)
            })
            .and_then(|bytes| bytes.checked_add(tb_core::MAX_EVENT_METADATA_BYTES))
            .and_then(|bytes| bytes.checked_add(self.format.max_frame_bytes))
            .ok_or(DocumentError::Capacity("document work reservation"))?;
        let mut previous = None;
        for write in writes {
            let pair = (write.binding.collection, write.binding.document);
            if previous.is_some_and(|previous| previous >= pair) {
                return Err(DocumentError::InvalidWriteOrder);
            }
            previous = Some(pair);
        }
        Ok(self
            .budget
            .try_reserve(self.write_attribution, work_bytes)?)
    }

    fn prepare_group(
        &self,
        snapshot: tb_core::ReadSnapshot,
        transaction_id: TransactionId,
        event: DocumentEventId,
        event_id: EventId,
        prepared: PreparedWrites,
    ) -> Result<PreparedGroup, DocumentError> {
        let PreparedWrites {
            bindings,
            changes,
            source,
            primary,
        } = prepared;
        let mut roles = Vec::with_capacity(changes.len() * 2 + 3);
        roles.extend(std::iter::repeat_n(WalRole::SourceChange, changes.len()));
        roles.extend(std::iter::repeat_n(WalRole::Primary, changes.len()));
        roles.extend([WalRole::Marker, WalRole::Retry, WalRole::HighWater]);
        let mut metadata_reservation = self
            .budget
            .try_reserve(self.write_attribution, tb_core::MAX_EVENT_METADATA_BYTES)?;
        let mut metadata = Vec::new();
        encode_metadata_into(
            &mut metadata,
            MetadataHeader {
                id: event_id,
                epoch: self.epoch,
                configuration: self.configuration,
                transaction_id,
                record_format: 1,
                merge_policy: 1,
            },
            &bindings,
            &changes,
            &roles,
            self.limits,
        )?;
        let metadata_permit = metadata_reservation
            .split(metadata.len())
            .map_err(|_| DocumentError::Corrupt("metadata reservation split"))?;
        drop(metadata_reservation);
        let mut mutations = source;
        mutations.extend(primary);
        mutations.push(Mutation::Put {
            key: key(encode_marker_key(self.origin, event.sequence)?)?,
            value: metadata.clone(),
        });
        mutations.push(Mutation::Put {
            key: key(encode_retry_key(transaction_id))?,
            value: encode_retry_value(event)?.to_vec(),
        });
        mutations.push(Mutation::Put {
            key: key(encode_high_water_key(self.origin)?)?,
            value: encode_high_water_value(event.sequence)?.to_vec(),
        });
        let operations = mutations
            .iter()
            .map(|mutation| match mutation {
                Mutation::Put { key, value } => WalOperation::Put {
                    key: key.as_bytes(),
                    value,
                },
                Mutation::Delete { key } => WalOperation::Delete {
                    key: key.as_bytes(),
                },
            })
            .collect::<Vec<_>>();
        let expected_commit_sequence = snapshot
            .seq
            .get()
            .checked_add(1)
            .ok_or(DocumentError::Capacity("storage commit sequence exhausted"))?;
        let mut expected_frame = Vec::new();
        encode_wal_frame_into(
            &mut expected_frame,
            expected_commit_sequence,
            transaction_id,
            &operations,
            &self.format,
        )?;
        Ok(PreparedGroup {
            mutations,
            metadata,
            metadata_permit,
            expected_frame,
            expected_commit_sequence,
        })
    }

    fn prepare_writes(
        &self,
        snapshot: tb_core::ReadSnapshot,
        writes: &[DocumentWrite<'_>],
        event: DocumentEventId,
    ) -> Result<PreparedWrites, DocumentError> {
        let mut prepared = PreparedWrites {
            bindings: Vec::new(),
            changes: Vec::new(),
            source: Vec::new(),
            primary: Vec::new(),
        };
        prepared
            .bindings
            .try_reserve_exact(writes.len())
            .map_err(|_| DocumentError::Capacity("binding allocation"))?;
        prepared
            .changes
            .try_reserve_exact(writes.len())
            .map_err(|_| DocumentError::Capacity("change allocation"))?;
        prepared
            .source
            .try_reserve_exact(writes.len())
            .map_err(|_| DocumentError::Capacity("source allocation"))?;
        prepared
            .primary
            .try_reserve_exact(writes.len())
            .map_err(|_| DocumentError::Capacity("primary allocation"))?;
        for (index, &write) in writes.iter().enumerate() {
            let source_ordinal = prepared.source.len();
            let (binding, change, source, primary) =
                self.prepare_one(snapshot, write, event, index, source_ordinal)?;
            if prepared
                .bindings
                .last()
                .is_none_or(|previous: &CollectionBinding| {
                    previous.collection != binding.collection
                })
            {
                prepared.bindings.push(binding);
            } else if prepared.bindings.last() != Some(&binding) {
                return Err(DocumentError::SchemaChanged);
            }
            prepared.changes.push(change);
            prepared.source.push(source);
            prepared.primary.push(primary);
        }
        Ok(prepared)
    }

    fn prepare_one(
        &self,
        snapshot: tb_core::ReadSnapshot,
        write: DocumentWrite<'_>,
        event: DocumentEventId,
        logical_index: usize,
        source_ordinal: usize,
    ) -> Result<(CollectionBinding, MetadataChange, Mutation, Mutation), DocumentError> {
        let binding = write.binding;
        let (kind, document_bytes) = self.prepare_value(snapshot, write)?;
        let primary_key = key(encode_primary_key(binding.collection, binding.document))?;
        let old_value = self.storage.get(snapshot, &primary_key)?;
        let old_state = old_value
            .as_ref()
            .map(|value| {
                decode_state_with_limits(
                    value.bytes(),
                    self.limits.max_origins,
                    self.limits.max_siblings,
                )
            })
            .transpose()?;
        if let Some(state) = &old_state {
            if state.collection != binding.collection || state.document != binding.document {
                return Err(DocumentError::Corrupt(
                    "existing primary key/value mismatch",
                ));
            }
            for &sibling in &state.siblings {
                self.check_sibling_catalog(
                    snapshot,
                    binding.collection,
                    binding.document,
                    sibling,
                )?;
            }
        }
        let observed =
            self.validated_observed(write.observed, old_state.as_ref(), event.sequence)?;
        let state_bytes = self.prepare_state(
            binding,
            event,
            kind,
            &document_bytes,
            &observed,
            old_state.as_ref(),
        )?;
        let mut change_bytes = Vec::new();
        encode_change_into(&mut change_bytes, event, binding, kind, &document_bytes)?;
        let logical_ordinal =
            u16::try_from(logical_index).map_err(|_| DocumentError::Capacity("change ordinal"))?;
        let source_wal_ordinal = u16::try_from(source_ordinal)
            .map_err(|_| DocumentError::Capacity("source WAL ordinal"))?;
        let change_key = key(encode_change_key(
            binding.collection,
            binding.document,
            self.origin,
            event.sequence,
            logical_ordinal,
        )?)?;
        Ok((
            CollectionBinding {
                collection: binding.collection,
                generation: binding.generation,
                schema_digest: binding.schema_digest,
            },
            MetadataChange {
                collection: binding.collection,
                document: binding.document,
                source_wal_ordinal,
                kind,
                observed,
            },
            Mutation::Put {
                key: change_key,
                value: change_bytes,
            },
            Mutation::Put {
                key: primary_key,
                value: state_bytes,
            },
        ))
    }

    fn prepare_value(
        &self,
        snapshot: tb_core::ReadSnapshot,
        write: DocumentWrite<'_>,
    ) -> Result<(DocumentKind, Vec<u8>), DocumentError> {
        let binding = write.binding;
        let active_key = key(tb_format::encode_active_key(binding.collection))?;
        let active_bytes = self
            .storage
            .get(snapshot, &active_key)?
            .ok_or(DocumentError::SchemaChanged)?;
        let active = decode_active_value(active_bytes.bytes())?;
        if active.generation != binding.generation || active.digest != binding.schema_digest {
            return Err(DocumentError::SchemaChanged);
        }
        let catalog_key = key(tb_format::encode_catalog_key(
            binding.collection,
            binding.generation,
        )?)?;
        let catalog_bytes = self
            .storage
            .get(snapshot, &catalog_key)?
            .ok_or(DocumentError::Corrupt("active schema definition missing"))?;
        let catalog = decode_catalog(catalog_bytes.bytes())?;
        if catalog.collection != binding.collection
            || catalog.generation != binding.generation
            || catalog.digest != binding.schema_digest
        {
            return Err(DocumentError::Corrupt("active schema definition mismatch"));
        }
        let mut document_bytes = Vec::new();
        let kind = match write.value {
            DocumentWriteValue::Live(fields) => {
                encode_document_into(&mut document_bytes, binding, fields)?;
                validate_document_schema(decode_document(&document_bytes)?, &catalog.schema)
                    .map_err(|error| match error {
                        FormatError::Allocation => DocumentError::Format(error),
                        other => DocumentError::InvalidDocumentFields(other),
                    })?;
                DocumentKind::Live
            }
            DocumentWriteValue::Tombstone => DocumentKind::Tombstone,
        };
        Ok((kind, document_bytes))
    }

    fn prepare_state(
        &self,
        binding: DocumentBinding,
        event: DocumentEventId,
        kind: DocumentKind,
        document_bytes: &[u8],
        observed: &CausalContext,
        old_state: Option<&tb_format::DocumentStateView<'_>>,
    ) -> Result<Vec<u8>, DocumentError> {
        let mut context = BTreeMap::new();
        if let Some(state) = old_state {
            for dot in &state.context {
                context.insert(dot.origin, dot.sequence);
            }
        }
        for (origin, prefix) in observed.entries() {
            context
                .entry(origin)
                .and_modify(|old: &mut u64| *old = (*old).max(prefix))
                .or_insert(prefix);
        }
        context.insert(self.origin, event.sequence);
        if context.len() > self.limits.max_origins {
            return Err(DocumentError::Capacity("causal context origins"));
        }
        let context = context
            .into_iter()
            .map(|(origin, sequence)| DocumentDot { origin, sequence })
            .collect::<Vec<_>>();
        let mut siblings = Vec::new();
        if let Some(state) = old_state {
            for &sibling in &state.siblings {
                if observed.get(sibling.event.origin) < sibling.event.sequence {
                    siblings.push(sibling);
                }
            }
        }
        siblings.push(DocumentSibling {
            event,
            generation: binding.generation,
            schema_digest: binding.schema_digest,
            kind,
            document_bytes,
        });
        siblings.sort_by_key(|sibling| sibling.event);
        if siblings.len() > self.limits.max_siblings {
            return Err(DocumentError::Capacity("document siblings"));
        }
        let mut state_bytes = Vec::new();
        encode_state_into(
            &mut state_bytes,
            binding.collection,
            binding.document,
            &context,
            &siblings,
        )?;
        Ok(state_bytes)
    }

    fn validated_observed(
        &self,
        supplied: &CausalContext,
        state: Option<&tb_format::DocumentStateView<'_>>,
        sequence: u64,
    ) -> Result<CausalContext, DocumentError> {
        let mut known = BTreeMap::<OriginId, u64>::new();
        if let Some(state) = state {
            for dot in &state.context {
                known.insert(dot.origin, dot.sequence);
            }
            for sibling in &state.siblings {
                known
                    .entry(sibling.event.origin)
                    .and_modify(|prefix| *prefix = (*prefix).max(sibling.event.sequence))
                    .or_insert(sibling.event.sequence);
            }
        }
        if sequence > 1 {
            known.insert(self.origin, sequence - 1);
        }
        let mut observed = BTreeMap::new();
        for (origin, prefix) in supplied.entries() {
            if prefix == 0 {
                return Err(DocumentError::InvalidCausalContext(
                    "zero observed causal prefix",
                ));
            }
            if prefix > known.get(&origin).copied().unwrap_or(0) {
                return Err(DocumentError::InvalidCausalContext(
                    "causal token exceeds known local history",
                ));
            }
            observed.insert(origin, prefix);
        }
        if sequence > 1 {
            observed.insert(self.origin, sequence - 1);
        }
        if observed.len() > self.limits.max_origins {
            return Err(DocumentError::Capacity("observed causal origins"));
        }
        Ok(CausalContext::from_entries(
            observed,
            self.limits.max_origins,
        )?)
    }

    fn retained_frame(
        &self,
        transaction_id: TransactionId,
    ) -> Result<Option<CommittedBatch>, DocumentError> {
        #[cfg(test)]
        if self
            .fail_frame_lookup_once
            .swap(false, std::sync::atomic::Ordering::SeqCst)
        {
            return Err(DocumentError::Storage(StorageError::Io(
                std::io::Error::other("injected retained frame read failure"),
            )));
        }
        Ok(self
            .storage
            .committed_batch_for_transaction(transaction_id)?)
    }

    // Reopen audits the retained WAL once, including events whose transaction
    // retry deadline has expired and are therefore absent from storage's live
    // transaction index. Steady commits and retries use the indexed lookup.
    fn audit_frame_on_reopen(
        &self,
        transaction_id: TransactionId,
    ) -> Result<Option<CommittedBatch>, DocumentError> {
        let mut cursor = self.storage.committed_cursor()?;
        let mut after = CommitSeq::ZERO;
        let mut found = None;
        while let Some(batch) = cursor.next(after, self.format.max_frame_bytes)? {
            after = batch.sequence;
            if batch.transaction_id == transaction_id {
                if found.is_some() {
                    return Err(DocumentError::Corrupt(
                        "duplicate retained transaction frame",
                    ));
                }
                found = Some(batch);
            }
        }
        Ok(found)
    }

    #[allow(
        clippy::too_many_lines,
        reason = "recovery checks high-water, marker, retry, and retained frame as one fail-closed invariant"
    )]
    pub(super) fn reconcile_local_tail(&mut self) -> Result<(), DocumentError> {
        let snapshot = self.storage.snapshot();
        let high_water_key = key(encode_high_water_key(self.origin)?)?;
        let high_water = self.storage.get(snapshot, &high_water_key)?;
        let range = KeyRange::new(
            KeyBound::Included(Key::new(vec![0xD4, 1])?),
            KeyBound::Excluded(Key::new(vec![0xD5])?),
        )?;
        let page = self.storage.scan(snapshot, &range, 2)?;
        if page.rows().len() > 1
            || page
                .rows()
                .first()
                .is_some_and(|row| row.key != high_water_key)
        {
            return Err(DocumentError::Corrupt(
                "local origin high-water identity mismatch",
            ));
        }
        let Some(high_water) = high_water else {
            let marker_range = KeyRange::new(
                KeyBound::Included(Key::new(vec![0xD2, 1])?),
                KeyBound::Excluded(Key::new(vec![0xD3])?),
            )?;
            if !self
                .storage
                .scan(snapshot, &marker_range, 1)?
                .rows()
                .is_empty()
            {
                return Err(DocumentError::Corrupt(
                    "event marker without local high-water",
                ));
            }
            let mut cursor = self.storage.committed_cursor()?;
            let mut after = CommitSeq::ZERO;
            while let Some(batch) = cursor.next(after, self.format.max_frame_bytes)? {
                after = batch.sequence;
                for operation in batch.frame(&self.format)?.operations() {
                    match operation? {
                        WalOperation::Put { key, value } => match decode_document_key(key)? {
                            DocumentKey::Catalog {
                                collection,
                                generation,
                            } => {
                                let catalog = decode_catalog(value)?;
                                if catalog.collection != collection
                                    || catalog.generation != generation
                                {
                                    return Err(DocumentError::Corrupt(
                                        "catalog-only WAL binding mismatch",
                                    ));
                                }
                            }
                            DocumentKey::Active { .. } => {
                                decode_active_value(value)?;
                            }
                            _ => {
                                return Err(DocumentError::Corrupt(
                                    "document event without high-water",
                                ));
                            }
                        },
                        WalOperation::Delete { .. } => {
                            return Err(DocumentError::Corrupt("raw Delete in document shard"));
                        }
                    }
                }
            }
            self.local_sequence = 0;
            return Ok(());
        };
        let sequence = decode_high_water_value(high_water.bytes())?;
        let marker_key = key(encode_marker_key(self.origin, sequence)?)?;
        let marker = self
            .storage
            .get(snapshot, &marker_key)?
            .ok_or(DocumentError::Corrupt("high-water event marker missing"))?;
        let metadata = decode_metadata(marker.bytes(), self.limits)?;
        let expected_id = EventId::new(self.database, self.shard, self.origin, sequence)?;
        if metadata.header.id != expected_id {
            return Err(DocumentError::Corrupt("high-water event identity mismatch"));
        }
        let retry_key = key(encode_retry_key(metadata.header.transaction_id))?;
        let retry = self
            .storage
            .get(snapshot, &retry_key)?
            .ok_or(DocumentError::Corrupt("high-water retry mapping missing"))?;
        if decode_retry_value(retry.bytes())?
            != (DocumentEventId {
                database: self.database,
                shard: self.shard,
                origin: self.origin,
                sequence,
            })
        {
            return Err(DocumentError::Corrupt("high-water retry mapping mismatch"));
        }
        let batch = self
            .audit_frame_on_reopen(metadata.header.transaction_id)?
            .ok_or(DocumentError::Corrupt("high-water WAL frame missing"))?;
        let digest = compute_event_digest(marker.bytes(), batch.encoded_bytes())?;
        if verify_event(marker.bytes(), batch.encoded_bytes(), digest, self.limits)?.id()
            != expected_id
        {
            return Err(DocumentError::Corrupt("high-water WAL frame mismatch"));
        }
        self.local_sequence = sequence;
        Ok(())
    }

    #[allow(
        clippy::too_many_lines,
        reason = "retry compares the exact retained source frame and caller mutation before returning its original receipt"
    )]
    fn resolve_retry(
        &self,
        snapshot: tb_core::ReadSnapshot,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<Option<DocumentCommit>, DocumentError> {
        if transaction_id.expires_at_unix_ms() <= self.storage.stats().expired_floor_ms {
            return Err(DocumentError::Storage(StorageError::IdempotencyExpired));
        }
        let retry_key = key(encode_retry_key(transaction_id))?;
        let Some(retry) = self.storage.get(snapshot, &retry_key)? else {
            return Ok(None);
        };
        let event = decode_retry_value(retry.bytes())?;
        if event.database != self.database
            || event.shard != self.shard
            || event.origin != self.origin
        {
            return Err(DocumentError::Corrupt("retry event scope mismatch"));
        }
        let marker_key = key(encode_marker_key(event.origin, event.sequence)?)?;
        let marker = self
            .storage
            .get(snapshot, &marker_key)?
            .ok_or(DocumentError::Corrupt("retry event marker missing"))?;
        let metadata = decode_metadata(marker.bytes(), self.limits)?;
        if metadata.header.transaction_id != transaction_id
            || metadata.header.id
                != EventId::new(self.database, self.shard, self.origin, event.sequence)?
        {
            return Err(DocumentError::Corrupt("retry metadata identity mismatch"));
        }
        if metadata.changes.len() != writes.len() {
            return Err(DocumentError::Storage(StorageError::IdempotencyConflict));
        }
        let batch = self
            .retained_frame(transaction_id)?
            .ok_or(DocumentError::Corrupt("retry WAL frame missing"))?;
        let digest = compute_event_digest(marker.bytes(), batch.encoded_bytes())?;
        verify_event(marker.bytes(), batch.encoded_bytes(), digest, self.limits)?;
        let frame = batch.frame(&self.format)?;
        let mut original_mutations = Vec::new();
        original_mutations
            .try_reserve_exact(frame.operation_count())
            .map_err(|_| DocumentError::Capacity("retry frame mutation allocation"))?;
        for operation in frame.operations() {
            match operation? {
                WalOperation::Put { key: bytes, value } => original_mutations.push(Mutation::Put {
                    key: Key::new(bytes.to_vec())?,
                    value: value.to_vec(),
                }),
                WalOperation::Delete { .. } => {
                    return Err(DocumentError::Corrupt("retry frame has raw Delete"));
                }
            }
        }
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
            let original = &original_mutations[usize::from(change.source_wal_ordinal)];
            let Mutation::Put { value, .. } = original else {
                return Err(DocumentError::Corrupt("retry source change missing"));
            };
            let decoded = decode_change(value)?;
            let mut intended = Vec::new();
            let kind = match write.value {
                DocumentWriteValue::Live(fields) => {
                    encode_document_into(&mut intended, write.binding, fields)?;
                    DocumentKind::Live
                }
                DocumentWriteValue::Tombstone => DocumentKind::Tombstone,
            };
            if decoded.kind != kind || decoded.document_bytes != intended {
                return Err(DocumentError::Storage(StorageError::IdempotencyConflict));
            }
            for (origin, prefix) in write.observed.entries() {
                if prefix > change.observed.get(origin) {
                    return Err(DocumentError::InvalidCausalContext(
                        "causal token exceeds known local history",
                    ));
                }
            }
            let mut observed = BTreeMap::from_iter(write.observed.entries());
            if event.sequence > 1 {
                observed.insert(self.origin, event.sequence - 1);
            }
            let observed = CausalContext::from_entries(observed, self.limits.max_origins)?;
            if change.observed != observed {
                return Err(DocumentError::Storage(StorageError::IdempotencyConflict));
            }
        }
        let receipt = self
            .storage
            .commit_batch(snapshot, transaction_id, &original_mutations)?;
        if receipt.commit_seq != batch.sequence {
            return Err(DocumentError::Corrupt(
                "retry receipt differs from retained frame",
            ));
        }
        let metadata_permit = self
            .budget
            .try_reserve(self.write_attribution, marker.bytes().len())?;
        Ok(Some(DocumentCommit {
            receipt,
            event,
            event_digest: digest.to_bytes(),
            metadata: marker.bytes().to_vec(),
            frame: batch,
            _metadata_permit: metadata_permit,
        }))
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::sync::atomic::Ordering;
    use std::time::{SystemTime, UNIX_EPOCH};

    use tb_core::{
        BudgetConfig, CollectionId, DatabaseId, DocumentId, MemoryBudget, OriginId, ReplicaId,
        ShardId, TenantId, WorkerId,
    };
    use tb_format::{
        DocumentBinding, DocumentFieldInput, FieldType, FormatError, encode_catalog_into,
        encode_schema_into,
    };
    use tb_replication::{CausalContext, ConfigurationId, ReplicationLimits};
    use tb_storage::StorageConfig;

    use super::{DocumentWrite, DocumentWriteValue};
    use crate::{DocumentError, DocumentPoint, DocumentStore, DocumentStoreConfig};

    struct TempShard(PathBuf);

    impl Drop for TempShard {
        fn drop(&mut self) {
            if self.0.exists() {
                std::fs::remove_dir_all(&self.0).expect("remove test shard");
            }
        }
    }

    fn local_input_fixture() -> (TempShard, DocumentStore, DocumentBinding, OriginId) {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let directory = TempShard(std::env::temp_dir().join(format!(
            "terrabase-document-invalid-input-{}-{nonce}",
            std::process::id(),
        )));
        let origin = OriginId {
            replica_id: ReplicaId::from_bytes([3; 16]),
            incarnation: [4; 16],
        };
        let config = DocumentStoreConfig {
            storage: StorageConfig::local(&directory.0, TenantId::from_bytes([1; 16]), WorkerId(8))
                .with_clock(Arc::new(|| 1_000)),
            database: DatabaseId::from_bytes([2; 16]),
            shard: ShardId::new(7),
            origin,
            epoch: tb_core::MembershipEpoch::new(1),
            configuration: ConfigurationId::from_bytes([5; 32]).unwrap(),
            replication: ReplicationLimits {
                max_origins: 8,
                max_changes: 4,
                max_siblings: 4,
                max_value_bytes: tb_core::MAX_VALUE_BYTES,
                max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
            },
        };
        let mut store = DocumentStore::open(
            config,
            MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).unwrap()),
        )
        .unwrap();
        let collection = CollectionId::from_bytes([6; 16]);
        let mut schema = Vec::new();
        encode_schema_into(&mut schema, &[]).unwrap();
        let digest = encode_catalog_into(&mut Vec::new(), collection, 1, 0, &schema).unwrap();
        store
            .provision_schema(
                tb_core::TransactionId::new(50_000, [1; 16]),
                collection,
                1,
                0,
                &schema,
            )
            .unwrap();
        let binding = DocumentBinding {
            collection,
            document: DocumentId::from_bytes([7; 16]),
            generation: 1,
            schema_digest: digest,
        };
        (directory, store, binding, origin)
    }

    #[test]
    fn submitted_live_schema_failure_has_client_input_provenance() {
        let (_directory, mut store, binding, _) = local_input_fixture();
        let observed = CausalContext::new();
        let fields = [DocumentFieldInput {
            field_id: 1,
            value_type: FieldType::Null,
            value: &[],
        }];
        let before = store.snapshot();
        assert!(matches!(
            store.commit_documents(
                tb_core::TransactionId::new(50_000, [2; 16]),
                &[DocumentWrite {
                    binding,
                    observed: &observed,
                    value: DocumentWriteValue::Live(&fields),
                }],
            ),
            Err(DocumentError::InvalidDocumentFields(FormatError::Invalid {
                reason: "unknown top-level field",
                ..
            }))
        ));
        assert_eq!(store.snapshot(), before);

        let committed = store
            .commit_documents(
                tb_core::TransactionId::new(50_000, [3; 16]),
                &[DocumentWrite {
                    binding,
                    observed: &observed,
                    value: DocumentWriteValue::Live(&[]),
                }],
            )
            .expect("valid write after rejected fields");
        assert_eq!(committed.event.sequence, 1);
    }

    #[test]
    fn invalid_order_and_causal_input_leave_local_writer_usable() {
        let (_directory, mut store, binding, origin) = local_input_fixture();
        let empty = CausalContext::new();
        let first = DocumentWrite {
            binding,
            observed: &empty,
            value: DocumentWriteValue::Tombstone,
        };
        let second = DocumentWrite {
            binding: DocumentBinding {
                document: DocumentId::from_bytes([8; 16]),
                ..first.binding
            },
            ..first
        };
        let before = store.snapshot();
        assert!(matches!(
            store.commit_documents(
                tb_core::TransactionId::new(50_000, [2; 16]),
                &[second, first]
            ),
            Err(DocumentError::InvalidWriteOrder)
        ));
        assert_eq!(store.snapshot(), before);
        assert!(matches!(
            store.commit_documents(
                tb_core::TransactionId::new(50_000, [3; 16]),
                &[first, first]
            ),
            Err(DocumentError::InvalidWriteOrder)
        ));
        assert_eq!(store.snapshot(), before);
        let future = CausalContext::from_entries([(origin, 999)], 8).unwrap();
        let forged = DocumentWrite {
            observed: &future,
            ..first
        };
        assert!(matches!(
            store.commit_documents(tb_core::TransactionId::new(50_000, [4; 16]), &[forged]),
            Err(DocumentError::InvalidCausalContext(
                "causal token exceeds known local history"
            ))
        ));
        assert_eq!(store.snapshot(), before);
        let exact_token = tb_core::TransactionId::new(50_000, [5; 16]);
        let commit = store.commit_documents(exact_token, &[first]).unwrap();
        assert_eq!(commit.event.sequence, 1);
        let committed = store.snapshot();
        assert!(matches!(
            store.commit_documents(exact_token, &[first, second]),
            Err(DocumentError::Storage(
                tb_storage::StorageError::IdempotencyConflict
            ))
        ));
        assert_eq!(store.snapshot(), committed);
        assert!(matches!(
            store
                .read(committed, binding.collection, binding.document)
                .unwrap()
                .point(),
            DocumentPoint::Deleted { .. }
        ));
        let retry = store.commit_documents(exact_token, &[first]).unwrap();
        assert_eq!(retry.receipt.commit_seq, commit.receipt.commit_seq);
    }

    #[test]
    #[allow(
        clippy::too_many_lines,
        reason = "one crash-recovery scenario checks poisoning, forged retry rejection, valid retry, and sequence continuity"
    )]
    fn failed_post_sync_frame_read_poisoned_until_reopen_reconciles_event() {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let directory = TempShard(std::env::temp_dir().join(format!(
            "terrabase-document-post-sync-{}-{nonce}",
            std::process::id(),
        )));
        let origin = OriginId {
            replica_id: ReplicaId::from_bytes([3; 16]),
            incarnation: [4; 16],
        };
        let config = DocumentStoreConfig {
            storage: StorageConfig::local(&directory.0, TenantId::from_bytes([1; 16]), WorkerId(8))
                .with_clock(Arc::new(|| 1_000)),
            database: DatabaseId::from_bytes([2; 16]),
            shard: ShardId::new(7),
            origin,
            epoch: tb_core::MembershipEpoch::new(1),
            configuration: ConfigurationId::from_bytes([5; 32]).unwrap(),
            replication: ReplicationLimits {
                max_origins: 8,
                max_changes: 4,
                max_siblings: 4,
                max_value_bytes: tb_core::MAX_VALUE_BYTES,
                max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
            },
        };
        let memory = MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).unwrap());
        let mut store = DocumentStore::open(config.clone(), memory.clone()).unwrap();
        let collection = CollectionId::from_bytes([6; 16]);
        let document = DocumentId::from_bytes([7; 16]);
        let mut schema = Vec::new();
        encode_schema_into(&mut schema, &[]).unwrap();
        let digest = encode_catalog_into(&mut Vec::new(), collection, 1, 0, &schema).unwrap();
        assert!(
            store
                .provision_schema(
                    tb_core::TransactionId::new(50_000, [1; 16]),
                    collection,
                    1,
                    0,
                    &schema
                )
                .unwrap()
                .is_some()
        );
        let binding = DocumentBinding {
            collection,
            document,
            generation: 1,
            schema_digest: digest,
        };
        let empty = CausalContext::new();
        store
            .commit_documents(
                tb_core::TransactionId::new(50_000, [9; 16]),
                &[DocumentWrite {
                    binding: DocumentBinding {
                        document: DocumentId::from_bytes([8; 16]),
                        ..binding
                    },
                    observed: &empty,
                    value: DocumentWriteValue::Live(&[]),
                }],
            )
            .unwrap();
        let first = DocumentWrite {
            binding,
            observed: &empty,
            value: DocumentWriteValue::Live(&[]),
        };
        store.fail_frame_lookup_once.store(true, Ordering::SeqCst);
        let txn = tb_core::TransactionId::new(50_000, [2; 16]);
        let Err(DocumentError::CommitUncertain(source)) = store.commit_documents(txn, &[first])
        else {
            panic!("post-sync verification must report uncertainty");
        };
        assert!(matches!(
            source.as_ref(),
            DocumentError::Storage(tb_storage::StorageError::Io(_))
        ));
        assert!(matches!(
            store.commit_documents(tb_core::TransactionId::new(50_000, [3; 16]), &[first]),
            Err(DocumentError::NeedsRecovery)
        ));
        drop(store);

        let mut reopened = DocumentStore::open(config, memory).unwrap();
        let before_retry = reopened.snapshot();
        let future = CausalContext::from_entries([(origin, 999)], 8).unwrap();
        assert!(matches!(
            reopened.commit_documents(
                txn,
                &[DocumentWrite {
                    binding,
                    observed: &future,
                    value: DocumentWriteValue::Live(&[]),
                }],
            ),
            Err(DocumentError::InvalidCausalContext(
                "causal token exceeds known local history"
            ))
        ));
        assert_eq!(reopened.snapshot(), before_retry);
        let retry = reopened.commit_documents(txn, &[first]).unwrap();
        assert_eq!(retry.event.sequence, 2);
        let read = reopened
            .read(reopened.snapshot(), collection, document)
            .unwrap();
        let DocumentPoint::Value { context, .. } = read.point() else {
            panic!("recovered event must be visible");
        };
        let context = context.clone();
        let next = reopened
            .commit_documents(
                tb_core::TransactionId::new(50_000, [3; 16]),
                &[DocumentWrite {
                    binding,
                    observed: &context,
                    value: DocumentWriteValue::Tombstone,
                }],
            )
            .unwrap();
        assert_eq!(next.event.sequence, 3);
    }
}
