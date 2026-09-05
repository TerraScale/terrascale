//! The versioned member envelope and its fail-closed wire codec.
//!
//! Every frame crossing a member lane is one [`Envelope`] under the
//! workspace message-contract discipline (the Orchestrator NATS spec's
//! "Message contract" section, applied to the member plane): schema name and
//! version; message, correlation, and causation identities; an idempotency
//! key where mutation is possible; source and target Members; issued and
//! expiry stamps in lane ticks; the ownership epoch whenever the frame can
//! mutate data-range state; and the payload type's content digest.
//!
//! The codec fails closed on every discipline violation: unknown schema,
//! unknown major or minor version, unknown frame kind, truncation, trailing
//! bytes, oversized frames, invalid UTF-8, and digest disagreement with the
//! payload. A frame whose expiry stamp has passed fails closed at the
//! receiving Member. No frame ever carries credentials, and there is no
//! generic byte-blob escape: every payload is one of the four typed frame
//! kinds of the W4 catch-up/push protocol plus its acknowledgement report.

use crate::clock::LaneInstant;
use crate::identity::{CollectionName, DocumentId, MemberId, OperationId, TenantId};
use ring::digest::{Context, Digest, SHA256};

/// The schema name every member envelope carries.
pub const ENVELOPE_SCHEMA: &str = "terrabase.member.lane";

/// The supported envelope major version. Breaking envelope changes bump it;
/// unknown majors fail closed.
pub const ENVELOPE_VERSION_MAJOR: u16 = 1;

/// The supported envelope minor version. Additive changes bump it; a minor
/// above the supported one fails closed rather than guessing. Minor 1
/// (SE-07) adds the batched acknowledgement report
/// ([`FrameKind::AckBatch`]); minor 2 (S-05, ADR 0041 D-35/D-36) adds the
/// journal tailer's replicated-record batch ([`FrameKind::ReplicationBatch`]);
/// minor 3 adds the durable source sequence to replicated records; minor 4
/// adds the bounded opaque causal-context bytes to each replicated record.
/// Encoders use the canonical minor for each frame kind so older peers can
/// continue to decode unchanged frame classes, while a legacy replication
/// batch at minor 3 remains decodable with an empty context.
pub const ENVELOPE_VERSION_MINOR: u16 = REPLICATION_BATCH_CONTEXT_VERSION_MINOR;

const ACK_BATCH_VERSION_MINOR: u16 = 1;
const REPLICATION_BATCH_VERSION_MINOR: u16 = 3;
const REPLICATION_BATCH_CONTEXT_VERSION_MINOR: u16 = 4;

/// The digest length the codec writes and verifies.
pub const DIGEST_BYTES: usize = 32;

/// The maximum payload one envelope may carry. A catch-up batch beyond the
/// bound is split by the sender; a frame beyond it fails closed.
pub const MAX_PAYLOAD_BYTES: usize = 256 * 1024;

/// The maximum total frame size (envelope plus payload).
pub const MAX_FRAME_BYTES: usize = MAX_PAYLOAD_BYTES + 1024;

/// The maximum length of any identifier or free-text field in a payload.
pub const MAX_FIELD_BYTES: u32 = 4096;

/// The maximum opaque causal-context byte string carried by one replicated
/// record. The transport only bounds and preserves these bytes; the engine
/// owns their causal interpretation and canonical-content validation.
pub const MAX_CONTEXT_BYTES: usize = 64 * 1024;

/// Compatibility spelling for callers that name the field explicitly as a
/// causal context.
pub const MAX_CAUSAL_CONTEXT_BYTES: usize = MAX_CONTEXT_BYTES;

/// The written per-frame envelope-overhead budget of the member lane
/// (PD-2026-08-26-13 D1): the encoded bytes of one envelope minus its
/// payload must stay at or below this bound. The budget is provisional and
/// lane-scoped until SE-07 ratifies it; the lane measures and records the
/// overhead of every frame class against it.
pub const BUDGET_ENVELOPE_OVERHEAD_BYTES: usize = 192;

/// The written bytes-on-wire budget per delivered change on the member lane
/// (PD-2026-08-26-13 D1): delivered payload bytes divided by delivered
/// committed changes must stay at or below this bound, measured from the
/// transport's per-direction counters. Provisional and lane-scoped until
/// SE-07 ratifies it.
pub const BUDGET_BYTES_PER_DELIVERED_CHANGE: usize = 512;

/// One frame kind of the member protocol.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum FrameKind {
    /// One Member asks another for a tenant's published history.
    CatchUpRequest,
    /// One Member serves a tenant's published history.
    CatchUpBatch,
    /// One Member pushes just-committed operations to a peer.
    Push,
    /// One Member reports whether it durably holds an operation, answering a
    /// push or probe. The acknowledgement path of the real network lane: the
    /// writer cannot read a peer's durable state in-process, so the
    /// observation crosses the wire.
    AckReport,
    /// One Member reports its durable holding of every operation one pushed
    /// batch carried, in one frame (SE-07 budgets E-3/E-4: the
    /// acknowledgement of a K-operation push is one frame per peer, not K).
    /// Additive in envelope minor version 1.
    AckBatch,
    /// One Member's journal tailer ships its committed journal rows as
    /// replicated records to a peer region (ADR 0041 D-35/D-36: canonical
    /// body bytes plus causal metadata, one frame per group-commit wave per
    /// peer). Its current wire shape requires envelope minor version 3.
    ReplicationBatch,
}

impl FrameKind {
    /// The canonical wire value of the kind.
    #[must_use]
    pub const fn wire(self) -> u8 {
        match self {
            Self::CatchUpRequest => 1,
            Self::CatchUpBatch => 2,
            Self::Push => 3,
            Self::AckReport => 4,
            Self::AckBatch => 5,
            Self::ReplicationBatch => 6,
        }
    }

