//! The typed frame-body vocabulary of `terrascale.sync.v2`: one enum
//! variant per frame kind, each with a canonical byte encoding that is
//! the digest input and the wire body.

use std::collections::BTreeMap;

use crate::SyncCheckpoint;
use crate::SyncValue;

/// The registry identity of one frame kind. The codes are stable wire
/// values every SDK implements; unknown codes fail closed on decode.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FrameKind {
    /// Client: open a session (fresh, or resuming from a checkpoint).
    SessionSetup,
    /// Server: the session is accepted; states the server checkpoint.
    SessionAccepted,
    /// Client: modify the session's collection subscriptions.
    SubscriptionModify,
    /// Server: the subscription set now in force.
    SubscriptionChanged,
    /// Client: acknowledge the session's durable resumption position.
    CheckpointAck,
    /// Either direction: liveness probe with no payload.
    Heartbeat,
    /// Server: one byte-budgeted delta batch.
    DeltaBatch,
    /// Server: one full-state answer (initial sync or forced resync).
    SnapshotAnswer,
    /// Server: a content-free trigger notification (push the hint,
    /// pull the delta).
    Trigger,
    /// Server: the session fell behind the retained floor; resync.
    ResyncRequired,
    /// Server: the serving region changed; re-resolve and resume.
    RegionRouteChange,
    /// Server: a fatal error from the closed error vocabulary.
    Error,
    /// Client: a typed mutation request carrying an idempotency key.
    MutationRequest,
    /// Server: the acknowledgement of a mutation (the only answer).
    MutationAck,
}

impl FrameKind {
    /// The stable wire code of the kind.
    #[must_use]
    pub fn code(self) -> u16 {
        match self {
            FrameKind::SessionSetup => 0x0001,
            FrameKind::SessionAccepted => 0x0002,
            FrameKind::SubscriptionModify => 0x0003,
            FrameKind::SubscriptionChanged => 0x0004,
            FrameKind::CheckpointAck => 0x0005,
            FrameKind::Heartbeat => 0x0006,
            FrameKind::DeltaBatch => 0x0007,
            FrameKind::SnapshotAnswer => 0x0008,
            FrameKind::Trigger => 0x0009,
            FrameKind::ResyncRequired => 0x000A,
            FrameKind::RegionRouteChange => 0x000B,
            FrameKind::Error => 0x000C,
            FrameKind::MutationRequest => 0x000D,
            FrameKind::MutationAck => 0x000E,
        }
    }

    /// The kind for a wire code, or `None` for unknown codes.
    #[must_use]
    pub fn from_code(code: u16) -> Option<Self> {
        Some(match code {
            0x0001 => FrameKind::SessionSetup,
            0x0002 => FrameKind::SessionAccepted,
            0x0003 => FrameKind::SubscriptionModify,
            0x0004 => FrameKind::SubscriptionChanged,
            0x0005 => FrameKind::CheckpointAck,
            0x0006 => FrameKind::Heartbeat,
            0x0007 => FrameKind::DeltaBatch,
            0x0008 => FrameKind::SnapshotAnswer,
            0x0009 => FrameKind::Trigger,
            0x000A => FrameKind::ResyncRequired,
            0x000B => FrameKind::RegionRouteChange,
            0x000C => FrameKind::Error,
            0x000D => FrameKind::MutationRequest,
            0x000E => FrameKind::MutationAck,
            _ => return None,
        })
    }
}

/// The payload codec identity of a delivery frame: the only places
/// where byte payloads ride the protocol, each named and versioned.
/// Per ADR 0024 the payload codecs are TerraBase-native; the codes
/// begin at 3 so the superseded external-engine codec codes (1 and 2)
/// fail closed on decode instead of aliasing a native codec.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PayloadCodec {
    /// A native `terrabase-native/1` delta batch, codec version 1.
    NativeDelta1,
    /// A native `terrabase-snapshot/1` snapshot, codec version 1.
    NativeSnapshot1,
}

impl PayloadCodec {
    /// The codec's wire code.
    #[must_use]
    pub fn code(self) -> u8 {
        match self {
            PayloadCodec::NativeDelta1 => 3,
            PayloadCodec::NativeSnapshot1 => 4,
        }
    }

    /// The codec for a wire code, or `None` for unknown codes —
    /// including 1 and 2, the superseded external-engine codec codes
    /// this contract deliberately does not accept.
    #[must_use]
    pub fn from_code(code: u8) -> Option<Self> {
        Some(match code {
            3 => PayloadCodec::NativeDelta1,
            4 => PayloadCodec::NativeSnapshot1,
            _ => return None,
        })
    }

    /// The codec's contract name (`terrabase-native/1`), the identity
    /// the conformance vectors record.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            PayloadCodec::NativeDelta1 => "terrabase-native/1",
            PayloadCodec::NativeSnapshot1 => "terrabase-snapshot/1",
        }
    }
}

