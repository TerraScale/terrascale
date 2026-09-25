//! Certified remote D1 apply. This module owns document durability only; it
//! neither sends an acknowledgment nor promotes a server's applied readiness.

use std::collections::BTreeMap;
use std::sync::Mutex;

use sha2::{Digest, Sha256};
use tb_core::{
    Attribution, CommitSeq, MemoryCategory, OriginId, ReadSnapshot, Reservation, TransactionId,
};
use tb_format::{
    AppliedIdentity, DocumentEventId, DocumentKind, FormatError, StatePointer, WalOperation,
    decode_applied_identity, decode_catalog, decode_change, decode_document, encode_applied_key,
    encode_applied_value, encode_causal_manifest_key, encode_state_pointer,
    validate_document_schema,
};
use tb_replication::{
    AppendFailureKind, CertifiedEvent, CommitEvent, DurablePrefix, DurableReadKind,
    MAX_DISTRIBUTED_METADATA_BYTES, MAX_WAL_GROUP_BYTES, MetadataError, ReceiveError,
    ReplicaReceiver, VerifiedSourceChange, compute_event_digest, decode_metadata, verify_incoming,
};
use tb_storage::{
    Mutation, PlannedBatch, PreparedBatchOutcome, StageWorkPlan, StageWorkReservation, StorageError,
};

use crate::distributed::{DistributedDocumentStore, DistributedWriterState};
use crate::distributed_state::{
    check_signed_binding, effective_limits, load_manifest, merge_manifest,
};
use crate::{DocumentError, key};

const MAX_DURABLE_GROUP_BYTES: usize = 8 + MAX_DISTRIBUTED_METADATA_BYTES + MAX_WAL_GROUP_BYTES;
const FINAL_NONCE_DOMAIN: &[u8] = b"TBDOC-RECEIVER-FINAL-v1\0";
const STAGE_NONCE_DOMAIN: &[u8] = b"TBDOC-RECEIVER-D6-v1\0";

/// Document-engine state only. No variant authorizes a network `AppliedAck`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DocumentApplyOutcome {
    Applied { sequence: u64 },
    Duplicate { sequence: u64 },
    DurableOnly { durable_prefix: u64 },
}

struct ReceiverRow {
    collection: tb_core::CollectionId,
    document: tb_core::DocumentId,
    observed: tb_replication::CausalContext,
    source: Mutation,
    pointer: Mutation,
    manifest_sha256: [u8; 32],
    stage_missing: bool,
}

struct ReceiverStage {
    row_index: usize,
    token: TransactionId,
    manifest_sha256: [u8; 32],
}

struct ReceiverPlan {
    event: DocumentEventId,
    identity: AppliedIdentity,
    final_token: TransactionId,
    rows: Vec<ReceiverRow>,
    stages: Vec<ReceiverStage>,
    stage_plans: Vec<PlannedBatch>,
    final_mutations: Vec<Mutation>,
    _work: Reservation,
}

fn event_identity(event: &CommitEvent) -> AppliedIdentity {
    let id = event.id();
    AppliedIdentity {
        event: DocumentEventId {
            database: id.database(),
            shard: id.shard(),
            origin: id.origin(),
            sequence: id.sequence(),
        },
        digest: event.digest().to_bytes(),
        epoch: event.epoch(),
        configuration: event.configuration().to_bytes(),
    }
}

fn source_parts(row: &ReceiverRow) -> (&[u8], &[u8]) {
    let Mutation::Put { key, value } = &row.source else {
        unreachable!("receiver source D1 is Put")
    };
    (key.as_bytes(), value)
}

fn fresh_final_token(
    store: &DistributedDocumentStore,
    identity: AppliedIdentity,
    snapshot: ReadSnapshot,
) -> Result<TransactionId, DocumentError> {
    let window = store.config.storage.max_retry_window_ms.min(60_000);
    if window == 0 {
        return Err(DocumentError::Capacity("receiver retry window"));
    }
    let expires = store
        .storage
        .now_unix_ms()
        .checked_add(window)
        .ok_or(DocumentError::Capacity("receiver retry expiry"))?;
    let mut hash = Sha256::new();
    hash.update(FINAL_NONCE_DOMAIN);
    hash.update(identity.event.database.to_bytes());
    hash.update(identity.event.shard.get().to_le_bytes());
    hash.update(identity.event.origin.replica_id.to_bytes());
    hash.update(identity.event.origin.incarnation);
    hash.update(identity.event.sequence.to_le_bytes());
    hash.update(identity.digest);
    hash.update(snapshot.seq.get().to_le_bytes());
    hash.update(expires.to_le_bytes());
    let digest: [u8; 32] = hash.finalize().into();
    Ok(TransactionId::new(
        expires,
        digest[..16].try_into().expect("digest width"),
    ))
}

fn stage_token(
    final_token: TransactionId,
    index: usize,
    sha: [u8; 32],
) -> Result<TransactionId, DocumentError> {
    let mut hash = Sha256::new();
    hash.update(STAGE_NONCE_DOMAIN);
    hash.update(final_token.to_bytes());
    hash.update((index as u64).to_le_bytes());
    hash.update(sha);
    let digest: [u8; 32] = hash.finalize().into();
    let token = TransactionId::new(
        final_token.expires_at_unix_ms(),
        digest[..16].try_into().expect("digest width"),
    );
    if token == final_token {
        return Err(DocumentError::Capacity("receiver stage token collision"));
    }
    Ok(token)
}