    /// Decodes the kind from its wire value.
    ///
    /// # Errors
    ///
    /// Returns [`EnvelopeError::UnsupportedKind`] for any other value.
    pub const fn from_wire(value: u8) -> Result<Self, EnvelopeError> {
        match value {
            1 => Ok(Self::CatchUpRequest),
            2 => Ok(Self::CatchUpBatch),
            3 => Ok(Self::Push),
            4 => Ok(Self::AckReport),
            5 => Ok(Self::AckBatch),
            6 => Ok(Self::ReplicationBatch),
            _ => Err(EnvelopeError::UnsupportedKind),
        }
    }

    /// The canonical kind name used by receipts and budgets.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::CatchUpRequest => "catch-up-request",
            Self::CatchUpBatch => "catch-up-batch",
            Self::Push => "push",
            Self::AckReport => "ack-report",
            Self::AckBatch => "ack-batch",
            Self::ReplicationBatch => "replication-batch",
        }
    }

    /// Returns the oldest envelope minor that can represent this kind.
    const fn minimum_minor(self) -> u16 {
        match self {
            Self::CatchUpRequest | Self::CatchUpBatch | Self::Push | Self::AckReport => 0,
            Self::AckBatch => ACK_BATCH_VERSION_MINOR,
            Self::ReplicationBatch => REPLICATION_BATCH_VERSION_MINOR,
        }
    }

    /// Returns the minor used by the canonical encoder for this kind.
    const fn encoding_minor(self) -> u16 {
        match self {
            Self::ReplicationBatch => REPLICATION_BATCH_CONTEXT_VERSION_MINOR,
            _ => self.minimum_minor(),
        }
    }
}

/// One committed write as the lane journals and delivers it.
///
/// The write set travels reconstructed — the full document for a put, the
/// exact deleted key for a delete — never as a digest (the publication
/// journal's contract).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum LaneWrite {
    /// Puts one document.
    Put {
        /// The document identity.
        document: LaneDocument,
    },
    /// Deletes one document key, recorded with the collection it was deleted
    /// from and the version at delete (zero when the key was absent).
    Delete {
        /// The collection the delete targeted, when recorded.
        collection: Option<CollectionName>,
        /// The deleted document identity.
        id: DocumentId,
        /// The document version at delete time; zero is a no-op delete.
        version_at_delete: u64,
    },
}

/// One lane document: identity, collection, and a text value (the lane
/// fixture's document model).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LaneDocument {
    /// The document identity.
    pub id: DocumentId,
    /// The collection the document belongs to.
    pub collection: CollectionName,
    /// The document's text value.
    pub value: String,
}

/// One journaled commit with its writer clock stamp.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LaneCommit {
    /// The stable operation identity.
    pub operation: OperationId,
    /// The writer's hybrid logical clock stamp.
    pub stamp: crate::clock::ClockStamp,
    /// The source's journal sequence of the commit.
    pub sequence: u64,
    /// The reconstructed write set.
    pub writes: Vec<LaneWrite>,
}

/// The maximum nesting depth one [`LaneValue`] may carry: the wire form of
/// the engine's document body, bounded so a hostile frame cannot demand
/// unbounded recursion while decoding.
pub const MAX_LANE_VALUE_DEPTH: u32 = 64;

/// One structured document body as the lane carries it inside a replicated
/// record: the engine's value model, typed on the wire field by field —
/// never a free-form byte blob (the member-envelope discipline). Equality
/// is bit equality (a float compares by its bit pattern), so the value
/// remains a total order on the wire.
#[derive(Clone, Debug)]
pub enum LaneValue {
    /// A null value.
    Null,
    /// A boolean value.
    Boolean(bool),
    /// A signed integer value.
    Integer(i64),
    /// A floating-point value.
    Number(f64),
    /// A text value.
    String(String),
    /// An ordered sequence of structured values.
    Array(Vec<Self>),
    /// A map of field names to structured values, in field-name order.
    Object(Vec<(String, Self)>),
}

impl PartialEq for LaneValue {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (Self::Null, Self::Null) => true,
            (Self::Boolean(left), Self::Boolean(right)) => left == right,
            (Self::Integer(left), Self::Integer(right)) => left == right,
            (Self::Number(left), Self::Number(right)) => left.to_bits() == right.to_bits(),
            (Self::String(left), Self::String(right)) => left == right,
            (Self::Array(left), Self::Array(right)) => left == right,
            (Self::Object(left), Self::Object(right)) => left == right,
            _ => false,
        }
    }
}

impl Eq for LaneValue {}

impl LaneValue {
    /// Encodes the value at the given nesting depth.
    fn encode(&self, out: &mut Vec<u8>, depth: u32) -> Result<(), EnvelopeError> {
        if depth > MAX_LANE_VALUE_DEPTH {
            return Err(EnvelopeError::FieldTooLarge);
        }
        match self {
            Self::Null => put_u8(out, 0),
            Self::Boolean(value) => {
                put_u8(out, 1);
                put_u8(out, u8::from(*value));
            }
            Self::Integer(value) => {
                put_u8(out, 2);
                out.extend_from_slice(&value.to_le_bytes());
            }
            Self::Number(value) => {
                put_u8(out, 3);
                out.extend_from_slice(&value.to_bits().to_le_bytes());
            }
            Self::String(value) => {
                put_u8(out, 4);
                put_str(out, value)?;
            }
            Self::Array(items) => {
                put_u8(out, 5);
                put_u32(out, u32::try_from(items.len()).map_err(|_| EnvelopeError::Oversized)?);
                for item in items {
                    item.encode(out, depth + 1)?;
                }
            }
            Self::Object(fields) => {
                put_u8(out, 6);
                put_u32(out, u32::try_from(fields.len()).map_err(|_| EnvelopeError::Oversized)?);
                for (name, value) in fields {
                    put_str(out, name)?;
                    value.encode(out, depth + 1)?;
                }
            }
        }
        Ok(())
    }

