//! A byte-level hand encoder for minimal TBEP envelopes.
//!
//! This module writes size-prefixed FlatBuffers envelopes directly, with
//! no generated builder, so the golden vectors and hostile-buffer cases
//! are independent of the generated bindings: if a regeneration changed
//! the wire layout the pinned bytes encode against, these vectors and
//! the decode assertions catch it.
//!
//! The envelope layout it produces is exactly the layout the schema
//! describes. All offsets below are relative to the FlatBuffers region
//! (the byte after the 4-byte size prefix); all integers are
//! little-endian. The complete size-prefixed buffer is the verifier's
//! alignment origin, so the envelope table begins at absolute byte 32
//! and its `u64` feature field begins at absolute byte 40.
//!
//! ```text
//! offset  size  content
//!      0     4  u32 root offset (to the envelope table = 28, relative)
//!      4     4  file identifier "TBEP"
//!      8    20  envelope vtable: u16 len, u16 table_len, 8 field slots
//!     28    56  envelope table T
//!   T+0=28   4  i32 soffset back to the vtable (T - 8 = 20)
//!   T+4=32   2  u16 protocol_major     (field id 0)
//!   T+6=34   2  u16 protocol_minor     (field id 1)
//!   T+8=36   8  u64 required_features  (field id 2)
//!  T+16=44   2  u16 operation          (field id 3)
//!  T+18=46  16  correlation ULID       (field id 4)
//!  T+34=62  16  causation ULID         (field id 5)
//!  T+50=78   1  u8  body union tag     (field id 6)
//!  T+51=79   1  padding
//!  T+52=80   4  u32 body offset        (field id 7, relative to T+52)
//!     84     8  the body vtable
//!     92   ...  the body table and its descendants
//! ```

/// The mutable pieces of one hand-encoded envelope. Byte-level hostile
/// cases start from [`HandEnvelope::encode`] and then overwrite chosen
/// bytes.
#[derive(Debug, Clone)]
pub struct HandEnvelope {
    /// Envelope `protocol_major`.
    pub protocol_major: u16,
    /// Envelope `protocol_minor`.
    pub protocol_minor: u16,
    /// Envelope `required_features` bitset.
    pub required_features: u64,
    /// Envelope `operation` code.
    pub operation: u16,
    /// The 16 correlation bytes.
    pub correlation: [u8; 16],
    /// The 16 causation bytes.
    pub causation: [u8; 16],
    /// The body union tag byte.
    pub body_type: u8,
    /// The encoded body vtable, table, and descendants (appended at
    /// offset 84), or `None` to leave the body fields out entirely.
    pub body: Option<Vec<u8>>,
    /// When `false`, the correlation vtable slot is omitted (field
    /// absent) for the missing-correlation semantic case.
    pub correlation_present: bool,
    /// When `false`, the causation vtable slot is omitted.
    pub causation_present: bool,
}

impl HandEnvelope {
    /// A well-formed envelope for one operation with a pre-encoded body
    /// table and union tag.
    #[must_use]
    pub fn new(operation: u16, body_type: u8, body: Vec<u8>) -> Self {
        let mut correlation = [0_u8; 16];
        for (index, byte) in correlation.iter_mut().enumerate() {
            *byte = index as u8;
        }
        Self {
            protocol_major: 1,
            protocol_minor: 0,
            required_features: 0,
            operation,
            correlation,
            causation: [7_u8; 16],
            body_type,
            body: Some(body),
            correlation_present: true,
            causation_present: true,
        }
    }

    /// Encode to a complete size-prefixed buffer.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        const ROOT: usize = 0;
        const IDENTIFIER: usize = 4;
        const VTABLE: usize = 8;
        const TABLE: usize = 28;
        const TABLE_LEN: usize = 56;
        const BODY_OFFSET_FIELD: usize = TABLE + 52;
        const BODY_AT: usize = TABLE + TABLE_LEN;
        const BODY_TABLE_AT: usize = BODY_AT + 8;

        let body = self.body.clone().unwrap_or_default();
        let body_offset = (BODY_TABLE_AT - BODY_OFFSET_FIELD) as u32;