/// The closed fatal-error vocabulary shared by error frames and mutation
/// rejections. Codes are stable wire values.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorCode {
    /// The peer's protocol version is not spoken here (fail closed).
    UnsupportedVersion,
    /// A frame did not decode.
    MalformedFrame,
    /// A presented checkpoint was refused (foreign peer, ahead of the
    /// server).
    CheckpointRefused,
    /// A named scope or collection was refused.
    ScopeRefused,
    /// The session is ending or has ended.
    SessionClosed,
    /// The server is shedding load; retry after re-resolving.
    Overloaded,
}

impl ErrorCode {
    /// The code's wire value.
    #[must_use]
    pub fn code(self) -> u8 {
        match self {
            ErrorCode::UnsupportedVersion => 1,
            ErrorCode::MalformedFrame => 2,
            ErrorCode::CheckpointRefused => 3,
            ErrorCode::ScopeRefused => 4,
            ErrorCode::SessionClosed => 5,
            ErrorCode::Overloaded => 6,
        }
    }

    /// The code for a wire value, or `None` for unknown values.
    #[must_use]
    pub fn from_code(code: u8) -> Option<Self> {
        Some(match code {
            1 => ErrorCode::UnsupportedVersion,
            2 => ErrorCode::MalformedFrame,
            3 => ErrorCode::CheckpointRefused,
            4 => ErrorCode::ScopeRefused,
            5 => ErrorCode::SessionClosed,
            6 => ErrorCode::Overloaded,
            _ => return None,
        })
    }

    /// The stable name the conformance vectors record.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            ErrorCode::UnsupportedVersion => "unsupported_version",
            ErrorCode::MalformedFrame => "malformed_frame",
            ErrorCode::CheckpointRefused => "checkpoint_refused",
            ErrorCode::ScopeRefused => "scope_refused",
            ErrorCode::SessionClosed => "session_closed",
            ErrorCode::Overloaded => "overloaded",
        }
    }
}

/// Why the server demanded a resync.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResyncReason {
    /// The session's checkpoint is older than the retained floor.
    BehindRetainedFloor,
    /// History compaction raised the floor past the session.
    HistoryCompacted,
}

impl ResyncReason {
    /// The reason's wire value.
    #[must_use]
    pub fn code(self) -> u8 {
        match self {
            ResyncReason::BehindRetainedFloor => 1,
            ResyncReason::HistoryCompacted => 2,
        }
    }

    /// The reason for a wire value, or `None` for unknown values.
    #[must_use]
    pub fn from_code(code: u8) -> Option<Self> {
        Some(match code {
            1 => ResyncReason::BehindRetainedFloor,
            2 => ResyncReason::HistoryCompacted,
            _ => return None,
        })
    }

    /// The stable name the conformance vectors record.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            ResyncReason::BehindRetainedFloor => "behind-retained-floor",
            ResyncReason::HistoryCompacted => "history-compacted",
        }
    }
}

/// The write a mutation request carries: a typed document body, never
/// an encoded delta — the server is the single writer of record.
#[derive(Clone, Debug, PartialEq)]
pub enum DocumentWrite {
    /// Replace the document body with these fields.
    Put {
        /// The full new body of the document.
        body: BTreeMap<String, SyncValue>,
    },
    /// Delete the document.
    Delete,
}

/// The outcome of a mutation: the durable identities once applied, or a
/// typed rejection. There is no third state — mutations are
/// server-ack-only in v2 clients.
#[derive(Clone, Debug, PartialEq)]
pub enum MutationOutcome {
    /// The mutation is durably applied; the delta arrives as delivery.
    Applied {
        /// The storage sequence of the applied commit.
        storage_sequence: u64,
        /// The stable operation identity of the applied commit.
        operation_id: String,
    },
    /// The mutation was refused.
    Rejected {
        /// The typed refusal reason (the closed error vocabulary).
        reason: ErrorCode,
    },
}

