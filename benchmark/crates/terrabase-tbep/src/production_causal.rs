//! Scoped journal shipping for the authenticated production QUIC connection.
//! Receipts are returned to the runtime only after apply; the runtime must
//! establish regional RF2 durability before releasing them to the peer.

use crate::host::HostingError;
use crate::registry::BoundScope;
use crate::replication_lanes::{replicated_record, replicated_write};
use std::collections::BTreeMap;
use terrabase::engine::Engine;
use terrabase::streaming::ChangeCheckpoint;
use terrabase::types::{Cancellation, DocumentValue, TenantId};
use terrabase_member_transport::clock::LaneInstant;
use terrabase_member_transport::envelope::{Envelope, FrameKind, Payload, decode, encode};
use terrabase_member_transport::identity::{MemberId, OperationId, TenantId as WireTenant};

/// Discriminator used by the member socket dispatcher.
pub const MAGIC: &[u8; 4] = b"TBCJ";
const RETRY_MS: u64 = 2_000;
const WINDOW: usize = 8;

struct Pending {
    bytes: Vec<u8>,
    checkpoint: ChangeCheckpoint,
    operations: Vec<String>,
    receipt_hash: String,
    sequences: BTreeMap<String, u64>,
    sent_at: u64,
    acknowledged: bool,
}

/// Result of processing one authenticated, scoped frame.
pub enum CausalReceive {
    /// Missing causal dependencies; caller schedules catch-up and sends no receipt.
    Deferred,
    /// Applied locally. Release this receipt only after the local WAL follower
    /// durably acknowledges the apply, including replayed operations.
    Applied {
        /// Exact correlated receipt to send after RF2.
        receipt: Vec<u8>,
        /// Source operation identities covered by this receipt.
        operations: Vec<String>,
    },
    /// A peer durably accepted these previously shipped operations.
    Receipt {
        /// Exact operation identities covered by the validated receipt.
        operations: Vec<String>,
    },
}

/// Bounded pipelined journal sender and scoped apply/receipt validator.
pub struct CausalDriver {
    local: MemberId,
    peer: MemberId,
    origin: String,
    scope: BoundScope,
    prefix: Vec<u8>,
    next_id: u64,
    cursor: Option<ChangeCheckpoint>,
    pending: BTreeMap<u64, Pending>,
    durable_peer_frontier: BTreeMap<String, u64>,
    retired: BTreeMap<u64, String>,
}

fn refused(message: impl Into<String>) -> HostingError {
    HostingError::Refused(message.into())
}
fn failure(error: impl std::fmt::Debug) -> HostingError {
    HostingError::EngineFailure(format!("causal replication: {error:?}"))
}

impl CausalDriver {
    /// Binds a database lane to the authenticated selected peer and placement.
    /// # Errors
    /// Refuses invalid scope, identity, or zero placement fencing tokens.
    pub fn new(
        local: MemberId,
        peer: MemberId,
        origin: String,
        scope: BoundScope,
        placement_generation: u64,
        replica_epoch: u64,
    ) -> Result<Self, HostingError> {
        scope.validate().map_err(failure)?;
        if local == peer || origin.is_empty() || placement_generation == 0 || replica_epoch == 0 {
            return Err(refused("invalid causal lane admission"));
        }
        let mut prefix = MAGIC.to_vec();
        for value in [&scope.tenant, &scope.project, &scope.database] {
            prefix.extend_from_slice(&u32::try_from(value.len()).map_err(failure)?.to_be_bytes());
            prefix.extend_from_slice(value.as_bytes());
        }
        prefix.extend_from_slice(&placement_generation.to_be_bytes());
        prefix.extend_from_slice(&replica_epoch.to_be_bytes());
        Ok(Self {
            local,
            peer,
            origin,
            scope,
            prefix,
            next_id: u64::try_from(
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_err(failure)?
                    .as_nanos()
                    & u128::from(u64::MAX),
            )
            .map_err(failure)?,
            cursor: None,
            pending: BTreeMap::new(),
            durable_peer_frontier: BTreeMap::new(),
            retired: BTreeMap::new(),
        })
    }