        let mut flat = vec![0_u8; BODY_AT];
        flat[ROOT..ROOT + 4].copy_from_slice(&(TABLE as u32).to_le_bytes());
        flat[IDENTIFIER..IDENTIFIER + 4].copy_from_slice(b"TBEP");
        // Vtable: 4 header bytes + 8 slots of 2 bytes.
        flat[VTABLE..VTABLE + 2].copy_from_slice(&20_u16.to_le_bytes());
        flat[VTABLE + 2..VTABLE + 4].copy_from_slice(&(TABLE_LEN as u16).to_le_bytes());
        let mut slots = [0_u16; 8];
        slots[0] = 4; // protocol_major
        slots[1] = 6; // protocol_minor
        slots[2] = 8; // required_features
        slots[3] = 16; // operation
        if self.correlation_present {
            slots[4] = 18; // correlation
        }
        if self.causation_present {
            slots[5] = 34; // causation
        }
        if self.body.is_some() {
            slots[6] = 50; // body_type
            slots[7] = 52; // body
        }
        for (index, slot) in slots.iter().enumerate() {
            let at = VTABLE + 4 + index * 2;
            flat[at..at + 2].copy_from_slice(&slot.to_le_bytes());
        }
        // Envelope table.
        flat[TABLE..TABLE + 4].copy_from_slice(&((TABLE - VTABLE) as i32).to_le_bytes());
        flat[TABLE + 4..TABLE + 6].copy_from_slice(&self.protocol_major.to_le_bytes());
        flat[TABLE + 6..TABLE + 8].copy_from_slice(&self.protocol_minor.to_le_bytes());
        flat[TABLE + 8..TABLE + 16].copy_from_slice(&self.required_features.to_le_bytes());
        flat[TABLE + 16..TABLE + 18].copy_from_slice(&self.operation.to_le_bytes());
        flat[TABLE + 18..TABLE + 34].copy_from_slice(&self.correlation);
        flat[TABLE + 34..TABLE + 50].copy_from_slice(&self.causation);
        flat[TABLE + 50] = self.body_type;
        flat[TABLE + 52..TABLE + 56].copy_from_slice(&body_offset.to_le_bytes());
        flat.extend_from_slice(&body);

        let mut framed = Vec::with_capacity(flat.len() + 4);
        framed.extend_from_slice(&(flat.len() as u32).to_le_bytes());
        framed.extend_from_slice(&flat);
        framed
    }
}

/// The union tag of [`crate::tbep_generated::terrabase::tbep::TbepFrame`]'s
/// first member, `ErrorFrame` (tags are assigned in declaration order).
pub const ERROR_FRAME_TAG: u8 = 1;

/// The union tag of the `DocumentWriteResponse` member of `TbepFrame`
/// (fifth declared member).
pub const DOCUMENT_WRITE_RESPONSE_TAG: u8 = 9;

/// Encode a minimal well-formed `ErrorFrame` body table: error code plus
/// a NUL-terminated detail string. Offsets are relative to the returned
/// slice, so callers append it at a 4-byte-aligned envelope offset.
///
/// ```text
///   0     8  vtable: u16 len=8, u16 table_len=12, slot0=4, slot1=8
///   8    12  table: i32 soffset=8 (back to the vtable), u16 code,
///                 2 pad, u32 detail offset (=4, relative to its own
///                 position at 16)
///  20     4  u32 detail length
///  24   ...  detail bytes, then one NUL terminator
/// ```
#[must_use]
pub fn encode_error_frame(code: u16, detail: &str) -> Vec<u8> {
    let mut out = Vec::with_capacity(24 + detail.len() + 1);
    out.extend_from_slice(&8_u16.to_le_bytes()); // vtable length
    out.extend_from_slice(&12_u16.to_le_bytes()); // table length
    out.extend_from_slice(&4_u16.to_le_bytes()); // code slot (id 0)
    out.extend_from_slice(&8_u16.to_le_bytes()); // detail slot (id 1)
    out.extend_from_slice(&8_i32.to_le_bytes()); // table soffset (back to 0)
    out.extend_from_slice(&code.to_le_bytes()); // code at table+4
    out.extend_from_slice(&[0, 0]); // pad to table+8
    out.extend_from_slice(&4_u32.to_le_bytes()); // detail offset at table+8
    out.extend_from_slice(&(detail.len() as u32).to_le_bytes());
    out.extend_from_slice(detail.as_bytes());
    out.push(0);
    out
}

/// Encode a minimal `DocumentWriteResponse` body table whose `outcome`
/// union tag and offset are fully controlled by the caller. Hostile
/// cases use this to target misaligned or out-of-bounds nested tables.
#[must_use]
pub fn encode_document_write_response(outcome_type: u8, outcome_offset: u32) -> Vec<u8> {
    let mut out = Vec::with_capacity(20);
    out.extend_from_slice(&8_u16.to_le_bytes()); // vtable length
    out.extend_from_slice(&12_u16.to_le_bytes()); // table length
    out.extend_from_slice(&4_u16.to_le_bytes()); // outcome_type slot (id 0)
    out.extend_from_slice(&8_u16.to_le_bytes()); // outcome slot (id 1)
    out.extend_from_slice(&8_i32.to_le_bytes()); // table soffset
    out.push(outcome_type); // at table+4
    out.extend_from_slice(&[0, 0, 0]); // pad to table+8
    out.extend_from_slice(&outcome_offset.to_le_bytes()); // at table+8
    out
}
