//! Admission-aware tonic codec for the incoming replication stream.

use std::sync::{Arc, Mutex, MutexGuard};

use bytes::Buf;
use prost::Message;
use tonic::Status;
use tonic::codec::{BufferSettings, Codec, DecodeBuf, Decoder, EncodeBuf, Encoder};

use tb_core::{Attribution, MemoryBudget, WorkReservation};

use crate::{
    AdmissionController, BatchPermit, MAX_DECODED_APPLICATION_BYTES, MAX_ENCODED_APPLICATION_BYTES,
    MAX_EVENT_METADATA_BYTES, MAX_GRPC_MESSAGE_BYTES, MAX_TRANSACTION_GROUP_BYTES,
    PreAdmittedBatch, ProtocolError, v1,
};

/// Per-frame observation gate, called with the next logical byte charge before
/// process admission and before Prost materializes a request.
pub type ReceiveMemoryGate = Arc<dyn Fn(usize) -> bool + Send + Sync + 'static>;

/// Process and peer admission state for one incoming `Sync` RPC.
///
/// A stream must decode one Hello first. The handler installs the peer budget
/// only after authenticating and validating that Hello; until then the codec
/// refuses to decode another request. `process_budget` is cloned into each
/// codec invocation so its guards remain charged through request handling.
pub struct SyncAdmissionContext {
    process_budget: MemoryBudget,
    attribution: Attribution,
    memory_gate: Option<ReceiveMemoryGate>,
    phase: Mutex<AdmissionPhase>,
}

#[derive(Clone)]
enum AdmissionPhase {
    HelloOnly,
    AwaitingPeerAuthorization,
    Authorized(AdmissionController),
}

impl SyncAdmissionContext {
    /// Creates a context in Hello-only mode with no cgroup/RSS observation hook.
    #[must_use]
    pub fn new(process_budget: MemoryBudget, attribution: Attribution) -> Self {
        Self {
            process_budget,
            attribution,
            memory_gate: None,
            phase: Mutex::new(AdmissionPhase::HelloOnly),
        }
    }

    /// Installs the synchronous cgroup/RSS admission check for each frame.
    ///
    /// The gate receives [`MAX_PREDECODE_RESERVATION_BYTES`] before the codec
    /// admits the process work or invokes Prost. Returning false rejects the
    /// frame with [`ProtocolError::ProcessHeadroomRejected`].
    #[must_use]
    pub fn with_receive_memory_gate(mut self, gate: ReceiveMemoryGate) -> Self {
        self.memory_gate = Some(gate);
        self
    }

    /// Enables request decoding after the stream's Hello was authenticated and
    /// validated by its service handler.
    ///
    /// This transition is one-way and must occur before the handler polls the
    /// next request. It installs the per-peer controller used for each batch.
    ///
    /// # Errors
    ///
    /// Returns [`ProtocolError::Invalid`] unless the codec has decoded exactly
    /// one first Hello and is awaiting this authorization step.
    pub fn authorize_peer(&self, admission: AdmissionController) -> Result<(), ProtocolError> {
        let mut phase = self.lock_phase();
        if !matches!(*phase, AdmissionPhase::AwaitingPeerAuthorization) {
            return Err(ProtocolError::Invalid(
                "peer admission installed outside the post-Hello state",
            ));
        }
        *phase = AdmissionPhase::Authorized(admission);
        Ok(())
    }