fn lock_inbox(
    inbox: &Mutex<ReplicaReceiver>,
) -> Result<std::sync::MutexGuard<'_, ReplicaReceiver>, DocumentError> {
    inbox.lock().map_err(|_| DocumentError::NeedsRecovery)
}

fn append_error(error: ReceiveError) -> DocumentError {
    match error {
        ReceiveError::Append {
            kind: AppendFailureKind::IncomingConflict,
            ..
        } => DocumentError::IncomingConflict,
        other => other.into(),
    }
}

fn append_failure_requires_recovery(error: &DocumentError) -> bool {
    !matches!(
        error,
        DocumentError::IncomingConflict
            | DocumentError::Receive(ReceiveError::Append {
                kind: AppendFailureKind::Unavailable,
                ..
            })
    )
}

fn poison_on_stored_state_error(writer: &mut DistributedWriterState, error: &DocumentError) {
    let malformed = |error: &FormatError| {
        !matches!(
            error,
            FormatError::LimitExceeded { .. } | FormatError::Allocation
        )
    };
    let malformed_metadata = |error: &MetadataError| match error {
        MetadataError::Admission(_)
        | MetadataError::Allocation
        | MetadataError::Limit(_)
        | MetadataError::Replication(tb_replication::ReplicationError::LimitExceeded(_)) => false,
        MetadataError::Format(format) => malformed(format),
        _ => true,
    };
    if matches!(error, DocumentError::Corrupt(_))
        || matches!(error, DocumentError::Format(format) if malformed(format))
        || matches!(error, DocumentError::Metadata(metadata) if malformed_metadata(metadata))
        || matches!(
            error,
            DocumentError::Receive(ReceiveError::DurableRead {
                kind: DurableReadKind::StoredFault | DurableReadKind::NeedsRecovery,
                ..
            })
        )
        || matches!(
            error,
            DocumentError::Storage(StorageError::Corrupt(_) | StorageError::Poisoned)
        )
        || matches!(error, DocumentError::Storage(StorageError::Format(format)) if malformed(format))
    {
        writer.poisoned = true;
    }
}

pub(crate) fn poison_on_applied_read_error(
    writer: &mut DistributedWriterState,
    error: &DocumentError,
) {
    let unavailable = matches!(
        error,
        DocumentError::Admission(_)
            | DocumentError::Capacity(_)
            | DocumentError::Core(tb_core::CoreError::CapacityExceeded { .. })
            | DocumentError::Format(FormatError::Allocation)
            | DocumentError::Storage(
                StorageError::Admission(_)
                    | StorageError::Capacity(_)
                    | StorageError::Io(_)
                    | StorageError::Core(tb_core::CoreError::CapacityExceeded { .. })
                    | StorageError::Format(FormatError::Allocation)
            )
    );
    if !unavailable {
        writer.poisoned = true;
    }
}

fn recovered_dependencies_ready(
    event: &CommitEvent,
    local_origin: OriginId,
    local_sequence: u64,
    applied_prefix: &BTreeMap<OriginId, u64>,
) -> bool {
    event.changes().iter().all(|change| {
        change.observed.entries().all(|(origin, sequence)| {
            let ready = if origin == local_origin {
                local_sequence
            } else {
                applied_prefix.get(&origin).copied().unwrap_or(0)
            };
            sequence <= ready
        })
    })
}

impl DistributedDocumentStore {
    fn inbox_for(&self, origin: OriginId) -> Result<&Mutex<ReplicaReceiver>, DocumentError> {
        self.inboxes
            .get()
            .and_then(|inboxes| inboxes.get(&origin))
            .ok_or(DocumentError::NeedsRecovery)
    }

    fn checked_remote_prefix(
        &self,
        writer: &mut DistributedWriterState,
        origin: OriginId,
    ) -> Result<DurablePrefix, DocumentError> {
        let view = self.authority.view();
        if origin == view.local_origin() || !view.members().any(|member| member.origin == origin) {
            return Err(DocumentError::IncomingConflict);
        }
        let receiver = lock_inbox(self.inbox_for(origin)?)?;
        if receiver.validate_view(view).is_err() {
            writer.poisoned = true;
            return Err(DocumentError::Corrupt(
                "installed receiver authority mismatch",
            ));
        }
        let prefix = receiver.durable_prefix();
        if prefix.origin != origin
            || prefix.epoch != view.epoch()
            || prefix.configuration != view.configuration()
        {
            writer.poisoned = true;
            return Err(DocumentError::Corrupt("installed receiver scope mismatch"));
        }
        if prefix.sequence < writer.applied_prefix.get(&origin).copied().unwrap_or(0) {
            writer.poisoned = true;
            return Err(DocumentError::Corrupt("recovered D5 ahead of inbox"));
        }
        Ok(prefix)
    }

    /// Return only the contiguous synced inbox prefix for a signed remote
    /// origin. The prefix says nothing about document apply or read readiness.
    ///
    /// # Errors
    /// Rejects a foreign origin, an uninstalled receiver, or poisoned state.
    pub fn durable_prefix(&self, origin: OriginId) -> Result<DurablePrefix, DocumentError> {
        let mut writer = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if writer.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        self.checked_remote_prefix(&mut writer, origin)
    }

    /// Return the contiguous target D5 prefix for one signed remote origin.
    /// Zero is valid after reconciliation when no remote event was applied.
    /// This accessor does not authorize a network `AppliedAck`.
    ///
    /// # Errors
    /// Rejects an unreconciled or poisoned owner, foreign origin, missing
    /// receiver, or D5 state ahead of its exact durable inbox.
    pub fn applied_prefix(&self, origin: OriginId) -> Result<u64, DocumentError> {
        let mut writer = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if writer.poisoned || !writer.inboxes_reconciled {
            return Err(DocumentError::NeedsRecovery);
        }
        self.checked_remote_prefix(&mut writer, origin)?;
        Ok(writer.applied_prefix.get(&origin).copied().unwrap_or(0))
    }