/// The typed payload of one `terrascale.sync.v2` frame.
#[derive(Clone, Debug, PartialEq)]
pub enum SyncV2Body {
    /// Open a session, fresh (`resume` is `None`) or resuming from the
    /// session's durable checkpoint.
    SessionSetup {
        /// The collections the session subscribes to.
        collections: Vec<String>,
        /// The checkpoint a resuming session presents.
        resume: Option<SyncCheckpoint>,
    },
    /// The server accepts the session and states its checkpoint.
    SessionAccepted {
        /// The server-assigned session identity (diagnostics).
        session: u64,
        /// The server's current checkpoint.
        server: SyncCheckpoint,
        /// The subscription set now in force.
        collections: Vec<String>,
    },
    /// Modify the session's subscriptions.
    SubscriptionModify {
        /// Collections to add.
        add: Vec<String>,
        /// Collections to remove.
        remove: Vec<String>,
    },
    /// The subscription set now in force.
    SubscriptionChanged {
        /// The active subscription set.
        active: Vec<String>,
    },
    /// Acknowledge the session's resumption position; retransmission is
    /// bounded by the acknowledged checkpoint.
    CheckpointAck {
        /// The session's durable checkpoint.
        checkpoint: SyncCheckpoint,
    },
    /// Liveness probe; no payload.
    Heartbeat,
    /// One byte-budgeted delta batch.
    DeltaBatch {
        /// The payload codec: always [`PayloadCodec::NativeDelta1`].
        codec: PayloadCodec,
        /// The encoded delta bytes.
        payload: Vec<u8>,
        /// The checkpoint after applying this batch.
        next: SyncCheckpoint,
        /// Whether the batch fits the configured byte budget.
        within_budget: bool,
        /// True only when a single commit's delta alone exceeded the
        /// budget and the batch carries exactly that one commit.
        single_commit_oversize: bool,
        /// How many journal commits the batch carries.
        commits: u64,
    },
    /// One full-state answer (initial sync or forced resync).
    SnapshotAnswer {
        /// The payload codec: always [`PayloadCodec::NativeSnapshot1`].
        codec: PayloadCodec,
        /// The encoded snapshot bytes.
        payload: Vec<u8>,
        /// The checkpoint the snapshot corresponds to.
        checkpoint: SyncCheckpoint,
    },
    /// A content-free trigger notification: the collection scope plus
    /// the two commit identifiers. It never carries document data; the
    /// client answers by pulling delta batches from its checkpoint.
    Trigger {
        /// The collection the commit landed in.
        collection: String,
        /// The storage sequence of the published commit.
        storage_sequence: u64,
        /// The stable operation identity of the published commit.
        operation_id: String,
    },
    /// The session must resync: its checkpoint is below the floor.
    ResyncRequired {
        /// The oldest checkpoint deltas still serve from.
        floor: SyncCheckpoint,
        /// Why the resync was demanded.
        reason: ResyncReason,
    },
    /// The serving region changed; the client re-resolves the endpoint
    /// through the Platform and resumes from its checkpoint.
    RegionRouteChange {
        /// The new serving region's name.
        region: String,
    },
    /// A fatal error from the closed vocabulary.
    Error {
        /// The typed error code.
        code: ErrorCode,
        /// A bounded, machine-readable detail string (no secrets, no
        /// document data).
        detail: Option<String>,
    },
    /// A mutation request: typed document write plus the envelope's
    /// idempotency key. The only mutation frame a client may send.
    MutationRequest {
        /// The target collection.
        collection: String,
        /// The target document.
        document: String,
        /// The typed write.
        write: DocumentWrite,
    },
    /// The server's acknowledgement of a mutation: the only answer.
    MutationAck {
        /// The applied identities or the typed refusal.
        outcome: MutationOutcome,
    },
}

// --- Field limits (fail closed on both seal and decode) -------------------

/// The longest identifier string a frame carries (bytes, encoded).
pub const MAX_IDENT_BYTES: usize = 256;

/// The longest operation identity or idempotency key (bytes).
pub const MAX_KEY_BYTES: usize = 128;

/// The longest trigger collection or operation identity (bytes).
pub const MAX_TRIGGER_IDENT_BYTES: usize = 64;

/// The most entries one collection list carries.
pub const MAX_COLLECTIONS: usize = 256;

/// The most fields one mutation body carries.
pub const MAX_FIELDS: usize = 1024;

/// The longest error detail string (bytes).
pub const MAX_DETAIL_BYTES: usize = 256;

fn push_u8(out: &mut Vec<u8>, value: u8) {
    out.push(value);
}

fn push_u16(out: &mut Vec<u8>, value: u16) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn push_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn push_u64(out: &mut Vec<u8>, value: u64) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn push_str(out: &mut Vec<u8>, text: &str, limit: usize) -> Result<(), &'static str> {
    if text.len() > limit {
        return Err("string exceeds its frame limit");
    }
    push_u64(out, u64::try_from(text.len()).expect("bounded by the limit"));
    out.extend_from_slice(text.as_bytes());
    Ok(())
}

fn push_strings(out: &mut Vec<u8>, list: &[String]) -> Result<(), &'static str> {
    if list.len() > MAX_COLLECTIONS {
        return Err("too many collection entries");
    }
    push_u32(out, u32::try_from(list.len()).expect("bounded by the limit"));
    for entry in list {
        push_str(out, entry, MAX_IDENT_BYTES)?;
    }
    Ok(())
}

