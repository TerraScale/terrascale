//! Atomic application of a complete source transaction.
use super::{
    AppliedReplication, CommitError, CommitOutcome, CommitReceipt, DocumentValue, Engine,
    EngineError, IndexGenerationSet, LocalDurability, OperationId, ReadSnapshot, ReplicatedRecord,
    TenantScope, merge, replication_fingerprint, storage,
};

/// Maximum records in one bounded source transaction.
pub const MAX_REPLICATION_PUBLICATION_RECORDS: usize = 100_000;
/// Maximum encoded content retained or applied for one source transaction.
pub const MAX_REPLICATION_PUBLICATION_BYTES: usize = 64 * 1024 * 1024;

/// An indivisible source transaction, including private control changes.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicatedPublication {
    /// Source origin identity.
    pub origin: String,
    /// Source transaction sequence.
    pub origin_sequence: u64,
    /// Source hybrid clock.
    pub hlc: u64,
    /// Source operation identity.
    pub commit_ulid: String,
    /// Source dependencies shared by all mutations.
    pub context: Vec<u8>,
    /// Complete original document mutations.
    pub records: Vec<ReplicatedRecord>,
    /// Versioned private metadata and schema changes.
    pub private_payload: Vec<u8>,
}

impl ReplicatedPublication {
    fn commit_intent(&self) -> storage::ReplicationPublicationIntent {
        storage::ReplicationPublicationIntent {
            origin: self.origin.clone(),
            origin_sequence: self.origin_sequence,
            hlc: self.hlc,
            commit_ulid: self.commit_ulid.clone(),
            context: self.context.clone(),
            records: self.records.clone(),
            private_payload: self.private_payload.clone(),
            observations: Vec::new(),
            schema_observations: Vec::new(),
            conflicts: Vec::new(),
        }
    }

    /// Canonical source fingerprint, invariant under document delivery order.
    ///
    /// # Errors
    /// Rejects invalid identities, mismatched source records and oversized groups.
    pub fn fingerprint(&self) -> Result<[u8; 32], EngineError> {
        publication_binding(self).map(|(_, fingerprint)| fingerprint)
    }
}