    /// Install one fixed receiver for every signed remote origin. Ownership
    /// remains with this store so reconciliation cannot be invalidated by a
    /// caller replacing an inbox after the gate opens.
    ///
    /// # Errors
    /// Rejects an incomplete, duplicate, foreign, or second installation.
    pub fn install_receivers(&self, receivers: Vec<ReplicaReceiver>) -> Result<(), DocumentError> {
        let writer = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if writer.poisoned || self.inboxes.get().is_some() {
            return Err(DocumentError::NeedsRecovery);
        }
        let view = self.authority.view();
        let mut inboxes = BTreeMap::new();
        for receiver in receivers {
            receiver.validate_view(view)?;
            let scope = receiver.durable_prefix();
            if scope.origin == view.local_origin()
                || scope.epoch != view.epoch()
                || scope.configuration != view.configuration()
                || !view.members().any(|member| member.origin == scope.origin)
                || inboxes.insert(scope.origin, Mutex::new(receiver)).is_some()
            {
                return Err(DocumentError::Corrupt(
                    "receiver installation scope or duplicate",
                ));
            }
        }
        if inboxes.len()
            != view
                .members()
                .filter(|member| member.origin != view.local_origin())
                .count()
        {
            return Err(DocumentError::Corrupt(
                "receiver installation missing member",
            ));
        }
        self.inboxes
            .set(inboxes)
            .map_err(|_| DocumentError::NeedsRecovery)?;
        Ok(())
    }