fn push_checkpoint(out: &mut Vec<u8>, checkpoint: &SyncCheckpoint) {
    let bytes = checkpoint.to_bytes();
    push_u64(out, u64::try_from(bytes.len()).expect("checkpoint length fits"));
    out.extend_from_slice(&bytes);
}

fn push_payload(out: &mut Vec<u8>, payload: &[u8]) {
    push_u64(out, u64::try_from(payload.len()).expect("payload length fits"));
    out.extend_from_slice(payload);
}

fn push_value(out: &mut Vec<u8>, value: &SyncValue) -> Result<(), &'static str> {
    match value {
        SyncValue::Null => push_u8(out, 0),
        SyncValue::Bool(b) => {
            push_u8(out, 1);
            push_u8(out, u8::from(*b));
        }
        SyncValue::Int(i) => {
            push_u8(out, 2);
            out.extend_from_slice(&i.to_le_bytes());
        }
        SyncValue::Float(x) => {
            push_u8(out, 3);
            out.extend_from_slice(&x.to_bits().to_le_bytes());
        }
        SyncValue::Str(s) => {
            push_u8(out, 4);
            // Values ride only inside mutation bodies, whose bounded
            // field text keeps frames small; the same ident limit applies
            // (validated before encoding runs).
            push_str(out, s, MAX_IDENT_BYTES)?;
        }
    }
    Ok(())
}

/// The decode cursor over one body: every read is bounds-checked, and
/// every failure is the same typed malformed-frame rejection.
struct BodyCursor<'a> {
    rest: &'a [u8],
}

impl BodyCursor<'_> {
    fn take(&mut self, n: usize) -> Result<&[u8], &'static str> {
        if self.rest.len() < n {
            Err("truncated frame body")
        } else {
            let (head, tail) = self.rest.split_at(n);
            self.rest = tail;
            Ok(head)
        }
    }

    fn u8(&mut self) -> Result<u8, &'static str> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16, &'static str> {
        let bytes = self.take(2)?;
        Ok(u16::from_le_bytes(bytes.try_into().expect("two bytes")))
    }

    fn u32(&mut self) -> Result<u32, &'static str> {
        let bytes = self.take(4)?;
        Ok(u32::from_le_bytes(bytes.try_into().expect("four bytes")))
    }

    fn u64(&mut self) -> Result<u64, &'static str> {
        let bytes = self.take(8)?;
        Ok(u64::from_le_bytes(bytes.try_into().expect("eight bytes")))
    }

    fn i64(&mut self) -> Result<i64, &'static str> {
        let bytes = self.take(8)?;
        Ok(i64::from_le_bytes(bytes.try_into().expect("eight bytes")))
    }

    fn f64(&mut self) -> Result<f64, &'static str> {
        let bits = self.u64()?;
        Ok(f64::from_bits(bits))
    }

    fn str(&mut self, limit: usize) -> Result<String, &'static str> {
        let len = self.u64()?;
        let len = usize::try_from(len).map_err(|_| "string length does not fit the platform")?;
        if len > limit {
            return Err("string exceeds its frame limit");
        }
        let bytes = self.take(len)?;
        String::from_utf8(bytes.to_vec()).map_err(|_| "string fields are utf-8")
    }

    fn strings(&mut self) -> Result<Vec<String>, &'static str> {
        let count = self.u32()? as usize;
        if count > MAX_COLLECTIONS {
            return Err("too many collection entries");
        }
        let mut list = Vec::with_capacity(count);
        for _ in 0..count {
            list.push(self.str(MAX_IDENT_BYTES)?);
        }
        Ok(list)
    }

    fn checkpoint(&mut self) -> Result<SyncCheckpoint, crate::SyncCoreError> {
        let len = self.u64().map_err(crate::SyncCoreError::InvalidFrame)?;
        let len = usize::try_from(len).map_err(|_| {
            crate::SyncCoreError::InvalidFrame("checkpoint length does not fit the platform")
        })?;
        if len > crate::MAX_CHECKPOINT_BYTES {
            return Err(crate::SyncCoreError::InvalidFrame("checkpoint exceeds its decode bound"));
        }
        let bytes = self.take(len).map_err(crate::SyncCoreError::InvalidFrame)?;
        SyncCheckpoint::from_bytes(bytes)
    }

    fn payload(&mut self, bound: usize) -> Result<Vec<u8>, &'static str> {
        let len = self.u64()?;
        let len = usize::try_from(len).map_err(|_| "payload length does not fit the platform")?;
        if len > bound {
            return Err("payload exceeds the frame bound");
        }
        Ok(self.take(len)?.to_vec())
    }

    fn value(&mut self) -> Result<SyncValue, &'static str> {
        Ok(match self.u8()? {
            0 => SyncValue::Null,
            1 => SyncValue::Bool(self.u8()? != 0),
            2 => SyncValue::Int(self.i64()?),
            3 => SyncValue::Float(self.f64()?),
            4 => SyncValue::Str(self.str(MAX_IDENT_BYTES)?),
            _ => return Err("unknown value tag"),
        })
    }

    fn end(&self) -> Result<(), &'static str> {
        if self.rest.is_empty() { Ok(()) } else { Err("trailing bytes in the frame body") }
    }
}

