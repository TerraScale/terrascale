//! The typed frame-body vocabulary of `terrascale.sync.v1`: one enum
//! variant per frame kind, each with a canonical byte encoding that is
//! the digest input and the wire body.
//!
//! The CRDT-era kinds (checkpoint acknowledgement, delta batches,
//! snapshot answers, resync demands, mutation requests and acks) were
//! deleted with the per-field materialization they carried (S-04,
//! D-38); their wire codes fail closed on decode until the result-view
//! delivery vocabulary replaces them.

use super::ProtocolError;

/// The registry identity of one frame kind. The codes are stable wire
/// values every SDK implements; unknown codes fail closed on decode.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FrameKind {
    /// Client: open a session.
    SessionSetup,
    /// Server: the session is accepted.
    SessionAccepted,
    /// Client: modify the session's collection subscriptions.
    SubscriptionModify,
    /// Server: the subscription set now in force.
    SubscriptionChanged,
    /// Either direction: liveness probe with no payload.
    Heartbeat,
    /// Server: a content-free trigger notification (push the hint,
    /// pull the revision).
    Trigger,
    /// Server: the serving region changed; re-resolve and resume.
    RegionRouteChange,
    /// Server: a fatal error from the closed error vocabulary.
    Error,
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
            FrameKind::Heartbeat => 0x0006,
            FrameKind::Trigger => 0x0009,
            FrameKind::RegionRouteChange => 0x000B,
            FrameKind::Error => 0x000C,
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
            0x0006 => FrameKind::Heartbeat,
            0x0009 => FrameKind::Trigger,
            0x000B => FrameKind::RegionRouteChange,
            0x000C => FrameKind::Error,
            _ => return None,
        })
    }
}

/// The closed fatal-error vocabulary shared by error frames. Codes are
/// stable wire values.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorCode {
    /// The peer's protocol version is not spoken here (fail closed).
    UnsupportedVersion,
    /// A frame did not decode.
    MalformedFrame,
    /// A presented checkpoint was refused.
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

/// The typed payload of one `terrascale.sync.v1` frame.
#[derive(Clone, Debug, PartialEq)]
pub enum SyncV1Body {
    /// Open a session.
    SessionSetup {
        /// The collections the session subscribes to.
        collections: Vec<String>,
    },
    /// The server accepts the session.
    SessionAccepted {
        /// The server-assigned session identity (diagnostics).
        session: u64,
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
    /// Liveness probe; no payload.
    Heartbeat,
    /// A content-free trigger notification: the collection scope plus
    /// the two commit identifiers. It never carries document data.
    Trigger {
        /// The collection the commit landed in.
        collection: String,
        /// The storage sequence of the published commit.
        storage_sequence: u64,
        /// The stable operation identity of the published commit.
        operation_id: String,
    },
    /// The serving region changed; the client re-resolves the endpoint
    /// through the Platform and resumes.
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
}

// --- Field limits (fail closed on both seal and decode) -------------------

/// The longest identifier string a frame carries (bytes, encoded).
pub const MAX_IDENT_BYTES: usize = 256;

/// The longest trigger collection or operation identity (bytes).
pub const MAX_TRIGGER_IDENT_BYTES: usize = 64;

/// The most entries one collection list carries.
pub const MAX_COLLECTIONS: usize = 256;

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

    fn end(&self) -> Result<(), &'static str> {
        if self.rest.is_empty() { Ok(()) } else { Err("trailing bytes in the frame body") }
    }
}

impl SyncV1Body {
    /// The body's frame kind.
    #[must_use]
    pub fn kind(&self) -> FrameKind {
        match self {
            SyncV1Body::SessionSetup { .. } => FrameKind::SessionSetup,
            SyncV1Body::SessionAccepted { .. } => FrameKind::SessionAccepted,
            SyncV1Body::SubscriptionModify { .. } => FrameKind::SubscriptionModify,
            SyncV1Body::SubscriptionChanged { .. } => FrameKind::SubscriptionChanged,
            SyncV1Body::Heartbeat => FrameKind::Heartbeat,
            SyncV1Body::Trigger { .. } => FrameKind::Trigger,
            SyncV1Body::RegionRouteChange { .. } => FrameKind::RegionRouteChange,
            SyncV1Body::Error { .. } => FrameKind::Error,
        }
    }

    /// Whether this body belongs to the mutation family: the only
    /// bodies whose frames carry an idempotency key. The CRDT-era
    /// client write path was deleted with the per-field materialization
    /// (S-04, D-38); no mutation kinds exist in this vocabulary, so the
    /// answer is always `false` and a frame carrying a key fails closed
    /// on the envelope's pairing rule until new mutation kinds land.
    #[must_use]
    pub fn is_mutation(&self) -> bool {
        false
    }