    fn lock_phase(&self) -> MutexGuard<'_, AdmissionPhase> {
        self.phase
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    fn reserve_frame(
        &self,
        kind: PreflightKind,
        peer_bytes: Option<usize>,
    ) -> Result<FrameAdmission, ProtocolError> {
        let admission = {
            let phase = self.lock_phase();
            match (&*phase, kind) {
                (AdmissionPhase::HelloOnly, PreflightKind::Hello) => None,
                (AdmissionPhase::HelloOnly, _) => {
                    return Err(ProtocolError::Invalid(
                        "the first replication request must be Hello",
                    ));
                }
                (AdmissionPhase::AwaitingPeerAuthorization, _) => {
                    return Err(ProtocolError::Invalid(
                        "peer authorization is required before the next request",
                    ));
                }
                (AdmissionPhase::Authorized(_), PreflightKind::Hello) => {
                    return Err(ProtocolError::Invalid(
                        "Hello may only be sent as the first replication request",
                    ));
                }
                (AdmissionPhase::Authorized(admission), _) => Some(admission.clone()),
            }
        };

        if self
            .memory_gate
            .as_ref()
            .is_some_and(|gate| !gate(MAX_PREDECODE_RESERVATION_BYTES))
        {
            return Err(ProtocolError::ProcessHeadroomRejected);
        }

        let predecode_work = self.process_budget.try_reserve_work(
            self.attribution,
            MAX_PREDECODE_RESERVATION_BYTES,
            1,
        )?;

        match (kind, admission, peer_bytes) {
            (PreflightKind::Hello, None, None) => Ok(FrameAdmission::Hello(predecode_work)),
            (PreflightKind::CommittedGroup, Some(admission), Some(bytes)) => {
                let peer_permit = admission.try_admit_batch(bytes)?;
                Ok(FrameAdmission::CommittedGroup {
                    peer_permit,
                    predecode_work,
                })
            }
            (PreflightKind::CreditUpdate | PreflightKind::KeepAlive, Some(_), None) => {
                Ok(FrameAdmission::Control(predecode_work))
            }
            _ => Err(ProtocolError::Invalid("invalid preflight admission state")),
        }
    }

    fn hello_decoded(&self) -> Result<(), ProtocolError> {
        let mut phase = self.lock_phase();
        if !matches!(*phase, AdmissionPhase::HelloOnly) {
            return Err(ProtocolError::Invalid(
                "replication Hello decoded outside the first-frame state",
            ));
        }
        *phase = AdmissionPhase::AwaitingPeerAuthorization;
        Ok(())
    }
}

/// Tonic codec for a bidirectional `Sync` RPC with pre-Prost request admission.
pub struct SyncCodec {
    context: Arc<SyncAdmissionContext>,
}

impl SyncCodec {
    /// Creates a codec bound to one RPC's admission context.
    #[must_use]
    pub fn new(context: Arc<SyncAdmissionContext>) -> Self {
        Self { context }
    }
}

impl Codec for SyncCodec {
    type Encode = v1::SyncResponse;
    type Decode = AdmittedSyncFrame;
    type Encoder = SyncResponseEncoder;
    type Decoder = SyncRequestDecoder;

    fn encoder(&mut self) -> Self::Encoder {
        SyncResponseEncoder
    }

    fn decoder(&mut self) -> Self::Decoder {
        SyncRequestDecoder {
            context: Arc::clone(&self.context),
        }
    }
}

/// One request with the RAII admission token coupled to its request variant.
///
/// The committed batch is held by `PreAdmittedBatch`, avoiding a second copy
/// of its potentially large Protobuf fields and making the frame and permits
/// impossible to mismatch.
pub enum AdmittedSyncFrame {
    /// First stream request; retain its process guard through handshake work.
    Hello {
        /// Decoded Hello request.
        request: v1::Hello,
        /// Process reservation made before Prost decoding.
        permit: WorkReservation,
    },
    /// Committed batch whose exact request and both permits are coupled.
    CommittedGroup(PreAdmittedBatch),
    /// Credit update with its process guard.
    CreditUpdate {
        /// Decoded credit update.
        request: v1::CreditUpdate,
        /// Process reservation made before Prost decoding.
        permit: WorkReservation,
    },
    /// Keep-alive with its process guard.
    KeepAlive {
        /// Decoded keep-alive request.
        request: v1::KeepAlive,
        /// Process reservation made before Prost decoding.
        permit: WorkReservation,
    },
}

/// Fixed conservative pre-Prost process reservation for one Sync frame.
///
/// This includes room for the maximum gRPC message, retained Protobuf byte
/// fields, decoder bookkeeping, and one work item. A separate reservation is
/// charged later for compacted/decompressed transport-group state.
pub const MAX_PREDECODE_RESERVATION_BYTES: usize = 17_825_792;

const MAX_PREFLIGHT_TOKENS: usize = 8_192;
const MAX_PREFLIGHT_DEPTH: usize = 8;

pub struct SyncResponseEncoder;

