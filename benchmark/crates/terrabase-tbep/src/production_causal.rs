//! Scoped journal shipping for the authenticated production QUIC connection.
//! Receipts are returned to the runtime only after apply; the runtime must
//! establish regional RF2 durability before releasing them to the peer.

use crate::host::HostingError;
use crate::registry::BoundScope;
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

static RETAINED_BYTES: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
const HOST_RETAINED_BYTES: usize = 256 * 1024 * 1024;
struct Reservation(usize);
impl Reservation {
    fn acquire(bytes: usize) -> Result<Self, HostingError> {
        RETAINED_BYTES
            .fetch_update(
                std::sync::atomic::Ordering::AcqRel,
                std::sync::atomic::Ordering::Acquire,
                |used| used.checked_add(bytes).filter(|next| *next <= HOST_RETAINED_BYTES),
            )
            .map_err(|_| refused("host causal retained byte budget exhausted"))?;
        Ok(Self(bytes))
    }
}
impl Drop for Reservation {
    fn drop(&mut self) {
        RETAINED_BYTES.fetch_sub(self.0, std::sync::atomic::Ordering::AcqRel);
    }
}

struct Pending {
    _reservation: Reservation,
    bytes: Vec<u8>,
    checkpoint: Option<u64>,
    operations: Vec<String>,
    receipt_hash: String,
    digest: [u8; 32],
    sequences: BTreeMap<String, u64>,
    sent_at: u64,
    acknowledged: bool,
    next_offset: usize,
}