impl Engine {
    /// Publishes all winning mutations, conflict history and private controls together.
    ///
    /// The complete source content binds one durable replay identity. Contention
    /// rejects the entire attempt so the receiver can recompute every merge.
    ///
    /// # Errors
    /// Rejects malformed, oversized, reused identities or stale merge snapshots.
    pub fn apply_replicated_publication(
        &self,
        scope: &TenantScope,
        publication: &ReplicatedPublication,
    ) -> Result<AppliedReplication, EngineError> {
        self.validate_scope(scope)?;
        let (operation, fingerprint) = publication_binding(publication)?;
        let (metadata, schema_envelopes) = if publication.private_payload.is_empty() {
            (Vec::new(), Vec::new())
        } else {
            storage::decode_private_changes(&publication.private_payload)
                .ok_or(EngineError::InvalidDocument)?
        };
        // A filtered shard catch-up page may intentionally carry no customer
        // records or private metadata. Committing that source identity still
        // advances the origin frontier, preserving causal continuity for the
        // next retained publication whose records are in range.
        let (snapshot, plans) = {
            let storage = self.lock_storage()?;
            let bound = storage
                .port
                .replication_fingerprint(scope.tenant(), &operation)
                .map_err(|()| EngineError::StorageFailure)?;
            if bound.is_some_and(|value| value != fingerprint) {
                if std::env::var_os("TB_SIM_TRACE").is_some() {
                    eprintln!("[engine] branch: bound fingerprint mismatch");
                }
                return Err(EngineError::OperationIdReuse { operation_id: operation });
            }
            if let Some(resolution) = storage.port.resolve_operation(scope.tenant(), &operation) {
                if bound != Some(fingerprint) {
                    if std::env::var_os("TB_SIM_TRACE").is_some() {
                        eprintln!("[engine] branch: resolved without matching fingerprint (bound={bound:?})");
                    }
                    return Err(EngineError::StorageFailure);
                }
                return match resolution {
                    storage::OperationResolution::Committed { sequence, durability } => {
                        Ok(AppliedReplication::new(
                            CommitReceipt::new(operation, sequence, durability),
                            true,
                        ))
                    }
                    storage::OperationResolution::Rejected(_) => {
                        if std::env::var_os("TB_SIM_TRACE").is_some() {
                            eprintln!("[engine] branch: resolution Rejected");
                        }
                        Err(EngineError::StorageFailure)
                    }
                };
            }
            let snapshot = ReadSnapshot::new(
                self.identity,
                scope.tenant().clone(),
                storage.port.published_state().0,
                IndexGenerationSet::default(),
            );
            let plans = publication
                .records
                .iter()
                .map(|record| {
                    let current = storage.port.document_merge_state(
                        scope.tenant(),
                        &record.collection,
                        &record.id,
                    );
                    let context = storage.port.document_context(
                        scope.tenant(),
                        &record.collection,
                        &record.id,
                    );
                    let schema = storage.port.merge_schema(&record.collection);
                    let plan =
                        match plan_record(record, current.as_ref(), context.as_deref(), schema.as_ref()) {
                            Ok(plan) => plan,
                            Err(error) => {
                                if std::env::var_os("TB_SIM_TRACE").is_some() {
                                    eprintln!("[engine] branch: plan_record error {error:?}");
                                }
                                return Err(error);
                            }
                        };
                    Ok((plan, schema.map(|schema| schema.to_canonical_string())))
                })
                .collect::<Result<Vec<_>, EngineError>>()?;
            (snapshot, plans)
        };
        self.witness_replication_clock(publication.hlc);
        let snapshot_sequence = snapshot.sequence();
        let mut transaction = self.begin(scope, snapshot, operation.clone()).map_err(|error| {
            // A tailer and repair lane can race the same still-unbound group.
            // Retry after the reservation completes; durable fingerprints
            // above decide actual identity reuse on the next attempt.
            if matches!(error, EngineError::OperationIdReuse { .. }) {
                EngineError::Conflict
            } else {
                error
            }
        })?;
        let mut group = publication.commit_intent();
        for (record, ((winner, conflict), schema)) in publication.records.iter().zip(plans) {
            group.schema_observations.push((record.collection.clone(), schema));
            group.observations.push((record.collection.clone(), record.id.clone()));
            if let Some(winner) = winner {
                transaction.stage_replicated(&winner)?;
            }
            if let Some(conflict) = conflict {
                group.conflicts.push((record.collection.clone(), record.id.clone(), conflict));
            }
        }
        transaction.metadata = metadata;
        transaction.schema_envelopes = schema_envelopes;
        transaction.replication_fingerprint = Some(fingerprint);
        transaction.replication_publication = Some(group);
        match transaction.commit(LocalDurability::Written) {
            CommitOutcome::Committed(receipt) => {
                let replayed = receipt.sequence() <= snapshot_sequence;
                Ok(AppliedReplication::new(receipt, replayed))
            }
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(CommitError::OperationIdReuse) => {
                Err(EngineError::OperationIdReuse { operation_id: operation })
            }
            other => {
                if std::env::var_os("TB_SIM_TRACE").is_some() {
                    eprintln!("[engine] branch: commit outcome {other:?}");
                }
                Err(EngineError::StorageFailure)
            }
        }
    }
}