impl SyncV2Body {
    /// The body's frame kind.
    #[must_use]
    pub fn kind(&self) -> FrameKind {
        match self {
            SyncV2Body::SessionSetup { .. } => FrameKind::SessionSetup,
            SyncV2Body::SessionAccepted { .. } => FrameKind::SessionAccepted,
            SyncV2Body::SubscriptionModify { .. } => FrameKind::SubscriptionModify,
            SyncV2Body::SubscriptionChanged { .. } => FrameKind::SubscriptionChanged,
            SyncV2Body::CheckpointAck { .. } => FrameKind::CheckpointAck,
            SyncV2Body::Heartbeat => FrameKind::Heartbeat,
            SyncV2Body::DeltaBatch { .. } => FrameKind::DeltaBatch,
            SyncV2Body::SnapshotAnswer { .. } => FrameKind::SnapshotAnswer,
            SyncV2Body::Trigger { .. } => FrameKind::Trigger,
            SyncV2Body::ResyncRequired { .. } => FrameKind::ResyncRequired,
            SyncV2Body::RegionRouteChange { .. } => FrameKind::RegionRouteChange,
            SyncV2Body::Error { .. } => FrameKind::Error,
            SyncV2Body::MutationRequest { .. } => FrameKind::MutationRequest,
            SyncV2Body::MutationAck { .. } => FrameKind::MutationAck,
        }
    }

    /// Whether this body belongs to the mutation family: the only
    /// bodies whose frames carry an idempotency key.
    #[must_use]
    pub fn is_mutation(&self) -> bool {
        matches!(self, SyncV2Body::MutationRequest { .. } | SyncV2Body::MutationAck { .. })
    }

    /// Whether the body carries an encoded byte payload (the named
    /// payload codecs are the only byte payloads on this protocol).
    #[must_use]
    pub fn carries_payload(&self) -> bool {
        matches!(self, SyncV2Body::DeltaBatch { .. } | SyncV2Body::SnapshotAnswer { .. })
    }

    /// The byte length of the encoded payload, `0` for every frame
    /// class without a payload (the envelope-overhead accounting input).
    #[must_use]
    pub fn payload_len(&self) -> usize {
        match self {
            SyncV2Body::DeltaBatch { payload, .. } | SyncV2Body::SnapshotAnswer { payload, .. } => {
                payload.len()
            }
            _ => 0,
        }
    }