/// Result of processing one authenticated, scoped frame.
pub enum CausalReceive {
    /// A fragment is retained until its complete publication arrives.
    Fragment,
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
struct Assembly {
    _reservation: Reservation,
    hash: [u8; 32],
    bytes: Vec<u8>,
    total: usize,
    received: usize,
    last_activity: u64,
    chunks: std::collections::BTreeSet<usize>,
}

/// Bounded scoped publication sender and receiver.
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
    repair_authorizations: BTreeMap<(String, u64), [u8; 32]>,
    assembly: Option<Assembly>,
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
            repair_authorizations: BTreeMap::new(),
            assembly: None,
        })
    }

    /// Peer frontier proved by contiguously acknowledged source journal pages.
    #[must_use]
    pub fn durable_peer_frontier(&self) -> &BTreeMap<String, u64> {
        &self.durable_peer_frontier
    }

    /// Authorizes a missing source group proved by a correlated repair leaf.
    /// The caller must admit the repair peer and source Region through placement.
    /// # Errors
    /// Refuses overflow or conflicting hash attestations without dropping entries.
    pub fn authorize_repair(
        &mut self,
        origin: String,
        sequence: u64,
        hash: [u8; 32],
    ) -> Result<(), HostingError> {
        let key = (origin, sequence);
        if let Some(old) = self.repair_authorizations.get(&key) {
            return if *old == hash {
                Ok(())
            } else {
                Err(refused("conflicting repair authorization"))
            };
        }
        if self.repair_authorizations.len() >= 256 {
            return Err(refused("repair authorization queue full"));
        }
        self.repair_authorizations.insert(key, hash);
        Ok(())
    }

    /// Releases repair permissions after the runtime proves the repair round
    /// complete and all related durable receipts have been delivered.
    pub fn clear_repair_authorizations(&mut self) {
        self.repair_authorizations.clear();
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
        if self.has_pending() || self.assembly.is_some() {
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
        for pending in self.pending.values_mut() {
            if pending.acknowledged {
                continue;
            }
            if pending.next_offset >= pending.bytes.len()
                && now_ms.saturating_sub(pending.sent_at) >= RETRY_MS
            {
                pending.next_offset = 0;
            }
            if pending.next_offset < pending.bytes.len() {
                pending.sent_at = now_ms;
                return Ok(Some(fragment(&self.prefix, pending)));
            }
        }
        if self.pending.values().any(|p| !p.acknowledged && p.bytes.len() > CHUNK) {
            return Ok(None);
        }
        if self.pending.len() >= WINDOW
            || self.pending.values().map(|p| p.bytes.len()).sum::<usize>() >= 64 * 1024 * 1024
        {
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
    /// Refuses exhausted credit, invalid metadata, and oversized publications.
    pub fn publication_frame(
        &mut self,
        publication: &ReplicatedPublication,
        now_ms: u64,
    ) -> Result<Vec<u8>, HostingError> {
        if self.pending.len() >= WINDOW
            || self.pending.values().any(|p| !p.acknowledged && p.bytes.len() > CHUNK)
        {
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
        let mut bytes = self.prefix.clone();
        bytes.push(1);
        bytes.extend_from_slice(&self.next_id.to_be_bytes());
        bytes.extend_from_slice(&compress_publication(
            &publication.encode_wire().map_err(failure)?,
        )?);
        if self.pending.values().map(|p| p.bytes.len()).sum::<usize>().saturating_add(bytes.len())
            > 65 * 1024 * 1024
        {
            return Err(refused("causal pending byte budget exhausted"));
        }
        let mut pending = Pending {
            _reservation: Reservation::acquire(bytes.len())?,
            receipt_hash: blake3::hash(&bytes).to_hex().to_string(),
            digest: *blake3::hash(&bytes).as_bytes(),
            bytes,
            checkpoint,
            operations: vec![publication.commit_ulid.clone()],
            sequences: if checkpoint.is_some() {
                BTreeMap::from([(publication.origin.clone(), publication.origin_sequence)])
            } else {
                BTreeMap::new()
            },
            sent_at: now_ms,
            acknowledged: false,
            next_offset: 0,
        };
        let frame = fragment(&self.prefix, &mut pending);
        self.pending.insert(self.next_id, pending);
        self.next_id =
            self.next_id.checked_add(1).ok_or_else(|| refused("causal identifiers exhausted"))?;
        Ok(frame)
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
    /// # Panics
    /// Internal reassembly invariants panic if assembly state is concurrently
    /// corrupted, which cannot occur through this single-driver API.
    pub fn receive_frame(
        &mut self,
        engine: &Engine,
        bytes: &[u8],
        now_ms: u64,
    ) -> Result<CausalReceive, HostingError> {
        if bytes.len() > 65 * 1024 * 1024 {
            return Err(refused("causal frame exceeds publication bound"));
        }
        let body = bytes
            .strip_prefix(self.prefix.as_slice())
            .ok_or_else(|| refused("causal frame scope or placement mismatch"))?;
        let (&kind, mut body) = body.split_first().ok_or_else(|| refused("empty causal frame"))?;
        if kind == 2 {
            return self.receive_fragment(engine, &mut body, now_ms);
        }
        if kind == 1 {
            return self.receive_publication(engine, body, bytes);
        }
        if kind != 0 {
            return Err(refused("unknown causal frame kind"));
        }
        let (envelope, _) = decode(body).map_err(failure)?;
        if envelope.source != self.peer || envelope.target != self.local {
            return Err(refused("causal receipt peer mismatch"));
        }
        match envelope.payload {
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

    fn receive_publication(
        &mut self,
        engine: &Engine,
        body: &[u8],
        frame_bytes: &[u8],
    ) -> Result<CausalReceive, HostingError> {
        let mut payload = body;
        let message_id = take_u64(&mut payload)?;
        let decoded = decompress_publication(payload)?;
        let publication = ReplicatedPublication::decode_wire(&decoded).map_err(failure)?;
        if publication.origin != self.origin
            && self
                .repair_authorizations
                .get(&(publication.origin.clone(), publication.origin_sequence))
                != Some(&publication.fingerprint().map_err(failure)?)
        {
            return Err(refused("causal source has no correlated repair authorization"));
        }
        let scope = engine.scope_for(&TenantId::try_new(&self.scope.tenant).map_err(failure)?);
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
            message_id,
            correlation_id: Some(message_id),
            causation_id: None,
            idempotency_key: None,
            source: self.local.clone(),
            target: self.peer.clone(),
            issued_at: LaneInstant::at_ticks(0),
            expires_at: None,
            range: None,
            epoch: None,
            payload: Payload::AckBatch {
                tenant: WireTenant::try_new(&self.scope.tenant).map_err(failure)?,
                reports: vec![(
                    OperationId::try_new(blake3::hash(frame_bytes).to_hex().as_str())
                        .map_err(failure)?,
                    true,
                )],
            },
        })?;
        Ok(CausalReceive::Applied { receipt, operations })
    }

    fn receive_fragment(
        &mut self,
        engine: &Engine,
        body: &mut &[u8],
        now_ms: u64,
    ) -> Result<CausalReceive, HostingError> {
        let total = usize::try_from(take_u64(body)?).map_err(failure)?;
        let offset = usize::try_from(take_u64(body)?).map_err(failure)?;
        let hash: [u8; 32] = body
            .get(..32)
            .ok_or_else(|| refused("truncated fragment hash"))?
            .try_into()
            .map_err(failure)?;
        *body = &body[32..];
        let chunk_hash = body.get(..32).ok_or_else(|| refused("truncated chunk hash"))?;
        *body = &body[32..];
        if blake3::hash(body).as_bytes() != chunk_hash
            || total > 65 * 1024 * 1024
            || total == 0
            || offset % CHUNK != 0
            || body.len() != CHUNK.min(total.saturating_sub(offset))
            || offset >= total
        {
            return Err(refused("invalid causal fragment"));
        }
        if self.assembly.as_ref().is_some_and(|a| now_ms.saturating_sub(a.last_activity) > 30_000) {
            self.assembly = None;
        }
        if self.assembly.as_ref().is_some_and(|a| a.hash != hash) {
            return Err(refused("causal reassembly occupied"));
        }
        if self.assembly.is_none() {
            let reservation = Reservation::acquire(total)?;
            self.assembly = Some(Assembly {
                _reservation: reservation,
                hash,
                bytes: vec![0; total],
                total,
                received: 0,
                last_activity: now_ms,
                chunks: std::collections::BTreeSet::new(),
            });
        }
        let assembly = self.assembly.as_mut().expect("assembly exists");
        assembly.last_activity = now_ms;
        if assembly.total != total {
            return Err(refused("causal fragment total mismatch"));
        }
        if assembly.chunks.insert(offset) {
            assembly.bytes[offset..offset + body.len()].copy_from_slice(body);
            assembly.received += body.len();
        }
        if assembly.received != total {
            return Ok(CausalReceive::Fragment);
        }
        let assembly = self.assembly.take().expect("assembly exists");
        if blake3::hash(&assembly.bytes).as_bytes() != &assembly.hash {
            return Err(refused("causal assembly hash mismatch"));
        }
        if assembly.bytes.get(self.prefix.len()) != Some(&1) {
            return Err(refused("assembled causal content is not a publication"));
        }
        self.receive_frame(engine, &assembly.bytes, now_ms)
    }
}

const DICTIONARY: &[u8] = include_bytes!("../assets/causal-json-v1.dict");
fn compress_publication(raw: &[u8]) -> Result<Vec<u8>, HostingError> {
    if raw.len() > terrabase::engine::MAX_REPLICATION_PUBLICATION_BYTES {
        return Err(refused("publication decoded bound exceeded"));
    }
    let mut compressor = zstd::bulk::Compressor::with_dictionary(3, DICTIONARY).map_err(failure)?;
    let compressed = compressor.compress(raw).map_err(failure)?;
    let mut bytes = Vec::with_capacity(40 + compressed.len());
    bytes.extend_from_slice(blake3::hash(DICTIONARY).as_bytes());
    bytes.extend_from_slice(&(raw.len() as u64).to_be_bytes());
    bytes.extend_from_slice(&compressed);
    Ok(bytes)
}
fn decompress_publication(mut bytes: &[u8]) -> Result<Vec<u8>, HostingError> {
    if bytes.get(..32) != Some(blake3::hash(DICTIONARY).as_bytes().as_slice()) {
        return Err(refused("publication dictionary version mismatch"));
    }
    bytes = &bytes[32..];
    let expected = usize::try_from(take_u64(&mut bytes)?).map_err(failure)?;
    if expected > terrabase::engine::MAX_REPLICATION_PUBLICATION_BYTES {
        return Err(refused("publication decompression bound exceeded"));
    }
    let mut decompressor =
        zstd::bulk::Decompressor::with_dictionary(DICTIONARY).map_err(failure)?;
    let decoded = decompressor.decompress(bytes, expected).map_err(failure)?;
    if decoded.len() != expected {
        return Err(refused("publication decompressed length mismatch"));
    }
    Ok(decoded)
}

const CHUNK: usize = 32 * 1024;
fn fragment(prefix: &[u8], pending: &mut Pending) -> Vec<u8> {
    if pending.bytes.len() <= CHUNK {
        pending.next_offset = pending.bytes.len();
        return pending.bytes.clone();
    }
    let offset = pending.next_offset;
    let end = offset.saturating_add(CHUNK).min(pending.bytes.len());
    let chunk = &pending.bytes[offset..end];
    let mut frame = prefix.to_vec();
    frame.push(2);
    frame.extend_from_slice(&(pending.bytes.len() as u64).to_be_bytes());
    frame.extend_from_slice(&(offset as u64).to_be_bytes());
    frame.extend_from_slice(&pending.digest);
    frame.extend_from_slice(blake3::hash(chunk).as_bytes());
    frame.extend_from_slice(chunk);
    pending.next_offset = end;
    frame
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
    fn dictionary_compression_is_deterministic_and_bounded() {
        let raw = br#"{"collection":"orders","status":"pending","origin":"gru"}"#.repeat(1000);
        let compressed = compress_publication(&raw).unwrap();
        assert_eq!(compressed, compress_publication(&raw).unwrap());
        assert!(compressed.len() < raw.len());
        assert_eq!(decompress_publication(&compressed).unwrap(), raw);
        let mut foreign = compressed.clone();
        foreign[0] ^= 1;
        assert!(decompress_publication(&foreign).is_err());
        let mut bomb = compressed;
        bomb[32..40].copy_from_slice(&(65_u64 * 1024 * 1024).to_be_bytes());
        assert!(decompress_publication(&bomb).is_err());
    }

    fn seed_fragmented_publication(source: &Engine, tenant: &TenantId) -> String {
        let scope = source.scope_for(tenant);
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
                DocumentValue::Object(BTreeMap::from([(
                    "large".into(),
                    DocumentValue::String(
                        (0_u64..3000)
                            .map(|i| blake3::hash(&i.to_be_bytes()).to_hex().to_string())
                            .collect(),
                    ),
                )])),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
        tx.put(
            Document::new(
                DocumentId::try_new("second").unwrap(),
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
        batch.records()[0].provenance().origin().to_owned()
    }

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
        let origin = seed_fragmented_publication(&source, &tenant);
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
        let mut frames = vec![frame.clone()];
        while let Some(next) = sender.outbound_frame(&source, 0).unwrap() {
            frames.push(next);
        }
        assert!(frames.len() > 1);
        assert_eq!(sender.outbound_frame(&source, RETRY_MS).unwrap().unwrap(), frame);
        assert_rejects_invalid_frames(&mut receiver, &target, &frame, &frames[1]);
        let mut receipt = None;
        for chunk in frames.iter().rev() {
            match receiver.receive_frame(&target, chunk, 0).unwrap() {
                CausalReceive::Fragment => {
                    assert!(
                        target
                            .replication_publications(&target.scope_for(&tenant), 0, 1)
                            .unwrap()
                            .is_empty()
                    );
                }
                CausalReceive::Applied { receipt: value, .. } => {
                    receipt = Some(value);
                }
                _ => panic!("unexpected frame result"),
            }
        }
        let receipt = receipt.expect("complete group applied");
        let applied_publications =
            target.replication_publications(&target.scope_for(&tenant), 0, 1).unwrap();
        assert_eq!(applied_publications[0].1.records.len(), 2);
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

    fn assert_rejects_invalid_frames(
        receiver: &mut CausalDriver,
        target: &Engine,
        frame: &[u8],
        damaged_frame: &[u8],
    ) {
        let mut wrong_database = frame.to_vec();
        wrong_database[12] ^= 1;
        assert!(receiver.receive_frame(target, &wrong_database, 0).is_err());
        let mut damaged = damaged_frame.to_vec();
        let last = damaged.len() - 1;
        damaged[last] ^= 1;
        assert!(receiver.receive_frame(target, &damaged, 0).is_err());
    }
}