    /// Decodes one value at the given nesting depth.
    fn decode(cursor: &mut Cursor<'_>, depth: u32) -> Result<Self, EnvelopeError> {
        if depth > MAX_LANE_VALUE_DEPTH {
            return Err(EnvelopeError::MalformedLength);
        }
        let value = match cursor.take_u8()? {
            0 => Self::Null,
            1 => Self::Boolean(matches!(cursor.take_u8()?, 1)),
            2 => Self::Integer(i64::from_le_bytes(wide_bytes(cursor)?)),
            3 => Self::Number(f64::from_bits(u64::from_le_bytes(wide_bytes(cursor)?))),
            4 => Self::String(take_str(cursor)?),
            5 => {
                let count = cursor.take_u32()? as usize;
                if count > MAX_PAYLOAD_BYTES {
                    return Err(EnvelopeError::MalformedLength);
                }
                let mut items = Vec::with_capacity(count.min(cursor.remaining()));
                for _ in 0..count {
                    items.push(Self::decode(cursor, depth + 1)?);
                }
                Self::Array(items)
            }
            6 => {
                let count = cursor.take_u32()? as usize;
                if count > MAX_PAYLOAD_BYTES {
                    return Err(EnvelopeError::MalformedLength);
                }
                let mut fields =
                    Vec::with_capacity(bounded_capacity(cursor, count, MIN_OBJECT_ENTRY_BYTES));
                for _ in 0..count {
                    let name = take_str(cursor)?;
                    let value = Self::decode(cursor, depth + 1)?;
                    fields.push((name, value));
                }
                Self::Object(fields)
            }
            _ => return Err(EnvelopeError::MalformedLength),
        };
        Ok(value)
    }
}

/// One replicated record as the journal tailer ships it (ADR 0041 D-36):
/// the canonical body plus the causal metadata — commit identity, source
/// HLC stamp, source region, and source journal sequence — the receiving
/// region's deterministic merge orders by. A tombstone carries `None` for
/// the body.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicatedWrite {
    /// The collection the document belongs to.
    pub collection: CollectionName,
    /// The document identity.
    pub id: DocumentId,
    /// The published body; `None` marks a tombstone.
    pub body: Option<LaneValue>,
    /// The source region's commit identity (the idempotency binding on the
    /// receiver).
    pub commit_ulid: String,
    /// The source region's hybrid logical clock stamp.
    pub hlc: u64,
    /// The source region's identity.
    pub origin: String,
    /// The source region's durable per-origin journal sequence.
    pub origin_sequence: u64,
    /// The source row's canonical causal-context bytes.
    ///
    /// These bytes are intentionally opaque at the transport boundary. They
    /// are copied byte-for-byte into the envelope and validated by the engine
    /// that owns the causal codec. An empty value is the legacy form for
    /// records decoded from envelope minor 3.
    pub context: Vec<u8>,
}

/// The typed payload of one member envelope.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum Payload {
    /// Requests a tenant's published commits after a cursor position.
    CatchUpRequest {
        /// The tenant whose history is requested.
        tenant: TenantId,
        /// The requester's progress in the source's sequence space, or `None`
        /// for a full scan.
        after: Option<u64>,
        /// The requester's bounded batch size.
        limit: u32,
    },
    /// Serves a tenant's published commits.
    CatchUpBatch {
        /// The tenant the batch belongs to.
        tenant: TenantId,
        /// The served commits in source sequence order.
        commits: Vec<LaneCommit>,
        /// The highest source sequence this batch serves.
        through: Option<u64>,
        /// The source's latest recorded sequence for this tenant.
        tenant_high_water: Option<u64>,
        /// The oldest sequence the source's journal can serve.
        servable_from: Option<u64>,
        /// The requester's cursor position the source cannot serve below;
        /// `None` when the served range is complete.
        gap_below: Option<u64>,
    },
    /// Pushes just-committed operations under an ownership epoch.
    Push {
        /// The tenant the pushed commits belong to.
        tenant: TenantId,
        /// The data range the commits were admitted under.
        range: String,
        /// The ownership epoch of the range at the pushing Member's view.
        epoch: u64,
        /// The pushed commits with their writer clock stamps.
        commits: Vec<LaneCommit>,
    },
    /// Reports one Member's durable holding of one operation.
    AckReport {
        /// The tenant the observation names.
        tenant: TenantId,
        /// The operation identity observed.
        operation: OperationId,
        /// Whether the Member's own publication of the operation is committed
        /// at the durable level.
        held_synced: bool,
    },
    /// Reports one Member's durable holding of every operation one pushed
    /// batch carried: the batched acknowledgement of a multi-operation push
    /// (envelope minor 1, SE-07 budgets E-3/E-4).
    AckBatch {
        /// The tenant the observations name.
        tenant: TenantId,
        /// The observed operations with their durable holding, in the
        /// pushed batch's order.
        reports: Vec<(OperationId, bool)>,
    },
    /// Ships one journal tailer batch of replicated records to a peer
    /// region (envelope minor 3, ADR 0041 D-35/D-36): one frame per
    /// group-commit wave per peer, canonical body bytes plus causal
    /// metadata and the source journal sequence. Context-bearing records use
    /// envelope minor 4; minor 3 is retained for legacy records without a
    /// context field.
    ReplicationBatch {
        /// The tenant the records belong to.
        tenant: TenantId,
        /// The shipped records in source journal order.
        records: Vec<ReplicatedWrite>,
    },
}

/// One member envelope: the discipline header plus its typed payload.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Envelope {
    /// The frame kind; also carried in the wire header.
    pub kind: FrameKind,
    /// The message identity, minted monotonically by the source Member.
    pub message_id: u64,
    /// The message identity this frame answers, when it is a reply.
    pub correlation_id: Option<u64>,
    /// The message identity this frame was caused by, when it continues one.
    pub causation_id: Option<u64>,
    /// The idempotency key where the frame can mutate state (the operation
    /// identity of an acknowledgement report); batches carry their
    /// per-operation keys inside the payload and are resolved by the durable
    /// operation ledger.
    pub idempotency_key: Option<OperationId>,
    /// The sending Member.
    pub source: MemberId,
    /// The addressed Member.
    pub target: MemberId,
    /// The lane tick the frame was issued at.
    pub issued_at: LaneInstant,
    /// The lane tick the frame expires at; a receiver fails closed past it.
    pub expires_at: Option<LaneInstant>,
    /// The data range, whenever the frame can mutate data-range state.
    pub range: Option<String>,
    /// The ownership epoch carried with the range.
    pub epoch: Option<u64>,
    /// The typed payload.
    pub payload: Payload,
}