    /// Validates the body's invariants (limits, codec/kind pairing,
    /// flag consistency). Seal and decode both call this, so the wire
    /// never carries a body the codec would reject.
    ///
    /// # Errors
    ///
    /// Returns a static reason naming the violated invariant.
    pub fn validate(&self) -> Result<(), &'static str> {
        match self {
            SyncV2Body::SessionSetup { collections, resume } => {
                check_strings(collections)?;
                if let Some(checkpoint) = resume {
                    SyncCheckpoint::from_bytes(&checkpoint.to_bytes())
                        .map_err(|_| "the resume checkpoint does not round-trip")?;
                }
            }
            SyncV2Body::SessionAccepted { server, collections, .. } => {
                check_strings(collections)?;
                SyncCheckpoint::from_bytes(&server.to_bytes())
                    .map_err(|_| "the server checkpoint does not round-trip")?;
            }
            SyncV2Body::SubscriptionModify { add, remove } => {
                check_strings(add)?;
                check_strings(remove)?;
            }
            SyncV2Body::SubscriptionChanged { active } => check_strings(active)?,
            SyncV2Body::CheckpointAck { checkpoint } => {
                SyncCheckpoint::from_bytes(&checkpoint.to_bytes())
                    .map_err(|_| "the checkpoint does not round-trip")?;
            }
            SyncV2Body::Heartbeat => {}
            SyncV2Body::DeltaBatch {
                codec,
                payload,
                next,
                single_commit_oversize,
                commits,
                ..
            } => {
                validate_delta(*codec, payload, next, *single_commit_oversize, *commits)?;
            }
            SyncV2Body::SnapshotAnswer { codec, payload, checkpoint } => {
                validate_snapshot(*codec, payload, checkpoint)?;
            }
            SyncV2Body::Trigger { collection, operation_id, .. } => {
                if collection.len() > MAX_TRIGGER_IDENT_BYTES
                    || operation_id.len() > MAX_TRIGGER_IDENT_BYTES
                {
                    return Err("trigger identifiers are bounded to 64 bytes");
                }
            }
            SyncV2Body::ResyncRequired { floor, .. } => {
                SyncCheckpoint::from_bytes(&floor.to_bytes())
                    .map_err(|_| "the floor checkpoint does not round-trip")?;
            }
            SyncV2Body::RegionRouteChange { region } => {
                if region.len() > MAX_IDENT_BYTES {
                    return Err("string exceeds its frame limit");
                }
            }
            SyncV2Body::Error { detail, .. } => {
                if detail.as_ref().is_some_and(|d| d.len() > MAX_DETAIL_BYTES) {
                    return Err("the error detail is bounded");
                }
            }
            SyncV2Body::MutationRequest { collection, document, write } => {
                validate_mutation_request(collection, document, write)?;
            }
            SyncV2Body::MutationAck { outcome } => {
                if let MutationOutcome::Applied { operation_id, .. } = outcome
                    && operation_id.len() > MAX_KEY_BYTES
                {
                    return Err("string exceeds its frame limit");
                }
            }
        }
        Ok(())
    }

    /// The canonical body encoding: the digest input and the wire body.
    ///
    /// # Errors
    ///
    /// Returns a static reason when the body violates its invariants.
    ///
    /// # Panics
    ///
    /// Panics only on `usize` overflow past the validated bounds, which
    /// the field limits preclude.
    pub fn to_body_bytes(&self) -> Result<Vec<u8>, &'static str> {
        self.validate()?;
        let mut out = Vec::new();
        push_u16(&mut out, self.kind().code());
        match self {
            SyncV2Body::SessionSetup { collections, resume } => {
                push_strings(&mut out, collections)?;
                match resume {
                    Some(checkpoint) => {
                        push_u8(&mut out, 1);
                        push_checkpoint(&mut out, checkpoint);
                    }
                    None => push_u8(&mut out, 0),
                }
            }
            SyncV2Body::SessionAccepted { session, server, collections } => {
                push_u64(&mut out, *session);
                push_checkpoint(&mut out, server);
                push_strings(&mut out, collections)?;
            }
            SyncV2Body::SubscriptionModify { add, remove } => {
                push_strings(&mut out, add)?;
                push_strings(&mut out, remove)?;
            }
            SyncV2Body::SubscriptionChanged { active } => push_strings(&mut out, active)?,
            SyncV2Body::CheckpointAck { checkpoint } => push_checkpoint(&mut out, checkpoint),
            SyncV2Body::Heartbeat => {}
            SyncV2Body::DeltaBatch {
                codec,
                payload,
                next,
                within_budget,
                single_commit_oversize,
                commits,
            } => {
                push_u8(&mut out, codec.code());
                push_payload(&mut out, payload);
                push_checkpoint(&mut out, next);
                push_u8(&mut out, u8::from(*within_budget));
                push_u8(&mut out, u8::from(*single_commit_oversize));
                push_u64(&mut out, *commits);
            }
            SyncV2Body::SnapshotAnswer { codec, payload, checkpoint } => {
                push_u8(&mut out, codec.code());
                push_payload(&mut out, payload);
                push_checkpoint(&mut out, checkpoint);
            }
            SyncV2Body::Trigger { collection, storage_sequence, operation_id } => {
                push_str(&mut out, collection, MAX_TRIGGER_IDENT_BYTES)?;
                push_u64(&mut out, *storage_sequence);
                push_str(&mut out, operation_id, MAX_TRIGGER_IDENT_BYTES)?;
            }
            SyncV2Body::ResyncRequired { floor, reason } => {
                push_checkpoint(&mut out, floor);
                push_u8(&mut out, reason.code());
            }
            SyncV2Body::RegionRouteChange { region } => {
                push_str(&mut out, region, MAX_IDENT_BYTES)?;
            }
            SyncV2Body::Error { code, detail } => {
                push_u8(&mut out, code.code());
                match detail {
                    Some(text) => {
                        push_u8(&mut out, 1);
                        push_str(&mut out, text, MAX_DETAIL_BYTES)?;
                    }
                    None => push_u8(&mut out, 0),
                }
            }
            SyncV2Body::MutationRequest { collection, document, write } => {
                push_str(&mut out, collection, MAX_IDENT_BYTES)?;
                push_str(&mut out, document, MAX_IDENT_BYTES)?;
                match write {
                    DocumentWrite::Put { body } => {
                        push_u8(&mut out, 1);
                        push_u32(&mut out, u32::try_from(body.len()).expect("validated"));
                        for (key, value) in body {
                            push_str(&mut out, key, MAX_IDENT_BYTES)?;
                            push_value(&mut out, value)?;
                        }
                    }
                    DocumentWrite::Delete => push_u8(&mut out, 2),
                }
            }
            SyncV2Body::MutationAck { outcome } => match outcome {
                MutationOutcome::Applied { storage_sequence, operation_id } => {
                    push_u8(&mut out, 1);
                    push_u64(&mut out, *storage_sequence);
                    push_str(&mut out, operation_id, MAX_KEY_BYTES)?;
                }
                MutationOutcome::Rejected { reason } => {
                    push_u8(&mut out, 2);
                    push_u8(&mut out, reason.code());
                }
            },
        }
        Ok(out)
    }

    /// Decodes a canonical body, failing closed on every structural
    /// problem.
    ///
    /// # Errors
    ///
    /// Returns [`crate::SyncCoreError::InvalidFrame`] for structural
    /// problems, including nested checkpoint versions.
    pub fn from_body_bytes(bytes: &[u8]) -> Result<Self, crate::SyncCoreError> {
        let malformed = crate::SyncCoreError::InvalidFrame;
        let mut cursor = BodyCursor { rest: bytes };
        let kind = cursor.u16().map_err(malformed)?;
        let Some(kind) = FrameKind::from_code(kind) else {
            return Err(malformed("unknown frame kind"));
        };
        let body = match kind {
            FrameKind::SessionSetup => {
                let collections = cursor.strings().map_err(malformed)?;
                let resume = match cursor.u8().map_err(malformed)? {
                    0 => None,
                    1 => Some(cursor.checkpoint()?),
                    _ => return Err(malformed("unknown resume flag")),
                };
                SyncV2Body::SessionSetup { collections, resume }
            }
            FrameKind::SessionAccepted => {
                let session = cursor.u64().map_err(malformed)?;
                let server = cursor.checkpoint()?;
                let collections = cursor.strings().map_err(malformed)?;
                SyncV2Body::SessionAccepted { session, server, collections }
            }
            FrameKind::SubscriptionModify => {
                let add = cursor.strings().map_err(malformed)?;
                let remove = cursor.strings().map_err(malformed)?;
                SyncV2Body::SubscriptionModify { add, remove }
            }
            FrameKind::SubscriptionChanged => {
                let active = cursor.strings().map_err(malformed)?;
                SyncV2Body::SubscriptionChanged { active }
            }
            FrameKind::CheckpointAck => {
                let checkpoint = cursor.checkpoint()?;
                SyncV2Body::CheckpointAck { checkpoint }
            }
            FrameKind::Heartbeat => SyncV2Body::Heartbeat,
            FrameKind::DeltaBatch => decode_delta(&mut cursor)?,
            FrameKind::SnapshotAnswer => {
                let codec = PayloadCodec::from_code(cursor.u8().map_err(malformed)?)
                    .ok_or_else(|| malformed("unknown payload codec"))?;
                let payload = cursor.payload(super::MAX_PAYLOAD_BYTES).map_err(malformed)?;
                let checkpoint = cursor.checkpoint()?;
                SyncV2Body::SnapshotAnswer { codec, payload, checkpoint }
            }
            FrameKind::Trigger => {
                let collection = cursor.str(MAX_TRIGGER_IDENT_BYTES).map_err(malformed)?;
                let storage_sequence = cursor.u64().map_err(malformed)?;
                let operation_id = cursor.str(MAX_TRIGGER_IDENT_BYTES).map_err(malformed)?;
                SyncV2Body::Trigger { collection, storage_sequence, operation_id }
            }
            FrameKind::ResyncRequired => {
                let floor = cursor.checkpoint()?;
                let reason = ResyncReason::from_code(cursor.u8().map_err(malformed)?)
                    .ok_or_else(|| malformed("unknown resync reason"))?;
                SyncV2Body::ResyncRequired { floor, reason }
            }
            FrameKind::RegionRouteChange => {
                let region = cursor.str(MAX_IDENT_BYTES).map_err(malformed)?;
                SyncV2Body::RegionRouteChange { region }
            }
            FrameKind::Error => {
                let code = ErrorCode::from_code(cursor.u8().map_err(malformed)?)
                    .ok_or_else(|| malformed("unknown error code"))?;
                let detail = match cursor.u8().map_err(malformed)? {
                    0 => None,
                    1 => Some(cursor.str(MAX_DETAIL_BYTES).map_err(malformed)?),
                    _ => return Err(malformed("unknown detail flag")),
                };
                SyncV2Body::Error { code, detail }
            }
            FrameKind::MutationRequest => decode_mutation_request(&mut cursor)?,
            FrameKind::MutationAck => {
                let outcome = match cursor.u8().map_err(malformed)? {
                    1 => {
                        let storage_sequence = cursor.u64().map_err(malformed)?;
                        let operation_id = cursor.str(MAX_KEY_BYTES).map_err(malformed)?;
                        MutationOutcome::Applied { storage_sequence, operation_id }
                    }
                    2 => {
                        let reason = ErrorCode::from_code(cursor.u8().map_err(malformed)?)
                            .ok_or_else(|| malformed("unknown error code"))?;
                        MutationOutcome::Rejected { reason }
                    }
                    _ => return Err(malformed("unknown outcome tag")),
                };
                SyncV2Body::MutationAck { outcome }
            }
        };
        cursor.end().map_err(malformed)?;
        body.validate().map_err(malformed)?;
        Ok(body)
    }
}

