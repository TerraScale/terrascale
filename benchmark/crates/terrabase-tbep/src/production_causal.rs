//! Scoped journal shipping for the authenticated production QUIC connection.
//! Receipts are returned to the runtime only after apply; the runtime must
//! establish regional RF2 durability before releasing them to the peer.

use crate::host::HostingError;
use crate::registry::BoundScope;
use crate::replication_lanes::{replicated_record, replicated_write};
use std::collections::BTreeMap;
use terrabase::engine::{Engine, ReplicatedPublication};
use terrabase::types::TenantId;
use terrabase_member_transport::clock::LaneInstant;
use terrabase_member_transport::envelope::{Envelope, FrameKind, Payload, decode, encode};
use terrabase_member_transport::identity::{MemberId, OperationId, TenantId as WireTenant};

/// Discriminator used by the member socket dispatcher.
pub const MAGIC: &[u8; 4] = b"TBCJ";
const RETRY_MS: u64 = 2_000;
const WINDOW: usize = 8;

struct Pending {
    bytes: Vec<u8>,
    checkpoint: Option<u64>,
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
    local_origin: String,
    scope: BoundScope,
    prefix: Vec<u8>,
    next_id: u64,
    cursor: u64,
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
        local_origin: String,
        scope: BoundScope,
        placement_generation: u64,
        replica_epoch: u64,
    ) -> Result<Self, HostingError> {
        scope.validate().map_err(failure)?;
        if local == peer
            || local_origin.is_empty()
            || origin.is_empty()
            || placement_generation == 0
            || replica_epoch == 0
        {
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
            local_origin,
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
            cursor: 0,
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

    /// Whether any shipped publication still requires a peer receipt.
    #[must_use]
    pub fn has_pending(&self) -> bool {
        !self.pending.is_empty()
    }

    /// Proves the acknowledged cursor reaches the current retained outbox head.
    /// # Errors
    /// Returns durable storage or scope failures.
    pub fn is_caught_up(&self, engine: &Engine) -> Result<bool, HostingError> {
        if self.has_pending() {
            return Ok(false);
        }
        let scope = engine.scope_for(&TenantId::try_new(&self.scope.tenant).map_err(failure)?);
        Ok(engine.replication_publications(&scope, self.cursor, 1).map_err(failure)?.is_empty())
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
        let local_origin = &self.local_origin;
        let mut after =
            self.pending.values().filter_map(|p| p.checkpoint).max().unwrap_or(self.cursor);
        let scope = engine.scope_for(&TenantId::try_new(&self.scope.tenant).map_err(failure)?);
        let mut selected = None;
        for _ in 0..8 {
            let rows = engine.replication_publications(&scope, after, 1).map_err(failure)?;
            let Some((sequence, publication)) = rows.into_iter().next() else {
                return Ok(None);
            };
            if publication.origin == *local_origin {
                selected = Some((sequence, publication));
                break;
            }
            if self.pending.is_empty() {
                self.cursor = sequence;
            }
            after = sequence;
        }
        let Some((sequence, publication)) = selected else {
            return Ok(None);
        };
        let bytes = self.encode_publication(&publication, Some(sequence), now_ms)?;
        Ok(Some(bytes))
    }

    /// Ships one complete publication requested by range reconciliation.
    /// Repair receipts never move the sequential journal tail checkpoint.
    /// # Errors
    /// Refuses exhausted credit, invalid metadata, and oversized envelopes.
    pub fn publication_frame(
        &mut self,
        publication: &ReplicatedPublication,
        now_ms: u64,
    ) -> Result<Vec<u8>, HostingError> {
        if self.pending.len() >= WINDOW {
            return Err(refused("causal credit window is full"));
        }
        self.encode_publication(publication, None, now_ms)
    }

    fn encode_publication(
        &mut self,
        publication: &ReplicatedPublication,
        checkpoint: Option<u64>,
        now_ms: u64,
    ) -> Result<Vec<u8>, HostingError> {
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
                records: publication
                    .records
                    .iter()
                    .map(replicated_write)
                    .collect::<Result<Vec<_>, _>>()?,
            },
        };
        let mut bytes = self.prefix.clone();
        bytes.push(1);
        for value in [
            publication.origin.as_bytes(),
            publication.commit_ulid.as_bytes(),
            publication.context.as_slice(),
            publication.private_payload.as_slice(),
        ] {
            put_bytes(&mut bytes, value)?;
        }
        bytes.extend_from_slice(&publication.origin_sequence.to_be_bytes());
        bytes.extend_from_slice(&publication.hlc.to_be_bytes());
        bytes.extend_from_slice(&encode(&envelope).map_err(failure)?.bytes);
        self.pending.insert(
            self.next_id,
            Pending {
                receipt_hash: blake3::hash(&bytes).to_hex().to_string(),
                bytes: bytes.clone(),
                checkpoint,
                operations: vec![publication.commit_ulid.clone()],
                sequences: BTreeMap::from([(
                    publication.origin.clone(),
                    publication.origin_sequence,
                )]),
                sent_at: now_ms,
                acknowledged: false,
            },
        );
        self.next_id =
            self.next_id.checked_add(1).ok_or_else(|| refused("causal identifiers exhausted"))?;
        Ok(bytes)
    }

    fn wrap(&self, envelope: &Envelope) -> Result<Vec<u8>, HostingError> {
        let mut bytes = self.prefix.clone();
        bytes.push(0);
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
        if bytes.len() > 65 * 1024 * 1024 {
            return Err(refused("causal frame exceeds publication bound"));
        }
        let body = bytes
            .strip_prefix(self.prefix.as_slice())
            .ok_or_else(|| refused("causal frame scope or placement mismatch"))?;
        let (&kind, mut body) = body.split_first().ok_or_else(|| refused("empty causal frame"))?;
        let metadata = if kind == 1 {
            let origin = String::from_utf8(take_bytes(&mut body)?.to_vec()).map_err(failure)?;
            let commit = String::from_utf8(take_bytes(&mut body)?.to_vec()).map_err(failure)?;
            let context = take_bytes(&mut body)?.to_vec();
            let private = take_bytes(&mut body)?.to_vec();
            let sequence = take_u64(&mut body)?;
            let hlc = take_u64(&mut body)?;
            Some((origin, commit, context, private, sequence, hlc))
        } else if kind == 0 {
            None
        } else {
            return Err(refused("unknown causal frame format"));
        };
        let (envelope, _) = decode(body).map_err(failure)?;
        if envelope.source != self.peer || envelope.target != self.local {
            return Err(refused("causal frame identity mismatch"));
        }
        match envelope.payload {
            Payload::ReplicationBatch { tenant, records } => {
                if tenant.as_str() != self.scope.tenant || records.len() > 1024 {
                    return Err(refused("invalid causal batch scope or bound"));
                }
                let (origin, commit, context, private, sequence, hlc) =
                    metadata.ok_or_else(|| refused("missing publication metadata"))?;
                if origin != self.origin {
                    return Err(refused("causal source is not the admitted selected region"));
                }
                let publication = ReplicatedPublication {
                    origin,
                    origin_sequence: sequence,
                    hlc,
                    commit_ulid: commit,
                    context,
                    private_payload: private,
                    records: records
                        .iter()
                        .map(replicated_record)
                        .collect::<Result<Vec<_>, _>>()?,
                };
                let scope =
                    engine.scope_for(&TenantId::try_new(&self.scope.tenant).map_err(failure)?);
                if !engine
                    .replication_publication_dependencies_ready(&scope, &publication)
                    .map_err(failure)?
                {
                    return Ok(CausalReceive::Deferred);
                }
                engine.apply_replicated_publication(&scope, &publication).map_err(failure)?;
                let operations = vec![publication.commit_ulid.clone()];
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
                if metadata.is_some() {
                    return Err(refused("receipt carries publication metadata"));
                }
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
                    if let Some(checkpoint) = settled.checkpoint {
                        self.cursor = checkpoint;
                    }
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

fn put_bytes(out: &mut Vec<u8>, value: &[u8]) -> Result<(), HostingError> {
    if value.len() > 64 * 1024 * 1024 {
        return Err(refused("publication field too large"));
    }
    out.extend_from_slice(&u32::try_from(value.len()).map_err(failure)?.to_be_bytes());
    out.extend_from_slice(value);
    Ok(())
}
fn take_bytes<'a>(input: &mut &'a [u8]) -> Result<&'a [u8], HostingError> {
    let length = input.get(..4).ok_or_else(|| refused("truncated causal length"))?;
    let length = u32::from_be_bytes(length.try_into().map_err(failure)?) as usize;
    *input = &input[4..];
    let value = input.get(..length).ok_or_else(|| refused("truncated causal payload"))?;
    *input = &input[length..];
    Ok(value)
}
fn take_u64(input: &mut &[u8]) -> Result<u64, HostingError> {
    let value = u64::from_be_bytes(
        input
            .get(..8)
            .ok_or_else(|| refused("truncated causal sequence"))?
            .try_into()
            .map_err(failure)?,
    );
    *input = &input[8..];
    Ok(value)
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::engine::{CommitOutcome, EngineConfig};
    use terrabase::types::{Cancellation, DocumentValue};
    use terrabase::types::{
        CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability,
    };

    #[test]
    fn retry_receipt_and_database_fencing() {
        let path = std::env::temp_dir().join(format!(
            "causal-driver-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let source = Engine::open(EngineConfig::durable(path.join("source")).unwrap()).unwrap();
        let target = Engine::open(EngineConfig::durable(path.join("target")).unwrap()).unwrap();
        let tenant = TenantId::try_new("tenant").unwrap();
        let scope = source.scope_for(&tenant);
        let mut tx = source
            .begin(
                &scope,
                source.read_snapshot(&scope).unwrap(),
                terrabase::types::OperationId::try_new("operation").unwrap(),
            )
            .unwrap();
        tx.put(
            Document::new(
                DocumentId::try_new("doc").unwrap(),
                CollectionName::try_new("items").unwrap(),
                DocumentValue::Object(BTreeMap::new()),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
        assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        let batch = source
            .streams()
            .replay_sync_journal(&scope, None, 2, &Cancellation::default())
            .unwrap();
        let origin = batch.records()[0].provenance().origin().to_owned();
        let bound = BoundScope {
            principal: "member".into(),
            tenant: "tenant".into(),
            project: "project".into(),
            database: "database".into(),
        };
        let a = MemberId::try_new("a").unwrap();
        let b = MemberId::try_new("b").unwrap();
        let mut sender = CausalDriver::new(
            a.clone(),
            b.clone(),
            origin.clone(),
            origin.clone(),
            bound.clone(),
            1,
            1,
        )
        .unwrap();
        let mut receiver = CausalDriver::new(b, a, origin.clone(), origin, bound, 1, 1).unwrap();
        let frame = sender.outbound_frame(&source, 0).unwrap().unwrap();
        assert_eq!(sender.outbound_frame(&source, RETRY_MS).unwrap().unwrap(), frame);
        let mut wrong_database = frame.clone();
        wrong_database[12] ^= 1;
        assert!(receiver.receive_frame(&target, &wrong_database, 0).is_err());
        let CausalReceive::Applied { receipt, .. } =
            receiver.receive_frame(&target, &frame, 0).unwrap()
        else {
            panic!("must apply")
        };
        assert!(sender.durable_peer_frontier().is_empty());
        assert!(matches!(
            sender.receive_frame(&source, &receipt, 0).unwrap(),
            CausalReceive::Receipt { .. }
        ));
        assert!(sender.outbound_frame(&source, RETRY_MS * 2).unwrap().is_none());
        assert!(
            matches!(sender.receive_frame(&source, &receipt, 0).unwrap(), CausalReceive::Receipt { operations } if operations.is_empty())
        );
        drop(source);
        drop(target);
        std::fs::remove_dir_all(path).unwrap();
    }
}