/// Every way the codec fails closed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EnvelopeError {
    /// The frame's schema is not the member-lane schema.
    UnknownSchema,
    /// The frame's major version is not the supported one.
    UnsupportedMajorVersion,
    /// The frame's minor version is unsupported for its kind.
    UnsupportedMinorVersion,
    /// The frame's kind byte is not a known kind.
    UnsupportedKind,
    /// The frame ended inside a field.
    Truncated,
    /// The frame carried bytes past its digest.
    TrailingBytes,
    /// A length prefix named more bytes than the frame holds, or broke a
    /// bound.
    MalformedLength,
    /// A text field was not valid UTF-8.
    InvalidText,
    /// A field broke its declared bound.
    FieldTooLarge,
    /// The frame or its payload exceeded [`MAX_FRAME_BYTES`] /
    /// [`MAX_PAYLOAD_BYTES`].
    Oversized,
    /// The digest does not agree with the payload.
    DigestMismatch,
    /// The envelope is addressed to another Member (a member-boundary
    /// discipline failure recorded by the receiving Member).
    WrongTarget,
    /// The envelope's expiry tick has passed at the receiver's clock.
    Expired,
}

/// The encoded form of one envelope with its measured lengths, so the
/// transport and the receipts can account bytes on wire exactly.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EncodedFrame {
    /// The full encoded envelope.
    pub bytes: Vec<u8>,
    /// The payload length inside it.
    pub payload_len: usize,
}

impl EncodedFrame {
    /// Returns the envelope overhead: encoded bytes minus payload.
    #[must_use]
    pub fn overhead_bytes(&self) -> usize {
        self.bytes.len() - self.payload_len
    }

    /// Returns the wire kind byte of the encoded frame, at its fixed offset
    /// after the schema string and version fields, without decoding the
    /// payload.
    ///
    /// # Errors
    ///
    /// Returns the codec's fail-closed errors for a malformed prefix.
    pub fn peek_kind(&self) -> Result<FrameKind, EnvelopeError> {
        peek_kind(&self.bytes)
    }
}

/// Encodes one envelope under the discipline header.
///
/// # Errors
///
/// Returns [`EnvelopeError::Oversized`] when the payload or frame breaks its
/// bound and [`EnvelopeError::FieldTooLarge`] for an oversized text field.
pub fn encode(envelope: &Envelope) -> Result<EncodedFrame, EnvelopeError> {
    let mut payload = Vec::new();
    encode_payload(&mut payload, &envelope.payload)?;
    if payload.len() > MAX_PAYLOAD_BYTES {
        return Err(EnvelopeError::Oversized);
    }
    let mut frame = Vec::with_capacity(payload.len() + 256);
    put_u8(
        &mut frame,
        u8::try_from(ENVELOPE_SCHEMA.len()).map_err(|_| EnvelopeError::FieldTooLarge)?,
    );
    frame.extend_from_slice(ENVELOPE_SCHEMA.as_bytes());
    put_u16(&mut frame, ENVELOPE_VERSION_MAJOR);
    put_u16(&mut frame, envelope.kind.encoding_minor());
    put_u8(&mut frame, envelope.kind.wire());
    put_u64(&mut frame, envelope.message_id);
    put_option_u64(&mut frame, envelope.correlation_id);
    put_option_u64(&mut frame, envelope.causation_id);
    put_option_str(
        &mut frame,
        envelope.idempotency_key.as_ref().map(super::identity::OperationId::as_str),
    )?;
    put_str(&mut frame, envelope.source.as_str())?;
    put_str(&mut frame, envelope.target.as_str())?;
    put_u64(&mut frame, envelope.issued_at.ticks());
    put_option_u64(&mut frame, envelope.expires_at.map(super::clock::LaneInstant::ticks));
    put_option_str(&mut frame, envelope.range.as_deref())?;
    put_option_u64(&mut frame, envelope.epoch);
    put_u32(&mut frame, u32::try_from(payload.len()).map_err(|_| EnvelopeError::Oversized)?);
    if frame.len() + payload.len() + DIGEST_BYTES > MAX_FRAME_BYTES {
        return Err(EnvelopeError::Oversized);
    }
    frame.extend_from_slice(&payload);
    let digest = sha256(&frame);
    frame.extend_from_slice(digest.as_ref());
    Ok(EncodedFrame { bytes: frame, payload_len: payload.len() })
}