fn publication_binding(
    publication: &ReplicatedPublication,
) -> Result<(OperationId, [u8; 32]), EngineError> {
    if publication.origin.is_empty()
        || publication.origin.len() > 255
        || publication.origin_sequence == 0
        || publication.origin_sequence > i64::MAX as u64
        || publication.records.len() > MAX_REPLICATION_PUBLICATION_RECORDS
        || publication.private_payload.len() > MAX_REPLICATION_PUBLICATION_BYTES
        || (publication.records.is_empty() && publication.private_payload.is_empty())
    {
        return Err(EngineError::InvalidDocument);
    }
    OperationId::try_new(&publication.commit_ulid).map_err(|_| EngineError::InvalidDocument)?;
    let context =
        merge::decode_context(&publication.context).ok_or(EngineError::InvalidDocument)?;
    if !merge::DocumentVersion::new(
        context,
        merge::DocumentDot::new(publication.origin.clone(), publication.origin_sequence),
    )
    .is_well_formed()
    {
        return Err(EngineError::InvalidDocument);
    }
    let mut identity = blake3::Hasher::new();
    identity.update(b"terrabase-replication-publication-v1\0");
    identity.update(&(publication.origin.len() as u64).to_be_bytes());
    identity.update(publication.origin.as_bytes());
    identity.update(&publication.origin_sequence.to_be_bytes());
    let operation = OperationId::try_new(&format!("repl-group-{}", identity.finalize().to_hex()))
        .map_err(|_| EngineError::InvalidDocument)?;
    let mut fingerprint = identity;
    fingerprint.update(&publication.hlc.to_be_bytes());
    for bytes in
        [publication.commit_ulid.as_bytes(), &publication.context, &publication.private_payload]
    {
        fingerprint.update(&(bytes.len() as u64).to_be_bytes());
        fingerprint.update(bytes);
    }
    let mut ordered = publication.records.iter().collect::<Vec<_>>();
    ordered.sort_by(|a, b| (&a.collection, &a.id).cmp(&(&b.collection, &b.id)));
    let mut last = None;
    let mut bytes = 41usize
        .checked_add(publication.origin.len())
        .and_then(|bytes| bytes.checked_add(publication.commit_ulid.len()))
        .and_then(|bytes| bytes.checked_add(publication.context.len()))
        .and_then(|bytes| bytes.checked_add(publication.private_payload.len()))
        .ok_or(EngineError::InvalidDocument)?;
    if bytes > MAX_REPLICATION_PUBLICATION_BYTES {
        return Err(EngineError::InvalidDocument);
    }
    for record in ordered {
        if record.origin != publication.origin
            || record.origin_sequence != publication.origin_sequence
            || record.hlc != publication.hlc
            || record.commit_ulid != publication.commit_ulid
            || record.context != publication.context
            || last == Some((&record.collection, &record.id))
        {
            return Err(EngineError::InvalidDocument);
        }
        last = Some((&record.collection, &record.id));
        // Fingerprinting validates the per-document size before any publication.
        let mut canonical = record.clone();
        if canonical.deleted {
            canonical.body = DocumentValue::Null;
        }
        fingerprint.update(&replication_fingerprint(&canonical)?);
        let body_bytes = if canonical.deleted {
            0
        } else {
            storage::encode_private_value(&canonical.body)
                .ok_or(EngineError::InvalidDocument)?
                .len()
        };
        bytes = bytes
            .checked_add(13)
            .and_then(|bytes| bytes.checked_add(record.collection.as_str().len()))
            .and_then(|bytes| bytes.checked_add(record.id.as_str().len()))
            .and_then(|bytes| bytes.checked_add(body_bytes))
            .ok_or(EngineError::InvalidDocument)?;
        if bytes > MAX_REPLICATION_PUBLICATION_BYTES {
            return Err(EngineError::InvalidDocument);
        }
    }
    Ok((operation, *fingerprint.finalize().as_bytes()))
}

type MergePlan = (Option<ReplicatedRecord>, Option<storage::MergeConflictEntry>);