impl Encoder for SyncResponseEncoder {
    type Item = v1::SyncResponse;
    type Error = Status;

    fn encode(&mut self, item: Self::Item, dst: &mut EncodeBuf<'_>) -> Result<(), Self::Error> {
        item.encode(dst)
            .map_err(|error| Status::internal(format!("encode Sync response: {error}")))
    }

    fn buffer_settings(&self) -> BufferSettings {
        BufferSettings::new(16 * 1024, 32 * 1024)
    }
}

pub struct SyncRequestDecoder {
    context: Arc<SyncAdmissionContext>,
}

impl Decoder for SyncRequestDecoder {
    type Item = AdmittedSyncFrame;
    type Error = Status;

    fn decode(&mut self, src: &mut DecodeBuf<'_>) -> Result<Option<Self::Item>, Self::Error> {
        let message_bytes = src.chunk();
        if message_bytes.len() != src.remaining() {
            return Err(Status::invalid_argument(
                "non-contiguous Sync request message buffer",
            ));
        }
        let preflight =
            preflight_request(message_bytes).map_err(|error| protocol_status(&error))?;
        let permit = self
            .context
            .reserve_frame(preflight.kind, preflight.peer_bytes)
            .map_err(|error| protocol_status(&error))?;
        let request = v1::SyncRequest::decode(&mut *src)
            .map_err(|error| Status::invalid_argument(format!("decode Sync request: {error}")))?;
        if src.has_remaining() {
            return Err(Status::invalid_argument(
                "Prost left trailing bytes in the Sync request",
            ));
        }

        let frame = match (preflight.kind, request.frame, permit) {
            (
                PreflightKind::Hello,
                Some(v1::sync_request::Frame::Hello(request)),
                FrameAdmission::Hello(permit),
            ) => {
                self.context
                    .hello_decoded()
                    .map_err(|error| protocol_status(&error))?;
                AdmittedSyncFrame::Hello { request, permit }
            }
            (
                PreflightKind::CommittedGroup,
                Some(v1::sync_request::Frame::CommittedGroup(batch)),
                FrameAdmission::CommittedGroup {
                    peer_permit,
                    predecode_work,
                },
            ) => AdmittedSyncFrame::CommittedGroup(PreAdmittedBatch::new(
                batch,
                peer_permit,
                predecode_work,
            )),
            (
                PreflightKind::CreditUpdate,
                Some(v1::sync_request::Frame::Credit(request)),
                FrameAdmission::Control(permit),
            ) => AdmittedSyncFrame::CreditUpdate { request, permit },
            (
                PreflightKind::KeepAlive,
                Some(v1::sync_request::Frame::KeepAlive(request)),
                FrameAdmission::Control(permit),
            ) => AdmittedSyncFrame::KeepAlive { request, permit },
            _ => {
                return Err(Status::invalid_argument(
                    "Sync request changed between preflight and Prost decoding",
                ));
            }
        };
        Ok(Some(frame))
    }

    fn buffer_settings(&self) -> BufferSettings {
        BufferSettings::new(16 * 1024, 32 * 1024)
    }
}

enum FrameAdmission {
    Hello(WorkReservation),
    CommittedGroup {
        peer_permit: BatchPermit,
        predecode_work: WorkReservation,
    },
    Control(WorkReservation),
}

#[derive(Clone, Copy, Debug)]
enum PreflightKind {
    Hello,
    CommittedGroup,
    CreditUpdate,
    KeepAlive,
}

struct Preflight {
    kind: PreflightKind,
    peer_bytes: Option<usize>,
}

fn protocol_status(error: &ProtocolError) -> Status {
    match error {
        ProtocolError::Admission(_)
        | ProtocolError::ProcessAdmission(_)
        | ProtocolError::ProcessHeadroomRejected => Status::resource_exhausted(error.to_string()),
        _ => Status::invalid_argument(error.to_string()),
    }
}