/// Decodes one envelope, verifying the discipline header and digest.
///
/// # Errors
///
/// Fails closed with the matching [`EnvelopeError`] for every violation.
pub fn decode(frame: &[u8]) -> Result<(Envelope, EncodedFrame), EnvelopeError> {
    let mut cursor = Cursor::new(frame);
    let schema_len = cursor.take_u8()? as usize;
    if frame.len() < 1 + schema_len {
        return Err(EnvelopeError::Truncated);
    }
    let schema = cursor.take_bytes(schema_len)?;
    if schema != ENVELOPE_SCHEMA.as_bytes() {
        return Err(EnvelopeError::UnknownSchema);
    }
    let major = cursor.take_u16()?;
    let minor = cursor.take_u16()?;
    if major != ENVELOPE_VERSION_MAJOR {
        return Err(EnvelopeError::UnsupportedMajorVersion);
    }
    if minor > ENVELOPE_VERSION_MINOR {
        return Err(EnvelopeError::UnsupportedMinorVersion);
    }
    let kind = FrameKind::from_wire(cursor.take_u8()?)?;
    if minor < kind.minimum_minor() {
        return Err(EnvelopeError::UnsupportedMinorVersion);
    }
    let message_id = cursor.take_u64()?;
    let correlation_id = take_option_u64(&mut cursor)?;
    let causation_id = take_option_u64(&mut cursor)?;
    let idempotency_key = take_option_str(&mut cursor)?
        .map(|text| OperationId::try_new(&text).map_err(|_| EnvelopeError::FieldTooLarge))
        .transpose()?;
    let source =
        MemberId::try_new(&take_str(&mut cursor)?).map_err(|_| EnvelopeError::FieldTooLarge)?;
    let target =
        MemberId::try_new(&take_str(&mut cursor)?).map_err(|_| EnvelopeError::FieldTooLarge)?;
    let issued_at = LaneInstant::at_ticks(cursor.take_u64()?);
    let expires_at = take_option_u64(&mut cursor)?.map(LaneInstant::at_ticks);
    let range = take_option_str(&mut cursor)?;
    let epoch = take_option_u64(&mut cursor)?;
    let payload_len = cursor.take_u32()? as usize;
    if payload_len > MAX_PAYLOAD_BYTES {
        return Err(EnvelopeError::Oversized);
    }
    let payload_bytes = cursor.take_bytes(payload_len)?;
    let digest = cursor.take_bytes(DIGEST_BYTES)?;
    if !cursor.is_empty() {
        return Err(EnvelopeError::TrailingBytes);
    }
    let signed_len = frame.len() - DIGEST_BYTES;
    if sha256(&frame[..signed_len]).as_ref() != digest {
        return Err(EnvelopeError::DigestMismatch);
    }
    let mut payload_cursor = Cursor::new(payload_bytes);
    let payload = decode_payload(&mut payload_cursor, kind, minor)?;
    if !payload_cursor.is_empty() {
        return Err(EnvelopeError::TrailingBytes);
    }
    let encoded = EncodedFrame { bytes: frame.to_vec(), payload_len };
    Ok((
        Envelope {
            kind,
            message_id,
            correlation_id,
            causation_id,
            idempotency_key,
            source,
            target,
            issued_at,
            expires_at,
            range,
            epoch,
            payload,
        },
        encoded,
    ))
}

/// Returns the frame's kind byte at its fixed header offset.
///
/// # Errors
///
/// Returns the codec's fail-closed errors for a malformed prefix.
pub fn peek_kind(frame: &[u8]) -> Result<FrameKind, EnvelopeError> {
    let mut cursor = Cursor::new(frame);
    let schema_len = cursor.take_u8()? as usize;
    if frame.len() < 1 + schema_len {
        return Err(EnvelopeError::Truncated);
    }
    let schema = cursor.take_bytes(schema_len)?;
    if schema != ENVELOPE_SCHEMA.as_bytes() {
        return Err(EnvelopeError::UnknownSchema);
    }
    let major = cursor.take_u16()?;
    let minor = cursor.take_u16()?;
    if major != ENVELOPE_VERSION_MAJOR {
        return Err(EnvelopeError::UnsupportedMajorVersion);
    }
    if minor > ENVELOPE_VERSION_MINOR {
        return Err(EnvelopeError::UnsupportedMinorVersion);
    }
    let kind = FrameKind::from_wire(cursor.take_u8()?)?;
    if minor < kind.minimum_minor() {
        return Err(EnvelopeError::UnsupportedMinorVersion);
    }
    Ok(kind)
}

/// Returns the digest of `bytes`.
fn sha256(bytes: &[u8]) -> Digest {
    let mut context = Context::new(&SHA256);
    context.update(bytes);
    context.finish()
}

/// Returns the envelope's payload length by walking the header offsets
/// without decoding the payload — the socket lane's receive path uses this
/// to rebuild the measured [`EncodedFrame`] from raw stream bytes.
///
/// Returns `None` when the header is truncated or malformed.
#[must_use]
pub fn peek_payload_len(frame: &[u8]) -> Option<usize> {
    let mut offset = 0;
    let schema_len = *frame.first()? as usize;
    offset += 1 + schema_len;
    offset += 2 + 2; // version fields
    offset += 1; // kind
    offset += 8; // message id
    offset += option_len(frame, offset)?; // correlation
    offset += option_len(frame, offset)?; // causation
    offset += 1; // idempotency tag
    if tag_is_some(frame, offset - 1)? {
        offset += str_len(frame, offset)? + 4;
    }
    offset += str_len(frame, offset)? + 4; // source
    offset += str_len(frame, offset)? + 4; // target
    offset += 8; // issued at
    offset += option_len(frame, offset)?; // expiry
    offset += 1; // range tag
    if tag_is_some(frame, offset - 1)? {
        offset += str_len(frame, offset)? + 4;
    }
    offset += option_len(frame, offset)?; // epoch
    let payload_len = le_u32_at(frame, offset)?;
    Some(payload_len as usize)
}

fn tag_is_some(frame: &[u8], at: usize) -> Option<bool> {
    match frame.get(at)? {
        0 => Some(false),
        1 => Some(true),
        _ => None,
    }
}

fn str_len(frame: &[u8], at: usize) -> Option<usize> {
    let bytes = frame.get(at..at + 4)?;
    let mut words = [0_u8; 4];
    words.copy_from_slice(bytes);
    Some(u32::from_le_bytes(words) as usize)
}

fn option_len(frame: &[u8], at: usize) -> Option<usize> {
    if tag_is_some(frame, at)? { Some(9) } else { Some(1) }
}

fn le_u32_at(frame: &[u8], at: usize) -> Option<u32> {
    let bytes = frame.get(at..at + 4)?;
    let mut words = [0_u8; 4];
    words.copy_from_slice(bytes);
    Some(u32::from_le_bytes(words))
}