    /// Validates the body's invariants (limits, flag consistency). Seal
    /// and decode both call this, so the wire never carries a body the
    /// codec would reject.
    ///
    /// # Errors
    ///
    /// Returns a static reason naming the violated invariant.
    pub fn validate(&self) -> Result<(), &'static str> {
        match self {
            SyncV1Body::SessionSetup { collections }
            | SyncV1Body::SessionAccepted { collections, .. } => check_strings(collections)?,
            SyncV1Body::SubscriptionModify { add, remove } => {
                check_strings(add)?;
                check_strings(remove)?;
            }
            SyncV1Body::SubscriptionChanged { active } => check_strings(active)?,
            SyncV1Body::Heartbeat => {}
            SyncV1Body::Trigger { collection, operation_id, .. } => {
                if collection.len() > MAX_TRIGGER_IDENT_BYTES
                    || operation_id.len() > MAX_TRIGGER_IDENT_BYTES
                {
                    return Err("trigger identifiers are bounded to 64 bytes");
                }
            }
            SyncV1Body::RegionRouteChange { region } => {
                if region.len() > MAX_IDENT_BYTES {
                    return Err("string exceeds its frame limit");
                }
            }
            SyncV1Body::Error { detail, .. } => {
                if detail.as_ref().is_some_and(|d| d.len() > MAX_DETAIL_BYTES) {
                    return Err("the error detail is bounded");
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
            SyncV1Body::SessionSetup { collections } => push_strings(&mut out, collections)?,
            SyncV1Body::SessionAccepted { session, collections } => {
                push_u64(&mut out, *session);
                push_strings(&mut out, collections)?;
            }
            SyncV1Body::SubscriptionModify { add, remove } => {
                push_strings(&mut out, add)?;
                push_strings(&mut out, remove)?;
            }
            SyncV1Body::SubscriptionChanged { active } => push_strings(&mut out, active)?,
            SyncV1Body::Heartbeat => {}
            SyncV1Body::Trigger { collection, storage_sequence, operation_id } => {
                push_str(&mut out, collection, MAX_TRIGGER_IDENT_BYTES)?;
                push_u64(&mut out, *storage_sequence);
                push_str(&mut out, operation_id, MAX_TRIGGER_IDENT_BYTES)?;
            }
            SyncV1Body::RegionRouteChange { region } => {
                push_str(&mut out, region, MAX_IDENT_BYTES)?;
            }
            SyncV1Body::Error { code, detail } => {
                push_u8(&mut out, code.code());
                match detail {
                    Some(text) => {
                        push_u8(&mut out, 1);
                        push_str(&mut out, text, MAX_DETAIL_BYTES)?;
                    }
                    None => push_u8(&mut out, 0),
                }
            }
        }
        Ok(out)
    }

    /// Decodes a canonical body, failing closed on every structural
    /// problem.
    ///
    /// # Errors
    ///
    /// Returns [`ProtocolError::MalformedFrame`] for structural
    /// problems, including unknown frame kinds.
    pub fn from_body_bytes(bytes: &[u8]) -> Result<Self, ProtocolError> {
        let malformed = ProtocolError::MalformedFrame;
        let mut cursor = BodyCursor { rest: bytes };
        let kind = cursor.u16().map_err(malformed)?;
        let Some(kind) = FrameKind::from_code(kind) else {
            return Err(malformed("unknown frame kind"));
        };
        let body = match kind {
            FrameKind::SessionSetup => {
                let collections = cursor.strings().map_err(malformed)?;
                SyncV1Body::SessionSetup { collections }
            }
            FrameKind::SessionAccepted => {
                let session = cursor.u64().map_err(malformed)?;
                let collections = cursor.strings().map_err(malformed)?;
                SyncV1Body::SessionAccepted { session, collections }
            }
            FrameKind::SubscriptionModify => {
                let add = cursor.strings().map_err(malformed)?;
                let remove = cursor.strings().map_err(malformed)?;
                SyncV1Body::SubscriptionModify { add, remove }
            }
            FrameKind::SubscriptionChanged => {
                let active = cursor.strings().map_err(malformed)?;
                SyncV1Body::SubscriptionChanged { active }
            }
            FrameKind::Heartbeat => SyncV1Body::Heartbeat,
            FrameKind::Trigger => {
                let collection = cursor.str(MAX_TRIGGER_IDENT_BYTES).map_err(malformed)?;
                let storage_sequence = cursor.u64().map_err(malformed)?;
                let operation_id = cursor.str(MAX_TRIGGER_IDENT_BYTES).map_err(malformed)?;
                SyncV1Body::Trigger { collection, storage_sequence, operation_id }
            }
            FrameKind::RegionRouteChange => {
                let region = cursor.str(MAX_IDENT_BYTES).map_err(malformed)?;
                SyncV1Body::RegionRouteChange { region }
            }
            FrameKind::Error => {
                let code = ErrorCode::from_code(cursor.u8().map_err(malformed)?)
                    .ok_or_else(|| malformed("unknown error code"))?;
                let detail = match cursor.u8().map_err(malformed)? {
                    0 => None,
                    1 => Some(cursor.str(MAX_DETAIL_BYTES).map_err(malformed)?),
                    _ => return Err(malformed("unknown detail flag")),
                };
                SyncV1Body::Error { code, detail }
            }
        };
        cursor.end().map_err(malformed)?;
        body.validate().map_err(malformed)?;
        Ok(body)
    }
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