fn preflight_request(bytes: &[u8]) -> Result<Preflight, ProtocolError> {
    if bytes.is_empty() || bytes.len() > MAX_GRPC_MESSAGE_BYTES {
        return Err(ProtocolError::LimitExceeded("gRPC request bytes"));
    }
    let mut cursor = 0;
    let mut budget = PreflightBudget::default();
    let mut frame: Option<(PreflightKind, &[u8])> = None;
    while cursor < bytes.len() {
        let (field, _wire_type, value) = next_preflight_field(bytes, &mut cursor, &mut budget)?;
        if (1..=4).contains(&field) {
            let FieldValue::Bytes(payload) = value else {
                return Err(ProtocolError::Invalid(
                    "Sync frame must be length-delimited",
                ));
            };
            let kind = match field {
                1 => PreflightKind::Hello,
                2 => PreflightKind::CommittedGroup,
                3 => PreflightKind::CreditUpdate,
                4 => PreflightKind::KeepAlive,
                _ => unreachable!("field range checked"),
            };
            if frame.replace((kind, payload)).is_some() {
                return Err(ProtocolError::Invalid("multiple Sync oneof variants"));
            }
        }
    }
    let (kind, payload) = frame.ok_or(ProtocolError::Invalid("missing Sync request frame"))?;
    let peer_bytes = match kind {
        PreflightKind::Hello => {
            preflight_message(PreflightMessage::Hello, payload, &mut budget, 1)?;
            None
        }
        PreflightKind::CommittedGroup => Some(preflight_batch(payload, &mut budget)?),
        PreflightKind::CreditUpdate => {
            preflight_message(PreflightMessage::CreditUpdate, payload, &mut budget, 1)?;
            None
        }
        PreflightKind::KeepAlive => {
            preflight_message(PreflightMessage::KeepAlive, payload, &mut budget, 1)?;
            None
        }
    };
    Ok(Preflight { kind, peer_bytes })
}

fn preflight_batch(bytes: &[u8], budget: &mut PreflightBudget) -> Result<usize, ProtocolError> {
    let mut cursor = 0;
    let mut wal_bytes = None;
    let mut metadata_bytes = None;
    let mut encoded_bytes = None;
    let mut decoded_bytes = None;
    let mut envelope_len = None;
    let mut metadata_len = None;
    while cursor < bytes.len() {
        let (field, _wire_type, value) = next_preflight_field(bytes, &mut cursor, budget)?;
        if field == 8
            && let FieldValue::Bytes(event_id) = value
        {
            preflight_message(PreflightMessage::CommittedEventId, event_id, budget, 2)?;
        }
        match field {
            12 | 13 | 15 | 16 => {
                let FieldValue::Varint(value) = value else {
                    return Err(ProtocolError::Invalid("batch preflight integer wire type"));
                };
                let value = usize::try_from(value)
                    .map_err(|_| ProtocolError::LimitExceeded("preflight batch declaration"))?;
                let slot = match field {
                    12 => &mut wal_bytes,
                    13 => &mut metadata_bytes,
                    15 => &mut encoded_bytes,
                    16 => &mut decoded_bytes,
                    _ => unreachable!("field range checked"),
                };
                if slot.replace(value).is_some() {
                    return Err(ProtocolError::Invalid("duplicate batch preflight field"));
                }
            }
            17 | 18 => {
                let FieldValue::Bytes(payload) = value else {
                    return Err(ProtocolError::Invalid("batch preflight byte wire type"));
                };
                let slot = if field == 17 {
                    &mut envelope_len
                } else {
                    &mut metadata_len
                };
                if slot.replace(payload.len()).is_some() {
                    return Err(ProtocolError::Invalid("duplicate batch preflight field"));
                }
            }
            _ => {}
        }
    }

    let wal_bytes = wal_bytes.ok_or(ProtocolError::Invalid("missing WAL size"))?;
    let metadata_bytes = metadata_bytes.ok_or(ProtocolError::Invalid("missing metadata size"))?;
    let encoded_bytes = encoded_bytes.ok_or(ProtocolError::Invalid("missing encoded size"))?;
    let decoded_bytes = decoded_bytes.ok_or(ProtocolError::Invalid("missing decoded size"))?;
    let envelope_len = envelope_len.ok_or(ProtocolError::Invalid("missing envelope"))?;
    let metadata_len = metadata_len.ok_or(ProtocolError::Invalid("missing metadata"))?;

    if wal_bytes == 0
        || wal_bytes > MAX_TRANSACTION_GROUP_BYTES
        || metadata_bytes == 0
        || metadata_bytes > MAX_EVENT_METADATA_BYTES
        || encoded_bytes == 0
        || encoded_bytes > MAX_ENCODED_APPLICATION_BYTES
        || decoded_bytes == 0
        || decoded_bytes > MAX_DECODED_APPLICATION_BYTES
    {
        return Err(ProtocolError::LimitExceeded("preflight batch bytes"));
    }
    let actual_encoded = envelope_len
        .checked_add(metadata_len)
        .ok_or(ProtocolError::LimitExceeded("preflight encoded bytes"))?;
    let actual_decoded = wal_bytes
        .checked_add(metadata_len)
        .ok_or(ProtocolError::LimitExceeded("preflight decoded bytes"))?;
    if metadata_len != metadata_bytes
        || actual_encoded != encoded_bytes
        || actual_decoded != decoded_bytes
    {
        return Err(ProtocolError::Invalid(
            "preflight batch length declarations",
        ));
    }
    encoded_bytes
        .checked_add(decoded_bytes)
        .ok_or(ProtocolError::LimitExceeded(
            "preflight receive reservation",
        ))
}