/// A bounds-checked read cursor over one frame.
struct Cursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> Cursor<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }

    fn is_empty(&self) -> bool {
        self.offset >= self.bytes.len()
    }

    /// The bytes the cursor has not consumed yet — the declared payload's
    /// own bound on how many elements can possibly follow.
    fn remaining(&self) -> usize {
        self.bytes.len() - self.offset
    }

    fn take_bytes(&mut self, len: usize) -> Result<&'a [u8], EnvelopeError> {
        if self.bytes.len() - self.offset < len {
            return Err(EnvelopeError::Truncated);
        }
        let start = self.offset;
        self.offset += len;
        Ok(&self.bytes[start..start + len])
    }

    fn take_u8(&mut self) -> Result<u8, EnvelopeError> {
        Ok(self.take_bytes(1)?[0])
    }

    fn take_u16(&mut self) -> Result<u16, EnvelopeError> {
        let bytes = self.take_bytes(2)?;
        Ok(u16::from_le_bytes([bytes[0], bytes[1]]))
    }

    fn take_u32(&mut self) -> Result<u32, EnvelopeError> {
        let bytes = self.take_bytes(4)?;
        Ok(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
    }

    fn take_u64(&mut self) -> Result<u64, EnvelopeError> {
        let bytes = self.take_bytes(8)?;
        let mut words = [0_u8; 8];
        words.copy_from_slice(bytes);
        Ok(u64::from_le_bytes(words))
    }
}

/// Reads eight little-endian bytes as one wide word. Private to the value
/// codec's integer and number arms.
fn wide_bytes(cursor: &mut Cursor<'_>) -> Result<[u8; 8], EnvelopeError> {
    let mut word = [0_u8; 8];
    word.copy_from_slice(cursor.take_bytes(8)?);
    Ok(word)
}

fn put_u8(out: &mut Vec<u8>, value: u8) {
    out.push(value);
}

fn put_u16(out: &mut Vec<u8>, value: u16) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn put_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn put_u64(out: &mut Vec<u8>, value: u64) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn put_str(out: &mut Vec<u8>, text: &str) -> Result<(), EnvelopeError> {
    if text.len() > MAX_FIELD_BYTES as usize {
        return Err(EnvelopeError::FieldTooLarge);
    }
    put_u32(out, u32::try_from(text.len()).map_err(|_| EnvelopeError::FieldTooLarge)?);
    out.extend_from_slice(text.as_bytes());
    Ok(())
}

/// Writes one bounded opaque byte string in canonical length-prefixed form.
fn put_bytes_field(out: &mut Vec<u8>, bytes: &[u8], max: usize) -> Result<(), EnvelopeError> {
    if bytes.len() > max {
        return Err(EnvelopeError::FieldTooLarge);
    }
    put_u32(out, u32::try_from(bytes.len()).map_err(|_| EnvelopeError::FieldTooLarge)?);
    out.extend_from_slice(bytes);
    Ok(())
}

fn put_option_u64(out: &mut Vec<u8>, value: Option<u64>) {
    match value {
        Some(value) => {
            put_u8(out, 1);
            put_u64(out, value);
        }
        None => put_u8(out, 0),
    }
}

fn put_option_str(out: &mut Vec<u8>, text: Option<&str>) -> Result<(), EnvelopeError> {
    if let Some(text) = text {
        put_u8(out, 1);
        put_str(out, text)?;
    } else {
        put_u8(out, 0);
    }
    Ok(())
}

fn take_str(cursor: &mut Cursor<'_>) -> Result<String, EnvelopeError> {
    let len = cursor.take_u32()? as usize;
    if len > MAX_FIELD_BYTES as usize {
        return Err(EnvelopeError::FieldTooLarge);
    }
    let bytes = cursor.take_bytes(len)?;
    String::from_utf8(bytes.to_vec()).map_err(|_| EnvelopeError::InvalidText)
}

/// Reads one bounded opaque byte string in canonical length-prefixed form.
fn take_bytes_field(cursor: &mut Cursor<'_>, max: usize) -> Result<Vec<u8>, EnvelopeError> {
    let len = cursor.take_u32()? as usize;
    if len > max {
        return Err(EnvelopeError::FieldTooLarge);
    }
    Ok(cursor.take_bytes(len)?.to_vec())
}

fn take_option_u64(cursor: &mut Cursor<'_>) -> Result<Option<u64>, EnvelopeError> {
    match cursor.take_u8()? {
        0 => Ok(None),
        1 => Ok(Some(cursor.take_u64()?)),
        _ => Err(EnvelopeError::MalformedLength),
    }
}

fn take_option_str(cursor: &mut Cursor<'_>) -> Result<Option<String>, EnvelopeError> {
    match cursor.take_u8()? {
        0 => Ok(None),
        1 => Ok(Some(take_str(cursor)?)),
        _ => Err(EnvelopeError::MalformedLength),
    }
}

fn encode_payload(out: &mut Vec<u8>, payload: &Payload) -> Result<(), EnvelopeError> {
    match payload {
        Payload::CatchUpRequest { tenant, after, limit } => {
            put_str(out, tenant.as_str())?;
            put_option_u64(out, *after);
            put_u32(out, *limit);
        }
        Payload::CatchUpBatch {
            tenant,
            commits,
            through,
            tenant_high_water,
            servable_from,
            gap_below,
        } => {
            put_str(out, tenant.as_str())?;
            put_u32(out, u32::try_from(commits.len()).map_err(|_| EnvelopeError::Oversized)?);
            for commit in commits {
                encode_commit(out, commit)?;
            }
            put_option_u64(out, *through);
            put_option_u64(out, *tenant_high_water);
            put_option_u64(out, *servable_from);
            put_option_u64(out, *gap_below);
        }
        Payload::Push { tenant, range, epoch, commits } => {
            put_str(out, tenant.as_str())?;
            put_str(out, range)?;
            put_u64(out, *epoch);
            put_u32(out, u32::try_from(commits.len()).map_err(|_| EnvelopeError::Oversized)?);
            for commit in commits {
                encode_commit(out, commit)?;
            }
        }
        Payload::AckReport { tenant, operation, held_synced } => {
            put_str(out, tenant.as_str())?;
            put_str(out, operation.as_str())?;
            put_u8(out, u8::from(*held_synced));
        }
        Payload::AckBatch { tenant, reports } => {
            put_str(out, tenant.as_str())?;
            put_u32(out, u32::try_from(reports.len()).map_err(|_| EnvelopeError::Oversized)?);
            for (operation, held_synced) in reports {
                put_str(out, operation.as_str())?;
                put_u8(out, u8::from(*held_synced));
            }
        }
        Payload::ReplicationBatch { tenant, records } => {
            put_str(out, tenant.as_str())?;
            put_u32(out, u32::try_from(records.len()).map_err(|_| EnvelopeError::Oversized)?);
            for record in records {
                put_str(out, record.collection.as_str())?;
                put_str(out, record.id.as_str())?;
                put_u8(out, u8::from(record.body.is_some()));
                if let Some(body) = &record.body {
                    body.encode(out, 0)?;
                }
                put_str(out, &record.commit_ulid)?;
                put_u64(out, record.hlc);
                put_str(out, &record.origin)?;
                if record.origin_sequence == 0 || record.origin_sequence > i64::MAX as u64 {
                    return Err(EnvelopeError::MalformedLength);
                }
                put_u64(out, record.origin_sequence);
                // Minor 4's canonical record shape always carries this
                // length-prefixed field, including for an empty context.
                put_bytes_field(out, &record.context, MAX_CONTEXT_BYTES)?;
            }
        }
    }
    Ok(())
}