fn decode_delta(cursor: &mut BodyCursor<'_>) -> Result<SyncV2Body, crate::SyncCoreError> {
    let malformed = crate::SyncCoreError::InvalidFrame;
    let codec = PayloadCodec::from_code(cursor.u8().map_err(malformed)?)
        .ok_or_else(|| malformed("unknown payload codec"))?;
    let payload = cursor.payload(super::MAX_PAYLOAD_BYTES).map_err(malformed)?;
    let next = cursor.checkpoint()?;
    let within_budget = cursor.u8().map_err(malformed)? != 0;
    let single_commit_oversize = cursor.u8().map_err(malformed)? != 0;
    let commits = cursor.u64().map_err(malformed)?;
    Ok(SyncV2Body::DeltaBatch {
        codec,
        payload,
        next,
        within_budget,
        single_commit_oversize,
        commits,
    })
}

fn decode_mutation_request(
    cursor: &mut BodyCursor<'_>,
) -> Result<SyncV2Body, crate::SyncCoreError> {
    let malformed = crate::SyncCoreError::InvalidFrame;
    let collection = cursor.str(MAX_IDENT_BYTES).map_err(malformed)?;
    let document = cursor.str(MAX_IDENT_BYTES).map_err(malformed)?;
    let write = match cursor.u8().map_err(malformed)? {
        1 => {
            let count = cursor.u32().map_err(malformed)? as usize;
            if count > MAX_FIELDS {
                return Err(malformed("too many mutation fields"));
            }
            let mut body = BTreeMap::new();
            for _ in 0..count {
                let key = cursor.str(MAX_IDENT_BYTES).map_err(malformed)?;
                let value = cursor.value().map_err(malformed)?;
                body.insert(key, value);
            }
            DocumentWrite::Put { body }
        }
        2 => DocumentWrite::Delete,
        _ => return Err(malformed("unknown write tag")),
    };
    Ok(SyncV2Body::MutationRequest { collection, document, write })
}