#[derive(Default)]
struct PreflightBudget {
    tokens: usize,
    frontiers: usize,
    codecs: usize,
}

impl PreflightBudget {
    fn count_token(&mut self) -> Result<(), ProtocolError> {
        self.tokens = self
            .tokens
            .checked_add(1)
            .ok_or(ProtocolError::LimitExceeded(
                "protobuf preflight token count",
            ))?;
        if self.tokens > MAX_PREFLIGHT_TOKENS {
            return Err(ProtocolError::LimitExceeded(
                "protobuf preflight token count",
            ));
        }
        Ok(())
    }

    fn count_frontier(&mut self) -> Result<(), ProtocolError> {
        self.frontiers = self
            .frontiers
            .checked_add(1)
            .ok_or(ProtocolError::LimitExceeded("frontier count"))?;
        if self.frontiers > crate::MAX_FRONTIERS_PER_HANDSHAKE {
            return Err(ProtocolError::LimitExceeded("frontier count"));
        }
        Ok(())
    }

    fn count_codec(&mut self) -> Result<(), ProtocolError> {
        self.codecs = self
            .codecs
            .checked_add(1)
            .ok_or(ProtocolError::Invalid("too many peer application codecs"))?;
        if self.codecs > 2 {
            return Err(ProtocolError::Invalid("too many peer application codecs"));
        }
        Ok(())
    }
}

#[derive(Clone, Copy)]
enum PreflightMessage {
    Hello,
    PeerIdentity,
    VersionRange,
    ProtocolVersion,
    TransferLimits,
    PeerFrontier,
    OriginIdentity,
    CommittedEventId,
    CreditUpdate,
    KeepAlive,
}

fn next_preflight_field<'a>(
    bytes: &'a [u8],
    cursor: &mut usize,
    budget: &mut PreflightBudget,
) -> Result<(u32, u8, FieldValue<'a>), ProtocolError> {
    let field = next_field(bytes, cursor)?;
    budget.count_token()?;
    if field.1 == 3 || field.1 == 4 {
        return Err(ProtocolError::Invalid("protobuf groups are unsupported"));
    }
    Ok(field)
}

fn preflight_message(
    message: PreflightMessage,
    bytes: &[u8],
    budget: &mut PreflightBudget,
    depth: usize,
) -> Result<(), ProtocolError> {
    if depth > MAX_PREFLIGHT_DEPTH {
        return Err(ProtocolError::LimitExceeded("protobuf preflight depth"));
    }
    let mut cursor = 0;
    while cursor < bytes.len() {
        let (field, _wire_type, value) = next_preflight_field(bytes, &mut cursor, budget)?;
        if matches!(message, PreflightMessage::Hello) && field == 8 {
            preflight_codec_values(value, budget)?;
        }
        if matches!(message, PreflightMessage::Hello)
            && field == 10
            && matches!(value, FieldValue::Bytes(_))
        {
            budget.count_frontier()?;
        }
        if let (Some(nested_message), FieldValue::Bytes(nested_bytes)) =
            (nested_message(message, field), value)
        {
            preflight_message(nested_message, nested_bytes, budget, depth + 1)?;
        }
    }
    Ok(())
}