fn encode_commit(out: &mut Vec<u8>, commit: &LaneCommit) -> Result<(), EnvelopeError> {
    put_str(out, commit.operation.as_str())?;
    put_u64(out, commit.stamp.ticks());
    put_u64(out, commit.stamp.counter());
    put_u64(out, commit.sequence);
    put_u32(out, u32::try_from(commit.writes.len()).map_err(|_| EnvelopeError::Oversized)?);
    for write in &commit.writes {
        encode_write(out, write)?;
    }
    Ok(())
}

fn encode_write(out: &mut Vec<u8>, write: &LaneWrite) -> Result<(), EnvelopeError> {
    match write {
        LaneWrite::Put { document } => {
            put_u8(out, 1);
            put_str(out, document.id.as_str())?;
            put_str(out, document.collection.as_str())?;
            put_str(out, &document.value)?;
        }
        LaneWrite::Delete { collection, id, version_at_delete } => {
            put_u8(out, 2);
            put_option_str(out, collection.as_ref().map(super::identity::CollectionName::as_str))?;
            put_str(out, id.as_str())?;
            put_u64(out, *version_at_delete);
        }
    }
    Ok(())
}

/// The minimum wire bytes one decoded element of each repeated payload
/// sequence occupies: every counted field at its empty minimum (a `u32`
/// string length of zero still costs its 4 width bytes). Capacity planning
/// bounds a declared element count by these lower bounds against the
/// bytes the declared payload actually holds, so a hostile count inside a
/// digest-valid frame can never pre-allocate more than
/// payload-proportional memory (the count alone is not trusted for
/// capacity; the elements themselves still fail closed on truncation).
const MIN_ACK_REPORT_WIRE_BYTES: usize = 5;
const MIN_COMMIT_WIRE_BYTES: usize = 32;
const MIN_WRITE_WIRE_BYTES: usize = 13;
/// The minimum wire bytes one decoded replicated record occupies (two empty
/// identifier strings, the body tag, an empty commit identity, the HLC, an
/// empty origin, and the source sequence — every counted field at its empty
/// minimum).
const MIN_REPLICATED_WIRE_BYTES: usize = 33;
/// The minimum wire bytes one decoded context-bearing replicated record
/// occupies. The canonical context field is always present, including when
/// its byte string is empty.
const MIN_REPLICATED_CONTEXT_WIRE_BYTES: usize = MIN_REPLICATED_WIRE_BYTES + 4;
/// The minimum wire bytes one decoded object entry of a [`LaneValue`]
/// occupies (a `u32` string length of zero plus one value tag byte).
const MIN_OBJECT_ENTRY_BYTES: usize = 5;

/// The decode capacity for a declared element count: the count, bounded by
/// how many elements the cursor's remaining bytes could possibly hold.
fn bounded_capacity(cursor: &Cursor<'_>, declared: usize, min_wire_bytes: usize) -> usize {
    declared.min(cursor.remaining() / min_wire_bytes)
}

fn decode_payload(
    cursor: &mut Cursor<'_>,
    kind: FrameKind,
    minor: u16,
) -> Result<Payload, EnvelopeError> {
    match kind {
        FrameKind::CatchUpRequest => decode_catch_up_request(cursor),
        FrameKind::CatchUpBatch => decode_catch_up_batch(cursor),
        FrameKind::Push => decode_push(cursor),
        FrameKind::AckReport => decode_ack_report(cursor),
        FrameKind::AckBatch => decode_ack_batch(cursor),
        FrameKind::ReplicationBatch => decode_replication_batch(cursor, minor),
    }
}

fn decode_tenant(cursor: &mut Cursor<'_>) -> Result<TenantId, EnvelopeError> {
    TenantId::try_new(&take_str(cursor)?).map_err(|_| EnvelopeError::FieldTooLarge)
}

fn decode_catch_up_request(cursor: &mut Cursor<'_>) -> Result<Payload, EnvelopeError> {
    let tenant = decode_tenant(cursor)?;
    let after = take_option_u64(cursor)?;
    let limit = cursor.take_u32()?;
    Ok(Payload::CatchUpRequest { tenant, after, limit })
}

fn decode_catch_up_batch(cursor: &mut Cursor<'_>) -> Result<Payload, EnvelopeError> {
    let tenant = decode_tenant(cursor)?;
    let commits = decode_commits(cursor)?;
    let through = take_option_u64(cursor)?;
    let tenant_high_water = take_option_u64(cursor)?;
    let servable_from = take_option_u64(cursor)?;
    let gap_below = take_option_u64(cursor)?;
    Ok(Payload::CatchUpBatch {
        tenant,
        commits,
        through,
        tenant_high_water,
        servable_from,
        gap_below,
    })
}

fn decode_push(cursor: &mut Cursor<'_>) -> Result<Payload, EnvelopeError> {
    let tenant = decode_tenant(cursor)?;
    let range = take_str(cursor)?;
    let epoch = cursor.take_u64()?;
    let commits = decode_commits(cursor)?;
    Ok(Payload::Push { tenant, range, epoch, commits })
}