    /// Peer frontier proved by contiguously acknowledged source journal pages.
    #[must_use]
    pub fn durable_peer_frontier(&self) -> &BTreeMap<String, u64> {
        &self.durable_peer_frontier
    }

    /// Produces one bounded frame, retrying timed-out unacknowledged frames.
    /// Callers send these bytes only on this driver's authenticated connection.
    /// # Errors
    /// Returns journal, encoding, or scope failures without moving progress.
    pub fn outbound_frame(
        &mut self,
        engine: &Engine,
        now_ms: u64,
    ) -> Result<Option<Vec<u8>>, HostingError> {
        if let Some(frame) = self
            .pending
            .values_mut()
            .find(|p| !p.acknowledged && now_ms.saturating_sub(p.sent_at) >= RETRY_MS)
        {
            frame.sent_at = now_ms;
            return Ok(Some(frame.bytes.clone()));
        }
        if self.pending.len() >= WINDOW {
            return Ok(None);
        }
        let after = self.pending.last_key_value().map(|(_, p)| p.checkpoint).or(self.cursor);
        let tenant = TenantId::try_new(&self.scope.tenant).map_err(failure)?;
        let scope = engine.scope_for(&tenant);
        // One row per frame preserves each retained publication checkpoint,
        // including transactions spanning a page boundary.
        let batch = engine
            .streams()
            .replay_sync_journal(&scope, after, 2, &Cancellation::default())
            .map_err(failure)?;
        let Some(row) = batch.records().first() else {
            return Ok(None);
        };
        if batch
            .records()
            .get(1)
            .is_some_and(|next| next.position().sequence() == row.position().sequence())
        {
            return Err(refused("multi-document causal publication requires atomic group apply"));
        }
        if row.provenance().kind() != terrabase::streaming::JournalKind::Origin {
            // Advance filtered rows only when no unacknowledged prefix exists.
            if self.pending.is_empty() {
                self.cursor = Some(row.position());
            }
            return Ok(None);
        }
        let (body, deleted) = match row.mutation() {
            terrabase::streaming::SyncJournalMutation::Put(value) => (value.clone(), false),
            terrabase::streaming::SyncJournalMutation::Delete => (DocumentValue::Null, true),
        };
        let provenance = row.provenance();
        let record = terrabase::engine::ReplicatedRecord {
            collection: row.collection().clone(),
            id: row.document().clone(),
            body,
            deleted,
            commit_ulid: row.operation_id().as_str().to_owned(),
            hlc: provenance.hlc(),
            origin: provenance.origin().to_owned(),
            origin_sequence: provenance.origin_sequence(),
            context: provenance.context().to_vec(),
        };
        let envelope = Envelope {
            kind: FrameKind::ReplicationBatch,
            message_id: self.next_id,
            correlation_id: None,
            causation_id: None,
            idempotency_key: None,
            source: self.local.clone(),
            target: self.peer.clone(),
            issued_at: LaneInstant::at_ticks(0),
            expires_at: None,
            range: None,
            epoch: None,
            payload: Payload::ReplicationBatch {
                tenant: WireTenant::try_new(&self.scope.tenant).map_err(failure)?,
                records: vec![replicated_write(&record)?],
            },
        };
        let bytes = self.wrap(&envelope)?;
        self.pending.insert(
            self.next_id,
            Pending {
                bytes: bytes.clone(),
                receipt_hash: blake3::hash(&bytes).to_hex().to_string(),
                checkpoint: row.position(),
                operations: vec![record.commit_ulid],
                sequences: BTreeMap::from([(record.origin, record.origin_sequence)]),
                sent_at: now_ms,
                acknowledged: false,
            },
        );
        self.next_id = self
            .next_id
            .checked_add(1)
            .ok_or_else(|| refused("causal message identifiers exhausted"))?;
        Ok(Some(bytes))
    }

    fn wrap(&self, envelope: &Envelope) -> Result<Vec<u8>, HostingError> {
        let mut bytes = self.prefix.clone();
        bytes.extend_from_slice(&encode(envelope).map_err(failure)?.bytes);
        Ok(bytes)
    }