    #[allow(
        clippy::too_many_lines,
        reason = "reconciliation validates D5 identities and every durable inbox under one fail-closed recovery pass"
    )]
    fn reconcile_inboxes_inner(
        &self,
        writer: &DistributedWriterState,
    ) -> Result<(), DocumentError> {
        let view = self.authority.view();
        let inboxes = self.inboxes.get().ok_or(DocumentError::NeedsRecovery)?;
        let frontier = self.storage.snapshot();
        for member in view
            .members()
            .filter(|member| member.origin != view.local_origin())
        {
            let receiver = lock_inbox(
                inboxes
                    .get(&member.origin)
                    .ok_or(DocumentError::Corrupt("signed receiver missing"))?,
            )?;
            let durable = receiver.durable_prefix();
            if durable.origin != member.origin
                || durable.epoch != view.epoch()
                || durable.configuration != view.configuration()
                || durable.sequence
                    < writer
                        .applied_prefix
                        .get(&member.origin)
                        .copied()
                        .unwrap_or(0)
            {
                return Err(DocumentError::Corrupt("recovered D5 ahead of inbox"));
            }
        }
        let mut cursor = self.storage.committed_cursor()?;
        let mut after = CommitSeq::ZERO;
        let mut local_sequence = 0_u64;
        let mut applied_prefix = BTreeMap::<OriginId, u64>::new();
        while let Some(batch) = cursor.next(after, self.config.storage.format.max_frame_bytes)? {
            after = batch.sequence;
            let frame = batch.frame(&self.config.storage.format)?;
            let mut applied = None;
            let mut source_marker = None;
            for operation in frame.operations() {
                let WalOperation::Put { key, value } = operation? else {
                    return Err(DocumentError::Corrupt("Delete in distributed document WAL"));
                };
                match key.first() {
                    Some(0xD5) if applied.replace((key, value)).is_some() => {
                        return Err(DocumentError::Corrupt("multiple D5 in receiver final"));
                    }
                    Some(0xD2) if source_marker.replace(value).is_some() => {
                        return Err(DocumentError::Corrupt("multiple D2 in source final"));
                    }
                    _ => {}
                }
            }
            if let Some((applied_key, applied_value)) = applied {
                if source_marker.is_some() {
                    return Err(DocumentError::Corrupt("mixed source and receiver final"));
                }
                let identity = decode_applied_identity(
                    applied_key,
                    applied_value,
                    view.database(),
                    view.shard(),
                )?;
                let origin = identity.event.origin;
                let expected = applied_prefix
                    .get(&origin)
                    .copied()
                    .unwrap_or(0)
                    .checked_add(1)
                    .ok_or(DocumentError::Capacity(
                        "recovered applied sequence exhausted",
                    ))?;
                if identity.event.sequence != expected {
                    return Err(DocumentError::Corrupt("recovered D5 origin gap"));
                }
                let inbox = inboxes
                    .get(&origin)
                    .ok_or(DocumentError::Corrupt("receiver final has no signed inbox"))?;
                let receiver = lock_inbox(inbox)?;
                let group = receiver
                    .read_durable_exact(identity.event.sequence, MAX_DURABLE_GROUP_BYTES)?
                    .ok_or(DocumentError::Corrupt("recovered D5 lacks inbox marker"))?;
                let proof = verify_incoming(
                    group.metadata(),
                    group.wal_frame(),
                    group.digest(),
                    effective_limits(self.config.replication),
                    &self.budget,
                    Attribution {
                        tenant: self.config.storage.tenant,
                        worker: self.config.storage.worker,
                        category: MemoryCategory::Replication,
                    },
                )?;
                receiver.certify_durable_replay(view, &group, &proof)?;
                drop(receiver);
                self.check_receiver_event(proof.event(), proof.source_changes())?;
                if event_identity(proof.event()) != identity {
                    return Err(DocumentError::Corrupt(
                        "recovered D5 inbox identity mismatch",
                    ));
                }
                let sources = proof.source_changes();
                if frame.operation_count()
                    != sources
                        .len()
                        .checked_mul(2)
                        .and_then(|n| n.checked_add(1))
                        .ok_or(DocumentError::Capacity("recovered receiver role count"))?
                {
                    return Err(DocumentError::Corrupt("recovered receiver role count"));
                }
                let mut operations = frame.operations();
                for source in sources {
                    let operation = operations
                        .next()
                        .ok_or(DocumentError::Corrupt("recovered receiver D1 missing"))??;
                    let WalOperation::Put { key, value } = operation else {
                        return Err(DocumentError::Corrupt("Delete in receiver final"));
                    };
                    if key != source.key() || value != source.value() {
                        return Err(DocumentError::Corrupt(
                            "recovered receiver D1 inbox mismatch",
                        ));
                    }
                }
                for _ in sources {
                    let operation = operations
                        .next()
                        .ok_or(DocumentError::Corrupt("recovered receiver D0 missing"))??;
                    if !matches!(operation, WalOperation::Put { key, .. } if key.first() == Some(&0xD0))
                    {
                        return Err(DocumentError::Corrupt(
                            "recovered receiver D0 role mismatch",
                        ));
                    }
                }
                let operation = operations
                    .next()
                    .ok_or(DocumentError::Corrupt("recovered receiver D5 missing"))??;
                if !matches!(operation, WalOperation::Put { key, value } if key == applied_key && value == applied_value)
                    || operations.next().is_some()
                {
                    return Err(DocumentError::Corrupt(
                        "recovered receiver D5 role mismatch",
                    ));
                }
                if !recovered_dependencies_ready(
                    proof.event(),
                    view.local_origin(),
                    local_sequence,
                    &applied_prefix,
                ) {
                    return Err(DocumentError::Corrupt("recovered receiver causal gap"));
                }
                self.check_applied_identity(frontier, identity)?;
                for source in sources {
                    let stored = self
                        .storage
                        .get(frontier, &tb_core::Key::new(source.key().to_vec())?)?
                        .ok_or(DocumentError::Corrupt("recovered receiver D1 missing"))?;
                    if stored.bytes() != source.value() {
                        return Err(DocumentError::Corrupt("recovered receiver D1 changed"));
                    }
                }
                applied_prefix.insert(origin, identity.event.sequence);
            } else if let Some(metadata_bytes) = source_marker {
                let next = local_sequence
                    .checked_add(1)
                    .ok_or(DocumentError::Capacity(
                        "recovered source sequence exhausted",
                    ))?;
                let limits = effective_limits(self.config.replication);
                let metadata = decode_metadata(metadata_bytes, limits)?;
                if metadata.header.id.origin() != view.local_origin()
                    || metadata.header.id.database() != view.database()
                    || metadata.header.id.shard() != view.shard()
                    || metadata.header.id.sequence() != next
                    || metadata.header.epoch != view.epoch()
                    || metadata.header.configuration != view.configuration()
                {
                    return Err(DocumentError::Corrupt("recovered source marker scope"));
                }
                let digest = compute_event_digest(metadata_bytes, batch.encoded_bytes())?;
                let proof = verify_incoming(
                    metadata_bytes,
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
                    return Err(DocumentError::Corrupt("recovered source identity changed"));
                }
                if !recovered_dependencies_ready(
                    proof.event(),
                    view.local_origin(),
                    local_sequence,
                    &applied_prefix,
                ) {
                    return Err(DocumentError::Corrupt("recovered source causal gap"));
                }
                local_sequence = next;
            }
        }
        if after != frontier.seq
            || local_sequence != writer.local_sequence
            || applied_prefix != writer.applied_prefix
        {
            return Err(DocumentError::Corrupt(
                "recovered inbox coupling frontier changed",
            ));
        }
        Ok(())
    }

    /// Couple every recovered D5 final to the exact durable inbox group for
    /// its fixed signed origin. This gate does not drain durable-only backlog.
    ///
    /// # Errors
    /// Rejects a missing or changed inbox, target row, causal dependency, or
    /// incomplete retained WAL scan; admission failures remain retryable.
    pub fn reconcile_inboxes(&self) -> Result<(), DocumentError> {
        let mut writer = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if writer.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        if writer.inboxes_reconciled {
            return Ok(());
        }
        if let Err(error) = self.reconcile_inboxes_inner(&writer) {
            poison_on_stored_state_error(&mut writer, &error);
            return Err(error);
        }
        writer.inboxes_reconciled = true;
        Ok(())
    }

    fn check_receiver_event(
        &self,
        event: &CommitEvent,
        sources: &[VerifiedSourceChange<'_>],
    ) -> Result<(), DocumentError> {
        let view = self.authority.view();
        let id = event.id();
        if id.database() != view.database()
            || id.shard() != view.shard()
            || id.origin() == view.local_origin()
            || event.epoch() != view.epoch()
            || event.configuration() != view.configuration()
            || !view.members().any(|member| member.origin == id.origin())
            || sources.len() != event.changes().len()
        {
            return Err(DocumentError::Corrupt("receiver certified event scope"));
        }
        for (change, source) in event.changes().iter().zip(sources) {
            if change.binding != source.binding() || change.document != source.document() {
                return Err(DocumentError::Corrupt("receiver proof change mismatch"));
            }
            let catalog = decode_catalog(check_signed_binding(view, change.binding)?)?;
            let decoded = decode_change(source.value())?;
            if decoded.kind == DocumentKind::Live {
                validate_document_schema(
                    decode_document(decoded.document_bytes)?,
                    &catalog.schema,
                )?;
            }
            for (origin, _) in change.observed.entries() {
                if !view.members().any(|member| member.origin == origin) {
                    return Err(DocumentError::Corrupt("unknown observed origin"));
                }
            }
        }
        Ok(())
    }

    fn dependencies_ready(&self, event: &CommitEvent, state: &DistributedWriterState) -> bool {
        let local = self.authority.view().local_origin();
        event.changes().iter().all(|change| {
            change.observed.entries().all(|(origin, sequence)| {
                let applied = if origin == local {
                    state.local_sequence
                } else {
                    state.applied_prefix.get(&origin).copied().unwrap_or(0)
                };
                sequence <= applied
            })
        })
    }

    fn check_applied_identity(
        &self,
        snapshot: ReadSnapshot,
        identity: AppliedIdentity,
    ) -> Result<(), DocumentError> {
        let actual = self.read_applied_identity(snapshot, identity.event)?;
        if actual != identity {
            return Err(DocumentError::Corrupt("applied D5 identity changed"));
        }
        Ok(())
    }

    fn read_applied_identity(
        &self,
        snapshot: ReadSnapshot,
        event: DocumentEventId,
    ) -> Result<AppliedIdentity, DocumentError> {
        let applied_key = key(encode_applied_key(event)?)?;
        let stored = self
            .storage
            .get(snapshot, &applied_key)?
            .ok_or(DocumentError::Corrupt("applied D5 missing"))?;
        let actual = decode_applied_identity(
            applied_key.as_bytes(),
            stored.bytes(),
            event.database,
            event.shard,
        )?;
        Ok(actual)
    }

    fn confirm_existing_duplicate(
        &self,
        writer: &mut DistributedWriterState,
        inbox: &Mutex<ReplicaReceiver>,
        certified: &CertifiedEvent<'_, '_, '_>,
    ) -> Result<u64, DocumentError> {
        let identity = event_identity(certified.event());
        // Read target D5 before entering the inbox lock.
        let stored_identity =
            match self.read_applied_identity(self.storage.snapshot(), identity.event) {
                Ok(stored) => stored,
                Err(error) => {
                    poison_on_applied_read_error(writer, &error);
                    return Err(error);
                }
            };
        let receiver = lock_inbox(inbox)?;
        let group =
            match receiver.read_durable_exact(identity.event.sequence, MAX_DURABLE_GROUP_BYTES) {
                Ok(Some(group)) => group,
                Ok(None) => {
                    writer.poisoned = true;
                    return Err(DocumentError::Corrupt(
                        "applied D5 lacks durable inbox marker",
                    ));
                }
                Err(error) => {
                    let error = error.into();
                    poison_on_stored_state_error(writer, &error);
                    return Err(error);
                }
            };
        let durable_identity = AppliedIdentity {
            event: DocumentEventId {
                database: group.id().database(),
                shard: group.id().shard(),
                origin: group.id().origin(),
                sequence: group.id().sequence(),
            },
            digest: group.digest().to_bytes(),
            epoch: group.epoch(),
            configuration: group.configuration().to_bytes(),
        };
        if durable_identity != stored_identity {
            writer.poisoned = true;
            return Err(DocumentError::Corrupt("applied D5 inbox identity changed"));
        }
        if identity != stored_identity || !certified.matches_durable_group(&group) {
            return Err(DocumentError::IncomingConflict);
        }
        Ok(receiver.durable_prefix().sequence)
    }

    #[allow(
        clippy::too_many_lines,
        reason = "receiver planning validates the entire certified event before one atomic document publication"
    )]
    fn prepare_receiver_plan(
        &self,
        event: &CommitEvent,
        sources: &[VerifiedSourceChange<'_>],
    ) -> Result<ReceiverPlan, DocumentError> {
        // Both callers validate the certified event before planning. Errors
        // below can be classified as stored-state faults at those call sites.
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        let snapshot = self.storage.snapshot();
        let identity = event_identity(event);
        let final_token = fresh_final_token(self, identity, snapshot)?;
        let max_manifest = 52_usize
            .checked_add(40 * limits.max_origins)
            .and_then(|n| n.checked_add(143 * limits.max_siblings))
            .ok_or(DocumentError::Capacity("receiver manifest work bound"))?;
        let source_bytes = sources
            .iter()
            .try_fold(0_usize, |total, source| {
                total
                    .checked_add(source.key().len())?
                    .checked_add(source.value().len())
            })
            .ok_or(DocumentError::Capacity("receiver source work bound"))?;
        let work_bytes = source_bytes
            .checked_mul(3)
            .and_then(|n| {
                n.checked_add(
                    sources
                        .len()
                        .checked_mul(max_manifest.checked_mul(3)?.checked_add(2_048)?)?,
                )
            })
            .ok_or(DocumentError::Capacity("receiver work bound"))?;
        let work = self.budget.try_reserve(
            Attribution {
                tenant: self.config.storage.tenant,
                worker: self.config.storage.worker,
                category: MemoryCategory::WriteBuffer,
            },
            work_bytes,
        )?;
        let mut rows = Vec::new();
        let mut stages = Vec::new();
        let mut stage_plans = Vec::new();
        let mut final_mutations = Vec::new();
        rows.try_reserve_exact(sources.len())
            .map_err(|_| DocumentError::Capacity("receiver rows"))?;
        stages
            .try_reserve_exact(sources.len())
            .map_err(|_| DocumentError::Capacity("receiver stages"))?;
        stage_plans
            .try_reserve_exact(sources.len())
            .map_err(|_| DocumentError::Capacity("receiver plans"))?;
        final_mutations
            .try_reserve_exact(sources.len() * 2 + 1)
            .map_err(|_| DocumentError::Capacity("receiver final operations"))?;
        for (index, (change, source)) in event.changes().iter().zip(sources).enumerate() {
            let source_key = tb_core::Key::new(source.key().to_vec())?;
            if let Some(stored) = self.storage.get(snapshot, &source_key)? {
                return Err(if stored.bytes() == source.value() {
                    DocumentError::Corrupt("receiver D1 exists without D5")
                } else {
                    DocumentError::Corrupt("receiver D1 exact-byte collision")
                });
            }
            let source_mutation = Mutation::Put {
                key: source_key,
                value: source.value().to_vec(),
            };
            let (manifest, sha) = merge_manifest(
                &self.storage,
                &self.budget,
                &self.config.storage,
                view,
                limits,
                snapshot,
                change.binding.collection,
                change.document,
                &change.observed,
                source.key(),
                source.value(),
            )?;
            let manifest_key = key(encode_causal_manifest_key(
                change.binding.collection,
                change.document,
                sha,
            ))?;
            let stage_missing = match self.storage.get(snapshot, &manifest_key)? {
                Some(value) if value.bytes() == manifest => false,
                Some(_) => return Err(DocumentError::Corrupt("receiver D6 hash collision")),
                None => true,
            };
            if stage_missing {
                let token = stage_token(final_token, index, sha)?;
                let mutation = Mutation::Put {
                    key: manifest_key,
                    value: manifest,
                };
                stage_plans.push(PlannedBatch::exact(
                    &self.config.storage.format,
                    token,
                    &[mutation],
                )?);
                stages.push(ReceiverStage {
                    row_index: index,
                    token,
                    manifest_sha256: sha,
                });
            }
            let (pointer_key, pointer_value) = encode_state_pointer(StatePointer {
                collection: change.binding.collection,
                document: change.document,
                manifest_sha256: sha,
            });
            rows.push(ReceiverRow {
                collection: change.binding.collection,
                document: change.document,
                observed: change.observed.clone(),
                source: source_mutation,
                pointer: Mutation::Put {
                    key: key(pointer_key)?,
                    value: pointer_value.to_vec(),
                },
                manifest_sha256: sha,
                stage_missing,
            });
        }
        for row in &rows {
            let (source_key, source_value) = source_parts(row);
            final_mutations.push(Mutation::Put {
                key: tb_core::Key::new(source_key.to_vec())?,
                value: source_value.to_vec(),
            });
        }
        for row in &rows {
            let Mutation::Put {
                key: pointer_key,
                value,
            } = &row.pointer
            else {
                unreachable!()
            };
            final_mutations.push(Mutation::Put {
                key: pointer_key.clone(),
                value: value.clone(),
            });
        }
        final_mutations.push(Mutation::Put {
            key: key(encode_applied_key(identity.event)?)?,
            value: encode_applied_value(identity)?.to_vec(),
        });
        // The exact final plan is admitted alongside every D6 stage before
        // the first stage write. Source D1 bytes are never re-encoded.
        PlannedBatch::exact(&self.config.storage.format, final_token, &final_mutations)?;
        Ok(ReceiverPlan {
            event: identity.event,
            identity,
            final_token,
            rows,
            stages,
            stage_plans,
            final_mutations,
            _work: work,
        })
    }

    fn stage_receiver_manifests(
        &self,
        plan: &ReceiverPlan,
        work: &mut StageWorkReservation<'_>,
        writer: &mut DistributedWriterState,
    ) -> Result<(), DocumentError> {
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        for stage in &plan.stages {
            let row = &plan.rows[stage.row_index];
            if !row.stage_missing {
                writer.poisoned = true;
                return Err(DocumentError::Corrupt("receiver D6 stage changed"));
            }
            let (source_key, source_value) = source_parts(row);
            let (manifest, sha) = match merge_manifest(
                &self.storage,
                &self.budget,
                &self.config.storage,
                view,
                limits,
                self.storage.snapshot(),
                row.collection,
                row.document,
                &row.observed,
                source_key,
                source_value,
            ) {
                Ok(merged) => merged,
                Err(error) => {
                    poison_on_stored_state_error(writer, &error);
                    return Err(error);
                }
            };
            if sha != stage.manifest_sha256 || sha != row.manifest_sha256 {
                writer.poisoned = true;
                return Err(DocumentError::Corrupt("receiver D6 regeneration changed"));
            }
            let mutation = Mutation::Put {
                key: key(encode_causal_manifest_key(
                    row.collection,
                    row.document,
                    sha,
                ))?,
                value: manifest,
            };
            if let Err(error) = work.commit_stage(self.storage.snapshot(), stage.token, &[mutation])
            {
                if matches!(error, StorageError::Io(_)) {
                    writer.poisoned = true;
                }
                let error = error.into();
                poison_on_stored_state_error(writer, &error);
                return Err(error);
            }
        }
        Ok(())
    }

    fn verify_published(&self, plan: &ReceiverPlan) -> Result<(), DocumentError> {
        let snapshot = self.storage.snapshot();
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        self.check_applied_identity(snapshot, plan.identity)?;
        for row in &plan.rows {
            let (source_key, source_value) = source_parts(row);
            let stored = self
                .storage
                .get(snapshot, &tb_core::Key::new(source_key.to_vec())?)?
                .ok_or(DocumentError::Corrupt("published receiver D1 missing"))?;
            if stored.bytes() != source_value {
                return Err(DocumentError::Corrupt("published receiver D1 changed"));
            }
            let Mutation::Put {
                key: pointer_key,
                value: pointer_value,
            } = &row.pointer
            else {
                unreachable!()
            };
            let pointer = self
                .storage
                .get(snapshot, pointer_key)?
                .ok_or(DocumentError::Corrupt("published receiver D0 missing"))?;
            if pointer.bytes() != pointer_value {
                return Err(DocumentError::Corrupt("published receiver D0 changed"));
            }
            let (manifest, _permit) = load_manifest(
                &self.storage,
                &self.budget,
                &self.config.storage,
                view,
                limits,
                snapshot,
                (pointer_key.as_bytes(), pointer.bytes()),
            )?;
            if !manifest
                .siblings()
                .contains(&tb_format::describe_change(source_key, source_value)?)
            {
                return Err(DocumentError::Corrupt("published D0 omits receiver D1"));
            }
        }
        Ok(())
    }

    fn publish_receiver_plan(
        &self,
        mut plan: ReceiverPlan,
        writer: &mut DistributedWriterState,
        durable_before_final: impl FnOnce() -> Result<u64, DocumentError>,
    ) -> Result<DocumentApplyOutcome, DocumentError> {
        let final_plan = PlannedBatch::exact(
            &self.config.storage.format,
            plan.final_token,
            &plan.final_mutations,
        )?;
        let work = self.storage.reserve_stage_work(StageWorkPlan::new(
            std::mem::take(&mut plan.stage_plans),
            final_plan,
        ));
        let mut work = match work {
            Ok(work) => work,
            Err(error) => {
                let error = error.into();
                poison_on_stored_state_error(writer, &error);
                return Err(error);
            }
        };
        self.stage_receiver_manifests(&plan, &mut work, writer)?;
        let final_snapshot = self.storage.snapshot();
        let prepared =
            match work.prepare_final(final_snapshot, plan.final_token, &plan.final_mutations) {
                Ok(PreparedBatchOutcome::Ready(ready)) => ready,
                Ok(PreparedBatchOutcome::AlreadyCommitted(_)) => {
                    writer.poisoned = true;
                    return Err(DocumentError::Corrupt(
                        "receiver final token already committed",
                    ));
                }
                Err(error) => {
                    if matches!(error, StorageError::Io(_)) {
                        writer.poisoned = true;
                    }
                    let error = error.into();
                    poison_on_stored_state_error(writer, &error);
                    return Err(error);
                }
            };
        // `prepared` retains the document storage-inner guard. The closure is
        // the only place this path enters the inbox lock, preserving
        // document writer -> document storage -> inbox order.
        match durable_before_final() {
            Ok(prefix) if prefix >= plan.event.sequence => prefix,
            Ok(_) => {
                writer.poisoned = true;
                drop(prepared);
                return Err(DocumentError::NeedsRecovery);
            }
            Err(error) => {
                if append_failure_requires_recovery(&error) {
                    writer.poisoned = true;
                }
                drop(prepared);
                return Err(error);
            }
        };
        if let Err(error) = prepared.commit() {
            writer.poisoned = true;
            return Err(error.into());
        }
        if let Err(error) = self.verify_published(&plan) {
            writer.poisoned = true;
            return Err(error);
        }
        writer
            .applied_prefix
            .insert(plan.event.origin, plan.event.sequence);
        Ok(DocumentApplyOutcome::Applied {
            sequence: plan.event.sequence,
        })
    }

    /// Persist or apply one live authority-certified remote event. Before
    /// all recovered inboxes are coupled, only durable admission is possible.
    ///
    /// # Errors
    /// Rejects scope/collision/capacity faults and closes this document writer
    /// after any uncertain inbox or final publication attempt.
    #[allow(
        clippy::too_many_lines,
        reason = "receive keeps duplicate, durable-only, and final-apply decisions under one writer guard"
    )]
    pub fn receive_certified(
        &self,
        certified: CertifiedEvent<'_, '_, '_>,
    ) -> Result<DocumentApplyOutcome, DocumentError> {
        let mut writer = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if writer.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        certified
            .validate_view(self.authority.view())
            .map_err(|_| DocumentError::IncomingConflict)?;
        let event = certified.event();
        self.check_receiver_event(event, certified.source_changes())?;
        let identity = event_identity(event);
        let inbox = self.inbox_for(identity.event.origin)?;
        let prefix = writer
            .applied_prefix
            .get(&identity.event.origin)
            .copied()
            .unwrap_or(0);
        if identity.event.sequence <= prefix {
            let durable_prefix = self.confirm_existing_duplicate(&mut writer, inbox, &certified)?;
            return Ok(if writer.inboxes_reconciled {
                DocumentApplyOutcome::Duplicate {
                    sequence: identity.event.sequence,
                }
            } else {
                DocumentApplyOutcome::DurableOnly { durable_prefix }
            });
        }
        {
            let receiver = lock_inbox(inbox)?;
            let durable_prefix = receiver.durable_prefix().sequence;
            if identity.event.sequence <= durable_prefix {
                let group = match receiver
                    .read_durable_exact(identity.event.sequence, MAX_DURABLE_GROUP_BYTES)
                {
                    Ok(Some(group)) => group,
                    Ok(None) => {
                        writer.poisoned = true;
                        return Err(DocumentError::Corrupt("durable inbox marker missing"));
                    }
                    Err(error) => {
                        let error = error.into();
                        poison_on_stored_state_error(&mut writer, &error);
                        return Err(error);
                    }
                };
                if !certified.matches_durable_group(&group) {
                    return Err(DocumentError::IncomingConflict);
                }
                return Ok(DocumentApplyOutcome::DurableOnly { durable_prefix });
            }
        }
        if !writer.inboxes_reconciled {
            let mut receiver = lock_inbox(inbox)?;
            let durable = match receiver.append_certified(certified) {
                Ok(durable) => durable,
                Err(error) => {
                    let error = append_error(error);
                    if append_failure_requires_recovery(&error) {
                        writer.poisoned = true;
                    }
                    return Err(error);
                }
            };
            return Ok(DocumentApplyOutcome::DurableOnly {
                durable_prefix: durable.sequence,
            });
        }
        let expected = prefix.checked_add(1).ok_or(DocumentError::Capacity(
            "receiver applied sequence exhausted",
        ))?;
        if identity.event.sequence != expected || !self.dependencies_ready(event, &writer) {
            let mut inbox = lock_inbox(inbox)?;
            let receiver = &mut *inbox;
            let result = receiver.append_certified(certified);
            return match result {
                Ok(durable) => Ok(DocumentApplyOutcome::DurableOnly {
                    durable_prefix: durable.sequence,
                }),
                Err(error) => {
                    let error = append_error(error);
                    if append_failure_requires_recovery(&error) {
                        writer.poisoned = true;
                    }
                    Err(error)
                }
            };
        }
        let plan = match self.prepare_receiver_plan(event, certified.source_changes()) {
            Ok(plan) => plan,
            Err(error) => {
                poison_on_stored_state_error(&mut writer, &error);
                return Err(error);
            }
        };
        self.publish_receiver_plan(plan, &mut writer, || {
            let mut inbox = lock_inbox(inbox)?;
            let receiver = &mut *inbox;
            Ok(receiver
                .append_certified(certified)
                .map_err(append_error)?
                .sequence)
        })
    }

    /// Apply the next exact, already-durable inbox group for this origin.
    /// `None` means the recovered contiguous inbox has no next marker.
    ///
    /// # Errors
    /// Rejects missing authority, changed bytes, capacity or uncertain final
    /// publication. Replay never appends or issues an acknowledgment.
    pub fn replay_next(
        &self,
        origin: OriginId,
    ) -> Result<Option<DocumentApplyOutcome>, DocumentError> {
        let mut writer = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if writer.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        if !writer.inboxes_reconciled {
            return Err(DocumentError::NeedsRecovery);
        }
        let inbox = self.inbox_for(origin)?;
        let (group, durable_prefix) = {
            let inbox = lock_inbox(inbox)?;
            let receiver = &*inbox;
            let origin = receiver.durable_prefix().origin;
            let applied = writer.applied_prefix.get(&origin).copied().unwrap_or(0);
            if receiver.durable_prefix().sequence < applied {
                writer.poisoned = true;
                return Err(DocumentError::Corrupt("applied D5 ahead of durable inbox"));
            }
            let next = writer
                .applied_prefix
                .get(&origin)
                .copied()
                .unwrap_or(0)
                .checked_add(1)
                .ok_or(DocumentError::Capacity(
                    "receiver replay sequence exhausted",
                ))?;
            let group = match receiver.read_durable_exact(next, MAX_DURABLE_GROUP_BYTES) {
                Ok(group) => group,
                Err(error) => {
                    let error = error.into();
                    poison_on_stored_state_error(&mut writer, &error);
                    return Err(error);
                }
            };
            (group, receiver.durable_prefix().sequence)
        };
        let Some(group) = group else { return Ok(None) };
        let limits = effective_limits(self.config.replication);
        let proof = match verify_incoming(
            group.metadata(),
            group.wal_frame(),
            group.digest(),
            limits,
            &self.budget,
            Attribution {
                tenant: self.config.storage.tenant,
                worker: self.config.storage.worker,
                category: MemoryCategory::Replication,
            },
        ) {
            Ok(proof) => proof,
            Err(error) => {
                let error = error.into();
                poison_on_stored_state_error(&mut writer, &error);
                return Err(error);
            }
        };
        {
            let inbox = lock_inbox(inbox)?;
            let receiver = &*inbox;
            if let Err(error) =
                receiver.certify_durable_replay(self.authority.view(), &group, &proof)
            {
                writer.poisoned = true;
                return Err(error.into());
            }
        }
        if let Err(error) = self.check_receiver_event(proof.event(), proof.source_changes()) {
            poison_on_stored_state_error(&mut writer, &error);
            return Err(error);
        }
        if !self.dependencies_ready(proof.event(), &writer) {
            return Ok(Some(DocumentApplyOutcome::DurableOnly { durable_prefix }));
        }
        let plan = match self.prepare_receiver_plan(proof.event(), proof.source_changes()) {
            Ok(plan) => plan,
            Err(error) => {
                poison_on_stored_state_error(&mut writer, &error);
                return Err(error);
            }
        };
        let outcome = self.publish_receiver_plan(plan, &mut writer, || {
            let inbox = lock_inbox(inbox)?;
            let receiver = &*inbox;
            let certified =
                receiver.certify_durable_replay(self.authority.view(), &group, &proof)?;
            Ok(certified.durable_group().id().sequence())
        })?;
        Ok(Some(outcome))
    }
}