fn plan_record(
    record: &ReplicatedRecord,
    current: Option<&storage::DocumentMergeState>,
    context: Option<&[u8]>,
    schema: Option<&storage::MergeSchema>,
) -> Result<MergePlan, EngineError> {
    let Some(current) = current else {
        return Ok((Some(record.clone()), None));
    };
    let current_version = merge::DocumentVersion::new(
        merge::decode_context(context.unwrap_or_default()).ok_or(EngineError::StorageFailure)?,
        merge::DocumentDot::new(current.origin.clone(), current.origin_sequence),
    );
    let incoming_version = merge::DocumentVersion::new(
        merge::decode_context(&record.context).ok_or(EngineError::InvalidDocument)?,
        merge::DocumentDot::new(record.origin.clone(), record.origin_sequence),
    );
    if !current_version.is_well_formed() {
        return Err(EngineError::StorageFailure);
    }
    if incoming_version.context().contains(current_version.dot()) {
        return Ok((Some(record.clone()), None));
    }
    if current_version.context().contains(incoming_version.dot()) {
        return Ok((None, None));
    }
    if current.body.as_ref() == Some(&record.body) && current.deleted == record.deleted {
        return Ok((None, None));
    }
    let current_stamp = (current.hlc, current.origin.as_str());
    let incoming_stamp = (record.hlc, record.origin.as_str());
    let mut winner = record.clone();
    let (current_wins, policy, explanation) = if !record.deleted
        && !current.deleted
        && let (Some(DocumentValue::Object(local)), DocumentValue::Object(remote)) =
            (current.body.as_ref(), &record.body)
    {
        let (merged, policy, explanation) = merge::merge_object_fields(
            local,
            current.hlc,
            &current.origin,
            &current.commit_ulid,
            remote,
            record.hlc,
            &record.origin,
            &record.commit_ulid,
            schema,
        );
        winner.body = DocumentValue::Object(merged);
        let current_wins = current.body.as_ref() == Some(&winner.body);
        if current_stamp > incoming_stamp {
            winner.hlc = current.hlc;
            winner.origin.clone_from(&current.origin);
            winner.origin_sequence = current.origin_sequence;
        }
        (current_wins, policy, explanation)
    } else {
        let current_wins = current_stamp > incoming_stamp
            || (current_stamp == incoming_stamp && current.deleted && !record.deleted);
        let policy = if current_stamp == incoming_stamp {
            storage::MergePolicy::RemoveWins
        } else {
            storage::MergePolicy::HlcTiebreak
        };
        let (hlc, origin) = if current_wins { current_stamp } else { incoming_stamp };
        let explanation = format!(
            "merge {} (hlc {}, origin '{}') vs {} (hlc {}, origin '{}'): policy {}; winner (hlc {}, origin '{}')",
            current.commit_ulid,
            current.hlc,
            current.origin,
            record.commit_ulid,
            record.hlc,
            record.origin,
            policy.as_str(),
            hlc,
            origin
        );
        (current_wins, policy, explanation)
    };
    winner.context = merge::encode_context(&current_version.joined_context(&incoming_version));
    let conflict = if current_wins {
        storage::MergeConflictEntry {
            loser_commit_ulid: record.commit_ulid.clone(),
            loser_hlc: record.hlc,
            loser_origin: record.origin.clone(),
            loser_deleted: record.deleted,
            winner_commit_ulid: current.commit_ulid.clone(),
            winner_hlc: current.hlc,
            winner_origin: current.origin.clone(),
            policy: policy.as_str().to_owned(),
            explanation,
        }
    } else {
        storage::MergeConflictEntry {
            loser_commit_ulid: current.commit_ulid.clone(),
            loser_hlc: current.hlc,
            loser_origin: current.origin.clone(),
            loser_deleted: current.deleted,
            winner_commit_ulid: record.commit_ulid.clone(),
            winner_hlc: winner.hlc,
            winner_origin: winner.origin.clone(),
            policy: policy.as_str().to_owned(),
            explanation,
        }
    };
    Ok(((!current_wins).then_some(winner), Some(conflict)))
}