fn validate_delta(
    codec: PayloadCodec,
    payload: &[u8],
    next: &SyncCheckpoint,
    single_commit_oversize: bool,
    commits: u64,
) -> Result<(), &'static str> {
    if codec != PayloadCodec::NativeDelta1 {
        return Err("a delta batch carries the terrabase-native/1 codec only");
    }
    if payload.len() > super::MAX_PAYLOAD_BYTES {
        return Err("payload exceeds the frame bound");
    }
    if single_commit_oversize && commits != 1 {
        return Err("the oversize flag requires exactly one commit");
    }
    SyncCheckpoint::from_bytes(&next.to_bytes())
        .map_err(|_| "the next checkpoint does not round-trip")?;
    Ok(())
}

fn validate_snapshot(
    codec: PayloadCodec,
    payload: &[u8],
    checkpoint: &SyncCheckpoint,
) -> Result<(), &'static str> {
    if codec != PayloadCodec::NativeSnapshot1 {
        return Err("a snapshot answer carries the terrabase-snapshot/1 codec only");
    }
    if payload.len() > super::MAX_PAYLOAD_BYTES {
        return Err("payload exceeds the frame bound");
    }
    SyncCheckpoint::from_bytes(&checkpoint.to_bytes())
        .map_err(|_| "the snapshot checkpoint does not round-trip")?;
    Ok(())
}

fn validate_mutation_request(
    collection: &str,
    document: &str,
    write: &DocumentWrite,
) -> Result<(), &'static str> {
    if collection.len() > MAX_IDENT_BYTES || document.len() > MAX_IDENT_BYTES {
        return Err("string exceeds its frame limit");
    }
    if let DocumentWrite::Put { body } = write {
        if body.len() > MAX_FIELDS {
            return Err("too many mutation fields");
        }
        for (key, value) in body {
            if key.len() > MAX_IDENT_BYTES {
                return Err("string exceeds its frame limit");
            }
            if matches!(value, SyncValue::Str(s) if s.len() > MAX_IDENT_BYTES) {
                return Err("string exceeds its frame limit");
            }
        }
    }
    Ok(())
}

fn check_strings(list: &[String]) -> Result<(), &'static str> {
    if list.len() > MAX_COLLECTIONS {
        return Err("too many collection entries");
    }
    if list.iter().any(|entry| entry.len() > MAX_IDENT_BYTES) {
        return Err("string exceeds its frame limit");
    }
    Ok(())
}