    /// Validates a socket frame's database, placement, peer and exact receipt.
    /// # Errors
    /// Refuses unrelated or malformed frames and failed/dependency-blocked applies.
    pub fn receive_frame(
        &mut self,
        engine: &Engine,
        bytes: &[u8],
        _now_ms: u64,
    ) -> Result<CausalReceive, HostingError> {
        let body = bytes
            .strip_prefix(self.prefix.as_slice())
            .ok_or_else(|| refused("causal frame scope or placement mismatch"))?;
        let (envelope, _) = decode(body).map_err(failure)?;
        if envelope.source != self.peer || envelope.target != self.local {
            return Err(refused("causal frame identity mismatch"));
        }
        match envelope.payload {
            Payload::ReplicationBatch { tenant, records } => {
                if tenant.as_str() != self.scope.tenant || records.len() != 1 {
                    return Err(refused("invalid causal batch scope or bound"));
                }
                let record = replicated_record(&records[0])?;
                if record.origin != self.origin {
                    return Err(refused("causal source is not the admitted selected region"));
                }
                let scope =
                    engine.scope_for(&TenantId::try_new(&self.scope.tenant).map_err(failure)?);
                if !engine.replication_dependencies_ready(&scope, &record).map_err(failure)? {
                    return Ok(CausalReceive::Deferred);
                }
                engine.apply_replicated(&scope, &record).map_err(failure)?;
                let operations = vec![record.commit_ulid.clone()];
                let receipt = self.wrap(&Envelope {
                    kind: FrameKind::AckBatch,
                    message_id: envelope.message_id,
                    correlation_id: Some(envelope.message_id),
                    causation_id: None,
                    idempotency_key: None,
                    source: self.local.clone(),
                    target: self.peer.clone(),
                    issued_at: LaneInstant::at_ticks(0),
                    expires_at: None,
                    range: None,
                    epoch: None,
                    payload: Payload::AckBatch {
                        tenant,
                        reports: vec![(
                            OperationId::try_new(blake3::hash(bytes).to_hex().as_str())
                                .map_err(failure)?,
                            true,
                        )],
                    },
                })?;
                Ok(CausalReceive::Applied { receipt, operations })
            }
            Payload::AckBatch { tenant, reports } => {
                let id = envelope
                    .correlation_id
                    .ok_or_else(|| refused("uncorrelated causal receipt"))?;
                if let Some(receipt_hash) = self.retired.get(&id) {
                    if tenant.as_str() != self.scope.tenant
                        || reports.len() != 1
                        || reports
                            .iter()
                            .zip(std::iter::once(receipt_hash))
                            .any(|((op, held), expected)| !held || op.as_str() != expected)
                    {
                        return Err(refused("invalid duplicate causal receipt"));
                    }
                    return Ok(CausalReceive::Receipt { operations: Vec::new() });
                }
                let pending =
                    self.pending.get_mut(&id).ok_or_else(|| refused("unknown causal receipt"))?;
                if tenant.as_str() != self.scope.tenant
                    || reports.len() != 1
                    || reports
                        .iter()
                        .zip(std::iter::once(&pending.receipt_hash))
                        .any(|((op, held), expected)| !held || op.as_str() != expected)
                {
                    return Err(refused("causal receipt does not prove the sent operations"));
                }
                pending.acknowledged = true;
                let operations = pending.operations.clone();
                while self.pending.first_key_value().is_some_and(|(_, p)| p.acknowledged) {
                    let (settled_id, settled) = self.pending.pop_first().expect("checked nonempty");
                    self.retired.insert(settled_id, settled.receipt_hash);
                    while self.retired.len() > WINDOW * 8 {
                        self.retired.pop_first();
                    }
                    self.cursor = Some(settled.checkpoint);
                    for (origin, sequence) in settled.sequences {
                        let complete = sequence;
                        self.durable_peer_frontier
                            .entry(origin)
                            .and_modify(|v| *v = (*v).max(complete))
                            .or_insert(complete);
                    }
                }
                Ok(CausalReceive::Receipt { operations })
            }
            _ => Err(refused("unsupported causal frame kind")),
        }
    }
}