fn preflight_codec_values(
    value: FieldValue<'_>,
    budget: &mut PreflightBudget,
) -> Result<(), ProtocolError> {
    match value {
        FieldValue::Varint(_) => budget.count_codec(),
        FieldValue::Bytes(packed) => {
            let mut cursor = 0;
            while cursor < packed.len() {
                read_varint(packed, &mut cursor)?;
                budget.count_codec()?;
            }
            Ok(())
        }
        FieldValue::Fixed => Ok(()),
    }
}

fn nested_message(message: PreflightMessage, field: u32) -> Option<PreflightMessage> {
    match (message, field) {
        (PreflightMessage::Hello, 1) => Some(PreflightMessage::PeerIdentity),
        (PreflightMessage::Hello, 3..=5) => Some(PreflightMessage::VersionRange),
        (PreflightMessage::Hello, 9) => Some(PreflightMessage::TransferLimits),
        (PreflightMessage::Hello, 10) => Some(PreflightMessage::PeerFrontier),
        (PreflightMessage::VersionRange, 1 | 2) => Some(PreflightMessage::ProtocolVersion),
        (PreflightMessage::PeerFrontier, 1) | (PreflightMessage::CommittedEventId, 3) => {
            Some(PreflightMessage::OriginIdentity)
        }
        _ => None,
    }
}

#[derive(Clone, Copy)]
enum FieldValue<'a> {
    Varint(u64),
    Bytes(&'a [u8]),
    Fixed,
}

fn next_field<'a>(
    bytes: &'a [u8],
    cursor: &mut usize,
) -> Result<(u32, u8, FieldValue<'a>), ProtocolError> {
    let key = read_varint(bytes, cursor)?;
    let field = u32::try_from(key >> 3)
        .map_err(|_| ProtocolError::Invalid("protobuf field number overflow"))?;
    if field == 0 {
        return Err(ProtocolError::Invalid("protobuf field number zero"));
    }
    let wire_type = (key & 0x07) as u8;
    let value = match wire_type {
        0 => FieldValue::Varint(read_varint(bytes, cursor)?),
        1 => {
            advance(bytes, cursor, 8)?;
            FieldValue::Fixed
        }
        2 => {
            let length = usize::try_from(read_varint(bytes, cursor)?)
                .map_err(|_| ProtocolError::Invalid("protobuf length overflow"))?;
            let end = cursor
                .checked_add(length)
                .filter(|end| *end <= bytes.len())
                .ok_or(ProtocolError::Invalid("truncated protobuf field"))?;
            let payload = &bytes[*cursor..end];
            *cursor = end;
            FieldValue::Bytes(payload)
        }
        5 => {
            advance(bytes, cursor, 4)?;
            FieldValue::Fixed
        }
        3 | 4 => FieldValue::Fixed,
        _ => return Err(ProtocolError::Invalid("invalid protobuf wire type")),
    };
    Ok((field, wire_type, value))
}

fn read_varint(bytes: &[u8], cursor: &mut usize) -> Result<u64, ProtocolError> {
    let mut value = 0u64;
    for index in 0..10 {
        let byte = *bytes
            .get(*cursor)
            .ok_or(ProtocolError::Invalid("truncated protobuf varint"))?;
        *cursor += 1;
        if index == 9 && byte > 1 {
            return Err(ProtocolError::Invalid("protobuf varint overflow"));
        }
        value |= u64::from(byte & 0x7f) << (index * 7);
        if byte & 0x80 == 0 {
            return Ok(value);
        }
    }
    Err(ProtocolError::Invalid("protobuf varint overflow"))
}