fn decode_ack_report(cursor: &mut Cursor<'_>) -> Result<Payload, EnvelopeError> {
    let tenant = decode_tenant(cursor)?;
    let operation = decode_operation(cursor)?;
    let held_synced = decode_bool(cursor)?;
    Ok(Payload::AckReport { tenant, operation, held_synced })
}

fn decode_ack_batch(cursor: &mut Cursor<'_>) -> Result<Payload, EnvelopeError> {
    let tenant = decode_tenant(cursor)?;
    let count = bounded_count(cursor.take_u32()? as usize)?;
    let mut reports =
        Vec::with_capacity(bounded_capacity(cursor, count, MIN_ACK_REPORT_WIRE_BYTES));
    for _ in 0..count {
        reports.push((decode_operation(cursor)?, decode_bool(cursor)?));
    }
    Ok(Payload::AckBatch { tenant, reports })
}

fn decode_replication_batch(cursor: &mut Cursor<'_>, minor: u16) -> Result<Payload, EnvelopeError> {
    let tenant = decode_tenant(cursor)?;
    let count = bounded_count(cursor.take_u32()? as usize)?;
    let min_wire_bytes = if minor >= REPLICATION_BATCH_CONTEXT_VERSION_MINOR {
        MIN_REPLICATED_CONTEXT_WIRE_BYTES
    } else {
        MIN_REPLICATED_WIRE_BYTES
    };
    let mut records = Vec::with_capacity(bounded_capacity(cursor, count, min_wire_bytes));
    for _ in 0..count {
        records.push(decode_replicated_write(
            cursor,
            minor >= REPLICATION_BATCH_CONTEXT_VERSION_MINOR,
        )?);
    }
    Ok(Payload::ReplicationBatch { tenant, records })
}

fn bounded_count(count: usize) -> Result<usize, EnvelopeError> {
    if count > MAX_PAYLOAD_BYTES { Err(EnvelopeError::MalformedLength) } else { Ok(count) }
}

fn decode_operation(cursor: &mut Cursor<'_>) -> Result<OperationId, EnvelopeError> {
    OperationId::try_new(&take_str(cursor)?).map_err(|_| EnvelopeError::FieldTooLarge)
}

fn decode_bool(cursor: &mut Cursor<'_>) -> Result<bool, EnvelopeError> {
    match cursor.take_u8()? {
        0 => Ok(false),
        1 => Ok(true),
        _ => Err(EnvelopeError::MalformedLength),
    }
}

fn decode_replicated_write(
    cursor: &mut Cursor<'_>,
    has_context: bool,
) -> Result<ReplicatedWrite, EnvelopeError> {
    let collection =
        CollectionName::try_new(&take_str(cursor)?).map_err(|_| EnvelopeError::FieldTooLarge)?;
    let id = DocumentId::try_new(&take_str(cursor)?).map_err(|_| EnvelopeError::FieldTooLarge)?;
    let body = match cursor.take_u8()? {
        0 => None,
        1 => Some(LaneValue::decode(cursor, 0)?),
        _ => return Err(EnvelopeError::MalformedLength),
    };
    let commit_ulid = take_str(cursor)?;
    let hlc = cursor.take_u64()?;
    let origin = take_str(cursor)?;
    let origin_sequence = cursor.take_u64()?;
    if origin_sequence == 0 || origin_sequence > i64::MAX as u64 {
        return Err(EnvelopeError::MalformedLength);
    }
    let context =
        if has_context { take_bytes_field(cursor, MAX_CONTEXT_BYTES)? } else { Vec::new() };
    Ok(ReplicatedWrite { collection, id, body, commit_ulid, hlc, origin, origin_sequence, context })
}

fn decode_commits(cursor: &mut Cursor<'_>) -> Result<Vec<LaneCommit>, EnvelopeError> {
    let count = cursor.take_u32()? as usize;
    if count > MAX_PAYLOAD_BYTES {
        return Err(EnvelopeError::MalformedLength);
    }
    let mut commits = Vec::with_capacity(bounded_capacity(cursor, count, MIN_COMMIT_WIRE_BYTES));
    for _ in 0..count {
        let operation =
            OperationId::try_new(&take_str(cursor)?).map_err(|_| EnvelopeError::FieldTooLarge)?;
        let stamp = crate::clock::ClockStamp::new(cursor.take_u64()?, cursor.take_u64()?);
        let sequence = cursor.take_u64()?;
        let writes_count = cursor.take_u32()? as usize;
        if writes_count > MAX_PAYLOAD_BYTES {
            return Err(EnvelopeError::MalformedLength);
        }
        let mut writes =
            Vec::with_capacity(bounded_capacity(cursor, writes_count, MIN_WRITE_WIRE_BYTES));
        for _ in 0..writes_count {
            writes.push(decode_write(cursor)?);
        }
        commits.push(LaneCommit { operation, stamp, sequence, writes });
    }
    Ok(commits)
}

fn decode_write(cursor: &mut Cursor<'_>) -> Result<LaneWrite, EnvelopeError> {
    match cursor.take_u8()? {
        1 => {
            let id = DocumentId::try_new(&take_str(cursor)?)
                .map_err(|_| EnvelopeError::FieldTooLarge)?;
            let collection = CollectionName::try_new(&take_str(cursor)?)
                .map_err(|_| EnvelopeError::FieldTooLarge)?;
            let value = take_str(cursor)?;
            Ok(LaneWrite::Put { document: LaneDocument { id, collection, value } })
        }
        2 => {
            let collection = take_option_str(cursor)?
                .map(|text| CollectionName::try_new(&text))
                .transpose()
                .map_err(|_| EnvelopeError::FieldTooLarge)?;
            let id = DocumentId::try_new(&take_str(cursor)?)
                .map_err(|_| EnvelopeError::FieldTooLarge)?;
            let version_at_delete = cursor.take_u64()?;
            Ok(LaneWrite::Delete { collection, id, version_at_delete })
        }
        _ => Err(EnvelopeError::MalformedLength),
    }
}