fn advance(bytes: &[u8], cursor: &mut usize, length: usize) -> Result<(), ProtocolError> {
    *cursor = cursor
        .checked_add(length)
        .filter(|end| *end <= bytes.len())
        .ok_or(ProtocolError::Invalid("truncated protobuf field"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    use tb_core::{BudgetConfig, MemoryCategory, TenantId, WorkerId};

    fn attribution() -> Attribution {
        Attribution {
            tenant: TenantId::from_bytes([1; 16]),
            worker: WorkerId(1),
            category: MemoryCategory::Replication,
        }
    }

    fn process_budget() -> MemoryBudget {
        MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid process budget"))
    }

    fn limits() -> crate::AdmissionLimits {
        crate::AdmissionLimits::new(1024, 1, 1).expect("valid peer limits")
    }

    fn put_varint(mut value: u64, output: &mut Vec<u8>) {
        while value >= 0x80 {
            let low_byte = u8::try_from(value & 0x7f).expect("masked varint byte fits u8");
            output.push(low_byte | 0x80);
            value >>= 7;
        }
        output.push(u8::try_from(value).expect("final varint byte fits u8"));
    }

    fn put_integer_field(field: u32, value: u64, output: &mut Vec<u8>) {
        put_varint(u64::from(field) << 3, output);
        put_varint(value, output);
    }

    fn put_bytes_field(field: u32, value: &[u8], output: &mut Vec<u8>) {
        put_varint((u64::from(field) << 3) | 2, output);
        put_varint(
            u64::try_from(value.len()).expect("fixture length fits u64"),
            output,
        );
        output.extend_from_slice(value);
    }

    fn batch_request(decoded_size: usize) -> Vec<u8> {
        let mut batch = Vec::new();
        put_integer_field(12, 100, &mut batch);
        put_integer_field(13, 10, &mut batch);
        put_integer_field(15, 100, &mut batch);
        put_integer_field(
            16,
            u64::try_from(decoded_size).expect("fixture size fits u64"),
            &mut batch,
        );
        put_bytes_field(17, &[0; 90], &mut batch);
        put_bytes_field(18, &[0; 10], &mut batch);

        let mut request = Vec::new();
        put_bytes_field(2, &batch, &mut request);
        request
    }

    fn hello_request(hello: &[u8]) -> Vec<u8> {
        let mut request = Vec::new();
        put_bytes_field(1, hello, &mut request);
        request
    }

    #[test]
    fn first_frame_and_authorization_gate_run_before_process_reservation() {
        let budget = process_budget();
        let context = SyncAdmissionContext::new(budget.clone(), attribution())
            .with_receive_memory_gate(Arc::new(|charge| charge == MAX_PREDECODE_RESERVATION_BYTES));

        assert!(matches!(
            context.reserve_frame(PreflightKind::CreditUpdate, None),
            Err(ProtocolError::Invalid(_))
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 0);

        let hello = context
            .reserve_frame(PreflightKind::Hello, None)
            .expect("first Hello is admitted");
        assert_eq!(
            budget.snapshot().reserved_bytes,
            MAX_PREDECODE_RESERVATION_BYTES
        );
        drop(hello);
        context.hello_decoded().expect("first Hello is decoded");

        assert!(matches!(
            context.reserve_frame(PreflightKind::KeepAlive, None),
            Err(ProtocolError::Invalid(_))
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        context
            .authorize_peer(AdmissionController::new(limits()))
            .expect("validated Hello installs the peer budget");

        let control = context
            .reserve_frame(PreflightKind::KeepAlive, None)
            .expect("authorized stream admits control");
        assert_eq!(
            budget.snapshot().reserved_bytes,
            MAX_PREDECODE_RESERVATION_BYTES
        );
        drop(control);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
    }

    #[test]
    fn rejected_memory_gate_does_not_reserve_process_or_peer_capacity() {
        let budget = process_budget();
        let peer = AdmissionController::new(limits());
        let context = SyncAdmissionContext::new(budget.clone(), attribution())
            .with_receive_memory_gate(Arc::new(|_| false));
        context
            .hello_decoded()
            .expect("fixture reaches authorization state");
        context
            .authorize_peer(peer.clone())
            .expect("peer budget installed");

        assert!(matches!(
            context.reserve_frame(PreflightKind::CommittedGroup, Some(210)),
            Err(ProtocolError::ProcessHeadroomRejected)
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(peer.reserved_bytes(), 0);
        assert_eq!(peer.in_flight_batches(), 0);
    }

    #[test]
    fn batch_preflight_charges_declared_application_bytes_before_prost() {
        let request = batch_request(110);
        let preflight = preflight_request(&request).expect("well-formed bounded batch");
        assert!(matches!(preflight.kind, PreflightKind::CommittedGroup));
        assert_eq!(preflight.peer_bytes, Some(210));

        let budget = process_budget();
        let peer = AdmissionController::new(limits());
        let context = SyncAdmissionContext::new(budget.clone(), attribution());
        context
            .hello_decoded()
            .expect("fixture reaches authorization state");
        context
            .authorize_peer(peer.clone())
            .expect("peer budget installed");
        let permit = context
            .reserve_frame(preflight.kind, preflight.peer_bytes)
            .expect("predecode and peer budgets reserved before Prost");
        assert!(matches!(permit, FrameAdmission::CommittedGroup { .. }));
        assert_eq!(
            budget.snapshot().reserved_bytes,
            MAX_PREDECODE_RESERVATION_BYTES
        );
        assert_eq!(budget.snapshot().reserved_items, 1);
        assert_eq!(peer.reserved_bytes(), 210);
        assert_eq!(peer.in_flight_batches(), 1);
        drop(permit);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
        assert_eq!(peer.reserved_bytes(), 0);
        assert_eq!(peer.in_flight_batches(), 0);
    }

    #[test]
    fn batch_preflight_rejects_duplicate_and_inconsistent_lengths() {
        let mut duplicate_outer_variant = batch_request(110);
        put_bytes_field(3, &[], &mut duplicate_outer_variant);
        assert!(matches!(
            preflight_request(&duplicate_outer_variant),
            Err(ProtocolError::Invalid("multiple Sync oneof variants"))
        ));

        assert!(matches!(
            preflight_request(&batch_request(111)),
            Err(ProtocolError::Invalid(
                "preflight batch length declarations"
            ))
        ));
    }

    #[test]
    fn hello_preflight_bounds_frontier_vector_before_process_or_peer_admission() {
        let mut hello = Vec::new();
        for _ in 0..=crate::MAX_FRONTIERS_PER_HANDSHAKE {
            put_bytes_field(10, &[], &mut hello);
        }
        let request = hello_request(&hello);
        let budget = process_budget();
        let peer = AdmissionController::new(limits());
        let context = SyncAdmissionContext::new(budget.clone(), attribution());

        let result = preflight_request(&request).and_then(|preflight| {
            context
                .reserve_frame(preflight.kind, preflight.peer_bytes)
                .map(|_| ())
        });
        assert!(matches!(
            result,
            Err(ProtocolError::LimitExceeded("frontier count"))
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
        assert_eq!(peer.reserved_bytes(), 0);
        assert_eq!(peer.in_flight_batches(), 0);

        let decoded = v1::SyncRequest::decode(request.as_slice())
            .expect("the adversarial Hello remains Prost-decodable");
        let Some(v1::sync_request::Frame::Hello(decoded_hello)) = decoded.frame else {
            panic!("fixture must decode as Hello");
        };
        assert_eq!(
            decoded_hello.frontiers.len(),
            crate::MAX_FRONTIERS_PER_HANDSHAKE + 1
        );
    }

    #[test]
    fn hello_preflight_caps_packed_and_unpacked_codec_repetitions() {
        let mut unpacked = Vec::new();
        for codec in [0, 1, 0] {
            put_integer_field(8, codec, &mut unpacked);
        }
        assert!(matches!(
            preflight_request(&hello_request(&unpacked)),
            Err(ProtocolError::Invalid("too many peer application codecs"))
        ));

        let mut packed = Vec::new();
        put_bytes_field(8, &[0, 1, 0], &mut packed);
        assert!(matches!(
            preflight_request(&hello_request(&packed)),
            Err(ProtocolError::Invalid("too many peer application codecs"))
        ));
    }

    #[test]
    fn control_preflight_caps_field_tokens_before_admission() {
        let mut control = Vec::new();
        for _ in 0..MAX_PREFLIGHT_TOKENS {
            put_integer_field(1, 1, &mut control);
        }
        let mut request = Vec::new();
        put_bytes_field(3, &control, &mut request);

        assert!(matches!(
            preflight_request(&request),
            Err(ProtocolError::LimitExceeded(
                "protobuf preflight token count"
            ))
        ));
    }
}
